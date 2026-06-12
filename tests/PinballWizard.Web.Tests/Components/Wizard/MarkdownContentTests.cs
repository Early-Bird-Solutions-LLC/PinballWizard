using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Behavioral tests for MarkdownContent + MarkdownTokenizer.
//
// MarkdownContent is the safe-markdown rendering component introduced in
// issue #371. It wraps MarkdownTokenizer to translate a safe subset of
// markdown (bold, italic, ordered/unordered lists, line breaks) into HTML
// elements using Blazor's RenderTreeBuilder (never MarkupString).
//
// Tests assert rendering correctness and the XSS safety invariant. Each test
// uses a fresh BunitContext per the pattern established in TokenRendererTests.
public sealed class MarkdownContentTests
{
    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Empty text renders nothing
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_text_renders_nothing()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, string.Empty));

        // No MudText / mud-typography wrapper should exist.
        Assert.Empty(cut.FindAll("p"));
        Assert.Empty(cut.FindAll("ol"));
        Assert.Empty(cut.FindAll("ul"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Plain text (no markdown markers) renders in a <p> element
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plain_text_renders_in_paragraph()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, "This is plain text."));

        var paras = cut.FindAll("p").ToList();
        Assert.Single(paras);
        Assert.Equal("This is plain text.", paras[0].TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // **bold** renders <strong>
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bold_markers_render_strong_element()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, "The **Godzilla Pro** is a great machine."));

        var strong = cut.Find("strong");
        Assert.Equal("Godzilla Pro", strong.TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // *italic* renders <em>
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Italic_markers_render_em_element()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, "The *Limited Edition* has unique art."));

        var em = cut.Find("em");
        Assert.Equal("Limited Edition", em.TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Ordered list renders <ol> + <li> elements
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ordered_list_renders_ol_with_li_items()
    {
        using var ctx = BuildCtx();
        var text = "1. First item\n2. Second item\n3. Third item";
        var cut = ctx.Render<MarkdownContent>(p => p.Add(c => c.Text, text));

        cut.Find("ol");
        var items = cut.FindAll("li").ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("First item", items[0].TextContent.Trim());
        Assert.Equal("Second item", items[1].TextContent.Trim());
        Assert.Equal("Third item", items[2].TextContent.Trim());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Unordered list renders <ul> + <li> elements
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unordered_list_renders_ul_with_li_items()
    {
        using var ctx = BuildCtx();
        var text = "- Alpha\n- Beta\n- Gamma";
        var cut = ctx.Render<MarkdownContent>(p => p.Add(c => c.Text, text));

        cut.Find("ul");
        var items = cut.FindAll("li").ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("Alpha", items[0].TextContent.Trim());
        Assert.Equal("Beta", items[1].TextContent.Trim());
        Assert.Equal("Gamma", items[2].TextContent.Trim());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Intra-paragraph line break renders <br />
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Intra_paragraph_line_break_renders_br_element()
    {
        using var ctx = BuildCtx();
        var text = "First line\nSecond line";
        var cut = ctx.Render<MarkdownContent>(p => p.Add(c => c.Text, text));

        // One <br> between the two lines.
        Assert.Single(cut.FindAll("br"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Bold inside a list item renders correctly
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bold_inside_list_item_renders_strong_within_li()
    {
        using var ctx = BuildCtx();
        // Issue #371 live format: "1. **Medieval Madness Remake (2015)** — OPDB source"
        var text = "1. **Medieval Madness Remake (2015)** — OPDB source\n2. Regular item";
        var cut = ctx.Render<MarkdownContent>(p => p.Add(c => c.Text, text));

        var items = cut.FindAll("li").ToList();
        Assert.Equal(2, items.Count);

        // First item has a <strong> child.
        Assert.NotNull(items[0].QuerySelector("strong"));
        Assert.Equal("Medieval Madness Remake (2015)", items[0].QuerySelector("strong")!.TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // XSS safety: <script> tag in text is encoded, not executed
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Script_tag_in_text_is_html_encoded_not_executed()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, "<script>alert(1)</script>**x**"));

        // No <script> element in the rendered tree.
        Assert.Empty(cut.FindAll("script"));

        // The raw InnerHtml must not contain an unencoded <script> open tag.
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // XSS safety: inline event handler attempt is text-encoded
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inline_event_handler_in_text_is_not_rendered_as_attribute()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, "**Click <img onerror=alert(1)> here**"));

        // No img element with onerror attribute must appear.
        Assert.Empty(cut.FindAll("img"));
        // The literal text "onerror" might appear encoded in text content — that's fine.
        // What must NOT appear is an actual onerror event handler attribute.
        var imgs = cut.FindAll("[onerror]");
        Assert.Empty(imgs);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Multiple blank-line-separated paragraphs render as separate <p> elements
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Blank_line_separated_text_renders_multiple_paragraphs()
    {
        using var ctx = BuildCtx();
        var text = "First paragraph.\n\nSecond paragraph.";
        var cut = ctx.Render<MarkdownContent>(p => p.Add(c => c.Text, text));

        var paras = cut.FindAll("p").ToList();
        Assert.Equal(2, paras.Count);
        Assert.Equal("First paragraph.", paras[0].TextContent.Trim());
        Assert.Equal("Second paragraph.", paras[1].TextContent.Trim());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Unmatched single star does not crash and renders literal asterisk
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unmatched_single_star_renders_as_literal_text()
    {
        using var ctx = BuildCtx();
        // An asterisk with no closing partner must not throw; renders literally.
        var cut = ctx.Render<MarkdownContent>(p => p
            .Add(c => c.Text, "Price is $10 * 3 units"));

        // No exception thrown (render completed). Text content should be present.
        Assert.NotEmpty(cut.Markup);
        // No <em> element spawned from the lone *.
        Assert.Empty(cut.FindAll("em"));
    }
}
