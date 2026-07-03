# Tilt Forums Rulesheet Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ingest Tilt Forums' community-maintained rulesheets (~80-90 modern machines across every manufacturer) into the PinballWizard RAG index as `DocumentType.Rulesheet` content, filling the corpus's current gameplay-rules-depth gap.

**Architecture:** A polite HTTP client (`TiltForumsRulesheetsClient`, `PoliteScraperBase`) discovers rulesheets from a manufacturer-grouped master-list wiki page, fetches each Discourse topic's first (wiki) post, and extracts clean text. A manufacturer-scoped matcher (`TiltForumsGameMatcher`) resolves each rulesheet to exactly one catalog `Machine`, refusing to guess on collisions. A synthesizer (`TiltForumsRulesheetsSynthesizer`) chunks the text and a new `--sync-tiltforums-rulesheets` CLI verb writes straight to AI Search via `IRagIndexer`. This is a synthesis pipeline — no Cosmos `DocumentRecord`, no `ScraperOrchestrator`, no `DocumentLinker` — mirroring the existing, production-proven `KineticistTutorialsClient`/`KineticistTutorialsSynthesizer`/`--sync-kineticist-tutorials` pattern exactly, because the standard PDF-download pipeline (`IDocumentTextExtractor`) cannot process inline HTML content.

**Tech Stack:** .NET 10, AngleSharp (HTML parsing), xUnit + NSubstitute (tests), existing `IChunker`/`HybridChunker`, existing `IRagIndexer` (Azure AI Search).

## Global Constraints

- Every outbound HTTP request MUST route through `PoliteScraperBase.GetStringPolitelyAsync` — no bare `HttpClient` calls (LOCKED invariant, `feedback_polite_scraping.md`).
- `robots.txt` is honored unconditionally — already verified permissive for `/t/` and `/c/` paths on `tiltforums.com` (no `Crawl-delay`, no relevant `Disallow`).
- No Cosmos `DocumentRecord`/`RawDocumentRecord` is created for this content — synthesis path only, matching Kineticist/P3-SDK/Freshdesk-articles precedent.
- Never guess on an ambiguous cross-manufacturer title match — log to the unmatched count and move on (Invariant #17, "fallbacks must not hide failures").
- No premature abstraction: no new `SourceType` enum member, no new citation-styling plumbing — none of that is needed for this feature (verified: the codebase has zero per-source-type citation styling anywhere).
- Personal-identity commits only (`Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`), no Claude attribution trailer — already configured in this worktree.
- Design reference: [`docs/superpowers/specs/2026-07-03-tiltforums-rulesheet-scraper-design.md`](../specs/2026-07-03-tiltforums-rulesheet-scraper-design.md). Decision reference: [ADR-0050](../../adr/0050-tiltforums-rulesheet-ingestion.md).

---

## Task 1: `IngestionSourceIds` constant + seed entry

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IngestionSourceIds.cs`
- Modify: `data/seeds/ingestion_sources.v1.json`

**Interfaces:**
- Produces: `IngestionSourceIds.TiltForumsRulesheets` (string constant, value `"tiltforums_rulesheets"`) — consumed by Task 8's CLI verb for logging/consistency and available for any future admin-UI wiring.

This task has no behavior to unit test (a constant and a JSON seed entry) — verified by the solution building and the seed file remaining valid JSON.

- [ ] **Step 1: Add the constant**

In `src/PinballWizard.Application/Persistence/IngestionSourceIds.cs`, add a new line inside the `IngestionSourceIds` class, after the existing `PinballBrothersFreshdesk` constant:

```csharp
    public const string PinballBrothersFreshdesk = "pb_freshdesk";
    public const string TiltForumsRulesheets = "tiltforums_rulesheets";
```

- [ ] **Step 2: Add the seed entry**

In `data/seeds/ingestion_sources.v1.json`, find the `kineticist_tutorials` entry (a JSON object with `"id": "kineticist_tutorials"`) and add a new object immediately after its closing `},`:

```json
  {
    "id": "tiltforums_rulesheets",
    "displayName": "Tilt Forums Rulesheets",
    "scraperImplKey": "tiltforums_rulesheets",
    "baseUrl": "https://tiltforums.com/",
    "enabled": true,
    "cadence": "manual",
    "politenessOverrides": null,
    "sourceGroup": "Tilt Forums",
    "discoveryStatus": "Active",
    "discoveryDate": "2026-07-03"
  },
```

- [ ] **Step 3: Verify the JSON is still valid and the solution builds**

Run: `dotnet build PinballWizard.slnx`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Application/Persistence/IngestionSourceIds.cs data/seeds/ingestion_sources.v1.json
git commit -m "feat(tiltforums) add IngestionSourceIds constant and seed entry"
```

---

## Task 2: `TiltForumsRulesheetListing` and `TiltForumsRulesheetArticle` models

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetListing.cs`
- Create: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetArticle.cs`

**Interfaces:**
- Produces: `TiltForumsRulesheetListing { GameTitle, ManufacturerHeaderText, TopicUrl }` and `TiltForumsRulesheetArticle { GameTitle, ManufacturerHeaderText, TopicUrl, Author, BodyText, CodeRevision, PublishedAt }` — consumed by Task 3 (client), Task 5 (matcher), Task 6 (synthesizer), Task 8 (CLI verb).

Plain data classes — no behavior, no dedicated test. Verified by Task 3's tests compiling against them.

- [ ] **Step 1: Create the listing model**

```csharp
namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// One entry from the Tilt Forums "Rulesheet Master List" wiki page — a
/// game title, the manufacturer section it's grouped under, and the topic
/// URL to fetch the full rulesheet from.
/// </summary>
public sealed class TiltForumsRulesheetListing
{
    /// <summary>Game title as it appears in the master list table (already clean — no "Rulesheet" suffix).</summary>
    public required string GameTitle { get; init; }

    /// <summary>The manufacturer section heading text this listing was found under (e.g. "Stern Pinball").</summary>
    public required string ManufacturerHeaderText { get; init; }

    /// <summary>Full URL of the Discourse topic containing the rulesheet.</summary>
    public required string TopicUrl { get; init; }
}
```

- [ ] **Step 2: Create the article model**

```csharp
namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// A fetched Tilt Forums rulesheet — the wiki OP's extracted text plus
/// metadata, ready for synthesis into RAG chunks.
/// </summary>
public sealed class TiltForumsRulesheetArticle
{
    /// <summary>Game title, carried through from the originating <see cref="TiltForumsRulesheetListing"/>.</summary>
    public required string GameTitle { get; init; }

    /// <summary>Manufacturer section heading text, carried through from the listing.</summary>
    public required string ManufacturerHeaderText { get; init; }

    /// <summary>Full URL of the Discourse topic — the citation URL that rides every RAG answer sourced from this article.</summary>
    public required string TopicUrl { get; init; }

    /// <summary>Wiki post author's Discourse username.</summary>
    public required string Author { get; init; }

    /// <summary>Extracted, heading-preserved plain text of the wiki OP (headings prefixed with Markdown-style <c>##</c>/<c>###</c>/<c>####</c>).</summary>
    public required string BodyText { get; init; }

    /// <summary>The "Wiki Rulesheet based on Code Rev: X.XX" value, if present in the post body.</summary>
    public string? CodeRevision { get; init; }

    /// <summary>Original post timestamp, if parseable.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetListing.cs src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetArticle.cs
git commit -m "feat(tiltforums) add rulesheet listing and article models"
```

---

