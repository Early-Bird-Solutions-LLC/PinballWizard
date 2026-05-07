using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Application.Ai.Evaluation;

// Configuration for the Phase 3 evaluation harness per ADR-0016.
// Section: "Evaluation". Wired from appsettings + environment in the
// Infrastructure DI extension; consumed by EvaluationHarness.
public sealed class EvalHarnessOptions
{
    public const string SectionName = "Evaluation";

    // Path (relative to current working directory, typically the repo
    // root) to the JSONL ground-truth file. ADR-0016 § Ground-truth
    // shape pins the v1 path.
    public string GroundTruthPath { get; set; } = "data/eval/wizard.v1.jsonl";

    // Directory where timestamped results JSON files are written.
    // Each run produces wizard.{yyyyMMddTHHmmssZ}.json; ADR-0016 calls
    // for these to be committed so the metric trajectory is visible
    // in `git log`.
    public string ResultsDirectory { get; set; } = "data/eval/results";

    // Maximum total time budget for a single eval run, expressed in
    // seconds. Bounds wall-clock cost: 30 questions × ~10s/question
    // sub-agent dispatch plus margin = ~600s default. The harness
    // honors the caller's CancellationToken first; this value caps
    // the harness's own internal timeout.
    [Range(30, 3600)]
    public int RunTimeoutSeconds { get; set; } = 600;

    // Per-question dispatch timeout in seconds. Caps any single
    // IAiRouter.AnswerAsync call (a hung agent shouldn't bring down
    // the whole eval). Failures register as a per-question Error
    // string in the result JSON — the run continues.
    [Range(5, 600)]
    public int PerQuestionTimeoutSeconds { get; set; } = 120;

    // When true, on every harness invocation the four custom code-based
    // evaluators are upserted (idempotently) into the Foundry project
    // via ProjectEvaluators.CreateVersionAsync. ADR-0016 § Negative
    // consequences: "If the project is recreated (e.g., DR drill), the
    // evaluators must be re-registered. Mitigation: idempotent
    // registration on every harness run." Set false in tightly-looped
    // local dev to skip the network round-trip.
    public bool RegisterEvaluatorsOnRun { get; set; } = true;

    // Foundry-side display label prefixed onto every registered
    // evaluator (e.g. "pinwiz" → "pinwiz.citation_precision"). Keeps
    // our custom evaluators visually grouped in the Foundry portal
    // alongside the platform's built-ins.
    public string EvaluatorNamespace { get; set; } = "pinwiz";
}
