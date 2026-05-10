using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Rag.Chunking;

// Phase 4 hybrid chunker (ADR-0019) — token-budgeted chunks within
// heading-bounded sections. Three customer-delight refinements layer on
// top of the ADR's baseline algorithm:
//
//   1. Repeating header/footer detection + strip. Stern manuals carry a
//      running header on every page ("STERN PINBALL — GODZILLA OPS
//      MANUAL"); leaving it in chunk text contaminates every retrieved
//      citation snippet shown to the user. Detected by counting first /
//      last non-empty lines across pages and stripping any that repeat
//      on > HeaderFooterRepeatThreshold of pages (default 50%).
//
//   2. Section heading prepended to chunk text (markdown H2). The
//      heading is already in `Chunk.SectionHeading` for the citation
//      surface; duplicating it in chunk text gives the embedding model
//      additional lexical signal so heading-anchored queries
//      ("how does Foo Mode work?") retrieve the matching section even
//      when the body doesn't repeat the heading vocabulary.
//
//   3. Service bulletins treated as a single section. Bulletins are
//      short, single-issue documents whose Symptom / Cause / Resolution
//      sub-headings over-fragment retrieval. This refinement keeps the
//      sub-headings in chunk text but ignores them as section
//      boundaries — a query for "X symptom" retrieves the whole
//      bulletin, not just the Symptom paragraph.
//
// All three refinements are switchable via ChunkerOptions so H3
// calibration can ablate each independently.
public sealed class HybridChunker : IChunker
{
    private readonly ChunkerOptions _options;
    private readonly ILogger<HybridChunker> _logger;
    private readonly TiktokenTokenizer _tokenizer;

    public HybridChunker(
        IOptions<ChunkerOptions> options,
        ILogger<HybridChunker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value ?? throw new ArgumentException(
            "ChunkerOptions instance was null inside IOptions wrapper.", nameof(options));
        _logger = logger;

        // The Cl100kBase data package ships the BPE vocab as an embedded
        // resource that this factory loads via assembly probe. Pinning to
        // cl100k_base specifically (rather than CreateForModel) so that
        // a future ADR-0020 model swap that retains cl100k_base doesn't
        // require a chunker change. Tokenizer is thread-safe; cached for
        // the lifetime of the singleton.
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    public IReadOnlyList<Chunk> Chunk(
        ExtractedDocument document,
        ChunkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);

        if (document.Status != ExtractionStatus.Success)
        {
            // Defensive: callers should branch on Status before calling
            // the chunker, but if we're invoked anyway, return empty
            // rather than throwing — keeps the Cosmos Change Feed
            // Function (W3-2) pipeline straight-line without try/catch.
            _logger.LogDebug(
                "Chunk called with non-Success ExtractedDocument (Status={Status}); returning empty.",
                document.Status);
            return [];
        }

        if (document.Pages.Count == 0)
        {
            _logger.LogDebug("Chunk called with zero pages; returning empty.");
            return [];
        }

        var strippedPages = StripRepeatingHeadersFooters(document.Pages);
        var sections = PartitionIntoSections(strippedPages, document.Outline, request.DocumentType);

        if (sections.Count == 0)
        {
            _logger.LogInformation(
                "Document {DocumentId} produced zero non-empty sections after header/footer strip; emitting no chunks.",
                request.DocumentId);
            return [];
        }

        var chunks = new List<Chunk>(capacity: sections.Count * 2);
        var chunkIndex = 0;

        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunkIndex = ChunkSection(section, chunks, chunkIndex);
        }

        _logger.LogDebug(
            "Chunked document {DocumentId} ({DocumentType}) into {ChunkCount} chunks across {SectionCount} sections.",
            request.DocumentId, request.DocumentType, chunks.Count, sections.Count);

