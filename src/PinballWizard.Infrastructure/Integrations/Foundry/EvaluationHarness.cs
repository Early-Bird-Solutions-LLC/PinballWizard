using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Ai.Evaluation.Evaluators;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

// Phase 3 evaluation-harness implementation per ADR-0016. Reads
// EvalHarnessOptions.GroundTruthPath (a JSONL file), drives each
// question through IAiRouter (which exercises the deployed Foundry
// agents — production code path per DL-0002 / DL-0003), scores the
// response with the four custom code-based evaluators registered in
// the Application layer, aggregates, and writes a timestamped JSON
// file to EvalHarnessOptions.ResultsDirectory.
//
// API surface choice (this class's Phase 3 deviation from the literal
// ADR-0016 pseudo-code):
//
//   ADR-0016 sketches a `evaluationClient.CreateEvaluationAsync(...)
//   → CreateEvaluationRunAsync(...) → poll → GetEvaluationRunOutputItemsAsync`
//   sequence. Azure.AI.Projects 2.0.1 does NOT yet expose a clean
//   public programmatic surface for that flow — the shipped Evaluation
//   namespace is gated behind the AAIP001 experimental diagnostic and
//   the operations-client accessors (GetProjectEvaluatorsClient,
//   GetEvaluationRulesClient) are non-public in this SDK version. The
//   "create eval / get results" verbs from earlier Foundry SDK previews
//   aren't in the GA public surface yet.
//
//   We adapt without weakening the spec: the four custom evaluators
//   are defined as canonical .NET classes in
//   PinballWizard.Application.Ai.Evaluation.Evaluators; the Phase 3
//   harness drives them in-process against each WizardAnswer; the
//   results JSON committed under data/eval/results/ IS the eval
//   artifact (same shape ADR-0016 calls for, just produced by a
//   local executor instead of Foundry's evaluator runtime). Python
//   reference snippets for the four evaluators live alongside this
//   harness in EvaluatorPythonSpecs.cs so the registration round-trip
//   is a one-line swap when the public SDK exposes it (Phase 6 turns
//   on continuous-eval / scheduled-eval per ADR-0016 § Phase 6
//   forward-compat — same SDK surface unlock catches both).
//
//   "Code-based" evaluator semantics are preserved: the evaluator
//   definition is the canonical contract; for Phase 3 the C# class
//   IS the runtime, the Python snippet is the spec for the future
//   Foundry-side registration. The committed JSON shape is stable.
public sealed class EvaluationHarness : IEvaluationHarness
{
    private static readonly JsonSerializerOptions ResultsSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    // Edition-aware expected_outcome discriminators (AB#259,
    // edition-scope-model-design §6). Kept in sync with EvalQuestion's
    // ExpectedOutcome default ("grounded").
    private const string OutcomeAnsweredAllEditions = "answered_all_editions";
    private const string OutcomeHonestSubstitution = "honest_substitution";

    private readonly IAiRouter _router;
    private readonly IAgentPromptProvider _promptProvider;
    private readonly CitationPrecisionEvaluator _precisionEvaluator;
    private readonly CitationRecallEvaluator _recallEvaluator;
    private readonly CitationCoverageEvaluator _coverageEvaluator;
    private readonly SubagentAccuracyEvaluator _subagentEvaluator;
    private readonly RefusalCorrectnessEvaluator _refusalEvaluator;
    private readonly AnsweredAllEditionsEvaluator _answeredAllEditionsEvaluator;
    private readonly HonestSubstitutionEvaluator _honestSubstitutionEvaluator;
    private readonly GroundingIntegrityEvaluator _groundingIntegrityEvaluator;
    private readonly MachineIdCoverageEvaluator _machineIdCoverageEvaluator;
    private readonly EvalHarnessOptions _evalOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EvaluationHarness> _logger;

