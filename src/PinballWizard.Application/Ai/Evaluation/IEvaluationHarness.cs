namespace PinballWizard.Application.Ai.Evaluation;

// Phase 3 evaluation harness contract per ADR-0016. Implementations
// drive each question in a JSONL ground-truth file through IAiRouter
// (which exercises the deployed Foundry agents — production code path
// per DL-0002 / DL-0003), score the response with the four custom
// code-based evaluators (citation precision/recall, subagent accuracy,
// refusal correctness), aggregate, and write a timestamped JSON file
// to the results directory.
//
// The interface lives in the Application layer alongside IAiRouter;
// the implementation lives in Infrastructure because it depends on
// Azure.AI.Projects (for Foundry evaluator-definition registration).
public interface IEvaluationHarness
{
    Task<EvalRunResult> RunAsync(CancellationToken cancellationToken);
}
