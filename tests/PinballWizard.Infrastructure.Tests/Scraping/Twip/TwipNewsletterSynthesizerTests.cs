using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.Twip;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Twip;

public sealed class TwipNewsletterSynthesizerTests
{
    // ── Arrange helpers ─────────────────────────────────────────────────

    private static HybridChunker NewChunker() =>
        new(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance);

    private static TwipNewsletterSynthesizer NewSynthesizer() =>
        new(NewChunker(), NullLogger<TwipNewsletterSynthesizer>.Instance);

    private static TwipNewsletterArticle SampleArticle(
        string? bodyText = null, DateTimeOffset? publishedAt = null) => new()
    {
        Slug = "this-week-2026-06-20",
        Title = "This Week in Pinball: June 20, 2026",
        Description = "Stern announces new title; JJP updates pricing.",
        CanonicalUrl = "https://twip.kineticist.com/p/this-week-2026-06-20",
        Author = "Colin Alsheimer",
        PublishedAt = publishedAt ?? new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero),
        BodyText = bodyText ?? """
            ## New Releases

            Stern Pinball announced a new title this week with custom artwork from a renowned designer.

            JJP has updated pricing on existing titles effective July 1.

            ## Tournament News

            The IFPA world rankings have been updated with results from the Chicago tournament.
            """,
    };

    private static ChunkRequest SampleRequest() => new(
        MachineId: "pinball_news",
        MachineTitle: "Pinball News",
        Manufacturer: "Kineticist",
        DocumentId: "twip_this-week-2026-06-20",
        DocumentUrl: "https://twip.kineticist.com/p/this-week-2026-06-20",
        DocumentType: DocumentType.NewsDigest,
        LastScrapedUtc: new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero));

    // ── Null guard tests ─────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullChunker_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new TwipNewsletterSynthesizer(null!, NullLogger<TwipNewsletterSynthesizer>.Instance));

    [Fact]
    public void Ctor_NullLogger_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new TwipNewsletterSynthesizer(NewChunker(), null!));

    [Fact]
    public void Synthesize_NullArticle_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(null!, SampleRequest()));

    [Fact]
    public void Synthesize_NullChunkRequest_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(SampleArticle(), null!));

    // ── Happy-path tests ─────────────────────────────────────────────────

    [Fact]
    public void Synthesize_SampleArticle_ReturnsNonEmptyChunks()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0, "TokenCount must be > 0."));
    }

    [Fact]
    public void Synthesize_SampleArticle_LeadContainsAuthorAttribution()
    {
        // Lead line: "Weekly pinball news by Colin Alsheimer, published June 20, 2026."
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Weekly pinball news by Colin Alsheimer", allText, StringComparison.Ordinal);
        Assert.Contains("June 20, 2026", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_SourceUrlInAttributedText()
    {
        // Canonical URL must appear in text (provenance invariant #1).
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains(
            "https://twip.kineticist.com/p/this-week-2026-06-20",
            allText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_DescriptionInAttributedText()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Stern announces new title", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_BodyContentPresent()
    {
        // Body text must survive the synthesis (not stripped by attribution logic).
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("JJP has updated pricing", allText, StringComparison.Ordinal);
        Assert.Contains("IFPA world rankings", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_ChunkIndexStartsAtZeroAndIsStrictlyIncreasing()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());

        Assert.Equal(0, chunks[0].ChunkIndex);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].ChunkIndex > chunks[i - 1].ChunkIndex,
                "ChunkIndex must be strictly increasing.");
        }
    }

    [Fact]
    public void Synthesize_SampleArticle_TokenCountsWithinReasonableEnvelope()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());

        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 1000));
    }

    // ── Graceful-degradation tests ────────────────────────────────────────

    [Fact]
    public void Synthesize_EmptyBodyText_ReturnsEmpty_NoFabrication()
    {
        // Invariant #17: empty body must yield 0 chunks, not placeholder content.
        var article = SampleArticle(bodyText: "");
        var chunks = NewSynthesizer().Synthesize(article, SampleRequest());

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_WhitespaceOnlyBodyText_ReturnsEmpty()
    {
        var article = SampleArticle(bodyText: "   \r\n   \t  ");
        var chunks = NewSynthesizer().Synthesize(article, SampleRequest());

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_ArticleWithoutPublishedAt_StillSynthesizes()
    {
        var article = SampleArticle(publishedAt: null) with { PublishedAt = null };
        var chunks = NewSynthesizer().Synthesize(article, SampleRequest());

        Assert.NotEmpty(chunks);
        // Date clause must be absent but no "()" artifact.
        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.DoesNotContain("()", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_LeadLine_DoesNotContainBareCommaWhenNoDate()
    {
        // "Weekly pinball news by Colin Alsheimer." not "Weekly pinball news by Colin Alsheimer,."
        var article = SampleArticle(publishedAt: null) with { PublishedAt = null };
        var chunks = NewSynthesizer().Synthesize(article, SampleRequest());
        var lead = chunks[0].Text;

        Assert.DoesNotContain(",.", lead, StringComparison.Ordinal);
    }
}
