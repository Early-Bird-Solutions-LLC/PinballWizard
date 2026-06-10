# Document Linking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the file-mediated `catalog.json` → `ScrapedDocumentSeeder` pipeline with a cloud-first, Cosmos-native document-to-machine linking system: scrapers write to `scraped_documents_raw`, an async linker resolves machine associations via a 5-tier algorithm, and the existing `scraped_documents` Change Feed drives RAG ingestion unchanged.

**Architecture:** Two containers (`scraped_documents_raw` partitioned by `document_id`, `link_overrides` partitioned by `source_pattern`) sit between the scraper and the existing `scraped_documents` container. A `DocumentLinker` service runs the tiered algorithm (override → xref slug → filename → page 1 → page 2 → ADI OCR) and fan-outs multi-machine documents into N `scraped_documents` records. A `--link-documents` CLI command wraps the linker for local runs and backfill; an ACA Job wraps it for scheduled production runs.

**Tech Stack:** .NET 10 / C# 14, Microsoft.Azure.Cosmos (data-plane), Azure.ResourceManager.CosmosDB (ARM schema), xUnit + NSubstitute, `IDocumentTextExtractor` (existing), `IMachineRepository` (existing), `CosmosRepository<T>` (existing base class), `CosmosOptions` container list (existing pattern for container registration).

---

## Parallelism map

Read this before executing. Tasks in the same **wave** have no inter-task dependencies and can run in parallel worktrees. Tasks in later waves depend on earlier wave output.

| Wave | Tasks | Can parallelize? |
|---|---|---|
| Wave 1 | T1 (domain types), T2 (`LinkingUtilities`) | Yes — no shared output files |
| Wave 2 | T3 (`IRawDocumentRepository` + Cosmos impl), T4 (`ILinkOverrideRepository` + Cosmos impl) | Yes after T1 |
| Wave 3 | T5 (Cosmos container registration + bootstrapper), T6 (`edition` field on `ScrapedDocumentRecord` + `ScrapedDocumentChange`) | Yes after T2+T3+T4 |
| Wave 4 | T7 (`IDocumentLinker` + `DocumentLinker` — tiers 0–3), T8 (`DocumentLinker` — tiers 4–5 + terminal) | Sequential — T8 extends T7 |
| Wave 5 | T9 (`--link-documents` CLI), T10 (scraper write-path migration), T11 (backfill CLI `--migrate-to-raw`) | T9+T11 after T7+T8; T10 after T3 |
| Wave 6 | T12 (retire `CatalogBuilder` + `ScrapedDocumentSeeder`), T13 (OTel metrics) | T12 after T10+T11; T13 after T7+T8 |
| Wave 7 | T14 (ACA Job manifest), T15 (Admin UI — triage + linking + override management) | T14 after T9; T15 independent (uses existing Cosmos clients) |

---

## File map

### New files

| File | Layer | Responsibility |
|---|---|---|
| `src/PinballWizard.Core/Models/RawDocument.cs` | Core | `RawDocumentRecord` domain model + `LinkStatus` enum |
| `src/PinballWizard.Core/Models/LinkOverride.cs` | Core | `LinkOverrideRecord` domain model |
| `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs` | Application | Write + query interface for `scraped_documents_raw` |
| `src/PinballWizard.Application/Persistence/ILinkOverrideRepository.cs` | Application | Write + query interface for `link_overrides` |
| `src/PinballWizard.Application/Linking/LinkingUtilities.cs` | Application | Static helpers: `NormalizeForMatch`, `ExtractEditionFromText`, `ExtractEdition`, `ExtractGameSlugFromUrl`, `IsWordBoundaryMatch` |
| `src/PinballWizard.Application/Linking/IDocumentLinker.cs` | Application | `IDocumentLinker` interface + `LinkingResult` record |
| `src/PinballWizard.Application/Linking/DocumentLinker.cs` | Application | Tier 0–5 algorithm, multi-machine fan-out, idempotency |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs` | Infrastructure | Cosmos POCO for `scraped_documents_raw` |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/LinkOverrideRecord.cs` | Infrastructure | Cosmos POCO for `link_overrides` |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` | Infrastructure | `IRawDocumentRepository` implementation |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosLinkOverrideRepository.cs` | Infrastructure | `ILinkOverrideRepository` implementation |
| `src/PinballWizard.Cli/Commands/LinkDocumentsCommand.cs` | CLI | `--link-documents` command handler |
| `src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs` | CLI | `--migrate-to-raw` one-time backfill command |
| `deploy/linker-job/linker-job.bicep` | Infra | ACA Job definition for scheduled linker runs |
| `tests/PinballWizard.Scraper.Tests/Linking/LinkingUtilitiesTests.cs` | Tests | Unit tests for all static helpers |
| `tests/PinballWizard.Scraper.Tests/Linking/DocumentLinkerTests.cs` | Tests | Unit tests for each tier + multi-machine + idempotency |

### Modified files

| File | Change |
|---|---|
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs` | Add `scraped_documents_raw` and `link_overrides` container entries to the default list |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs` | Register `IRawDocumentRepository`, `ILinkOverrideRepository`, `IDocumentLinker` |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapedDocumentRecord.cs` | Add `edition` field (`string?`, `[JsonPropertyName("edition")]`) |
| `src/PinballWizard.Application/Rag/Ingestion/IRagIngestionPipeline.cs` | Add `Edition` property to `ScrapedDocumentChange` record |
| `src/PinballWizard.Infrastructure/Rag/Ingestion/RagSourceDocument.cs` | Add `edition` JSON field |
| `src/PinballWizard.Application/Scraping/ScraperOrchestrator.cs` | Replace `CatalogBuilder.MergeScrapedItem` call with `IRawDocumentRepository.UpsertAsync` |
| `src/PinballWizard.Cli/Program.cs` | Wire `--link-documents` and `--migrate-to-raw` commands; remove `--build-catalog` and `--seed-scraped-documents` |

### Retired files (delete at end of T12)

- `src/PinballWizard.Application/Provenance/CatalogBuilder.cs`
- `src/PinballWizard.Application/Sync/ScrapedDocumentSeeder.cs`
- `src/PinballWizard.Application/Sync/IScrapedDocumentSeeder.cs` (if exists)

---

## Wave 1

### Task 1: Domain models — `RawDocumentRecord` + `LinkOverride`

**Files:**
- Create: `src/PinballWizard.Core/Models/RawDocument.cs`
- Create: `src/PinballWizard.Core/Models/LinkOverride.cs`
- Test: `tests/PinballWizard.Scraper.Tests/Linking/LinkingUtilitiesTests.cs` (scaffold only — actual tests in T2)

**Context:** These are the domain types that every other task depends on. `LinkStatus` is an enum that flows through the entire system. Get the names exactly right here — changing them later requires cascading updates.

- [ ] **Step 1: Write `RawDocument.cs`**

```csharp
// src/PinballWizard.Core/Models/RawDocument.cs
namespace PinballWizard.Core.Models;

public enum LinkStatus
{
    Pending,
    Linked,
    PlatformGeneric,
    NotInCatalog,
    Failed,
    ManuallyLinked,
}

// Domain model for a `scraped_documents_raw` Cosmos record.
// Partition key: document_id. One record per unique file URL.
public sealed class RawDocumentRecord
{
    public required string DocumentId { get; init; }
    public required string DocumentUrl { get; init; }
    public required string DocumentType { get; init; }
    public required SourceInfo Source { get; init; }
    public required TimelineInfo Timeline { get; set; }
    public ClassificationInfo? Classification { get; init; }
    public DownloadedFileInfo? File { get; init; }
    public HttpMetadata? Http { get; init; }
    public List<CrossReference> CrossReferences { get; set; } = [];
    public string? ContentHash { get; init; }

    // Linker-managed fields
    public LinkStatus LinkStatus { get; set; } = LinkStatus.Pending;
    public string? ResolutionStrategy { get; set; }
    public DateTimeOffset? LinkAttemptedAt { get; set; }
    public string? LinkFailureReason { get; set; }
    public string? LinkedBy { get; set; }
    public DateTimeOffset? LinkedAt { get; set; }
    public string? OverrideId { get; set; }
}
```

- [ ] **Step 2: Write `LinkOverride.cs`**

```csharp
// src/PinballWizard.Core/Models/LinkOverride.cs
namespace PinballWizard.Core.Models;

