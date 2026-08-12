using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
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

    // ── Task 5: page tiers (3-4) via the resolver ──────────────────────────────

    [Fact]
    public async Task PageTier_DoesNotLinkAcrossManufacturers()
    {
        // Page prose mentions many titles. A Stern manual saying "8 ball" must NOT bind
        // to the Williams machine — PageText is fuzzy, so scoping is a HARD filter.
        // This is the guard the migration must not break: it passes pre-change (the
        // legacy hard filter already does this) and must still pass after.
        var machines = new[] { MakeMachine("Williams-8Ball", "Eight Ball", "williams") };
        var linker = await BuildLinkerWithResolverAsync(
            machines, pageText: "eight ball is mentioned here");

        var raw = MakeRaw("doc-stern", "https://sternpinball.com/batman-manual.pdf",
            gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage,
            localPath: "docs/batman-manual.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
    }

    [Fact]
    public async Task PageTier_CuratedAlias_LinksAcronymPageText()
    {
        // Resolver-only capability at the page tiers: "MTMTE" appears in no title or
        // slug, so the legacy page-text index can never reach the machine. An opaque
        // filename keeps Tier 2 out of the way; only page-1 text carries the signal.
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
        var linker = await BuildLinkerWithResolverAsync(
            machines, aliases, pageText: "MTMTE pinball machine service manual");

        var raw = MakeRaw("doc-mtmte-page", "https://sternpinball.com/doc123.pdf",
            gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage,
            localPath: "docs/doc123.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GBLzz-M4ok4"], result.LinkedMachineIds);
        Assert.Equal("page_1_resolver", result.ResolutionStrategy);
    }

    // ── Task 6: Tier 1 (provenance slug) via the resolver ──────────────────────

    [Fact]
    public async Task Tier1_ProvenanceSlug_LinksToOtherManufacturer_WhenSoleCandidate()
    {
        // Regression guard: provenance scoping is a SOFT preference, so a
        // Stern-sourced doc whose slug resolves only to a Sega machine still links.
        // Must pass identically pre- and post-migration — if it fails pre-change,
        // the migration premise is wrong.
        var machines = new[]
        {
            MakeMachine("Sega-Godzilla-1998", "Godzilla", "sega",
                slugs: new Dictionary<string, string> { ["sega"] = "godzilla" }),
        };
        var linker = await BuildLinkerWithResolverAsync(machines);

        var raw = MakeRaw("doc-gz", "https://sternpinball.com/doc.pdf",
            gameSlug: "godzilla", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["Sega-Godzilla-1998"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task Tier1_ProvenanceSlug_LinksSlugLessMachine_ViaResolver()
    {
        // Resolver-only capability at Tier 1: the legacy _machinesBySlug index is
        // built from ManufacturerSlugs alone, so a slug-less machine is unreachable
        // by provenance slug — the resolver's title-derived variants reach it.
        var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };
        var linker = await BuildLinkerWithResolverAsync(machines);

        var raw = MakeRaw("doc-hw-slug", "https://americanpinball.com/some-opaque-doc.pdf",
            gameSlug: "hot-wheels", manufacturerKey: "americanpinball",
            sourceType: SourceType.AmericanPinballGamePage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["AP-Hot-Wheels"], result.LinkedMachineIds);
        // Pins that the RESOLVER handled it — the legacy index cannot see this machine.
        Assert.Equal("game_slug_resolver", result.ResolutionStrategy);
    }

    [Fact]
    public async Task Tier1_ProvenanceSlug_EditionFamily_FansOutGroupLevelDoc()
    {
        // The resolver's ResolvedFamily arm at Tier 1: two editions share a slug and
        // a GroupId; a group-level doc (rulesheet) must fan out to BOTH bases via
        // EditionResolver — the resolver narrows to the family, EditionResolver
        // decides within it.
        var machines = new[]
        {
            MakeMachine("BM66-Pro", "Batman '66", "stern", groupId: "G-bm66",
                slugs: new Dictionary<string, string> { ["stern"] = "batman-66" }),
            MakeMachine("BM66-Prem", "Batman '66", "stern", groupId: "G-bm66",
                slugs: new Dictionary<string, string> { ["stern"] = "batman-66" }),
        };
        var linker = await BuildLinkerWithResolverAsync(machines);

        var raw = MakeRaw("doc-bm66", "https://sternpinball.com/batman-66-rulesheet.pdf",
            gameSlug: "batman-66", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(2, result.LinkedMachineIds.Count);
        Assert.Equal("game_slug_resolver_edition_group", result.ResolutionStrategy);
        Assert.Equal(EditionScope.FranchiseWide, result.EditionScope);
    }

    // ── Issue #825: pure-numeric title / PageText evidence guard ──────────────

    [Fact]
    public async Task PageTier_PureNumericTitle_DoesNotLinkFromPageText()
    {
        // RED → GREEN mechanism test (issue #825).
        //
        // The mis-attribution path: AP bulletin scraper uses SourceType.ServiceBulletinPage,
        // which InferManufacturerKey maps to "stern". The Stern machine titled "24" has
        // variant "24" (single token, FullTitle). Page text of an AP service bulletin
        // contains "24 VDC" (coil voltage) → containment match → AP bulletin linked to
        // Stern "24". After the fix, pure-numeric single-token variants are excluded from
        // PageText evidence so the result must not be Linked.
        var machines = new[] { MakeMachine("GrEkZ-ML13O", "24", "stern") };
        var linker = await BuildLinkerWithResolverAsync(
            machines,
            pageText: "Coil voltage: 24 VDC. Check bar door alignment. See Fig. 3.");

        // ServiceBulletinPage maps to manufacturer hint "stern" — exactly the live scenario.
        var raw = MakeRaw("doc-f2aa7aa77a787783",
            "http://s4.american-pinball.com/img/support/2019-7/Bar-Door-Check.pdf",
            gameSlug: "",
            manufacturerKey: "stern",
            sourceType: SourceType.ServiceBulletinPage,
            localPath: "docs/Bar-Door-Check.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.NotEqual(LinkStatus.Linked, result.FinalStatus);
        Assert.Empty(result.LinkedMachineIds);
    }

    [Fact]
    public async Task PageTier_PureNumericTitle_StillLinksViaFilename()
    {
        // Numeric variants are only excluded from page-text evidence; a filename
        // that literally contains "24" (e.g. "24Manual.pdf") is an intentional
        // reference and must still link via the filename tier.
        var machines = new[] { MakeMachine("GrEkZ-ML13O", "24", "stern") };
        var linker = await BuildLinkerWithResolverAsync(machines);

        var raw = MakeRaw("doc-24-manual",
            "https://sternpinball.com/wp-content/uploads/2018/11/24Manual.pdf",
            gameSlug: "",
            manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GrEkZ-ML13O"], result.LinkedMachineIds);
        Assert.Equal("filename_resolver", result.ResolutionStrategy);
    }

    [Fact]
    public async Task PageTier_AlphaTitleMachine_StillLinksFromPageText()
    {
        // Non-numeric machines are unaffected: "Godzilla" appearing in page text
        // must still resolve the Godzilla machine via the page tier.
        var machines = new[]
        {
            MakeMachine("GweeP-M1", "Godzilla", "stern"),
        };
        var linker = await BuildLinkerWithResolverAsync(
            machines, pageText: "This Godzilla pinball machine service manual covers all models.");

        var raw = MakeRaw("doc-gz-page",
            "https://sternpinball.com/some-opaque-bulletin.pdf",
            gameSlug: "",
            manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage,
            localPath: "docs/some-opaque-bulletin.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GweeP-M1"], result.LinkedMachineIds);
        Assert.Equal("page_1_resolver", result.ResolutionStrategy);
    }

    // ── Task 7: ambiguity becomes needs_review, never a silent drop ────────────

    [Fact]
    public async Task Ambiguous_WritesNeedsReview_WithCandidates()
    {
        // Two same-manufacturer machines, different GroupIds → not an edition
        // family → the resolver reports Ambiguous. ADR-0054 §5: ambiguity is never
        // guessed — it must surface as needs_review for the admin queue, not an
        // honest-looking NotInCatalog that hides a real decision.
        var machines = new[]
        {
            MakeMachine("Stern-A", "Mystery Machine", "stern", groupId: "GrpA"),
            MakeMachine("Stern-B", "Mystery Machine", "stern", groupId: "GrpB"),
        };
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var linker = await BuildLinkerWithResolverAsync(machines, rawRepo: rawRepo);

        var raw = MakeRaw("doc-amb", "https://sternpinball.com/mystery-machine.pdf",
            gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NeedsReview, result.FinalStatus);
        Assert.Empty(result.LinkedMachineIds);

        // The review block must actually be WRITTEN, with both candidates — the test
        // name says "WithCandidates", so pin the persisted content, not just the status.
        await rawRepo.Received(1).UpdateLinkStatusAsync(
            Arg.Is("doc-amb"), Arg.Is(LinkStatus.NeedsReview),
            Arg.Is<string?>(s => s == null),
            Arg.Is<string?>(s => s != null && s.Contains("Ambiguous")),
            Arg.Is<string?>(s => s == null),
            Arg.Any<CancellationToken>(),
            Arg.Is<LinkReviewInfo?>(r =>
                r != null
                && r.Candidates.Count == 2
                && r.Candidates.Any(c => c.MachineId == "Stern-A")
                && r.Candidates.Any(c => c.MachineId == "Stern-B")));
    }

    [Fact]
    public async Task Tier1Ambiguity_SurfacesAsNeedsReview_WithTier1Candidates()
    {
        // A Tier-1 (ProvenanceSlug) ambiguity concerns machines X/Y. The filename
        // carries only a single-token trailing-qualifier slug ("pinball" — the
        // 172-document guard), which the resolver refuses to match, so no filename
        // decision exists (the legacy index that once tied P/Q on it was retired in
        // Task 8). The genuine Tier-1 ambiguity must surface as needs_review with
        // TIER 1's candidates — and only those; P/Q must not appear.
        var machines = new[]
        {
            MakeMachine("Stern-X", "Mystery Alpha", "stern", groupId: "GrpX",
                slugs: new Dictionary<string, string> { ["stern"] = "mystery" }),
            MakeMachine("Stern-Y", "Mystery Beta", "stern", groupId: "GrpY",
                slugs: new Dictionary<string, string> { ["stern"] = "mystery" }),
            MakeMachine("Stern-P", "Pinball Prime", "stern", groupId: "GrpP",
                slugs: new Dictionary<string, string> { ["stern"] = "pinball" }),
            MakeMachine("Stern-Q", "Pinball Ultra", "stern", groupId: "GrpQ",
                slugs: new Dictionary<string, string> { ["stern"] = "pinball" }),
        };
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var linker = await BuildLinkerWithResolverAsync(machines, rawRepo: rawRepo);

        var raw = MakeRaw("doc-cross", "https://sternpinball.com/service-pinball.pdf",
            gameSlug: "mystery", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NeedsReview, result.FinalStatus);
        await rawRepo.Received(1).UpdateLinkStatusAsync(
            Arg.Is("doc-cross"), Arg.Is(LinkStatus.NeedsReview),
            Arg.Is<string?>(s => s == null),
            Arg.Any<string?>(),
            Arg.Is<string?>(s => s == null),
            Arg.Any<CancellationToken>(),
            Arg.Is<LinkReviewInfo?>(r =>
                r != null
                && r.Candidates.Count == 2
                && r.Candidates.Any(c => c.MachineId == "Stern-X")
                && r.Candidates.Any(c => c.MachineId == "Stern-Y")));
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
        string? pageText = null,
        IRawDocumentRepository? rawRepo = null,
        CancellationToken ct = default)
    {
        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(aliases ?? []);
        return BuildLinkerAsync(machines, aliasLoader, pageText, rawRepo, ct);
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
        SourceType sourceType = SourceType.ManualsPage,
        string? localPath = null)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = docType,
            File = localPath is null
                ? null
                : new DownloadedFileInfo
                {
                    LocalPath = localPath,
                    Filename = Path.GetFileName(localPath),
                },
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
        IMachineAliasLoader aliasLoader,
        string? pageText = null,
        IRawDocumentRepository? rawRepo = null,
        CancellationToken ct = default)
    {
        // Caller-supplied rawRepo lets a test verify the persisted write (e.g. the
        // needs_review LinkReviewInfo); default substitute otherwise.
        rawRepo ??= Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());

        var machineList = machines.ToList();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        // Page tiers run only when extractor + blob store are wired and the raw
        // record carries a File.LocalPath (see LinkAsync's Tier 3-4 guard).
        IDocumentPreviewExtractor? previewExtractor = null;
        IDocumentBlobStore? blobStore = null;
        if (pageText is not null)
        {
            previewExtractor = Substitute.For<IDocumentPreviewExtractor>();
            previewExtractor.ExtractPreviewAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedPreview(
                    ExtractionStatus.Success,
                    [new ExtractedPage(1, pageText)], Error: null));

            blobStore = Substitute.For<IDocumentBlobStore>();
            blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1024L);
            blobStore.TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream());
        }

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            previewExtractor: previewExtractor,
            NullLogger<DocumentLinker>.Instance,
            blobStore: blobStore,
            aliasLoader: aliasLoader);

        await linker.InitializeAsync(ct);
        return linker;
    }
}
