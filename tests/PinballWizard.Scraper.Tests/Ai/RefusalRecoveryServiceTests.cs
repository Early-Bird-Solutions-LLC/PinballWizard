using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Refusal;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

// Behavioral tests for RefusalRecoveryService per ADR-0026 § 4 and the
// Wave 2 PR-R2/R3 spec. Each test exercises a distinct behavior path
// (token-overlap scoring, per-category community routing, cap enforcement,
// empty result, exception swallowing) to confirm that the recovery service
// enriches refusals correctly without ever breaking the primary path.
//
// IMachineRepository.QueryByTitleAsync is mocked with a synchronous
// async-iterator helper (ToAsyncEnumerable) — the same pattern used in
// MachineGroundingToolTests.
//
// ICommunityResourceLoader is mocked using NSubstitute. In tests where the
// community resource content is not the focus, the loader returns a minimal
// set that satisfies plurality minimums.
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

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

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

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

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

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

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

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

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

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        // Act: must not throw.
        var detail = await svc.BuildRecoveryAsync(
            "godzilla multiball",
            RefusalCategory.NoCitation,
            CancellationToken.None);

        Assert.Null(detail);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. OutOfScope → marketplace + machine_reference + manufacturer cards
    //    (PR-R3: CommunityResources routing)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Returns_Marketplace_And_MachineReference_Cards()
    {
        // Arrange: OutOfScope should route to marketplace, machine_reference,
        // and manufacturer_pages. Verify CommunityResources is non-null and
        // contains at least one card from each of the marketplace and
        // machine_reference categories.
        var repo = EmptyRepo();
        var loader = LoaderWithResources(
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"),
            ManufacturerPage("Stern Pinball", "https://sternpinball.example.com"));

        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "buy a godzilla",
            RefusalCategory.OutOfScope,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.CommunityResources);

        var categories = detail.CommunityResources!
            .Select(r => r.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("marketplace", categories);
        Assert.Contains("machine_reference", categories);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. OutOfScope marketplace cards count at least 3
    //    (ADR-0026 § 5 plurality pin — load-bearing test per spec)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Marketplace_Cards_Count_At_Least_3()
    {
        // This test pins the ADR-0026 § 5 plurality invariant: when the user
        // gets a refusal for OutOfScope, at least 3 marketplace cards must
        // appear. A count of fewer than 3 means we're implicitly recommending
        // one venue over others — the favoritism failure mode.
        //
        // The loader is populated with exactly 3 marketplace entries (the
        // minimum from the seed) so the test verifies the floor, not a
        // coincidental surplus.
        var repo = EmptyRepo();
        var loader = LoaderWithResources(
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"));

        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "where can I buy a machine",
            RefusalCategory.OutOfScope,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.CommunityResources);

        var marketplaceCount = detail.CommunityResources!
            .Count(r => string.Equals(r.Category, "marketplace", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            marketplaceCount >= 3,
            $"Expected at least 3 marketplace community cards for OutOfScope refusals (ADR-0026 § 5 plurality). Got {marketplaceCount}.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8. UpstreamThrottled → null CommunityResources
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_UpstreamThrottled_Returns_Null_CommunityResources()
    {
        // UpstreamThrottled is a transient infra fault — the refusal message
        // already tells the user to retry. Adding community resource cards
        // would clutter the RefusalPanel with irrelevant alternatives when
        // the only correct action is "wait and try again."
        // PR-R3 spec: CommunityResources = null for UpstreamThrottled.
        var repo = Substitute.For<IMachineRepository>();

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        // UpstreamThrottled returns null from BuildRecoveryAsync (whole-detail null)
        // because CategorySupportsRelatedMachines is false. Verify the whole
        // detail is null — null detail → no CommunityResources to inspect.
        var detail = await svc.BuildRecoveryAsync(
            "godzilla wizard mode",
            RefusalCategory.UpstreamThrottled,
            CancellationToken.None);

        Assert.Null(detail);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 9. InsufficientGrounding → forums + machine_reference + news_and_culture
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_InsufficientGrounding_Returns_Forums_And_MachineReference_Cards()
    {
        // InsufficientGrounding means retrieval returned chunks but scored too
        // low to ground an answer — route the user to forums + canonical refs
        // + news where they can find the answer from community members.
        var repo = EmptyRepo();
        var loader = LoaderWithResources(
            Forum("Pinside", "https://pinside.example.com"),
            Forum("Tilt Forums", "https://tiltforums.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"),
            NewsAndCulture("Pinball News", "https://pinballnews.example.com"),
            // marketplace is NOT in the InsufficientGrounding routing
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"));

        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "what are the rules for godzilla multiball",
            RefusalCategory.InsufficientGrounding,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.CommunityResources);

        var categories = detail.CommunityResources!
            .Select(r => r.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("forums", categories);
        Assert.Contains("machine_reference", categories);
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

    // Returns a loader stub that serves the minimum required plurality set
    // (3 marketplace + 2 machine_reference) so tests focused on RelatedMachines
    // do not fail on an empty or invalid loader.
    private static ICommunityResourceLoader MinimalLoader()
    {
        return LoaderWithResources(
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"));
    }

    // Returns a loader stub configured to return the given resources.
    private static ICommunityResourceLoader LoaderWithResources(params CommunityResource[] resources)
    {
        var loader = Substitute.For<ICommunityResourceLoader>();

        loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunityResource>>(resources.ToList().AsReadOnly()));

        // Wire LoadByCategoryAsync to filter from the resources list — mirrors
        // the real CommunityResourceLoader.LoadByCategoryAsync behaviour.
        loader.LoadByCategoryAsync(Arg.Any<CommunityResourceCategory>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cat = callInfo.Arg<CommunityResourceCategory>();
                var categoryString = CommunityResourceLoader.CategoryToString(cat);
                var filtered = resources
                    .Where(r => string.Equals(r.Category, categoryString, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
                return Task.FromResult<IReadOnlyList<CommunityResource>>(filtered);
            });

        return loader;
    }

    private static IMachineRepository EmptyRepo()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());
        return repo;
    }

    private static CommunityResource Marketplace(string name, string url) =>
        new(Name: name, Url: url, Category: "marketplace", Description: null);

    private static CommunityResource MachineRef(string name, string url) =>
        new(Name: name, Url: url, Category: "machine_reference", Description: null);

    private static CommunityResource ManufacturerPage(string name, string url) =>
        new(Name: name, Url: url, Category: "manufacturer_pages", Description: null);

    private static CommunityResource Forum(string name, string url) =>
        new(Name: name, Url: url, Category: "forums", Description: null);

    private static CommunityResource NewsAndCulture(string name, string url) =>
        new(Name: name, Url: url, Category: "news_and_culture", Description: null);
}
