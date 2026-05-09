using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

// Behavioral tests for RefusalRecoveryService per ADR-0026 § 4 and
// the Wave 2 PR-R2 spec. Each test exercises a distinct behavior path
// (token-overlap scoring, per-category policy, cap enforcement, empty
// result, exception swallowing) to confirm that the recovery service
// enriches refusals correctly without ever breaking the primary path.
//
// IMachineRepository.QueryByTitleAsync is mocked with a synchronous
// async-iterator helper (ToAsyncEnumerable) — the same pattern used
// in MachineGroundingToolTests.
public sealed class RefusalRecoveryServiceTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 1. OutOfScope → top-3 machines by token-overlap
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Returns_Top3_Machines_By_Token_Overlap()
    {
        // Arrange: question "godzilla wizard mode tips" — tokens are
        // "godzilla", "wizard", "mode", "tips" (stop-words "and" etc. filtered).
        // Repository returns Godzilla for "godzilla" token only (1 hit),
        // Addams Family for "wizard" token only (1 hit),
        // Medieval Madness for both "wizard" and "mode" tokens (2 hits).
        // Expected order: Medieval Madness first (2), then Godzilla and Addams
        // Family (1 each).
        const string Question = "godzilla wizard mode tips";

        var godzilla = NewMachine("GRBN-GODZ", "Godzilla", "stern");
        var adamsFamily = NewMachine("GRBN-AFAM", "The Addams Family", "bally");
        var medievalMadness = NewMachine("GRBN-MMED", "Medieval Madness", "williams");

        var repo = Substitute.For<IMachineRepository>();

        // "godzilla" → Godzilla
        repo.QueryByTitleAsync("godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(godzilla));

        // "wizard" → Addams Family + Medieval Madness
        repo.QueryByTitleAsync("wizard", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(adamsFamily, medievalMadness));

        // "mode" → Medieval Madness only
        repo.QueryByTitleAsync("mode", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(medievalMadness));

        // "tips" → nothing
        repo.QueryByTitleAsync("tips", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var svc = new RefusalRecoveryService(repo, NullLogger<RefusalRecoveryService>.Instance);

        // Act
        var detail = await svc.BuildRecoveryAsync(Question, RefusalCategory.OutOfScope, CancellationToken.None);

        // Assert
        Assert.NotNull(detail);
        Assert.NotNull(detail!.RelatedMachines);
        var machines = detail.RelatedMachines!;

        Assert.True(machines.Count >= 1, "Expected at least 1 related machine.");

        // Medieval Madness scored 2 overlapping tokens — must be first.
        Assert.Equal("GRBN-MMED", machines[0].MachineId);
        Assert.Equal("Medieval Madness", machines[0].Title);

        // Godzilla and Addams Family each scored 1. Both should appear.
        var ids = machines.Select(m => m.MachineId).ToHashSet();
        Assert.Contains("GRBN-GODZ", ids);
        Assert.Contains("GRBN-AFAM", ids);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Cap at 3 related machines even if more overlap
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_LowConfidence_Caps_At_3_Related_Machines()
    {
        // Arrange: all 5 machines score 1 overlap each. Only 3 must appear
        // on the result regardless.
        const string Question = "stern pinball machine";

        var machines = Enumerable.Range(1, 5)
            .Select(i => NewMachine($"ID-{i}", $"Machine {i}", "stern"))
            .ToArray();

        var repo = Substitute.For<IMachineRepository>();

        // The token "stern" returns all 5 machines.
        repo.QueryByTitleAsync("stern", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machines));

        // "pinball" and "machine" return nothing (filtering keeps "stern"
        // as the load-bearing token).
        repo.QueryByTitleAsync("pinball", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());
        repo.QueryByTitleAsync("machine", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var svc = new RefusalRecoveryService(repo, NullLogger<RefusalRecoveryService>.Instance);

        // Act
        var detail = await svc.BuildRecoveryAsync(Question, RefusalCategory.LowModelConfidence, CancellationToken.None);

        // Assert
        Assert.NotNull(detail);
        Assert.NotNull(detail!.RelatedMachines);
        Assert.Equal(3, detail.RelatedMachines!.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. UpstreamThrottled → no recovery (returns null immediately)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_UpstreamThrottled_Returns_Null_Without_Querying_Repository()
    {
        // UpstreamThrottled is a transient infra fault. Recovery suggestions
        // would mislead the user about recoverability ("here are similar
        // machines" implies the request can be retried differently, but the
        // real message is "try again later"). Per IRefusalRecoveryService
        // policy: no recovery for this category.
        var repo = Substitute.For<IMachineRepository>();

        var svc = new RefusalRecoveryService(repo, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "godzilla rules",
            RefusalCategory.UpstreamThrottled,
            CancellationToken.None);

        Assert.Null(detail);

        // The repository must not be touched — category is filtered out
        // before any lookup.
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. No machines match any token → empty RelatedMachines (not null)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_NoMachinesMatch_Returns_RefusalDetail_With_Empty_RelatedMachines()
    {
        // When token-overlap scoring finds no candidate machines, the service
        // must still return a non-null RefusalDetail (the refusal was allowed)
        // with an empty (not null) RelatedMachines list. Null vs empty is a
        // meaningful distinction: null means "category unsupported or
        // exception"; empty means "supported category but nothing matched."
        var repo = Substitute.For<IMachineRepository>();

        // All token queries return nothing.
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var svc = new RefusalRecoveryService(repo, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "xyzzy unobtainium",
            RefusalCategory.InsufficientGrounding,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.RelatedMachines);
        Assert.Empty(detail.RelatedMachines!);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Repository throws → exception swallowed, returns null
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_RepositoryThrows_Swallows_Exception_And_Returns_Null()
    {
        // Best-effort guarantee: a repository failure must never surface to
        // the caller as an exception. The primary refusal is already
        // constructed; recovery is additive. Returning null means "no
        // enrichment available" — the caller emits the bare refusal.
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("simulated Cosmos failure"));

        var svc = new RefusalRecoveryService(repo, NullLogger<RefusalRecoveryService>.Instance);

        // Act: must not throw.
        var detail = await svc.BuildRecoveryAsync(
            "godzilla multiball",
            RefusalCategory.NoCitation,
            CancellationToken.None);

        Assert.Null(detail);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static Machine NewMachine(string id, string title, string manufacturer) => new()
    {
        Id = id,
        PartitionKey = manufacturer,
        ManufacturerDisplayName = manufacturer,
        Title = title,
        Year = 2020,
        OpdbSourceUrl = $"https://opdb.org/machines/{id}",
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
