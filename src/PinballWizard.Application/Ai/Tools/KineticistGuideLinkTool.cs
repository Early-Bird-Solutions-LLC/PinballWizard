using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Kineticist;
using PinballWizard.Application.Observability;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to the Wizard orchestrator (ADR-0043 Tier B).
// Sibling to MarketValueTool — returns attributed guide deep-links for a
// resolved machine so Rules questions can route outward to Kineticist when
// corpus grounding is thin or absent.
//
// Attached to the WIZARD ONLY (not sub-agents) — ToolTraceCitationExtractor
// can only observe FunctionResultContent in the Wizard's AgentResponse.Messages.
//
// IKineticistGuideLinkProvider is optional (nullable DI). When absent — because
// Kineticist:ApiKey is not configured — the tool returns null immediately.
// Fail closed: never scrapes /news/*.md as a silent fallback.
//
// The `= null` / `= default` on CancellationToken is load-bearing: without it,
// AIFunctionFactory.Create includes the parameter in the JSON Schema `required`
// array (same rationale as SearchCorpusTool / MarketValueTool).
public sealed class KineticistGuideLinkTool
{
    internal const string ToolTagValue = "getKineticistGuideLinks";

    private readonly IKineticistGuideLinkProvider? _provider;
    private readonly ILogger<KineticistGuideLinkTool> _logger;

    public KineticistGuideLinkTool(
        ILogger<KineticistGuideLinkTool> logger,
        IKineticistGuideLinkProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _provider = provider;
    }

    [Description("Get attributed gameplay-rules deep-links for a pinball machine from the Kineticist partner API (ADR-0043 Tier B). Returns Kineticist's full tutorial URL, an optional external game-rules URL, and an optional pinball-primer URL. Prefer passing opdbId from getMachineByTitle. Returns null when the API key is absent, the game is unknown, or no deep-link fields are present. Never fabricates URLs. Use when routing Rules questions so the answer can cite / route the player to the exact guide.")]
    public async Task<KineticistGuideLinksDto?> GetKineticistGuideLinksAsync(
        [Description("Optional: the OPDB ID for the machine (e.g. 'G33YF-Mxxxx' or group id). Preferred lookup — Kineticist's /games/{idOrSlug} accepts OPDB ids directly.")] string? opdbId = null,
        [Description("Optional: Kineticist game slug when known (e.g. 'godzilla-2021'). Used only when opdbId is absent.")] string? kineticistSlug = null,
        [Description("Optional: display title for citation text (e.g. from getMachineByTitle). Does not affect lookup.")] string? machineTitle = null,
        CancellationToken cancellationToken = default)
    {
        if (_provider is null)
        {
            _logger.LogDebug(
                "KineticistGuideLinkTool: IKineticistGuideLinkProvider not registered (Kineticist:ApiKey absent) — returning null.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(opdbId) && string.IsNullOrWhiteSpace(kineticistSlug))
        {
            _logger.LogDebug("KineticistGuideLinkTool: no opdbId or kineticistSlug supplied — returning null.");
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _provider
                .GetGuideLinksAsync(opdbId, kineticistSlug, cancellationToken)
                .ConfigureAwait(false);

            if (result is null || !result.HasAnyLink)
            {
                _logger.LogDebug(
                    "KineticistGuideLinkTool: no guide deep-links for opdbId={OpdbId} slug={Slug}",
                    opdbId ?? "(none)",
                    kineticistSlug ?? "(none)");
                return null;
            }

            var title = string.IsNullOrWhiteSpace(machineTitle) ? result.Name : machineTitle.Trim();

            _logger.LogDebug(
                "KineticistGuideLinkTool: deep-links found machineTitle={MachineTitle} opdbId={OpdbId} slug={Slug} hasTutorial={HasTutorial}",
                title,
                opdbId ?? result.OpdbId ?? "(none)",
                result.Slug,
                !string.IsNullOrWhiteSpace(result.FullTutorialLink));

            return new KineticistGuideLinksDto(
                MachineTitle: title,
                OpdbId: result.OpdbId ?? opdbId,
                KineticistSlug: result.Slug,
                FullTutorialLink: result.FullTutorialLink,
                GameRulesLink: result.GameRulesLink,
                PinballPrimerUrl: result.PinballPrimerUrl,
                AttributionUrl: result.PrimaryAttributionUrl,
                AttributionText: $"Gameplay guide on Kineticist — {title}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: log + meter, return null. Never fall back to scraping.
            PinballWizardTelemetry.AiToolErrors.Add(
                1,
                new KeyValuePair<string, object?>("tool", ToolTagValue));

            _logger.LogWarning(
                ex,
                "KineticistGuideLinkTool: unexpected exception — returning null (fail closed, no scrape fallback). opdbId={OpdbId} slug={Slug}",
                opdbId ?? "(none)",
                kineticistSlug ?? "(none)");

            return null;
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", ToolTagValue));
        }
    }
}