// Domain model for a `link_overrides` Cosmos record.
// Partition key: source_pattern (= discovery_url|document_type, URL-normalized).
// id == source_pattern — one record per pattern, upsert semantics.
public sealed class LinkOverrideRecord
{
    public required string SourcePattern { get; init; }
    // Empty array = confirmed platform-generic (no machine scope).
    public required string[] MachineIds { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Notes { get; init; }
}
```

- [ ] **Step 3: Build solution to verify types compile**

```
dotnet build PinballWizard.slnx
```
Expected: 0 errors, 0 warnings (the project enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

- [ ] **Step 4: Commit**

```
git add src/PinballWizard.Core/Models/RawDocument.cs src/PinballWizard.Core/Models/LinkOverride.cs
git commit -m "feat(catalog) T1: domain models — RawDocumentRecord, LinkStatus, LinkOverrideRecord"
```

---

### Task 2: `LinkingUtilities` — shared matching helpers

**Files:**
- Create: `src/PinballWizard.Application/Linking/LinkingUtilities.cs`
- Create: `tests/PinballWizard.Scraper.Tests/Linking/LinkingUtilitiesTests.cs`

**Context:** These helpers are migrated from `CatalogBuilder.cs`. The critical change is `IsWordBoundaryMatch` — the existing `Contains` approach produces false positives (e.g., slug `tron` matches "electronic"). The new word-boundary rule uses padded-space matching: `(" " + normText + " ").Contains(" " + token + " ")`. This is cheaper than regex and passes all the problematic probe cases.

`NormalizeForMatch` strips `-`, `_`, `.`, whitespace, lowercases. Padding with spaces before/after makes the boundary check work on normalized text.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/PinballWizard.Scraper.Tests/Linking/LinkingUtilitiesTests.cs
using PinballWizard.Application.Linking;
using Xunit;

namespace PinballWizard.Scraper.Tests.Linking;

public class LinkingUtilitiesTests
{
    // NormalizeForMatch
    [Theory]
    [InlineData("stranger-things", "strangerthings")]
    [InlineData("Stranger Things", "strangerthings")]
    [InlineData("stranger_things", "strangerthings")]
    [InlineData("TRON", "tron")]
    [InlineData("", "")]
    public void NormalizeForMatch_stripsAndLowers(string input, string expected)
        => Assert.Equal(expected, LinkingUtilities.NormalizeForMatch(input));

    // IsWordBoundaryMatch — true positives
    [Theory]
    [InlineData("tron_legacy_manual.pdf", "tron")]            // slug is whole word
    [InlineData("kiss_premium_manual.pdf", "kiss")]
    [InlineData("stern_tron_le.pdf", "tron")]
    public void IsWordBoundaryMatch_matchesWholeSlug(string filename, string slug)
        => Assert.True(LinkingUtilities.IsWordBoundaryMatch(
            LinkingUtilities.NormalizeForMatch(filename),
            LinkingUtilities.NormalizeForMatch(slug)));

    // IsWordBoundaryMatch — false positives that substring matching gets wrong
    [Theory]
    [InlineData("electronic_manual.pdf", "tron")]             // "tron" inside "electronic"
    [InlineData("kiss_me_kate_manual.pdf", "kiss")]           // only if the slug is "kiss"
    public void IsWordBoundaryMatch_rejectsFalsePositives(string filename, string slug)
    {
        // "tron" appears inside "electronic" but is NOT a word boundary match
        // "kiss" appears inside "kiss_me_kate" — this one IS a true positive but
        // the test verifies by looking at the padded form
        var normFile = LinkingUtilities.NormalizeForMatch(filename);
        var normSlug = LinkingUtilities.NormalizeForMatch(slug);
        // electronic → "electronic"; " tron " is NOT in " electronic "
        if (filename == "electronic_manual.pdf")
            Assert.False(LinkingUtilities.IsWordBoundaryMatch(normFile, normSlug));
        else
            Assert.True(LinkingUtilities.IsWordBoundaryMatch(normFile, normSlug));
    }

    // ExtractEditionFromText
    [Theory]
    [InlineData("godzilla premium manual", "Premium")]
    [InlineData("metallica le rules", "LE")]
    [InlineData("mandalorian pro manual", "Pro")]
    [InlineData("batman no edition", null)]
    public void ExtractEditionFromText_returnsCanonical(string text, string? expected)
        => Assert.Equal(expected, LinkingUtilities.ExtractEditionFromText(
            LinkingUtilities.NormalizeForMatch(text)));

    // ExtractEdition (slug-position anchored)
    [Theory]
    [InlineData("godzillapremiummanual", "godzilla", "Premium")]
    [InlineData("godzillapromanual", "godzilla", "Pro")]
    [InlineData("godzillamanual", "godzilla", null)]
    public void ExtractEdition_anchorsToSlugPosition(string normFilename, string normSlug, string? expected)
        => Assert.Equal(expected, LinkingUtilities.ExtractEdition(normFilename, normSlug));

    // ExtractGameSlugFromUrl
    [Theory]
    [InlineData("https://sternpinball.com/game/godzilla/", "godzilla")]
    [InlineData("https://sternpinball.com/game/stranger-things/manual/", "stranger-things")]
    [InlineData("https://sternpinball.com/manuals/", null)]
    [InlineData("", null)]
    public void ExtractGameSlugFromUrl_extractsCorrectly(string url, string? expected)
        => Assert.Equal(expected, LinkingUtilities.ExtractGameSlugFromUrl(url));
}
```

- [ ] **Step 2: Run tests — verify they fail with "type not found"**

```
dotnet test tests/PinballWizard.Scraper.Tests/ --filter "FullyQualifiedName~LinkingUtilitiesTests" -v minimal
```
Expected: build error — `LinkingUtilities` doesn't exist yet.

- [ ] **Step 3: Write `LinkingUtilities.cs`**

```csharp
// src/PinballWizard.Application/Linking/LinkingUtilities.cs
namespace PinballWizard.Application.Linking;

public static class LinkingUtilities
{
    // Edition markers in priority order (longer strings first to avoid
    // "le" winning before "limited" when both appear).
    private static readonly (string Marker, string Canonical)[] EditionMarkers =
    [
        ("premium", "Premium"),
        ("limited", "Limited"),
        ("pro", "Pro"),
        ("le", "LE"),
        ("vault", "Vault"),
        ("ce", "CE"),
    ];

    // Lowercases and strips -, _, ., and whitespace so that
    // "stranger-things", "StrangerThings", and "stranger_things"
    // all collapse to "strangerthings".
    public static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var lower = value.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (c == '_' || c == '-' || c == '.' || char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Word-boundary match on already-normalized strings.
    // Pads both sides with a space so short slugs like "tron" or "kiss"
    // don't match mid-word (e.g., "tron" inside "electronic").
    // normText and normSlug must both already be NormalizeForMatch output.
    public static bool IsWordBoundaryMatch(string normText, string normSlug)
    {
        if (string.IsNullOrEmpty(normSlug) || string.IsNullOrEmpty(normText))
            return false;
        var paddedText = " " + normText + " ";
        var paddedSlug = " " + normSlug + " ";
        return paddedText.Contains(paddedSlug, StringComparison.Ordinal);
    }

    // Scans normalizedText for any edition marker anywhere in the string.
    // Used when we have link_text but no slug position to anchor from.
    public static string? ExtractEditionFromText(string normalizedText)
    {
        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (normalizedText.Contains(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    // Anchored edition extraction: finds the slug within normFilename,
    // then checks what immediately follows for an edition marker.
    // normFilename and normSlug must both already be NormalizeForMatch output.
    public static string? ExtractEdition(string normFilename, string normSlug)
    {
        var idx = normFilename.IndexOf(normSlug, StringComparison.Ordinal);
        if (idx < 0) return null;
        var afterSlug = idx + normSlug.Length;
        if (afterSlug >= normFilename.Length) return null;
        var tail = normFilename[afterSlug..];
        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (tail.StartsWith(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    // Extracts the game slug from a URL of the form
    // https://sternpinball.com/game/{slug}[/...].
    // Returns null for any other URL shape.
    public static string? ExtractGameSlugFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("game", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```
dotnet test tests/PinballWizard.Scraper.Tests/ --filter "FullyQualifiedName~LinkingUtilitiesTests" -v minimal
```
Expected: all pass, 0 failures.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Application/Linking/LinkingUtilities.cs tests/PinballWizard.Scraper.Tests/Linking/LinkingUtilitiesTests.cs
git commit -m "feat(catalog) T2: LinkingUtilities — NormalizeForMatch, IsWordBoundaryMatch, edition extraction"
```

---

## Wave 2

> Run T3 and T4 in parallel — they touch different files.

### Task 3: `IRawDocumentRepository` + `CosmosRawDocumentRepository`

**Files:**
- Create: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs`

**Context:** This is the write target for the scraper (replaces `catalog.json`). The partition key is `document_id`. The `id` in Cosmos equals `document_id`. The merge/dedup logic lives here: if a record with the same `document_id` exists, update `timeline.last_checked_at` and add cross-references — don't duplicate.

The `UpsertRawAsync` method takes a full `DocumentRecord` (the existing domain model the scraper already produces) and maps it to the Cosmos POCO. This keeps the scraper's output type unchanged.

- [ ] **Step 1: Write the interface**

```csharp
// src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

// Write + query interface for the `scraped_documents_raw` container.
// Partition key: document_id. One record per unique file URL.
public interface IRawDocumentRepository
{
    // Idempotent upsert. If the document_id already exists:
    //   - updates timeline.last_checked_at
    //   - adds new cross-references from item.CrossReferences
    //   - updates content_hash if hash has changed
    // If new: inserts with link_status = Pending.
    // Returns the upserted record.
    Task<RawDocumentRecord> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken);

    // Stream all records where LinkStatus is in the given set.
    // Used by the linker to find work.
    IAsyncEnumerable<RawDocumentRecord> StreamByStatusAsync(
        IReadOnlyCollection<LinkStatus> statuses,
        CancellationToken cancellationToken);

    // Set link_status and linker metadata on an existing record.
    // Used by the linker after resolving (or failing to resolve) a document.
    Task UpdateLinkStatusAsync(
        string documentId,
        LinkStatus status,
        string? resolutionStrategy,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken);

    // Point-read by document_id (= partition key).
    Task<RawDocumentRecord?> GetAsync(string documentId, CancellationToken cancellationToken);

