using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class FreshdeskArticleExtractorTests
{
    private const string ArticleUrl =
        "https://pinballbrothers.freshdesk.com/support/solutions/articles/80001073771-queen-pinball-technical-manual";

    // Captured verbatim (trimmed to the load-bearing elements) from
    // pinballbrothers.freshdesk.com/support/solutions/articles/80001073771-queen-pinball-technical-manual
    // on 2026-07-03. Real markup — not a guessed shape.
    private const string ArticleWithAttachmentHtml = """
        <html><body>
        <div class="breadcrumb">
            <a href="/support/solutions"> Solution home </a>
            <a href="/support/solutions/80000460814">FAQs QUEEN</a>
            <a href="/support/solutions/folders/80000701915">Queen - General</a>
        </div>
        <h2 class="heading">QUEEN Pinball - Technical Manual
             <a href="#" class="solution-print--icon print--remove" title="Print this Article" id="print-article">
                <span class="icon-print"></span>
                <span class="text-print">Print</span>
             </a>
        </h2>
        <p>Modified on: Fri, 28 Apr, 2023 at  8:34 AM</p>
        <hr />
        <article class="article-body" id="article-body" rel="image-enlarge">
            <p dir="ltr"><span dir="ltr">Here in attach you can find the QUEEN Technical Manual<br><br>**This version is not final**<br></span><br><br><span>ENJOY YOUR GAME!!</span><br><br><span>Andrea DM</span><br><span>PB Support TEAM</span></p>
        </article>
        <hr />
        <div class="cs-g-c attachments" id="article-80001073771-attachments">
            <div class="attachment">
                <div class="attachment-type"><span class="file-type"> pdf </span></div>
                <div class="attach_content">
                    <div class="ellipsis">
                        <a href="/helpdesk/attachments/80209470065" class="filename" target="_blank" data-toggle='tooltip' title='QUEEN PINBALL TECHNICAL GAME MANUAL R1.pdf'>QUEEN PINBAL... </a>
                    </div>
                    <div>(10.2 MB) </div>
                </div>
            </div>
        </div>
        </body></html>
        """;

    // Captured verbatim shape for a text-only article (no .attachments block at all) —
    // e.g. "Volume is flickering up/down" troubleshooting articles.
    private const string ArticleWithoutAttachmentHtml = """
        <html><body>
        <h2 class="heading">Volume is "flickering" up/down
             <a href="#" class="solution-print--icon print--remove" id="print-article"><span class="text-print">Print</span></a>
        </h2>
        <p>Modified on: Wed, 26 Jan, 2022 at  7:45 AM</p>
        <article class="article-body" id="article-body">
            <p>After game boot, I see volume is changing rapidly up and down. 1. Check the fuses in playfield controller box. 2. Check and reseat cables from playfield.</p>
        </article>
        </body></html>
        """;

    private const string ArticleWithNoTitleHtml = "<html><body><p>No heading here.</p></body></html>";

    [Fact]
    public void Extract_ArticleWithAttachment_ReturnsTitleBodyAndAttachment()
    {
        var result = FreshdeskArticleExtractor.Extract(ArticleWithAttachmentHtml, ArticleUrl);

        Assert.NotNull(result);
        Assert.Equal("QUEEN Pinball - Technical Manual", result!.Title);
        Assert.Contains("Here in attach you can find the QUEEN Technical Manual", result.BodyText, StringComparison.Ordinal);
        Assert.Contains("ENJOY YOUR GAME", result.BodyText, StringComparison.Ordinal);

        // The nested "Print" icon anchor must NOT leak into the title.
        Assert.DoesNotContain("Print", result.Title, StringComparison.Ordinal);

        Assert.Single(result.Attachments);
        Assert.Equal("https://pinballbrothers.freshdesk.com/helpdesk/attachments/80209470065", result.Attachments[0].Url);
        Assert.Equal("QUEEN PINBALL TECHNICAL GAME MANUAL R1.pdf", result.Attachments[0].FileName);
    }

    [Fact]
    public void Extract_ArticleWithoutAttachment_ReturnsEmptyAttachmentList()
    {
        var result = FreshdeskArticleExtractor.Extract(ArticleWithoutAttachmentHtml, ArticleUrl);

        Assert.NotNull(result);
        Assert.Equal("Volume is \"flickering\" up/down", result!.Title);
        Assert.Contains("Check the fuses in playfield controller box", result.BodyText, StringComparison.Ordinal);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public void Extract_NoHeadingElement_ReturnsNull()
    {
        // Degrade visibly: a page we can't find a title on yields null, not a
        // fabricated empty-title record (Invariant #17).
        var result = FreshdeskArticleExtractor.Extract(ArticleWithNoTitleHtml, ArticleUrl);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_EmptyHtml_ReturnsNull()
    {
        var result = FreshdeskArticleExtractor.Extract(string.Empty, ArticleUrl);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_NullHtml_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FreshdeskArticleExtractor.Extract(null!, ArticleUrl));
    }

    [Fact]
    public void Extract_WhitespaceArticleUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() => FreshdeskArticleExtractor.Extract(ArticleWithAttachmentHtml, "  "));
    }
}
