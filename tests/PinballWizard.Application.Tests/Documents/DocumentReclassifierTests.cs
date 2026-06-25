using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Documents;

/// <summary>
/// Behavior tests for <see cref="DocumentReclassifier"/>.
///
/// Verifies that:
///   - a stored Other-typed document whose Source signals "rules" is
///     reclassified to Rulesheet and written back;
///   - a document already correctly classified is left untouched (idempotent);
///   - all provenance fields are preserved on write-back;
///   - per-document failures are caught and counted without aborting the run
///     (invariant #17 degrade-visibly).
/// </summary>
public sealed class DocumentReclassifierTests
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();

    // ── Core reclassification behavior ───────────────────────────────────

    [Fact]
    public async Task OtherTypedRulesDoc_IsReclassifiedToRulesheet_AndWrittenBack()
    {
        // A document stored as Other whose link text is "Rules" and whose URL
        // contains "rules" — the PR #507 Rulesheet branch should now catch it.
        var doc = MakeRaw("doc_rules_1",
            fileUrl: "https://chicago-gaming.com/docs/afm-rules.pdf",
            linkText: "Rules",
            discoveryContext: "Game Page → Specs tab",
            storedType: DocumentType.Other);

        StubStream(doc);

        var result = await MakeSvc().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Reclassified);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Failed);

        // Transition recorded correctly.
        Assert.Single(result.Transitions);
        Assert.Equal("Other", result.Transitions[0].OldType);
        Assert.Equal("Rulesheet", result.Transitions[0].NewType);
        Assert.Equal("doc_rules_1", result.Transitions[0].DocumentId);

        // UpdateDocumentTypeAsync called with Rulesheet.
        await _repo.Received(1).UpdateDocumentTypeAsync(
            "doc_rules_1",
            DocumentType.Rulesheet,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CorrectlyTypedManual_IsUnchanged_NoWriteIssued()
    {
        // A document already classified as Manual whose link text is "Manual"
        // — re-running classification must return the same type, so no write.
        var doc = MakeRaw("doc_manual_1",
            fileUrl: "https://sternpinball.com/wp-content/uploads/godzilla-pro-manual.pdf",
            linkText: "Godzilla Pro Manual",
            discoveryContext: "Manuals page",
            storedType: DocumentType.Manual);

        StubStream(doc);

        var result = await MakeSvc().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(0, result.Reclassified);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Transitions);

        // No write issued.
        await _repo.DidNotReceive().UpdateDocumentTypeAsync(
            Arg.Any<string>(), Arg.Any<DocumentType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyRulesheet_IsIdempotent_NoWriteIssued()
    {
        // A document already stored as Rulesheet — second run must be a no-op.
        var doc = MakeRaw("doc_rs_1",
            fileUrl: "https://spookypinball.com/rules/spooky-rules.pdf",
            linkText: "Rules",
            discoveryContext: "Game Page",
            storedType: DocumentType.Rulesheet);

        StubStream(doc);

        var result = await MakeSvc().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(0, result.Reclassified);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, result.Failed);

        await _repo.DidNotReceive().UpdateDocumentTypeAsync(
            Arg.Any<string>(), Arg.Any<DocumentType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvenancePreserved_OnlyDocumentTypePassedToUpdate()
    {
        // Write-back must call UpdateDocumentTypeAsync — not UpsertRawAsync — so
        // provenance fields (Source, Timeline, CrossReferences, linker state)
        // are untouched. Verify UpsertRawAsync is never called.
        var doc = MakeRaw("doc_rules_2",
            fileUrl: "https://american-pinball.com/downloads/ap-rules.pdf",
            linkText: "Game Rules",
            discoveryContext: "Game Page",
            storedType: DocumentType.Other);

        StubStream(doc);

        await MakeSvc().RunAsync(CancellationToken.None);

        // Only the targeted update is called.
        await _repo.Received(1).UpdateDocumentTypeAsync(
            "doc_rules_2",
            DocumentType.Rulesheet,
            Arg.Any<CancellationToken>());

        // The full-record upsert (which would overwrite provenance) is never called.
        await _repo.DidNotReceive().UpsertRawAsync(
            Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerDocumentException_IsCaughtAndCounted_RunContinues()
    {
        // First doc: throws; second doc: successful reclassification.
        // The service must not abort on a per-document error (invariant #17).
        var docThrows = MakeRaw("doc_bad_1",
            fileUrl: "https://example.com/rules.pdf",
            linkText: "Rules",
            discoveryContext: "Game Page",
            storedType: DocumentType.Other);

        var docOk = MakeRaw("doc_ok_1",
            fileUrl: "https://spookypinball.com/rules/beetlejuice-rules.pdf",
            linkText: "Rules",
            discoveryContext: "Game Page",
            storedType: DocumentType.Other);

        StubStream(docThrows, docOk);

        // First doc's UpdateDocumentTypeAsync throws; second succeeds.
        _repo.UpdateDocumentTypeAsync("doc_bad_1", Arg.Any<DocumentType>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Cosmos timeout simulation"));

        var result = await MakeSvc().RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.Reclassified);   // doc_ok_1 succeeded
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(1, result.Failed);          // doc_bad_1 caught
    }

    [Fact]
    public async Task MultipleOtherDocs_AllReclassifiedWhenRulesSignalsPresent()
    {
        // Three docs all typed Other with different rules-signal patterns — all
        // should reclassify to Rulesheet.
        var docs = new[]
        {
            MakeRaw("doc_1", "https://site.com/rules.pdf",      "Rules",     "Game Page", DocumentType.Other),
            MakeRaw("doc_2", "https://site.com/rulesheet.pdf",  null,        "Game Page", DocumentType.Other),
            MakeRaw("doc_3", "https://site.com/manual.pdf",     "Rulesheet", "Game Page", DocumentType.Other),
        };

        StubStream(docs);

        var result = await MakeSvc().RunAsync(CancellationToken.None);

        Assert.Equal(3, result.Scanned);
        Assert.Equal(3, result.Reclassified);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Failed);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private DocumentReclassifier MakeSvc() =>
        new(_repo, NullLogger<DocumentReclassifier>.Instance);

    private void StubStream(params RawDocumentRecord[] docs) =>
        _repo.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(ToAsync(docs));

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsync(IEnumerable<RawDocumentRecord> docs)
    {
        foreach (var d in docs) { yield return d; await Task.Yield(); }
    }

    private static RawDocumentRecord MakeRaw(
        string documentId,
        string fileUrl,
        string? linkText,
        string discoveryContext,
        DocumentType storedType) => new()
    {
        DocumentId = documentId,
        DocumentUrl = fileUrl,
        DocumentType = storedType,
        Source = new SourceInfo
        {
            DiscoveryUrl = "https://example.com/game-page/",
            DiscoveryContext = discoveryContext,
            FileUrl = fileUrl,
            LinkText = linkText,
            ScrapedAt = DateTime.UtcNow,
            SourceType = SourceType.GamePage,
        },
        Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
    };
}