    public EvaluationHarness(
        IAiRouter router,
        IAgentPromptProvider promptProvider,
        CitationPrecisionEvaluator precisionEvaluator,
        CitationRecallEvaluator recallEvaluator,
        CitationCoverageEvaluator coverageEvaluator,
        SubagentAccuracyEvaluator subagentEvaluator,
        RefusalCorrectnessEvaluator refusalEvaluator,
        AnsweredAllEditionsEvaluator answeredAllEditionsEvaluator,
        HonestSubstitutionEvaluator honestSubstitutionEvaluator,
        GroundingIntegrityEvaluator groundingIntegrityEvaluator,
        MachineIdCoverageEvaluator machineIdCoverageEvaluator,
        IOptions<EvalHarnessOptions> evalOptions,
        ILogger<EvaluationHarness> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(promptProvider);
        ArgumentNullException.ThrowIfNull(precisionEvaluator);
        ArgumentNullException.ThrowIfNull(recallEvaluator);
        ArgumentNullException.ThrowIfNull(coverageEvaluator);
        ArgumentNullException.ThrowIfNull(subagentEvaluator);
        ArgumentNullException.ThrowIfNull(refusalEvaluator);
        ArgumentNullException.ThrowIfNull(answeredAllEditionsEvaluator);
        ArgumentNullException.ThrowIfNull(honestSubstitutionEvaluator);
        ArgumentNullException.ThrowIfNull(groundingIntegrityEvaluator);
        ArgumentNullException.ThrowIfNull(machineIdCoverageEvaluator);
        ArgumentNullException.ThrowIfNull(evalOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _router = router;
        _promptProvider = promptProvider;
        _precisionEvaluator = precisionEvaluator;
        _recallEvaluator = recallEvaluator;
        _coverageEvaluator = coverageEvaluator;
        _subagentEvaluator = subagentEvaluator;
        _refusalEvaluator = refusalEvaluator;
        _answeredAllEditionsEvaluator = answeredAllEditionsEvaluator;
        _honestSubstitutionEvaluator = honestSubstitutionEvaluator;
        _groundingIntegrityEvaluator = groundingIntegrityEvaluator;
        _machineIdCoverageEvaluator = machineIdCoverageEvaluator;
        _evalOptions = evalOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EvalRunResult> RunAsync(CancellationToken cancellationToken)
    {
        using var activity = PinballWizardTelemetry.ActivitySource.StartActivity(
            PinballWizardTelemetry.EvalRunActivity, ActivityKind.Internal);

        // Tie the harness's internal time-budget to the caller's token
        // via a linked CTS. The caller's cancellation always wins.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCts.CancelAfter(TimeSpan.FromSeconds(_evalOptions.RunTimeoutSeconds));

        var startedAt = _timeProvider.GetUtcNow();
        var groundTruthPath = _evalOptions.GroundTruthPath;
        var questions = EvalQuestionParser.ParseFile(groundTruthPath);
        _logger.LogInformation(
            "EvaluationHarness loaded {Count} questions from {Path}.",
            questions.Count, groundTruthPath);

        if (_evalOptions.RegisterEvaluatorsOnRun)
        {
            // Phase 3: the public Azure.AI.Projects 2.0.1 surface for
            // ProjectEvaluators is gated behind AAIP001 (experimental)
            // and its accessor on AIProjectClient is non-public, so we
            // can't yet upsert the four definitions programmatically.
            // The Python equivalents live in EvaluatorPythonSpecs.* as
            // the canonical spec; a future SDK version flips this to a
            // real round-trip. The counter still increments per planned
            // registration so dashboards reading pinwiz.eval.evaluator.*
            // see a stable signal across the SDK transition.
            foreach (var evaluatorName in EvaluatorPythonSpecs.AllNames(_evalOptions.EvaluatorNamespace))
            {
                PinballWizardTelemetry.EvalEvaluatorRegistrations.Add(1);
                _logger.LogDebug(
                    "EvaluationHarness: planned-registration noop for evaluator '{Name}' (Azure.AI.Projects 2.0.1 evaluator surface is non-public; spec preserved in EvaluatorPythonSpecs).",
                    evaluatorName);
            }
        }

        var perQuestionResults = new List<EvalQuestionResult>(questions.Count);
        var perQuestionTimeout = TimeSpan.FromSeconds(_evalOptions.PerQuestionTimeoutSeconds);

        try
        {
            foreach (var question in questions)
            {
                runCts.Token.ThrowIfCancellationRequested();
                var result = await EvaluateOneAsync(question, perQuestionTimeout, runCts.Token)
                    .ConfigureAwait(false);
                perQuestionResults.Add(result);
                PinballWizardTelemetry.EvalQuestionsScored.Add(1);
                PinballWizardTelemetry.EvalQuestionDurationMs.Record(result.DurationMs);
            }
        }
        catch (Exception ex)
        {
            // Per-question failures are caught and recorded inside
            // EvaluateOneAsync; anything reaching here is a fatal
            // run-level failure (caller cancellation, run-timeout
            // expiry, harness bug). Increment the failed counter
            // and propagate so the caller's exit code reflects the
            // failure. Broad catch is intentional — this is a
            // log-then-rethrow; no exception is swallowed.
            //
            // #362: write the PARTIAL results first. The 2026-06-11
            // credential-timeout runs aborted exactly here and lost the
            // scorecard for every healthy question already evaluated —
            // hiding the signal the run existed to produce. Best-effort,
            // CancellationToken.None on purpose: the run token is what
            // just expired, and a local file write must not be governed
            // by it.
            PinballWizardTelemetry.EvalRunsFailed.Add(1);
            if (perQuestionResults.Count > 0)
            {
                await TryWritePartialResultsAsync(startedAt, groundTruthPath, perQuestionResults, ex)
                    .ConfigureAwait(false);
            }
            throw;
        }

        var completedAt = _timeProvider.GetUtcNow();
        var aggregate = ComputeAggregate(perQuestionResults);
        var bySlice = ComputeBySlice(perQuestionResults);
        var resultsPath = BuildResultsPath(startedAt);
        var evaluationId = $"pinwiz-eval-{startedAt:yyyyMMddTHHmmssZ}";

        var runResult = new EvalRunResult(
            EvaluationId: evaluationId,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            GroundTruthPath: groundTruthPath,
            ResultsPath: resultsPath,
            PromptVersion: _promptProvider.PromptVersion,
            Aggregate: aggregate,
            BySlice: bySlice,
            Questions: perQuestionResults);

        Directory.CreateDirectory(_evalOptions.ResultsDirectory);
        var json = JsonSerializer.Serialize(runResult, ResultsSerializerOptions);
        // CancellationToken.None: by this point every question is scored —
        // a run-timeout expiring during the local file write must not
        // discard the completed scorecard (#362).
        await File.WriteAllTextAsync(resultsPath, json, CancellationToken.None).ConfigureAwait(false);

        PinballWizardTelemetry.EvalRuns.Add(1);
        _logger.LogInformation(
            "EvaluationHarness completed {Count} questions ({Errors} errors) in {Elapsed:N1}s; results written to {Path}. " +
            "citation_precision={Precision:F3} citation_recall={Recall:F3} citation_coverage={Coverage:F3} subagent_accuracy={Subagent:F3} refusal_correctness={Refusal:F3} grounding_integrity={GroundingIntegrity:F3} machine_id_coverage={MachineIdCoverage:F3}",
            aggregate.QuestionCount,
            aggregate.ErrorCount,
            (completedAt - startedAt).TotalSeconds,
            resultsPath,
            aggregate.CitationPrecisionMean,
            aggregate.CitationRecallMean,
            aggregate.CitationCoverageMean,
            aggregate.SubagentAccuracyMean,
            aggregate.RefusalCorrectnessMean,
            aggregate.GroundingIntegrityMean,
            aggregate.MachineIdCoverageMean);

        return runResult;
    }

    // Best-effort partial-results write on run-level abort (#362). The file
    // is the real artifact marked clearly as partial: '.partial' suffix and
    // the abort reason embedded — never presented as a completed run.
    private async Task TryWritePartialResultsAsync(
        DateTimeOffset startedAt,
        string groundTruthPath,
        List<EvalQuestionResult> perQuestionResults,
        Exception abortReason)
    {
        try
        {
            var resultsPath = BuildResultsPath(startedAt) + ".partial";
            var partialResults = perQuestionResults;
            var runResult = new EvalRunResult(
                EvaluationId: $"pinwiz-eval-{startedAt:yyyyMMddTHHmmssZ}-PARTIAL",
                StartedAt: startedAt,
                CompletedAt: _timeProvider.GetUtcNow(),
                GroundTruthPath: groundTruthPath,
                ResultsPath: resultsPath,
                PromptVersion: _promptProvider.PromptVersion,
                Aggregate: ComputeAggregate(partialResults),
                BySlice: ComputeBySlice(partialResults),
                Questions: partialResults);

            Directory.CreateDirectory(_evalOptions.ResultsDirectory);
            var json = JsonSerializer.Serialize(runResult, ResultsSerializerOptions);
            await File.WriteAllTextAsync(resultsPath, json, CancellationToken.None).ConfigureAwait(false);

            _logger.LogWarning(
                "EvaluationHarness run ABORTED ({Reason}) after {Count} question(s) — partial results written to {Path}.",
                abortReason.GetType().Name,
                perQuestionResults.Count,
                resultsPath);
        }
        catch (Exception writeEx)
        {
            // The abort itself is about to propagate; a failed salvage
            // write must not mask it. Log and let the original throw.
            _logger.LogError(writeEx,
                "EvaluationHarness could not write partial results after run abort.");
        }
    }

    private async Task<EvalQuestionResult> EvaluateOneAsync(
        EvalQuestion question,
        TimeSpan perQuestionTimeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        WizardAnswer? answer = null;
        string? error = null;
        try
        {
            using var perQuestionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perQuestionCts.CancelAfter(perQuestionTimeout);
            answer = await _router.AnswerAsync(question.Question, perQuestionCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-driven cancellation: propagate to abort the whole run.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Per-question timeout: log + record + continue with the
            // next question. The aggregate counts this as 0 across all
            // metrics (worst-case scoring of a hung question).
            error = $"Per-question timeout after {perQuestionTimeout.TotalSeconds:N0}s: {ex.Message}";
            _logger.LogWarning(ex, "EvaluationHarness: question {Id} timed out.", question.Id);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "EvaluationHarness: question {Id} threw.", question.Id);
        }

        stopwatch.Stop();
        var durationMs = stopwatch.Elapsed.TotalMilliseconds;

        var predictedCitations = answer is null
            ? []
            : ExtractCitationIds(answer.Citations);
        var predictedSubAgent = answer?.SubAgentUsed ?? string.Empty;
        var predictedRefusal = answer?.IsRefusal ?? false;
        var answerText = answer?.Text ?? string.Empty;

        // Metric-hygiene (AB#259, 2026-06-10): an acceptable_refusal-only
        // gap row that refuses carries NO citation signal — its
        // expected_citation_set describes the answer the agent MAY give,
        // not one it MUST give. Grading the absent answer's citations as
        // zeros misrepresented the metric (the strike-one artifact).
        // refusal_required rows keep the empty-set conventions (precision
        // guards against fabricated citations inside a refusal).
        var acceptablyRefusedGapRow = predictedRefusal
            && question.AcceptableRefusal
            && !question.RefusalRequired;

        // Edition-aware citation scoring (AB#259): when the row supplies
        // acceptable_citation_sets, score against the most-favorable set
        // (any-of). Otherwise fall back to the single expected_citation_set
        // (back-compat with pre-edition rows).
        double? citationPrecision;
        double? citationRecall;
        if (acceptablyRefusedGapRow)
        {
            citationPrecision = null;
            citationRecall = null;
        }
        else if (question.AcceptableCitationSets is { Count: > 0 } acceptableSets)
        {
            citationPrecision = _precisionEvaluator.ComputeAnyOf(predictedCitations, acceptableSets);
            citationRecall = _recallEvaluator.ComputeAnyOf(predictedCitations, acceptableSets);
        }
        else
        {
            citationPrecision = _precisionEvaluator.Compute(predictedCitations, question.ExpectedCitationSet);
            citationRecall = _recallEvaluator.Compute(predictedCitations, question.ExpectedCitationSet);
        }

        // R2 / R3 outcome evaluators only apply to rows that declare the
        // matching expected_outcome; null elsewhere (the metric is
        // undefined and excluded from that row's aggregate denominator).
        double? answeredAllEditions = null;
        double? honestSubstitution = null;
        if (string.Equals(question.ExpectedOutcome, OutcomeAnsweredAllEditions, StringComparison.OrdinalIgnoreCase))
        {
            answeredAllEditions = _answeredAllEditionsEvaluator.Compute(
                answerText, predictedCitations, question.RequiredEditions ?? []);
        }
        else if (string.Equals(question.ExpectedOutcome, OutcomeHonestSubstitution, StringComparison.OrdinalIgnoreCase))
        {
            // R3 needs the named edition. Prefer the first required edition
            // if the curator supplied one; otherwise the row is misconfigured
            // and scores 0 (the named-edition gap can't be checked).
            var namedEdition = question.RequiredEditions is { Count: > 0 } reqs
                ? reqs[0]
                : null;
            honestSubstitution = string.IsNullOrWhiteSpace(namedEdition)
                ? 0.0
                : _honestSubstitutionEvaluator.Compute(answerText, predictedCitations, namedEdition);
        }

        // Grounding-integrity: Rules/Repair answers must carry ≥1 CorpusChunk
        // citation — not only a MachineRecord identity entry. Null on refusals,
        // Valuation answers, and error rows (metric undefined there; excluded
        // from the aggregate denominator per the nullable-metric convention).
        double? groundingIntegrity = null;
        if (!predictedRefusal && answer is not null
            && (string.Equals(predictedSubAgent, "Rules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(predictedSubAgent, "Repair", StringComparison.OrdinalIgnoreCase)))
        {
            groundingIntegrity = _groundingIntegrityEvaluator.Compute(answer.Citations);
        }

        // MachineIdCoverage (issue #719): when the question names a machine
        // (MachineId set) and the answer is not a refusal, every searchCorpus
        // call in the tool-call trace must have carried a non-null machineId.
        // Null on refusals, questions without MachineId, and error rows
        // (excluded from the aggregate denominator per the nullable-metric
        // convention). Evaluator returns null when the trace is unavailable
        // (cache hit) or has no searchCorpus calls — both excluded.
        double? machineIdCoverage = null;
        if (!predictedRefusal && answer is not null && !string.IsNullOrEmpty(question.MachineId))
        {
            machineIdCoverage = _machineIdCoverageEvaluator.Compute(answer.ToolCallTrace);
        }

        var scores = new EvalScores(
            CitationPrecision: citationPrecision,
            CitationRecall: citationRecall,
            // Coverage measures citations-per-paragraph of an ANSWER; a
            // refusal is not an answer, so the metric is undefined on any
            // refused row (the refusal itself is graded by
            // refusal_correctness, and missing citations by recall).
            CitationCoverage: predictedRefusal
                ? null
                : _coverageEvaluator.Compute(answerText, predictedCitations),
            SubagentAccuracy: _subagentEvaluator.Compute(predictedSubAgent, question.ExpectedSubAgent, question.AcceptableSubAgents),
            RefusalCorrectness: _refusalEvaluator.Compute(predictedRefusal, question.AcceptableRefusal, question.RefusalRequired),
            AnsweredAllEditions: answeredAllEditions,
            HonestSubstitution: honestSubstitution,
            GroundingIntegrity: groundingIntegrity,
            MachineIdCoverage: machineIdCoverage);

        return new EvalQuestionResult(
            Id: question.Id,
            Question: question.Question,
            ExpectedSubAgent: question.ExpectedSubAgent,
            PredictedSubAgent: predictedSubAgent,
            ExpectedCitationSet: question.ExpectedCitationSet,
            PredictedCitationSet: predictedCitations,
            AcceptableRefusal: question.AcceptableRefusal,
            PredictedRefusal: predictedRefusal,
            AnswerText: answerText,
            Scores: scores,
            DurationMs: durationMs,
            Error: error,
            Slice: question.Slice);
    }

    private static List<string> ExtractCitationIds(IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return [];
        }

        var ids = new List<string>(citations.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var citation in citations)
        {
            // Phase 3 ground-truth ids are OPDB MachineId values (e.g.
            // GRBN-MQR4P) — sometimes wrapped with the "mch_" prefix in
            // the seed file for symmetry with Phase 4 doc_ ids. Accept
            // either form by storing the raw MachineId; the eval-set
            // curator is responsible for matching the expected form.
            // Phase 4 RAG fills in DocumentChunkId; both flow through.
            var id = citation.MachineId ?? citation.DocumentChunkId;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static EvalAggregate ComputeAggregate(List<EvalQuestionResult> results)
    {
        if (results.Count == 0)
        {
            return new EvalAggregate(
                QuestionCount: 0,
                ErrorCount: 0,
                CitationPrecisionMean: null,
                CitationRecallMean: null,
                CitationCoverageMean: null,
                SubagentAccuracyMean: 0.0,
                RefusalCorrectnessMean: null);
        }

        // Nullable metrics aggregate only over rows where the metric is
        // defined (non-null score) so the signal isn't diluted by rows
        // it doesn't apply to — R2/R3 outcome rows, refused rows
        // (coverage), and acceptable_refusal gap rows (refusal,
        // precision/recall on refusal). The *_count fields make each
        // mean's denominator visible in the results file.
        double precisionSum = 0;
        var precisionCount = 0;
        double recallSum = 0;
        var recallCount = 0;
        double coverageSum = 0;
        var coverageCount = 0;
        double subagentSum = 0;
        double refusalSum = 0;
        var refusalCount = 0;
        var errorCount = 0;

        double answeredAllEditionsSum = 0;
        var answeredAllEditionsCount = 0;
        double honestSubstitutionSum = 0;
        var honestSubstitutionCount = 0;
        double groundingIntegritySum = 0;
        var groundingIntegrityCount = 0;
        double machineIdCoverageSum = 0;
        var machineIdCoverageCount = 0;

        foreach (var r in results)
        {
            if (r.Scores.CitationPrecision is { } precision)
            {
                precisionSum += precision;
                precisionCount++;
            }
            if (r.Scores.CitationRecall is { } recall)
            {
                recallSum += recall;
                recallCount++;
            }
            if (r.Scores.CitationCoverage is { } coverage)
            {
                coverageSum += coverage;
                coverageCount++;
            }
            subagentSum += r.Scores.SubagentAccuracy;
            if (r.Scores.RefusalCorrectness is { } refusal)
            {
                refusalSum += refusal;
                refusalCount++;
            }
            if (r.Scores.AnsweredAllEditions is { } aae)
            {
                answeredAllEditionsSum += aae;
                answeredAllEditionsCount++;
            }
            if (r.Scores.HonestSubstitution is { } hs)
            {
                honestSubstitutionSum += hs;
                honestSubstitutionCount++;
            }
            if (r.Scores.GroundingIntegrity is { } gi)
            {
                groundingIntegritySum += gi;
                groundingIntegrityCount++;
            }
            if (r.Scores.MachineIdCoverage is { } mic)
            {
                machineIdCoverageSum += mic;
                machineIdCoverageCount++;
            }
            if (!string.IsNullOrEmpty(r.Error))
            {
                errorCount++;
            }
        }

        var n = results.Count;
        return new EvalAggregate(
            QuestionCount: n,
            ErrorCount: errorCount,
            CitationPrecisionMean: precisionCount > 0 ? precisionSum / precisionCount : null,
            CitationRecallMean: recallCount > 0 ? recallSum / recallCount : null,
            CitationCoverageMean: coverageCount > 0 ? coverageSum / coverageCount : null,
            SubagentAccuracyMean: subagentSum / n,
            RefusalCorrectnessMean: refusalCount > 0 ? refusalSum / refusalCount : null,
            CitationPrecisionCount: precisionCount,
            CitationRecallCount: recallCount,
            CitationCoverageCount: coverageCount,
            RefusalCorrectnessCount: refusalCount,
            AnsweredAllEditionsMean: answeredAllEditionsCount > 0
                ? answeredAllEditionsSum / answeredAllEditionsCount
                : null,
            AnsweredAllEditionsCount: answeredAllEditionsCount,
            HonestSubstitutionMean: honestSubstitutionCount > 0
                ? honestSubstitutionSum / honestSubstitutionCount
                : null,
            HonestSubstitutionCount: honestSubstitutionCount,
            GroundingIntegrityMean: groundingIntegrityCount > 0
                ? groundingIntegritySum / groundingIntegrityCount
                : null,
            GroundingIntegrityCount: groundingIntegrityCount,
            MachineIdCoverageMean: machineIdCoverageCount > 0
                ? machineIdCoverageSum / machineIdCoverageCount
                : null,
            MachineIdCoverageCount: machineIdCoverageCount);
    }

    // Groups per-question results by Slice and calls ComputeAggregate on each
    // group — the same arithmetic used for the top-level Aggregate, so slice
    // means are computed identically (DRY: no duplicated aggregation logic).
    // Rows with a null Slice go into the "(unsliced)" bucket so every question
    // is accounted for and the bucket set is exhaustive. The result is ordered
    // by key for stable JSON serialization across runs.
    private static Dictionary<string, EvalAggregate> ComputeBySlice(
        List<EvalQuestionResult> results)
    {
        var groups = new Dictionary<string, List<EvalQuestionResult>>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            var key = r.Slice ?? "(unsliced)";
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = [];
                groups[key] = bucket;
            }
            bucket.Add(r);
        }

        // Sort keys so the JSON output is stable across runs.
        return groups
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(
                kv => kv.Key,
                kv => ComputeAggregate(kv.Value),
                StringComparer.Ordinal);
    }

    private string BuildResultsPath(DateTimeOffset startedAt)
    {
        var stamp = startedAt.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "Z";
        var fileName = $"wizard.{stamp}.json";
        return Path.Combine(_evalOptions.ResultsDirectory, fileName);
    }
}
