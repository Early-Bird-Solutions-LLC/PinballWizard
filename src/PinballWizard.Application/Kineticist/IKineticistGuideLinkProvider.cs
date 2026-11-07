namespace PinballWizard.Application.Kineticist;

// Application-layer abstraction for ADR-0043 Tier B guide deep-links.
// Infrastructure implementation: KineticistGuideLinkProvider (Integrations/
// Kineticist/). DI-gated on KineticistOptions.ApiKeyKey presence — when
// absent, nothing registers this interface and KineticistGuideLinkTool
// returns null (Wizard degrades to corpus-only / honest refuse).
//
// Prefer OPDB id lookup (group or machine id). Kineticist slug is a
// secondary path when the Wizard only has a slug. Never scrapes the
// challenged /news/*.md surface — API deep-links only (fail closed).
public interface IKineticistGuideLinkProvider
{
    Task<KineticistGuideLinkResult?> GetGuideLinksAsync(
        string? opdbId,
        string? kineticistSlug,
        CancellationToken cancellationToken = default);
}
