using Microsoft.Extensions.Logging;
using PinballWizard.Application.Kineticist;

namespace PinballWizard.Infrastructure.Integrations.Kineticist;

// IKineticistGuideLinkProvider backed by the keyed Kineticist games API
// (ADR-0043 Tier B). Prefer OPDB-id lookup; fall back to Kineticist slug.
// Fail closed: API miss / empty fields → null. Never scrapes /news/*.md.
internal sealed class KineticistGuideLinkProvider : IKineticistGuideLinkProvider
{
    private readonly IKineticistApiClient _api;
    private readonly ILogger<KineticistGuideLinkProvider> _logger;

    public KineticistGuideLinkProvider(
        IKineticistApiClient api,
        ILogger<KineticistGuideLinkProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _logger = logger;
    }

    public async Task<KineticistGuideLinkResult?> GetGuideLinksAsync(
        string? opdbId,
        string? kineticistSlug,
        CancellationToken cancellationToken = default)
    {
        KineticistGuideLinks? links = null;

        // Primary: OPDB id (group or machine) — /games/{idOrSlug} accepts both.
        if (!string.IsNullOrWhiteSpace(opdbId))
        {
            links = await _api.GetGuideLinksAsync(opdbId.Trim(), cancellationToken).ConfigureAwait(false);
        }

        // Secondary: Kineticist slug when OPDB path missed or was not supplied.
        if (links is null && !string.IsNullOrWhiteSpace(kineticistSlug))
        {
            links = await _api.GetGuideLinksAsync(kineticistSlug.Trim(), cancellationToken).ConfigureAwait(false);
        }

        if (links is null)
        {
            _logger.LogDebug(
                "KineticistGuideLinkProvider: no guide deep-links for opdbId={OpdbId} slug={Slug}.",
                opdbId,
                kineticistSlug);
            return null;
        }

        return new KineticistGuideLinkResult(
            Slug: links.Slug,
            Name: links.Name,
            OpdbId: links.OpdbId,
            FullTutorialLink: links.FullTutorialLink,
            GameRulesLink: links.GameRulesLink,
            PinballPrimerUrl: links.PinballPrimerUrl);
    }
}