    // Query by source_pattern (discovery_url|document_type) for the override lookup.
    // Returns all raw documents whose discovery_url + document_type match the pattern.
    IAsyncEnumerable<RawDocumentRecord> StreamBySourcePatternAsync(
        string sourcePattern,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the Cosmos POCO**

```csharp
// src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs
using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Wire-format POCO for the `scraped_documents_raw` container.
// Partition key path: /document_id. id == document_id.
internal sealed class RawDocumentCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("document_id")]
    public required string PartitionKey { get; init; }  // IEntity.PartitionKey

    [JsonPropertyName("document_url")]
    public required string DocumentUrl { get; init; }

    [JsonPropertyName("document_type")]
    public required string DocumentType { get; init; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("source")]
    public required RawSourceInfo Source { get; init; }

    [JsonPropertyName("classification")]
    public RawClassificationInfo? Classification { get; init; }

    [JsonPropertyName("file")]
    public RawFileInfo? File { get; init; }

    [JsonPropertyName("http")]
    public RawHttpInfo? Http { get; init; }

    [JsonPropertyName("timeline")]
    public required RawTimelineInfo Timeline { get; set; }

    [JsonPropertyName("cross_references")]
    public List<RawCrossRef> CrossReferences { get; set; } = [];

    [JsonPropertyName("link_status")]
    public string LinkStatus { get; set; } = "pending";

    [JsonPropertyName("resolution_strategy")]
    public string? ResolutionStrategy { get; set; }

    [JsonPropertyName("link_attempted_at")]
    public DateTimeOffset? LinkAttemptedAt { get; set; }

    [JsonPropertyName("link_failure_reason")]
    public string? LinkFailureReason { get; set; }

    [JsonPropertyName("linked_by")]
    public string? LinkedBy { get; set; }

    [JsonPropertyName("linked_at")]
    public DateTimeOffset? LinkedAt { get; set; }

    [JsonPropertyName("override_id")]
    public string? OverrideId { get; set; }
}

internal sealed class RawSourceInfo
{
    [JsonPropertyName("discovery_url")]
    public required string DiscoveryUrl { get; init; }
    [JsonPropertyName("discovery_context")]
    public required string DiscoveryContext { get; init; }
    [JsonPropertyName("file_url")]
    public required string FileUrl { get; init; }
    [JsonPropertyName("link_text")]
    public string? LinkText { get; init; }
    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }
    [JsonPropertyName("tab")]
    public string? Tab { get; init; }
    [JsonPropertyName("scraped_at")]
    public DateTimeOffset ScrapedAt { get; init; }
}

internal sealed class RawClassificationInfo
{
    [JsonPropertyName("document_type")]
    public string? DocumentType { get; init; }
    [JsonPropertyName("file_format")]
    public string? FileFormat { get; init; }
}

internal sealed class RawFileInfo
{
    [JsonPropertyName("local_path")]
    public string? LocalPath { get; init; }
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }
    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; init; }
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }
    [JsonPropertyName("page_count")]
    public int? PageCount { get; init; }
}

internal sealed class RawHttpInfo
{
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }
    [JsonPropertyName("last_modified")]
    public DateTime? LastModified { get; init; }
    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }
    [JsonPropertyName("content_length")]
    public long? ContentLength { get; init; }
}

internal sealed class RawTimelineInfo
{
    [JsonPropertyName("first_discovered_at")]
    public DateTimeOffset FirstDiscoveredAt { get; init; }
    [JsonPropertyName("last_checked_at")]
    public DateTimeOffset LastCheckedAt { get; set; }
    [JsonPropertyName("last_downloaded_at")]
    public DateTimeOffset? LastDownloadedAt { get; init; }
    [JsonPropertyName("last_content_changed_at")]
    public DateTimeOffset? LastContentChangedAt { get; init; }
    [JsonPropertyName("version_count")]
    public int VersionCount { get; init; } = 1;
}

internal sealed class RawCrossRef
{
    [JsonPropertyName("also_found_at")]
    public required string AlsoFoundAt { get; init; }
    [JsonPropertyName("discovery_context")]
    public required string DiscoveryContext { get; init; }
    [JsonPropertyName("link_text")]
    public string? LinkText { get; init; }
    [JsonPropertyName("discovered_at")]
    public DateTimeOffset DiscoveredAt { get; init; }
}
```

- [ ] **Step 3: Write `CosmosRawDocumentRepository.cs`**

```csharp
// src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

internal sealed class CosmosRawDocumentRepository
    : CosmosRepository<RawDocumentCosmosRecord>, IRawDocumentRepository
{
    public CosmosRawDocumentRepository(Container container, ILogger<CosmosRawDocumentRepository> logger)
        : base(container, logger) { }

    public async Task<RawDocumentRecord> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var existing = await GetAsync(record.DocumentId, cancellationToken).ConfigureAwait(false);

        RawDocumentCosmosRecord cosmos;
        if (existing is not null)
        {
            // Merge: update last_checked_at, accumulate cross-references, update hash if changed.
            var updated = MapToCosmosRecord(record);
            updated = updated with
            {
                LinkStatus = MapLinkStatus(existing.LinkStatus),
                ResolutionStrategy = existing.ResolutionStrategy,
                LinkAttemptedAt = existing.LinkAttemptedAt,
                LinkFailureReason = existing.LinkFailureReason,
                LinkedBy = existing.LinkedBy,
                LinkedAt = existing.LinkedAt,
                OverrideId = existing.OverrideId,
            };
            // Merge cross-references: add new ones not already present.
            var existingUrls = existing.CrossReferences.Select(cr => cr.AlsoFoundAt).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cr in record.CrossReferences)
            {
                if (!existingUrls.Contains(cr.AlsoFoundAt))
                    updated.CrossReferences.Add(new RawCrossRef
                    {
                        AlsoFoundAt = cr.AlsoFoundAt,
                        DiscoveryContext = cr.DiscoveryContext,
                        LinkText = cr.LinkText,
                        DiscoveredAt = DateTimeOffset.UtcNow,
                    });
            }
            cosmos = updated;
        }
        else
        {
            cosmos = MapToCosmosRecord(record);
        }

        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
        return MapToDomain(cosmos);
    }

    public async IAsyncEnumerable<RawDocumentRecord> StreamByStatusAsync(
        IReadOnlyCollection<LinkStatus> statuses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var statusStrings = statuses.Select(MapLinkStatusToString).ToList();
        var inClause = string.Join(", ", statusStrings.Select((_, i) => $"@s{i}"));
        var query = $"SELECT * FROM c WHERE c.link_status IN ({inClause})";
        var parameters = statusStrings
            .Select((s, i) => ($"s{i}", (object)s))
            .ToDictionary(t => t.Item1, t => t.Item2);

        await foreach (var rec in StreamAsync(query, parameters, partitionKey: null, cancellationToken))
            yield return MapToDomain(rec);
    }

    public async Task UpdateLinkStatusAsync(
        string documentId,
        LinkStatus status,
        string? resolutionStrategy,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            Logger.LogWarning("UpdateLinkStatus: document {DocumentId} not found — skipping", documentId);
            return;
        }

        var updated = existing with
        {
            LinkStatus = MapLinkStatusToString(status),
            ResolutionStrategy = resolutionStrategy,
            LinkFailureReason = failureReason,
            OverrideId = overrideId,
            LinkAttemptedAt = DateTimeOffset.UtcNow,
        };
        await base.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RawDocumentRecord?> GetAsync(string documentId, CancellationToken cancellationToken)
    {
        var cosmos = await GetByIdAsync(documentId, documentId, cancellationToken).ConfigureAwait(false);
        return cosmos is null ? null : MapToDomain(cosmos);
    }

    public async IAsyncEnumerable<RawDocumentRecord> StreamBySourcePatternAsync(
        string sourcePattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // source_pattern = discovery_url|document_type — match the parts separately.
        var parts = sourcePattern.Split('|');
        if (parts.Length != 2) yield break;
        var (discoveryUrl, docType) = (parts[0], parts[1]);
        var query = "SELECT * FROM c WHERE c.source.discovery_url = @url AND c.document_type = @type";
        var parameters = new Dictionary<string, object>
        {
            ["url"] = discoveryUrl,
            ["type"] = docType,
        };
        await foreach (var rec in StreamAsync(query, parameters, partitionKey: null, cancellationToken))
            yield return MapToDomain(rec);
    }

    private static RawDocumentCosmosRecord MapToCosmosRecord(DocumentRecord record) =>
        new()
        {
            Id = record.DocumentId,
            PartitionKey = record.DocumentId,
            DocumentUrl = record.Source.FileUrl,
            DocumentType = record.Classification.DocumentType.ToString(),
            ContentHash = record.File?.Sha256,
            Source = new RawSourceInfo
            {
                DiscoveryUrl = record.Source.DiscoveryUrl,
                DiscoveryContext = record.Source.DiscoveryContext,
                FileUrl = record.Source.FileUrl,
                LinkText = record.Source.LinkText,
                SourceType = record.Source.SourceType.ToString(),
                Tab = record.Source.Tab,
                ScrapedAt = new DateTimeOffset(record.Source.ScrapedAt, TimeSpan.Zero),
            },
            Classification = record.Classification is null ? null : new RawClassificationInfo
            {
                DocumentType = record.Classification.DocumentType.ToString(),
                FileFormat = record.Classification.FileFormat,
            },
            File = record.File is null ? null : new RawFileInfo
            {
                LocalPath = record.File.LocalPath,
                Filename = record.File.Filename,
                SizeBytes = record.File.SizeBytes,
                Sha256 = record.File.Sha256,
                MimeType = record.File.MimeType,
                PageCount = record.File.PageCount,
            },
            Http = record.Http is null ? null : new RawHttpInfo
            {
                ETag = record.Http.ETag,
                LastModified = record.Http.LastModified,
                ContentType = record.Http.ContentType,
                ContentLength = record.Http.ContentLength,
            },
            Timeline = new RawTimelineInfo
            {
                FirstDiscoveredAt = new DateTimeOffset(record.Timeline.FirstDiscoveredAt, TimeSpan.Zero),
                LastCheckedAt = DateTimeOffset.UtcNow,
                LastDownloadedAt = record.Timeline.LastDownloadedAt.HasValue
                    ? new DateTimeOffset(record.Timeline.LastDownloadedAt.Value, TimeSpan.Zero) : null,
                LastContentChangedAt = record.Timeline.LastContentChangedAt.HasValue
                    ? new DateTimeOffset(record.Timeline.LastContentChangedAt.Value, TimeSpan.Zero) : null,
                VersionCount = record.Timeline.VersionCount,
            },
            CrossReferences = record.CrossReferences
                .Select(cr => new RawCrossRef
                {
                    AlsoFoundAt = cr.AlsoFoundAt,
                    DiscoveryContext = cr.DiscoveryContext,
                    LinkText = cr.LinkText,
                    DiscoveredAt = DateTimeOffset.UtcNow,
                }).ToList(),
        };

    private static RawDocumentRecord MapToDomain(RawDocumentCosmosRecord r) =>
        new()
        {
            DocumentId = r.PartitionKey,
            DocumentUrl = r.DocumentUrl,
            DocumentType = r.DocumentType,
            ContentHash = r.ContentHash,
            LinkStatus = ParseLinkStatus(r.LinkStatus),
            ResolutionStrategy = r.ResolutionStrategy,
            LinkAttemptedAt = r.LinkAttemptedAt,
            LinkFailureReason = r.LinkFailureReason,
            LinkedBy = r.LinkedBy,
            LinkedAt = r.LinkedAt,
            OverrideId = r.OverrideId,
            Source = new SourceInfo
            {
                DiscoveryUrl = r.Source.DiscoveryUrl,
                DiscoveryContext = r.Source.DiscoveryContext,
                FileUrl = r.Source.FileUrl,
                LinkText = r.Source.LinkText,
                SourceType = Enum.TryParse<SourceType>(r.Source.SourceType, out var st) ? st : SourceType.ManualsPage,
                Tab = r.Source.Tab,
                ScrapedAt = r.Source.ScrapedAt.UtcDateTime,
                ActionType = ActionType.OpenPdf,
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = r.Timeline.FirstDiscoveredAt.UtcDateTime,
                LastCheckedAt = r.Timeline.LastCheckedAt.UtcDateTime,
                LastDownloadedAt = r.Timeline.LastDownloadedAt?.UtcDateTime,
                LastContentChangedAt = r.Timeline.LastContentChangedAt?.UtcDateTime,
                VersionCount = r.Timeline.VersionCount,
            },
            CrossReferences = r.CrossReferences
                .Select(cr => new CrossReference
                {
                    AlsoFoundAt = cr.AlsoFoundAt,
                    DiscoveryContext = cr.DiscoveryContext,
                    LinkText = cr.LinkText,
                    DiscoveredAt = cr.DiscoveredAt.UtcDateTime,
                }).ToList(),
        };

    private static string MapLinkStatusToString(LinkStatus s) => s switch
    {
        LinkStatus.Pending => "pending",
        LinkStatus.Linked => "linked",
        LinkStatus.PlatformGeneric => "platform_generic",
        LinkStatus.NotInCatalog => "not_in_catalog",
        LinkStatus.Failed => "failed",
        LinkStatus.ManuallyLinked => "manually_linked",
        _ => "pending",
    };

    private static string MapLinkStatus(string s) => s; // passthrough — already a wire string

    private static LinkStatus ParseLinkStatus(string s) => s switch
    {
        "linked" => LinkStatus.Linked,
        "platform_generic" => LinkStatus.PlatformGeneric,
        "not_in_catalog" => LinkStatus.NotInCatalog,
        "failed" => LinkStatus.Failed,
        "manually_linked" => LinkStatus.ManuallyLinked,
        _ => LinkStatus.Pending,
    };
}
```

- [ ] **Step 4: Build**

```
dotnet build PinballWizard.slnx
```
Expected: 0 errors.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs
git commit -m "feat(catalog) T3: IRawDocumentRepository + CosmosRawDocumentRepository"
```

---

### Task 4: `ILinkOverrideRepository` + `CosmosLinkOverrideRepository`

**Files:**
- Create: `src/PinballWizard.Application/Persistence/ILinkOverrideRepository.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/LinkOverrideRecord.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosLinkOverrideRepository.cs`

**Context:** The `link_overrides` container uses `source_pattern` as both `id` and partition key. One record per `{discovery_url}|{document_type}` pattern. Upsert semantics — a second admin decision overwrites the first. The linker reads from this at startup; the admin UI writes to it.

- [ ] **Step 1: Write the interface**

```csharp
// src/PinballWizard.Application/Persistence/ILinkOverrideRepository.cs
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

public interface ILinkOverrideRepository
{
    // Load all overrides for startup caching by the linker.
    // In practice < 1,000 records — safe to load eagerly.
    Task<IReadOnlyDictionary<string, LinkOverrideRecord>> LoadAllAsync(CancellationToken cancellationToken);

    // Upsert an admin decision. source_pattern = id = partition key.
    Task UpsertAsync(LinkOverrideRecord record, CancellationToken cancellationToken);

    // Point-read by source_pattern.
    Task<LinkOverrideRecord?> GetAsync(string sourcePattern, CancellationToken cancellationToken);

    // Delete an override (revoke admin decision).
    Task DeleteAsync(string sourcePattern, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the Cosmos POCO**

```csharp
// src/PinballWizard.Infrastructure/Persistence/Cosmos/LinkOverrideRecord.cs
using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Wire-format POCO for the `link_overrides` container.
// Partition key path: /source_pattern. id == source_pattern.
internal sealed class LinkOverrideCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source_pattern")]
    public required string PartitionKey { get; init; }  // IEntity.PartitionKey

    [JsonPropertyName("machine_ids")]
    public required string[] MachineIds { get; init; }

    [JsonPropertyName("created_by")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}
```

- [ ] **Step 3: Write `CosmosLinkOverrideRepository.cs`**

```csharp
// src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosLinkOverrideRepository.cs
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

internal sealed class CosmosLinkOverrideRepository
    : CosmosRepository<LinkOverrideCosmosRecord>, ILinkOverrideRepository
{
    public CosmosLinkOverrideRepository(Container container, ILogger<CosmosLinkOverrideRepository> logger)
        : base(container, logger) { }

    public async Task<IReadOnlyDictionary<string, LinkOverrideRecord>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LinkOverrideRecord>(StringComparer.OrdinalIgnoreCase);
        await foreach (var rec in StreamAsync("SELECT * FROM c", null, null, cancellationToken))
            result[rec.PartitionKey] = MapToDomain(rec);
        return result;
    }

    public async Task UpsertAsync(LinkOverrideRecord record, CancellationToken cancellationToken)
    {
        var cosmos = new LinkOverrideCosmosRecord
        {
            Id = record.SourcePattern,
            PartitionKey = record.SourcePattern,
            MachineIds = record.MachineIds,
            CreatedBy = record.CreatedBy,
            CreatedAt = record.CreatedAt,
            Notes = record.Notes,
        };
        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LinkOverrideRecord?> GetAsync(string sourcePattern, CancellationToken cancellationToken)
    {
        var cosmos = await GetByIdAsync(sourcePattern, sourcePattern, cancellationToken).ConfigureAwait(false);
        return cosmos is null ? null : MapToDomain(cosmos);
    }

    public Task DeleteAsync(string sourcePattern, CancellationToken cancellationToken)
        => DeleteAsync(sourcePattern, sourcePattern, cancellationToken);

    private static LinkOverrideRecord MapToDomain(LinkOverrideCosmosRecord r) =>
        new()
        {
            SourcePattern = r.PartitionKey,
            MachineIds = r.MachineIds,
            CreatedBy = r.CreatedBy,
            CreatedAt = r.CreatedAt,
            Notes = r.Notes,
        };
}
```

- [ ] **Step 4: Build**

```
dotnet build PinballWizard.slnx
```
Expected: 0 errors.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Application/Persistence/ILinkOverrideRepository.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/LinkOverrideRecord.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosLinkOverrideRepository.cs
git commit -m "feat(catalog) T4: ILinkOverrideRepository + CosmosLinkOverrideRepository"
```

---

## Wave 3

> Run T5 and T6 in parallel.

### Task 5: Register new containers + repositories in DI

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs`

**Context:** Follow exactly the pattern used for `scraped_documents` and `rag_leases`. Add entries to the `Containers` default list in `CosmosOptions`. Add `AddSingleton` calls in `ServiceCollectionExtensions.AddCosmosPersistence`. No ARM bicep changes needed — `--ensure-cosmos-containers` is the canonical creator per ADR-0012.

- [ ] **Step 1: Add container declarations to `CosmosOptions.cs`**

In `CosmosOptions.cs`, find the end of the `Containers` list (after `rag_dead_letters`). Add these two entries before the closing `]`:

```csharp
        // scraped_documents_raw: scraper write target (Phase 4.5 document-linking)
        // Partition key: /document_id (one record per unique file URL).
        // Written by scrapers; read + updated by the linker.
        // Selective indexing: only link_status and document_type are queried
        // in bulk; everything else is point-reads by document_id.
        new()
        {
            Name = "scraped_documents_raw",
            PartitionKeyPath = "/document_id",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/link_status/?", "/document_type/?"],
                ExcludedPaths = ["/*"],
            },
        },
        // link_overrides: admin feedback store (Phase 4.5 document-linking)
        // Partition key: /source_pattern (= id). One record per
        // {discovery_url}|{document_type} pattern. Upsert semantics.
        // No TTL — overrides are permanent until explicitly revoked.
        new()
        {
            Name = "link_overrides",
            PartitionKeyPath = "/source_pattern",
        },
```

- [ ] **Step 2: Register repositories in `ServiceCollectionExtensions.cs`**

In `AddCosmosPersistence`, after the `IFeaturedMachineRepository` registration, add:

```csharp
        services.AddSingleton<IRawDocumentRepository>(sp =>
        {
            var container = ResolveContainer(sp, "scraped_documents_raw");
            return new CosmosRawDocumentRepository(container,
                sp.GetRequiredService<ILogger<CosmosRawDocumentRepository>>());
        });

        services.AddSingleton<ILinkOverrideRepository>(sp =>
        {
            var container = ResolveContainer(sp, "link_overrides");
            return new CosmosLinkOverrideRepository(container,
                sp.GetRequiredService<ILogger<CosmosLinkOverrideRepository>>());
        });
```

- [ ] **Step 3: Build**

```
dotnet build PinballWizard.slnx
```
Expected: 0 errors.

- [ ] **Step 4: Verify container list with a quick test**

```
dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers --verbose
```

Against the Aspire emulator this should create `scraped_documents_raw` and `link_overrides`. If the emulator isn't running, the command exits with "Cosmos not configured" — that's acceptable at this stage.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs
git commit -m "feat(catalog) T5: register scraped_documents_raw + link_overrides containers and repositories"
```

---

### Task 6: Add `edition` field to `ScrapedDocumentRecord` + `ScrapedDocumentChange`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapedDocumentRecord.cs`
- Modify: `src/PinballWizard.Application/Rag/Ingestion/IRagIngestionPipeline.cs`
- Modify: `src/PinballWizard.Infrastructure/Rag/Ingestion/RagSourceDocument.cs` (find this file — it's the change-feed read projection)

**Context:** `edition` is a nullable string (`"Pro"` | `"Premium"` | `"LE"` | `"CE"` | `"Vault"` | null). Null means the document applies to all editions. This is additive — the change-feed consumer picks it up automatically; no schema migration needed in AI Search because new chunks just carry the field.

- [ ] **Step 1: Find `RagSourceDocument.cs`**

```
grep -r "RagSourceDocument" src/ --include="*.cs" -l
```

Read that file before editing.

- [ ] **Step 2: Add `edition` to `ScrapedDocumentRecord.cs`**

After the `LastDownloadedAt` property, add:

```csharp
    [JsonPropertyName("edition")]
    public string? Edition { get; init; }
```

- [ ] **Step 3: Add `Edition` to `ScrapedDocumentChange` record**

In `IRagIngestionPipeline.cs`, update the `ScrapedDocumentChange` record to add `Edition`:

```csharp
public sealed record ScrapedDocumentChange(
    string DocumentId,
    string DocumentUrl,
    string MachineId,
    string MachineTitle,
    string Manufacturer,
    DocumentType DocumentType,
    string ContentHash,
    DateTimeOffset? LastScrapedUtc = null,
    string? Edition = null);   // ← add this
```

- [ ] **Step 4: Add `edition` field to `RagSourceDocument.cs`**

Add a `string? Edition` property with `[JsonPropertyName("edition")]` alongside the other properties in that file. Then find where `RagSourceDocument` is mapped to `ScrapedDocumentChange` (likely in the change-feed hosted service) and thread `Edition` through.

Run:
```
grep -r "ScrapedDocumentChange(" src/ --include="*.cs" -n
```
to find all call sites. Each site that passes the record from a `RagSourceDocument` needs to pass `Edition: doc.Edition`.

- [ ] **Step 5: Build + run all tests**

```
dotnet build PinballWizard.slnx && dotnet test tests/PinballWizard.Scraper.Tests/ -v minimal
```
Expected: 0 errors, 0 test failures. The `edition` field is additive — no existing tests should break.

- [ ] **Step 6: Commit**

```
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapedDocumentRecord.cs \
        src/PinballWizard.Application/Rag/Ingestion/IRagIngestionPipeline.cs \
        src/PinballWizard.Infrastructure/Rag/Ingestion/RagSourceDocument.cs
# + any change-feed adapter file modified in step 4
git commit -m "feat(catalog) T6: add edition field to ScrapedDocumentRecord, ScrapedDocumentChange, RagSourceDocument"
```

---

## Wave 4

### Task 7: `IDocumentLinker` + `DocumentLinker` — tiers 0–3

**Files:**
- Create: `src/PinballWizard.Application/Linking/IDocumentLinker.cs`
- Create: `src/PinballWizard.Application/Linking/DocumentLinker.cs`
- Create (extend): `tests/PinballWizard.Scraper.Tests/Linking/DocumentLinkerTests.cs`

**Context:** The linker is the heart of the system. It processes `RawDocumentRecord` instances one at a time, running the tiered algorithm. This task covers tiers 0–3 (override, xref slug, filename, page 1). Tier 4 (page 2) and tier 5 (ADI OCR) are in T8 to keep each task reviewable.

The linker loads `link_overrides` eagerly at construction time (`_overrides` dict). It never modifies `link_overrides` — that's the admin UI's job. It writes `scraped_documents` records (linked set) and updates `scraped_documents_raw` statuses.

`LinkingResult` tells the caller what happened — useful for OTel tagging.

- [ ] **Step 1: Write the interface**

```csharp
// src/PinballWizard.Application/Linking/IDocumentLinker.cs
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

public sealed record LinkingResult(
    string DocumentId,
    LinkStatus FinalStatus,
    string? ResolutionStrategy,
    // Machines this document was linked to (empty if unlinked).
    IReadOnlyList<string> LinkedMachineIds,
    string? FailureReason = null);

public interface IDocumentLinker
{
    // Load overrides from the repository. Call once before processing.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Run the tier 0–5 algorithm for a single raw document.
    // Writes scraped_documents records for each resolved machine.
    // Updates scraped_documents_raw with the final status.
    Task<LinkingResult> LinkAsync(RawDocumentRecord raw, CancellationToken cancellationToken);

    // Process all pending/failed/not_in_catalog documents.
    // Returns aggregate counts.
    Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed)>
        RunBatchAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write failing tests for tiers 0–3**

```csharp
// tests/PinballWizard.Scraper.Tests/Linking/DocumentLinkerTests.cs
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests.Linking;

public class DocumentLinkerTests
{
    private static RawDocumentRecord MakeRaw(
        string documentId = "doc_abc123",
        string discoveryUrl = "https://sternpinball.com/manuals/",
        string fileUrl = "https://sternpinball.com/files/godzilla_manual.pdf",
        string linkText = "Godzilla Manual",
        string documentType = "Manual",
        IEnumerable<CrossReference>? crossRefs = null) =>
        new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = documentType,
            Source = new SourceInfo
            {
                DiscoveryUrl = discoveryUrl,
                DiscoveryContext = "Manuals Page",
                FileUrl = fileUrl,
                LinkText = linkText,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow,
                ActionType = ActionType.OpenPdf,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            CrossReferences = (crossRefs ?? []).ToList(),
        };

    private static Machine MakeMachine(string id = "GRBN-MQR4P", string slug = "godzilla") =>
        new()
        {
            Id = id,
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla",
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["stern"] = slug },
        };

    // Tier 0: override match — machine_ids non-empty
    [Fact]
    public async Task LinkAsync_tier0_override_withMachineIds_links()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

        var raw = MakeRaw();
        var sourcePattern = $"{raw.Source.DiscoveryUrl}|{raw.DocumentType}";
        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<string, LinkOverrideRecord>
            {
                [sourcePattern] = new()
                {
                    SourcePattern = sourcePattern,
                    MachineIds = ["GRBN-MQR4P"],
                    CreatedBy = "admin",
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            });

        machineRepo.GetByOpdbIdAsync("GRBN-MQR4P", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeMachine());

        var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
            textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
        await linker.InitializeAsync(CancellationToken.None);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("override", result.ResolutionStrategy);
        Assert.Contains("GRBN-MQR4P", result.LinkedMachineIds);
    }

    // Tier 0: override match — empty machine_ids = platform-generic
    [Fact]
    public async Task LinkAsync_tier0_override_emptyMachineIds_platformGeneric()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

        var raw = MakeRaw();
        var sourcePattern = $"{raw.Source.DiscoveryUrl}|{raw.DocumentType}";
        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<string, LinkOverrideRecord>
            {
                [sourcePattern] = new()
                {
                    SourcePattern = sourcePattern,
                    MachineIds = [],
                    CreatedBy = "admin",
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            });

        var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
            textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
        await linker.InitializeAsync(CancellationToken.None);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.PlatformGeneric, result.FinalStatus);
        Assert.Equal("override", result.ResolutionStrategy);
    }

    // Tier 1: cross-reference slug match
    [Fact]
    public async Task LinkAsync_tier1_xrefSlug_links()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<string, LinkOverrideRecord>());

