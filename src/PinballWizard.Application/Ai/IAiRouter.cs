namespace PinballWizard.Application.Ai;

// IAiRouter is the public Application-layer entry point for the Wizard
// answer flow per ADR-0014. The implementation is a thin pre/post wrapper
// around the Microsoft Agent Framework's connected-agents dispatch:
//   pre-call: cache lookup
//   call:     AIAgent.RunAsync against the Wizard agent (which dispatches
//             to Valuation / Rules / Repair sub-agents per its prompt)
//   post-call: confidence calculation, refusal categorization, cost
//              ceiling check, telemetry emit, cache write
//
// Phase 3 Wave 2 PR 4 ships the skeleton. PR 5 fills sub-agent prompts +
// the getMachineByTitle function tool. PR 6 layers in confidence-driven
// refusal.
public interface IAiRouter
{
    Task<WizardAnswer> AnswerAsync(string question, CancellationToken cancellationToken);
}