## Task 3: `TiltForumsRulesheetsClient.DiscoverRulesheetsAsync` (master list parsing)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs`

**Interfaces:**
- Consumes: `TiltForumsRulesheetListing` (Task 2), `PoliteScraperBase` (`GetStringPolitelyAsync(HttpClient, Uri, CancellationToken)`), `IPolitenessGate`, `PolitenessOptions`.
- Produces: `TiltForumsRulesheetsClient.DiscoverRulesheetsAsync(CancellationToken) : Task<IReadOnlyList<TiltForumsRulesheetListing>>` — consumed by Task 8 (CLI verb).

The master list page structure was fetched and verified directly (2026-07-03, raw HTML, not summarized): manufacturer sections are `<h2 id="heading--{key}">{Name}:</h2>` followed by a sibling `<div class="md-table"><table><tbody><tr><td><a href="{topicUrl}">{Title}</a></td>...</tr></tbody></table>`. A trailing `<h2>Legacy non-wiki Rulesheet List (external)</h2>` heading has **no `id` attribute** — filtering to `h2[id]` excludes it automatically. All of this lives inside the wiki OP, `<div id='post_1'>`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsRulesheetsClientTests
{
    private const string BaseUrl = "https://tiltforums.com";
    private const string MasterListUrl = $"{BaseUrl}/t/rulesheet-master-list/7230";

    // Shape verified against the live page 2026-07-03: manufacturer h2[id]
    // headings, each followed by a sibling div.md-table > table > tbody > tr,
    // all inside #post_1. The trailing "Legacy..." heading has no id and must
    // be excluded.
    private const string MasterListHtml = """
        <html><body>
        <div id='post_1' class='topic-body crawler-post'>
          <div class='post' itemprop='text'>
            <h2 id="heading--stern">Stern Pinball:</h2>
            <div class="md-table">
            <table>
            <thead><tr><th>Game</th><th>Released</th></tr></thead>
            <tbody>
            <tr><td><a href="https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229">Transformers: More Than Meets The Eye</a></td><td>June 2026</td></tr>
            <tr><td><a href="https://tiltforums.com/t/star-wars-stern-rulesheet/2812">Star Wars</a></td><td>2017</td></tr>
            </tbody>
            </table>
            </div>
            <h2 id="heading--spooky">Spooky Pinball:</h2>
            <div class="md-table">
            <table>
            <tbody>
            <tr><td><a href="https://tiltforums.com/t/star-wars-fall-of-the-empire-rulesheet/9872">Star Wars: Fall of the Empire</a></td><td>2025</td></tr>
            </tbody>
            </table>
            </div>
            <h2>Legacy non-wiki Rulesheet List (external)</h2>
            <p><a href="https://replayfoundation.org/papa/">PAPA Rulesheets</a></p>
          </div>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task DiscoverRulesheetsAsync_TwoManufacturerSections_ReturnsAllListings()
    {
        var (client, gate, _) = BuildClient(h => h.MapHtml(MasterListUrl, MasterListHtml));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.Equal(3, listings.Count);

        Assert.Equal("Transformers: More Than Meets The Eye", listings[0].GameTitle);
        Assert.Equal("Stern Pinball", listings[0].ManufacturerHeaderText);
        Assert.Equal("https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229", listings[0].TopicUrl);

        Assert.Equal("Star Wars", listings[1].GameTitle);
        Assert.Equal("Stern Pinball", listings[1].ManufacturerHeaderText);

        Assert.Equal("Star Wars: Fall of the Empire", listings[2].GameTitle);
        Assert.Equal("Spooky Pinball", listings[2].ManufacturerHeaderText);

        // Politeness: exactly one fetch went through the gate.
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverRulesheetsAsync_LegacyHeadingWithNoId_Excluded()
    {
        var (client, _, _) = BuildClient(h => h.MapHtml(MasterListUrl, MasterListHtml));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.DoesNotContain(listings, l => l.TopicUrl.Contains("replayfoundation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(listings, l => l.ManufacturerHeaderText.Contains("Legacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverRulesheetsAsync_HttpFailure_ReturnsEmpty_NoFabrication()
    {
        var (client, _, _) = BuildClient(h => h.Map(MasterListUrl, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.Empty(listings);
    }

    [Fact]
    public async Task DiscoverRulesheetsAsync_SectionWithNoTable_SkippedWithoutThrowing()
    {
        const string html = """
            <html><body>
            <div id='post_1' class='topic-body crawler-post'>
              <div class='post' itemprop='text'>
                <h2 id="heading--empty">Empty Manufacturer:</h2>
                <p>No table follows this heading.</p>
                <h2 id="heading--stern">Stern Pinball:</h2>
                <div class="md-table"><table><tbody>
                <tr><td><a href="https://tiltforums.com/t/godzilla-rulesheet/1">Godzilla</a></td></tr>
                </tbody></table></div>
              </div>
            </div>
            </body></html>
            """;

        var (client, _, _) = BuildClient(h => h.MapHtml(MasterListUrl, html));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.Single(listings);
        Assert.Equal("Godzilla", listings[0].GameTitle);
    }

    private static (TiltForumsRulesheetsClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(BaseUrl),
        };

        var client = new TiltForumsRulesheetsClient(
            httpClient,
            gate,
            Options.Create(new PolitenessOptions()),
            NullLogger<TiltForumsRulesheetsClient>.Instance);

        return (client, gate, handler);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests"`
Expected: FAIL — `TiltForumsRulesheetsClient` does not exist yet (compile error).

- [ ] **Step 3: Write the client**

```csharp
using AngleSharp;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// HTTP client for tiltforums.com. Discovers rulesheets from the
/// manufacturer-grouped "Rulesheet Master List" wiki page and fetches
/// individual topic pages for the wiki OP content.
/// </summary>
/// <remarks>
/// All requests route through <see cref="PoliteScraperBase"/> (LOCKED
/// invariant). robots.txt (verified 2026-07-03, raw fetch) places no
/// Crawl-delay and does not disallow <c>/t/</c> or <c>/c/</c> paths for
/// <c>User-agent: *</c>. Not registered as <c>ISourceScraper</c> — this
/// content is inline HTML, not a downloadable file, so it is ingested via
/// the synthesis pipeline (see <see cref="TiltForumsRulesheetsSynthesizer"/>
/// and the <c>--sync-tiltforums-rulesheets</c> CLI verb), matching
/// <c>KineticistTutorialsClient</c>'s precedent — not the PDF-oriented
/// <c>ScraperOrchestrator</c>/download/<c>DocumentLinker</c> pipeline.
/// </remarks>
public sealed class TiltForumsRulesheetsClient : PoliteScraperBase
{
    private readonly HttpClient _http;

    private const string BaseUrl = "https://tiltforums.com";
    private const string MasterListPath = "/t/rulesheet-master-list/7230";

    public TiltForumsRulesheetsClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<TiltForumsRulesheetsClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>
    /// Discovers all rulesheet listings from the manufacturer-grouped master
    /// list wiki page. Returns an empty list on fetch failure (degrades
    /// visibly — logged, not fabricated).
    /// </summary>
    public async Task<IReadOnlyList<TiltForumsRulesheetListing>> DiscoverRulesheetsAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{BaseUrl}{MasterListPath}");

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "TiltForumsRulesheetsClient: failed to fetch master list at {Url}.", url);
            return [];
        }

        using var browsingContext = BrowsingContext.New(Configuration.Default);
        var parser = browsingContext.GetService<IHtmlParser>()!;
        using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        var listings = new List<TiltForumsRulesheetListing>();

        // Only headings WITH an id attribute are real manufacturer sections —
        // the trailing "Legacy non-wiki Rulesheet List" heading has no id
        // (verified against the live page 2026-07-03) and is excluded by
        // this selector, not by name-matching.
        foreach (var heading in document.QuerySelectorAll("#post_1 h2[id]"))
        {
            var manufacturerName = heading.TextContent.Trim().TrimEnd(':').Trim();
            if (string.IsNullOrWhiteSpace(manufacturerName)) continue;

            var sibling = heading.NextElementSibling;
            var table = sibling?.TagName.Equals("TABLE", StringComparison.OrdinalIgnoreCase) == true
                ? sibling
                : sibling?.QuerySelector("table");
            if (table is null) continue;

            foreach (var row in table.QuerySelectorAll("tbody tr"))
            {
                var firstCell = row.QuerySelector("td");
                var link = firstCell?.QuerySelector("a[href]");
                if (link is null) continue;

                var href = link.GetAttribute("href");
                var title = link.TextContent.Trim();
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title)) continue;

                listings.Add(new TiltForumsRulesheetListing
                {
                    GameTitle = title,
                    ManufacturerHeaderText = manufacturerName,
                    TopicUrl = href,
                });
            }
        }

        Logger.LogInformation("TiltForumsRulesheetsClient: master list yielded {Count} rulesheet listing(s).", listings.Count);
        return listings;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs
git commit -m "feat(tiltforums) add master-list discovery to TiltForumsRulesheetsClient"
```

---

## Task 4: `TiltForumsRulesheetsClient.DiscoverSubcategoryTopicUrlsAsync` (completeness check)

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs`
- Modify: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs`

**Interfaces:**
- Produces: `TiltForumsRulesheetsClient.DiscoverSubcategoryTopicUrlsAsync(CancellationToken) : Task<IReadOnlyList<string>>` — consumed by Task 8 (CLI verb, gap detection).

Subcategory page structure verified directly (2026-07-03, raw HTML): `https://tiltforums.com/c/game-specific/rulesheet-wikis/18`, topic rows are `table.topic-list > tr.topic-list-item`, each containing `a.title.raw-link.raw-topic-link[href]`. Pagination is `?page=N`; the unparameterized URL is page 0, and Discourse emits `<link rel="next" href="...?page=1">` while more pages remain. This client stops paginating the same way `KineticistTutorialsClient.DiscoverTutorialSlugsAsync` does: on a 404, or when a page yields zero new URLs.

- [ ] **Step 1: Add the failing tests**

Add to `TiltForumsRulesheetsClientTests.cs`:

```csharp
    private const string SubcategoryPageUrl0 = $"{BaseUrl}/c/game-specific/rulesheet-wikis/18";
    private static string SubcategoryPageUrl(int page) => $"{SubcategoryPageUrl0}?page={page}";

    // Shape verified against the live page 2026-07-03: table.topic-list >
    // tr.topic-list-item, each with a.title.raw-link.raw-topic-link[href].
    private const string SubcategoryPage0Html = """
        <html><body>
        <table class='topic-list'>
        <tbody>
        <tr class="topic-list-item">
          <td class="main-link">
            <a itemprop='url' href='https://tiltforums.com/t/rulesheet-master-list/7230' class='title raw-link raw-topic-link'>Rulesheet Master List</a>
          </td>
        </tr>
        <tr class="topic-list-item">
          <td class="main-link">
            <a itemprop='url' href='https://tiltforums.com/t/godzilla-rulesheet/1' class='title raw-link raw-topic-link'>Godzilla Rulesheet</a>
          </td>
        </tr>
        </tbody>
        </table>
        </body></html>
        """;

    private const string SubcategoryPage1Html = """
        <html><body>
        <table class='topic-list'>
        <tbody>
        <tr class="topic-list-item">
          <td class="main-link">
            <a itemprop='url' href='https://tiltforums.com/t/jaws-rulesheet/2' class='title raw-link raw-topic-link'>Jaws Rulesheet</a>
          </td>
        </tr>
        </tbody>
        </table>
        </body></html>
        """;

    [Fact]
    public async Task DiscoverSubcategoryTopicUrlsAsync_TwoPages_ReturnsAllUrls()
    {
        var (client, gate, _) = BuildClient(h => h
            .MapHtml(SubcategoryPageUrl0, SubcategoryPage0Html)
            .MapHtml(SubcategoryPageUrl(1), SubcategoryPage1Html)
            .Map(SubcategoryPageUrl(2), _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var urls = await client.DiscoverSubcategoryTopicUrlsAsync(CancellationToken.None);

        Assert.Equal(3, urls.Count);
        Assert.Contains("https://tiltforums.com/t/rulesheet-master-list/7230", urls);
        Assert.Contains("https://tiltforums.com/t/godzilla-rulesheet/1", urls);
        Assert.Contains("https://tiltforums.com/t/jaws-rulesheet/2", urls);
        Assert.Equal(gate.Acquired.Count, gate.Reported.Count);
    }

    [Fact]
    public async Task DiscoverSubcategoryTopicUrlsAsync_EmptyPage_StopsPagination()
    {
        const string emptyPage = "<html><body><table class='topic-list'><tbody></tbody></table></body></html>";

        var (client, _, _) = BuildClient(h => h
            .MapHtml(SubcategoryPageUrl0, SubcategoryPage0Html)
            .MapHtml(SubcategoryPageUrl(1), emptyPage));

        var urls = await client.DiscoverSubcategoryTopicUrlsAsync(CancellationToken.None);

        Assert.Equal(2, urls.Count);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests"`
Expected: FAIL — `DiscoverSubcategoryTopicUrlsAsync` does not exist yet (compile error).

- [ ] **Step 3: Implement the method**

Add to `TiltForumsRulesheetsClient.cs`, after `DiscoverRulesheetsAsync`. Add `using System.Net;` to the top of the file.

```csharp
    private const string SubcategoryPath = "/c/game-specific/rulesheet-wikis/18";

    /// <summary>
    /// Discovers every topic URL listed in the "Wiki Rulesheets" subcategory,
    /// for cross-checking against <see cref="DiscoverRulesheetsAsync"/>'s
    /// master-list results — the master list is human-maintained and may lag
    /// a newly-added rulesheet.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverSubcategoryTopicUrlsAsync(CancellationToken cancellationToken)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 0;

        while (true)
        {
            var pageUrl = page == 0
                ? new Uri($"{BaseUrl}{SubcategoryPath}")
                : new Uri($"{BaseUrl}{SubcategoryPath}?page={page}");

            string html;
            try
            {
                html = await GetStringPolitelyAsync(_http, pageUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.LogDebug("TiltForumsRulesheetsClient: subcategory page {Page} returned 404; pagination exhausted.", page);
                break;
            }

            using var browsingContext = BrowsingContext.New(Configuration.Default);
            var parser = browsingContext.GetService<IHtmlParser>()!;
            using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

            var newCount = 0;
            foreach (var link in document.QuerySelectorAll("a.raw-topic-link[href]"))
            {
                var href = link.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (urls.Add(href)) newCount++;
            }

            Logger.LogDebug(
                "TiltForumsRulesheetsClient: subcategory page {Page} yielded {New} new topic URL(s) (total {Total}).",
                page, newCount, urls.Count);

            if (newCount == 0) break;
            page++;
        }

        Logger.LogInformation("TiltForumsRulesheetsClient: subcategory listing yielded {Count} total topic URL(s).", urls.Count);
        return [.. urls];
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests"`
Expected: PASS (6 tests total).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs
git commit -m "feat(tiltforums) add subcategory completeness check to TiltForumsRulesheetsClient"
```

---

## Task 5: `TiltForumsRulesheetsClient.FetchRulesheetAsync` (topic page → article)

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs`
- Modify: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs`

**Interfaces:**
- Consumes: `TiltForumsRulesheetListing` (Task 2).
- Produces: `TiltForumsRulesheetsClient.FetchRulesheetAsync(TiltForumsRulesheetListing, CancellationToken) : Task<TiltForumsRulesheetArticle?>` — consumed by Task 8 (CLI verb).

Topic page structure verified directly (2026-07-03, raw HTML): the wiki OP is `<div id='post_1' class='topic-body crawler-post'>` (reply posts additionally carry `itemprop='comment'`, which post_1 lacks — but selecting by `#post_1` id is simpler and sufficient). Content lives in `<div class='post' itemprop='text'>` inside it, as a flat sequence of `<h1>`/`<p>`/`<ul>` elements (headings use `<h1>`, not `<h2>`/`<h3>` — corrected from the original design draft). Author is `#post_1 .creator [itemprop='name']`. Timestamp is `#post_1 time.post-time[datetime]`. The "Code Rev" marker appears as plain text inside an `<li>` (e.g. "Wiki Rulesheet based on Code Rev: 0.87") — extracted via regex over the flattened text, not a dedicated element.

- [ ] **Step 1: Add the failing tests**

Add to `TiltForumsRulesheetsClientTests.cs`:

```csharp
    // Shape verified against the live "Transformers" topic page 2026-07-03:
    // #post_1 > div.post[itemprop='text'] holds h1/p/ul content; author and
    // timestamp live in #post_1's crawler-post-meta block.
    private const string TopicPageHtml = """
        <html><body>
        <div id='post_1' class='topic-body crawler-post'>
          <div class='crawler-post-meta'>
            <span class="creator" itemprop="author">
              <a itemprop="url" href='https://tiltforums.com/u/CaptainBZarre'><span itemprop='name'>CaptainBZarre</span></a>
            </span>
            <span class="crawler-post-infos">
              <time datetime='2026-05-21T15:03:35Z' class='post-time'>May 21, 2026, 3:03pm</time>
            </span>
          </div>
          <div class='post' itemprop='text'>
            <h1>Quick Links:</h1>
            <ul><li><a href="#heading--gameinfo">Game Information</a></li></ul>
            <h1 id="heading--gameinfo">Game Information &amp; Overview:</h1>
            <ul>
              <li>Lead Designer: Elliot Eismin</li>
              <li>Wiki Rulesheet based on Code Rev: 0.87
                <ul><li><em>Edit the Code revision, if applicable, when you make changes</em></li></ul>
              </li>
            </ul>
            <p><em>Transformers: More Than Meets the Eye</em> is Elliot Eismin's first machine as lead designer.</p>
            <h1 id="heading--modes">Main Modes:</h1>
            <p>Knock down the drop targets in front of Megatron, then shoot the scoop.</p>
          </div>
        </div>
        <div id='post_2' itemprop='comment' class='topic-body crawler-post'>
          <div class='post' itemprop='text'><p>Nice writeup!</p></div>
        </div>
        </body></html>
        """;

    private static TiltForumsRulesheetListing TransformersListing() => new()
    {
        GameTitle = "Transformers: More Than Meets The Eye",
        ManufacturerHeaderText = "Stern Pinball",
        TopicUrl = "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
    };

    [Fact]
    public async Task FetchRulesheetAsync_TransformersFixture_ParsesAllFields()
    {
        var (client, gate, _) = BuildClient(h => h.MapHtml(TransformersListing().TopicUrl, TopicPageHtml));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.NotNull(article);
        Assert.Equal("Transformers: More Than Meets The Eye", article!.GameTitle);
        Assert.Equal("CaptainBZarre", article.Author);
        Assert.Equal("0.87", article.CodeRevision);
        Assert.NotNull(article.PublishedAt);
        Assert.Equal(2026, article.PublishedAt!.Value.Year);
        Assert.Equal(5, article.PublishedAt!.Value.Month);

        // Body text includes wiki OP content, heading-prefixed.
        Assert.Contains("Megatron", article.BodyText, StringComparison.Ordinal);
        Assert.Contains("## Quick Links:", article.BodyText, StringComparison.Ordinal);
        Assert.Contains("## Main Modes:", article.BodyText, StringComparison.Ordinal);

        // Reply post ("Nice writeup!") must NOT leak into the wiki OP body.
        Assert.DoesNotContain("Nice writeup", article.BodyText, StringComparison.Ordinal);

        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task FetchRulesheetAsync_HttpFailure_ReturnsNull()
    {
        var (client, gate, _) = BuildClient(h => h
            .Map(TransformersListing().TopicUrl, _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.Null(article);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task FetchRulesheetAsync_NoPostOneContent_ReturnsNull()
    {
        const string html = "<html><body><p>Not a Discourse topic page.</p></body></html>";
        var (client, _, _) = BuildClient(h => h.MapHtml(TransformersListing().TopicUrl, html));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.Null(article);
    }

    [Fact]
    public async Task FetchRulesheetAsync_NoCodeRevMarker_CodeRevisionIsNull()
    {
        const string html = """
            <html><body>
            <div id='post_1' class='topic-body crawler-post'>
              <div class='crawler-post-meta'>
                <span class="creator" itemprop="author"><span itemprop='name'>SomeUser</span></span>
              </div>
              <div class='post' itemprop='text'>
                <h1>Overview</h1>
                <p>No code revision marker in this fixture.</p>
              </div>
            </div>
            </body></html>
            """;

        var (client, _, _) = BuildClient(h => h.MapHtml(TransformersListing().TopicUrl, html));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.NotNull(article);
        Assert.Null(article!.CodeRevision);
        Assert.Null(article.PublishedAt);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests"`
Expected: FAIL — `FetchRulesheetAsync` does not exist yet (compile error).

- [ ] **Step 3: Implement the method**

Add to `TiltForumsRulesheetsClient.cs`. Change the class declaration to `sealed partial class` (for `[GeneratedRegex]`), and add `using System.Text.RegularExpressions;` and `using AngleSharp.Dom;` to the top of the file:

```csharp
public sealed partial class TiltForumsRulesheetsClient : PoliteScraperBase
```

Add after `DiscoverSubcategoryTopicUrlsAsync`:

```csharp
    [GeneratedRegex(@"Code Rev:\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRevisionRegex();

    /// <summary>
    /// Fetches a single rulesheet topic page and extracts the wiki OP
    /// (post_1) content. Returns <see langword="null"/> when the page
    /// cannot be fetched or has no recognizable wiki-post content (logged +
    /// skipped — degrades visibly, never fabricates content).
    /// </summary>
    public async Task<TiltForumsRulesheetArticle?> FetchRulesheetAsync(
        TiltForumsRulesheetListing listing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listing);

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, new Uri(listing.TopicUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex,
                "TiltForumsRulesheetsClient: failed to fetch topic '{Title}' at {Url}; skipping.",
                listing.GameTitle, listing.TopicUrl);
            return null;
        }

        using var browsingContext = BrowsingContext.New(Configuration.Default);
        var parser = browsingContext.GetService<IHtmlParser>()!;
        using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        // #post_1 is always the wiki OP; reply posts additionally carry
        // itemprop='comment', but scoping to the id alone is sufficient and
        // simpler — post_1 never has that attribute.
        var postContent = document.QuerySelector("#post_1 .post");
        if (postContent is null)
        {
            Logger.LogWarning(
                "TiltForumsRulesheetsClient: no wiki post content found for '{Title}' at {Url}; skipping.",
                listing.GameTitle, listing.TopicUrl);
            return null;
        }

        var bodyText = ExtractBodyText(postContent);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            Logger.LogWarning(
                "TiltForumsRulesheetsClient: empty wiki post body for '{Title}' at {Url}; skipping.",
                listing.GameTitle, listing.TopicUrl);
            return null;
        }

        var author = document.QuerySelector("#post_1 .creator [itemprop='name']")?.TextContent.Trim()
            ?? "Tilt Forums community";

        DateTimeOffset? publishedAt = null;
        var timeAttr = document.QuerySelector("#post_1 time.post-time")?.GetAttribute("datetime");
        if (timeAttr is not null && DateTimeOffset.TryParse(timeAttr, out var parsed))
        {
            publishedAt = parsed;
        }

        var codeRevMatch = CodeRevisionRegex().Match(bodyText);
        var codeRevision = codeRevMatch.Success ? codeRevMatch.Groups[1].Value : null;

        return new TiltForumsRulesheetArticle
        {
            GameTitle = listing.GameTitle,
            ManufacturerHeaderText = listing.ManufacturerHeaderText,
            TopicUrl = listing.TopicUrl,
            Author = author,
            BodyText = bodyText,
            CodeRevision = codeRevision,
            PublishedAt = publishedAt,
        };
    }

    // Flattens the wiki post's child elements into heading-prefixed plain
    // text (h1 -> "## ", h2 -> "### ", h3 -> "#### ") so downstream chunking
    // preserves section boundaries. Live content uses h1 for all section
    // headings (verified 2026-07-03) — h2/h3 handling is defensive.
    private static string ExtractBodyText(IElement postContent)
    {
        var parts = new List<string>();
        foreach (var el in postContent.Children)
        {
            var text = el.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            parts.Add(el.TagName.ToUpperInvariant() switch
            {
                "H1" => $"## {text}",
                "H2" => $"### {text}",
                "H3" => $"#### {text}",
                _ => text,
            });
        }
        return string.Join("\n\n", parts).Trim();
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests"`
Expected: PASS (10 tests total).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs
git commit -m "feat(tiltforums) add topic-page fetch and extraction to TiltForumsRulesheetsClient"
```

---

## Task 6: `TiltForumsGameMatcher` (manufacturer-scoped game matching)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs`
- Create: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs`

**Interfaces:**
- Consumes: `IMachineRepository.QueryByTitleAsync(string, CancellationToken) : IAsyncEnumerable<Machine>` (existing), `OpdbMachineMapper.NormalizeManufacturerKey(string) : string` (existing, `PinballWizard.Infrastructure.Integrations.Opdb`), `Machine { Id, PartitionKey, Title, ManufacturerDisplayName }` (existing).
- Produces: `TiltForumsGameMatchStatus` (enum: `Resolved`, `NoMatchInManufacturerPartition`, `MultipleMatchesInManufacturerPartition`), `TiltForumsGameMatchResult { Status, MachineId, MachineTitle, ManufacturerDisplayName }`, `TiltForumsGameMatcher.ResolveAsync(IMachineRepository, string, string, CancellationToken) : Task<TiltForumsGameMatchResult>` — consumed by Task 8 (CLI verb).

This is the one piece with no existing template — every current scraper is single-manufacturer, so nothing in the codebase disambiguates a title across manufacturer partitions at scrape/sync time. The design: resolve the manufacturer header text (e.g. "Stern Pinball") to its canonical partition key via the *existing* `OpdbMachineMapper.NormalizeManufacturerKey`, then filter `QueryByTitleAsync`'s cross-partition results down to that one partition. Zero or multiple matches within that partition are never guessed — they're reported as distinct, named outcomes.

- [ ] **Step 1: Write the failing tests**

```csharp
using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsGameMatcherTests
{
    private static Machine MakeMachine(string id, string manufacturerKey, string manufacturerDisplayName, string title) => new()
    {
        Id = id,
        PartitionKey = manufacturerKey,
        ManufacturerDisplayName = manufacturerDisplayName,
        Title = title,
    };

    [Fact]
    public async Task ResolveAsync_SingleMatchInManufacturerPartition_ReturnsResolved()
    {
        var stern2021 = MakeMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla");
        var sega1998 = MakeMachine("G4O1L-abc12", "sega", "Sega", "Godzilla");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([stern2021, sega1998]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Godzilla", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Equal("GweeP-MW95j", result.MachineId);
        Assert.Equal("Godzilla", result.MachineTitle);
        Assert.Equal("Stern Pinball", result.ManufacturerDisplayName);
    }

    [Fact]
    public async Task ResolveAsync_NoMatchInManufacturerPartition_ReturnsNoMatch()
    {
        // "Star Wars" exists for Bally/Williams only — Stern has no machine
        // by this exact title, so this must NOT fall back to an unscoped
        // guess; it must report NoMatch.
        var williamsStarWars = MakeMachine("G4O1L-MDW47", "williams", "Williams", "Star Wars");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Star Wars", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([williamsStarWars]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Star Wars", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Null(result.MachineId);
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesInSamePartition_ReturnsMultipleMatches_NotGuessed()
    {
        var edition1 = MakeMachine("ABCD-1", "stern", "Stern Pinball", "Some Game");
        var edition2 = MakeMachine("ABCD-2", "stern", "Stern Pinball", "Some Game");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Some Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([edition1, edition2]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Null(result.MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ManufacturerHeaderTextNormalized_MatchesPartitionKey()
    {
        // "Jersey Jack Pinball" (master-list header text) must normalize to
        // partition key "jjp" via the existing OpdbMachineMapper function.
        var jjpMachine = MakeMachine("JJP-1", "jjp", "Jersey Jack Pinball", "Wonka");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Wonka", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([jjpMachine]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Wonka", "Jersey Jack Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Equal("JJP-1", result.MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ZeroCandidatesAtAll_ReturnsNoMatch()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Nonexistent Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Nonexistent Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
    }

    private static async IAsyncEnumerable<Machine> ToAsyncEnumerable(IEnumerable<Machine> machines)
    {
        foreach (var machine in machines)
        {
            yield return machine;
        }
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsGameMatcherTests"`
Expected: FAIL — `TiltForumsGameMatcher` does not exist yet (compile error).

- [ ] **Step 3: Implement the matcher**

```csharp
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Integrations.Opdb;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// Outcome of resolving a Tilt Forums rulesheet's game title to a single
/// catalog <c>Machine</c>, scoped to the manufacturer the master list
/// grouped it under.
/// </summary>
public enum TiltForumsGameMatchStatus
{
    /// <summary>Exactly one machine matched the title within the resolved manufacturer partition.</summary>
    Resolved,

    /// <summary>No machine matched the title within the resolved manufacturer partition.</summary>
    NoMatchInManufacturerPartition,

    /// <summary>More than one machine matched the title within the same manufacturer partition — a genuine same-manufacturer edition collision. Not guessed.</summary>
    MultipleMatchesInManufacturerPartition,
}

/// <summary>Result of <see cref="TiltForumsGameMatcher.ResolveAsync"/>.</summary>
public sealed record TiltForumsGameMatchResult(
    TiltForumsGameMatchStatus Status,
    string? MachineId,
    string? MachineTitle,
    string? ManufacturerDisplayName);

/// <summary>
/// Resolves a Tilt Forums rulesheet's (title, manufacturer header text) pair
/// to a single catalog <c>Machine</c>.
/// </summary>
/// <remarks>
/// Every existing single-manufacturer scraper's HTTP client only ever
/// touches one manufacturer's site, so nothing in the codebase before this
/// has had to disambiguate a title across manufacturer partitions at
/// scrape/sync time — <c>IMachineTitleLookupRepository</c>'s own fallback
/// path takes the first OPDB id unscoped (see
/// <c>KineticistTutorialsClient</c>'s "legacy fallback" comment). Tilt
/// Forums is genuinely cross-manufacturer, so this type exists specifically
/// to avoid that class of silent wrong-manufacturer match: it uses the
/// manufacturer hint the master list's own section headers already provide,
/// normalized via the existing <see cref="OpdbMachineMapper.NormalizeManufacturerKey"/>,
/// to filter <see cref="IMachineRepository.QueryByTitleAsync"/>'s
/// cross-partition results down to the one partition that should contain
/// the match — never falling back to an unscoped guess.
/// </remarks>
public static class TiltForumsGameMatcher
{
    public static async Task<TiltForumsGameMatchResult> ResolveAsync(
        IMachineRepository machineRepository,
        string gameTitle,
        string manufacturerHeaderText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machineRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturerHeaderText);

        var manufacturerKey = OpdbMachineMapper.NormalizeManufacturerKey(manufacturerHeaderText);

        var matches = new List<PinballWizard.Core.Domain.Machine>();
        await foreach (var machine in machineRepository.QueryByTitleAsync(gameTitle, cancellationToken))
        {
            if (string.Equals(machine.PartitionKey, manufacturerKey, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(machine);
            }
        }

        return matches.Count switch
        {
            0 => new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, null, null, null),
            1 => new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.Resolved, matches[0].Id, matches[0].Title, matches[0].ManufacturerDisplayName),
            _ => new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, null, null, null),
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsGameMatcherTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs
git commit -m "feat(tiltforums) add manufacturer-scoped game matcher"
```

---

## Task 7: `TiltForumsRulesheetsSynthesizer`

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsSynthesizer.cs`
- Create: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsSynthesizerTests.cs`

**Interfaces:**
- Consumes: `TiltForumsRulesheetArticle` (Task 2), `IChunker.Chunk(ExtractedDocument, ChunkRequest, CancellationToken) : IReadOnlyList<Chunk>` (existing), `ChunkRequest`/`Chunk` (existing, `PinballWizard.Application.Rag.Chunking`), `ExtractedDocument`/`ExtractedPage`/`ExtractionStatus` (existing, `PinballWizard.Application.Rag.Extraction`).
- Produces: `TiltForumsRulesheetsSynthesizer.Synthesize(TiltForumsRulesheetArticle, ChunkRequest) : IReadOnlyList<Chunk>` — consumed by Task 8 (CLI verb).

Directly mirrors `KineticistTutorialsSynthesizer` (verified verbatim). One difference: `TiltForumsRulesheetArticle.BodyText` (produced by Task 5's `ExtractBodyText`) does **not** contain a duplicate game-title heading the way Kineticist's raw Markdown does — the extraction already starts from the wiki OP's first real heading ("Quick Links:"), so there is no H1-dedup step needed here.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsRulesheetsSynthesizerTests
{
    private static HybridChunker NewChunker() =>
        new(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance);

    private static TiltForumsRulesheetsSynthesizer NewSynthesizer() =>
        new(NewChunker(), NullLogger<TiltForumsRulesheetsSynthesizer>.Instance);

    private static TiltForumsRulesheetArticle TransformersArticle() => new()
    {
        GameTitle = "Transformers: More Than Meets The Eye",
        ManufacturerHeaderText = "Stern Pinball",
        TopicUrl = "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
        Author = "CaptainBZarre",
        CodeRevision = "0.87",
        PublishedAt = new DateTimeOffset(2026, 5, 21, 15, 3, 35, TimeSpan.Zero),
        BodyText = """
            ## Quick Links:

            Game Information, Layout, Skill Shots, Main Modes.

            ## Game Information & Overview:

            Lead Designer: Elliot Eismin. Wiki Rulesheet based on Code Rev: 0.87

            ## Main Modes:

            Knock down the drop targets in front of Megatron, then shoot the scoop behind them to start the currently flashing mission. There are five different missions to play through, timed for 60 seconds.

            ## Wizard Modes:

            One Shall Fall is a mini-wizard mode reached by playing two missions.
            """,
    };

    private static ChunkRequest SampleChunkRequest(string docId = "tiltforums_10229_GweeP-MW95j") => new(
        MachineId: "GweeP-MW95j",
        MachineTitle: "Transformers: More Than Meets The Eye",
        Manufacturer: "Stern Pinball",
        DocumentId: docId,
        DocumentUrl: "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
        DocumentType: DocumentType.Rulesheet,
        LastScrapedUtc: new DateTimeOffset(2026, 5, 21, 15, 3, 35, TimeSpan.Zero));

    [Fact]
    public void Ctor_NullChunker_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TiltForumsRulesheetsSynthesizer(null!, NullLogger<TiltForumsRulesheetsSynthesizer>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TiltForumsRulesheetsSynthesizer(NewChunker(), null!));
    }

    [Fact]
    public void Synthesize_NullArticle_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(null!, SampleChunkRequest()));
    }

    [Fact]
    public void Synthesize_NullChunkRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(TransformersArticle(), null!));
    }

    [Fact]
    public void Synthesize_TransformersArticle_ReturnsNonEmptyChunks()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Synthesize_TransformersArticle_AttributionAndSourceInText()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Community wiki rulesheet", allText, StringComparison.Ordinal);
        Assert.Contains("code rev 0.87", allText, StringComparison.Ordinal);
        Assert.Contains(
            "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
            allText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_TransformersArticle_BodyContentPresent()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Megatron", allText, StringComparison.Ordinal);
        Assert.Contains("One Shall Fall", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_ChunkIndex_StartsAtZeroAndIsStrictlyIncreasing()
    {
        var chunks = NewSynthesizer().Synthesize(TransformersArticle(), SampleChunkRequest());

        Assert.Equal(0, chunks[0].ChunkIndex);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].ChunkIndex > chunks[i - 1].ChunkIndex);
        }
    }

    [Fact]
    public void Synthesize_EmptyBodyText_ReturnsEmpty_NoFabrication()
    {
        var article = new TiltForumsRulesheetArticle
        {
            GameTitle = "No Content Game",
            ManufacturerHeaderText = "Stern Pinball",
            TopicUrl = "https://tiltforums.com/t/no-content-rulesheet/1",
            Author = "Someone",
            BodyText = "",
        };

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest("tiltforums_1_X"));

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_NoCodeRevision_OmitsCodeRevClause()
    {
        var article = new TiltForumsRulesheetArticle
        {
            GameTitle = "Some Game",
            ManufacturerHeaderText = "Stern Pinball",
            TopicUrl = "https://tiltforums.com/t/some-game-rulesheet/1",
            Author = "Someone",
            BodyText = "## Overview\n\nSome body text about the game.",
            CodeRevision = null,
        };

        var chunks = NewSynthesizer().Synthesize(article, SampleChunkRequest("tiltforums_1_X"));

        var allText = string.Concat(chunks.Select(c => c.Text));
        Assert.DoesNotContain("code rev", allText, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsSynthesizerTests"`
Expected: FAIL — `TiltForumsRulesheetsSynthesizer` does not exist yet (compile error).

- [ ] **Step 3: Implement the synthesizer**

```csharp
using System.Globalization;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// Converts a <see cref="TiltForumsRulesheetArticle"/> into a list of
/// <see cref="Chunk"/> objects ready for indexing by <c>IRagIndexer</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>KineticistTutorialsSynthesizer</c> exactly: the wiki OP text is
/// already clean, heading-structured content, so this wraps it as a
/// single-page <see cref="ExtractedDocument"/> and hands it to
/// <see cref="IChunker"/> (<c>HybridChunker</c>) — no PDF extraction, no
/// Cosmos write. Called from the <c>--sync-tiltforums-rulesheets</c> CLI
/// verb, which then calls <c>IRagIndexer.UpsertAsync</c> directly.
/// </remarks>
public sealed class TiltForumsRulesheetsSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<TiltForumsRulesheetsSynthesizer> _logger;

    public TiltForumsRulesheetsSynthesizer(IChunker chunker, ILogger<TiltForumsRulesheetsSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    /// <summary>
    /// Synthesizes chunks from a <see cref="TiltForumsRulesheetArticle"/>.
    /// Returns an empty list when the article has no usable content.
    /// </summary>
    public IReadOnlyList<Chunk> Synthesize(TiltForumsRulesheetArticle article, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(article.BodyText))
        {
            _logger.LogWarning(
                "TiltForumsRulesheetsSynthesizer: article '{Title}' has empty BodyText; skipping.",
                article.GameTitle);
            return [];
        }

        var attributedText = BuildAttributedText(article);

        var extracted = new ExtractedDocument(
            Status: ExtractionStatus.Success,
            Text: attributedText,
            Pages: [new ExtractedPage(PageNumber: 1, Text: attributedText)],
            Outline: [],
            Error: null);

        var chunks = _chunker.Chunk(extracted, chunkRequest);

        _logger.LogDebug(
            "TiltForumsRulesheetsSynthesizer: '{Title}' -> {Count} chunk(s) ({Tokens} tokens total).",
            article.GameTitle, chunks.Count, chunks.Sum(c => c.TokenCount));

        return chunks;
    }

    private static string BuildAttributedText(TiltForumsRulesheetArticle article)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"# {article.GameTitle} — Rulesheet");
        lines.Append(CultureInfo.InvariantCulture, "Community wiki rulesheet");
        if (!string.IsNullOrWhiteSpace(article.CodeRevision))
        {
            lines.Append(CultureInfo.InvariantCulture, $" (code rev {article.CodeRevision})");
        }
        lines.AppendLine(CultureInfo.InvariantCulture, $". Source: Tilt Forums, {article.TopicUrl}");
        lines.AppendLine();
        lines.Append(article.BodyText);
        return lines.ToString();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsRulesheetsSynthesizerTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsSynthesizer.cs tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsSynthesizerTests.cs
git commit -m "feat(tiltforums) add TiltForumsRulesheetsSynthesizer"
```

---

## Task 8: DI registration (`AddTiltForumsScraping`)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/TiltForums/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `TiltForumsRulesheetsClient` (Task 3-5), `TiltForumsRulesheetsSynthesizer` (Task 7), `PolitenessOptions` (existing).
- Produces: `IServiceCollection.AddTiltForumsScraping() : IServiceCollection` — consumed by Task 9 (`Program.cs` wiring).

No config section needed (unlike Kineticist's `KineticistOptions`/API key) — the base URL is a hardcoded constant in the client (matches `ManualsScraper`'s `ManualsPath` pattern), since there's no auth or per-environment override required. No dedicated test — DI wiring is verified by the CLI verb resolving successfully in Task 9's manual smoke check.

- [ ] **Step 1: Write the extension method**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// DI registration helpers for Tilt Forums rulesheet ingestion.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a typed <see cref="HttpClient"/> for
    /// <see cref="TiltForumsRulesheetsClient"/> and
    /// <see cref="TiltForumsRulesheetsSynthesizer"/> as transients. No config
    /// section is needed — the base URL is a hardcoded constant on the
    /// client, matching <c>ManualsScraper</c>'s pattern, since this source
    /// has no auth and no per-environment override requirement.
    /// </summary>
    public static IServiceCollection AddTiltForumsScraping(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<TiltForumsRulesheetsClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            client.BaseAddress = new Uri("https://tiltforums.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<TiltForumsRulesheetsSynthesizer>();

        return services;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/ServiceCollectionExtensions.cs
git commit -m "feat(tiltforums) add DI registration for Tilt Forums scraping"
```

---

## Task 9: `--sync-tiltforums-rulesheets` CLI verb

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs`

**Interfaces:**
- Consumes: everything from Tasks 3-8 (`TiltForumsRulesheetsClient`, `TiltForumsGameMatcher`, `TiltForumsRulesheetsSynthesizer`, `AddTiltForumsScraping`), plus existing `IRagIndexer`, `IMachineRepository`, `RagIndexerOptions`.
- Produces: the `--sync-tiltforums-rulesheets` CLI flag — the end-user-facing entry point for this whole feature.

Mirrors the `--sync-kineticist-tutorials` verb's shape (verified verbatim), adapted for: (1) two-phase discovery (master list + subcategory gap check) instead of one, and (2) `TiltForumsGameMatcher`'s manufacturer-scoped resolution instead of Kineticist's OPDB-API/title-lookup resolution. This block has no dedicated unit test — matches the existing precedent (`--sync-kineticist-tutorials` has no test file either; its Client and Synthesizer are the tested units, same structure here).

- [ ] **Step 1: Add the option declaration**

In `src/PinballWizard.Cli/Program.cs`, find the `syncKineticistTutorialsOption` declaration and add a new option immediately after it:

```csharp
var syncKineticistTutorialsOption = new Option<bool>("--sync-kineticist-tutorials")
{
    Description = "Fetch and index all Kineticist pinball tutorial articles as Rulesheet documents in AI Search (ADR-0043 / Domain-2). Each article is fetched as clean Markdown via the .md URL suffix — no PDF extraction. Machine linking uses IMachineTitleLookupRepository; unresolvable slugs are logged and skipped. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
};

var syncTiltForumsRulesheetsOption = new Option<bool>("--sync-tiltforums-rulesheets")
{
    Description = "Fetch and index Tilt Forums community rulesheets as Rulesheet documents in AI Search (ADR-0050 / Domain-2). Discovers rulesheets from the manufacturer-grouped master list wiki page, resolves each to a catalog machine scoped to its manufacturer (never guessing on cross-manufacturer title collisions), and indexes the wiki post content. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
};
```

- [ ] **Step 2: Register the option on the root command**

Find `rootCommand.Options.Add(syncKineticistTutorialsOption);` and add immediately after:

```csharp
rootCommand.Options.Add(syncKineticistTutorialsOption);
rootCommand.Options.Add(syncTiltForumsRulesheetsOption);
```

- [ ] **Step 3: Extract the option's value**

Find `var syncKineticistTutorials = parseResult.GetValue(syncKineticistTutorialsOption);` and add immediately after:

```csharp
var syncKineticistTutorials = parseResult.GetValue(syncKineticistTutorialsOption);
var syncTiltForumsRulesheets = parseResult.GetValue(syncTiltForumsRulesheetsOption);
```

- [ ] **Step 4: Register `AddTiltForumsScraping` in the RAG-gated DI block**

Find `builder.Services.AddKineticistScraping(builder.Configuration);` (inside the same conditional gate as the other RAG-dependent registrations) and add immediately after:

```csharp
builder.Services.AddKineticistScraping(builder.Configuration);
builder.Services.AddTiltForumsScraping();
```

- [ ] **Step 5: Add the verb handler**

Find the end of the `--sync-kineticist-tutorials` handler block (it ends with `if (kineticistFailed > 0) Environment.ExitCode = 1; return; }` followed by the comment introducing `if (syncTwipNewsletter)`). Insert the new handler between them:

```csharp
        if (kineticistFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --sync-tiltforums-rulesheets (Domain-2 — index Tilt Forums
    // community rulesheets as Rulesheet docs in AI Search, ADR-0050).
    // Mirrors --sync-kineticist-tutorials: no Cosmos scraped_documents_raw
    // record, no change-feed, direct IRagIndexer.UpsertAsync. Game matching
    // is manufacturer-scoped (TiltForumsGameMatcher) rather than unscoped,
    // because Tilt Forums is cross-manufacturer, unlike every existing
    // single-manufacturer scraper.
    if (syncTiltForumsRulesheets)
    {
        var tiltForumsClient = host.Services.GetService<PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetsClient>();
        var tiltForumsSynthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetsSynthesizer>();
        var tiltForumsIndexer = host.Services.GetService<IRagIndexer>();
        var tiltForumsMachineRepo = host.Services.GetService<IMachineRepository>();

        if (tiltForumsClient is null || tiltForumsSynthesizer is null || tiltForumsIndexer is null || tiltForumsMachineRepo is null)
        {
            Console.Error.WriteLine(
                "--sync-tiltforums-rulesheets requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("Discovering Tilt Forums rulesheets from the master list...");
        var listings = await tiltForumsClient.DiscoverRulesheetsAsync(cancellationToken);
        Console.WriteLine($"Found {listings.Count} rulesheet listing(s) in the master list.");

        Console.WriteLine("Cross-checking against the Wiki Rulesheets subcategory for gaps...");
        var subcategoryUrls = await tiltForumsClient.DiscoverSubcategoryTopicUrlsAsync(cancellationToken);
        var masterListUrls = listings.Select(l => l.TopicUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tiltForumsGaps = subcategoryUrls
            .Where(u => !masterListUrls.Contains(u) && !u.Contains("rulesheet-master-list", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tiltForumsGaps.Count > 0)
        {
            Console.WriteLine($"  {tiltForumsGaps.Count} topic(s) in the subcategory are not in the master list (not ingested this run):");
            foreach (var gap in tiltForumsGaps)
            {
                Console.WriteLine($"    {gap}");
            }
        }

        var tiltForumsIndexed = 0;
        var tiltForumsSkippedNoContent = 0;
        var tiltForumsUnmatched = 0;
        var tiltForumsFailed = 0;
        var tiltForumsIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();

        foreach (var listing in listings)
        {
            if (cancellationToken.IsCancellationRequested) break;

            PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchResult matchResult;
            try
            {
                matchResult = await PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatcher.ResolveAsync(
                    tiltForumsMachineRepo, listing.GameTitle, listing.ManufacturerHeaderText, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: game matching failed for '{listing.GameTitle}' ({listing.ManufacturerHeaderText}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            if (matchResult.Status != PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.Resolved)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: unmatched '{listing.GameTitle}' ({listing.ManufacturerHeaderText}) — {matchResult.Status}.");
                tiltForumsUnmatched++;
                continue;
            }

            var article = await tiltForumsClient.FetchRulesheetAsync(listing, cancellationToken);
            if (article is null)
            {
                tiltForumsSkippedNoContent++;
                continue;
            }

            var topicId = new Uri(listing.TopicUrl).Segments[^1].TrimEnd('/');
            var documentId = $"tiltforums_{topicId}_{matchResult.MachineId}";

            var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                MachineId: matchResult.MachineId!,
                MachineTitle: matchResult.MachineTitle!,
                Manufacturer: matchResult.ManufacturerDisplayName!,
                DocumentId: documentId,
                DocumentUrl: article.TopicUrl,
                DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow);

            var chunks = tiltForumsSynthesizer.Synthesize(article, chunkRequest);
            if (chunks.Count == 0)
            {
                tiltForumsSkippedNoContent++;
                continue;
            }

            try
            {
                var result = await tiltForumsIndexer.UpsertAsync(chunkRequest, chunks, tiltForumsIndexerOptions, cancellationToken);
                if (result.Failures.Count > 0)
                {
                    foreach (var failure in result.Failures)
                    {
                        Console.Error.WriteLine(
                            $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.GameTitle}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                    }
                    tiltForumsFailed++;
                }
                else
                {
                    Console.WriteLine($"  Indexed '{article.GameTitle}' -> machine {matchResult.MachineId} ({chunks.Count} chunk(s))");
                    tiltForumsIndexed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Failed to index '{article.GameTitle}': {ex.Message}");
                tiltForumsFailed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"--sync-tiltforums-rulesheets complete: indexed={tiltForumsIndexed} unmatched={tiltForumsUnmatched} skipped_no_content={tiltForumsSkippedNoContent} failed={tiltForumsFailed}");
        if (tiltForumsFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --sync-twip-newsletter ...
```

- [ ] **Step 6: Build**

Run: `dotnet build PinballWizard.slnx`
Expected: Build succeeds with no errors or warnings.

- [ ] **Step 7: Manual smoke check (help text only — no live network/Cosmos required)**

Run: `dotnet run --project src/PinballWizard.Cli -- --help`
Expected: Output includes a `--sync-tiltforums-rulesheets` line with the description text from Step 1.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git commit -m "feat(tiltforums) add --sync-tiltforums-rulesheets CLI verb"
```

---

## Task 10: Full test suite + documentation touch-up

**Files:**
- Modify: `CLAUDE.md` (source manufacturers table has no Tilt Forums row today — this is intentionally NOT a manufacturer, so no row is added; instead a one-line mention goes in the Domain-2/Phase 7 context if such a section exists, otherwise skip if there's no natural home — verify by reading the current file before editing).

**Interfaces:** None — this task is verification + optional documentation, not new production code.

- [ ] **Step 1: Run the full CI-equivalent test suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: All tests pass, including the ~24 new tests added across Tasks 3-7.

- [ ] **Step 2: Check whether `CLAUDE.md` needs a one-line mention**

Read `CLAUDE.md`'s "Source manufacturers" section and any Phase 7 / current-work-stream section. Tilt Forums is not a manufacturer, so it does NOT get a row in the `ISourceScraper` table. If there's a natural "current work stream" or "non-manufacturer sources" list (compare how Kineticist/TWIP are documented, if at all, in `CLAUDE.md` today), add one line there following the same style. If no such section exists, skip this step — do not invent a new section for it.

- [ ] **Step 3: If Step 2 made a change, commit it**

```bash
git add CLAUDE.md
git commit -m "docs update CLAUDE.md with Tilt Forums ingestion mention"
```

If Step 2 made no change, skip this commit.

- [ ] **Step 4: Confirm the working tree is clean and all commits are present**

Run: `git log --oneline main..HEAD`
Expected: One commit per task (9-10 commits total), nothing uncommitted (`git status --short` empty).
