using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence;

// Pins the write path for MarkSupersededAsync — the operation that marks a
// wp.sternpinball.com duplicate as superseded by its canonical sternpinball.com
// counterpart. This is a soft-supersede: the row is preserved (provenance is
// sacred per the LOCKED invariant) but excluded from all processing pipelines
// by the by-construction allow-list property tested separately.
//
// Key contract:
//   1. Sets LinkStatus = Superseded and SupersededBy = <canonical doc id>.
//   2. Leaves provenance fields (Source, Timeline, File, CrossReferences, Game,
//      Classification) exactly as they were — only linker-owned fields change.
//   3. Throws InvalidOperationException for a missing document (same pattern as
//      MarkDownloadSkipAsync and UpdateLinkStatusAsync).
public sealed class RawDocumentMarkSupersededTests
{
    [Fact]
    public async Task MarkSupersededAsync_SetsStatusAndSupersededBy()
    {
        var repo = await NewRepositoryWithDocumentAsync("doc-superseded-1");

        await repo.MarkSupersededAsync(
            "doc-superseded-1",
            supersededByDocumentId: "doc_canonical_abc",
            reason: "host_alias_duplicate",
            CancellationToken.None);

        var stored = await repo.GetAsync("doc-superseded-1", CancellationToken.None);
        Assert.Equal(LinkStatus.Superseded, stored!.LinkStatus);
        Assert.Equal("doc_canonical_abc", stored.SupersededBy);
    }

    [Fact]
    public async Task MarkSupersededAsync_LeavesProvenanceFieldsUntouched()
    {
        // Provenance fields (Source, Timeline, File, CrossReferences, Game,
        // Classification) must survive the supersede write unchanged.
        // This is the same discipline as MarkDownloadSkipAsync — only the
        // fields this operation logically owns (LinkStatus, SupersededBy, linker
        // reason/strategy fields) may change.
        var repo = await NewRepositoryWithDocumentAsync("doc-superseded-2",
            sourceDiscoveryUrl: "https://wp.sternpinball.com/game/stranger-things/",
            sourceFileUrl: "https://wp.sternpinball.com/wp-content/uploads/stranger_things_manual.pdf");

        await repo.MarkSupersededAsync(
            "doc-superseded-2",
            supersededByDocumentId: "doc_canonical_xyz",
            reason: "host_alias_duplicate",
            CancellationToken.None);

        var stored = await repo.GetAsync("doc-superseded-2", CancellationToken.None);

        // Status + SupersededBy written
        Assert.Equal(LinkStatus.Superseded, stored!.LinkStatus);
        Assert.Equal("doc_canonical_xyz", stored.SupersededBy);

        // Provenance fields untouched
        Assert.Equal("https://wp.sternpinball.com/game/stranger-things/", stored.Source.DiscoveryUrl);
        Assert.Equal("https://wp.sternpinball.com/wp-content/uploads/stranger_things_manual.pdf", stored.Source.FileUrl);
        Assert.Equal("Test context", stored.Source.DiscoveryContext);

        // Timeline still present (first_discovered_at not wiped)
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.Timeline.FirstDiscoveredAt);

        // Classification, Game and CrossReferences survive too. These are the fields a
        // partial-model write would silently drop, so asserting them is the whole point
        // — the comment above previously named them without any assertion behind it.
        Assert.NotNull(stored.Classification);
        Assert.Equal(DocumentType.Manual, stored.Classification!.DocumentType);

        Assert.NotNull(stored.Game);
        Assert.Equal("Stranger Things", stored.Game!.Title);
        Assert.Equal("stranger-things", stored.Game.Slug);
        Assert.Equal("Premium", stored.Game.Edition);

        var crossRef = Assert.Single(stored.CrossReferences);
        Assert.Equal("https://sternpinball.com/manuals/", crossRef.AlsoFoundAt);
        Assert.Equal("manuals index", crossRef.DiscoveryContext);
    }

    [Fact]
    public async Task MarkSupersededAsync_DocumentNotFound_Throws()
    {
        var container = Substitute.For<Container>();

        // ReadItemAsync returns CosmosException with 404 status code — standard
        // missing-document response pattern used by GetByIdAsync in CosmosRepository.
        container
            .ReadItemAsync<RawDocumentCosmosRecord>(
                Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(new CosmosException("Not Found", HttpStatusCode.NotFound, 0, string.Empty, 0));

        var repo = new CosmosRawDocumentRepository(
            container, NullLogger<CosmosRawDocumentRepository>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.MarkSupersededAsync(
                "doc-missing",
                supersededByDocumentId: "doc_canonical_xyz",
                reason: "host_alias_duplicate",
                CancellationToken.None));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helper
    // ────────────────────────────────────────────────────────────────────────

    private static Task<CosmosRawDocumentRepository> NewRepositoryWithDocumentAsync(
        string docId,
        string sourceDiscoveryUrl = "https://example.com/discover",
        string sourceFileUrl = "https://example.com/file.pdf")
    {
        var container = Substitute.For<Container>();

        var cosmosRecord = new RawDocumentCosmosRecord
        {
            Id = docId,
            PartitionKey = docId,
            DocumentUrl = sourceFileUrl,
            DocumentType = "Manual",
            LinkStatus = "needs_review",
            Source = new RawSourceInfo
            {
                DiscoveryUrl = sourceDiscoveryUrl,
                DiscoveryContext = "Test context",
                FileUrl = sourceFileUrl,
            },
            Timeline = new RawTimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            // Populated so the provenance test can assert these survive rather than
            // merely assert on the two fields that happened to be set. A read-modify-write
            // preserves them structurally, but the point of the test is to catch a future
            // refactor to a partial-model write — which would drop exactly these.
            Classification = new RawClassificationInfo
            {
                DocumentType = "Manual",
                FileFormat = "pdf",
            },
            Game = new RawGameInfo
            {
                Title = "Stranger Things",
                Slug = "stranger-things",
                Edition = "Premium",
                GamePageUrl = "https://sternpinball.com/game/stranger-things/",
            },
            CrossReferences =
            [
                new RawCrossRef
                {
                    AlsoFoundAt = "https://sternpinball.com/manuals/",
                    DiscoveryContext = "manuals index",
                    LinkText = "Stranger Things Manual",
                    DiscoveredAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
                },
            ],
        };

        container
            .ReadItemAsync<RawDocumentCosmosRecord>(
                docId, new PartitionKey(docId),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeItemResponse(cosmosRecord, HttpStatusCode.OK));

        container
            .UpsertItemAsync(Arg.Any<RawDocumentCosmosRecord>(),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        return Task.FromResult(new CosmosRawDocumentRepository(
            container, NullLogger<CosmosRawDocumentRepository>.Instance));
    }

    private static ItemResponse<TItem> MakeItemResponse<TItem>(TItem? resource, HttpStatusCode statusCode)
        => new FakeItemResponse<TItem>(resource, statusCode);

    private sealed class FakeItemResponse<TItem> : ItemResponse<TItem>
    {
        private readonly TItem? _resource;
        private readonly HttpStatusCode _statusCode;

        public FakeItemResponse(TItem? resource, HttpStatusCode statusCode)
        {
            _resource = resource;
            _statusCode = statusCode;
        }

        public override TItem Resource => _resource!;
        public override HttpStatusCode StatusCode => _statusCode;
        public override double RequestCharge => 0;
        public override Headers Headers => new();
        public override CosmosDiagnostics Diagnostics => null!;
        public override string? ActivityId => null;
        public override string? ETag => null;
    }
}
