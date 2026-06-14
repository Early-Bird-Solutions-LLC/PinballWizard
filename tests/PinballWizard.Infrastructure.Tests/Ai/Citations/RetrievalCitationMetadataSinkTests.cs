using PinballWizard.Application.Ai.Citations;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Citations;

// Unit tests for RetrievalCitationMetadataSink — the request-scoped
// side channel that carries Score + LastScrapedUtc from SearchCorpusTool
// to ToolTraceCitationExtractor (fix/citation-metadata-channel, ADR-0035).
public sealed class RetrievalCitationMetadataSinkTests
{
    [Fact]
    public void TryGet_UnknownUrl_ReturnsFalse()
    {
        var sink = new RetrievalCitationMetadataSink();

        var found = sink.TryGet("https://example/missing.pdf", out var meta);

        Assert.False(found);
        Assert.Null(meta);
    }

    [Fact]
    public void Record_ThenTryGet_ReturnsMetadata()
    {
        var sink = new RetrievalCitationMetadataSink();
        var expected = new RetrievalCitationMetadata(
            LastScrapedUtc: new DateTimeOffset(2026, 3, 22, 14, 30, 0, TimeSpan.Zero),
            RelevanceScore: 0.85);

        sink.Record("https://example/manual.pdf", expected);
        var found = sink.TryGet("https://example/manual.pdf", out var meta);

        Assert.True(found);
        Assert.Equal(expected, meta);
    }

    [Fact]
    public void Record_SameUrl_FirstWriteWins()
    {
        // First-write-wins: subsequent writes for the same URL are silently
        // ignored. The citation dedup in ToolTraceCitationExtractor collapses
        // multiple chunks per document to the first (highest-ranked) hit, so
        // the sink must mirror that winner-takes-all behaviour.
        var sink = new RetrievalCitationMetadataSink();
        var first = new RetrievalCitationMetadata(
            LastScrapedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            RelevanceScore: 0.90);
        var second = new RetrievalCitationMetadata(
            LastScrapedUtc: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            RelevanceScore: 0.50);

        sink.Record("https://example/doc.pdf", first);
        sink.Record("https://example/doc.pdf", second); // must be ignored

        sink.TryGet("https://example/doc.pdf", out var meta);
        Assert.Equal(first, meta);
    }

    [Fact]
    public void TryGet_CaseInsensitiveKey()
    {
        // Keys are OrdinalIgnoreCase — the URL case coming from the retriever
        // and the URL case on the SearchCorpusHit may differ across platforms.
        var sink = new RetrievalCitationMetadataSink();
        var metadata = new RetrievalCitationMetadata(
            LastScrapedUtc: null,
            RelevanceScore: 0.70);

        sink.Record("https://EXAMPLE/Manual.pdf", metadata);
        var found = sink.TryGet("https://example/manual.pdf", out var meta);

        Assert.True(found);
        Assert.Equal(metadata, meta);
    }

    [Fact]
    public void Record_TwoDistinctUrls_BothRetrievable()
    {
        var sink = new RetrievalCitationMetadataSink();
        var metaA = new RetrievalCitationMetadata(LastScrapedUtc: null, RelevanceScore: 0.9);
        var metaB = new RetrievalCitationMetadata(LastScrapedUtc: null, RelevanceScore: 0.7);

        sink.Record("https://example/doc_a.pdf", metaA);
        sink.Record("https://example/doc_b.pdf", metaB);

        sink.TryGet("https://example/doc_a.pdf", out var a);
        sink.TryGet("https://example/doc_b.pdf", out var b);
        Assert.Equal(metaA, a);
        Assert.Equal(metaB, b);
    }
}
