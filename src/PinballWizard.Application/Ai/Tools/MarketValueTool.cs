using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Pricing;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to the Wizard orchestrator (ADR-0045).
// Sibling to MachineGroundingTool and SearchCorpusTool — this one provides
// live market pricing from the Silverball Labs API.
//
// Attached to the WIZARD ONLY (not sub-agents) — same reasoning as searchCorpus:
// ToolTraceCitationExtractor can only observe FunctionResultContent in the Wizard's
// AgentResponse.Messages; sub-agent internal tool calls are invisible to it.
// The Wizard calls getMarketValue before dispatching to the Valuation sub-agent,
// then passes the result inline so Valuation can cite it (Wizard.md Step 3.75).
//
// IMarketValueProvider is optional (nullable DI injection). When absent — because
// SilverballLabs:ApiKey is not configured — the tool returns null immediately,
// and the Wizard degrades to an honest "live pricing unavailable" answer rather
// than fabricating or throwing (invariant #17).
//
// The `= null` default on the CancellationToken is load-bearing: without it,
// AIFunctionFactory.Create includes the parameter in the JSON Schema `required`
// array and the model would be forced to supply it, which is never correct.
// See SearchCorpusTool for the same rationale (ev-repair-0008 root cause).
public sealed class MarketValueTool
{
    internal const string ToolTagValue = "getMarketValue";

    private readonly IMarketValueProvider? _provider;
    private readonly ILogger<MarketValueTool> _logger;

    public MarketValueTool(
        ILogger<MarketValueTool> logger,
        IMarketValueProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _provider = provider;
    }

    [Description("Get live market pricing for a pinball machine. Returns recent sale prices by condition (mint/excellent/good/fair/poor), median price, trend direction, and a sourced summary. Returns null when live pricing data is unavailable. Every price surfaced MUST be attributed to Silverball Labs and PinballPrices.com per ADR-0045 terms.")]
    public async Task<MarketValueDto?> GetMarketValueAsync(
        [Description("The pinball machine title as resolved by getMachineByTitle.")] string machineTitle,
        [Description("Optional: the OPDB ID for the machine (e.g. 'GRBNN-MQERZ'). Preferred lookup path — more reliable than title-based matching. Pass from the getMachineByTitle result when available.")] string? opdbId = null,
        [Description("Optional: the machine manufacturer (e.g. 'Williams'). Used for the name-based fallback lookup when OPDB ID is not available.")] string? manufacturer = null,
        CancellationToken cancellationToken = default)
    {
        if (_provider is null)
        {
            // Silverball Labs not configured — log at Debug so it's visible
            // during local dev without the key but doesn't fire on every
            // production call where the key is expected. The Wizard degrades
            // gracefully to an "unavailable" answer per the Valuation.md prompt.
            _logger.LogDebug("MarketValueTool: IMarketValueProvider not registered (SilverballLabs:ApiKey absent) — returning null.");
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _provider
                .GetMarketValueAsync(opdbId, machineTitle, manufacturer, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                _logger.LogDebug(
                    "MarketValueTool: no pricing data for machineTitle={MachineTitle} opdbId={OpdbId}",
                    machineTitle,
                    opdbId ?? "(none)");
                return null;
            }

            _logger.LogDebug(
                "MarketValueTool: pricing data found machineTitle={MachineTitle} opdbId={OpdbId} medianPrice={MedianPrice}",
                machineTitle,
                opdbId ?? "(none)",
                result.MedianPrice);

            // Map Application result → tool DTO. Attribution fields are preserved
            // verbatim so the citation extractor can build a MarketValue citation
            // without an additional lookup.
            return new MarketValueDto(
                MachineTitle: machineTitle,
                MedianPrice: result.MedianPrice,
                AvgPrice: result.AvgPrice,
                Min: result.Min,
                Max: result.Max,
                ByCondition: result.ByCondition
                    .Select(c => new MarketValueConditionDto(c.Condition, c.MedianPrice, c.SaleCount))
                    .ToList(),
                TrendDirection: result.TrendDirection,
                PriceSummary: result.PriceSummary,
                LastSaleDate: result.LastSaleDate,
                AttributionUrl: result.Attribution.Url,
                AttributionText: result.Attribution.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: log + meter, return null so the Wizard degrades
            // gracefully rather than aborting the Valuation turn.
            PinballWizardTelemetry.AiToolErrors.Add(
                1,
                new KeyValuePair<string, object?>("tool", ToolTagValue));

            _logger.LogWarning(
                ex,
                "MarketValueTool: unexpected exception — returning null so Wizard can degrade gracefully. machineTitle={MachineTitle} opdbId={OpdbId}",
                machineTitle,
                opdbId ?? "(none)");

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