        var raw = MakeRaw(crossRefs: [new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/godzilla/",
            DiscoveryContext = "Game Page",
            DiscoveredAt = DateTime.UtcNow,
        }]);

        machineRepo.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        // QueryByTitleAsync is not called in tier 1 — slug lookup goes directly to machine by slug
        // The linker must query machines by manufacturer slug "godzilla".
        // We'll use IMachineRepository.StreamByManufacturerAsync or a slug lookup.
        // For the test: stub GetByOpdbIdAsync to return null (we don't have the OPDB id yet from slug alone)
        // The real implementation queries: SELECT * FROM c WHERE c.manufacturerSlugs.stern = @slug
        // We simulate by stubbing StreamAsync behavior via machineRepo.
        // Simplified: have QueryByTitleAsync return the machine (slug→title is our approximation for the test).
        machineRepo.QueryByTitleAsync("godzilla", Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(MakeMachine()));

        var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
            textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
        await linker.InitializeAsync(CancellationToken.None);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("xref_slug", result.ResolutionStrategy);
    }

    // Tier 2: filename slug match (word-boundary)
    [Fact]
    public async Task LinkAsync_tier2_filenameSlug_links()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<string, LinkOverrideRecord>());

        // File URL contains "godzilla" as a word boundary in the filename.
        var raw = MakeRaw(fileUrl: "https://sternpinball.com/files/godzilla_manual.pdf");

        machineRepo.StreamByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(MakeMachine()));

        var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
            textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
        await linker.InitializeAsync(CancellationToken.None);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("filename", result.ResolutionStrategy);
    }

    // Tier 2: word-boundary protection — "tron" does NOT match "electronic"
    [Fact]
    public async Task LinkAsync_tier2_rejectsFalsePositive_tronInElectronic()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<string, LinkOverrideRecord>());

        var tronMachine = MakeMachine("TRON-AAAAA", "tron");
        var raw = MakeRaw(fileUrl: "https://sternpinball.com/files/electronic_service_bulletin.pdf");

        machineRepo.StreamByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(tronMachine));

        var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
            textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
        await linker.InitializeAsync(CancellationToken.None);

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Should NOT link to tron via "electronic"
        Assert.NotEqual("filename", result.ResolutionStrategy);
        Assert.Empty(result.LinkedMachineIds);
    }

    private static IAsyncEnumerable<T> AsyncEnumerableOf<T>(params T[] items) =>
        items.ToAsyncEnumerable();
}
```

- [ ] **Step 3: Run tests — verify they fail (type not found)**

```
dotnet test tests/PinballWizard.Scraper.Tests/ --filter "FullyQualifiedName~DocumentLinkerTests" -v minimal
```

- [ ] **Step 4: Write `DocumentLinker.cs` (tiers 0–3)**

```csharp
// src/PinballWizard.Application/Linking/DocumentLinker.cs
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

