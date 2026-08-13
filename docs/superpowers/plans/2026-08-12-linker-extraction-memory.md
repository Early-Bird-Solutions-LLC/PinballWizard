# Linker Extraction Memory (#832) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the nightly linker's memory flat with respect to document size and count so a 42 MB PDF (`doc_4ea5f0c438428b8b`) no longer OOMs the 0.5 vCPU / 1 GiB ACA job — and build the page-tier regression gate that can actually prove the refactor changed no linking behaviour.

**Architecture:** Three unbounded memory sinks are closed at their sources: blob reads become temp-file-backed instead of `MemoryStream`-buffered (`BlobDocumentStore`); the linker stops materializing whole documents to read two pages (new narrow `IDocumentPreviewExtractor` returning a distinct `ExtractedPreview`); and extraction concurrency gets its own bound (`SemaphoreSlim(4)`) decoupled from `CosmosWriteConcurrency=20`. A size guard moves upstream of the download (`GetSizeAsync` before any transfer). A new capture verb + page-text replay fixture arms tiers 3/4 with offline regression coverage for the first time.

**Tech Stack:** .NET 10, PdfPig 0.1.15 (`PdfDocument.GetPage` is lazy per page — verified at tag v0.1.15), Azure.Storage.Blobs 12.29.1, xUnit + NSubstitute, System.CommandLine (CLI).

**Spec (authoritative):** `docs/superpowers/specs/2026-08-12-linker-extraction-memory-design.md` — read it before starting any task. Where this plan and the spec disagree, the spec wins; report the divergence.

## Global Constraints

- Work ONLY in the worktree `.claude/worktrees/Dev-Issue832-LinkerExtractionMemory` (branch `Dev-Issue832-LinkerExtractionMemory`). Never touch the main tree.
- Every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>` (repo invariant). **No Claude attribution trailer.** Use the exact `git -c` flags shown in each commit step.
- Conventional commit titles: `<type>(<scope>) <imperative summary>` — types `feat|fix|refactor|docs|test|chore`.
- Do NOT raise container memory anywhere — explicitly forbidden by `.claude/rules/timeout-debugging.md` and the spec's Non-goals.
- `PdfExtractionOptions.MaxStreamBytes` stays the single source of the size threshold. Never introduce a second constant with the value `100L * 1024 * 1024` — reference `PdfExtractionOptions.DefaultMaxStreamBytes` (created in Task 1).
- New counter name is exactly `pinwiz.linker.extraction_skipped_total`, tag key `reason`, tag values `size_exceeded` | `blob_missing` | `extract_failed`.
- Do not modify `IDocumentTextExtractor` or any RAG-ingestion code path (`ScrapedDocumentIngestionPipeline`, `BlobDocumentBytesSource` behaviour) — Task 4 only corrects a stale *comment* in `BlobDocumentBytesSource`.
- Tests: never mask a failure with a skip (`.claude/rules/no-masking-skips.md`). The only permitted skip patterns are the pre-existing `[RequiresAzuriteFact]` and `[RequiresCapturedFixtureFact]` attributes.
- Run tests with `dotnet test <project> -c Release`. Full-suite command: `dotnet test PinballWizard.slnx -c Release`.
- Destructive git verbs are forbidden: no `git stash drop/pop/clear`, `git checkout -- .`, `git reset --hard`, `git clean`, force-push, branch deletion.

---

### Task 1: `ExtractedPreview` type + `IDocumentPreviewExtractor` interface (Application contract)

**Files:**
- Create: `src/PinballWizard.Application/Rag/Extraction/ExtractedPreview.cs`
- Create: `src/PinballWizard.Application/Rag/Extraction/IDocumentPreviewExtractor.cs`
- Modify: `src/PinballWizard.Application/Rag/Extraction/PdfExtractionOptions.cs:31-32` (hoist the default into a named const)
- Test: `tests/PinballWizard.Application.Tests/Rag/Extraction/ExtractedPreviewTests.cs`

**Interfaces:**
- Consumes: existing `ExtractionStatus` enum and `ExtractedPage` record (`src/PinballWizard.Application/Rag/Extraction/ExtractionStatus.cs`, `ExtractedDocument.cs:33`).
- Produces: `IDocumentPreviewExtractor.ExtractPreviewAsync(Stream pdfStream, int pageCount, CancellationToken cancellationToken)` returning `Task<ExtractedPreview>`; `ExtractedPreview(ExtractionStatus Status, IReadOnlyList<ExtractedPage> Pages, string? Error)` with static `Failure(ExtractionStatus, string)`; `PdfExtractionOptions.DefaultMaxStreamBytes` const. Tasks 2, 3, 5, 6, 7, 8 all consume these exact names.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Application.Tests/Rag/Extraction/ExtractedPreviewTests.cs`:

```csharp
using PinballWizard.Application.Rag.Extraction;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Extraction;

public sealed class ExtractedPreviewTests
{
    [Fact]
    public void Failure_ProducesEmptyPagesAndCarriesError()
    {
        var result = ExtractedPreview.Failure(ExtractionStatus.Malformed, "boom");

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
        Assert.Empty(result.Pages);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public void DefaultMaxStreamBytes_MatchesOptionsPropertyDefault()
    {
        // Guards the single-source-of-threshold constraint: the const the
        // DocumentLinker ctor defaults to must be the same value the options
        // property defaults to. If someone edits one and not the other, this fails.
        Assert.Equal(PdfExtractionOptions.DefaultMaxStreamBytes, new PdfExtractionOptions().MaxStreamBytes);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~ExtractedPreviewTests"`
Expected: FAIL to compile — `ExtractedPreview` and `DefaultMaxStreamBytes` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/PinballWizard.Application/Rag/Extraction/ExtractedPreview.cs`:

```csharp
namespace PinballWizard.Application.Rag.Extraction;

// The result of IDocumentPreviewExtractor.ExtractPreviewAsync (#832).
//
// Deliberately NOT an ExtractedDocument: a preview carries only the first N
// pages — no whole-document Text, no Outline — so a truncated parse is
// type-incompatible with the chunking/indexing path and can never be indexed
// as if complete. The bad state is unrepresentable, not merely discouraged.
public sealed record ExtractedPreview(
    ExtractionStatus Status,
    IReadOnlyList<ExtractedPage> Pages,
    string? Error)
{
    public static ExtractedPreview Failure(ExtractionStatus status, string error) => new(
        Status: status,
        Pages: [],
        Error: error);
}
```

Create `src/PinballWizard.Application/Rag/Extraction/IDocumentPreviewExtractor.cs`:

```csharp
namespace PinballWizard.Application.Rag.Extraction;

