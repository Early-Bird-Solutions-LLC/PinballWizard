using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using PinballWizard.Web.Components.Citations;

namespace PinballWizard.Web.Components.Wizard;

// Minimal safe-markdown tokenizer for Wizard answer prose.
//
// Renders a safe subset of markdown — bold, italic, ordered/unordered lists
// (with nested UL under OL items), ATX headings, thematic breaks, and
// paragraph line breaks — into known-safe Blazor RenderFragment elements.
// All text content is HTML-encoded by Blazor's RenderFragment pipeline;
// MarkupString is never used.
//
// Design constraints:
//   - Streaming path is UNTOUCHED: TokenRenderer's streaming spans stay
//     plain-text. This class is called only on canonical (Final/complete) text.
//   - Safety: the output is built from individual RenderTreeBuilder calls,
//     not from MarkupString. XSS-class payloads are impossible because text
//     nodes are always AddContent(text), never SetInnerHtml.
//   - CSP: no inline event handlers or style attributes are ever emitted.
//   - Links: the model's inline-link syntax [text](url) is stripped server-
//     side by the Citation pipeline before this class ever sees the text.
//     This tokenizer does NOT render links; if [text](url) leaks through,
//     the brackets and parens render as literal text (safe degradation).
//
// Supported syntax (spec-minimal):
//   **text**           → <strong>text</strong>
//   *text*             → <em>text</em>
//   1. item text\n     → <ol><li>…</li></ol>  (nested - bullets stay in the same <ol>)
//   - item text\n      → <ul><li>…</li></ul>
//   ##–###### heading  → <h2>–<h6>  (# demoted to h2 so answers never steal page h1)
//   --- / *** / ___    → <hr />
//   blank line         → paragraph break (<p> wrapping)
//   line break (\n)    → <br /> within a paragraph
//
// Not supported (renders as literal text):
//   __bold__  _italic_  ~~strike~~  `code`  links  tables
//
// Sequence numbers: Blazor's ASP0006 analyzer requires literal integers in
// Razor-generated code; for manually-written RenderTreeBuilder code, using 0
// for all calls is the documented pattern. Suppress ASP0006 at the method level.
internal static class MarkdownTokenizer
{
    // Builds a RenderFragment for the given markdown text using the safe subset above.
    internal static RenderFragment Render(string text)
    {
        if (string.IsNullOrEmpty(text))
            return _ => { };

        var blocks = ParseBlocks(text);
        return builder => BuildTree(builder, blocks);
    }

    // ── Block types ───────────────────────────────────────────────────────

    private abstract record Block;

    private sealed record ParagraphBlock(string Text) : Block;
    private sealed record HeadingBlock(int Level, string Text) : Block;
    private sealed record ThematicBreakBlock : Block;

    // Ordered list item may carry nested unordered bullets (issue #786):
    //   1. Parent
    //   - nested
    //   2. Sibling
    // must stay ONE <ol> with two <li>s, the first containing a nested <ul>.
    private sealed record OrderedListItem(string Text, List<string> NestedUnordered);
    private sealed record OrderedListBlock(List<OrderedListItem> Items) : Block;
    private sealed record UnorderedListBlock(List<string> Items) : Block;

    // ── Block parser ──────────────────────────────────────────────────────