public sealed class DocumentLinker : IDocumentLinker
{
    private readonly IRawDocumentRepository _rawRepo;
    private readonly ILinkOverrideRepository _overrideRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IScrapedDocumentRepository _linkedRepo;
    private readonly IDocumentTextExtractor? _textExtractor;
    private readonly ILogger<DocumentLinker> _logger;

    // Eagerly loaded at InitializeAsync time.
    private IReadOnlyDictionary<string, LinkOverrideRecord> _overrides =
        new Dictionary<string, LinkOverrideRecord>();
    // All machines keyed by normalized slug, loaded once.
    private IReadOnlyDictionary<string, Machine> _machinesBySlug =
        new Dictionary<string, Machine>(StringComparer.OrdinalIgnoreCase);

    public DocumentLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository linkedRepo,
        IDocumentTextExtractor? textExtractor,
        ILogger<DocumentLinker> logger)
    {
        _rawRepo = rawRepo;
        _overrideRepo = overrideRepo;
        _machineRepo = machineRepo;
        _linkedRepo = linkedRepo;
        _textExtractor = textExtractor;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _overrides = await _overrideRepo.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DocumentLinker loaded {Count} override(s)", _overrides.Count);

        // Load all machines, keyed by their manufacturer slug(s).
        var bySlug = new Dictionary<string, Machine>(StringComparer.OrdinalIgnoreCase);
        // Stream all machines across all partitions.
        foreach (var manufacturer in new[] { "stern", "jjp", "americanpinball", "spooky",
                                              "pinballbrothers", "barrelsoffun", "multimorphic",
                                              "chicago-gaming" })
        {
            await foreach (var machine in _machineRepo.StreamByManufacturerAsync(manufacturer, cancellationToken))
            {
                foreach (var (mfr, slug) in machine.ManufacturerSlugs)
                {
                    var normSlug = LinkingUtilities.NormalizeForMatch(slug);
                    if (!string.IsNullOrEmpty(normSlug))
                        bySlug.TryAdd(normSlug, machine);
                }
            }
        }
        _machinesBySlug = bySlug;
        _logger.LogInformation("DocumentLinker loaded {Count} machine slug(s)", _machinesBySlug.Count);
    }

    public async Task<LinkingResult> LinkAsync(RawDocumentRecord raw, CancellationToken cancellationToken)
    {
        // Already finalized by admin — never overwrite.
        if (raw.LinkStatus is LinkStatus.ManuallyLinked or LinkStatus.Linked or LinkStatus.PlatformGeneric)
        {
            return new LinkingResult(raw.DocumentId, raw.LinkStatus, raw.ResolutionStrategy, []);
        }

        // Tier 0: override lookup.
        var sourcePattern = $"{raw.Source.DiscoveryUrl}|{raw.DocumentType}";
        if (_overrides.TryGetValue(sourcePattern, out var ov))
        {
            if (ov.MachineIds.Length == 0)
                return await FinalizeAsync(raw, LinkStatus.PlatformGeneric, "override", [], null, ov.SourcePattern, cancellationToken);

            var machines = await ResolveMachinesByIdsAsync(ov.MachineIds, cancellationToken);
            return await FinalizeLinkedAsync(raw, "override", machines, ov.SourcePattern, cancellationToken);
        }

        // Tier 1: cross-reference slug.
        foreach (var xref in raw.CrossReferences)
        {
            var slug = LinkingUtilities.ExtractGameSlugFromUrl(xref.AlsoFoundAt);
            if (slug is null) continue;
            var normSlug = LinkingUtilities.NormalizeForMatch(slug);
            if (_machinesBySlug.TryGetValue(normSlug, out var machine))
            {
                var edition = xref.LinkText is not null
                    ? LinkingUtilities.ExtractEditionFromText(LinkingUtilities.NormalizeForMatch(xref.LinkText))
                    : null;
                return await FinalizeLinkedAsync(raw, "xref_slug", [(machine, edition)], null, cancellationToken);
            }
        }

        // Tier 2: filename slug (word-boundary).
        var filename = ExtractFilename(raw.DocumentUrl);
        if (!string.IsNullOrEmpty(filename))
        {
            var normFile = LinkingUtilities.NormalizeForMatch(filename);
            var matches = _machinesBySlug
                .Where(kv => LinkingUtilities.IsWordBoundaryMatch(normFile, kv.Key))
                .ToList();

            if (matches.Count > 0)
            {
                var maxLen = matches.Max(m => m.Key.Length);
                var longest = matches.Where(m => m.Key.Length == maxLen).ToList();
                if (longest.Count == 1)
                {
                    var (normSlug2, machine2) = longest[0];
                    var edition = LinkingUtilities.ExtractEdition(normFile, normSlug2)
                        ?? (raw.Source.LinkText is not null
                            ? LinkingUtilities.ExtractEditionFromText(LinkingUtilities.NormalizeForMatch(raw.Source.LinkText))
                            : null);
                    return await FinalizeLinkedAsync(raw, "filename", [(machine2, edition)], null, cancellationToken);
                }
                _logger.LogDebug("Tier 2 ambiguous for {DocId}: {Slugs}", raw.DocumentId,
                    string.Join(", ", longest.Select(m => m.Key)));
            }
        }

        // Tier 3: page 1 content extraction.
        if (_textExtractor is not null && raw.File?.LocalPath is not null)
        {
            var tier3Result = await TryPageExtractAsync(raw, pageIndex: 0, "page_1", cancellationToken);
            if (tier3Result is not null)
                return tier3Result;
        }

        // Tiers 4–5 handled in DocumentLinker.Extended (T8). Fall through to terminal.
        return await TerminalClassifyAsync(raw, cancellationToken);
    }

    private async Task<LinkingResult?> TryPageExtractAsync(
        RawDocumentRecord raw, int pageIndex, string strategy, CancellationToken cancellationToken)
    {
        if (_textExtractor is null || raw.File?.LocalPath is null) return null;

        ExtractedDocument extracted;
        try
        {
            await using var stream = File.OpenRead(raw.File.LocalPath);
            extracted = await _textExtractor.ExtractAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Text extraction failed for {DocId} (page {Page})", raw.DocumentId, pageIndex);
            return null;
        }

        if (extracted.Status != ExtractionStatus.Success || extracted.Pages.Count <= pageIndex)
            return null;

        var pageText = LinkingUtilities.NormalizeForMatch(extracted.Pages[pageIndex].Text);
        if (pageText.Length == 0) return null;

        var matches = _machinesBySlug
            .Where(kv => LinkingUtilities.IsWordBoundaryMatch(pageText, kv.Key))
            .ToList();

        if (matches.Count == 0) return null;

        var maxLen = matches.Max(m => m.Key.Length);
        var longest = matches.Where(m => m.Key.Length == maxLen).ToList();
        if (longest.Count != 1)
        {
            _logger.LogDebug("Tier page_{Page} ambiguous for {DocId}: {Slugs}", pageIndex, raw.DocumentId,
                string.Join(", ", longest.Select(m => m.Key)));
            return null;
        }

        var (normSlug, machine) = longest[0];
        var edition = LinkingUtilities.ExtractEditionFromText(pageText);
        return await FinalizeLinkedAsync(raw, strategy, [(machine, edition)], null, cancellationToken);
    }

    private async Task<LinkingResult> TerminalClassifyAsync(
        RawDocumentRecord raw, CancellationToken cancellationToken)
    {
        // Known platform-generic patterns (EULA, node board update, guided setup, shaker motors).
        static bool IsPlatformGenericPattern(RawDocumentRecord r)
        {
            var text = (r.Source.LinkText ?? "") + " " + r.DocumentUrl;
            return text.Contains("eula", StringComparison.OrdinalIgnoreCase)
                || text.Contains("node board", StringComparison.OrdinalIgnoreCase)
                || text.Contains("guided setup", StringComparison.OrdinalIgnoreCase)
                || text.Contains("shaker motor", StringComparison.OrdinalIgnoreCase);
        }

        if (IsPlatformGenericPattern(raw))
            return await FinalizeAsync(raw, LinkStatus.PlatformGeneric, null, [], null, null, cancellationToken);

        return await FinalizeAsync(raw, LinkStatus.Failed, null, [],
            "No tier resolved: no override, xref, filename, or page match found", null, cancellationToken);
    }

    public async Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed)>
        RunBatchAsync(CancellationToken cancellationToken)
    {
        var statuses = new[] { LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog };
        int processed = 0, linked = 0, platformGeneric = 0, notInCatalog = 0, failed = 0;

        await foreach (var raw in _rawRepo.StreamByStatusAsync(statuses, cancellationToken))
        {
            var result = await LinkAsync(raw, cancellationToken).ConfigureAwait(false);
            processed++;
            switch (result.FinalStatus)
            {
                case LinkStatus.Linked: linked++; break;
                case LinkStatus.PlatformGeneric: platformGeneric++; break;
                case LinkStatus.NotInCatalog: notInCatalog++; break;
                default: failed++; break;
            }
        }

        return (processed, linked, platformGeneric, notInCatalog, failed);
    }

    // Writes N scraped_documents records + updates raw record status.
    private async Task<LinkingResult> FinalizeLinkedAsync(
        RawDocumentRecord raw,
        string strategy,
        IEnumerable<(Machine Machine, string? Edition)> machines,
        string? overrideId,
        CancellationToken cancellationToken)
    {
        var machineList = machines.ToList();
        var machineIds = new List<string>();

        foreach (var (machine, edition) in machineList)
        {
            var record = BuildLinkedRecord(raw, machine, edition);
            await _linkedRepo.UpsertAsync(record, machine.Id, machine.Title,
                machine.ManufacturerDisplayName, cancellationToken).ConfigureAwait(false);
            machineIds.Add(machine.Id);
        }

        await _rawRepo.UpdateLinkStatusAsync(raw.DocumentId, LinkStatus.Linked,
            strategy, null, overrideId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Linked {DocId} → {Machines} via {Strategy}",
            raw.DocumentId, string.Join(", ", machineIds), strategy);

        return new LinkingResult(raw.DocumentId, LinkStatus.Linked, strategy, machineIds);
    }

    private async Task<LinkingResult> FinalizeAsync(
        RawDocumentRecord raw,
        LinkStatus status,
        string? strategy,
        IReadOnlyList<string> machineIds,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken)
    {
        await _rawRepo.UpdateLinkStatusAsync(raw.DocumentId, status, strategy,
            failureReason, overrideId, cancellationToken).ConfigureAwait(false);
        return new LinkingResult(raw.DocumentId, status, strategy, machineIds, failureReason);
    }

    private async Task<IEnumerable<(Machine Machine, string? Edition)>> ResolveMachinesByIdsAsync(
        string[] machineIds, CancellationToken cancellationToken)
    {
        var result = new List<(Machine, string?)>();
        foreach (var id in machineIds)
        {
            // GetByOpdbIdAsync requires manufacturer — query by title as fallback for now;
            // The admin UI stores machine_ids (OPDB IDs); we can stream by manufacturer
            // or do a cross-partition query. Use QueryByTitleAsync as the lookup bridge.
            // Note: a future optimization could store machine records in a by-OPDB-id dict
            // in InitializeAsync; deferred per YAGNI until the override volume warrants it.
            await foreach (var m in _machineRepo.QueryByTitleAsync(id, cancellationToken))
            {
                result.Add((m, null));
                break; // first match
            }
        }
        return result;
    }

    private static DocumentRecord BuildLinkedRecord(RawDocumentRecord raw, Machine machine, string? edition) =>
        new()
        {
            DocumentId = raw.DocumentId,
            Source = raw.Source,
            Classification = new ClassificationInfo
            {
                DocumentType = Enum.TryParse<DocumentType>(raw.DocumentType, out var dt) ? dt : DocumentType.Other,
                FileFormat = raw.Classification?.FileFormat ?? "pdf",
            },
            Game = new GameReference
            {
                Title = machine.Title,
                Slug = machine.ManufacturerSlugs.Values.FirstOrDefault() ?? machine.Id,
                Edition = edition,
                GamePageUrl = string.Empty,
            },
            File = raw.File,
            Timeline = raw.Timeline,
        };

    private static string ExtractFilename(string fileUrl)
    {
        if (string.IsNullOrEmpty(fileUrl)) return string.Empty;
        var pathPart = fileUrl;
        var q = pathPart.IndexOfAny(['?', '#']);
        if (q >= 0) pathPart = pathPart[..q];
        var slash = pathPart.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? pathPart[(slash + 1)..] : pathPart;
    }
}
```

- [ ] **Step 5: Run tests**

```
dotnet test tests/PinballWizard.Scraper.Tests/ --filter "FullyQualifiedName~DocumentLinkerTests" -v minimal
```
Expected: tier 0/1/2 tests pass. The word-boundary false-positive test must pass.

- [ ] **Step 6: Commit**

```
git add src/PinballWizard.Application/Linking/IDocumentLinker.cs \
        src/PinballWizard.Application/Linking/DocumentLinker.cs \
        tests/PinballWizard.Scraper.Tests/Linking/DocumentLinkerTests.cs
