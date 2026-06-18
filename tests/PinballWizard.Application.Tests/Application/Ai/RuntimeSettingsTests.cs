using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai.Hosting;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai;

// PR-B1: the layering rule is the contract — stored admin override wins,
// absent override falls back to the IOptions default, and a malformed
// stored value degrades to the default VISIBLY (warning), never silently
// poisons the ask.
// PR retrieval-runtime-keys: extends the snapshot with RetrievalTopK and
// RetrievalMinimumScore; same layering rule applies.
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
        // Retrieval defaults match RetrievalOptions record-parameter defaults
        // (ADR-0021 § Search defaults): TopK=10, MinimumScore=0.0.
        var retrievalDefaults = new RetrievalOptions();
        Assert.Equal(retrievalDefaults.TopK, snapshot.RetrievalTopK);
        Assert.Equal(retrievalDefaults.MinimumScore, snapshot.RetrievalMinimumScore);
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
    public async Task GetSnapshotAsync_RetrievalTopKOverride_WinsOverDefault()
    {
        var repo = Substitute.For<IAdminSettingsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(null));
        repo.GetAsync(WellKnownSettings.RetrievalTopK, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(Record(WellKnownSettings.RetrievalTopK, "5")));

        var snapshot = await Build(repo).GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(5, snapshot.RetrievalTopK);
        Assert.Equal(new RetrievalOptions().MinimumScore, snapshot.RetrievalMinimumScore); // untouched
    }

    [Fact]
    public async Task GetSnapshotAsync_RetrievalMinimumScoreOverride_WinsOverDefault()
    {
        var repo = Substitute.For<IAdminSettingsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(null));
        repo.GetAsync(WellKnownSettings.RetrievalMinimumScore, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AdminSettingRecord?>(Record(WellKnownSettings.RetrievalMinimumScore, "0.45")));

        var snapshot = await Build(repo).GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(0.45, snapshot.RetrievalMinimumScore, precision: 6);
        Assert.Equal(new RetrievalOptions().TopK, snapshot.RetrievalTopK); // untouched
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
    [InlineData("ai.confidence_threshold", "0.2", false)]   // below floor
    [InlineData("ai.confidence_threshold", "0.99", false)]  // above cap
    [InlineData("ai.per_call_cost_ceiling_usd_cents", "25", true)]
    [InlineData("ai.per_call_cost_ceiling_usd_cents", "0", false)]
    [InlineData("ai.max_conversation_turns", "12", true)]
    [InlineData("ai.max_conversation_turns", "21", false)]  // above the API guard
    [InlineData("ai.max_conversation_turns", "many", false)]
    // Retrieval keys (PR retrieval-runtime-keys)
    [InlineData("rag.retrieval_top_k", "10", true)]
    [InlineData("rag.retrieval_top_k", "1", true)]          // floor
    [InlineData("rag.retrieval_top_k", "20", true)]         // ceiling
    [InlineData("rag.retrieval_top_k", "0", false)]         // below floor
    [InlineData("rag.retrieval_top_k", "21", false)]        // above ceiling (TopKCeiling)
    [InlineData("rag.retrieval_minimum_score", "0.0", true)]
    [InlineData("rag.retrieval_minimum_score", "0.5", true)]
    [InlineData("rag.retrieval_minimum_score", "1.0", true)]
    [InlineData("rag.retrieval_minimum_score", "-0.1", false)] // below floor
    [InlineData("rag.retrieval_minimum_score", "1.1", false)]  // above ceiling
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