    private static List<Block> ParseBlocks(string text)
    {
        // Normalize line endings to \n for simplicity.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var lines = text.Split('\n');
        var blocks = new List<Block>();
        var paragraphLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0) return;
            var joined = string.Join("\n", paragraphLines).Trim();
            if (!string.IsNullOrEmpty(joined))
                blocks.Add(new ParagraphBlock(joined));
            paragraphLines.Clear();
        }

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Blank line → paragraph separator.
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            // Thematic break: --- / *** / ___ (3+ of the same char, optional spaces).
            if (IsThematicBreak(line))
            {
                FlushParagraph();
                blocks.Add(new ThematicBreakBlock());
                i++;
                continue;
            }

            // ATX heading: 1–6 leading # + space + text.
            if (IsAtxHeading(line, out var headingLevel, out var headingText))
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(headingLevel, headingText!));
                i++;
                continue;
            }

            // Ordered list: "N. " prefix. Nested "- "/"* " lines between items
            // stay attached to the current item so the <ol> is not split (#786).
            if (IsOrderedListItem(line, out var olText))
            {
                FlushParagraph();
                var items = new List<OrderedListItem>
                {
                    new(olText!, []),
                };
                i++;
                while (i < lines.Length)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        // Blank inside a list: peek — continue if next non-blank
                        // is still a list item (OL or nested UL); else end list.
                        var peek = i + 1;
                        while (peek < lines.Length && string.IsNullOrWhiteSpace(lines[peek]))
                            peek++;
                        if (peek < lines.Length
                            && (IsOrderedListItem(lines[peek], out _)
                                || IsUnorderedListItem(lines[peek], out _)))
                        {
                            i++;
                            continue;
                        }
                        break;
                    }

                    if (IsOrderedListItem(lines[i], out var nextOlText))
                    {
                        items.Add(new OrderedListItem(nextOlText!, []));
                        i++;
                        continue;
                    }

                    if (IsUnorderedListItem(lines[i], out var nestedUlText))
                    {
                        items[^1].NestedUnordered.Add(nestedUlText!);
                        i++;
                        continue;
                    }

                    break;
                }
                blocks.Add(new OrderedListBlock(items));
                continue;
            }

            // Unordered list: "- " or "* " prefix.
            if (IsUnorderedListItem(line, out var ulText))
            {
                FlushParagraph();
                var items = new List<string> { ulText! };
                i++;
                while (i < lines.Length && IsUnorderedListItem(lines[i], out var nextUlText))
                {
                    items.Add(nextUlText!);
                    i++;
                }
                blocks.Add(new UnorderedListBlock(items));
                continue;
            }

            // Plain line → accumulate into paragraph.
            paragraphLines.Add(line);
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    private static bool IsAtxHeading(string line, out int level, out string? text)
    {
        level = 0;
        text = null;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '#')
            return false;

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;

        // CommonMark: 1–6 hashes, then whitespace (or end). We require a space
        // before text so "###foo" stays literal.
        if (hashes is < 1 or > 6)
            return false;
        if (hashes >= trimmed.Length)
        {
            level = hashes;
            text = string.Empty;
            return true;
        }
        if (!char.IsWhiteSpace(trimmed[hashes]))
            return false;

        level = hashes;
        text = trimmed[hashes..].Trim();
        // Strip optional trailing closing hashes ("### Title ###").
        text = text.TrimEnd('#').TrimEnd();
        return true;
    }

    private static bool IsThematicBreak(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
            return false;

        var ch = trimmed[0];
        if (ch is not ('-' or '*' or '_'))
            return false;

        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == ch)
            {
                count++;
                continue;
            }
            if (char.IsWhiteSpace(c))
                continue;
            return false;
        }

        return count >= 3;
    }

    private static bool IsOrderedListItem(string line, out string? text)
    {
        // Match "1. " through "999. " (1-3 digit ordinal + dot + space).
        var trimmed = line.TrimStart();
        var dotIdx = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (dotIdx is >= 1 and <= 3)
        {
            var prefix = trimmed[..dotIdx];
            if (prefix.All(char.IsDigit))
            {
                text = trimmed[(dotIdx + 2)..].Trim();
                return true;
            }
        }
        text = null;
        return false;
    }

    private static bool IsUnorderedListItem(string line, out string? text)
    {
        var trimmed = line.TrimStart();
        if ((trimmed.StartsWith("- ", StringComparison.Ordinal) ||
             trimmed.StartsWith("* ", StringComparison.Ordinal)) &&
            trimmed.Length > 2)
        {
            text = trimmed[2..].Trim();
            return true;
        }
        text = null;
        return false;
    }

    // ── RenderTreeBuilder output ──────────────────────────────────────────

