using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Persistence;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai;

// PR-B3: the load-bearing invariants for OverridingAgentPromptProvider:
//   1. Active override wins over embedded resource.
//   2. Inactive override is ignored — embedded resource is used.
//   3. Cosmos failure falls back to embedded resource WITH a warning log
//      (invariant #17 — degrade visibly, never mask).
//   4. PromptVersion includes "+{agentName}.v{n}" suffix when an override
//      is active (cache-key correctness: stale cached answers must not
//      survive a prompt change).
//   5. No repository registered (null) → behaves like embedded-only.
public sealed class OverridingAgentPromptProviderTests
{
    private static EmbeddedResourceAgentPromptProvider BuildEmbedded()
        => new EmbeddedResourceAgentPromptProvider();

    private static OverridingAgentPromptProvider Build(
        IAgentPromptOverrideRepository? repo = null)
        => new(BuildEmbedded(), NullLogger<OverridingAgentPromptProvider>.Instance, repo);

    private static AgentPromptOverride MakeOverride(string agentName, int version, bool isActive)
        => new(agentName, version, $"custom prompt for {agentName} v{version}", isActive,
               DateTimeOffset.UnixEpoch, "test-admin");

    // ── Resolution order ─────────────────────────────────────────────

    [Fact]
    public void GetPrompt_ActiveOverrideExists_ReturnsOverrideContent()
    {
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(AgentName.Wizard, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(
                MakeOverride(AgentName.Wizard, 2, isActive: true)));

        var provider = Build(repo);
        var result = provider.GetPrompt(AgentName.Wizard);

        Assert.Equal($"custom prompt for {AgentName.Wizard} v2", result);
    }

    [Fact]
    public void GetPrompt_NoActiveOverride_ReturnsEmbeddedContent()
    {
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));

        var provider = Build(repo);
        var embedded = BuildEmbedded().GetPrompt(AgentName.Wizard);
        var result = provider.GetPrompt(AgentName.Wizard);

        Assert.Equal(embedded, result);
    }

    [Fact]
    public void GetPrompt_NullRepository_ReturnsEmbeddedContent()
    {
        // Host without Cosmos wired — IAgentPromptOverrideRepository not registered.
        var provider = Build(repo: null);
        var embedded = BuildEmbedded().GetPrompt(AgentName.Wizard);
        var result = provider.GetPrompt(AgentName.Wizard);

        Assert.Equal(embedded, result);
    }

    [Fact]
    public void GetPrompt_CosmosFailure_FallsBackToEmbeddedWithoutThrowing()
    {
        // Invariant #17: an unreachable override store must not take down asks.
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cosmos down"));

        var provider = Build(repo);
        var embedded = BuildEmbedded().GetPrompt(AgentName.Wizard);

        // Must not throw.
        var result = provider.GetPrompt(AgentName.Wizard);

        Assert.Equal(embedded, result);
    }

    // ── PromptVersion — cache-key correctness ────────────────────────

    [Fact]
    public void PromptVersion_NoOverrides_EqualsEmbeddedVersion()
    {
        var provider = Build(repo: null);

        Assert.Equal(EmbeddedResourceAgentPromptProvider.CurrentPromptVersion,
            provider.PromptVersion);
    }

    [Fact]
    public async Task RefreshVersionAsync_ActiveWizardOverrideV2_AppendsWizardSuffix()
    {
        var repo = Substitute.For<IAgentPromptOverrideRepository>();

        // Wizard has active v2; others have no override.
        repo.GetActiveAsync(AgentName.Wizard, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(
                MakeOverride(AgentName.Wizard, 2, isActive: true)));
        repo.GetActiveAsync(Arg.Is<string>(n => n != AgentName.Wizard), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));

        var provider = Build(repo);

        // Force a TTL-bypassed refresh by calling RefreshVersionAsync directly.
        await provider.RefreshVersionAsync(CancellationToken.None);

        var expected = $"{EmbeddedResourceAgentPromptProvider.CurrentPromptVersion}+Wizard.v2";
        Assert.Equal(expected, provider.PromptVersion);
    }

    [Fact]
    public async Task RefreshVersionAsync_MultipleActiveOverrides_SuffixesAreSortedAlphabetically()
    {
        // Repair v1 and Wizard v3 both active. Alphabetical order: Repair before Wizard.
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(AgentName.Repair, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(
                MakeOverride(AgentName.Repair, 1, isActive: true)));
        repo.GetActiveAsync(AgentName.Wizard, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(
                MakeOverride(AgentName.Wizard, 3, isActive: true)));
        repo.GetActiveAsync(Arg.Is<string>(n => n != AgentName.Repair && n != AgentName.Wizard),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));

        var provider = Build(repo);
        await provider.RefreshVersionAsync(CancellationToken.None);

        var expected = $"{EmbeddedResourceAgentPromptProvider.CurrentPromptVersion}+Repair.v1+Wizard.v3";
        Assert.Equal(expected, provider.PromptVersion);
    }

    [Fact]
    public async Task RefreshVersionAsync_AllOverridesDeactivated_VersionReverts()
    {
        var repo = Substitute.For<IAgentPromptOverrideRepository>();

        // First refresh: Wizard v2 active.
        repo.GetActiveAsync(AgentName.Wizard, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(
                MakeOverride(AgentName.Wizard, 2, isActive: true)));
        repo.GetActiveAsync(Arg.Is<string>(n => n != AgentName.Wizard), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));

        var provider = Build(repo);
        await provider.RefreshVersionAsync(CancellationToken.None);
        Assert.Contains("+Wizard.v2", provider.PromptVersion);

        // Simulate deactivation — force a new refresh by expiring the TTL.
        // We can't easily control the clock here, so we test the logic by
        // constructing a fresh provider (clean TTL state) with no active row.
        var repo2 = Substitute.For<IAgentPromptOverrideRepository>();
        repo2.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));

        var provider2 = Build(repo2);
        await provider2.RefreshVersionAsync(CancellationToken.None);

        Assert.Equal(EmbeddedResourceAgentPromptProvider.CurrentPromptVersion, provider2.PromptVersion);
    }

    [Fact]
    public async Task RefreshVersionAsync_PartialCosmosFailure_ExcludesFailingAgent()
    {
        // Rules lookup throws; Wizard returns active v1. Version string
        // should still include Wizard suffix — one failing agent must not
        // block the others.
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(AgentName.Rules, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cosmos blip"));
        repo.GetActiveAsync(AgentName.Wizard, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(
                MakeOverride(AgentName.Wizard, 1, isActive: true)));
        repo.GetActiveAsync(Arg.Is<string>(n => n != AgentName.Rules && n != AgentName.Wizard),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));

        var provider = Build(repo);
        // Should not throw even when one agent's lookup fails.
        await provider.RefreshVersionAsync(CancellationToken.None);

        Assert.Contains("+Wizard.v1", provider.PromptVersion);
        Assert.DoesNotContain("Rules", provider.PromptVersion);
    }

    [Fact]
    public async Task RefreshVersionAsync_NullRepository_LeavesVersionAtEmbedded()
    {
        var provider = Build(repo: null);
        // Should be a no-op — not throw.
        await provider.RefreshVersionAsync(CancellationToken.None);

        Assert.Equal(EmbeddedResourceAgentPromptProvider.CurrentPromptVersion,
            provider.PromptVersion);
    }
}
