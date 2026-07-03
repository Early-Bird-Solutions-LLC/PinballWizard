using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class PbFreshdeskArticleSynthesizerTests
{
    private static HybridChunker NewChunker() =>
        new(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance);

    private static PbFreshdeskArticleSynthesizer NewSynthesizer() =>
        new(NewChunker(), NullLogger<PbFreshdeskArticleSynthesizer>.Instance);

    private static FreshdeskArticle SampleArticle(string? bodyText = null) => new()
    {
        Title = "Volume is \"flickering\" up/down",
        Url = "https://pinballbrothers.freshdesk.com/support/solutions/articles/80000596607-volume-is-flickering-up-down",
        Folder = new FreshdeskFolder("FAQs ALIEN", "ALIEN - General", "https://pinballbrothers.freshdesk.com/support/solutions/folders/80000432961"),
        BodyText = bodyText ?? "After game boot, I see volume is changing rapidly up and down. 1. Check the fuses in playfield controller box. 2. Check and reseat cables from playfield.",
    };

    private static ChunkRequest SampleRequest() => new(
        MachineId: "mch_alien_g5b0e",
        MachineTitle: "Alien",
        Manufacturer: "Pinball Brothers",
        DocumentId: "pb_freshdesk_80000596607",
        DocumentUrl: "https://pinballbrothers.freshdesk.com/support/solutions/articles/80000596607-volume-is-flickering-up-down",
        DocumentType: DocumentType.SupportArticle);

    [Fact]
    public void Ctor_NullChunker_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new PbFreshdeskArticleSynthesizer(null!, NullLogger<PbFreshdeskArticleSynthesizer>.Instance));

    [Fact]
    public void Synthesize_NullArticle_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(null!, SampleRequest()));

    [Fact]
    public void Synthesize_NullChunkRequest_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(SampleArticle(), null!));

    [Fact]
    public void Synthesize_SampleArticle_ReturnsNonEmptyChunks()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Synthesize_SampleArticle_TitleInAttributedText()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Volume is \"flickering\" up/down", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_SourceUrlInAttributedText()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains(
            "https://pinballbrothers.freshdesk.com/support/solutions/articles/80000596607-volume-is-flickering-up-down",
            allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_FolderNameInAttributedText()
    {
        // Folder name is the provenance breadcrumb for a support article —
        // "ALIEN - General" tells the reader which manufacturer knowledge
        // base section this came from.
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("ALIEN - General", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_BodyContentPresent()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Check the fuses in playfield controller box", allText, StringComparison.Ordinal);
    }

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
}
