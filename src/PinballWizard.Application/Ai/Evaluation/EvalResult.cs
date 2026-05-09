using System.Text.Json.Serialization;

namespace PinballWizard.Application.Ai.Evaluation;

// Per-question scored result + the aggregate at the top of the JSON
// output written to data/eval/results/wizard.{timestamp}.json. The
// per-evaluator scores are normalized in [0.0, 1.0]; per-evaluator
// aggregates are arithmetic means across questions per ADR-0016.
//
// The shape is stable across runs so a future PR's results file can be
// `git diff`-compared against an earlier baseline — that is the deploy
// gate per guardrails.md § Run-time triggers (5% citation-precision
// regression blocks deploy).

public sealed record EvalQuestionResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("expected_sub_agent")] string ExpectedSubAgent,
    [property: JsonPropertyName("predicted_sub_agent")] string PredictedSubAgent,
    [property: JsonPropertyName("expected_citation_set")] IReadOnlyList<string> ExpectedCitationSet,
    [property: JsonPropertyName("predicted_citation_set")] IReadOnlyList<string> PredictedCitationSet,
    [property: JsonPropertyName("acceptable_refusal")] bool AcceptableRefusal,
    [property: JsonPropertyName("predicted_refusal")] bool PredictedRefusal,
    [property: JsonPropertyName("answer_text")] string AnswerText,
    [property: JsonPropertyName("scores")] EvalScores Scores,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record EvalScores(
    [property: JsonPropertyName("citation_precision")] double CitationPrecision,
    [property: JsonPropertyName("citation_recall")] double CitationRecall,
    [property: JsonPropertyName("citation_coverage")] double CitationCoverage,
    [property: JsonPropertyName("subagent_accuracy")] double SubagentAccuracy,
    [property: JsonPropertyName("refusal_correctness")] double RefusalCorrectness);

public sealed record EvalAggregate(
    [property: JsonPropertyName("question_count")] int QuestionCount,
    [property: JsonPropertyName("error_count")] int ErrorCount,
    [property: JsonPropertyName("citation_precision_mean")] double CitationPrecisionMean,
    [property: JsonPropertyName("citation_recall_mean")] double CitationRecallMean,
    [property: JsonPropertyName("citation_coverage_mean")] double CitationCoverageMean,
    [property: JsonPropertyName("subagent_accuracy_mean")] double SubagentAccuracyMean,
    [property: JsonPropertyName("refusal_correctness_mean")] double RefusalCorrectnessMean);

public sealed record EvalRunResult(
    [property: JsonPropertyName("evaluation_id")] string EvaluationId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("ground_truth_path")] string GroundTruthPath,
    [property: JsonPropertyName("results_path")] string ResultsPath,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("aggregate")] EvalAggregate Aggregate,
    [property: JsonPropertyName("questions")] IReadOnlyList<EvalQuestionResult> Questions);
