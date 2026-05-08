using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Chunking;

// Behavior-asserting tests for HybridChunker (ADR-0019). Each test
// covers a behavior the build-spec § Phase 4 item 15 calls out, OR one
// of the three customer-delight refinements layered on top of the
// ADR baseline (header/footer strip; heading-prefix in chunk text;
// bulletin-as-single-section). Test fixtures construct ExtractedDocument
// values directly rather than round-tripping through PdfPig — keeps the
// chunker tests independent of extractor quirks per the build-spec
// § Phase 4 lesson 7 about programmatic fixtures for pure-transform
// components.
public sealed class HybridChunkerTests
{
    private static readonly ChunkRequest ManualRequest = new(
        MachineId: "mch_godzilla",
        Manufacturer: "Stern Pinball",
        DocumentId: "doc_godzilla_manual",
        DocumentUrl: "https://sternpinball.com/wp-content/uploads/godzilla_manual.pdf",
        DocumentType: DocumentType.Manual);

    private static readonly ChunkRequest BulletinRequest = new(
        MachineId: "mch_godzilla",
        Manufacturer: "Stern Pinball",
        DocumentId: "doc_godzilla_sb_001",
        DocumentUrl: "https://sternpinball.com/wp-content/uploads/sb_001.pdf",
        DocumentType: DocumentType.ServiceBulletin);

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HybridChunker(null!, NullLogger<HybridChunker>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HybridChunker(Options.Create(new ChunkerOptions()), null!));
    }

    [Fact]
    public void Chunk_NullDocument_Throws()
    {
        var sut = NewChunker();
        Assert.Throws<ArgumentNullException>(() =>
            sut.Chunk(null!, ManualRequest));
    }

    [Fact]
    public void Chunk_NullRequest_Throws()
    {
        var sut = NewChunker();
        var doc = MakeDoc(ExtractionStatus.Success, pages: [new ExtractedPage(1, "hello world")]);
        Assert.Throws<ArgumentNullException>(() => sut.Chunk(doc, null!));
    }

    [Fact]
    public void Chunk_NonSuccessStatus_ReturnsEmpty()
    {
        var sut = NewChunker();
        var doc = ExtractedDocument.Failure(ExtractionStatus.OcrRequired, "scanned only");

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_ZeroPages_ReturnsEmpty()
    {
        var sut = NewChunker();
        var doc = MakeDoc(ExtractionStatus.Success, pages: []);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_ShortDocNoOutline_EmitsSingleChunk()
    {
        // Bulletin-shaped: one short page, no outline. Should produce
        // exactly one chunk with the original text and empty heading.
        var sut = NewChunker();
        var doc = MakeDoc(
            ExtractionStatus.Success,
            pages: [new ExtractedPage(1, "Symptom: lower-left flipper sticks. Cause: dirty coil sleeve. Resolution: replace per kit 545-9999-00.")]);

        var chunks = sut.Chunk(doc, BulletinRequest);

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(string.Empty, chunk.SectionHeading);
        Assert.Equal(1, chunk.PageStart);
        Assert.Equal(1, chunk.PageEnd);
        Assert.Contains("Symptom", chunk.Text);
        Assert.Contains("Resolution", chunk.Text);
        Assert.True(chunk.TokenCount > 0);
    }

    [Fact]
    public void Chunk_LongDocNoOutline_WindowsAcrossPages()
    {
        // Manual-shaped fallback case (ADR-0019 § no-outline fallback).
        // Long enough text on multiple pages to force at least two
        // windows; assert that page numbers track across windows.
        var sut = NewChunker(targetTokens: 256, overlapTokens: 25);
        var pages = new List<ExtractedPage>();
        for (var i = 1; i <= 5; i++)
        {
            pages.Add(new ExtractedPage(i, RepeatingFillerParagraph($"Page {i} content. ", 600)));
        }
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.True(chunks.Count >= 2, $"Expected ≥2 chunks for ~{600 * 5} chars at 256-token budget; got {chunks.Count}.");
        // Page numbers monotonically non-decreasing across chunks.
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].PageStart >= chunks[i - 1].PageStart,
                $"Chunk {i} pageStart={chunks[i].PageStart} regressed below chunk {i - 1} pageStart={chunks[i - 1].PageStart}.");
        }
        // Last chunk's PageEnd must reach the last page.
        Assert.Equal(5, chunks[^1].PageEnd);
        // ChunkIndex is contiguous.
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
        }
    }

    [Fact]
    public void Chunk_LongDocWithOutline_PartitionsBySectionAndIncludesHeadings()
    {
        // Manual-shaped with three top-level chapters. Each section is
        // long enough to produce at least one chunk; assert chunks
        // group by section (no chunk spans an outline boundary).
        var sut = NewChunker(targetTokens: 256);
        var pages = new List<ExtractedPage>
        {
            new(1, "Cover page text — small, but not boilerplate."),
            new(2, RepeatingFillerParagraph("Setup section content. ", 800)),
            new(3, RepeatingFillerParagraph("Setup section continuation. ", 800)),
            new(4, RepeatingFillerParagraph("Game rules content. ", 800)),
            new(5, RepeatingFillerParagraph("Game rules continuation. ", 800)),
            new(6, RepeatingFillerParagraph("Service section content. ", 800)),
        };
        var outline = new[]
        {
            new OutlineEntry("Setup", 2, 0),
            new OutlineEntry("Game Rules", 4, 0),
            new OutlineEntry("Service", 6, 0),
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        // Every chunk's SectionHeading must be one of the three known
        // headings or empty (front-matter); never anything else.
        var allowedHeadings = new HashSet<string> { string.Empty, "Setup", "Game Rules", "Service" };
        foreach (var chunk in chunks)
        {
            Assert.Contains(chunk.SectionHeading, allowedHeadings);
        }
        // We expect at least one chunk per outline section.
        Assert.Contains(chunks, c => c.SectionHeading == "Setup");
        Assert.Contains(chunks, c => c.SectionHeading == "Game Rules");
        Assert.Contains(chunks, c => c.SectionHeading == "Service");
        // Chunks of "Setup" must reside on pages 2–3 only (never on
        // page 4+ which belongs to "Game Rules").
        foreach (var chunk in chunks.Where(c => c.SectionHeading == "Setup"))
        {
            Assert.InRange(chunk.PageStart, 2, 3);
            Assert.InRange(chunk.PageEnd, 2, 3);
        }
        // Chunks of "Service" must reside on page 6 only.
        foreach (var chunk in chunks.Where(c => c.SectionHeading == "Service"))
        {
            Assert.Equal(6, chunk.PageStart);
            Assert.Equal(6, chunk.PageEnd);
        }
    }

    [Fact]
    public void Chunk_TocOnlyOutline_FrontMatterAndOneSection()
    {
        // PDF whose only outline entry points at a TOC page near the
        // start. Pages before TOC become front-matter (empty heading);
        // pages from TOC onward become a single "Table of Contents"
        // section. Asserts both partitions emit chunks.
        var sut = NewChunker(targetTokens: 256);
        var pages = new List<ExtractedPage>
        {
            new(1, RepeatingFillerParagraph("Front matter text. ", 600)),
            new(2, RepeatingFillerParagraph("More front matter. ", 600)),
            new(3, RepeatingFillerParagraph("Table of contents listing chapter one chapter two chapter three. ", 600)),
            new(4, RepeatingFillerParagraph("More TOC content for completeness. ", 600)),
        };
        var outline = new[] { new OutlineEntry("Table of Contents", 3, 0) };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        // Have at least one front-matter (empty heading) chunk and at
        // least one TOC (non-empty heading) chunk.
        Assert.Contains(chunks, c => c.SectionHeading == string.Empty && c.PageStart < 3);
        Assert.Contains(chunks, c => c.SectionHeading == "Table of Contents" && c.PageStart >= 3);
    }

    [Fact]
    public void Chunk_OverlappingOutlineEntries_SkipsParentSection()
    {
        // Two outline entries on the same page (chapter heading + first
        // sub-section both at page 5). The parent section's computed
        // page range is empty; the chunker skips it and only emits
        // chunks for the child section.
        var sut = NewChunker(targetTokens: 256);
        var pages = new List<ExtractedPage>
        {
            new(1, "Front matter."),
            new(5, RepeatingFillerParagraph("Sub-section content here. ", 600)),
            new(6, RepeatingFillerParagraph("More sub-section content. ", 600)),
        };
        var outline = new[]
        {
            new OutlineEntry("Chapter 1 (parent)", 5, 0),
            new OutlineEntry("Section 1.1 (child)", 5, 1),
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.DoesNotContain(chunks, c => c.SectionHeading == "Chapter 1 (parent)");
        Assert.Contains(chunks, c => c.SectionHeading == "Section 1.1 (child)");
    }

    [Fact]
    public void Chunk_ApplyHeadingPrefixDefault_PrependsMarkdownHeading()
    {
        // Refinement #2: section heading prepended to chunk text as
        // markdown H2 so the embedding model sees "## Heading\n\n…"
        // for retrieval signal.
        var sut = NewChunker();
        var pages = new[]
        {
            new ExtractedPage(1, "intro"),
            new ExtractedPage(2, RepeatingFillerParagraph("Setup text content. ", 400)),
        };
        var outline = new[] { new OutlineEntry("Setup", 2, 0) };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        var setupChunk = Assert.Single(chunks, c => c.SectionHeading == "Setup");
        Assert.StartsWith("## Setup\n\n", setupChunk.Text);
    }

    [Fact]
    public void Chunk_HeadingPrefixDisabled_OmitsPrefix()
    {
        // Same fixture; prefix disabled.
        var sut = NewChunker(applyHeadingPrefix: false);
        var pages = new[]
        {
            new ExtractedPage(1, "intro"),
            new ExtractedPage(2, RepeatingFillerParagraph("Setup text content. ", 400)),
        };
        var outline = new[] { new OutlineEntry("Setup", 2, 0) };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        var setupChunk = Assert.Single(chunks, c => c.SectionHeading == "Setup");
        Assert.DoesNotContain("## Setup", setupChunk.Text);
        // Heading still present in metadata for citation surface.
        Assert.Equal("Setup", setupChunk.SectionHeading);
    }

    [Fact]
    public void Chunk_EmptySectionHeading_SkipsHeadingPrefix()
    {
        // Front-matter section (heading="") never gets a "## \n\n"
        // prefix even when ApplyHeadingPrefix is true.
        var sut = NewChunker();
        var doc = MakeDoc(
            ExtractionStatus.Success,
            pages: [new ExtractedPage(1, "Front matter content with no outline at all.")]);

        var chunks = sut.Chunk(doc, ManualRequest);

        var chunk = Assert.Single(chunks);
        Assert.DoesNotContain("##", chunk.Text);
    }

    [Fact]
    public void Chunk_RepeatingHeader_StrippedFromChunkText()
    {
        // Refinement #1: a header line that appears on > 50% of pages
        // is detected and stripped before chunking. Without the strip,
        // every chunk's text would contain the boilerplate.
        var sut = NewChunker();
        const string header = "STERN PINBALL — GODZILLA OPERATING MANUAL";
        var pages = new List<ExtractedPage>();
        for (var i = 1; i <= 5; i++)
        {
            pages.Add(new ExtractedPage(i,
                $"{header}\n\nUnique page {i} content describing rules and gameplay."));
        }
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.DoesNotContain(header, chunk.Text);
        }
        // Content must still be there.
        Assert.Contains(chunks, c => c.Text.Contains("page 1 content"));
        Assert.Contains(chunks, c => c.Text.Contains("page 5 content"));
    }

    [Fact]
    public void Chunk_RepeatingFooter_StrippedFromChunkText()
    {
        var sut = NewChunker();
        const string footer = "Confidential — Stern Pinball Service Documentation";
        var pages = new List<ExtractedPage>();
        for (var i = 1; i <= 4; i++)
        {
            pages.Add(new ExtractedPage(i,
                $"Body text for page {i} with rules and gameplay details.\n\n{footer}"));
        }
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.DoesNotContain(footer, chunk.Text);
        }
    }

    [Fact]
    public void Chunk_BelowMinPagesForDetection_LeavesHeaderInPlace()
    {
        // Header detection skipped when fewer than HeaderFooterMinPages
        // (default 3) — too little signal to distinguish boilerplate
        // from content. A 2-page bulletin's repeating-looking line
        // stays in chunk text.
        var sut = NewChunker();
        const string maybeHeader = "STERN SERVICE BULLETIN SB-1234";
        var pages = new List<ExtractedPage>
        {
            new(1, $"{maybeHeader}\n\nPage 1 body."),
            new(2, $"{maybeHeader}\n\nPage 2 body."),
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages);

        var chunks = sut.Chunk(doc, BulletinRequest);

        Assert.Contains(chunks, c => c.Text.Contains(maybeHeader));
    }

    [Fact]
    public void Chunk_ServiceBulletinDefault_TreatedAsSingleSectionRegardlessOfOutline()
    {
        // Refinement #3: bulletins skip outline partitioning by default.
        // A bulletin with sub-headings still gets one chunk per
        // document (assuming it fits the budget) — important so a
        // query for "X symptom" retrieves the whole bulletin, not
        // just the Symptom paragraph.
        var sut = NewChunker(targetTokens: 512);
        var pages = new[]
        {
            new ExtractedPage(1, "Symptom: flipper coil overheats. Cause: stuck switch. Resolution: clean the EOS contact."),
        };
        var outline = new[]
        {
            new OutlineEntry("Symptom", 1, 0),
            new OutlineEntry("Cause", 1, 0),
            new OutlineEntry("Resolution", 1, 0),
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, BulletinRequest);

        var chunk = Assert.Single(chunks);
        Assert.Equal(string.Empty, chunk.SectionHeading);
        Assert.Contains("Symptom", chunk.Text);
        Assert.Contains("Cause", chunk.Text);
        Assert.Contains("Resolution", chunk.Text);
    }

    [Fact]
    public void Chunk_BulletinSingleSectionDisabled_RespectsOutlineLikeManual()
    {
        // Same bulletin fixture; refinement #3 turned off — chunker
        // falls back to ADR-0019 strict section-bounded chunking.
        var sut = NewChunker(bulletinTreatAsSingleSection: false);
        var pages = new[]
        {
            new ExtractedPage(1, RepeatingFillerParagraph("Symptom of issue. ", 200)),
            new ExtractedPage(2, RepeatingFillerParagraph("Cause analysis. ", 200)),
            new ExtractedPage(3, RepeatingFillerParagraph("Resolution steps. ", 200)),
        };
        var outline = new[]
        {
            new OutlineEntry("Symptom", 1, 0),
            new OutlineEntry("Cause", 2, 0),
            new OutlineEntry("Resolution", 3, 0),
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, BulletinRequest);

        Assert.Contains(chunks, c => c.SectionHeading == "Symptom");
        Assert.Contains(chunks, c => c.SectionHeading == "Cause");
        Assert.Contains(chunks, c => c.SectionHeading == "Resolution");
    }

    [Fact]
    public void Chunk_OutlinePageBeyondTotalPages_SkipsThatSectionGracefully()
    {
        // Defensive: a malformed outline (PdfPig occasionally surfaces
        // bookmarks with stale page numbers from before pages were
        // reflowed) shouldn't blow up the chunker — empty-section
        // skip handles it. Test pins this so a future "throw on bad
        // outline" change is a conscious choice.
        var sut = NewChunker(targetTokens: 256);
        var pages = new[]
        {
            new ExtractedPage(1, RepeatingFillerParagraph("Setup section content. ", 600)),
            new ExtractedPage(2, RepeatingFillerParagraph("Setup continued. ", 600)),
        };
        var outline = new[]
        {
            new OutlineEntry("Setup", 1, 0),
            new OutlineEntry("Phantom", 99, 0), // points past the last page
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.Contains(chunks, c => c.SectionHeading == "Setup");
        Assert.DoesNotContain(chunks, c => c.SectionHeading == "Phantom");
    }

    [Fact]
    public void Chunk_OutOfOrderOutline_SkipsInvertedSection()
    {
        // Defensive: out-of-order outline entries (entry[i+1].page <
        // entry[i].page) produce an inverted section whose page range
        // is empty — must skip rather than emit a malformed chunk.
        // Cosmetic outline corruption shouldn't break ingestion.
        var sut = NewChunker(targetTokens: 256);
        var pages = new[]
        {
            new ExtractedPage(1, RepeatingFillerParagraph("Page 1 content. ", 600)),
            new ExtractedPage(2, RepeatingFillerParagraph("Page 2 content. ", 600)),
            new ExtractedPage(3, RepeatingFillerParagraph("Page 3 content. ", 600)),
        };
        var outline = new[]
        {
            new OutlineEntry("Later", 3, 0),
            new OutlineEntry("Earlier", 1, 0), // out of order
        };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        // "Later" is inverted (its computed end = "Earlier".PageNumber-1 = 0,
        // less than its start of 3) — skipped.
        Assert.DoesNotContain(chunks, c => c.SectionHeading == "Later");
        // "Earlier" still emits chunks (last entry, runs to end of doc).
        Assert.Contains(chunks, c => c.SectionHeading == "Earlier");
    }

    [Fact]
    public void Chunk_OverlapTokens_ConsecutiveChunksShareContent()
    {
        // ADR-0019 § Algorithm step 3: ~10% overlap between consecutive
        // chunks within a section. Asserts the overlap is more than
        // zero by comparing tail-of-chunk-N with head-of-chunk-N+1.
        var sut = NewChunker(targetTokens: 64, overlapTokens: 16);
        var doc = MakeDoc(
            ExtractionStatus.Success,
            pages: [new ExtractedPage(1, RepeatingFillerParagraph("uniqueword{n} ", 1500))]);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.True(chunks.Count >= 2, "Expected multiple chunks for the overlap assertion to be meaningful.");
        // Take last 50 chars of chunk 0; assert any of those words also
        // appear in chunk 1's first 200 chars.
        var tail = chunks[0].Text[^Math.Min(50, chunks[0].Text.Length)..];
        var head = chunks[1].Text[..Math.Min(200, chunks[1].Text.Length)];
        // The overlap region should share at least one whole word.
        var tailWords = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(tailWords, w => w.Length > 3 && head.Contains(w));
    }

    [Fact]
    public void Chunk_TokenCount_IsBoundedByTargetPlusHeadingPrefix()
    {
        // Sanity: each chunk's TokenCount stays within a small slop
        // above TargetTokens (heading prefix is added but bounded).
        var sut = NewChunker(targetTokens: 256);
        var pages = new[]
        {
            new ExtractedPage(1, RepeatingFillerParagraph("Setup section content. ", 1500)),
        };
        var outline = new[] { new OutlineEntry("Setup", 1, 0) };
        var doc = MakeDoc(ExtractionStatus.Success, pages: pages, outline: outline);

        var chunks = sut.Chunk(doc, ManualRequest);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            // Allow up to 32-token slop for heading-prefix overhead.
            Assert.True(chunk.TokenCount <= 256 + 32,
                $"Chunk token count {chunk.TokenCount} exceeded budget+slop.");
        }
    }

    [Fact]
    public void Chunk_CancelledToken_Throws()
    {
        var sut = NewChunker();
        var doc = MakeDoc(
            ExtractionStatus.Success,
            pages: Enumerable.Range(1, 5)
                .Select(i => new ExtractedPage(i, RepeatingFillerParagraph($"Page {i} content. ", 600)))
                .ToList());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => sut.Chunk(doc, ManualRequest, cts.Token));
    }

    // --- Fixture helpers ----------------------------------------------------

    private static HybridChunker NewChunker(
        int? targetTokens = null,
        int? overlapTokens = null,
        bool? applyHeadingPrefix = null,
        bool? bulletinTreatAsSingleSection = null)
    {
        var options = new ChunkerOptions();
        if (targetTokens is { } t) options.TargetTokens = t;
        if (overlapTokens is { } o) options.OverlapTokens = o;
        if (applyHeadingPrefix is { } p) options.ApplyHeadingPrefix = p;
        if (bulletinTreatAsSingleSection is { } b) options.BulletinTreatAsSingleSection = b;
        return new HybridChunker(Options.Create(options), NullLogger<HybridChunker>.Instance);
    }

    private static ExtractedDocument MakeDoc(
        ExtractionStatus status,
        IReadOnlyList<ExtractedPage>? pages = null,
        IReadOnlyList<OutlineEntry>? outline = null)
    {
        var pageList = pages ?? [];
        return new ExtractedDocument(
            Status: status,
            Text: string.Join("\n", pageList.Select(p => p.Text)),
            Pages: pageList,
            Outline: outline ?? [],
            Error: null);
    }

    // Repeats `paragraph` until the resulting string reaches
    // `targetLengthChars`. Used to generate enough text to exercise the
    // chunker's windowing logic without committing big test fixtures.
    private static string RepeatingFillerParagraph(string paragraph, int targetLengthChars)
    {
        var sb = new StringBuilder(capacity: targetLengthChars);
        while (sb.Length < targetLengthChars)
        {
            sb.Append(paragraph);
        }
        return sb.ToString();
    }
}
