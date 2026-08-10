using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// ADR-0054 Wave 2 (Plan 2) — DocumentLinker-on-MachineResolver migration tests.
//
// Task 3 scope: the linker builds the identity-derived resolver index alongside the
// legacy slug index when an IMachineAliasLoader is supplied. Resolution behaviour is
// unchanged until the tiers consult the resolver (Tasks 4-7 extend this file).
public sealed class DocumentLinkerResolverTests
{
    [Fact]
    public async Task InitializeAsync_WithAliasLoader_IndexesSlugLessMachines()
    {
        // A machine with NO ManufacturerSlugs: invisible to the legacy bySlug index.
        var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };

        var linker = await BuildLinkerWithResolverAsync(machines);

        // The resolver index must contain variants for this machine even though it has no slugs.
        Assert.True(linker.ResolverVariantCountForTest > 0);
    }

    [Fact]
    public async Task InitializeAsync_AliasLoaderThrows_PropagatesException()
    {
        // The alias loader is fail-closed: a missing or invalid seed must kill the run
        // at startup, never degrade into a resolver-less linker that silently reverts
        // to legacy behaviour (invariant #17). This pins that posture so a future
        // try/catch "graceful degradation" regression fails a test, not just prose.
        var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };
        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<MachineAliasEntry>>(
                _ => throw new InvalidOperationException("seed missing"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildLinkerAsync(machines, aliasLoader));
    }

    [Fact]
    public async Task InitializeAsync_WithoutAliasLoader_DoesNotBuildResolverIndex()
    {
        // The pre-migration construction path (no alias loader) must stay inert:
        // no resolver index, so every tier keeps its legacy behaviour.
        var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };

        var linker = await BuildLinkerAsync(machines, aliasLoader: null);

        Assert.Equal(0, linker.ResolverVariantCountForTest);
    }

    // ── Task 4: Tier 2 (filename) via the resolver ─────────────────────────────

    [Fact]
    public async Task Tier2_LinksSlugLessMachine_ByFranchiseTitle()
    {
        // NOTE: the plan's version of this test used SourceType.ManualsPage, which
        // InferManufacturerKey maps to STERN — under the fuzzy tiers' hard
        // manufacturer filter (NarrowToSourceManufacturer contract, mirrored by the
        // resolver) an americanpinball machine is then unreachable by design, so
        // that test was unsatisfiable. AmericanPinballGamePage is what the AP
        // scraper actually stamps.
        var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };
        var linker = await BuildLinkerWithResolverAsync(machines);

        var raw = MakeRaw("doc-hw", "https://americanpinball.com/hot-wheels-manual.pdf",
            gameSlug: "", manufacturerKey: "americanpinball",
            sourceType: SourceType.AmericanPinballGamePage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["AP-Hot-Wheels"], result.LinkedMachineIds);
        // Pins that the RESOLVER path linked it — the legacy title fallback would
        // report "filename_slug", and Task 8 will delete that fallback.
        Assert.Equal("filename_resolver", result.ResolutionStrategy);
    }

    [Fact]
    public async Task Tier2_CuratedAlias_LinksAcronymFilename()
    {
        // The alias is the resolver-only capability: "MTMTE" appears in no title or
        // slug, so the legacy index can never reach the machine — only the curated
        // alias (machine_aliases.v1.json shape) resolves it. This is the red test
        // that proves Tier 2 actually consults the resolver.
        var machines = new[]
        {
            MakeMachine("GBLzz-M4ok4", "Transformers: More Than Meets the Eye", "stern",
                groupId: "GBLzz"),
        };
        var aliases = new[]
        {
            new MachineAliasEntry("MTMTE", OpdbGroupId: "GBLzz", MachineId: null,
                ManufacturerKey: "stern", Notes: "test", AddedBy: "test"),
        };
        var linker = await BuildLinkerWithResolverAsync(machines, aliases);

        var raw = MakeRaw("doc-mtmte", "https://sternpinball.com/Transformers_MTMTE_Pro_web.pdf",
            gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GBLzz-M4ok4"], result.LinkedMachineIds);
        // The machine belongs to a group, so the resolver's Resolved arm must route
        // through ResolveEditionFamily and stamp SingleEdition — a bare
        // "filename_resolver" result here would carry the FranchiseWide default,
        // which mis-scopes a single-edition manual across the whole family.
        Assert.Equal("filename_resolver_edition", result.ResolutionStrategy);
        Assert.Equal(EditionScope.SingleEdition, result.EditionScope);
    }

    [Fact]
    public async Task Tier2_SingleTokenTrailingQualifier_DoesNotMatch()
    {
        // The 172-document incident: a machine literally titled "Pinball" must not absorb
        // every document whose filename contains the word "pinball".
        var machines = new[] { MakeMachine("Stern-Pinball-1977", "Pinball", "stern") };
        var linker = await BuildLinkerWithResolverAsync(machines);

        var raw = MakeRaw("doc-generic", "https://sternpinball.com/service-bulletin-pinball.pdf",
            gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Empty(result.LinkedMachineIds);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Mirrors GoldenLinkSetReplayTests.MakeMachine but adds groupId and makes slugs
    // optional — Machine.ManufacturerSlugs defaults to an empty dictionary, so a
    // slug-less machine needs no argument. (No shared MachineFixtures class exists
    // in this repo; each test file defines its own builder.)
    private static Machine MakeMachine(
        string id, string title, string manufacturer,
        string? groupId = null,
        IDictionary<string, string>? slugs = null)
        => new()
        {
            Id = id,
            PartitionKey = manufacturer,
            ManufacturerDisplayName = manufacturer,
            Title = title,
            GroupId = groupId,
            ManufacturerSlugs = slugs is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(slugs, StringComparer.OrdinalIgnoreCase),
        };

    // Mirrors GoldenLinkSetReplayTests.BuildLinkerAsync, additionally passing an
    // IMachineAliasLoader whose LoadAsync returns the given aliases (empty by default).
    private static Task<DocumentLinker> BuildLinkerWithResolverAsync(
        IEnumerable<Machine> machines,
        IReadOnlyList<MachineAliasEntry>? aliases = null,
        CancellationToken ct = default)
    {
        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(aliases ?? []);
        return BuildLinkerAsync(machines, aliasLoader, ct);
    }

    // Mirrors GoldenLinkSetReplayTests.MakeRaw. The sourceType parameter is
    // load-bearing: LinkingUtilities.InferManufacturerKey derives the manufacturer
    // hint FROM SourceType, and that hint drives the Tier-2 manufacturer scoping.
    private static RawDocumentRecord MakeRaw(
        string documentId,
        string fileUrl,
        string gameSlug,
        string manufacturerKey,
        DocumentType docType = DocumentType.Manual,
        SourceType sourceType = SourceType.ManualsPage)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = docType,
            Source = new SourceInfo
            {
                DiscoveryUrl = $"https://example.com/{manufacturerKey}/manuals/",
                DiscoveryContext = $"{manufacturerKey} Manuals page",
                FileUrl = fileUrl,
                ScrapedAt = DateTime.UtcNow,
                SourceType = sourceType,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            Game = new GameReference
            {
                Title = gameSlug.Replace('-', ' '),
                Slug = gameSlug,
                GamePageUrl = $"https://example.com/{manufacturerKey}/game/{gameSlug}/",
            },
        };

    private static async Task<DocumentLinker> BuildLinkerAsync(
        IEnumerable<Machine> machines,
        IMachineAliasLoader? aliasLoader,
        CancellationToken ct = default)
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());

        var machineList = machines.ToList();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            textExtractor: null,
            NullLogger<DocumentLinker>.Instance,
            blobStore: null,
            aliasLoader: aliasLoader);

        await linker.InitializeAsync(ct);
        return linker;
    }
}
