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
    // IMachineAliasLoader whose LoadAsync returns an empty alias list.
    private static Task<DocumentLinker> BuildLinkerWithResolverAsync(
        IEnumerable<Machine> machines,
        CancellationToken ct = default)
    {
        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());
        return BuildLinkerAsync(machines, aliasLoader, ct);
    }

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
