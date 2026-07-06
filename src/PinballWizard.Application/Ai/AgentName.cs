namespace PinballWizard.Application.Ai;

// Canonical names of the agents IFoundryAgentFactory registers. Keeping
// them as constants (vs. a free-form string) makes refactoring safer and
// pins the names that prompt files (Wizard.md / Valuation.md / Rules.md /
// Repair.md / GridSearch.md) match against.
//
// Per ADR-0014 § Architecture, Wizard is the orchestrator (Microsoft
// Agent Framework's AIAgent composition primitives dispatch to the
// three customer-facing sub-agents based on Wizard.md's routing
// instructions). GridSearch is a separate, admin-only agent (natural-
// language-to-grid-filter translator for the admin data grids) — it is
// registered the same way but is not part of that customer-facing
// routing surface.
public static class AgentName
{
    public const string Wizard = "Wizard";
    public const string Valuation = "Valuation";
    public const string Rules = "Rules";
    public const string Repair = "Repair";
    public const string GridSearch = "GridSearch";

    public static readonly IReadOnlyList<string> All = [Wizard, Valuation, Rules, Repair, GridSearch];

    // The customer-facing Wizard sub-agents Wizard.md can route a public
    // question to — deliberately excludes GridSearch, which is an internal
    // admin tool with no public-facing seed question or routing entry.
    // Single source of truth for SeedQuestionLoader's TargetSubAgent
    // validation and SeedQuestionsContractTests' production-manifest pin —
    // both must agree on exactly this set, or a real mis-scoped seed
    // question (or a real new customer-facing sub-agent) could silently
    // pass one check and fail the other.
    public static readonly IReadOnlyList<string> PublicWizardSubAgents = [Wizard, Valuation, Rules, Repair];
}