// Page-limited PDF text extraction for callers that consume only the first
// few pages (#832 — DocumentLinker reads pages 1-2 for its page tiers).
//
// This is a deliberately narrow sibling of IDocumentTextExtractor, NOT a
// method on it: the ADI extractor cannot honor a memory bound (its
// ReadToBytesAsync materializes the whole blob before the page-range
// parameter limits anything), and FallbackDocumentTextExtractor delegates to
// ADI only on OcrRequired — a status the preview path never produces. A
// required method there would be dead code satisfying a contract it cannot
// honor. Only PdfPigDocumentTextExtractor implements this.
//
// Producible Status values are exactly: Success, Encrypted, Malformed,
// SizeExceeded. OcrRequired (heuristic deliberately skipped — an empty
// preview simply yields no linking evidence) and OcrFailed (no ADI in this
// path) never appear.
public interface IDocumentPreviewExtractor
{
    // Extract text from the first `pageCount` pages (1-based, capped at the
    // document's page count). The stream is read but not disposed; it MUST
    // be seekable (PDF cross-reference parsing needs random access).
    Task<ExtractedPreview> ExtractPreviewAsync(
        Stream pdfStream,
        int pageCount,
        CancellationToken cancellationToken);
}
```

In `src/PinballWizard.Application/Rag/Extraction/PdfExtractionOptions.cs`, add the const and reference it (replacing the literal on the property initializer at line 32):

```csharp
    // Single source of the extraction size threshold (#832): the options
    // property, the DocumentLinker ctor default, and the upstream size guard
    // all reference this const — never a second literal.
    public const long DefaultMaxStreamBytes = 100L * 1024 * 1024;

    [Range(typeof(long), "1024", "9223372036854775807")]
    public long MaxStreamBytes { get; set; } = DefaultMaxStreamBytes;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~ExtractedPreviewTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Rag/Extraction/ExtractedPreview.cs \
        src/PinballWizard.Application/Rag/Extraction/IDocumentPreviewExtractor.cs \
        src/PinballWizard.Application/Rag/Extraction/PdfExtractionOptions.cs \
        tests/PinballWizard.Application.Tests/Rag/Extraction/ExtractedPreviewTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(linking) add IDocumentPreviewExtractor contract for page-limited extraction (#832)"
```

---

### Task 2: PdfPig implements `ExtractPreviewAsync`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Rag/Extraction/PdfPigDocumentTextExtractor.cs:21` (class declaration + new members)
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/Extraction/PdfPigDocumentTextExtractorTests.cs`

**Interfaces:**
- Consumes: `IDocumentPreviewExtractor`, `ExtractedPreview` (Task 1); existing `PdfExtractionOptions.MaxStreamBytes`, `ExtractedPage`.
- Produces: `PdfPigDocumentTextExtractor : IDocumentTextExtractor, IDocumentPreviewExtractor`. Task 3 registers it; Tasks 5/7 call `ExtractPreviewAsync(stream, 2, ct)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/PinballWizard.Infrastructure.Tests/Rag/Extraction/PdfPigDocumentTextExtractorTests.cs` (the file already has `NewExtractor(...)` and `BuildPdfWithText(...)` helpers at lines 227-274 — reuse them; add the multi-page builder below next to `BuildPdfWithText`):

```csharp
    // --- ExtractPreviewAsync (#832) -------------------------------------------

    [Fact]
    public async Task ExtractPreviewAsync_ThreePagePdf_ReturnsExactlyTwoPages()
    {
        var pdfBytes = BuildPdfWithPages("Page one text about Godzilla.", "Page two text.", "Page three text.");
        var extractor = NewExtractor();
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(2, result.Pages[1].PageNumber);
        Assert.Contains("Godzilla", result.Pages[0].Text);
    }

    [Fact]
    public async Task ExtractPreviewAsync_SinglePagePdf_ReturnsOnePage_NotAnError()
    {
        var pdfBytes = BuildPdfWithText("Only page.");
        var extractor = NewExtractor();
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Single(result.Pages);
    }

    [Fact]
    public async Task ExtractPreviewAsync_OversizeStream_ReturnsSizeExceededWithoutParsing()
    {
        var pdfBytes = BuildPdfWithText("Content irrelevant; the guard fires on stream length.");
        var extractor = NewExtractor(maxStreamBytes: 16);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.SizeExceeded, result.Status);
        Assert.Contains("MaxStreamBytes", result.Error);
    }

    [Fact]
    public async Task ExtractPreviewAsync_GarbageBytes_ReturnsMalformed()
    {
        var extractor = NewExtractor();
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task ExtractPreviewAsync_NearEmptyText_ReturnsSuccess_NeverOcrRequired()
    {
        // The preview path deliberately skips the OcrRequiredCharFloor heuristic
        // (spec Section B): an empty preview yields no linking evidence and the
        // page tier declines — that is the honest outcome. OcrRequired belongs
        // to the indexing path only.
        var pdfBytes = BuildPdfWithText("x");
        var extractor = NewExtractor(ocrRequiredCharFloor: 10000);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.NotEqual(ExtractionStatus.OcrRequired, result.Status);
    }

    [Fact]
    public async Task ExtractPreviewAsync_PageCountBelowOne_Throws()
    {
        var extractor = NewExtractor();
        using var stream = new MemoryStream(BuildPdfWithText("content"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => extractor.ExtractPreviewAsync(stream, pageCount: 0, CancellationToken.None));
    }
```

Add the multi-page fixture helper next to `BuildPdfWithText` (same section, same style):

```csharp
    private static byte[] BuildPdfWithPages(params string[] pageTexts)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(width: 612, height: 792); // US Letter
            page.AddText(text, fontSize: 12, position: new(50, 700), font: font);
        }
        return builder.Build();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests -c Release --filter "FullyQualifiedName~PdfPigDocumentTextExtractorTests"`
Expected: FAIL to compile — `ExtractPreviewAsync` does not exist on the extractor.

- [ ] **Step 3: Implement `ExtractPreviewAsync`**

In `PdfPigDocumentTextExtractor.cs`: change the class declaration (line 21) to

```csharp
public sealed class PdfPigDocumentTextExtractor : IDocumentTextExtractor, IDocumentPreviewExtractor
```

and add after the existing `ExtractAsync` method (keep `Extract` untouched):

```csharp
    // #832 page-limited preview for DocumentLinker's page tiers. Verified
    // against PdfPig v0.1.15 (src/UglyToad.PdfPig/Content/Pages.cs): GetPage
    // resolves a single page node and calls pageFactory.Create for that page
    // only — construction is strictly on demand, no page cache — so requesting
    // two pages parses two pages, not all of them. PdfDocument.Open itself
    // reads only xref + catalog.
    //
    // Producible statuses: Success, Encrypted, Malformed, SizeExceeded — see
    // IDocumentPreviewExtractor. The OcrRequiredCharFloor heuristic is
    // deliberately NOT applied here: an empty preview yields no linking
    // evidence and the tier declines, which is the honest outcome (the
    // heuristic belongs to the indexing path).
    public Task<ExtractedPreview> ExtractPreviewAsync(
        Stream pdfStream,
        int pageCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);

        // Same pre-parse size guard as ExtractAsync — defence in depth behind
        // the linker's upstream GetSizeAsync check (spec Section C). One
        // threshold, two enforcement points, zero duplicated constants.
        if (pdfStream.CanSeek && pdfStream.Length > _options.MaxStreamBytes)
        {
            var bytes = pdfStream.Length;
            _logger.LogWarning(
                "PDF stream length {StreamBytes} exceeds MaxStreamBytes={MaxStreamBytes}; rejecting preview before parse.",
                bytes, _options.MaxStreamBytes);
            return Task.FromResult(ExtractedPreview.Failure(
                ExtractionStatus.SizeExceeded,
                $"PDF stream length {bytes} bytes exceeds MaxStreamBytes={_options.MaxStreamBytes}; rejected to bound memory usage."));
        }

        return Task.Run(() => ExtractPreview(pdfStream, pageCount), cancellationToken);
    }

    private ExtractedPreview ExtractPreview(Stream pdfStream, int pageCount)
    {
        // Same single-try posture as Extract (see the comment there): PdfPig
        // can throw mid-parse on malformed-but-openable PDFs, and the
        // structured-result-on-failure contract must hold for every operation
        // that touches the document.
        try
        {
            using var document = PdfDocument.Open(pdfStream);

            var n = Math.Min(pageCount, document.NumberOfPages);
            var pages = new List<ExtractedPage>(capacity: n);
            for (var i = 1; i <= n; i++)
            {
                var page = document.GetPage(i);
                pages.Add(new ExtractedPage(page.Number, page.Text ?? string.Empty));
            }

            return new ExtractedPreview(ExtractionStatus.Success, pages, Error: null);
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException ex)
        {
            _logger.LogWarning(ex, "PDF is encrypted; preview skipped.");
            return ExtractedPreview.Failure(ExtractionStatus.Encrypted, $"PDF is encrypted: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PdfPig failed to parse the document for preview; classifying as Malformed.");
            return ExtractedPreview.Failure(ExtractionStatus.Malformed, $"PdfPig parse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests -c Release --filter "FullyQualifiedName~PdfPigDocumentTextExtractorTests"`
Expected: PASS — all pre-existing `ExtractAsync` tests still green plus the 6 new preview tests.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Extraction/PdfPigDocumentTextExtractor.cs \
        tests/PinballWizard.Infrastructure.Tests/Rag/Extraction/PdfPigDocumentTextExtractorTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(linking) implement lazy page-limited ExtractPreviewAsync on PdfPig extractor (#832)"
```

---

### Task 3: DI registration in both branches + resolvability tests

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Rag/Extraction/ServiceCollectionExtensions.cs:29-44`
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/Extraction/ExtractionServiceCollectionTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentPreviewExtractor` (Task 1), `PdfPigDocumentTextExtractor` (Task 2).
- Produces: `AddPdfDocumentTextExtractor` guarantees `IDocumentPreviewExtractor` resolves (to the same PdfPig singleton) whenever the extraction module is registered — with or without ADI. Task 5's linker factory relies on this invariant.

**Why this task exists as its own gate (spec Section B, "load-bearing — do not skip"):** the linker resolves the preview extractor optionally (`GetService`). A missed registration would not throw at startup; it would silently disable extraction in production while every unit test (which constructs fakes directly) stays green. This task makes "extraction module present ⇒ preview resolvable" a tested invariant.

- [ ] **Step 1: Write the failing tests**

Create `tests/PinballWizard.Infrastructure.Tests/Rag/Extraction/ExtractionServiceCollectionTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Extraction;

// #832 DI-resolvability gate. The linker resolves IDocumentPreviewExtractor
// with GetService (optional — scraper-only CLI mode legitimately runs without
// extraction wiring), so a missed registration fails SILENTLY: startup does
// not throw, unit tests construct fakes directly and stay green, and in
// production every page-tier document quietly falls to not_in_catalog. These
// tests make "extraction module registered ⇒ preview resolvable" an invariant.
public sealed class ExtractionServiceCollectionTests
{
    private static ServiceProvider Build(bool withAdiEndpoint)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(withAdiEndpoint
                ? new Dictionary<string, string?> { [DocumentIntelligenceOptions.EndpointKey] = "https://adi.example.invalid/" }
                : [])
            .Build();
        services.AddPdfDocumentTextExtractor(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewExtractor_Resolves_InBothBranches(bool withAdiEndpoint)
    {
        using var sp = Build(withAdiEndpoint);

        var preview = sp.GetService<IDocumentPreviewExtractor>();

        Assert.NotNull(preview);
        Assert.IsType<PdfPigDocumentTextExtractor>(preview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewExtractor_IsSamePdfPigSingletonAsConcreteRegistration(bool withAdiEndpoint)
    {
        using var sp = Build(withAdiEndpoint);

        var preview = sp.GetRequiredService<IDocumentPreviewExtractor>();
        var concrete = sp.GetRequiredService<PdfPigDocumentTextExtractor>();

        Assert.Same(concrete, preview);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests -c Release --filter "FullyQualifiedName~ExtractionServiceCollectionTests"`
Expected: FAIL — `PreviewExtractor_Resolves_InBothBranches` asserts `NotNull` on an unregistered service.

- [ ] **Step 3: Add the registration**

In `AddPdfDocumentTextExtractor` (`ServiceCollectionExtensions.cs`), insert immediately after `services.TryAddSingleton<PdfPigDocumentTextExtractor>();` (line 30) — BEFORE the ADI branch so it applies to both:

```csharp
        // #832: the preview interface always maps to the PdfPig singleton,
        // in BOTH branches. Only PdfPig can honor a page/memory bound (ADI's
        // ReadToBytesAsync materializes the whole blob before its page-range
        // parameter limits anything), and the fallback decorator would never
        // route a preview to ADI anyway (it fires only on OcrRequired, which
        // the preview path never returns). Registered here — not in each
        // branch — so "extraction module present ⇒ preview resolvable" is
        // structural. DocumentLinker resolves this with GetService; a missed
        // registration would disable page tiers silently (see
        // ExtractionServiceCollectionTests).
        services.TryAddSingleton<IDocumentPreviewExtractor>(sp =>
            sp.GetRequiredService<PdfPigDocumentTextExtractor>());
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests -c Release --filter "FullyQualifiedName~ExtractionServiceCollectionTests"`
Expected: PASS (4 test cases).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Extraction/ServiceCollectionExtensions.cs \
        tests/PinballWizard.Infrastructure.Tests/Rag/Extraction/ExtractionServiceCollectionTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(linking) register IDocumentPreviewExtractor in both extraction branches (#832)"
```

---

### Task 4: Temp-file-backed blob reads in `BlobDocumentStore`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Documents/BlobDocumentStore.cs:7-19` (header comment), `:82-128` (`OpenReadAsync` / `TryOpenReadAsync`)
- Modify: `src/PinballWizard.Infrastructure/Rag/Ingestion/BlobDocumentBytesSource.cs:105-107` (stale comment only — no behaviour change)
- Test: `tests/PinballWizard.Infrastructure.Tests/Documents/BlobDocumentStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `OpenReadAsync`/`TryOpenReadAsync` keep their exact signatures and 404 contracts (`Task<Stream>` throws on 404; `Task<Stream?>` returns null on 404) but return a temp-file-backed `FileStream` (seekable, positioned at 0, deleted on dispose). Both existing callers (`DocumentLinker`, `BlobDocumentBytesSource`, `DocumentDownloadService` SHA backfill) consume plain `Stream` inside `await using` — no call-site changes.

- [ ] **Step 1: Write the failing tests**

Append to `tests/PinballWizard.Infrastructure.Tests/Documents/BlobDocumentStoreTests.cs` (Azurite-gated, same `[RequiresAzuriteFact]` + container-per-test pattern as `WriteThenOpenRead_RoundTripsBytes` at lines 62-90):

```csharp
    // --- #832 temp-file backing ------------------------------------------------

    [RequiresAzuriteFact]
    public async Task OpenReadAsync_ReturnsSeekableFileStream_NotMemoryStream_AndDeletesTempFileOnDispose()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;
        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerClient = serviceClient.GetBlobContainerClient($"test-{Guid.NewGuid():N}");
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);
            var expected = new byte[] { 10, 20, 30, 40 };
            using (var writeStream = new MemoryStream(expected))
                await sut.WriteAsync("temp-backed.bin", writeStream, CancellationToken.None);

            string tempPath;
            var stream = await sut.OpenReadAsync("temp-backed.bin", CancellationToken.None);
            await using (stream)
            {
                // The whole point of #832: the blob must NOT be materialized on
                // the heap. A FileStream is the contract; IsNotType<MemoryStream>
                // is the regression tripwire.
                Assert.IsNotType<MemoryStream>(stream);
                var fileStream = Assert.IsType<FileStream>(stream);
                tempPath = fileStream.Name;

                Assert.True(stream.CanSeek);
                Assert.Equal(0, stream.Position);
                var actual = new byte[expected.Length];
                await stream.ReadExactlyAsync(actual);
                Assert.Equal(expected, actual);
            }

            // DeleteOnClose semantics: the temp file must be gone after dispose.
            // (On Linux the unlink happens at dispose via SafeFileHandle
            // .ReleaseHandle — not at open — so this asserts the only
            // cross-platform guarantee: absence AFTER dispose.)
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    [RequiresAzuriteFact]
    public async Task TryOpenReadAsync_MissingBlob_StillReturnsNull_AndLeavesNoTempFile()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;
        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerClient = serviceClient.GetBlobContainerClient($"test-{Guid.NewGuid():N}");
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var result = await sut.TryOpenReadAsync("does-not-exist.pdf", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }
```

- [ ] **Step 2: Run tests to verify the new type assertion fails**

Run: `AZURITE_BLOB_SERVICE_URL=<your Azurite URL> dotnet test tests/PinballWizard.Infrastructure.Tests -c Release --filter "FullyQualifiedName~BlobDocumentStoreTests"`
Expected: `OpenReadAsync_ReturnsSeekableFileStream...` FAILS on `Assert.IsNotType<MemoryStream>` (current implementation returns MemoryStream). `TryOpenReadAsync_MissingBlob...` already passes (contract unchanged).

If no Azurite is available locally, start the Aspire AppHost (`start-apphost.ps1`) or a `mcr.microsoft.com/azure-storage/azurite` container and point the env var at it. Do NOT skip this red step — a test that was never seen red proves nothing (`no-masking-skips.md`).

- [ ] **Step 3: Implement temp-file backing**

Replace the bodies of `OpenReadAsync` and `TryOpenReadAsync` in `BlobDocumentStore.cs` and add the shared private helper:

```csharp
    public async Task<Stream> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        _logger.LogDebug("BlobDocumentStore: opening blob '{BlobName}'", blobName);

        // Azure.RequestFailedException with Status=404 propagates to the
        // caller when the blob does not exist — callers treat 404 as "not
        // yet downloaded" rather than a hard error.
        return await DownloadToTempFileAsync(blobName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> TryOpenReadAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        _logger.LogDebug("BlobDocumentStore: trying to open blob '{BlobName}'", blobName);

        // Absorb 404 here (Infrastructure layer) so Application callers never
        // need to reference Azure.RequestFailedException (an Azure SDK type).
        // Any non-404 storage error still propagates — Invariant #17: a read
        // error is not silently swallowed as "not available".
        try
        {
            return await DownloadToTempFileAsync(blobName, cancellationToken).ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("BlobDocumentStore: blob '{BlobName}' not found (404) — treating as miss.", blobName);
            return null;
        }
    }

    // #832: download into a temp FileStream instead of a MemoryStream, so
    // peak memory is O(copy buffer) regardless of blob size. The previous
    // MemoryStream buffering reasoned about ONE document fitting in the ACA
    // container's memory; it never accounted for concurrent extractions, and
    // MemoryStream's doubling growth transiently costs old+new buffers on the
    // LOH (the Azure SDK's PartitionedDownloader never pre-sizes the
    // destination — verified at tag Azure.Storage.Blobs_12.29.1).
    //
    // DeleteOnClose on Linux unlinks at DISPOSE (SafeFileHandle.ReleaseHandle
    // "mimics" the flag), not at open — so a SIGKILL leaves the file. That is
    // acceptable by construction: ACA container-scoped storage "disappears
    // when the container shuts down or restarts" (Microsoft Learn, storage
    // mounts), so an orphan can never outlive the failed execution. Budget:
    // ExtractionConcurrency(4) x MaxStreamBytes(100 MB) = 400 MB, inside the
    // 2 GiB ephemeral allowance at <=0.5 vCPU.
    private async Task<FileStream> DownloadToTempFileAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await _container.GetBlobClient(blobName)
                .DownloadToAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            // DeleteOnClose removes the temp file with the handle; nothing to
            // clean by hand even on the 404 path.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
```

Update the class header comment (lines 13-19) — the "OpenReadAsync downloads into a MemoryStream… fits inside the ACA container's 1 GiB memory limit" paragraph is now false; replace it with:

```csharp
// OpenReadAsync/TryOpenReadAsync hand back a seekable temp-file-backed
// FileStream (DeleteOnClose) so callers (PdfPig text extractor, SHA-256
// backfill) get random access without the blob ever being materialized on
// the heap (#832). See DownloadToTempFileAsync for the memory/disk budget.
```

In `BlobDocumentBytesSource.cs`, correct the stale comment at lines 105-107 (`// OpenReadAsync returns a seekable MemoryStream ...`) to:

```csharp
        // OpenReadAsync returns a seekable temp-file-backed stream positioned
        // at 0 (#832 — no longer a MemoryStream), satisfying PdfPig's
        // random-access requirement without heap cost proportional to size.
```

- [ ] **Step 4: Run the full Infrastructure test suite**

Run: `AZURITE_BLOB_SERVICE_URL=<url> dotnet test tests/PinballWizard.Infrastructure.Tests -c Release`
Expected: PASS — new tests green, existing `WriteThenOpenRead_RoundTripsBytes` still green (it asserts content, not stream type).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Documents/BlobDocumentStore.cs \
        src/PinballWizard.Infrastructure/Rag/Ingestion/BlobDocumentBytesSource.cs \
        tests/PinballWizard.Infrastructure.Tests/Documents/BlobDocumentStoreTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "fix(download) back blob reads with DeleteOnClose temp files, not MemoryStream (#832)"
```

---

### Task 5: `DocumentLinker` — preview contract, upstream size guard, open-inside-try, skip counter

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` — ctor `:90-116`, meters `:39-59`, tier gate `:260`, `TryExtractDocumentAsync` `:767-793`, `TryMatchPage` signature `:795-801`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs:253-270` (linker DI factory)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs` (update existing substitutes + add new tests)
- Test: `tests/PinballWizard.Application.Tests/Linking/GoldenLinkSetReplayTests.cs:100-105` (parameter rename only)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs` (substitute updates where it wires extractors)

**Interfaces:**
- Consumes: `IDocumentPreviewExtractor` / `ExtractedPreview` (Task 1), `PdfExtractionOptions.DefaultMaxStreamBytes` (Task 1), `IDocumentBlobStore.GetSizeAsync` (already on the interface — `IDocumentBlobStore.cs:11`).
- Produces: new ctor signature (below) — Task 6 adds `extractionConcurrency` on top of it; Task 8's harness constructs it with a fake preview extractor. `TryMatchPage(RawDocumentRecord raw, ExtractedPreview extracted, int pageIndex, string strategyName, AmbiguityCapture ambiguity)`.

New ctor (replaces `IDocumentTextExtractor? textExtractor` — the linker has no remaining use for the full-document contract):

```csharp
    public DocumentLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository docWriter,
        IDocumentPreviewExtractor? previewExtractor,
        ILogger<DocumentLinker> logger,
        IMachineAliasLoader aliasLoader,
        int cosmosWriteConcurrency = 20,
        IDocumentBlobStore? blobStore = null,
        long maxExtractionBytes = PdfExtractionOptions.DefaultMaxStreamBytes)
```

- [ ] **Step 1: Write the new failing tests**

Add to `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs` (follow the file's existing arrange pattern — NSubstitute repos, `blobStore.TryOpenReadAsync(...)` setups appear at lines 1183/1292/etc. as the template; use the same builder the file already uses to construct linkers, updating it for the new ctor in Step 3):

```csharp
    // --- #832 upstream size guard + honest degradation --------------------------

    [Fact]
    public async Task LinkAsync_BlobOverMaxExtractionBytes_SkipsWithoutOpeningBlob()
    {
        // Size guard fires on the GetSizeAsync properties call BEFORE any body
        // transfer — the oversized blob must never be downloaded at all.
        var blobStore = Substitute.For<IDocumentBlobStore>();
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(200L * 1024 * 1024); // 200 MB > 100 MB default cap
        var preview = Substitute.For<IDocumentPreviewExtractor>();

        var linker = await BuildLinkerWithBlobAsync(preview, blobStore);
        var raw = MakeRawWithLocalPath("doc_oversize", "chicagogaminggamepage/MB_Manual_Rev_1.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        await blobStore.DidNotReceive().TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await preview.DidNotReceive().ExtractPreviewAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // A size skip is NOT a failure — the document falls through the page
        // tiers to the normal no-tier-matched outcome.
        Assert.NotEqual(LinkStatus.Failed, result.FinalStatus);
    }

    [Fact]
    public async Task LinkAsync_GetSizeAsyncReturnsNull_SkipsAsBlobMissing_NotFailed()
    {
        var blobStore = Substitute.For<IDocumentBlobStore>();
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((long?)null); // 404 — blob not yet downloaded
        var preview = Substitute.For<IDocumentPreviewExtractor>();

        var linker = await BuildLinkerWithBlobAsync(preview, blobStore);
        var raw = MakeRawWithLocalPath("doc_noblob", "manualspage/missing.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        await preview.DidNotReceive().ExtractPreviewAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.NotEqual(LinkStatus.Failed, result.FinalStatus);
    }

    [Fact]
    public async Task LinkAsync_GetSizeAsyncThrows_MarksThatDocumentFailed_AndDoesNotEscape()
    {
        // The open path lives INSIDE the per-document try (spec Sections C+E):
        // a transient storage error degrades to Failed for that one document
        // instead of escaping to RunBatchAsync's batch-level catch.
        var blobStore = Substitute.For<IDocumentBlobStore>();
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<long?>>(_ => throw new InvalidOperationException("transient storage error"));
        var preview = Substitute.For<IDocumentPreviewExtractor>();

        var linker = await BuildLinkerWithBlobAsync(preview, blobStore);
        var raw = MakeRawWithLocalPath("doc_transient", "manualspage/x.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Failed, result.FinalStatus);
        Assert.Equal("text_extraction_exception", result.FailureReason);
    }

    [Fact]
    public async Task LinkAsync_PreviewReturnsNonSuccess_SkipsHonestly_NotFailed()
    {
        var blobStore = Substitute.For<IDocumentBlobStore>();
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1024L);
        blobStore.TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream([1, 2, 3]));
        var preview = Substitute.For<IDocumentPreviewExtractor>();
        preview.ExtractPreviewAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ExtractedPreview.Failure(ExtractionStatus.Encrypted, "encrypted"));

        var linker = await BuildLinkerWithBlobAsync(preview, blobStore);
        var raw = MakeRawWithLocalPath("doc_encrypted", "manualspage/enc.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.NotEqual(LinkStatus.Failed, result.FinalStatus);
    }

    [Fact]
    public async Task LinkAsync_RequestsExactlyTwoPreviewPages()
    {
        var blobStore = Substitute.For<IDocumentBlobStore>();
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1024L);
        blobStore.TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream([1, 2, 3]));
        var preview = Substitute.For<IDocumentPreviewExtractor>();
        preview.ExtractPreviewAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedPreview(ExtractionStatus.Success, [new ExtractedPage(1, "no match here")], null));

        var linker = await BuildLinkerWithBlobAsync(preview, blobStore);
        var raw = MakeRawWithLocalPath("doc_two_pages", "manualspage/t.pdf");

        await linker.LinkAsync(raw, CancellationToken.None);

        await preview.Received(1).ExtractPreviewAsync(Arg.Any<Stream>(), 2, Arg.Any<CancellationToken>());
    }
```

Helper notes for this step (place beside the file's existing helpers): `BuildLinkerWithBlobAsync(IDocumentPreviewExtractor, IDocumentBlobStore)` constructs the linker exactly like the file's existing builder but passing the two substitutes plus default `maxExtractionBytes`, then awaits `InitializeAsync`. `MakeRawWithLocalPath(docId, localPath)` clones the file's existing raw-record factory and sets `File = new DownloadedFileInfo { LocalPath = localPath, Filename = Path.GetFileName(localPath), SizeBytes = 0, Sha256 = null }` and `Game = null` (no slug — forces fall-through to the page tiers). Reuse the existing factories; do not invent new record shapes.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~DocumentLinkerTests"`
Expected: FAIL to compile — ctor still takes `IDocumentTextExtractor`, `GetSizeAsync` never called.

- [ ] **Step 3: Implement**

3a. **Ctor** (`DocumentLinker.cs:90-116`): apply the new signature from the Interfaces block above. Field changes: replace `private readonly IDocumentTextExtractor? _textExtractor;` with `private readonly IDocumentPreviewExtractor? _previewExtractor;`; add `private readonly long _maxExtractionBytes;`. Remove the now-unused `using`/reference to `IDocumentTextExtractor` if nothing else in the file uses it.

3b. **Meter** (next to the existing counters at `:39-59`):

```csharp
    private static readonly Counter<long> ExtractionSkippedCounter =
        LinkerMeter.CreateCounter<long>(
            "pinwiz.linker.extraction_skipped_total",
            unit: "{document}",
            description: "Documents whose page-tier extraction was skipped, tagged by reason: " +
                         "size_exceeded (blob larger than MaxStreamBytes — never downloaded), " +
                         "blob_missing (not in the store / deleted between size check and open), " +
                         "extract_failed (parse returned a non-Success status: encrypted/malformed/oversize). " +
                         "Skips are honest degradation, not failures — they do NOT increment failed counts " +
                         "(mirrors pinwiz.download.too_large_skip_total, #819).");
```

3c. **Tier gate** (`:260`): `if (_previewExtractor is not null && _blobStore is not null && raw.File?.LocalPath is not null)`.

3d. **`TryExtractDocumentAsync`** (replace `:767-793` wholesale). Constant `PageTierCount`:

```csharp
    // The linker's page tiers read pages 1-2 only ("page_1"/"page_2"); this is
    // the pageCount handed to the preview extractor. If a tier is ever added
    // for page 3, this constant is the single place to widen the preview.
    private const int PageTierCount = 2;

    // Returns (preview, false) on success, (null, false) when the blob is absent /
    // oversized / extraction returned non-Success (honest skips, metered), and
    // (null, true) when the path threw — so the caller can distinguish a normal
    // fall-through from an error that warrants Failed status.
    //
    // EVERYTHING here — the GetSizeAsync properties call included — sits inside
    // the try. Before #832 the blob open sat outside it, so an OOM during
    // buffering escaped to RunBatchAsync's batch-level catch and logged as
    // "exception linking" instead of a per-document extraction failure.
    private async Task<(ExtractedPreview? Doc, bool ExtractionFailed)> TryExtractDocumentAsync(
        RawDocumentRecord raw,
        CancellationToken cancellationToken)
    {
        try
        {
            // Upstream size guard (spec Section C): a blob-properties call, no
            // body transfer. An oversized blob is never downloaded to disk at all.
            var size = await _blobStore!.GetSizeAsync(raw.File!.LocalPath!, cancellationToken).ConfigureAwait(false);
            if (size is null)
            {
                _logger.LogDebug("DocumentLinker: page extraction skipped for {DocId} — blob not in store.", raw.DocumentId);
                ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "blob_missing"));
                return (null, false);
            }
            if (size > _maxExtractionBytes)
            {
                _logger.LogWarning(
                    "DocumentLinker: page extraction skipped for {DocId} — blob size {SizeBytes} exceeds MaxStreamBytes={MaxBytes}.",
                    raw.DocumentId, size, _maxExtractionBytes);
                ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "size_exceeded"));
                return (null, false);
            }

            // 404→null translation happens in Infrastructure so Application never
            // references Azure SDK types. Null here is the TOCTOU window: the blob
            // answered the size probe but vanished before the open.
            var stream = await _blobStore.TryOpenReadAsync(raw.File.LocalPath!, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                _logger.LogDebug("DocumentLinker: page extraction skipped for {DocId} — blob gone between size check and open.", raw.DocumentId);
                ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "blob_missing"));
                return (null, false);
            }

            await using (stream)
            {
                var extracted = await _previewExtractor!.ExtractPreviewAsync(stream, PageTierCount, cancellationToken).ConfigureAwait(false);
                if (extracted.Status == ExtractionStatus.Success)
                {
                    return (extracted, false);
                }

                _logger.LogInformation(
                    "DocumentLinker: page extraction skipped for {DocId} — preview status {Status}: {Error}",
                    raw.DocumentId, extracted.Status, extracted.Error);
                ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "extract_failed"));
                return (null, false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DocumentLinker: text extraction failed for {DocId}.", raw.DocumentId);
            return (null, true);
        }
    }
```

3e. **`TryMatchPage`** (`:795`): change the second parameter type from `ExtractedDocument extracted` to `ExtractedPreview extracted`. The body already reads only `extracted.Pages.Count` and `extracted.Pages[pageIndex].Text` — both carry over unchanged (verified in review: no consumer needs `Text`/`Outline`).

3f. **DI factory** (`Persistence/Cosmos/ServiceCollectionExtensions.cs:253-270`) — replace the `textExtractor` line and add the primitive pluck, following the file's existing `CosmosWriteConcurrency` precedent at lines 261-262 (spec Section C forbids injecting `IOptions<PdfExtractionOptions>` into the linker):

```csharp
            var previewExtractor = sp.GetService<IDocumentPreviewExtractor>();
            // Primitive pluck, mirroring cosmosWriteConcurrency below: the
            // options type stays the single source of the threshold without the
            // orchestrator taking a dependency on extraction configuration.
            var pdfOptions = sp.GetService<IOptions<PdfExtractionOptions>>();
            var maxExtractionBytes = pdfOptions?.Value.MaxStreamBytes ?? PdfExtractionOptions.DefaultMaxStreamBytes;
```

and pass `previewExtractor` (5th arg) plus `maxExtractionBytes: maxExtractionBytes` in the `new DocumentLinker(...)` call.

3g. **Existing tests**: `DocumentLinkerTests.cs` and `DocumentLinkerResolverTests.cs` substitute `IDocumentTextExtractor` and return `ExtractedDocument` in their page-tier arrangements (e.g. lines 1159-1200, 2061-2160, and `DocumentLinkerResolverTests.cs:527`); `GoldenLinkSetReplayTests.cs:102` passes `textExtractor: null`. Mechanical conversion in every case: `Substitute.For<IDocumentTextExtractor>()` → `Substitute.For<IDocumentPreviewExtractor>()`; `ExtractAsync(stream, ct)` setups → `ExtractPreviewAsync(stream, Arg.Any<int>(), ct)`; returned `new ExtractedDocument(Status, Text, Pages, Outline, Error)` → `new ExtractedPreview(Status, Pages, Error)` (drop Text/Outline args); `textExtractor: null` → `previewExtractor: null`. **Do not change any test's assertions** — only the arrange plumbing. IMPORTANT: existing page-tier tests that previously only mocked `TryOpenReadAsync` now also need `blobStore.GetSizeAsync(...).Returns(1024L)` or the new size guard will classify them `blob_missing` (NSubstitute's default for `Task<long?>` is null). Add that setup wherever `TryOpenReadAsync` is already mocked.

- [ ] **Step 4: Run the Application test suite**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release`
Expected: PASS — 5 new tests green, ALL existing linker/replay tests green with identical assertions. If any pre-existing assertion had to change, STOP: that is a behaviour change the spec says must not happen; report it instead of adapting the test.

- [ ] **Step 5: Build the full solution to catch any other `IDocumentTextExtractor`-into-linker call site**

Run: `dotnet build PinballWizard.slnx -c Release`
Expected: clean build.

- [ ] **Step 6: Commit**

```bash
git add -A
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "fix(linking) preview-based page tiers with upstream size guard and honest skip metering (#832)"
```

---

### Task 6: Dedicated extraction concurrency gate

**Files:**
- Modify: `src/PinballWizard.Core/Configuration/ScraperSettings.cs:23` (add setting below `CosmosWriteConcurrency`)
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` (ctor + gate around the open-plus-parse span)
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs` (pluck the new setting)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs`

**Interfaces:**
- Consumes: Task 5's ctor and `TryExtractDocumentAsync`.
- Produces: `ScraperSettings.ExtractionConcurrency` (default const `ScraperSettings.DefaultExtractionConcurrency = 4`); ctor gains final param `int extractionConcurrency = ScraperSettings.DefaultExtractionConcurrency`. Task 8's harness may pass it explicitly.

- [ ] **Step 1: Write the failing test**

Add to `DocumentLinkerTests.cs`:

```csharp
    // --- #832 extraction concurrency gate ---------------------------------------

    // Fake that PARKS every extraction on a gate the test controls. Without the
    // parking, a near-synchronous fake completes before its peers start and
    // max-observed concurrency never exceeds 1 whether or not the production
    // semaphore exists — the test would pass with the fix reverted, which is
    // exactly the false-green no-masking-skips.md forbids. Parked workers make
    // the final MaxObserved assertion deterministic: ANY overlap beyond the
    // gate's width is recorded before release.
    private sealed class GatedPreviewExtractor : IDocumentPreviewExtractor
    {
        private int _current;
        private int _max;
        private int _started;
        public SemaphoreSlim Gate { get; } = new(0);
        public int MaxObserved => Volatile.Read(ref _max);
        public int Started => Volatile.Read(ref _started);

        public async Task<ExtractedPreview> ExtractPreviewAsync(Stream pdfStream, int pageCount, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _current);
            int snapshot;
            while ((snapshot = Volatile.Read(ref _max)) < now)
            {
                if (Interlocked.CompareExchange(ref _max, now, snapshot) == snapshot) break;
            }
            Interlocked.Increment(ref _started);

            await Gate.WaitAsync(ct);

            Interlocked.Decrement(ref _current);
            return new ExtractedPreview(ExtractionStatus.Success, [new ExtractedPage(1, "no evidence")], null);
        }
    }

    [Fact]
    public async Task RunBatchAsync_ExtractionConcurrency_NeverExceedsConfiguredGate()
    {
        const int docCount = 8;
        const int gateWidth = 2;

        var extractor = new GatedPreviewExtractor();
        var blobStore = Substitute.For<IDocumentBlobStore>();
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1024L);
        blobStore.TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream([1]));

        // cosmosWriteConcurrency deliberately WIDER than the gate: the whole
        // point of #832's Section D is that the Parallel.ForEachAsync width no
        // longer governs parse memory.
        var linker = await BuildLinkerForBatchAsync(
            extractor, blobStore,
            pendingDocs: Enumerable.Range(0, docCount)
                .Select(i => MakeRawWithLocalPath($"doc_gate_{i}", $"manualspage/g{i}.pdf"))
                .ToList(),
            cosmosWriteConcurrency: docCount,
            extractionConcurrency: gateWidth);

        var batch = linker.RunBatchAsync(CancellationToken.None);

        // Wait until the gate is saturated (exactly gateWidth workers parked),
        // then release everyone and let the batch drain.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (extractor.Started < gateWidth && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(gateWidth, extractor.Started); // a third worker must NOT have started

        extractor.Gate.Release(docCount);
        await batch;

        // Deterministic ceiling: every extraction parked until release, so any
        // overlap beyond the gate was recorded in MaxObserved before this line.
        Assert.True(extractor.MaxObserved <= gateWidth,
            $"extraction concurrency reached {extractor.MaxObserved}, gate is {gateWidth}");
        Assert.Equal(docCount, extractor.Started); // and everyone eventually ran
    }
```

Helper note: `BuildLinkerForBatchAsync(...)` mirrors the file's existing batch-test builder — `rawRepo.StreamByStatusAsync` returning `pendingDocs.ToAsyncEnumerable()`, empty machine catalog, `overrideRepo.LoadAllAsync` returning an empty dictionary — passing the two concurrency values through to the ctor. If the file already has a batch builder, extend it with the two optional int params rather than duplicating it.

> **Signature caveat:** the fragment writes `linker.RunBatchAsync(CancellationToken.None)` as
> intent, not gospel — this plan did not verify `RunBatchAsync`'s exact parameter list. The file's
> existing batch tests (the ones exercising the "exception linking" batch-catch path) show the
> real call shape; match it exactly, adjusting only the linker construction.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~RunBatchAsync_ExtractionConcurrency"`
Expected: FAIL — with no gate, all 8 workers start (`extractor.Started` reaches 8, the `Assert.Equal(gateWidth, extractor.Started)` fires red). This red run IS the proof the test can detect the gate's absence — note the failure message in the task report.

- [ ] **Step 3: Implement the gate**

3a. `ScraperSettings.cs` (below `CosmosWriteConcurrency`, line 23):

```csharp
    /// <summary>Default for <see cref="ExtractionConcurrency"/> — also the DocumentLinker ctor default (single source).</summary>
    public const int DefaultExtractionConcurrency = 4;

    /// <summary>
    /// Maximum concurrent PDF page-preview extractions during --link-documents (#832).
    /// Deliberately separate from CosmosWriteConcurrency: writes are cheap I/O tuned
    /// wide (20); extractions are memory-bound (temp file + PdfPig parse structures)
    /// and must stay narrow on the 0.5 vCPU / 1 GiB linker job. Peak extraction
    /// memory ~ ExtractionConcurrency x per-document parse cost; peak temp disk =
    /// ExtractionConcurrency x MaxStreamBytes (400 MB at defaults, inside the 2 GiB
    /// ACA ephemeral allowance at <=0.5 vCPU).
    /// </summary>
    public int ExtractionConcurrency { get; set; } = DefaultExtractionConcurrency;
```

3b. `DocumentLinker.cs`: append ctor param `int extractionConcurrency = ScraperSettings.DefaultExtractionConcurrency`; add field

```csharp
    // #832 Section D: bounds the open-plus-parse span independently of
    // Parallel.ForEachAsync's MaxDegreeOfParallelism (= CosmosWriteConcurrency,
    // a write-throughput knob that must never govern parse memory again).
    // DocumentLinker is a singleton — the semaphore lives for the process.
    private readonly SemaphoreSlim _extractionGate;
```

initialize `_extractionGate = new SemaphoreSlim(extractionConcurrency, extractionConcurrency);` (guard `ArgumentOutOfRangeException.ThrowIfLessThan(extractionConcurrency, 1);`). In `TryExtractDocumentAsync`, wrap from the `GetSizeAsync` call through the `await using` block:

```csharp
            await _extractionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // ... GetSizeAsync + guards + TryOpenReadAsync + ExtractPreviewAsync (unchanged from Task 5)
            }
            finally
            {
                _extractionGate.Release();
            }
```

(The gate encloses the size probe too — cheap, and it keeps the invariant simple: at most N documents anywhere on the extraction path. The outer try/catch from Task 5 stays outermost so a throw still releases the gate via `finally` before degrading to `(null, true)`.)

3c. DI factory (`Persistence/Cosmos/ServiceCollectionExtensions.cs`): next to the existing `concurrency` pluck add

```csharp
            var extractionConcurrency = settings?.Value.ExtractionConcurrency
                ?? ScraperSettings.DefaultExtractionConcurrency;
```

and pass `extractionConcurrency: extractionConcurrency`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release`
Expected: PASS — gate test green, everything else untouched.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Configuration/ScraperSettings.cs \
        src/PinballWizard.Application/Linking/DocumentLinker.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs \
        tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(linking) bound extraction concurrency independently of Cosmos write width (#832)"
```

---

### Task 7: `--capture-page-text` verb (new capture tooling)

**Files:**
- Modify: `src/PinballWizard.Cli/Commands/CaptureGoldenSetCommand.cs` (new method + fixture types)
- Modify: `src/PinballWizard.Cli/Program.cs:278` (option definition, beside `captureGoldenSetOption`) and `:2077-2096` (handler, mirroring the `--capture-golden-set` block)
- Test: `tests/PinballWizard.Cli.Tests/Commands/CapturePageTextCommandTests.cs` (create)

**Interfaces:**
- Consumes: `IRawDocumentRepository.StreamByStatusAsync` (`RawDocumentRecord.ResolutionStrategy` is on the Core model, `RawDocument.cs:74`; page-tier values start with `"page_"` — `page_1_resolver`, `page_2_resolver`, and their `_edition` variants), `IScrapedDocumentRepository.StreamByDocumentIdAsync`, `IMachineRepository.StreamAllAsync`, `IMachineAliasLoader.LoadAsync`, `IDocumentBlobStore.TryOpenReadAsync`, `IDocumentPreviewExtractor.ExtractPreviewAsync` (Tasks 1-3), `InMemoryMachineIndex.Build` + `new MachineResolver(index, machinesById)` + `ResolutionQuery`/`EvidenceKind.PageText`/`ResolutionResult` (`src/PinballWizard.Application/Resolution/`, construction mirrors `DocumentLinker.InitializeAsync` at `DocumentLinker.cs:186-192`), `LinkingUtilities.InferManufacturerKey(raw.Source)`.
- Produces: `--capture-page-text` CLI verb; fixture file `tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json`; capture record `tests/PinballWizard.Application.Tests/Fixtures/Linking/CAPTURE-PAGE-TEXT.md`; write-side types `PageTextLinkEntry` / `PageTextLinkSetFixture` (Task 8 defines parallel read-side DTOs). `internal static string TruncateWithResolutionParity(string fullPageText, string? manufacturerKey, MachineResolver resolver, int budget)` — exposed for the unit test.

**Copyright posture (spec, explicit):** excerpts are truncated to the first 1,000 characters of raw page text — cover-page title territory, all the resolver consumes evidence from. `TruncateWithResolutionParity` verifies at capture time that the truncated excerpt resolves to the same outcome as the full page; where truncation changes the outcome, the full page text is kept for that entry and `Truncated=false` is recorded.

- [ ] **Step 1: Write the failing unit test for the parity-preserving truncation**

Create `tests/PinballWizard.Cli.Tests/Commands/CapturePageTextCommandTests.cs`:

```csharp
using PinballWizard.Application.Resolution;
using PinballWizard.Cli.Commands;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Cli.Tests.Commands;

public sealed class CapturePageTextCommandTests
{
    private static MachineResolver BuildResolver(params Machine[] machines)
    {
        var byId = machines.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var index = InMemoryMachineIndex.Build(machines, aliases: []);
        return new MachineResolver(index, byId);
    }

    private static Machine Godzilla => new()
    {
        Id = "GZ-TEST-01",
        PartitionKey = "stern",
        ManufacturerDisplayName = "stern",
        Title = "Godzilla",
        ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["stern"] = "godzilla" },
    };

    [Fact]
    public void Truncate_TitleInsideBudget_TruncatesAndPreservesResolution()
    {
        var resolver = BuildResolver(Godzilla);
        var fullText = "Godzilla Service Manual. " + new string('x', 5000);

        var excerpt = CaptureGoldenSetCommand.TruncateWithResolutionParity(
            fullText, manufacturerKey: "stern", resolver, budget: 1000);

        Assert.Equal(1000, excerpt.Length);
        Assert.Contains("Godzilla", excerpt);
    }

    [Fact]
    public void Truncate_TitleOnlyBeyondBudget_KeepsFullText()
    {
        // The title appears only after the budget boundary: truncating would
        // silently encode a WEAKER resolver input than production sees, so the
        // parity check must keep the full page text for this entry.
        var resolver = BuildResolver(Godzilla);
        var fullText = new string('x', 2000) + " Godzilla Service Manual.";

        var excerpt = CaptureGoldenSetCommand.TruncateWithResolutionParity(
            fullText, manufacturerKey: "stern", resolver, budget: 1000);

        Assert.Equal(fullText, excerpt);
    }

    [Fact]
    public void Truncate_TextShorterThanBudget_ReturnsUnchanged()
    {
        var resolver = BuildResolver(Godzilla);
        var fullText = "Godzilla Service Manual.";

        var excerpt = CaptureGoldenSetCommand.TruncateWithResolutionParity(
            fullText, manufacturerKey: "stern", resolver, budget: 1000);

        Assert.Same(fullText, excerpt);
    }
}
```

(If `tests/PinballWizard.Cli.Tests` lacks a project reference to `PinballWizard.Application` for the Resolution types, add it — check the existing csproj first; `LinkDocumentsCommandTests` likely already pulls it in transitively via the CLI project.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Cli.Tests -c Release --filter "FullyQualifiedName~CapturePageTextCommandTests"`
Expected: FAIL to compile — `TruncateWithResolutionParity` does not exist.

- [ ] **Step 3: Implement the verb**

In `CaptureGoldenSetCommand.cs`, add the parity helper (internal for the test):

```csharp
    // #832 copyright posture: fixtures store the smallest excerpt that still
    // resolves identically to the full page. Compares the resolver outcome of
    // the truncated excerpt against the full text (both as PageText evidence
    // with the same manufacturer hint the linker would use); on divergence the
    // full page is kept — a truncated fixture must never encode a weaker
    // resolver input than production sees.
    internal static string TruncateWithResolutionParity(
        string fullPageText,
        string? manufacturerKey,
        MachineResolver resolver,
        int budget)
    {
        if (fullPageText.Length <= budget) return fullPageText;

        var excerpt = fullPageText[..budget];

        static string? Outcome(ResolutionResult r) => r switch
        {
            ResolutionResult.Resolved res => res.MachineId,
            ResolutionResult.ResolvedFamily fam => $"family:{fam.GroupId}",
            _ => null,
        };

        var full = Outcome(resolver.Resolve(new ResolutionQuery(fullPageText, EvidenceKind.PageText, manufacturerKey)));
        var truncated = Outcome(resolver.Resolve(new ResolutionQuery(excerpt, EvidenceKind.PageText, manufacturerKey)));

        return string.Equals(full, truncated, StringComparison.Ordinal) ? excerpt : fullPageText;
    }
```

Then add `RunPageTextSetAsync` following the exact shape of `RunGoldenLinkSetAsync` (lines 25-144 are the template — same guard clauses, same `Console.Error` + `Environment.ExitCode = 2` on missing services, same fixture + CAPTURE.md emission):

```csharp
    private const int PageExcerptBudget = 1000;
    private const int PageTierCount = 2;

    internal static async Task RunPageTextSetAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var rawRepo = services.GetService<IRawDocumentRepository>();
        var scrapedRepo = services.GetService<IScrapedDocumentRepository>();
        var machineRepo = services.GetService<IMachineRepository>();
        var aliasLoader = services.GetService<IMachineAliasLoader>();
        var blobStore = services.GetService<IDocumentBlobStore>();
        var preview = services.GetService<IDocumentPreviewExtractor>();
        if (rawRepo is null || scrapedRepo is null || machineRepo is null
            || aliasLoader is null || blobStore is null || preview is null)
        {
            Console.Error.WriteLine(
                "--capture-page-text requires Cosmos AND blob storage to be configured " +
                "(ConnectionStrings:cosmos or Cosmos:AccountEndpoint, plus the pinwiz-raw blob store): " +
                "page text exists only transiently inside extraction, so capture must download and " +
                "preview-extract each page-tier blob.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("Building resolver (machines + aliases) for truncation parity checks...");
        var machines = new List<Machine>();
        await foreach (var m in machineRepo.StreamAllAsync(cancellationToken)) machines.Add(m);
        var machinesById = machines.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var aliases = await aliasLoader.LoadAsync(cancellationToken);
        var resolver = new MachineResolver(InMemoryMachineIndex.Build(machines, aliases), machinesById);

        Console.WriteLine("Streaming page-tier-linked documents from scraped_documents_raw...");
        var entries = new List<PageTextLinkEntry>();
        var docCount = 0;
        var skippedNoBlob = 0;
        var skippedExtract = 0;

        await foreach (var raw in rawRepo.StreamByStatusAsync([LinkStatus.Linked], cancellationToken))
        {
            // Page-tier strategies: page_1_resolver / page_2_resolver (+ _edition variants).
            if (raw.ResolutionStrategy?.StartsWith("page_", StringComparison.Ordinal) is not true) continue;
            if (raw.File?.LocalPath is null) continue;
            docCount++;

            await using var stream = await blobStore.TryOpenReadAsync(raw.File.LocalPath, cancellationToken);
            if (stream is null) { skippedNoBlob++; continue; }

            var extracted = await preview.ExtractPreviewAsync(stream, PageTierCount, cancellationToken);
            if (extracted.Status != ExtractionStatus.Success) { skippedExtract++; continue; }

            var mfrHint = LinkingUtilities.InferManufacturerKey(raw.Source);
            var pageTexts = new List<string>(extracted.Pages.Count);
            var truncated = true;
            foreach (var page in extracted.Pages)
            {
                var excerpt = TruncateWithResolutionParity(page.Text, mfrHint, resolver, PageExcerptBudget);
                if (ReferenceEquals(excerpt, page.Text) && page.Text.Length > PageExcerptBudget) truncated = false;
                pageTexts.Add(excerpt);
            }

            await foreach (var machineId in scrapedRepo.StreamByDocumentIdAsync(raw.DocumentId, cancellationToken))
            {
                machinesById.TryGetValue(machineId, out var machine);
                entries.Add(new PageTextLinkEntry
                {
                    DocumentId = raw.DocumentId,
                    LocalPath = raw.File.LocalPath,
                    FileUrl = raw.Source.FileUrl,
                    SourceType = raw.Source.SourceType.ToString(),
                    GameSlug = raw.Game?.Slug,
                    DocumentType = raw.DocumentType.ToString(),
                    ResolutionStrategy = raw.ResolutionStrategy!,
                    ExpectedMachineId = machineId,
                    ExpectedMachineTitle = machine?.Title ?? string.Empty,
                    ExpectedMachineManufacturer = machine?.PartitionKey ?? string.Empty,
                    PageTexts = pageTexts,
                    Truncated = truncated,
                });
            }
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var fixture = new PageTextLinkSetFixture
        {
            CapturedAt = capturedAt,
            Source = "live Cosmos scraped_documents_raw (link_status=Linked, resolution_strategy=page_*) "
                + "+ pinwiz-raw blob preview extraction (first 2 pages, parity-truncated excerpts)",
            DocumentCount = docCount,
            EntryCount = entries.Count,
            Entries = entries,
        };

        var outputDir = Path.Combine("tests", "PinballWizard.Application.Tests", "Fixtures", "Linking");
        Directory.CreateDirectory(outputDir);
        var fixturePath = Path.Combine(outputDir, "page-text-link-set.captured.json");
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, JsonOptions), cancellationToken);

        var captureMd = $"""
            # Page-Text Link Set — Capture Record

            Generated by `--capture-page-text` against live Cosmos + blob storage. The
            fixture (`page-text-link-set.captured.json`) arms the #832 page-tier
            regression gate: `PageTextLinkSetReplayTests` replays these excerpts
            through a fake IDocumentPreviewExtractor so tiers 3/4 execute OFFLINE —
            the coverage the slug-only golden set structurally cannot provide
            (its replay runs with previewExtractor: null).

            ## Capture details

            | Field | Value |
            |---|---|
            | Source | live Cosmos `scraped_documents_raw` (link_status=Linked, resolution_strategy=page_*) + pinwiz-raw blob preview extraction |
            | Captured at | {capturedAt:O} |
            | Page-tier documents | {docCount} |
            | Fan-out entries | {entries.Count} |
            | Skipped (blob missing) | {skippedNoBlob} |
            | Skipped (extraction non-Success) | {skippedExtract} |

            Skipped rows are named so a thin capture is never mistaken for full
            coverage. Non-zero skips mean some page-tier documents contribute no
            gate coverage — investigate before treating this as the baseline.

            ## Copyright posture

            Excerpts are the first {PageExcerptBudget} characters of raw page text
            (cover-page title territory) — the smallest excerpt verified AT CAPTURE
            TIME to resolve identically to the full page (see
            TruncateWithResolutionParity). Entries where truncation would change
            the resolution keep full page text and record Truncated=false.

            ## To recapture

            ```bash
            export AZURE_TOKEN_CREDENTIALS=dev
            export Cosmos__AccountEndpoint="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
            dotnet run --project src/PinballWizard.Cli -c Release -- --capture-page-text
            ```

            Re-run only after a deliberate re-link that you want as the new baseline.
            """;
        await File.WriteAllTextAsync(Path.Combine(outputDir, "CAPTURE-PAGE-TEXT.md"), captureMd, cancellationToken);

        Console.WriteLine();
        Console.WriteLine(
            $"--capture-page-text complete: {docCount} page-tier documents, {entries.Count} fan-out entries "
            + $"(blob-missing={skippedNoBlob}, extract-skip={skippedExtract}) → {fixturePath}");
    }
```

Fixture types (bottom of the file, beside `GoldenLinkEntry`):

```csharp
internal sealed class PageTextLinkEntry
{
    public string DocumentId { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;   // blob name — the replay's lookup key
    public string FileUrl { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string? GameSlug { get; init; }
    public string DocumentType { get; init; } = string.Empty;
    public string ResolutionStrategy { get; init; } = string.Empty;
    public string ExpectedMachineId { get; init; } = string.Empty;
    public string ExpectedMachineTitle { get; init; } = string.Empty;
    public string ExpectedMachineManufacturer { get; init; } = string.Empty;
    public List<string> PageTexts { get; init; } = [];       // index 0 = page 1
    public bool Truncated { get; init; }
}

internal sealed class PageTextLinkSetFixture
{
    public DateTimeOffset CapturedAt { get; init; }
    public string Source { get; init; } = string.Empty;
    public int DocumentCount { get; init; }
    public int EntryCount { get; init; }
    public List<PageTextLinkEntry> Entries { get; init; } = [];
}
```

Wire the CLI: in `Program.cs` add beside `captureGoldenSetOption` (line 278) a `new Option<bool>("--capture-page-text") { Description = "Capture page-tier excerpts + bindings for the #832 replay gate (requires Cosmos + blob storage)." }`, register it wherever `captureGoldenSetOption` is added to the root command, and add a handler block mirroring the `--capture-golden-set` one at lines 2077-2096 calling `CaptureGoldenSetCommand.RunPageTextSetAsync(host.Services, cancellationToken)`. Add whatever `using` imports the Resolution types need.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Cli.Tests -c Release --filter "FullyQualifiedName~CapturePageTextCommandTests"` then `dotnet build PinballWizard.slnx -c Release`
Expected: 3 tests PASS; clean build; `dotnet run --project src/PinballWizard.Cli -- --help` lists the new option.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Cli/Commands/CaptureGoldenSetCommand.cs src/PinballWizard.Cli/Program.cs \
        tests/PinballWizard.Cli.Tests/Commands/CapturePageTextCommandTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(cli) add --capture-page-text verb with parity-truncated excerpts (#832)"
```

---

### Task 8: Page-text replay gate (shared harness + synthetic tests + gated live test)

**Files:**
- Create: `tests/PinballWizard.Application.Tests/Linking/LinkerReplayHarness.cs` (extracted shared helpers)
- Create: `tests/PinballWizard.Application.Tests/Linking/PageTextLinkSetReplayTests.cs`
- Modify: `tests/PinballWizard.Application.Tests/Linking/GoldenLinkSetReplayTests.cs` (delegate `BuildLinkerAsync`/`MakeMachine`/`MakeRaw` to the harness — zero assertion changes)

**Interfaces:**
- Consumes: Task 5's linker ctor; Task 7's fixture JSON shape (parallel read-side DTOs defined here, mirroring the `GoldenLinkSetFixtureDto` pattern at `GoldenLinkSetReplayTests.cs:511-529` — no CLI project dependency); existing `RequiresCapturedFixtureFactAttribute` (`tests/PinballWizard.Application.Tests/Fixtures/RequiresCapturedFixtureFactAttribute.cs`).
- Produces: `LinkerReplayHarness.BuildLinkerAsync(IEnumerable<Machine> machines, IDocumentPreviewExtractor? previewExtractor = null, IDocumentBlobStore? blobStore = null, CancellationToken ct = default)`; `LinkerReplayHarness.MakeMachine(id, manufacturerKey, title, slug)`; `LinkerReplayHarness.MakeRaw(...)` gaining optional `string? localPath = null` (sets `File = new DownloadedFileInfo { LocalPath = localPath, Filename = Path.GetFileName(localPath), SizeBytes = 0, Sha256 = null }` when non-null).

**The replay mechanism** (how a fixture entry reaches the fake extractor): the linker opens the blob by `raw.File.LocalPath` and hands the extractor only a `Stream`. The fake blob store therefore returns `new MemoryStream(Encoding.UTF8.GetBytes(blobName))` — the stream *content* carries the identity — and the fake extractor reads it back and looks the entry up by `LocalPath`. `GetSizeAsync` on the fake MUST return a small non-null value (`1024L`): NSubstitute's default for `Task<long?>` is `null`, which Task 5's size guard classifies as `blob_missing` and every entry would silently skip — a vacuous gate.

- [ ] **Step 1: Extract the shared harness (pure refactor, no behaviour change)**

Create `LinkerReplayHarness.cs` containing `BuildLinkerAsync` (from `GoldenLinkSetReplayTests.cs:72-109`, adding the two optional params which default to null — golden replay behaviour unchanged), `MakeMachine` (`:149-160`), and `MakeRaw` (`:112-146`, adding the optional `localPath`). Make them `internal static`. Update `GoldenLinkSetReplayTests` to call the harness; delete the now-duplicated privates. Run:

`dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~GoldenLinkSetReplayTests"`
Expected: PASS with the exact same test count as before the refactor.

- [ ] **Step 2: Write the synthetic harness tests (always run) — failing first**

Create `PageTextLinkSetReplayTests.cs`:

```csharp
using System.Text;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Tests.Fixtures;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using System.Text.Json;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// #832 page-tier regression gate. The slug-only golden replay runs with
// previewExtractor: null, so tiers 3/4 NEVER execute there — this suite is the
// only offline coverage those tiers have. Two tiers of tests, mirroring
// GoldenLinkSetReplayTests: synthetic harness proofs (always run) and a
// live-fixture replay gated on the captured file.
public sealed class PageTextLinkSetReplayTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    internal const string CapturedFixtureRepoPath =
        "tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json";

    // ── Replay plumbing ────────────────────────────────────────────────────────

    // The linker hands the extractor a bare Stream; identity travels as the
    // stream CONTENT (the fake blob store writes the blob name into it).
    private sealed class FixturePreviewExtractor(
        IReadOnlyDictionary<string, IReadOnlyList<ExtractedPage>> pagesByBlobName) : IDocumentPreviewExtractor
    {
        public async Task<ExtractedPreview> ExtractPreviewAsync(Stream pdfStream, int pageCount, CancellationToken ct)
        {
            using var reader = new StreamReader(pdfStream, Encoding.UTF8, leaveOpen: true);
            var blobName = await reader.ReadToEndAsync(ct);
            return pagesByBlobName.TryGetValue(blobName, out var pages)
                ? new ExtractedPreview(ExtractionStatus.Success, pages.Take(pageCount).ToList(), Error: null)
                : ExtractedPreview.Failure(ExtractionStatus.Malformed, $"no fixture entry for blob '{blobName}'");
        }
    }

    private static IDocumentBlobStore MakeFixtureBlobStore()
    {
        var blobStore = Substitute.For<IDocumentBlobStore>();
        // Non-null small size is LOAD-BEARING: NSubstitute's Task<long?> default
        // is null, which the #832 size guard classifies as blob_missing — every
        // entry would silently skip and the gate would be vacuous.
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1024L);
        blobStore.TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (Stream?)new MemoryStream(Encoding.UTF8.GetBytes(call.Arg<string>())));
        return blobStore;
    }

    // ── Synthetic tests (always run — prove the gate can fail before live data) ──

    private static readonly Machine SynthGodzilla = LinkerReplayHarness.MakeMachine(
        id: "SYNTH-GZ-01", manufacturerKey: "stern", title: "Godzilla", slug: "godzilla-synth");

    private static readonly Machine SynthOktoberfest = LinkerReplayHarness.MakeMachine(
        id: "SYNTH-OK-01", manufacturerKey: "americanpinball", title: "Oktoberfest", slug: "oktoberfest-synth");

    [Fact]
    public async Task Synthetic_PageTextResolvesToExpectedMachine()
    {
        var extractor = new FixturePreviewExtractor(new Dictionary<string, IReadOnlyList<ExtractedPage>>
        {
            ["manualspage/gz.pdf"] = [new ExtractedPage(1, "Godzilla Service Manual — Stern Pinball")],
        });
        var linker = await LinkerReplayHarness.BuildLinkerAsync(
            [SynthGodzilla, SynthOktoberfest], extractor, MakeFixtureBlobStore());

        var raw = LinkerReplayHarness.MakeRaw(
            documentId: "doc_synth_page", fileUrl: "https://example.com/gz.pdf",
            gameSlug: string.Empty, manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage, localPath: "manualspage/gz.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Contains("SYNTH-GZ-01", result.LinkedMachineIds);
        Assert.StartsWith("page_1", result.ResolutionStrategy);
    }

    [Fact]
    public async Task Synthetic_MisattributionIsDetectable()
    {
        // The gate's reason to exist: feed page text naming machine A, expect
        // machine B, and confirm the replay CAN observe the divergence. If this
        // test ever passes with the divergence undetected, the gate is vacuous.
        var extractor = new FixturePreviewExtractor(new Dictionary<string, IReadOnlyList<ExtractedPage>>
        {
            ["manualspage/gz.pdf"] = [new ExtractedPage(1, "Godzilla Service Manual — Stern Pinball")],
        });
        var linker = await LinkerReplayHarness.BuildLinkerAsync(
            [SynthGodzilla, SynthOktoberfest], extractor, MakeFixtureBlobStore());

        var raw = LinkerReplayHarness.MakeRaw(
            documentId: "doc_synth_wrong", fileUrl: "https://example.com/gz.pdf",
            gameSlug: string.Empty, manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage, localPath: "manualspage/gz.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Linked to Godzilla — which IS a mis-attribution against an expectation
        // of Oktoberfest. The policy check the live test applies:
        var expectedMachineId = "SYNTH-OK-01";
        var misattributed = result.FinalStatus == LinkStatus.Linked
            && !result.LinkedMachineIds.Contains(expectedMachineId, StringComparer.OrdinalIgnoreCase);
        Assert.True(misattributed, "harness failed to detect a planted mis-attribution");
    }

    [Fact]
    public async Task Synthetic_NoEvidence_FallsThroughWithoutLinking()
    {
        var extractor = new FixturePreviewExtractor(new Dictionary<string, IReadOnlyList<ExtractedPage>>
        {
            ["manualspage/blank.pdf"] = [new ExtractedPage(1, "24 VDC power supply wiring diagram")],
        });
        var linker = await LinkerReplayHarness.BuildLinkerAsync(
            [SynthGodzilla], extractor, MakeFixtureBlobStore());

        var raw = LinkerReplayHarness.MakeRaw(
            documentId: "doc_synth_blank", fileUrl: "https://example.com/blank.pdf",
            gameSlug: string.Empty, manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage, localPath: "manualspage/blank.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.NotEqual(LinkStatus.Linked, result.FinalStatus);
    }

    // ── Live-fixture replay (gated) ────────────────────────────────────────────

    [RequiresCapturedFixtureFact(
        CapturedFixtureRepoPath,
        "Run: dotnet run --project src/PinballWizard.Cli -c Release -- --capture-page-text " +
        "(see tests/PinballWizard.Application.Tests/Fixtures/Linking/CAPTURE-PAGE-TEXT.md)")]
    public async Task PageTextLinkSet_Replays_WithNoMisattribution()
    {
        var fixturePath = FixturePath();
        var fixture = JsonSerializer.Deserialize<PageTextLinkSetFixtureDto>(
                await File.ReadAllTextAsync(fixturePath), CaseInsensitiveOptions)
            ?? throw new InvalidOperationException($"Could not deserialize fixture at {fixturePath}.");
        Assert.NotEmpty(fixture.Entries);

        // Seed the catalog with the REAL captured machine titles — page-tier
        // resolution matches identity variants built from Machine.Title, so
        // slug-derived fake titles (the golden replay's shortcut) would not arm
        // these tiers.
        var machines = fixture.Entries
            .Where(e => e.ExpectedMachineId is { Length: > 0 } && e.ExpectedMachineTitle is { Length: > 0 })
            .GroupBy(e => e.ExpectedMachineId, StringComparer.OrdinalIgnoreCase)
            .Select(g => LinkerReplayHarness.MakeMachine(
                id: g.Key,
                manufacturerKey: g.First().ExpectedMachineManufacturer,
                title: g.First().ExpectedMachineTitle,
                slug: $"unused-{g.Key.ToLowerInvariant()}"))
            .ToList();

        var pagesByBlobName = fixture.Entries
            .GroupBy(e => e.LocalPath, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ExtractedPage>)g.First().PageTexts
                    .Select((text, i) => new ExtractedPage(i + 1, text)).ToList(),
                StringComparer.Ordinal);

        var linker = await LinkerReplayHarness.BuildLinkerAsync(
            machines, new FixturePreviewExtractor(pagesByBlobName), MakeFixtureBlobStore());

        var mismatches = new List<string>();
        var notLinked = new List<string>();

        foreach (var entry in fixture.Entries)
        {
            var raw = LinkerReplayHarness.MakeRaw(
                documentId: entry.DocumentId,
                fileUrl: entry.FileUrl,
                gameSlug: entry.GameSlug ?? string.Empty,
                manufacturerKey: entry.ExpectedMachineManufacturer,
                docType: Enum.TryParse<DocumentType>(entry.DocumentType, out var dt) ? dt : DocumentType.Manual,
                sourceType: Enum.TryParse<SourceType>(entry.SourceType, out var st) ? st : SourceType.ManualsPage,
                localPath: entry.LocalPath);

            var result = await linker.LinkAsync(raw, CancellationToken.None);
            var resolved = result.LinkedMachineIds ?? [];

            if (result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked
                && !resolved.Contains(entry.ExpectedMachineId, StringComparer.OrdinalIgnoreCase))
            {
                mismatches.Add($"{entry.DocumentId}: expected {entry.ExpectedMachineId}, got [{string.Join(",", resolved)}]");
            }
            else if (result.FinalStatus is not (LinkStatus.Linked or LinkStatus.ManuallyLinked))
            {
                notLinked.Add($"{entry.DocumentId} ({result.FinalStatus})");
            }
        }

        if (notLinked.Count > 0)
        {
            Console.WriteLine($"[PageTextLinkSet] Entries that no longer link ({notLinked.Count}):");
            foreach (var nl in notLinked) Console.WriteLine($"  NOT_LINKED: {nl}");
        }

        // BOTH failure modes are blocking here — unlike the slug replay, this
        // fixture was captured WITH the evidence the tiers need, so an entry
        // that stops linking means the page tiers regressed, and an entry that
        // links elsewhere means mis-attribution.
        Assert.Empty(mismatches);
        Assert.Empty(notLinked);
    }

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
        return Path.Combine(root, CapturedFixtureRepoPath.Replace('/', Path.DirectorySeparatorChar));
    }

    // Read-side DTOs, parallel to CaptureGoldenSetCommand's write-side types —
    // duplicated deliberately so no CLI-project dependency is introduced (same
    // pattern as GoldenLinkSetFixtureDto).
    private sealed class PageTextLinkSetFixtureDto
    {
        public DateTimeOffset CapturedAt { get; init; }
        public string Source { get; init; } = string.Empty;
        public int DocumentCount { get; init; }
        public int EntryCount { get; init; }
        public List<PageTextLinkEntryDto> Entries { get; init; } = [];
    }

    private sealed class PageTextLinkEntryDto
    {
        public string DocumentId { get; init; } = string.Empty;
        public string LocalPath { get; init; } = string.Empty;
        public string FileUrl { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public string? GameSlug { get; init; }
        public string DocumentType { get; init; } = string.Empty;
        public string ResolutionStrategy { get; init; } = string.Empty;
        public string ExpectedMachineId { get; init; } = string.Empty;
        public string ExpectedMachineTitle { get; init; } = string.Empty;
        public string ExpectedMachineManufacturer { get; init; } = string.Empty;
        public List<string> PageTexts { get; init; } = [];
        public bool Truncated { get; init; }
    }
}
```

- [ ] **Step 3: Run tests — synthetic tests must run and pass; the live test must report Skipped (fixture not yet captured)**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~PageTextLinkSetReplayTests"`
Expected: 3 PASS + 1 SKIPPED (skip message names `--capture-page-text`). Confirm the skip is reported as Skipped, not Passed.

- [ ] **Step 4: Run the whole Application suite (harness refactor regression check)**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release`
Expected: PASS, same totals as end of Task 6 plus 3 new + 1 skip.

- [ ] **Step 5: Commit**

```bash
git add tests/PinballWizard.Application.Tests/Linking/LinkerReplayHarness.cs \
        tests/PinballWizard.Application.Tests/Linking/PageTextLinkSetReplayTests.cs \
        tests/PinballWizard.Application.Tests/Linking/GoldenLinkSetReplayTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "test(linking) page-text replay gate — first offline coverage for tiers 3/4 (#832)"
```

---

### Task 9: Live capture, fixture commit, and red-when-reverted proof — OPERATOR TASK

> **This task needs live Azure (Cosmos + blob) under the pinwiz identity.** Run it from a
> terminal opened in this repo (whose `.vscode/settings.json` wires
> `AZURE_CONFIG_DIR=D:/Projects/APS.ClaudeCodeConfig/orgs/pinwiz/azure`), or prefix each
> command with that `AZURE_CONFIG_DIR`. A session wired for another org will 401 — see
> `az-isolation.md`. Do NOT run this from an agent without that wiring confirmed.

**Files:**
- Create (by running the verb): `tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json`, `CAPTURE-PAGE-TEXT.md`

- [ ] **Step 1: Capture**

```bash
export AZURE_TOKEN_CREDENTIALS=dev
export Cosmos__AccountEndpoint="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
dotnet run --project src/PinballWizard.Cli -c Release -- --capture-page-text
```

Expected: completion line reporting page-tier documents and fan-out entries with skip counts. Inspect `CAPTURE-PAGE-TEXT.md`: if `Skipped (blob missing)` or `Skipped (extraction non-Success)` is non-zero, list the affected documents in the task report — a thin capture must be a known fact, not a silent one.

- [ ] **Step 2: Run the live replay gate**

Run: `dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~PageTextLinkSet_Replays_WithNoMisattribution"`
Expected: PASS (the skip is gone — the test executed; verify the test count says 1 passed, 0 skipped).

- [ ] **Step 3: Prove the gate can go red (acceptance criterion 1: "demonstrably red when the fix is reverted")**

Corrupt one document's **evidence** (page text) in a scratch copy and confirm the gate fires.

> **Do NOT corrupt `ExpectedMachineId` — that proof is vacuous.** Verified live 2026-08-12:
> the replay seeds its machine catalog FROM the fixture, so a corrupted expectation also
> corrupts the seeded world consistently (the fake machine inherits the entry's title and
> GroupId, joins the edition family, and satisfies its own expectation — the test stays
> green). The gate detects CODE regressions; the honest perturbation is therefore the
> evidence the code consumes, with the expectation left standing:

```bash
python - <<'PY'
import json, shutil
p = "tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json"
shutil.copy(p, p + ".bak")
with open(p, encoding="utf-8") as f: fx = json.load(f)
doc = fx["Entries"][0]["DocumentId"]
for e in fx["Entries"]:
    if e["DocumentId"] == doc:
        e["PageTexts"] = ["Oktoberfest Pinball on Tap Service Manual — American Pinball", ""]
with open(p, "w", encoding="utf-8") as f: json.dump(fx, f, indent=2)
print("evidence-corrupted doc:", doc)
PY
dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~PageTextLinkSet_Replays_WithNoMisattribution"
```

Expected: **FAIL** naming the corrupted document (entries sharing the same `LocalPath` blob
may fail with it — faithful, since one blob has one content). Then restore:

```bash
mv tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json.bak \
   tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json
dotnet test tests/PinballWizard.Application.Tests -c Release --filter "FullyQualifiedName~PageTextLinkSet_Replays_WithNoMisattribution"
```

Expected: PASS again. Record both outcomes (the red and the restored green) in the task report — the red run is the evidence.

- [ ] **Step 4: Commit the fixture**

```bash
git add tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json \
        tests/PinballWizard.Application.Tests/Fixtures/Linking/CAPTURE-PAGE-TEXT.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "test(linking) capture page-text replay baseline — arms tiers 3/4 offline (#832)"
```

---

### Task 10: Observability inventory + final verification

**Files:**
- Modify: `docs/observability.md` (new instrument entry — insert a `### Document linker instruments` row following the format of the download-instruments table at lines 297-303)
- Modify: `docs/superpowers/specs/2026-08-12-linker-extraction-memory-design.md:4` (status line only)

- [ ] **Step 1: Add the inventory entry**

In `docs/observability.md`, add to the linker instruments section (create the subsection if the existing linker counters aren't yet tabled there — check for `pinwiz.linker.documents_processed_total` first and co-locate):

```markdown
| `pinwiz.linker.extraction_skipped_total` | Counter | `reason` | Documents whose page-tier extraction was skipped during `--link-documents`. `reason` ∈ `size_exceeded` (blob larger than `Rag:PdfExtraction:MaxStreamBytes` — never downloaded; the #832 upstream guard), `blob_missing` (blob absent at size-check or open time), `extract_failed` (preview parse returned encrypted/malformed/oversize). Skips are honest degradation and do NOT increment failure counts — mirrors `pinwiz.download.too_large_skip_total` (#819). A spike in `size_exceeded` means a new class of oversized manuals; a spike in `extract_failed` means a scraper is downloading non-PDF or corrupt content. |
```

- [ ] **Step 2: Update the spec status line**

Change `**Status:** approved, not yet implemented — revised once after adversarial review (see below)` to `**Status:** implemented on branch Dev-Issue832-LinkerExtractionMemory (this plan: docs/superpowers/plans/2026-08-12-linker-extraction-memory.md) — revised twice after adversarial review (see below)`.

- [ ] **Step 3: Full-solution verification**

```bash
dotnet build PinballWizard.slnx -c Release
dotnet test PinballWizard.slnx -c Release
```

Expected: clean build; all suites green (Azurite-gated blob tests run if `AZURITE_BLOB_SERVICE_URL` is set — set it and run them; do not ship on skipped-only for the Task 4 tests). Record final totals in the task report.

- [ ] **Step 4: Commit**

```bash
git add docs/observability.md docs/superpowers/specs/2026-08-12-linker-extraction-memory-design.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "docs(observability) inventory pinwiz.linker.extraction_skipped_total (#832)"
```

---

## After the plan (ship phase — dispatcher, not a plan task)

Not tasks for implementers; the dispatcher runs the repo ship workflow (`.claude/rules/pinball-workflows.md`): `/local-review` → `/standards-audit` → PR-AUDIT checklist → `gh pr create` (description records both audit outcomes + links #832) → `claude-code` label → code-scanning triage → merge → watch `Deploy` to green. Then the **acceptance run** (spec Acceptance 3): trigger the linker ACA job manually from a pinwiz-wired terminal, confirm `doc_4ea5f0c438428b8b` extracts or is honestly recorded as a skip with a reason, execution reports **Succeeded**. Also file the `FileDownloader` buffering follow-up issue (spec Non-goals) referencing `FileDownloader.cs:145`.
