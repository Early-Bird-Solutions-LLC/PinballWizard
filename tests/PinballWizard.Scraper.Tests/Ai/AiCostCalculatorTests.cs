using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

public sealed class AiCostCalculatorTests
{
    private static AiCostCalculator CreateCalculator(Dictionary<string, ModelPricing>? pricing = null)
    {
        var options = new AiFoundryOptions
        {
            ProjectEndpoint = "https://example.com",
        };

        if (pricing is not null)
        {
            options.PricingTable.Clear();
            foreach (var (k, v) in pricing)
            {
                options.PricingTable[k] = v;
            }
        }

        return new AiCostCalculator(Options.Create(options), NullLogger<AiCostCalculator>.Instance);
    }

    [Fact]
    public void ComputeUsdCents_KnownDeployment_AppliesPerKRates()
    {
        // 1k input × 0.015c + 0.5k output × 0.060c = 0.015 + 0.030 = 0.045c
        var calc = CreateCalculator();
        var cost = calc.ComputeUsdCents(new TokenUsage("gpt-4o-mini", InputTokens: 1000, OutputTokens: 500));

        Assert.Equal(0.045, cost, precision: 4);
    }

    [Fact]
    public void ComputeUsdCents_HeavyTier_AppliesHigherRate()
    {
        // 1k input × 0.20c + 1k output × 0.80c = 0.20 + 0.80 = 1.00c
        var calc = CreateCalculator();
        var cost = calc.ComputeUsdCents(new TokenUsage("gpt-4-1", InputTokens: 1000, OutputTokens: 1000));

        Assert.Equal(1.00, cost, precision: 4);
    }

    [Fact]
    public void ComputeUsdCents_UnknownDeployment_ReturnsZero()
    {
        var calc = CreateCalculator();
        var cost = calc.ComputeUsdCents(new TokenUsage("not-a-real-deployment", 1000, 1000));
        Assert.Equal(0.0, cost);
    }

    [Fact]
    public void ComputeUsdCents_ZeroTokens_ReturnsZero()
    {
        var calc = CreateCalculator();
        var cost = calc.ComputeUsdCents(new TokenUsage("gpt-4o-mini", 0, 0));
        Assert.Equal(0.0, cost);
    }

    [Fact]
    public void ComputeUsdCents_EmptyDeploymentName_ReturnsZero()
    {
        var calc = CreateCalculator();
        var cost = calc.ComputeUsdCents(new TokenUsage(string.Empty, 1000, 1000));
        Assert.Equal(0.0, cost);
    }

    [Fact]
    public void ComputeUsdCents_NullUsage_Throws()
    {
        var calc = CreateCalculator();
        Assert.Throws<ArgumentNullException>(() => calc.ComputeUsdCents(null!));
    }

    [Fact]
    public void ComputeUsdCents_PricingTableEmpty_ReturnsZeroForAllInputs()
    {
        var calc = CreateCalculator(pricing: new Dictionary<string, ModelPricing>());
        var cost = calc.ComputeUsdCents(new TokenUsage("gpt-4o-mini", 10000, 10000));
        Assert.Equal(0.0, cost);
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AiCostCalculator(null!, NullLogger<AiCostCalculator>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var options = Options.Create(new AiFoundryOptions { ProjectEndpoint = "https://example.com" });
        Assert.Throws<ArgumentNullException>(() => new AiCostCalculator(options, null!));
    }

    [Fact]
    public void DefaultPricingTable_HasExpectedDeployments()
    {
        var options = new AiFoundryOptions();
        Assert.Contains("gpt-4o-mini", options.PricingTable.Keys);
        Assert.Contains("gpt-4-1", options.PricingTable.Keys);
        Assert.Contains("text-embedding-3-large", options.PricingTable.Keys);
    }
}
