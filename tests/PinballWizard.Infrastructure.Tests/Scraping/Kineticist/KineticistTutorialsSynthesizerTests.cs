using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.Kineticist;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Kineticist;

/// <summary>
/// Unit tests for <see cref="KineticistTutorialsSynthesizer"/>.
/// </summary>
public sealed class KineticistTutorialsSynthesizerTests
{
    // ── Arrange helpers ─────────────────────────────────────────────────────────

    private static HybridChunker NewChunker() =>
        new(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance);

    private static KineticistTutorialsSynthesizer NewSynthesizer() =>
        new(NewChunker(), NullLogger<KineticistTutorialsSynthesizer>.Instance);

    private static KineticistTutorialArticle TransformersArticle() => new()
    {
        Title = "Autobots, Transform and Roll Out!",
        Author = "Noah Crable",
        CanonicalUrl = "https://www.kineticist.com/news/transformers-pinball-tutorial",
        GameSlug = "transformers",
        MarkdownContent = """
            # Autobots, Transform and Roll Out!

            by [Noah Crable](/author/noah-crable) · June 25, 2026 · [Pinball Tutorial](/news/category/pinball-tutorial)

            > Learn to play Stern Pinball's 2026 release, Transformers: More Than Meets the Eye.

            ## About the Game

            The Autobots and Decepticons have waged war across the galaxy for eons.

            ## Getting Started

            Shoot the Megatron scoop to start a mission. Two missions lights One Shall Fall.

            ### Skill Shot

            Plunge the ball softly to the upper flipper and hit any lit shot.

            ## Strategies

            Focus on Autobot Run first, then qualify Transformers Multiball.

            https://www.kineticist.com/news/transformers-pinball-tutorial
            """,
        PublishedAt = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero),
    };

    private static KineticistTutorialArticle ArticleWithNoPublishedAt() => new()
    {
        Title = "Autobots, Transform and Roll Out!",
        Author = "Noah Crable",
        CanonicalUrl = "https://www.kineticist.com/news/transformers-pinball-tutorial",
        GameSlug = "transformers",
        MarkdownContent = "# Autobots, Transform and Roll Out!\n\nContent body.\n\nhttps://www.kineticist.com/news/transformers-pinball-tutorial",
        PublishedAt = null,
    };

    private static ChunkRequest SampleChunkRequest(string docId = "kineticist_transformers-pinball-tutorial") => new(
        MachineId: "TF-123",
        MachineTitle: "transformers",
        Manufacturer: "Stern Pinball",
        DocumentId: docId,
        DocumentUrl: "https://www.kineticist.com/news/transformers-pinball-tutorial",
        DocumentType: DocumentType.Rulesheet,
        LastScrapedUtc: new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero));

    // ── Null guard tests ────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullChunker_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KineticistTutorialsSynthesizer(null!, NullLogger<KineticistTutorialsSynthesizer>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KineticistTutorialsSynthesizer(NewChunker(), null!));
    }

    [Fact]
    public void Synthesize_NullArticle_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(null!, SampleChunkRequest()));
    }

    [Fact]
    public void Synthesize_NullChunkRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(TransformersArticle(), null!));
    }

    // ── Happy-path content tests ─────────────────────────────────────────────

    [Fact]
    public void Synthesize_TransformersArticle_ReturnsNonEmptyChunks()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0, "TokenCount must be > 0 (tokenizer ran)."));
    }

    [Fact]
    public void Synthesize_TransformersArticle_AttributionInText()
    {
        // The attributed-text lead — "Tutorial by {Author} ({Date}). Source: {URL}" —
        // must appear in the synthesized chunk text. It's prepended to the document
        // head so it always lands in chunk 0 regardless of HybridChunker section splits.
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Noah Crable", allText, StringComparison.Ordinal);
        Assert.Contains("Tutorial by Noah Crable", allText, StringComparison.Ordinal);
        // Author attribution plus the date in MMMM d, yyyy format.
        Assert.Contains("June 25, 2026", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_TransformersArticle_CanonicalUrlInAttributedText()
    {
        // The canonical URL MUST appear in the synthesized text (not just in
        // ChunkRequest.DocumentUrl) so it rides every chunk snippet the Wizard
        // retrieves — provenance invariant (#1).
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains(
            "https://www.kineticist.com/news/transformers-pinball-tutorial",
            allText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_TransformersArticle_NoDuplicateH1Title()
    {
        // The synthesizer strips the duplicate H1 from the .md body so the
        // title doesn't appear twice in the lead chunk.
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var leadChunk = chunks[0];
        var titleCount = CountOccurrences(leadChunk.Text, "Autobots, Transform and Roll Out!");
        Assert.True(titleCount <= 1, $"Title appears {titleCount} times in lead chunk — expected at most 1.");
    }

    [Fact]
    public void Synthesize_TransformersArticle_BodyContentPresent()
    {
        // Gameplay strategy text from the article body must appear in the chunks
        // (proves the body wasn't stripped by the H1-dedup logic).
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Megatron", allText, StringComparison.Ordinal);
        Assert.Contains("Autobot Run", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_ChunkIndex_StartsAtZeroAndIsStrictlyIncreasing()
    {
        // ChunkIndex must start at 0 and increase — stability check for the
        // AI Search key derivation (chunk_id = hash(DocumentId + ChunkIndex)).
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.Equal(0, chunks[0].ChunkIndex);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].ChunkIndex > chunks[i - 1].ChunkIndex,
                "ChunkIndex must be strictly increasing.");
        }
    }

    // ── Graceful-degradation tests ────────────────────────────────────────────

    [Fact]
    public void Synthesize_EmptyMarkdown_ReturnsEmpty_NoFabrication()
    {
        // Invariant #17 (no-masking fallbacks): an article with empty body
        // must yield 0 chunks — not a placeholder or "Unknown" text.
        var article = new KineticistTutorialArticle
        {
            Title = "No Content Article",
            Author = "Test Author",
            CanonicalUrl = "https://www.kineticist.com/news/no-content",
            GameSlug = "test",
            MarkdownContent = "",
        };

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest("kineticist_no-content"));

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_WhitespaceOnlyMarkdown_ReturnsEmpty()
    {
        var article = new KineticistTutorialArticle
        {
            Title = "Whitespace Article",
            Author = "Test Author",
            CanonicalUrl = "https://www.kineticist.com/news/whitespace",
            GameSlug = "test",
            MarkdownContent = "   \r\n  \t  ",
        };

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest("kineticist_whitespace"));

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_ArticleWithoutPublishedAt_StillSynthesizes()
    {
        // PublishedAt is optional; articles without a date must still produce chunks.
        var article = ArticleWithNoPublishedAt();

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest());

        Assert.NotEmpty(chunks);
        // Date clause must be absent (not "()").
        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.DoesNotContain("()", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_TokenCounts_WithinReasonableEnvelope()
    {
        // A realistic tutorial article (500-1500 words of Markdown) should chunk
        // into sub-1000-token pieces. An unreasonably large count indicates the
        // chunker is not splitting.
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 1000));
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
