namespace PinballWizard.Application.Kineticist;

// Application-layer result for ADR-0043 Tier B guide deep-links.
// Infrastructure maps KineticistGuideLinks → this shape so Application tools
// never take a dependency on the Infrastructure DTO.
public sealed record KineticistGuideLinkResult(
    string Slug,
    string Name,
    string? OpdbId,
    string? FullTutorialLink,
    string? GameRulesLink,
    string? PinballPrimerUrl)
{
    public bool HasAnyLink =>
        !string.IsNullOrWhiteSpace(FullTutorialLink)
        || !string.IsNullOrWhiteSpace(GameRulesLink)
        || !string.IsNullOrWhiteSpace(PinballPrimerUrl);

    public string? PrimaryAttributionUrl =>
        FirstNonEmpty(FullTutorialLink, GameRulesLink, PinballPrimerUrl);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }
}
