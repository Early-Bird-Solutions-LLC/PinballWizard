using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using PinballWizard.Web.Components.Shared;
// Disambiguate Markdig.Syntax.Block from MudBlazor.Block.
using Block = Markdig.Syntax.Block;

namespace PinballWizard.Web.Engineering;

// Walks a Markdig MarkdownDocument AST and emits MudBlazor components (and
// minimal plain HTML where specified) via RenderTreeBuilder.
//
// Safety contract: MarkupString and AddMarkupContent are NEVER used. All text
// content is passed to AddContent which HTML-encodes it through Blazor's
// rendering pipeline — XSS-class payloads are structurally impossible.
// This mirrors the posture established by MarkdownTokenizer.cs.
//
// URI safety: links whose final href does not match an allowlist of safe schemes
// (http/https, root-relative /, fragment #, or bare relative paths) are stripped
// to their link text with no href — javascript: and data: URIs never reach the DOM.
//
// Node mapping:
//   HeadingBlock       → MudText (Typo: h1→h4, h2→h5, h3→h6, else subtitle1)
//   ParagraphBlock     → MudText Typo.body1
//   ListBlock          → AppBulletList / AppBulletItem per list item
//   Table (pipe table) → plain <table class="eng-table"> / <thead> / <tbody>
//   CodeBlock          → <pre class="eng-code"><code> with AddContent text
//   HtmlBlock          → literal text via AddContent (never injected)
//   LiteralInline      → AddContent (HTML-encoded)
//   EmphasisInline     → <em> (×1) or <strong> (×2)
//   LinkInline         → MudLink; slug-resolved when in-manifest, Target="_blank" for http/https
//   HardlineBreakInline → <br>
//   HtmlInline         → literal text via AddContent (never injected)
//   Unhandled inline   → AddContent fallback (safe degradation, never vanishes)
//
// Sequence numbers: all set to 0. For hand-written RenderTreeBuilder code this
// is the documented pattern; Blazor diffing uses sequence only within
// Razor-generated source. ASP0006 is suppressed at class level.

#pragma warning disable ASP0006
internal static class MarkdownComponentRenderer
{
    internal static RenderFragment Render(
        MarkdownDocument ast,
        Func<string, string?> slugLinkResolver) =>
        builder => RenderBlocks(builder, ast, slugLinkResolver);

    // ── Block rendering ───────────────────────────────────────────────────────

    private static void RenderBlocks(
        RenderTreeBuilder builder,
        IEnumerable<Block> blocks,
        Func<string, string?> slugLinkResolver)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    RenderHeading(builder, heading, slugLinkResolver);
                    break;

                case ParagraphBlock para:
                    RenderParagraph(builder, para, slugLinkResolver);
                    break;

                case ListBlock list:
                    RenderList(builder, list, slugLinkResolver);
                    break;

                case Table table:
                    RenderTable(builder, table, slugLinkResolver);
                    break;

                case HtmlBlock html:
                    // Raw HTML in markdown: emit as escaped text, never as markup.
                    builder.AddContent(0, GetLeafText(html));
                    break;

                case CodeBlock code:
                    // Covers FencedCodeBlock (subclass) and plain CodeBlock.
                    RenderCode(builder, code);
                    break;

                case ContainerBlock container:
                    // Recurse into any other container (BlockQuote, etc.).
                    RenderBlocks(builder, container, slugLinkResolver);
                    break;

