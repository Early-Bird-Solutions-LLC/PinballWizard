using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

public sealed class EmbeddedResourceAgentPromptProviderTests
{
    [Fact]
    public void Ctor_LoadsAllFourAgentPrompts()
    {
        var provider = new EmbeddedResourceAgentPromptProvider();

        // Each of the four agents must have a non-empty prompt loaded
        // from the embedded resource. PR 4 placeholder content is
        // sufficient; PR 5 fills with real instructions.
        Assert.NotEmpty(provider.GetPrompt(AgentName.Wizard));
        Assert.NotEmpty(provider.GetPrompt(AgentName.Valuation));
        Assert.NotEmpty(provider.GetPrompt(AgentName.Rules));
        Assert.NotEmpty(provider.GetPrompt(AgentName.Repair));
    }

    [Fact]
    public void PromptVersion_ReturnsCurrentConstant()
    {
        var provider = new EmbeddedResourceAgentPromptProvider();
        Assert.Equal(EmbeddedResourceAgentPromptProvider.CurrentPromptVersion, provider.PromptVersion);
    }

    [Theory]
    [InlineData(AgentName.Wizard)]
    [InlineData(AgentName.Valuation)]
    [InlineData(AgentName.Rules)]
    [InlineData(AgentName.Repair)]
    public void GetPrompt_NamedAgent_ReturnsContent(string agentName)
    {
        var provider = new EmbeddedResourceAgentPromptProvider();
        var prompt = provider.GetPrompt(agentName);

        Assert.NotNull(prompt);
        Assert.Contains(agentName, prompt);
    }

    [Fact]
    public void GetPrompt_UnknownAgent_Throws()
    {
        var provider = new EmbeddedResourceAgentPromptProvider();
        var ex = Assert.Throws<ArgumentException>(() => provider.GetPrompt("Sorcerer"));
        Assert.Contains("Sorcerer", ex.Message);
    }

    [Fact]
    public void GetPrompt_Whitespace_Throws()
    {
        var provider = new EmbeddedResourceAgentPromptProvider();
        Assert.Throws<ArgumentException>(() => provider.GetPrompt("   "));
    }
}
