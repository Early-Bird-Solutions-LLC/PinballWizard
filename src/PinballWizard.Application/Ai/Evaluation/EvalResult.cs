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

// Edition-aware additions (AB#259): AnsweredAllEditions (R2) and
// HonestSubstitution (R3) are null on rows whose expected_outcome is not
// the matching outcome — the metric is undefined there, so it is omitted
// from that row's JSON and excluded from the aggregate denominator rather
// than scored 1.0 (which would dilute the R2/R3 signal across the mostly-
// grounded eval set). Aggregate means for these two are computed only over
// applicable rows; null when no row exercises that outcome.
public sealed record EvalScores(
    [property: JsonPropertyName("citation_precision")] double CitationPrecision,
    [property: JsonPropertyName("citation_recall")] double CitationRecall,
    [property: JsonPropertyName("citation_coverage")] double CitationCoverage,
    [property: JsonPropertyName("subagent_accuracy")] double SubagentAccuracy,
    [property: JsonPropertyName("refusal_correctness")] double RefusalCorrectness,
    [property: JsonPropertyName("answered_all_editions")] double? AnsweredAllEditions = null,
    [property: JsonPropertyName("honest_substitution")] double? HonestSubstitution = null);

public sealed record EvalAggregate(
    [property: JsonPropertyName("question_count")] int QuestionCount,
    [property: JsonPropertyName("error_count")] int ErrorCount,
    [property: JsonPropertyName("citation_precision_mean")] double CitationPrecisionMean,
    [property: JsonPropertyName("citation_recall_mean")] double CitationRecallMean,
    [property: JsonPropertyName("citation_coverage_mean")] double CitationCoverageMean,
    [property: JsonPropertyName("subagent_accuracy_mean")] double SubagentAccuracyMean,
    [property: JsonPropertyName("refusal_correctness_mean")] double RefusalCorrectnessMean,
    [property: JsonPropertyName("answered_all_editions_mean")] double? AnsweredAllEditionsMean = null,
    [property: JsonPropertyName("answered_all_editions_count")] int AnsweredAllEditionsCount = 0,
    [property: JsonPropertyName("honest_substitution_mean")] double? HonestSubstitutionMean = null,
    [property: JsonPropertyName("honest_substitution_count")] int HonestSubstitutionCount = 0);

public sealed record EvalRunResult(
    [property: JsonPropertyName("evaluation_id")] string EvaluationId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("ground_truth_path")] string GroundTruthPath,
    [property: JsonPropertyName("results_path")] string ResultsPath,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("aggregate")] EvalAggregate Aggregate,
    [property: JsonPropertyName("questions")] IReadOnlyList<EvalQuestionResult> Questions);