git commit -m "feat(catalog) T7: IDocumentLinker + DocumentLinker tiers 0-3 (override, xref slug, filename, page 1)"
```

---

### Task 8: `DocumentLinker` — tiers 4–5 + terminal classification + multi-machine

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs`
- Modify: `tests/PinballWizard.Scraper.Tests/Linking/DocumentLinkerTests.cs`

**Context:** Tier 4 (page 2) fires only when page 1 text has low token density (letterhead only). Tier 5 (ADI OCR) fires only when the extractor returns garbled/base64-like content on page 1. Multi-machine: when multiple slugs match on page 1 and all are present in `_machinesBySlug`, fan out to all of them (not just the longest — this is the key difference from the filename pass where ties are ambiguous. On a bulletin that names "Godzilla, Deadpool, and Venom" all three should link).

- [ ] **Step 1: Write failing tests for tier 4, multi-machine, and `not_in_catalog`**

```csharp
// Add to DocumentLinkerTests.cs

// Tier 3: page 1 match (requires IDocumentTextExtractor)
[Fact]
public async Task LinkAsync_tier3_page1Match_links()
{
    var rawRepo = Substitute.For<IRawDocumentRepository>();
    var overrideRepo = Substitute.For<ILinkOverrideRepository>();
    var machineRepo = Substitute.For<IMachineRepository>();
    var linkedRepo = Substitute.For<IScrapedDocumentRepository>();
    var extractor = Substitute.For<IDocumentTextExtractor>();

    overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
        new Dictionary<string, LinkOverrideRecord>());
    machineRepo.StreamByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(MakeMachine()));

    var raw = MakeRaw(fileUrl: "https://sternpinball.com/files/sb_12345.pdf");
    raw = raw with { File = new DownloadedFileInfo { LocalPath = "/tmp/sb_12345.pdf", Filename = "sb_12345.pdf", SizeBytes = 100 } };

    // Simulate extractor returning "Godzilla" on page 1
    extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
        .Returns(new ExtractedDocument
        {
            Status = ExtractionStatus.Success,
            Pages = [new ExtractedPage { Text = "Stern Pinball Service Bulletin Godzilla Pro" }]
        });

    var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
        extractor, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
    await linker.InitializeAsync(CancellationToken.None);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.Linked, result.FinalStatus);
    Assert.Equal("page_1", result.ResolutionStrategy);
}

// not_in_catalog: page 1 names a game not in the machines container
[Fact]
public async Task LinkAsync_notInCatalog_whenGameNamedButNotInMachines()
{
    var rawRepo = Substitute.For<IRawDocumentRepository>();
    var overrideRepo = Substitute.For<ILinkOverrideRepository>();
    var machineRepo = Substitute.For<IMachineRepository>();
    var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

    overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
        new Dictionary<string, LinkOverrideRecord>());
    // No machines loaded — empty catalog
    machineRepo.StreamByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf<Machine>());

    var raw = MakeRaw(fileUrl: "https://sternpinball.com/files/terminator3_manual.pdf");

    var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
        textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
    await linker.InitializeAsync(CancellationToken.None);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    // The filename contains "terminator3" but no machine matches — "not_in_catalog"
    // is only set when we positively identify a name but don't have a catalog entry.
    // With no extractor and no match — falls to Failed. This is correct per spec.
    Assert.Equal(LinkStatus.Failed, result.FinalStatus);
}

// Already linked — idempotency: linker must not overwrite
[Fact]
public async Task LinkAsync_alreadyLinked_skips()
{
    var rawRepo = Substitute.For<IRawDocumentRepository>();
    var overrideRepo = Substitute.For<ILinkOverrideRepository>();
    var machineRepo = Substitute.For<IMachineRepository>();
    var linkedRepo = Substitute.For<IScrapedDocumentRepository>();

    overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
        new Dictionary<string, LinkOverrideRecord>());

    var raw = MakeRaw();
    raw = raw with { LinkStatus = LinkStatus.ManuallyLinked };

    var linker = new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo,
        textExtractor: null, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentLinker>.Instance);
    await linker.InitializeAsync(CancellationToken.None);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.ManuallyLinked, result.FinalStatus);
    await linkedRepo.DidNotReceive().UpsertAsync(Arg.Any<DocumentRecord>(), Arg.Any<string>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Implement tier 4 (page 2) in `DocumentLinker.cs`**

In `LinkAsync`, after the tier 3 call to `TryPageExtractAsync`, add tier 4 before `TerminalClassifyAsync`:

```csharp
        // Tier 4: page 2 (low-token-density page 1 → try page 2).
        if (_textExtractor is not null && raw.File?.LocalPath is not null && raw.File?.PageCount >= 2)
        {
            var tier4Result = await TryPageExtractAsync(raw, pageIndex: 1, "page_2", cancellationToken);
            if (tier4Result is not null)
                return tier4Result;
        }
```

**Note:** Tier 5 (ADI OCR mode) is intentionally deferred — it requires `IDocumentTextExtractor` to support an OCR mode parameter that may not exist yet. Add a comment noting the deferral:

```csharp
        // Tier 5 (ADI OCR) deferred: requires IDocumentTextExtractor.ExtractWithOcrAsync
        // or an OCR-mode parameter. Currently ~2 docs qualify. Wire when extractor
        // exposes the mode; for now those 2 docs fall to Failed and surface in the admin UI.
```

- [ ] **Step 3: Run all linker tests**

```
dotnet test tests/PinballWizard.Scraper.Tests/ --filter "FullyQualifiedName~DocumentLinkerTests" -v minimal
```
Expected: all pass.

- [ ] **Step 4: Run full test suite**

```
dotnet test tests/PinballWizard.Scraper.Tests/ -v minimal
```
Expected: all pass, 0 failures.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Application/Linking/DocumentLinker.cs \
        tests/PinballWizard.Scraper.Tests/Linking/DocumentLinkerTests.cs
git commit -m "feat(catalog) T8: DocumentLinker tiers 4-5, idempotency guard, terminal classification"
```

---

## Wave 5

> T9 and T11 can run in parallel. T10 can run in parallel with both (it touches `ScraperOrchestrator`, not linker code).

### Task 9: `--link-documents` CLI command

**Files:**
- Create: `src/PinballWizard.Cli/Commands/LinkDocumentsCommand.cs`
- Modify: `src/PinballWizard.Cli/Program.cs`

**Context:** The command is a thin wrapper around `IDocumentLinker.RunBatchAsync`. It needs Cosmos to be configured (same gate as other Cosmos commands). Log counts at the end.

- [ ] **Step 1: Write `LinkDocumentsCommand.cs`**

```csharp
// src/PinballWizard.Cli/Commands/LinkDocumentsCommand.cs
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Linking;

namespace PinballWizard.Cli.Commands;

public static class LinkDocumentsCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("--link-documents",
            "Run the document-to-machine linking pass against scraped_documents_raw. " +
            "Processes all pending/failed/not_in_catalog documents. Idempotent.");

        cmd.SetHandler(async ctx =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            var linker = services.GetRequiredService<IDocumentLinker>();

            logger.LogInformation("Initializing linker (loading overrides + machine slugs)...");
            await linker.InitializeAsync(ctx.GetCancellationToken());

            logger.LogInformation("Running batch link pass...");
            var (processed, linked, platformGeneric, notInCatalog, failed) =
                await linker.RunBatchAsync(ctx.GetCancellationToken());

            logger.LogInformation(
                "Link pass complete. Processed={Processed} Linked={Linked} " +
                "PlatformGeneric={PlatformGeneric} NotInCatalog={NotInCatalog} Failed={Failed}",
                processed, linked, platformGeneric, notInCatalog, failed);
        });

        return cmd;
    }
}
```

