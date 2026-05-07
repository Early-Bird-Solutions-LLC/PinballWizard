namespace PinballWizard.Application.Ai;

// Canonical names of the four agents the IFoundryAgentFactory registers.
// Keeping them as constants (vs. a free-form string) makes refactoring
// safer and pins the names that prompt files (Wizard.md / Valuation.md /
// Rules.md / Repair.md) match against.
//
// Per ADR-0014 § Architecture, Wizard is the orchestrator (Microsoft
// Agent Framework's AIAgent composition primitives dispatch to the
// three sub-agents based on Wizard.md's routing instructions).
public static class AgentName
{
    public const string Wizard = "Wizard";
    public const string Valuation = "Valuation";
    public const string Rules = "Rules";
    public const string Repair = "Repair";

    public static readonly IReadOnlyList<string> All = [Wizard, Valuation, Rules, Repair];
}
