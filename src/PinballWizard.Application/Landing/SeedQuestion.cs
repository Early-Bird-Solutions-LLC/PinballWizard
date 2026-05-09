namespace PinballWizard.Application.Landing;

// Per ADR-0026 § Landing surface. TargetSubAgent values are pinned to
// the AgentName constants from the Application.Ai namespace — Wizard,
// Valuation, Rules, Repair. The slug is URL-friendly (lowercase + hyphens)
// and serves as the path segment in /wizard/q/{slug}.
//
// SeedQuestionsContractTests pins the on-disk wizard_seed_questions.v1.json:
// exactly 4 entries, one per sub-agent path. Any addition or rename here
// must keep that contract test green.
public sealed record SeedQuestion(
    string Slug,
    string Question,
    string TargetSubAgent,
    string Description);
