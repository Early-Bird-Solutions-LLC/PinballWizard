using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence;

// Pins the write path for LinkStatus.NeedsReview — the linker (ADR-0054 Wave 2)
// must be able to persist the review block (candidates, created_at) so the admin
// queue can surface it. Before this task, UpdateLinkStatusAsync accepted no review
// argument and the block could never be written.
public sealed class RawDocumentLinkReviewTests
{
    [Fact]
    public async Task UpdateLinkStatusAsync_WithLinkReview_PersistsCandidates()
    {
        var repo = await NewRepositoryWithDocumentAsync("doc-1");

        await repo.UpdateLinkStatusAsync(
            "doc-1", LinkStatus.NeedsReview, "filename", failureReason: null, overrideId: null,
            CancellationToken.None,
            new LinkReviewInfo
            {
                CreatedAt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
                Candidates =
                [
                    new LinkReviewCandidate
                    {
                        MachineId = "GweeP-MW95j", MachineTitle = "Godzilla (Pro)",
                        EvidenceKind = "Filename", MatchedVariant = "godzilla",
                    },
                ],
            });

        var stored = await repo.GetAsync("doc-1", CancellationToken.None);
        Assert.Equal(LinkStatus.NeedsReview, stored!.LinkStatus);
        Assert.Single(stored.LinkReview!.Candidates);
        Assert.Equal("GweeP-MW95j", stored.LinkReview.Candidates[0].MachineId);
        Assert.Equal("Godzilla (Pro)", stored.LinkReview.Candidates[0].MachineTitle);
        Assert.Equal("Filename", stored.LinkReview.Candidates[0].EvidenceKind);
        Assert.Equal("godzilla", stored.LinkReview.Candidates[0].MatchedVariant);
        Assert.Equal(new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc), stored.LinkReview.CreatedAt);
    }

    // Invariant #17: any status other than NeedsReview must clear an existing
    // review block so a resolved document cannot keep a stale candidate list.
    // Changing the ternary's false branch (dropping the null assignment) would
    // pass the happy-path test above while silently breaking this invariant.
    [Theory]
    [InlineData(LinkStatus.Linked)]
    [InlineData(LinkStatus.Failed)]
    [InlineData(LinkStatus.NotInCatalog)]
    [InlineData(LinkStatus.Pending)]
    public async Task UpdateLinkStatusAsync_NonNeedsReviewStatus_ClearsExistingLinkReview(LinkStatus status)
    {
        var repo = await NewRepositoryWithDocumentAsync("doc-2");

        // First write: stamp a review block on the document.
        await repo.UpdateLinkStatusAsync(
            "doc-2", LinkStatus.NeedsReview, resolutionStrategy: null, failureReason: null, overrideId: null,
            CancellationToken.None,
            new LinkReviewInfo
            {
                CreatedAt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
                Candidates = [new LinkReviewCandidate { MachineId = "stale-id" }],
            });

        // Second write: resolve to any other status — review block must be cleared.
        await repo.UpdateLinkStatusAsync(
            "doc-2", status, resolutionStrategy: null, failureReason: null, overrideId: null,
            CancellationToken.None);

        var stored = await repo.GetAsync("doc-2", CancellationToken.None);
        Assert.Equal(status, stored!.LinkStatus);
        Assert.Null(stored.LinkReview);
    }

    // ────────────────────────────────────────────────────────────────
    // Helper — creates a CosmosRawDocumentRepository backed by a
    // NSubstitute Container seeded with a single pending document.
    // ReadItemAsync returns the same cosmosRecord reference, so when
    // UpdateLinkStatusAsync modifies it in place the subsequent GetAsync
    // sees the updated state.
    // ────────────────────────────────────────────────────────────────

    private static Task<CosmosRawDocumentRepository> NewRepositoryWithDocumentAsync(string docId)
    {
        var container = Substitute.For<Container>();

        var cosmosRecord = new RawDocumentCosmosRecord
        {
            Id = docId,
            PartitionKey = docId,
            DocumentUrl = "https://example.com/file.pdf",
            DocumentType = "Manual",
            LinkStatus = "pending",
            Source = new RawSourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Timeline = new RawTimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        // Both the read-for-update (inside UpdateLinkStatusAsync) and the
        // read-for-return (inside GetAsync) use the same cosmosRecord reference.
        // After UpdateLinkStatusAsync mutates it, GetAsync picks up the changes.
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
            container,
            NullLogger<CosmosRawDocumentRepository>.Instance));
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
