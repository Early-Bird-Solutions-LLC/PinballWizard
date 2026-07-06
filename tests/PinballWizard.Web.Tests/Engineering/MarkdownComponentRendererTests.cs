using Bunit;
using Markdig;
using MudBlazor.Services;
using PinballWizard.Web.Engineering;
using Xunit;

namespace PinballWizard.Web.Tests.Engineering;

// Behavioral and XSS-safety tests for MarkdownComponentRenderer.
//
// Verifies the AST-to-MudBlazor rendering pipeline:
//   - Headings → MudText with the correct Typo mapping
//   - Paragraphs → MudText body1
//   - Lists → AppBulletList / AppBulletItem
//   - Pipe tables → plain <table>/<thead>/<tbody>
//   - Emphasis → <em>/<strong>
//   - Links: in-manifest relative → /engineering route, external → Target="_blank"
//   - XSS: raw HTML in markdown is ESCAPED (AddContent), never injected (never
//     AddMarkupContent / MarkupString). The escaped-HTML test is the hard gate.
//
// BunitContext + IAsyncLifetime: MudBlazor 9's KeyInterceptorService / PopoverService
// implement only IAsyncDisposable; Dispose() throws when AppBulletList (MudList)
// is in the render tree. DisposeAsync() via IAsyncLifetime fixes this — the same
// pattern as AsyncBunitContext in this repo.
public sealed class MarkdownComponentRendererTests : BunitContext, IAsyncLifetime
{
    public MarkdownComponentRendererTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync().ConfigureAwait(false);

    private static Markdig.Syntax.MarkdownDocument Parse(string md) =>
        Markdown.Parse(md, new MarkdownPipelineBuilder().UsePipeTables().Build());

    // ── Headings ──────────────────────────────────────────────────────────────

    [Fact]
    public void Heading_RendersAsMudText()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("# Hello"), _ => null);
        var cut = Render(frag);
        Assert.Contains("Hello", cut.Markup);
        Assert.Contains("mud-typography", cut.Markup);
    }

    [Fact]
    public void H1_MapsToTypoH4()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("# Heading One"), _ => null);
        var cut = Render(frag);
        Assert.Contains("mud-typography-h4", cut.Markup);
    }

    [Fact]
    public void H2_MapsToTypoH5()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("## Heading Two"), _ => null);
        var cut = Render(frag);
        Assert.Contains("mud-typography-h5", cut.Markup);
    }

    [Fact]
    public void H3_MapsToTypoH6()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("### Heading Three"), _ => null);
        var cut = Render(frag);
        Assert.Contains("mud-typography-h6", cut.Markup);
    }

    // ── XSS safety (hard gate) ────────────────────────────────────────────────

    [Fact]
    public void RawHtmlInMarkdown_IsEscapedNotInjected()
    {
        // This is the critical XSS gate: <script> in markdown must never appear
        // as a live DOM element. Blazor's AddContent HTML-encodes all text, so
        // "<script>" becomes "&lt;script&gt;" in the rendered markup.
        var frag = MarkdownComponentRenderer.Render(Parse("<script>alert(1)</script>"), _ => null);
        var cut = Render(frag);
        // No live <script> element.
        Assert.DoesNotContain("<script>", cut.Markup);
        // The text appears as HTML-encoded entities.
        Assert.Contains("&lt;script&gt;", cut.Markup);
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    [Fact]
    public void InManifestRelativeLink_IsRewrittenToEngineeringRoute()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("[glossary](glossary.md)"),
            rel => rel == "glossary.md" ? "/engineering/docs/glossary" : null);
        var cut = Render(frag);
        Assert.Contains("/engineering/docs/glossary", cut.Markup);
    }

    [Fact]
    public void ExternalLink_GetsTargetBlank()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("[Click here](https://example.com)"),
            _ => null);
        var cut = Render(frag);
        Assert.Contains("https://example.com", cut.Markup);
        Assert.Contains("_blank", cut.Markup);
    }

    [Fact]
    public void RelativeLink_NotInManifest_UsesRawUrl()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("[unknown](other.md)"),
            _ => null);   // resolver returns null → fall back to raw URL
        var cut = Render(frag);
        Assert.Contains("other.md", cut.Markup);
    }

    // ── Lists ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UnorderedList_RendersAsAppBulletList()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("- Item one\n- Item two"), _ => null);
        var cut = Render(frag);
        Assert.Contains("Item one", cut.Markup);
        Assert.Contains("Item two", cut.Markup);
        // AppBulletList wraps MudList which renders with the mud-list CSS class.
        Assert.Contains("mud-list", cut.Markup);
    }

    // ── Tables ────────────────────────────────────────────────────────────────

    [Fact]
    public void PipeTable_RendersAsPlainHtmlTable()
    {
        var md = "| Col A | Col B |\n|-------|-------|\n| val 1 | val 2 |";
        var frag = MarkdownComponentRenderer.Render(Parse(md), _ => null);
        var cut = Render(frag);
        Assert.Contains("<table", cut.Markup);
        Assert.Contains("Col A", cut.Markup);
        Assert.Contains("val 1", cut.Markup);
    }

    // ── Emphasis ──────────────────────────────────────────────────────────────

    [Fact]
    public void BoldEmphasis_RendersAsStrong()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("**bold text**"), _ => null);
        var cut = Render(frag);
        Assert.Contains("<strong>", cut.Markup);
        Assert.Contains("bold text", cut.Markup);
    }

    [Fact]
    public void ItalicEmphasis_RendersAsEm()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("*italic text*"), _ => null);
        var cut = Render(frag);
        Assert.Contains("<em>", cut.Markup);
        Assert.Contains("italic text", cut.Markup);
    }

    [Fact]
    public void BoldInsideListItem_RendersStrongWithinItem()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("- **Medieval Madness** is great\n- Regular item"),
            _ => null);
        var cut = Render(frag);
        Assert.Contains("<strong>", cut.Markup);
        Assert.Contains("Medieval Madness", cut.Markup);
    }

    // ── H4 heading ────────────────────────────────────────────────────────────

    [Fact]
    public void H4_MapsToTypoSubtitle1()
    {
        var frag = MarkdownComponentRenderer.Render(Parse("#### Heading Four"), _ => null);
        var cut = Render(frag);
        Assert.Contains("mud-typography-subtitle1", cut.Markup);
    }

    // ── Dangerous URI scheme stripping ────────────────────────────────────────

    [Fact]
    public void JavascriptSchemeLink_StripsHrefAndPreservesText()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("[evil](javascript:alert(1))"),
            _ => null);
        var cut = Render(frag);
        Assert.DoesNotContain("javascript:", cut.Markup);
        Assert.Contains("evil", cut.Markup);
    }

    [Fact]
    public void DataUriLink_StripsHref()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("[x](data:text/html,foo)"),
            _ => null);
        var cut = Render(frag);
        Assert.DoesNotContain("data:text/html", cut.Markup);
        Assert.Contains("x", cut.Markup);   // link text must survive href stripping
    }

    [Fact]
    public void ExternalHttpsLink_StillRendersHrefWithTargetBlank()
    {
        var frag = MarkdownComponentRenderer.Render(
            Parse("[y](https://example.com)"),
            _ => null);
        var cut = Render(frag);
        Assert.Contains("https://example.com", cut.Markup);
        Assert.Contains("_blank", cut.Markup);
    }
}
