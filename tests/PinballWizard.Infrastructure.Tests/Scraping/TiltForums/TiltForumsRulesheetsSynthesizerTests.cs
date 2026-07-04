using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsRulesheetsSynthesizerTests
{
    private static HybridChunker NewChunker() =>
        new(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance);

    private static TiltForumsRulesheetsSynthesizer NewSynthesizer() =>
        new(NewChunker(), NullLogger<TiltForumsRulesheetsSynthesizer>.Instance);

    private static TiltForumsRulesheetArticle TransformersArticle() => new()
    {
        GameTitle = "Transformers: More Than Meets The Eye",
        ManufacturerHeaderText = "Stern Pinball",
        TopicUrl = "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
        Author = "CaptainBZarre",
        CodeRevision = "0.87",
        PublishedAt = new DateTimeOffset(2026, 5, 21, 15, 3, 35, TimeSpan.Zero),
        BodyText = """
            ## Quick Links:

            Game Information, Layout, Skill Shots, Main Modes.

            ## Game Information & Overview:

            Lead Designer: Elliot Eismin. Wiki Rulesheet based on Code Rev: 0.87

            ## Main Modes:

            Knock down the drop targets in front of Megatron, then shoot the scoop behind them to start the currently flashing mission. There are five different missions to play through, timed for 60 seconds.

            ## Wizard Modes:

            One Shall Fall is a mini-wizard mode reached by playing two missions.
            """,
    };

    private static ChunkRequest SampleChunkRequest(string docId = "tiltforums_10229_GweeP-MW95j") => new(
        MachineId: "GweeP-MW95j",
        MachineTitle: "Transformers: More Than Meets The Eye",
        Manufacturer: "Stern Pinball",
        DocumentId: docId,
        DocumentUrl: "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
        DocumentType: DocumentType.Rulesheet,
        LastScrapedUtc: new DateTimeOffset(2026, 5, 21, 15, 3, 35, TimeSpan.Zero));

    [Fact]
    public void Ctor_NullChunker_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TiltForumsRulesheetsSynthesizer(null!, NullLogger<TiltForumsRulesheetsSynthesizer>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TiltForumsRulesheetsSynthesizer(NewChunker(), null!));
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

    [Fact]
    public void Synthesize_TransformersArticle_ReturnsNonEmptyChunks()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Synthesize_TransformersArticle_AttributionAndSourceInText()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Community wiki rulesheet", allText, StringComparison.Ordinal);
        Assert.Contains("code rev 0.87", allText, StringComparison.Ordinal);
        Assert.Contains(
            "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
            allText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_TransformersArticle_BodyContentPresent()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Megatron", allText, StringComparison.Ordinal);
        Assert.Contains("One Shall Fall", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_ChunkIndex_StartsAtZeroAndIsStrictlyIncreasing()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.Equal(0, chunks[0].ChunkIndex);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].ChunkIndex > chunks[i - 1].ChunkIndex);
        }
    }

    [Fact]
    public void Synthesize_EmptyBodyText_ReturnsEmpty_NoFabrication()
    {
        var article = new TiltForumsRulesheetArticle
        {
            GameTitle = "No Content Game",
            ManufacturerHeaderText = "Stern Pinball",
            TopicUrl = "https://tiltforums.com/t/no-content-rulesheet/1",
            Author = "Someone",
            BodyText = "",
        };

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest("tiltforums_1_X"));

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_NoCodeRevision_OmitsCodeRevClause()
    {
        var article = new TiltForumsRulesheetArticle
        {
            GameTitle = "Some Game",
            ManufacturerHeaderText = "Stern Pinball",
            TopicUrl = "https://tiltforums.com/t/some-game-rulesheet/1",
            Author = "Someone",
            BodyText = "## Overview\n\nSome body text about the game.",
            CodeRevision = null,
        };

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest("tiltforums_1_X"));

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.DoesNotContain("code rev", allText, StringComparison.OrdinalIgnoreCase);
    }
}