                default:
                    // Any other leaf block: fall back to literal lines.
                    if (block is LeafBlock lb && lb.Lines.Count > 0)
                        builder.AddContent(0, GetLeafText(lb));
                    break;
            }
        }
    }

    private static void RenderHeading(
        RenderTreeBuilder builder,
        HeadingBlock heading,
        Func<string, string?> slugLinkResolver)
    {
        var typo = heading.Level switch
        {
            1 => Typo.h4,
            2 => Typo.h5,
            3 => Typo.h6,
            _ => Typo.subtitle1,
        };
        builder.OpenComponent<MudText>(0);
        builder.AddComponentParameter(0, "Typo", typo);
        builder.AddComponentParameter(0, "ChildContent",
            (RenderFragment)(b => RenderInlines(b, heading.Inline, slugLinkResolver)));
        builder.CloseComponent();
    }

    private static void RenderParagraph(
        RenderTreeBuilder builder,
        ParagraphBlock para,
        Func<string, string?> slugLinkResolver)
    {
        builder.OpenComponent<MudText>(0);
        builder.AddComponentParameter(0, "Typo", Typo.body1);
        builder.AddComponentParameter(0, "ChildContent",
            (RenderFragment)(b => RenderInlines(b, para.Inline, slugLinkResolver)));
        builder.CloseComponent();
    }

    private static void RenderList(
        RenderTreeBuilder builder,
        ListBlock list,
        Func<string, string?> slugLinkResolver)
    {
        builder.OpenComponent<AppBulletList>(0);
        builder.AddComponentParameter(0, "ChildContent", (RenderFragment)(b =>
        {
            foreach (var listItem in list.OfType<ListItemBlock>())
            {
                b.OpenComponent<AppBulletItem>(0);
                b.AddComponentParameter(0, "ChildContent", (RenderFragment)(b2 =>
                {
                    foreach (var child in listItem)
                    {
                        if (child is ParagraphBlock p)
                            RenderInlines(b2, p.Inline, slugLinkResolver);
                    }
                }));
                b.CloseComponent();
            }
        }));
        builder.CloseComponent();
    }

    private static void RenderTable(
        RenderTreeBuilder builder,
        Table table,
        Func<string, string?> slugLinkResolver)
    {
        var headerRows = table.OfType<TableRow>().Where(r => r.IsHeader).ToList();
        var bodyRows = table.OfType<TableRow>().Where(r => !r.IsHeader).ToList();

        builder.OpenElement(0, "table");
        builder.AddAttribute(0, "class", "eng-table");

        if (headerRows.Count > 0)
        {
            builder.OpenElement(0, "thead");
            foreach (var row in headerRows)
                RenderTableRow(builder, row, isHeader: true, slugLinkResolver);
            builder.CloseElement();
        }

        if (bodyRows.Count > 0)
        {
            builder.OpenElement(0, "tbody");
            foreach (var row in bodyRows)
                RenderTableRow(builder, row, isHeader: false, slugLinkResolver);
            builder.CloseElement();
        }

        builder.CloseElement(); // table
    }

    private static void RenderTableRow(
        RenderTreeBuilder builder,
        TableRow row,
        bool isHeader,
        Func<string, string?> slugLinkResolver)
    {
        builder.OpenElement(0, "tr");
        foreach (var cell in row.OfType<TableCell>())
        {
            builder.OpenElement(0, isHeader ? "th" : "td");
            foreach (var child in cell)
            {
                if (child is ParagraphBlock p)
                    RenderInlines(builder, p.Inline, slugLinkResolver);
            }
            builder.CloseElement();
        }
        builder.CloseElement();
    }

    private static void RenderCode(RenderTreeBuilder builder, LeafBlock block)
    {
        builder.OpenElement(0, "pre");
        builder.AddAttribute(0, "class", "eng-code");
        builder.OpenElement(0, "code");
        builder.AddContent(0, GetLeafText(block));
        builder.CloseElement();
        builder.CloseElement();
    }

    // ── Inline rendering ──────────────────────────────────────────────────────

    private static void RenderInlines(
        RenderTreeBuilder builder,
        ContainerInline? inlines,
        Func<string, string?> slugLinkResolver)
    {
        if (inlines is null) return;
        foreach (var inline in inlines)
            RenderInline(builder, inline, slugLinkResolver);
    }

    private static void RenderInline(
        RenderTreeBuilder builder,
        Inline inline,
        Func<string, string?> slugLinkResolver)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.AddContent(0, literal.Content.ToString());
                break;

            case HtmlInline html:
                // Raw inline HTML: emit as escaped text, never as markup.
                builder.AddContent(0, html.Tag);
                break;

            case LineBreakInline lb:
                // Hard line break (two trailing spaces or backslash) → <br>.
                // Soft line break → space (CommonMark default).
                if (lb.IsHard)
                {
                    builder.OpenElement(0, "br");
                    builder.CloseElement();
                }
                else
                {
                    builder.AddContent(0, " ");
                }
                break;

            case EmphasisInline emphasis:
                var tag = emphasis.DelimiterCount >= 2 ? "strong" : "em";
                builder.OpenElement(0, tag);
                RenderInlines(builder, emphasis, slugLinkResolver);
                builder.CloseElement();
                break;

            case LinkInline link when !link.IsImage:
                RenderLink(builder, link, slugLinkResolver);
                break;

            case LinkInline image when image.IsImage:
                // Image: emit alt text from the link's inline children.
                RenderInlines(builder, image, slugLinkResolver);
                break;

            case CodeInline code:
                builder.OpenElement(0, "code");
                builder.AddContent(0, code.Content);
                builder.CloseElement();
                break;

            case ContainerInline container:
                // Unhandled container: recurse into children (safe degradation).
                RenderInlines(builder, container, slugLinkResolver);
                break;

            default:
                // Any unhandled inline: fall back to literal text via AddContent.
                // Content is always HTML-encoded by Blazor — never silently vanishes.
                builder.AddContent(0, inline.ToString() ?? "");
                break;
        }
    }

    private static void RenderLink(
        RenderTreeBuilder builder,
        LinkInline link,
        Func<string, string?> slugLinkResolver)
    {
        var url = link.Url ?? "";
        var isExternal = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var href = isExternal ? url : (slugLinkResolver(url) ?? url);

        if (!IsSafeHref(href))
        {
            // Dangerous scheme (javascript:, data:, etc.) — render link text only,
            // no href reaches the DOM.
            RenderInlines(builder, link, slugLinkResolver);
            return;
        }

        builder.OpenComponent<MudLink>(0);
        builder.AddComponentParameter(0, "Href", href);
        if (isExternal)
            builder.AddComponentParameter(0, "Target", "_blank");
        builder.AddComponentParameter(0, "ChildContent",
            (RenderFragment)(b => RenderInlines(b, link, slugLinkResolver)));
        builder.CloseComponent();
    }

    private static bool IsSafeHref(string url) =>
        url.Length == 0
        || url[0] == '/'
        || url[0] == '#'
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || !url.Contains(':', StringComparison.Ordinal); // relative repo paths like foo.md, ./bar
        // Note: percent-encoded colons (%3a) are safe — browsers do not decode the scheme
        // component when matching URI schemes, so %3a cannot smuggle a javascript: or data: URI.

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetLeafText(LeafBlock block)
    {
        var lines = block.Lines;
        if (lines.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines.Lines[i].Slice.ToString());
        }
        return sb.ToString();
    }
}
#pragma warning restore ASP0006