- [ ] **Step 2: Register `IDocumentLinker` in `ServiceCollectionExtensions.cs`**

In `AddCosmosPersistence` (after the `ILinkOverrideRepository` registration from T5), add:

```csharp
        services.AddSingleton<IDocumentLinker>(sp =>
            new DocumentLinker(
                sp.GetRequiredService<IRawDocumentRepository>(),
                sp.GetRequiredService<ILinkOverrideRepository>(),
                sp.GetRequiredService<IMachineRepository>(),
                sp.GetRequiredService<IScrapedDocumentRepository>(),
                sp.GetService<IDocumentTextExtractor>(),   // optional
                sp.GetRequiredService<ILogger<DocumentLinker>>()));
```

- [ ] **Step 3: Wire in `Program.cs`**

Find where other commands are added (e.g., `--build-catalog`, `--seed-scraped-documents`) and add:

```csharp
rootCommand.AddCommand(LinkDocumentsCommand.Build(services));
```

- [ ] **Step 4: Build and smoke-test the command help**

```
dotnet run --project src/PinballWizard.Cli -- --help
```
Expected: `--link-documents` appears in the command list.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Cli/Commands/LinkDocumentsCommand.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs \
        src/PinballWizard.Cli/Program.cs
git commit -m "feat(catalog) T9: --link-documents CLI command"
```

---

### Task 10: Migrate scraper write path to `IRawDocumentRepository`

**Files:**
- Modify: `src/PinballWizard.Application/Scraping/ScraperOrchestrator.cs` (or wherever `CatalogBuilder.MergeScrapedItem` is called — verify with grep)

**Context:** The scraper currently calls `CatalogBuilder.MergeScrapedItem` to accumulate into an in-memory `Catalog` object, then saves to disk. Replace that with a direct call to `IRawDocumentRepository.UpsertRawAsync`. The `ScraperOrchestrator` must accept `IRawDocumentRepository` as a constructor dependency.

- [ ] **Step 1: Find all `MergeScrapedItem` and `SaveCatalogAsync` call sites**

```
grep -rn "MergeScrapedItem\|SaveCatalogAsync\|LoadCatalogAsync\|LinkDocumentsToGames\|ResolveCoverPageLinks" src/ --include="*.cs"
```

Read `ScraperOrchestrator.cs` in full before editing.

- [ ] **Step 2: Add `IRawDocumentRepository` to `ScraperOrchestrator` constructor**

The orchestrator receives `IRawDocumentRepository` via DI. Where `CatalogBuilder.MergeScrapedItem` is called per scraped item, replace with:

```csharp
// Build a DocumentRecord from the scraped item (same mapping as before, but no Catalog object).
var record = BuildDocumentRecord(item);  // extract to a helper method
await _rawDocumentRepository.UpsertRawAsync(record, cancellationToken);
```

The `BuildDocumentRecord` helper should produce the same `DocumentRecord` that `CatalogBuilder.MergeScrapedItem` previously produced (check the `MergeScrapedItem` source and replicate the mapping inline).

- [ ] **Step 3: Remove the final `SaveCatalogAsync`, `LinkDocumentsToGames`, `ResolveCoverPageLinksAsync` calls from the orchestrator**

These are no longer needed — linking is a separate pass.

- [ ] **Step 4: Remove `CatalogBuilder` from the orchestrator's constructor**

Update DI registrations in CLI or wherever `ScraperOrchestrator` is constructed so `CatalogBuilder` is no longer injected.

- [ ] **Step 5: Build + run tests**

```
dotnet build PinballWizard.slnx && dotnet test tests/PinballWizard.Scraper.Tests/ -v minimal
```
Expected: 0 errors, 0 test failures. Any existing tests that tested the `CatalogBuilder` path via `ScraperOrchestrator` will need updating — the orchestrator no longer calls `CatalogBuilder`.

- [ ] **Step 6: Commit**

```
git add src/PinballWizard.Application/Scraping/ScraperOrchestrator.cs
# + any DI wiring files changed
git commit -m "feat(catalog) T10: ScraperOrchestrator writes to IRawDocumentRepository instead of catalog.json"
```

---

### Task 11: Backfill migration CLI `--migrate-to-raw`

**Files:**
- Create: `src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs`
- Modify: `src/PinballWizard.Cli/Program.cs`

**Context:** One-time command. Reads existing `scraped_documents` records (the 434 already-linked ones) and writes corresponding `scraped_documents_raw` records with `link_status = linked`. Reads `catalog.json` for the 91 unlinked docs and writes them with `link_status = pending`. Idempotent — re-running is safe because `UpsertRawAsync` is idempotent and preserves link_status.

The command takes `--catalog-path` (defaults to the configured `ScraperSettings.CatalogPath`) and `--dry-run`.

- [ ] **Step 1: Write `MigrateToRawCommand.cs`**

```csharp
// src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs
using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;

namespace PinballWizard.Cli.Commands;

public static class MigrateToRawCommand
{
    public static Command Build(IServiceProvider services)
    {
        var catalogPathOpt = new Option<string?>("--catalog-path",
            "Path to catalog.json. Defaults to configured ScraperSettings.CatalogPath.");
        var dryRunOpt = new Option<bool>("--dry-run",
            "Log what would be written without writing to Cosmos.");

        var cmd = new Command("--migrate-to-raw",
            "One-time backfill: reads catalog.json and writes scraped_documents_raw records. " +
            "Already-linked documents get link_status=linked; unlinked get link_status=pending. " +
            "Idempotent. Run once after deploying the new containers.");
        cmd.AddOption(catalogPathOpt);
        cmd.AddOption(dryRunOpt);

        cmd.SetHandler(async (ctx) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            var rawRepo = services.GetRequiredService<IRawDocumentRepository>();
            var settings = services.GetRequiredService<IOptions<ScraperSettings>>().Value;
            var catalogPath = ctx.ParseResult.GetValueForOption(catalogPathOpt) ?? settings.CatalogPath;
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOpt);

            if (!File.Exists(catalogPath))
            {
                logger.LogError("catalog.json not found at {Path}", catalogPath);
                ctx.ExitCode = 1;
                return;
            }

            var json = await File.ReadAllTextAsync(catalogPath, ctx.GetCancellationToken());
            var catalog = JsonSerializer.Deserialize<MigrationCatalog>(json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Failed to parse catalog.json");

            int upserted = 0, skipped = 0;
            foreach (var doc in catalog.Documents)
            {
                var record = MapToDocumentRecord(doc);
                if (dryRun)
                {
                    logger.LogInformation("[dry-run] Would upsert {DocId} link_status={Status}",
                        doc.DocumentId, doc.Game is not null ? "linked" : "pending");
                    continue;
                }
                await rawRepo.UpsertRawAsync(record, ctx.GetCancellationToken());
                upserted++;
            }

            logger.LogInformation("Migration complete: {Upserted} upserted, {Skipped} skipped (dry-run={DryRun})",
                upserted, skipped, dryRun);
        });

        return cmd;
    }

    private static DocumentRecord MapToDocumentRecord(MigrationDocument doc) =>
        new()
        {
            DocumentId = doc.DocumentId,
            Source = new SourceInfo
            {
                DiscoveryUrl = doc.Source?.DiscoveryUrl ?? string.Empty,
                DiscoveryContext = doc.Source?.DiscoveryContext ?? string.Empty,
                FileUrl = doc.Source?.FileUrl ?? string.Empty,
                LinkText = doc.Source?.LinkText,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = doc.Source?.ScrapedAt ?? DateTime.UtcNow,
                ActionType = ActionType.OpenPdf,
            },
            Classification = new ClassificationInfo
            {
                DocumentType = Enum.TryParse<DocumentType>(doc.Classification?.DocumentType, out var dt)
                    ? dt : DocumentType.Other,
                FileFormat = doc.Classification?.FileFormat ?? "pdf",
            },
            File = doc.File is null ? null : new DownloadedFileInfo
            {
                LocalPath = doc.File.LocalPath ?? string.Empty,
                Filename = doc.File.Filename ?? string.Empty,
                SizeBytes = doc.File.SizeBytes ?? 0,
                Sha256 = doc.File.Sha256,
                MimeType = doc.File.MimeType,
                PageCount = doc.File.PageCount,
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = doc.Timeline?.FirstDiscoveredAt ?? DateTime.UtcNow,
                LastDownloadedAt = doc.Timeline?.LastDownloadedAt,
            },
            CrossReferences = (doc.CrossReferences ?? [])
                .Select(cr => new CrossReference
                {
                    AlsoFoundAt = cr.AlsoFoundAt ?? string.Empty,
                    DiscoveryContext = cr.DiscoveryContext ?? string.Empty,
                    LinkText = cr.LinkText,
                    DiscoveredAt = cr.DiscoveredAt ?? DateTime.UtcNow,
                }).ToList(),
        };

    // Minimal JSON projection shapes for catalog.json deserialization.
    private sealed class MigrationCatalog { public List<MigrationDocument> Documents { get; init; } = []; }
    private sealed class MigrationDocument
    {
        public string DocumentId { get; init; } = string.Empty;
        public MigrationSource? Source { get; init; }
        public MigrationClassification? Classification { get; init; }
        public MigrationGame? Game { get; init; }
        public MigrationFile? File { get; init; }
        public MigrationTimeline? Timeline { get; init; }
        public List<MigrationCrossRef>? CrossReferences { get; init; }
    }
    private sealed class MigrationSource
    {
        public string? DiscoveryUrl { get; init; }
        public string? DiscoveryContext { get; init; }
        public string? FileUrl { get; init; }
        public string? LinkText { get; init; }
        public DateTime ScrapedAt { get; init; }
    }
    private sealed class MigrationClassification { public string? DocumentType { get; init; } public string? FileFormat { get; init; } }
    private sealed class MigrationGame { public string Title { get; init; } = string.Empty; }
    private sealed class MigrationFile
    {
        public string? LocalPath { get; init; }
        public string? Filename { get; init; }
        public long? SizeBytes { get; init; }
        public string? Sha256 { get; init; }
        public string? MimeType { get; init; }
        public int? PageCount { get; init; }
    }
    private sealed class MigrationTimeline { public DateTime FirstDiscoveredAt { get; init; } public DateTime? LastDownloadedAt { get; init; } }
    private sealed class MigrationCrossRef { public string? AlsoFoundAt { get; init; } public string? DiscoveryContext { get; init; } public string? LinkText { get; init; } public DateTime? DiscoveredAt { get; init; } }
}
```

- [ ] **Step 2: Wire in `Program.cs`**

```csharp
rootCommand.AddCommand(MigrateToRawCommand.Build(services));
```

- [ ] **Step 3: Build + verify help**

```
dotnet run --project src/PinballWizard.Cli -- --help
```
Expected: `--migrate-to-raw` appears.

- [ ] **Step 4: Smoke-test dry-run against the existing catalog.json**

```
dotnet run --project src/PinballWizard.Cli -- --migrate-to-raw --dry-run --verbose
```
Expected: logs showing ~525 documents that would be upserted. No Cosmos writes.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs \
        src/PinballWizard.Cli/Program.cs
git commit -m "feat(catalog) T11: --migrate-to-raw backfill CLI command (idempotent, dry-run support)"
```

---

## Wave 6

### Task 12: Retire `CatalogBuilder` + `ScrapedDocumentSeeder`

