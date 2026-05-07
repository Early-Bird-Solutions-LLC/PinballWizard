using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai.Cost;

// Default IAiCostCalculator backed by AiFoundryOptions.PricingTable.
// Each deployment carries an InputCentsPer1K + OutputCentsPer1K rate;
// the calculator multiplies by token counts and sums.
public sealed class AiCostCalculator : IAiCostCalculator
{
    private readonly AiFoundryOptions _options;
    private readonly ILogger<AiCostCalculator> _logger;

    public AiCostCalculator(IOptions<AiFoundryOptions> options, ILogger<AiCostCalculator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public double ComputeUsdCents(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (string.IsNullOrWhiteSpace(usage.DeploymentName))
        {
            return 0.0;
        }

        if (!_options.PricingTable.TryGetValue(usage.DeploymentName, out var pricing))
        {
            _logger.LogDebug(
                "AiCostCalculator: no pricing row for deployment '{DeploymentName}'. Cost reported as 0; add an entry to AiFoundryOptions.PricingTable to attribute this deployment.",
                usage.DeploymentName);
            return 0.0;
        }

        var inputCents = (usage.InputTokens / 1000.0) * pricing.InputCentsPer1K;
        var outputCents = (usage.OutputTokens / 1000.0) * pricing.OutputCentsPer1K;
        return inputCents + outputCents;
    }
}