        return chunks;
    }

    // Detect lines that appear as the first (or last) non-empty line on
    // > HeaderFooterRepeatThreshold of pages and strip them. Returns a
    // new list of `ExtractedPage` with stripped text; the originals are
    // unchanged. Skipped entirely when the document has fewer than
    // HeaderFooterMinPages pages — too little signal to distinguish
    // boilerplate from content.
    private IReadOnlyList<ExtractedPage> StripRepeatingHeadersFooters(IReadOnlyList<ExtractedPage> pages)
    {
        if (pages.Count < _options.HeaderFooterMinPages)
        {
            return pages;
        }

        var firstLineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastLineCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            var (first, last) = FirstAndLastNonEmptyLines(page.Text);
            if (!string.IsNullOrEmpty(first))
            {
                firstLineCounts[first] = firstLineCounts.GetValueOrDefault(first) + 1;
            }
            if (!string.IsNullOrEmpty(last) && last != first)
            {
                lastLineCounts[last] = lastLineCounts.GetValueOrDefault(last) + 1;
            }
        }

        var threshold = (int)Math.Ceiling(pages.Count * _options.HeaderFooterRepeatThreshold);
        var headerLine = firstLineCounts
            .Where(kv => kv.Value >= threshold)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        var footerLine = lastLineCounts
            .Where(kv => kv.Value >= threshold)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        if (headerLine is null && footerLine is null)
        {
            return pages;
        }

        _logger.LogDebug(
            "Detected repeating header={HasHeader} footer={HasFooter} across {PageCount} pages; stripping.",
            headerLine is not null, footerLine is not null, pages.Count);

        var result = new List<ExtractedPage>(capacity: pages.Count);
        foreach (var page in pages)
        {
            result.Add(new ExtractedPage(page.PageNumber, StripPageBoilerplate(page.Text, headerLine, footerLine)));
        }
        return result;
    }

    // Partition pages into sections. Service bulletins (when
    // BulletinTreatAsSingleSection=true) collapse to a single section
    // regardless of outline. Manuals partition by outline entries —
    // each entry begins a new section running to the page before the
    // next entry. Pages before the first outline entry form a
    // leading "" section so front-matter content isn't dropped (cover
    // / TOC are typically thin but a manual occasionally has rules
    // text on page 2). Empty sections (overlapping outline entries on
    // the same page) are skipped.
    private IReadOnlyList<Section> PartitionIntoSections(
        IReadOnlyList<ExtractedPage> pages,
        IReadOnlyList<OutlineEntry> outline,
        DocumentType documentType)
    {
        var collapseToSingle =
            outline.Count == 0
            || (documentType == DocumentType.ServiceBulletin && _options.BulletinTreatAsSingleSection);

        if (collapseToSingle)
        {
            if (outline.Count == 0)
            {
                _logger.LogDebug("No outline entries; falling back to single-section chunking.");
            }
            else
            {
                _logger.LogInformation(
                    "Treating service bulletin as single section (BulletinTreatAsSingleSection=true); ignoring {EntryCount} outline entries.",
                    outline.Count);
            }
            var nonEmpty = pages.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
            return nonEmpty.Count == 0
                ? []
                : new[] { new Section(string.Empty, nonEmpty) };
        }

        var sections = new List<Section>();
        var totalPages = pages.Count == 0 ? 0 : pages[^1].PageNumber;
        var firstOutlinePage = outline[0].PageNumber;

        if (firstOutlinePage > 1)
        {
            var frontMatter = pages
                .Where(p => p.PageNumber < firstOutlinePage && !string.IsNullOrWhiteSpace(p.Text))
                .ToList();
            if (frontMatter.Count > 0)
            {
                sections.Add(new Section(string.Empty, frontMatter));
            }
        }

        for (var i = 0; i < outline.Count; i++)
        {
            var sectionStart = outline[i].PageNumber;
            var sectionEnd = (i + 1 < outline.Count) ? outline[i + 1].PageNumber - 1 : totalPages;

            if (sectionEnd < sectionStart)
            {
                // Overlapping entries (chapter heading on same page as
                // its first sub-section) collapse to zero pages —
                // accept the deeper entry's body and skip the parent.
                continue;
            }

            var sectionPages = pages
                .Where(p => p.PageNumber >= sectionStart && p.PageNumber <= sectionEnd && !string.IsNullOrWhiteSpace(p.Text))
                .ToList();
            if (sectionPages.Count == 0)
            {
                continue;
            }

            sections.Add(new Section(outline[i].Title, sectionPages));
        }

        return sections;
    }

    // Tokenize the section's concatenated page text once, then slide a
    // window of TargetTokens with step (TargetTokens − OverlapTokens)
    // across the token list. Each window's char-range maps back to a
    // page range via the per-page char offsets recorded during
    // concatenation. Returns the next chunk index to use.
    private int ChunkSection(Section section, List<Chunk> output, int startingIndex)
    {
        var (sectionText, pageMap) = BuildSectionText(section.Pages);
        if (string.IsNullOrWhiteSpace(sectionText))
        {
            return startingIndex;
        }

        var tokens = _tokenizer.EncodeToTokens(sectionText, out _, considerNormalization: false);
        if (tokens.Count == 0)
        {
            return startingIndex;
        }

        var step = Math.Max(1, _options.TargetTokens - _options.OverlapTokens);
        var chunkIndex = startingIndex;

        for (var windowStart = 0; windowStart < tokens.Count; windowStart += step)
        {
            var windowEnd = Math.Min(windowStart + _options.TargetTokens, tokens.Count);
            var charStart = tokens[windowStart].Offset.Start.Value;
            var charEnd = tokens[windowEnd - 1].Offset.End.Value;
            var chunkText = sectionText[charStart..charEnd];

            if (string.IsNullOrWhiteSpace(chunkText))
            {
                if (windowEnd >= tokens.Count) break;
                continue;
            }

            var pageStart = PageAtChar(pageMap, charStart);
            var pageEnd = PageAtChar(pageMap, charEnd - 1);

            var (finalText, finalTokenCount) = ApplyHeadingPrefixIfEnabled(
                section.Heading, chunkText, windowEnd - windowStart);

            output.Add(new Chunk(
                ChunkIndex: chunkIndex++,
                Text: finalText,
                SectionHeading: section.Heading,
                PageStart: pageStart,
                PageEnd: pageEnd,
                TokenCount: finalTokenCount));

            if (windowEnd >= tokens.Count)
            {
                break;
            }
        }

        return chunkIndex;
    }

    // Concatenate page texts with single-newline separators. Records
    // each page's char range in the concatenated string so the chunker
    // can map a token's char offset back to its source page (and thus
    // each chunk to a `(page_start, page_end)` for the citation
    // surface).
    private static (string Text, IReadOnlyList<PageMapEntry> Map) BuildSectionText(IReadOnlyList<ExtractedPage> pages)
    {
        var sb = new StringBuilder(capacity: 4096);
        var map = new List<PageMapEntry>(capacity: pages.Count);

        for (var i = 0; i < pages.Count; i++)
        {
            var charStart = sb.Length;
            sb.Append(pages[i].Text);
            var charEnd = sb.Length;
            map.Add(new PageMapEntry(pages[i].PageNumber, charStart, charEnd));
            if (i < pages.Count - 1)
            {
                sb.Append('\n');
            }
        }

        return (sb.ToString(), map);
    }

    // Given a char index in the concatenated section text, return the
    // 1-based page number it came from. The map is built in page
    // order, so the first entry whose CharEnd > charIdx is the answer.
    // For chars in the inter-page separator (a single '\n' between
    // CharEnd[i] and CharStart[i+1]), attribute to the preceding page.
    private static int PageAtChar(IReadOnlyList<PageMapEntry> map, int charIdx)
    {
        for (var i = 0; i < map.Count; i++)
        {
            if (charIdx < map[i].CharEnd)
            {
                return map[i].PageNumber;
            }
            // char index falls in the separator after page i; attribute
            // to page i (the closing context, not the opening context
            // of the next page).
            if (i + 1 < map.Count && charIdx < map[i + 1].CharStart)
            {
                return map[i].PageNumber;
            }
        }
        return map[^1].PageNumber;
    }

    private (string Text, int TokenCount) ApplyHeadingPrefixIfEnabled(
        string sectionHeading,
        string chunkText,
        int chunkBodyTokenCount)
    {
        if (!_options.ApplyHeadingPrefix || string.IsNullOrWhiteSpace(sectionHeading))
        {
            return (chunkText, chunkBodyTokenCount);
        }

        var prefix = $"## {sectionHeading.Trim()}\n\n";
        var prefixTokens = _tokenizer.CountTokens(prefix);
        return (prefix + chunkText, prefixTokens + chunkBodyTokenCount);
    }

    // Returns the first non-empty trimmed line and the last non-empty
    // trimmed line of `pageText`. Either may be empty if the page has
    // no non-whitespace content.
    private static (string First, string Last) FirstAndLastNonEmptyLines(string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return (string.Empty, string.Empty);
        }

        var lines = pageText.Split('\n');
        var first = string.Empty;
        var last = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0)
            {
                first = trimmed;
                break;
            }
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0)
            {
                last = trimmed;
                break;
            }
        }

        return (first, last);
    }

    // Strip the leading line if it matches the detected header, and
    // the trailing line if it matches the detected footer. Both
    // comparisons are case-sensitive ordinal — repeating headers on
    // real Stern / JJP / AP manuals are rendered consistently across
    // pages by the same PDF producer, so case + whitespace
    // normalization isn't needed for the detection target. Also
    // collapses leading/trailing whitespace lines that sit between
    // the boilerplate and the content.
    private static string StripPageBoilerplate(string pageText, string? header, string? footer)
    {
        if (string.IsNullOrEmpty(pageText) || (header is null && footer is null))
        {
            return pageText;
        }

        var lines = pageText.Split('\n');
        var (start, end) = TrimBoilerplateBounds(lines, header, footer);

        return (start == 0 && end == lines.Length)
            ? pageText
            : string.Join('\n', lines, start, end - start);
    }

    // Advances `start` past leading whitespace and the header line (if present),
    // and retreats `end` past trailing whitespace and the footer line (if present).
    private static (int Start, int End) TrimBoilerplateBounds(string[] lines, string? header, string? footer)
    {
        var start = 0;
        var end = lines.Length;

        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        if (start < end && header is not null && lines[start].Trim() == header) start++;
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;

        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        if (end > start && footer is not null && lines[end - 1].Trim() == footer) end--;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;

        return (start, end);
    }

    private sealed record Section(string Heading, IReadOnlyList<ExtractedPage> Pages);

    private sealed record PageMapEntry(int PageNumber, int CharStart, int CharEnd);
}