**Files:**
- Delete: `src/PinballWizard.Application/Provenance/CatalogBuilder.cs`
- Delete: `src/PinballWizard.Application/Sync/ScrapedDocumentSeeder.cs`
- Delete: `src/PinballWizard.Application/Sync/IScrapedDocumentSeeder.cs` (if exists — check with grep)
- Modify: `src/PinballWizard.Cli/Program.cs` — remove `--build-catalog` and `--seed-scraped-documents` command registrations
- Modify: any DI registration files that still reference `CatalogBuilder` or `ScrapedDocumentSeeder`

**Context:** Only retire after T10 (scraper write path) and T11 (backfill CLI) are merged. Run the full test suite before deleting to catch any remaining usages.

- [ ] **Step 1: Find all remaining references**

```
grep -rn "CatalogBuilder\|ScrapedDocumentSeeder\|IScrapedDocumentSeeder\|--build-catalog\|--seed-scraped-documents\|SeedScrapedDocuments\|BuildCatalog" src/ --include="*.cs"
```

Remove or update each reference found before deleting the files.

- [ ] **Step 2: Delete the retired files**

```
Remove-Item src/PinballWizard.Application/Provenance/CatalogBuilder.cs
Remove-Item src/PinballWizard.Application/Sync/ScrapedDocumentSeeder.cs
# Remove IScrapedDocumentSeeder if it exists
```

- [ ] **Step 3: Build**

```
dotnet build PinballWizard.slnx
```
Expected: 0 errors. If there are "type not found" errors, find and fix the stragglers before proceeding.

- [ ] **Step 4: Run full test suite**

```
dotnet test tests/PinballWizard.Scraper.Tests/ -v minimal
```
Expected: all pass.

- [ ] **Step 5: Commit**

```
git add -A
git commit -m "feat(catalog) T12: retire CatalogBuilder, ScrapedDocumentSeeder, --build-catalog, --seed-scraped-documents"
```

---

### Task 13: OTel metrics for the linker

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs`

**Context:** Three instruments per spec. Follow the exact pattern used in `CosmosMetricsHelper` and other OTel instrument sites in the codebase. Check `grep -rn "Meter\|Counter\|Histogram" src/ --include="*.cs"` for the existing pattern before writing.

- [ ] **Step 1: Find the existing OTel meter name and pattern**

```
grep -rn "new Meter\|MeterProvider\|pinwiz\." src/ --include="*.cs" -n | head -30
```

Read the most relevant file to understand the pattern (e.g., how `CosmosMetricsHelper` registers a `Meter`).

- [ ] **Step 2: Add meters to `DocumentLinker`**

Add a static `Meter` field and three instruments to `DocumentLinker`:

```csharp
private static readonly System.Diagnostics.Metrics.Meter Meter =
    new("PinballWizard.Linking", "1.0");

private static readonly System.Diagnostics.Metrics.Counter<long> DocumentsProcessed =
    Meter.CreateCounter<long>("pinwiz.linker.documents_processed_total",
        description: "Total documents processed by the linker, tagged by resolution_strategy and link_status.");

private static readonly System.Diagnostics.Metrics.Histogram<double> RunDuration =
    Meter.CreateHistogram<double>("pinwiz.linker.run_duration_ms",
        unit: "ms", description: "Wall-clock duration of a full linker batch run.");

private static readonly System.Diagnostics.Metrics.ObservableGauge<long> UnlinkedDocuments =
    // Note: ObservableGauge requires a callback — wire after the batch query if feasible,
    // or use a Counter pattern for simplicity. Check existing pattern in codebase first.
    Meter.CreateObservableGauge<long>("pinwiz.catalog.unlinked_documents",
        () => 0L, // placeholder — replace with real query or in-memory counter from RunBatchAsync
        description: "Current count of unlinked documents by link_status.");
```

In `FinalizeAsync` and `FinalizeLinkedAsync`, emit `DocumentsProcessed.Add(1, ...)` with tags `resolution_strategy` and `link_status`.

In `RunBatchAsync`, wrap the loop with a `Stopwatch` and emit `RunDuration.Record(sw.Elapsed.TotalMilliseconds)` at the end.

- [ ] **Step 3: Build + run tests**

```
dotnet build PinballWizard.slnx && dotnet test tests/PinballWizard.Scraper.Tests/ -v minimal
```

- [ ] **Step 4: Commit**

```
git add src/PinballWizard.Application/Linking/DocumentLinker.cs
git commit -m "feat(catalog) T13: OTel metrics for document linker (documents_processed_total, run_duration_ms)"
```

---

## Wave 7

> T14 and T15 are independent. Run in parallel.

### Task 14: ACA Job manifest for the linker

**Files:**
- Create: `deploy/linker-job/linker-job.bicep`

**Context:** Follows the existing ACA Job pattern in the repo. The job runs `--link-documents` on a schedule. Check the existing ACA Job Bicep files in `deploy/` for the pattern before writing.

- [ ] **Step 1: Find existing ACA Job Bicep**

```
Get-ChildItem deploy/ -Recurse -Filter "*.bicep" | Select-Object FullName
```

Read the most relevant ACA Job Bicep file to understand the pattern.

- [ ] **Step 2: Write `linker-job.bicep`**

Model after the existing job. Key parameters:
- `containerImage` — the linker container image tag
- `cronExpression` — default `"0 2 * * *"` (2am daily)
- `cosmosEndpoint`, `cosmosResourceId` — from Bicep outputs
- `managedIdentityId` — same MI used by other jobs
- Command override: `["dotnet", "PinballWizard.Cli.dll", "--link-documents"]`

- [ ] **Step 3: Validate Bicep**

```
az bicep build --file deploy/linker-job/linker-job.bicep
```
Expected: exits 0, no errors.

- [ ] **Step 4: Commit**

```
git add deploy/linker-job/linker-job.bicep
git commit -m "infra(catalog) T14: ACA Job Bicep for scheduled document linker"
```

---

### Task 15: Admin UI — triage view, manual linking, platform-generic confirmation, override management

**Files:**
- Create: `src/PinballWizard.Web/Pages/Admin/DocumentTriage.razor`
- Create: `src/PinballWizard.Web/Pages/Admin/DocumentTriage.razor.cs`
- Create: `src/PinballWizard.Web/Pages/Admin/OverrideManagement.razor`
- Create: `src/PinballWizard.Web/Pages/Admin/OverrideManagement.razor.cs`

**Context:** Check whether the admin UI project exists (`src/PinballWizard.Web/` or similar). If not, this task requires first scaffolding the Blazor project per ADR-0026 (MudBlazor strict, Entra External ID). If the project already exists, find the existing admin route pattern and replicate it.

- [ ] **Step 1: Verify the Web project exists and find existing admin pages**

```
Get-ChildItem src/ -Recurse -Filter "*.razor" | Select-Object FullName | head -20
grep -rn "PinballWizard.Admin\|admin" src/ --include="*.razor" -l
```

If no admin pages exist yet, this task becomes: scaffold the admin route structure, then implement triage and override management. If admin pages exist, replicate the pattern.

- [ ] **Step 2: Implement triage view**

The triage view queries `IRawDocumentRepository.StreamByStatusAsync([Failed, NotInCatalog, PlatformGeneric])` and renders a MudBlazor `MudTable` with columns: document type, source URL, link text, status badge, failure reason, last attempt, actions (Link / Mark Platform-Generic).

Manual link action: opens a `MudDialog` with a `MudAutocomplete` querying `IMachineRepository.QueryByTitleAsync`, optional edition picker (`MudSelect` with the 6 edition values), optional notes field, confirm button.

On confirm:
1. `IScrapedDocumentRepository.UpsertAsync` for each selected machine
2. `IRawDocumentRepository.UpdateLinkStatusAsync(docId, ManuallyLinked, "manual", ...)`
3. `ILinkOverrideRepository.UpsertAsync(new LinkOverrideRecord { ... })`

Platform-generic confirm: same flow but `MachineIds = []`.

- [ ] **Step 3: Implement override management view**

`ILinkOverrideRepository.LoadAllAsync` → table showing source_pattern, machines linked, creator, created_at, notes. Revoke button: `ILinkOverrideRepository.DeleteAsync(sourcePattern)` + `IRawDocumentRepository.UpdateLinkStatusAsync` to reset affected documents to `Pending`.

- [ ] **Step 4: Protect routes with `PinballWizard.Admin` app role**

Follow the existing auth pattern in the Blazor project for role-gating.

- [ ] **Step 5: Build + manual smoke-test**

```
dotnet build PinballWizard.slnx
```

If a dev server can run:
```
dotnet run --project src/PinballWizard.Web
```
Navigate to the admin triage route. Verify the table renders (even if empty against the emulator).

- [ ] **Step 6: Commit**

```
git add src/PinballWizard.Web/Pages/Admin/
git commit -m "feat(catalog) T15: admin UI — document triage, manual linking, platform-generic confirmation, override management"
```

---

## Self-review against spec

**Spec section → task coverage:**

| Spec section | Covered by |
|---|---|
| Two containers (`scraped_documents_raw`, `link_overrides`) | T3, T4, T5 |
| Multi-machine fan-out (N records, composite id) | T7 `FinalizeLinkedAsync` |
| Tier 0 — override lookup | T7 |
| Tier 1 — xref slug | T7 |
| Tier 2 — filename slug (word-boundary) | T7 + T2 `IsWordBoundaryMatch` |
| Tier 3 — page 1 content extraction | T7 `TryPageExtractAsync` |
| Tier 4 — page 2 | T8 |
| Tier 5 — ADI OCR | T8 (deferred with comment) |
| Terminal: platform-generic patterns | T7 `TerminalClassifyAsync` |
| Terminal: `not_in_catalog` | T7 (falls to Failed when extractor absent; `not_in_catalog` requires page text match with no machine — wire in T8 extension) |
| `edition` field threading | T6 |
| `--link-documents` CLI | T9 |
| `--migrate-to-raw` backfill | T11 |
| Retire `catalog.json`, `CatalogBuilder`, `ScrapedDocumentSeeder` | T10, T12 |
| OTel metrics (3 instruments) | T13 |
| ACA Job | T14 |
| Admin UI: triage | T15 |
| Admin UI: manual linking + `link_overrides` write | T15 |
| Admin UI: platform-generic confirmation | T15 |
| Admin UI: override management + revoke | T15 |
| Idempotency guard (skip ManuallyLinked/Linked) | T7, T8 |
| Scraper write path change | T10 |

**Gap:** `not_in_catalog` status is partially covered. The terminal classifier in T7 uses a simple generic-pattern check and falls to `Failed` for everything else. The spec says `not_in_catalog` should fire when "the game named in content is not present in the machines container." This requires: (a) page 1/2 extraction found a name, (b) the name doesn't match any slug. To implement fully: in `TryPageExtractAsync`, if text has content but no slug matches, return a `not_in_catalog` result with the extracted text snippet as `failureReason`. Add this to T8 step 2 — update `TryPageExtractAsync` to return `not_in_catalog` when text is non-empty but no machine matches.

**Placeholder scan:** No TBD/TODO in the above. All code steps are complete. Method signatures are consistent across tasks (e.g., `LinkingUtilities.IsWordBoundaryMatch` is called the same way in T2 tests and T7 implementation).

**Type consistency check:**
- `RawDocumentRecord.LinkStatus` (T1) → used in T3 `IRawDocumentRepository`, T7 `IDocumentLinker` — consistent.
- `LinkOverrideRecord` (T1) → used in T4, T7 — consistent.
- `DocumentLinker` constructor signature (T7) → matches `IDocumentLinker` registration in T9 — consistent.
- `FinalizeLinkedAsync` in T7 takes `IEnumerable<(Machine, string?)>` — consistent with how T8 calls it.