#pragma warning disable ASP0006
    private static void BuildTree(RenderTreeBuilder builder, List<Block> blocks)
    {
        // Per-render occurrence counter: tracks how many times each citation number
        // has been emitted so that multiple [[cite:N]] tokens for the same N get
        // distinct Occurrence values (1, 2, …) → distinct DOM ids (marker-N-1, …).
        var occurrences = new Dictionary<int, int>();

        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphBlock p:
                    builder.OpenElement(0, "p");
                    RenderInline(builder, p.Text, occurrences);
                    builder.CloseElement();
                    break;

                case HeadingBlock h:
                    // Demote level-1 ATX to h2 so answer prose never injects a
                    // second document <h1> (page shell owns that — #789/#790).
                    // ##→h2, ###→h3, … ######→h6.
                    var htmlLevel = h.Level == 1 ? 2 : h.Level;
                    builder.OpenElement(0, "h" + htmlLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    RenderInline(builder, h.Text, occurrences);
                    builder.CloseElement();
                    break;

                case ThematicBreakBlock:
                    builder.OpenElement(0, "hr");
                    builder.CloseElement();
                    break;

                case OrderedListBlock ol:
                    builder.OpenElement(0, "ol");
                    foreach (var item in ol.Items)
                    {
                        builder.OpenElement(0, "li");
                        RenderInline(builder, item.Text, occurrences);
                        if (item.NestedUnordered.Count > 0)
                        {
                            builder.OpenElement(0, "ul");
                            foreach (var nested in item.NestedUnordered)
                            {
                                builder.OpenElement(0, "li");
                                RenderInline(builder, nested, occurrences);
                                builder.CloseElement();
                            }
                            builder.CloseElement();
                        }
                        builder.CloseElement();
                    }
                    builder.CloseElement();
                    break;

                case UnorderedListBlock ul:
                    builder.OpenElement(0, "ul");
                    foreach (var item in ul.Items)
                    {
                        builder.OpenElement(0, "li");
                        RenderInline(builder, item, occurrences);
                        builder.CloseElement();
                    }
                    builder.CloseElement();
                    break;
            }
        }
    }

    private static void RenderInline(RenderTreeBuilder builder, string text, Dictionary<int, int> occurrences)
    {
        var lines = text.Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            if (li > 0)
            {
                builder.OpenElement(0, "br");
                builder.CloseElement();
            }

            RenderInlineSpans(builder, lines[li], occurrences);
        }
    }

    private static void RenderInlineSpans(RenderTreeBuilder builder, string text, Dictionary<int, int> occurrences)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            // Try **bold** first (greedy — must check before single-star italic).
            if (pos + 1 < text.Length &&
                text[pos] == '*' && text[pos + 1] == '*')
            {
                var closeIdx = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                if (closeIdx >= pos + 2)
                {
                    builder.OpenElement(0, "strong");
                    builder.AddContent(0, text[(pos + 2)..closeIdx]);
                    builder.CloseElement();
                    pos = closeIdx + 2;
                    continue;
                }
            }

            // Try *italic* (single star, not part of ** pair).
            if (text[pos] == '*' &&
                (pos + 1 >= text.Length || text[pos + 1] != '*'))
            {
                var closeIdx = FindSingleStarClose(text, pos + 1);
                if (closeIdx > pos + 1)
                {
                    builder.OpenElement(0, "em");
                    builder.AddContent(0, text[(pos + 1)..closeIdx]);
                    builder.CloseElement();
                    pos = closeIdx + 1;
                    continue;
                }
            }

            // Try [[cite:N]] inline citation token.
            if (pos + 1 < text.Length && text[pos] == '[' && text[pos + 1] == '[')
            {
                if (TryMatchInlineToken(text, pos, out var consumed, out var kind, out var payload)
                    && kind == "cite"
                    && int.TryParse(payload, out var citeNumber))
                {
                    var occ = occurrences.TryGetValue(citeNumber, out var prev) ? prev + 1 : 1;
                    occurrences[citeNumber] = occ;
                    builder.OpenComponent<CitationMarker>(0);
                    builder.AddComponentParameter(0, nameof(CitationMarker.Number), citeNumber);
                    builder.AddComponentParameter(0, nameof(CitationMarker.Occurrence), occ);
                    builder.CloseComponent();
                    pos += consumed;
                    continue;
                }
            }

            // No inline marker — emit consecutive plain chars.
            var runStart = pos;
            while (pos < text.Length)
            {
                if (text[pos] == '*') break;
                if (text[pos] == '[') break;
                pos++;
            }

            if (pos > runStart)
            {
                builder.AddContent(0, text[runStart..pos]);
            }
            else
            {
                builder.AddContent(0, text[pos].ToString());
                pos++;
            }
        }
    }
#pragma warning restore ASP0006

    private static bool TryMatchInlineToken(
        string text, int pos, out int consumed, out string kind, out string payload)
    {
        consumed = 0;
        kind = "";
        payload = "";

        if (pos + 4 >= text.Length || text[pos] != '[' || text[pos + 1] != '[')
            return false;

        var close = text.IndexOf("]]", pos + 2, StringComparison.Ordinal);
        if (close < 0)
            return false;

        var inner = text.Substring(pos + 2, close - (pos + 2));
        var colon = inner.IndexOf(':');
        if (colon <= 0)
            return false;

        kind = inner[..colon];
        payload = inner[(colon + 1)..];
        if (payload.Length == 0)
            return false;

        consumed = (close + 2) - pos;
        return true;
    }

    private static int FindSingleStarClose(string text, int startIdx)
    {
        for (var i = startIdx; i < text.Length; i++)
        {
            if (text[i] == '*')
            {
                var prevStar = i > 0 && text[i - 1] == '*';
                var nextStar = i + 1 < text.Length && text[i + 1] == '*';
                if (!prevStar && !nextStar)
                    return i;
            }
        }
        return -1;
    }
}
