using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai.Hosting;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai;

// PR-B1: the layering rule is the contract — stored admin override wins,
// absent override falls back to the IOptions default, and a malformed
// stored value degrades to the default VISIBLY (warning), never silently
// poisons the ask.
public sealed class RuntimeSettingsTests
{
    private static AiFoundryOptions DefaultOptions() => new()
    {
        ConfidenceThreshold = 0.65,
        PerCallCostCeilingUsdCents = 10,
        MaxConversationTurns = 8,
    };

    private static RuntimeSettings Build(IAdminSettingsRepository repository, AiFoundryOptions? options = null)
        => new(repository, Options.Create(options ?? DefaultOptions()), NullLogger<RuntimeSettings>.Instance);

    private static AdminSettingRecord Record(string key, string value)
        => new(key, value, DateTimeOffset.UnixEpoch, "test-admin");

    [Fact]
    public async Task GetSnapshotAsync_NoOverrides_ReturnsOptionsDefaults()
    {
        var repo = Substitute.For<IAdminSettingsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(null));

        var snapshot = await Build(repo).GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(0.65, snapshot.ConfidenceThreshold);
        Assert.Equal(10, snapshot.PerCallCostCeilingUsdCents);
        Assert.Equal(8, snapshot.MaxConversationTurns);
    }

    [Fact]
    public async Task GetSnapshotAsync_StoredOverride_WinsOverDefault()
    {
        var repo = Substitute.For<IAdminSettingsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(null));
        repo.GetAsync(WellKnownSettings.ConfidenceThreshold, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(Record(WellKnownSettings.ConfidenceThreshold, "0.8")));

        var snapshot = await Build(repo).GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(0.8, snapshot.ConfidenceThreshold);
        Assert.Equal(10, snapshot.PerCallCostCeilingUsdCents); // untouched key stays default
    }

    [Fact]
    public async Task GetSnapshotAsync_UnparsableStoredValue_FallsBackToDefault()
    {
        // Writes are validated, so this row can only come from outside the
        // page (Data Explorer edit, migration bug). The read must not
        // throw — and must not silently honor garbage either: the value
        // falls back to the default and the warning log names the row.
        var repo = Substitute.For<IAdminSettingsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(null));
        repo.GetAsync(WellKnownSettings.MaxConversationTurns, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(Record(WellKnownSettings.MaxConversationTurns, "lots")));

        var snapshot = await Build(repo).GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(8, snapshot.MaxConversationTurns);
    }

    [Fact]
    public async Task GetSnapshotAsync_RepositoryFailure_Propagates()
    {
        // Invariant #17: an ask must fail loudly rather than silently run
        // on defaults while the operator believes their override is live.
        var repo = Substitute.For<IAdminSettingsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<AdminSettingRecord?>>(_ => throw new InvalidOperationException("cosmos down"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(repo).GetSnapshotAsync(CancellationToken.None));
    }
}

public sealed class WellKnownSettingsTests
{
    [Theory]
    [InlineData("ai.confidence_threshold", "0.65", true)]
    [InlineData("ai.confidence_threshold", "0.2", false)]  // below floor
    [InlineData("ai.confidence_threshold", "0.99", false)] // above cap
    [InlineData("ai.per_call_cost_ceiling_usd_cents", "25", true)]
    [InlineData("ai.per_call_cost_ceiling_usd_cents", "0", false)]
    [InlineData("ai.max_conversation_turns", "12", true)]
    [InlineData("ai.max_conversation_turns", "21", false)] // above the API guard
    [InlineData("ai.max_conversation_turns", "many", false)]
    [InlineData("not.a.real.key", "1", false)]
    public void TryValidate_EnforcesRangesAndKeySet(string key, string value, bool expected)
    {
        var ok = WellKnownSettings.TryValidate(key, value, out var error);

        Assert.Equal(expected, ok);
        Assert.Equal(expected, error is null);
    }

    [Fact]
    public void DefaultFor_RoundTripsEveryWellKnownKey()
    {
        // Every key the page can show must have a resolvable default —
        // a key added to AllKeys without a DefaultFor arm fails here, at
        // authoring time, instead of throwing on the settings page.
        var options = new AiFoundryOptions();

        foreach (var key in WellKnownSettings.AllKeys)
        {
            var value = WellKnownSettings.DefaultFor(key, options);
            Assert.True(
                WellKnownSettings.TryValidate(key, value, out var error),
                $"Default for {key} ('{value}') failed its own validation: {error}");
        }
    }
}
