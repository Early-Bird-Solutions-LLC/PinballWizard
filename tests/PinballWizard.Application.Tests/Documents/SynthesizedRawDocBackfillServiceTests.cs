using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Documents;

// Behavior tests for the synthesized raw-doc backfill. The service heals dead
// citations: a synthesized document present in the RAG index but missing its
// scraped_documents_raw row resolves to "Document not found" at /documents/{id}.
// The service writes the missing row (reconstructed from indexed metadata) and
// leaves already-present rows untouched. Hand fakes (not NSubstitute) so the
// async-enumerable source and the recording repository read cleanly.
public sealed class SynthesizedRawDocBackfillServiceTests
{
    private static readonly DateTimeOffset SynthAt = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_MissingDoc_WritesRawDocAndMarksPlatformGeneric()
    {
        var source = new SourceFake(Kineticist("kineticist_godzilla_GweeP-Ml9pZ", "Godzilla (Premium)", title: "How to play Godzilla"));
        var repo = new RawDocRepositoryFake(); // nothing pre-existing

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        var result = await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.SkippedExisting);
        Assert.Equal(0, result.Failed);

        var written = Assert.Single(repo.Upserts);
        Assert.Equal("kineticist_godzilla_GweeP-Ml9pZ", written.DocumentId);
        Assert.Equal("How to play Godzilla", written.Source.LinkText);
        Assert.Equal(SourceType.SynthesizedArticle, written.Source.SourceType);
        // Link status must be set to PlatformGeneric with the backfill-distinct strategy.
        var link = Assert.Single(repo.LinkUpdates);
        Assert.Equal("kineticist_godzilla_GweeP-Ml9pZ", link.DocumentId);
        Assert.Equal(LinkStatus.PlatformGeneric, link.Status);
        Assert.Equal("synthesized-backfill", link.ResolutionStrategy);
    }

    [Fact]
    public async Task RunAsync_ExistingDoc_SkipsWithoutWriting()
    {
        var source = new SourceFake(Kineticist("kineticist_x_M1", "X"));
        var repo = new RawDocRepositoryFake();
        repo.SeedExisting("kineticist_x_M1"); // live sync already wrote this row

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        var result = await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.SkippedExisting);
        Assert.Empty(repo.Upserts);        // never overwrites the live-sync row
        Assert.Empty(repo.LinkUpdates);
    }

    [Fact]
    public async Task RunAsync_DryRun_CountsButDoesNotWrite()
    {
        var source = new SourceFake(Kineticist("kineticist_x_M1", "X"));
        var repo = new RawDocRepositoryFake();

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        var result = await svc.RunAsync(dryRun: true, CancellationToken.None);

        Assert.Equal(1, result.Written); // reported as would-write
        Assert.True(result.DryRun);
        Assert.Empty(repo.Upserts);      // but nothing actually written
        Assert.Empty(repo.LinkUpdates);
    }

    [Fact]
    public async Task RunAsync_WriteThrows_CountsFailedAndContinues()
    {
        var source = new SourceFake(
            Kineticist("kineticist_boom_M1", "Boom"),
            Kineticist("kineticist_ok_M2", "Ok"));
        var repo = new RawDocRepositoryFake { ThrowOnUpsertFor = "kineticist_boom_M1" };

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        var result = await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Failed);                       // the throwing doc is metered
        Assert.Equal(1, result.Written);                      // the run continues to the next doc
        Assert.Equal("kineticist_ok_M2", Assert.Single(repo.Upserts).DocumentId);
    }

    [Fact]
    public async Task RunAsync_TiltForumsDoc_StripsRulesheetSuffixFromTitle()
    {
        // Tilt Forums content leads with "# {GameTitle} — Rulesheet"; the recovered
        // title must be the bare game title so it matches the live-sync value.
        var source = new SourceFake(new IndexedSynthesizedDocument(
            DocumentId: "tiltforums_7210_GweeP-Ml9pZ",
            MachineId: "GweeP-Ml9pZ",
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern",
            DocumentUrl: "https://tiltforums.com/t/godzilla/7210",
            DocumentTypeName: "Rulesheet",
            LastScrapedUtc: SynthAt,
            Title: "Godzilla — Rulesheet"));
        var repo = new RawDocRepositoryFake();

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal("Godzilla", Assert.Single(repo.Upserts).Source.LinkText);
    }

    [Fact]
    public async Task RunAsync_TwipDoc_HasNoGameAndUsesManufacturerOverride()
    {
        // TWIP newsletters carry the synthetic "pinball_news" machine id — no game,
        // manufacturer forced to "Kineticist" by the descriptor override.
        var source = new SourceFake(new IndexedSynthesizedDocument(
            DocumentId: "twip_this-week-2026-05-01",
            MachineId: "pinball_news",
            MachineTitle: "Pinball News",
            Manufacturer: "",
            DocumentUrl: "https://kineticist.com/twip/2026-05-01",
            DocumentTypeName: "NewsDigest",
            LastScrapedUtc: SynthAt,
            Title: "This Week in Pinball — May 1"));
        var repo = new RawDocRepositoryFake();

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        await svc.RunAsync(dryRun: false, CancellationToken.None);

        var written = Assert.Single(repo.Upserts);
        Assert.Null(written.Game);                                   // no bogus game block
        Assert.Equal("Kineticist", written.Manufacturer);           // descriptor override
        Assert.Equal(DocumentType.NewsDigest, written.Classification!.DocumentType);
    }

    [Fact]
    public async Task RunAsync_MachineLinkedDoc_PopulatesGameFromMachineTitle()
    {
        var source = new SourceFake(Kineticist("kineticist_godzilla_GweeP-Ml9pZ", "Godzilla (Premium)"));
        var repo = new RawDocRepositoryFake();

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        await svc.RunAsync(dryRun: false, CancellationToken.None);

        var written = Assert.Single(repo.Upserts);
        Assert.NotNull(written.Game);
        Assert.Equal("Godzilla (Premium)", written.Game!.Title);
    }

    [Fact]
    public async Task RunAsync_TitleMissing_FallsBackToMachineTitle()
    {
        var source = new SourceFake(Kineticist("kineticist_x_M1", "Attack from Mars", title: null));
        var repo = new RawDocRepositoryFake();

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal("Attack from Mars", Assert.Single(repo.Upserts).Source.LinkText);
    }

    [Fact]
    public async Task RunAsync_UnmappedDocumentId_SkippedWithoutWriting()
    {
        // Defensive guard: a doc-id the source yielded but no descriptor recognizes
        // (e.g. a scraped "doc_" id) is counted as skipped_unmapped, never written.
        var source = new SourceFake(new IndexedSynthesizedDocument(
            DocumentId: "doc_58c56c2ec9dfb4df",
            MachineId: "GweeP-Ml9pZ",
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern",
            DocumentUrl: "https://sternpinball.com/x.pdf",
            DocumentTypeName: "Manual",
            LastScrapedUtc: SynthAt,
            Title: "X"));
        var repo = new RawDocRepositoryFake();

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        var result = await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.SkippedUnmapped);
        Assert.Equal(0, result.Written);
        Assert.Empty(repo.Upserts);
    }

    [Fact]
    public async Task RunAsync_LinkStatusUpdateFails_StillCountsWritten()
    {
        // The row was written, so the citation resolves — a failure on the SUBSEQUENT
        // link-status update must not be counted as a backfill failure or roll back the
        // written count (the doc is no longer a dead citation).
        var source = new SourceFake(Kineticist("kineticist_x_M1", "X"));
        var repo = new RawDocRepositoryFake { ThrowOnLinkUpdateFor = "kineticist_x_M1" };

        var svc = new SynthesizedRawDocBackfillService(source, repo, NullLogger<SynthesizedRawDocBackfillService>.Instance);
        var result = await svc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.Failed);
        Assert.Single(repo.Upserts); // the row IS written
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static IndexedSynthesizedDocument Kineticist(string id, string machineTitle, string? title = "T") =>
        new(
            DocumentId: id,
            MachineId: "GweeP-Ml9pZ",
            MachineTitle: machineTitle,
            Manufacturer: "Stern",
            DocumentUrl: "https://kineticist.com/tutorials/x",
            DocumentTypeName: "Rulesheet",
            LastScrapedUtc: SynthAt,
            Title: title);

    // ── fakes ────────────────────────────────────────────────────────

    private sealed class SourceFake(params IndexedSynthesizedDocument[] docs) : IIndexedSynthesizedDocumentSource
    {
        public async IAsyncEnumerable<IndexedSynthesizedDocument> StreamSynthesizedDocumentsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var d in docs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return d;
                await Task.Yield();
            }
        }
    }

    private sealed class RawDocRepositoryFake : IRawDocumentRepository
    {
        private readonly HashSet<string> _existing = new(StringComparer.Ordinal);
        public List<DocumentRecord> Upserts { get; } = [];
        public List<(string DocumentId, LinkStatus Status, string? ResolutionStrategy)> LinkUpdates { get; } = [];
        public string? ThrowOnUpsertFor { get; set; }
        public string? ThrowOnLinkUpdateFor { get; set; }

        public void SeedExisting(string documentId) => _existing.Add(documentId);

        public Task<RawDocumentUpsertResult> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken)
        {
            if (record.DocumentId == ThrowOnUpsertFor)
            {
                throw new InvalidOperationException("simulated Cosmos write failure");
            }
            Upserts.Add(record);
            var raw = new RawDocumentRecord
            {
                DocumentId = record.DocumentId,
                DocumentUrl = record.Source.FileUrl,
                DocumentType = record.Classification?.DocumentType ?? DocumentType.Other,
                Source = record.Source,
                Timeline = record.Timeline,
            };
            return Task.FromResult(new RawDocumentUpsertResult(raw, UpsertOutcome.Created));
        }

        public Task UpdateLinkStatusAsync(string documentId, LinkStatus status, string? resolutionStrategy, string? failureReason, string? overrideId, CancellationToken cancellationToken, LinkReviewInfo? linkReview = null)
        {
            if (documentId == ThrowOnLinkUpdateFor)
            {
                throw new InvalidOperationException("simulated Cosmos link-status update failure");
            }
            LinkUpdates.Add((documentId, status, resolutionStrategy));
            return Task.CompletedTask;
        }

        public Task<RawDocumentRecord?> GetAsync(string documentId, CancellationToken cancellationToken)
        {
            if (!_existing.Contains(documentId))
            {
                return Task.FromResult<RawDocumentRecord?>(null);
            }
            var raw = new RawDocumentRecord
            {
                DocumentId = documentId,
                DocumentUrl = "https://example.test/x",
                DocumentType = DocumentType.Rulesheet,
                Source = new SourceInfo { DiscoveryUrl = "https://example.test/x", DiscoveryContext = "x", FileUrl = "https://example.test/x", LinkText = "x", ActionType = ActionType.ExternalLink, SourceType = SourceType.SynthesizedArticle },
                Timeline = new TimelineInfo { FirstDiscoveredAt = SynthAt.UtcDateTime, LastDownloadedAt = SynthAt.UtcDateTime },
            };
            return Task.FromResult<RawDocumentRecord?>(raw);
        }

        // Unused by the backfill service.
        public IAsyncEnumerable<RawDocumentRecord> StreamByStatusAsync(IReadOnlyCollection<LinkStatus> statuses, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RawDocumentRecord> StreamAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateFileAsync(string documentId, DownloadedFileInfo file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkDownloadSkipAsync(string documentId, DownloadSkipInfo skip, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DenormalizeContentHashAsync(string documentId, string sha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RawDocumentRecord> StreamBySourcePatternAsync(string sourcePattern, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RawDocumentRecord> StreamByRunIdAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateDocumentTypeAsync(string documentId, DocumentType newType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<DocumentListItem> StreamDocumentsAsync(string? game, string? manufacturer, string? type, bool includeAdminFields, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DocumentDetailRecord?> GetDocumentDetailAsync(string documentId, bool includeAdminFields, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
