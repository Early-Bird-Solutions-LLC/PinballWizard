using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using PinballWizard.Web.Components.Citations;

namespace PinballWizard.Web.Components.Wizard;

// Minimal safe-markdown tokenizer for Wizard answer prose.
//
// Renders a safe subset of markdown — bold, italic, ordered/unordered lists,
// and paragraph line breaks — into known-safe Blazor RenderFragment elements
// (strong / em / ol / ul / li / br / p). All text content is HTML-encoded by
// Blazor's RenderFragment pipeline; MarkupString is never used.
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
//   - Subset only: headings (# ##), thematic breaks (---), code blocks,
//     tables are not rendered; they pass through as literal text. The model
//     answer style does not use them today.
//
// Supported syntax (all spec-minimal, no nesting within inline spans):
//   **text**           → <strong>text</strong>
//   *text*             → <em>text</em>
//   1. item text\n     → <ol><li>item text</li>…</ol>
//   - item text\n      → <ul><li>item text</li>…</ul>
//   blank line         → paragraph break (<p> wrapping)
//   line break (\n)    → <br /> within a paragraph
//
// Not supported (renders as literal text):
//   __bold__  _italic_  ~~strike~~  `code`  # heading  links  tables
//
// Sequence numbers: Blazor's ASP0006 analyzer requires literal integers in
// Razor-generated code; for manually-written RenderTreeBuilder code, using 0
// for all calls is the documented pattern (Blazor diffing uses the sequence
// only within Razor-generated source positions, not in hand-authored builders).
// Suppress ASP0006 at the method level accordingly.
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
    private sealed record OrderedListBlock(List<string> Items) : Block;
    private sealed record UnorderedListBlock(List<string> Items) : Block;

    // ── Block parser ──────────────────────────────────────────────────────

    private static List<Block> ParseBlocks(string text)
    {
        // Normalize line endings to \n for simplicity.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Split into raw lines.
        var lines = text.Split('\n');

        var blocks = new List<Block>();
        var paragraphLines = new List<string>();

        // Process any accumulated paragraph-buffer lines and emit a ParagraphBlock.
        void FlushParagraph()
        {
            if (paragraphLines.Count == 0) return;
            // Join with \n so line-break rendering can split on it later.
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

            // Ordered list: "N. " prefix (1-3 digits + dot + space).
            if (IsOrderedListItem(line, out var olText))
            {
                FlushParagraph();
                var items = new List<string> { olText! };
                i++;
                while (i < lines.Length && IsOrderedListItem(lines[i], out var nextOlText))
                {
                    items.Add(nextOlText!);
                    i++;
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
    // Sequence numbers are all 0 — see class-level comment. Suppress ASP0006
    // (sequence-number lint) since this is a hand-written builder, not
    // Razor-generated code. The Blazor diffing algorithm does not rely on
    // sequence numbers in manually-written builders the same way it does in
    // Razor-generated sources.

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

                case OrderedListBlock ol:
                    builder.OpenElement(0, "ol");
                    foreach (var item in ol.Items)
                    {
                        builder.OpenElement(0, "li");
                        RenderInline(builder, item, occurrences);
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

    // Renders inline markdown within a block: **bold**, *italic*, \n→<br>.
    // Text content is always passed to AddContent (HTML-encoded by Blazor).
    // occurrences is threaded from BuildTree so [[cite:N]] markers across all
    // lines of a block (and all blocks) share the same per-render counter.
    private static void RenderInline(RenderTreeBuilder builder, string text, Dictionary<int, int> occurrences)
    {
        // Split by \n to handle intra-paragraph line breaks → <br />.
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

    // Renders bold/italic/cite inline markers within a single line of text.
    // Processes the string left-to-right, emitting text runs and wrapped elements.
    // occurrences is threaded from BuildTree so the per-render occurrence counter
    // for [[cite:N]] is maintained across all calls within a single Render pass.
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

            // Try [[cite:N]] inline citation token. Closed registry — currently only
            // "cite" with an integer payload. Unknown kind / malformed payload falls
            // through to literal text (fail-safe, CSP-safe, designed to extend).
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
                // Not a recognized token — fall through; the '[' is appended as literal text.
            }

            // No inline marker — emit one character as plain text. Buffer up
            // consecutive plain chars to reduce RenderTreeBuilder node count.
            var runStart = pos;
            while (pos < text.Length)
            {
                // Stop buffering at any potential marker start character.
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
                // Single unmatched '*' or '[' — emit it literally and advance.
                builder.AddContent(0, text[pos].ToString());
                pos++;
            }
        }
    }
#pragma warning restore ASP0006

    // Inline-token scanner: [[<kind>:<payload>]]. Returns true when a well-formed
    // token is found at pos; sets consumed to the full token length (including ]]
    // so the caller can skip past it). Empty kind, empty payload, or a missing ]]
    // are all treated as non-tokens so the surrounding '[' characters fall through
    // to literal text (fail-safe, CSP-safe). Designed to extend — callers filter
    // by kind ("cite" today; "portal" or others later).
    private static bool TryMatchInlineToken(
        string text, int pos, out int consumed, out string kind, out string payload)
    {
        consumed = 0;
        kind = "";
        payload = "";

        // Need at least "[[x:y]]" = 7 chars; pos+1 is already '[' (caller verified).
        if (pos + 4 >= text.Length || text[pos] != '[' || text[pos + 1] != '[')
            return false;

        var close = text.IndexOf("]]", pos + 2, StringComparison.Ordinal);
        if (close < 0)
            return false;

        var inner = text.Substring(pos + 2, close - (pos + 2)); // e.g. "cite:2"
        var colon = inner.IndexOf(':');
        if (colon <= 0)
            return false;

        kind = inner[..colon];
        payload = inner[(colon + 1)..];
        if (payload.Length == 0)
            return false;

        consumed = (close + 2) - pos; // include closing ]]
        return true;
    }

    // Finds the index of the closing single '*' after startIdx.
    // A single '*' close must NOT be immediately preceded by another '*'
    // (that would be part of a '**' pair). It also must not be followed by
    // another '*' (opening of a ** bold marker).
    private static int FindSingleStarClose(string text, int startIdx)
    {
        for (var i = startIdx; i < text.Length; i++)
        {
            if (text[i] == '*')
            {
                // Ensure it is not part of a ** pair.
                var prevStar = i > 0 && text[i - 1] == '*';
                var nextStar = i + 1 < text.Length && text[i + 1] == '*';
                if (!prevStar && !nextStar)
                    return i;
            }
        }
        return -1;
    }
}
