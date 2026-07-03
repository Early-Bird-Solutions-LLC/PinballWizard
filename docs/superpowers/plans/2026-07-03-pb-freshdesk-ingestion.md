# Pinball Brothers Freshdesk Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ingest Pinball Brothers' Freshdesk support-portal content (manuals, rulebooks, schematics, service bulletins, troubleshooting Q&A, update notes) for Alien, Queen, ABBA, and Predator so they appear as cited documents in the Wizard — closing the "Queen has no documents" gap while covering all four machines and general support content.

**Architecture:** A shared `FreshdeskSolutionsClient` crawls the live portal (categories → folders → articles, with pagination) each run. Attachment-bearing articles (Manual, Rulebook, Schematic, Service Bulletin PDFs) flow through a new `PbFreshdeskDocumentScraper` (`ISourceScraper`) into the existing `ScraperOrchestrator` → Cosmos `scraped_documents_raw` → RAG worker pipeline — same as every other manufacturer, fully admin-visible. Text-only articles (Q&A, How-To, Update notes, FAQ) flow through a new `PbFreshdeskArticleSynthesizer` CLI verb straight to the RAG index, mirroring the existing `TwipNewsletterSynthesizer` pattern.

**Tech Stack:** .NET 10, AngleSharp (HTML parsing), xUnit, existing `PoliteScraperBase`/`IPolitenessGate` scraping infrastructure, existing `IChunker`/`IRagIndexer` RAG pipeline.

## Global Constraints

- All HTTP requests route through `IPolitenessGate` via `PoliteScraperBase.GetStringPolitelyAsync` — no bare `HttpClient` calls (LOCKED invariant, `feedback_polite_scraping.md`).
- Every scraped document carries full provenance (`DiscoveryUrl`, `DiscoveryContext`, `GameSlug`) — provenance is sacred.
- No fallbacks that hide failures — a fetch/parse failure must log and skip visibly (return null / empty list), never fabricate content (Invariant #17).
- Commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`, no Claude attribution trailer (repo convention).
- All work happens inside the existing worktree `c:\earlybird\PinballWizard\.worktrees\pb-freshdesk-ingestion` on branch `feat/pb-freshdesk-ingestion` — do not create a new worktree.
- No XML doc comments required on public members (per `feedback_no_xml_docs.md`) — use `//` comments only where the WHY is non-obvious.

---

### Task 1: Core additions — DocumentType, SourceType, IngestionSourceIds, FreshdeskOptions

**Files:**
- Modify: `src/PinballWizard.Core/Models/Enums.cs`
- Modify: `src/PinballWizard.Application/Persistence/IngestionSourceIds.cs`
- Create: `src/PinballWizard.Core/Configuration/FreshdeskOptions.cs`
- Test: `tests/PinballWizard.Core.Tests` (compile-only check; no dedicated test file needed for enum/const additions — verified via Task 5/7's tests exercising the new values)

**Interfaces:**
- Produces: `DocumentType.SupportArticle`, `SourceType.PinballBrothersFreshdeskArticle`, `IngestionSourceIds.PinballBrothersFreshdesk` (`"pb_freshdesk"`), `FreshdeskOptions` (`BaseUrl`, `SolutionsHomePath`).

- [ ] **Step 1: Add the new enum values**

In `src/PinballWizard.Core/Models/Enums.cs`, add `PinballBrothersFreshdeskArticle` to the `SourceType` enum (after `JjpSupportPage`):

```csharp
public enum SourceType
{
    ManualsPage,
    GamePage,
    ServiceBulletinPage,
    JjpProductPage,
    AmericanPinballGamePage,
    SpookyPinballGamePage,
    SpookyPinballSupportPage,
    PinballBrothersGamePage,
    PinballBrothersDocumentPage,
    BarrelsOfFunProductPage,
    ChicagoGamingGamePage,
    MultimorphicProductPage,
    JjpSupportPage,
    PinballBrothersFreshdeskArticle,
}
```

Add `SupportArticle` to the `DocumentType` enum (after `SdkGuide`):

```csharp
    // A Freshdesk knowledge-base article with no downloadable attachment
    // (troubleshooting Q&A, "how to" guides, update/changelog notes). Indexed
    // via the PbFreshdeskArticleSynthesizer bypass path (--sync-pb-freshdesk-articles),
    // like NewsDigest/SdkGuide — not PDF-derived, not change-feed routed, and
    // deliberately excluded from RagIngestionOptions.AcceptedDocumentTypes
    // since it never flows through the Cosmos scraped_documents_raw pipeline.
    SupportArticle,
```

- [ ] **Step 2: Add the IngestionSourceIds constant**

In `src/PinballWizard.Application/Persistence/IngestionSourceIds.cs`, add after `MultimorphicP3Sdk`:

```csharp
    public const string PinballBrothersFreshdesk = "pb_freshdesk";
```

- [ ] **Step 3: Create FreshdeskOptions**

Create `src/PinballWizard.Core/Configuration/FreshdeskOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Pinball Brothers Freshdesk support-portal scraper.
// pinballbrothers.freshdesk.com is a separate host from pinballbrothers.com —
// robots.txt (verified 2026-07-03) allows /support/solutions/* and explicitly
// carves out Allow: /helpdesk/attachments from the broader Disallow: /helpdesk/.
public sealed class FreshdeskOptions
{
    public const string SectionName = "PinballBrothersFreshdesk";

    [Required, Url]
    public string BaseUrl { get; set; } = "https://pinballbrothers.freshdesk.com";

    public string SolutionsHomePath { get; set; } = "/support/solutions";
}
```

- [ ] **Step 4: Build to verify no compile errors**

Run: `dotnet build src/PinballWizard.Core/PinballWizard.Core.csproj src/PinballWizard.Application/PinballWizard.Application.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Models/Enums.cs src/PinballWizard.Application/Persistence/IngestionSourceIds.cs src/PinballWizard.Core/Configuration/FreshdeskOptions.cs
git commit -m "feat(core) add DocumentType.SupportArticle + Freshdesk source scaffolding"
```

---

### Task 2: Freshdesk domain models + FreshdeskArticleExtractor

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskModels.cs`
- Create: `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractor.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractorTests.cs`

**Interfaces:**
- Consumes: nothing (pure, no I/O).
- Produces: `FreshdeskFolder(string CategoryName, string FolderName, string Url)`, `FreshdeskArticleSummary(string Title, string Url, FreshdeskFolder Folder)`, `FreshdeskAttachment(string Url, string FileName)`, `FreshdeskArticle { Title, Url, Folder, BodyText, Attachments }`, `FreshdeskArticleExtractor.Extract(string html, string articleUrl) : FreshdeskArticleExtractor.ExtractedArticleContent?` where `ExtractedArticleContent(string Title, string BodyText, IReadOnlyList<FreshdeskAttachment> Attachments)`. Task 3 (`FreshdeskSolutionsClient`) consumes all of these.

Real markup fixtures below were captured directly from `pinballbrothers.freshdesk.com` on 2026-07-03 (not paraphrased) so the selectors are verified, not guessed.

- [ ] **Step 1: Write the failing extractor tests**

Create `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractorTests.cs`:

```csharp
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class FreshdeskArticleExtractorTests
{
    private const string ArticleUrl =
        "https://pinballbrothers.freshdesk.com/support/solutions/articles/80001073771-queen-pinball-technical-manual";

    // Captured verbatim (trimmed to the load-bearing elements) from
    // pinballbrothers.freshdesk.com/support/solutions/articles/80001073771-queen-pinball-technical-manual
    // on 2026-07-03. Real markup — not a guessed shape.
    private const string ArticleWithAttachmentHtml = """
        <html><body>
        <div class="breadcrumb">
            <a href="/support/solutions"> Solution home </a>
            <a href="/support/solutions/80000460814">FAQs QUEEN</a>
            <a href="/support/solutions/folders/80000701915">Queen - General</a>
        </div>
        <h2 class="heading">QUEEN Pinball - Technical Manual
             <a href="#" class="solution-print--icon print--remove" title="Print this Article" id="print-article">
                <span class="icon-print"></span>
                <span class="text-print">Print</span>
             </a>
        </h2>
        <p>Modified on: Fri, 28 Apr, 2023 at  8:34 AM</p>
        <hr />
        <article class="article-body" id="article-body" rel="image-enlarge">
            <p dir="ltr"><span dir="ltr">Here in attach you can find the QUEEN Technical Manual<br><br>**This version is not final**<br></span><br><br><span>ENJOY YOUR GAME!!</span><br><br><span>Andrea DM</span><br><span>PB Support TEAM</span></p>
        </article>
        <hr />
        <div class="cs-g-c attachments" id="article-80001073771-attachments">
            <div class="attachment">
                <div class="attachment-type"><span class="file-type"> pdf </span></div>
                <div class="attach_content">
                    <div class="ellipsis">
                        <a href="/helpdesk/attachments/80209470065" class="filename" target="_blank" data-toggle='tooltip' title='QUEEN PINBALL TECHNICAL GAME MANUAL R1.pdf'>QUEEN PINBAL... </a>
                    </div>
                    <div>(10.2 MB) </div>
                </div>
            </div>
        </div>
        </body></html>
        """;

    // Captured verbatim shape for a text-only article (no .attachments block at all) —
    // e.g. "Volume is flickering up/down" troubleshooting articles.
    private const string ArticleWithoutAttachmentHtml = """
        <html><body>
        <h2 class="heading">Volume is "flickering" up/down
             <a href="#" class="solution-print--icon print--remove" id="print-article"><span class="text-print">Print</span></a>
        </h2>
        <p>Modified on: Wed, 26 Jan, 2022 at  7:45 AM</p>
        <article class="article-body" id="article-body">
            <p>After game boot, I see volume is changing rapidly up and down. 1. Check the fuses in playfield controller box. 2. Check and reseat cables from playfield.</p>
        </article>
        </body></html>
        """;

    private const string ArticleWithNoTitleHtml = "<html><body><p>No heading here.</p></body></html>";

    [Fact]
    public void Extract_ArticleWithAttachment_ReturnsTitleBodyAndAttachment()
    {
        var result = FreshdeskArticleExtractor.Extract(ArticleWithAttachmentHtml, ArticleUrl);

        Assert.NotNull(result);
        Assert.Equal("QUEEN Pinball - Technical Manual", result!.Title);
        Assert.Contains("Here in attach you can find the QUEEN Technical Manual", result.BodyText, StringComparison.Ordinal);
        Assert.Contains("ENJOY YOUR GAME", result.BodyText, StringComparison.Ordinal);

        // The nested "Print" icon anchor must NOT leak into the title.
        Assert.DoesNotContain("Print", result.Title, StringComparison.Ordinal);

        Assert.Single(result.Attachments);
        Assert.Equal("https://pinballbrothers.freshdesk.com/helpdesk/attachments/80209470065", result.Attachments[0].Url);
        Assert.Equal("QUEEN PINBALL TECHNICAL GAME MANUAL R1.pdf", result.Attachments[0].FileName);
    }

    [Fact]
    public void Extract_ArticleWithoutAttachment_ReturnsEmptyAttachmentList()
    {
        var result = FreshdeskArticleExtractor.Extract(ArticleWithoutAttachmentHtml, ArticleUrl);

        Assert.NotNull(result);
        Assert.Equal("Volume is \"flickering\" up/down", result!.Title);
        Assert.Contains("Check the fuses in playfield controller box", result.BodyText, StringComparison.Ordinal);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public void Extract_NoHeadingElement_ReturnsNull()
    {
        // Degrade visibly: a page we can't find a title on yields null, not a
        // fabricated empty-title record (Invariant #17).
        var result = FreshdeskArticleExtractor.Extract(ArticleWithNoTitleHtml, ArticleUrl);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_EmptyHtml_ReturnsNull()
    {
        var result = FreshdeskArticleExtractor.Extract(string.Empty, ArticleUrl);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_NullHtml_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FreshdeskArticleExtractor.Extract(null!, ArticleUrl));
    }

    [Fact]
    public void Extract_WhitespaceArticleUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() => FreshdeskArticleExtractor.Extract(ArticleWithAttachmentHtml, "  "));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~FreshdeskArticleExtractorTests"`
Expected: FAIL — `FreshdeskArticleExtractor` does not exist (compile error).

- [ ] **Step 3: Create the domain models**

Create `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskModels.cs`:

```csharp
namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// One Freshdesk solution folder discovered from the /support/solutions
// category page (e.g. CategoryName="FAQs QUEEN", FolderName="QUEEN - Update").
public sealed record FreshdeskFolder(string CategoryName, string FolderName, string Url);

// One article link discovered from a folder's article-list page (pre-fetch —
// no body content yet).
public sealed record FreshdeskArticleSummary(string Title, string Url, FreshdeskFolder Folder);

// One downloadable attachment on an article page.
public sealed record FreshdeskAttachment(string Url, string FileName);

// A fully-fetched Freshdesk article: title, body text, and any attachments.
// Attachments.Count == 0 means this is a text-only article (routed to the
// synthesizer path); Attachments.Count > 0 means it becomes a normal
// ScrapedItem/DiscoveredLink per attachment (routed to the scraper path).
public sealed record FreshdeskArticle
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required FreshdeskFolder Folder { get; init; }
    public required string BodyText { get; init; }
    public IReadOnlyList<FreshdeskAttachment> Attachments { get; init; } = [];
}
```

- [ ] **Step 4: Implement FreshdeskArticleExtractor**

Create `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractor.cs`:

```csharp
using System.Net;
using AngleSharp.Html.Parser;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// Extracts title, body text, and attachment links from a single Freshdesk
// support-article page. Pure — no I/O. Selectors verified against real
// pinballbrothers.freshdesk.com markup on 2026-07-03:
//   - Title:      h2.heading (contains a nested #print-article icon anchor
//                 that must be stripped before reading TextContent)
//   - Body:       article.article-body (present on every article; empty
//                 string when the article has no prose, e.g. attachment-only)
//   - Attachment: .attachments a.filename[href] — href is a relative
//                 /helpdesk/attachments/{id} path (robots.txt explicitly
//                 Allows this path); the filename lives in the title=
//                 attribute, not the anchor text (which Freshdesk truncates
//                 with "...").
public static class FreshdeskArticleExtractor
{
    private const string BaseUrl = "https://pinballbrothers.freshdesk.com";

    private static readonly HtmlParser Parser = new();

    public sealed record ExtractedArticleContent(
        string Title,
        string BodyText,
        IReadOnlyList<FreshdeskAttachment> Attachments);

    public static ExtractedArticleContent? Extract(string html, string articleUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(articleUrl);

        if (string.IsNullOrWhiteSpace(html)) return null;

        using var doc = Parser.ParseDocument(html);

        var titleEl = doc.QuerySelector("h2.heading");
        if (titleEl is null) return null;

        // Strip the nested "Print this Article" icon anchor before reading
        // TextContent so it doesn't leak "Print" into the title.
        titleEl.QuerySelector("#print-article")?.Remove();
        var title = titleEl.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var bodyEl = doc.QuerySelector("article.article-body");
        var bodyText = NormalizeWhitespace(bodyEl?.TextContent ?? string.Empty);

        var attachments = new List<FreshdeskAttachment>();
        foreach (var anchor in doc.QuerySelectorAll(".attachments a.filename[href]"))
        {
            var href = anchor.GetAttribute("href");
            var fileName = anchor.GetAttribute("title");
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(fileName)) continue;

            var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : BaseUrl + href;

            attachments.Add(new FreshdeskAttachment(absoluteUrl, WebUtility.HtmlDecode(fileName)));
        }

        return new ExtractedArticleContent(title, bodyText, attachments);
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~FreshdeskArticleExtractorTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskModels.cs src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractor.cs tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractorTests.cs
git commit -m "feat(scraper) add Freshdesk article model + pure HTML extractor"
```

---

### Task 3: FreshdeskSolutionsClient (discovery + fetch)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClient.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClientTests.cs`

**Interfaces:**
- Consumes: `FreshdeskOptions` (Task 1), `FreshdeskFolder`/`FreshdeskArticleSummary`/`FreshdeskArticle`/`FreshdeskArticleExtractor` (Task 2), `PoliteScraperBase`/`IPolitenessGate`/`PolitenessOptions` (existing), `FakePolitenessGate`/`QueueingHttpMessageHandler` (existing test infra).
- Produces: `FreshdeskSolutionsClient.DiscoverFoldersAsync(CancellationToken) : Task<IReadOnlyList<FreshdeskFolder>>`, `DiscoverArticlesInFolderAsync(FreshdeskFolder, CancellationToken) : Task<IReadOnlyList<FreshdeskArticleSummary>>`, `FetchArticleAsync(FreshdeskArticleSummary, CancellationToken) : Task<FreshdeskArticle?>`. Task 5 (`PbFreshdeskDocumentScraper`) and Task 8 (CLI verb) consume all three methods.

Fixtures below are real markup captured 2026-07-03 from `pinballbrothers.freshdesk.com/support/solutions`, folder `80000432961` (ALIEN - General, 14 articles, confirms 2-page pagination), and folder `80000722109` (ALIEN - Electronics, 1 article, confirms no-pagination case).

- [ ] **Step 1: Write the failing client tests**

Create `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClientTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class FreshdeskSolutionsClientTests
{
    private const string BaseUrl = "https://pinballbrothers.freshdesk.com";
    private const string SolutionsUrl = $"{BaseUrl}/support/solutions";

    // Trimmed to the two load-bearing categories (General, FAQs QUEEN) —
    // real markup shape captured 2026-07-03.
    private const string SolutionsHomeHtml = """
        <html><body>
        <section id="solutions-home">
          <div class="cs-s">
            <h3 class="heading"> <a href="/support/solutions/80000165341">General</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list">
                <div class="list-lead">
                  <a href="/support/solutions/folders/80000680701" title="Service Bulletin"> Service Bulletin <span class='item-count'>4</span></a>
                </div>
              </section>
            </div>
          </div>
          <div class="cs-s">
            <h3 class="heading"> <a href="/support/solutions/80000460814">FAQs QUEEN</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list">
                <div class="list-lead">
                  <a href="/support/solutions/folders/80000701915" title="Queen - General"> Queen - General <span class='item-count'>4</span></a>
                </div>
              </section>
              <section class="cs-g article-list">
                <div class="list-lead">
                  <a href="/support/solutions/folders/80000722110" title="QUEEN - Electronics"> QUEEN - Electronics <span class='item-count'>1</span></a>
                </div>
              </section>
            </div>
          </div>
        </section>
        </body></html>
        """;

    // Real markup from folder 80000432961 page 1 (10 of 14 articles), with a
    // "next" pagination link to page 2.
    private const string FolderPage1Html = """
        <html><body>
        <h2 class="heading">ALIEN - General</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80000843825--how-to-rebuild-machine-alien-4-0" class="c-link">"How-to" rebuild machine - Alien 4.0</a></div></div>
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80000707814-alien-game-rules" class="c-link">Alien Game rules</a></div></div>
        </section>
        <div class="pagination"><ul> <li class="prev disabled"><a>&laquo; Previous</a></li> <li class="active"><a>1</a></li> <li><a href="/support/solutions/folders/80000432961/page/2">2</a></li> <li class="next"><a href="/support/solutions/folders/80000432961/page/2">Next &raquo;</a></li> </ul></div>
        </body></html>
        """;

    // Real markup from folder 80000432961 page 2 (remaining 4 articles) — "next" is disabled.
    private const string FolderPage2Html = """
        <html><body>
        <h2 class="heading">ALIEN - General</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80001068353-alien-rulesheet-explanation" class="c-link">ALIEN - Rulesheet explanation</a></div></div>
        </section>
        <div class="pagination"><ul> <li class="prev"><a href="/support/solutions/folders/80000432961/page/1">&laquo; Previous</a></li> <li><a href="/support/solutions/folders/80000432961/page/1">1</a></li> <li class="active"><a>2</a></li> <li class="next disabled"><a>Next &raquo;</a></li> </ul></div>
        </body></html>
        """;

    // Real markup from folder 80000722109 (ALIEN - Electronics, 1 article) — no pagination div at all.
    private const string SinglePageFolderHtml = """
        <html><body>
        <h2 class="heading">ALIEN - Electronics</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80001155211-alien-schematics" class="c-link">Alien - Schematics</a></div></div>
        </section>
        </body></html>
        """;

    private const string ArticleHtml = """
        <html><body>
        <h2 class="heading">QUEEN Pinball - Technical Manual <a href="#" id="print-article"><span>Print</span></a></h2>
        <article class="article-body" id="article-body"><p>Here in attach you can find the QUEEN Technical Manual</p></article>
        <div class="cs-g-c attachments"><div class="attachment"><div class="attach_content"><div class="ellipsis">
            <a href="/helpdesk/attachments/80209470065" class="filename" title='QUEEN PINBALL TECHNICAL GAME MANUAL R1.pdf'>QUEEN PINBAL...</a>
        </div></div></div></div>
        </body></html>
        """;

    [Fact]
    public async Task DiscoverFoldersAsync_ParsesCategoryAndFolderNames()
    {
        var (client, gate, handler) = BuildClient(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));

        var folders = await client.DiscoverFoldersAsync(CancellationToken.None);

        Assert.Equal(3, folders.Count);

        Assert.Contains(folders, f => f.CategoryName == "General" && f.FolderName == "Service Bulletin"
            && f.Url == $"{BaseUrl}/support/solutions/folders/80000680701");
        Assert.Contains(folders, f => f.CategoryName == "FAQs QUEEN" && f.FolderName == "Queen - General"
            && f.Url == $"{BaseUrl}/support/solutions/folders/80000701915");
        Assert.Contains(folders, f => f.CategoryName == "FAQs QUEEN" && f.FolderName == "QUEEN - Electronics"
            && f.Url == $"{BaseUrl}/support/solutions/folders/80000722110");

        // Politeness: exactly one request (the homepage), fully accounted for.
        Assert.Single(handler.Requests);
        Assert.Equal(gate.Acquired.Count, gate.Reported.Count);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverArticlesInFolderAsync_MultiPageFolder_FollowsPaginationToCompletion()
    {
        const string folderUrl = $"{BaseUrl}/support/solutions/folders/80000432961";
        var folder = new FreshdeskFolder("FAQs ALIEN", "ALIEN - General", folderUrl);

        var (client, gate, handler) = BuildClient(h => h
            .MapHtml(folderUrl, FolderPage1Html)
            .MapHtml($"{folderUrl}/page/2", FolderPage2Html));

        var summaries = await client.DiscoverArticlesInFolderAsync(folder, CancellationToken.None);

        // Both pages' articles are present — pagination was followed to completion,
        // not stopped after the first page.
        Assert.Equal(3, summaries.Count);
        Assert.Contains(summaries, s => s.Title == "\"How-to\" rebuild machine - Alien 4.0");
        Assert.Contains(summaries, s => s.Title == "Alien Game rules");
        Assert.Contains(summaries, s => s.Title == "ALIEN - Rulesheet explanation");
        Assert.All(summaries, s => Assert.Equal(folder, s.Folder));

        // Two page fetches, both through the gate.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
    }

    [Fact]
    public async Task DiscoverArticlesInFolderAsync_SinglePageFolder_StopsAfterOneFetch()
    {
        const string folderUrl = $"{BaseUrl}/support/solutions/folders/80000722109";
        var folder = new FreshdeskFolder("FAQs ALIEN", "ALIEN - Electronics", folderUrl);

        var (client, _, handler) = BuildClient(h => h.MapHtml(folderUrl, SinglePageFolderHtml));

        var summaries = await client.DiscoverArticlesInFolderAsync(folder, CancellationToken.None);

        Assert.Single(summaries);
        Assert.Equal("Alien - Schematics", summaries[0].Title);

        // No pagination div present → exactly one fetch, no attempt at a page/2 request.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchArticleAsync_WithAttachment_ReturnsFullArticle()
    {
        const string articleUrl = $"{BaseUrl}/support/solutions/articles/80001073771-queen-pinball-technical-manual";
        var folder = new FreshdeskFolder("FAQs QUEEN", "Queen - General", $"{BaseUrl}/support/solutions/folders/80000701915");
        var summary = new FreshdeskArticleSummary("QUEEN Pinball - Technical Manual", articleUrl, folder);

        var (client, gate, handler) = BuildClient(h => h.MapHtml(articleUrl, ArticleHtml));

        var article = await client.FetchArticleAsync(summary, CancellationToken.None);

        Assert.NotNull(article);
        Assert.Equal("QUEEN Pinball - Technical Manual", article!.Title);
        Assert.Equal(articleUrl, article.Url);
        Assert.Equal(folder, article.Folder);
        Assert.Single(article.Attachments);
        Assert.Equal($"{BaseUrl}/helpdesk/attachments/80209470065", article.Attachments[0].Url);

        Assert.Single(handler.Requests);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task FetchArticleAsync_HttpFailure_ReturnsNull()
    {
        const string articleUrl = $"{BaseUrl}/support/solutions/articles/nonexistent";
        var folder = new FreshdeskFolder("General", "FAQ", $"{BaseUrl}/support/solutions/folders/80000242806");
        var summary = new FreshdeskArticleSummary("Nonexistent", articleUrl, folder);

        var (client, gate, _) = BuildClient(h => h.Map(articleUrl,
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));

        var article = await client.FetchArticleAsync(summary, CancellationToken.None);

        // Degrade visibly: HTTP failure logs and returns null, doesn't throw.
        Assert.Null(article);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task DiscoverFoldersAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        var (client, gate, _) = BuildClient(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));
        gate.ThrowOnAcquire = new PolitenessException(PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(
            () => client.DiscoverFoldersAsync(CancellationToken.None));
    }

    private static (FreshdeskSolutionsClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var options = Options.Create(new FreshdeskOptions { BaseUrl = BaseUrl });
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) };

        var client = new FreshdeskSolutionsClient(
            httpClient,
            gate,
            Options.Create(new PolitenessOptions()),
            options,
            NullLogger<FreshdeskSolutionsClient>.Instance);

        return (client, gate, handler);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~FreshdeskSolutionsClientTests"`
Expected: FAIL — `FreshdeskSolutionsClient` does not exist (compile error).

- [ ] **Step 3: Implement FreshdeskSolutionsClient**

Create `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClient.cs`:

```csharp
using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// HTTP client for the Pinball Brothers Freshdesk support portal
// (pinballbrothers.freshdesk.com). Crawls the live site fresh on every call —
// no hardcoded category/folder/article lists — so newly published content is
// always picked up. Selectors verified against real markup 2026-07-03:
//   - Category/folder list: div.cs-s > h3.heading > a (category name);
//     div.list-lead > a[href*='/support/solutions/folders/'] (folder name via
//     its title= attribute + href).
//   - Folder article list:  section.article-list.c-list > .c-row.c-article-row
//     > .article-title > a.c-link[href]; pagination via li.next:not(.disabled) a[href].
public sealed class FreshdeskSolutionsClient : PoliteScraperBase
{
    private readonly HttpClient _http;
    private readonly FreshdeskOptions _options;
    private static readonly HtmlParser Parser = new();

    public FreshdeskSolutionsClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<FreshdeskOptions> options,
        ILogger<FreshdeskSolutionsClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<FreshdeskFolder>> DiscoverFoldersAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{_options.BaseUrl}{_options.SolutionsHomePath}");
        var html = await GetStringPolitelyAsync(_http, url, cancellationToken).ConfigureAwait(false);

        using var doc = Parser.ParseDocument(html);
        var folders = new List<FreshdeskFolder>();

        foreach (var categoryEl in doc.QuerySelectorAll("div.cs-s"))
        {
            var categoryName = categoryEl.QuerySelector("h3.heading a")?.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(categoryName)) continue;

            foreach (var folderAnchor in categoryEl.QuerySelectorAll("div.list-lead a[href*='/support/solutions/folders/']"))
            {
                var href = folderAnchor.GetAttribute("href");
                var folderName = folderAnchor.GetAttribute("title");
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(folderName)) continue;

                folders.Add(new FreshdeskFolder(
                    CategoryName: categoryName,
                    FolderName: WebUtility.HtmlDecode(folderName),
                    Url: $"{_options.BaseUrl}{href}"));
            }
        }

        Logger.LogInformation("Freshdesk: discovered {Count} folder(s) across all categories.", folders.Count);
        return folders;
    }

    public async Task<IReadOnlyList<FreshdeskArticleSummary>> DiscoverArticlesInFolderAsync(
        FreshdeskFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var summaries = new List<FreshdeskArticleSummary>();
        string? pageUrl = folder.Url;

        while (pageUrl is not null)
        {
            var html = await GetStringPolitelyAsync(_http, new Uri(pageUrl), cancellationToken).ConfigureAwait(false);
            using var doc = Parser.ParseDocument(html);

            foreach (var anchor in doc.QuerySelectorAll("section.article-list.c-list a.c-link[href]"))
            {
                var href = anchor.GetAttribute("href");
                var title = anchor.TextContent.Trim();
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title)) continue;

                summaries.Add(new FreshdeskArticleSummary(
                    Title: WebUtility.HtmlDecode(title),
                    Url: $"{_options.BaseUrl}{href}",
                    Folder: folder));
            }

            // "Next" link is absent entirely on single-page folders, and
            // present-but-disabled (no href) on the last page of a
            // multi-page folder — both terminate the loop.
            var nextHref = doc.QuerySelector("li.next:not(.disabled) a[href]")?.GetAttribute("href");
            pageUrl = string.IsNullOrWhiteSpace(nextHref) ? null : $"{_options.BaseUrl}{nextHref}";
        }

        return summaries;
    }

    public async Task<FreshdeskArticle?> FetchArticleAsync(
        FreshdeskArticleSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, new Uri(summary.Url), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Freshdesk: failed to fetch article '{Url}'; skipping.", summary.Url);
            return null;
        }

        var extracted = FreshdeskArticleExtractor.Extract(html, summary.Url);
        if (extracted is null)
        {
            Logger.LogWarning("Freshdesk: could not extract content from article '{Url}'; skipping.", summary.Url);
            return null;
        }

        return new FreshdeskArticle
        {
            Title = extracted.Title,
            Url = summary.Url,
            Folder = summary.Folder,
            BodyText = extracted.BodyText,
            Attachments = extracted.Attachments,
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~FreshdeskSolutionsClientTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClient.cs tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClientTests.cs
git commit -m "feat(scraper) add FreshdeskSolutionsClient (paginated discovery + article fetch)"
```

---

### Task 4: Extend ClassifyDocumentType for Freshdesk folder context

**Files:**
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs:305-346` (the `ClassifyDocumentType` method)
- Test: `tests/PinballWizard.Application.Tests/ScraperOrchestratorClassifyTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ScraperOrchestrator.ClassifyDocumentType` now also returns `Schematic` when `context` contains `"electronics"`, and `Rulesheet` when `linkText` contains `"rulebook"`. Task 5 relies on both.

- [ ] **Step 1: Write the failing tests**

Append to `tests/PinballWizard.Application.Tests/ScraperOrchestratorClassifyTests.cs` (after the existing `ClassifyDocumentType_PlainManual_StaysManual` test):

```csharp
    // Pinball Brothers Freshdesk: "QUEEN Pinball - Rulebook" is the exact
    // article title Pinball Brothers uses — "rulebook" does not contain the
    // substring "rules", so it needs its own keyword (verified against real
    // Freshdesk content 2026-07-03).
    [Fact]
    public void ClassifyDocumentType_Rulebook_ReturnsRulesheet()
    {
        Assert.Equal(DocumentType.Rulesheet,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/1", "QUEEN Pinball - Rulebook"),
                "Freshdesk Support Portal — Queen - General"));
    }

    // Pinball Brothers Freshdesk "Electronics" folders (e.g. "QUEEN -
    // Electronics", "ALIEN - Electronics") hold schematics/wiring diagrams.
    // The folder-name context is the reliable signal — link text varies
    // per article and isn't guaranteed to say "schematic".
    [Fact]
    public void ClassifyDocumentType_ElectronicsFolderContext_ReturnsSchematic()
    {
        Assert.Equal(DocumentType.Schematic,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/2", "Alien - Schematics"),
                "Freshdesk Support Portal — ALIEN - Electronics"));
    }

    [Fact]
    public void ClassifyDocumentType_ElectronicsFolderContext_OverridesGenericLinkText()
    {
        // Even when the link text gives no hint at all, the folder-name
        // context alone must be enough to classify as Schematic.
        Assert.Equal(DocumentType.Schematic,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/3", "Wiring diagram v2"),
                "Freshdesk Support Portal — QUEEN - Electronics"));
    }

    // Both Freshdesk Service Bulletin folder-name variants ("Service
    // Bulletin" and "SERVICE BULLETINS") classify identically via the
    // existing case-insensitive "service bulletin" context substring match.
    [Theory]
    [InlineData("Freshdesk Support Portal — Service Bulletin")]
    [InlineData("Freshdesk Support Portal — SERVICE BULLETINS")]
    public void ClassifyDocumentType_FreshdeskServiceBulletinFolders_ReturnsServiceBulletin(string context)
    {
        Assert.Equal(DocumentType.ServiceBulletin,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/4", "#001 Drop target bank coil short circuit"),
                context));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ScraperOrchestratorClassifyTests"`
Expected: FAIL — `ClassifyDocumentType_Rulebook_ReturnsRulesheet` and the two Electronics tests fail (return `Other` instead of `Rulesheet`/`Schematic`); the Service Bulletin tests already pass (existing logic already handles them, confirming no regression risk there).

- [ ] **Step 3: Extend ClassifyDocumentType**

In `src/PinballWizard.Application/ScraperOrchestrator.cs`, modify the `ClassifyDocumentType` method (lines 305-346):

```csharp
    internal static DocumentType ClassifyDocumentType(DiscoveredLink link, string context)
    {
        var url = link.FileUrl.ToLowerInvariant();
        var text = (link.LinkText ?? "").ToLowerInvariant();
        var ctx = context.ToLowerInvariant();

        if (text.Contains("feature matrix")) return DocumentType.FeatureMatrix;

        if (ctx.Contains("service bulletin")) return DocumentType.ServiceBulletin;
        if (ctx.Contains("game code")) return DocumentType.Firmware;
        if (ctx.Contains("promotional")) return DocumentType.Flyer;

        // Pinball Brothers Freshdesk "*- Electronics" folders hold
        // schematics/wiring diagrams. Folder-name context is the reliable
        // signal here — link text varies per article and isn't guaranteed
        // to mention "schematic".
        if (ctx.Contains("electronics")) return DocumentType.Schematic;

        if (text.Contains("manual")) return DocumentType.Manual;
        if (text.Contains("schematic")) return DocumentType.Schematic;
        if (text.Contains("firmware") || text.Contains("game code")) return DocumentType.Firmware;
        if (text.Contains("bulletin") || text.Contains("sb ") || text.Contains("sb#")) return DocumentType.ServiceBulletin;
        if (text.Contains("flyer") || text.Contains("feature")) return DocumentType.Flyer;
        if (text.Contains("spec")) return DocumentType.SpecSheet;

        // ADR-0042: "rules" / "rulesheet" / "rule sheet" in link text → Rulesheet.
        // Checked AFTER the "manual" branch so a doc whose link text is
        // "Rules Manual" or "Owner's Manual & Rules" has already returned Manual
        // above. We only catch standalone rules PDFs (e.g. "Spooky Rules",
        // "Game Rules PDF") that would otherwise fall to Other.
        // "rulebook" (Pinball Brothers Freshdesk's exact article title) is a
        // separate keyword since "rulebook" does not contain the substring
        // "rules".
        if (text.Contains("rulesheet") || text.Contains("rule sheet") || text.Contains("rulebook") ||
            (text.Contains("rules") && !text.Contains("manual")))
            return DocumentType.Rulesheet;

        if (url.Contains("manual")) return DocumentType.Manual;
        if (url.Contains("schematic")) return DocumentType.Schematic;
        if (url.Contains("sb") && url.Contains(".pdf")) return DocumentType.ServiceBulletin;
        if (url.EndsWith(".zip") || url.EndsWith(".spk")) return DocumentType.Firmware;

        // ADR-0042: "rules" / "rulesheet" in URL (without "manual" in URL or
        // already-matched text). Catches file names like
        // "spooky-beetlejuice-rules.pdf" when link text is absent or generic.
        if ((url.Contains("rules") || url.Contains("rulesheet")) &&
            !url.Contains("manual"))
            return DocumentType.Rulesheet;

        return DocumentType.Other;
    }
```

(Only two lines changed from the original: the new `if (ctx.Contains("electronics")) return DocumentType.Schematic;` block, and `text.Contains("rulebook") ||` added to the Rulesheet condition.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ScraperOrchestratorClassifyTests"`
Expected: PASS (all tests, including the pre-existing ones — confirms no regression).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/ScraperOrchestrator.cs tests/PinballWizard.Application.Tests/ScraperOrchestratorClassifyTests.cs
git commit -m "feat(classify) recognize Freshdesk Electronics-folder context and Rulebook keyword"
```

---

### Task 5: PbFreshdeskDocumentScraper (ISourceScraper)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraper.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraperTests.cs`

**Interfaces:**
- Consumes: `FreshdeskSolutionsClient` (Task 3), `IngestionSourceIds.PinballBrothersFreshdesk` (Task 1), `ScraperOrchestrator.ClassifyDocumentType` is NOT called directly by the scraper (that happens later in `ScraperOrchestrator.BuildDocumentRecord` — the scraper only sets `DiscoveryContext`/`LinkText`/`GameSlug` on the `DiscoveredLink`, matching every other scraper's division of labor).
- Produces: `PbFreshdeskDocumentScraper : PoliteScraperBase, ISourceScraper` with `Name = "Pinball Brothers Freshdesk Documents"`, `SourceId = "pb_freshdesk"`. Task 6 (DI) and Task 9 (seed) reference `SourceId`/`Name`.

- [ ] **Step 1: Write the failing scraper tests**

Create `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraperTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class PbFreshdeskDocumentScraperTests
{
    private const string BaseUrl = "https://pinballbrothers.freshdesk.com";
    private const string SolutionsUrl = $"{BaseUrl}/support/solutions";

    private const string SolutionsHomeHtml = """
        <html><body>
        <section id="solutions-home">
          <div class="cs-s">
            <h3 class="heading"> <a href="/x">General</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list"><div class="list-lead">
                <a href="/support/solutions/folders/1" title="Service Bulletin"> Service Bulletin <span class='item-count'>1</span></a>
              </div></section>
            </div>
          </div>
          <div class="cs-s">
            <h3 class="heading"> <a href="/x">FAQs QUEEN</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list"><div class="list-lead">
                <a href="/support/solutions/folders/2" title="Queen - General"> Queen - General <span class='item-count'>2</span></a>
              </div></section>
            </div>
          </div>
        </section>
        </body></html>
        """;

    private const string ServiceBulletinFolderHtml = """
        <html><body><h2 class="heading">Service Bulletin</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/10-sb1" class="c-link">#001 Drop target bank coil short circuit</a></div></div>
        </section>
        </body></html>
        """;

    private const string QueenGeneralFolderHtml = """
        <html><body><h2 class="heading">Queen - General</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/20-manual" class="c-link">QUEEN Pinball - Technical Manual</a></div></div>
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/21-howto" class="c-link">"How-to" Remove Guitar playfield</a></div></div>
        </section>
        </body></html>
        """;

    private static string ArticleWithAttachment(string title) => $$"""
        <html><body>
        <h2 class="heading">{{title}} <a id="print-article"><span>Print</span></a></h2>
        <article class="article-body" id="article-body"><p>Body text.</p></article>
        <div class="cs-g-c attachments"><div class="attachment"><div class="attach_content"><div class="ellipsis">
            <a href="/helpdesk/attachments/999" class="filename" title='{{title}}.pdf'>truncated...</a>
        </div></div></div></div>
        </body></html>
        """;

    private const string ArticleWithoutAttachmentHtml = """
        <html><body>
        <h2 class="heading">"How-to" Remove Guitar playfield <a id="print-article"><span>Print</span></a></h2>
        <article class="article-body" id="article-body"><p>Step-by-step instructions.</p></article>
        </body></html>
        """;

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsOnlyAttachmentBearingArticlesWithProvenance()
    {
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml(SolutionsUrl, SolutionsHomeHtml)
            .MapHtml($"{BaseUrl}/support/solutions/folders/1", ServiceBulletinFolderHtml)
            .MapHtml($"{BaseUrl}/support/solutions/folders/2", QueenGeneralFolderHtml)
            .MapHtml($"{BaseUrl}/support/solutions/articles/10-sb1", ArticleWithAttachment("#001 Drop target bank coil short circuit"))
            .MapHtml($"{BaseUrl}/support/solutions/articles/20-manual", ArticleWithAttachment("QUEEN Pinball - Technical Manual"))
            .MapHtml($"{BaseUrl}/support/solutions/articles/21-howto", ArticleWithoutAttachmentHtml));

        var items = await ScrapeAllAsync(scraper);

        // Only the two attachment-bearing articles yield. The text-only
        // "How-to" article is skipped entirely — it belongs to the
        // synthesizer path (Task 7/8), not this scraper.
        Assert.Equal(2, items.Count);

        var bulletin = items.Single(i => i.Link!.LinkText == "#001 Drop target bank coil short circuit");
        Assert.Null(bulletin.Link!.GameSlug); // "General" category → no specific machine.
        Assert.Equal("Freshdesk Support Portal — Service Bulletin", bulletin.DiscoveryContext);
        Assert.Equal($"{BaseUrl}/support/solutions/articles/10-sb1", bulletin.DiscoveryUrl);
        Assert.Equal(SourceType.PinballBrothersFreshdeskArticle, bulletin.SourceType);

        var manual = items.Single(i => i.Link!.LinkText == "QUEEN Pinball - Technical Manual");
        Assert.Equal("queen", manual.Link!.GameSlug); // "FAQs QUEEN" category → "queen".
        Assert.Equal("Freshdesk Support Portal — Queen - General", manual.DiscoveryContext);
        Assert.Equal($"{BaseUrl}/helpdesk/attachments/999", manual.Link.FileUrl);

        // Politeness invariants across the whole discovery + fetch chain:
        // homepage (1) + 2 folder pages + 3 article fetches = 6 requests.
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_ArticleFetchFailure_DoesNotAbortRun()
    {
        var (scraper, _, _) = BuildScraper(h => h
            .MapHtml(SolutionsUrl, SolutionsHomeHtml)
            .Map($"{BaseUrl}/support/solutions/folders/1",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{BaseUrl}/support/solutions/folders/2", QueenGeneralFolderHtml)
            .MapHtml($"{BaseUrl}/support/solutions/articles/20-manual", ArticleWithAttachment("QUEEN Pinball - Technical Manual"))
            .MapHtml($"{BaseUrl}/support/solutions/articles/21-howto", ArticleWithoutAttachmentHtml));

        var items = await ScrapeAllAsync(scraper);

        // The Service Bulletin folder's discovery failed (500), but the
        // Queen folder's manual still yields — one folder's failure must
        // not abort the whole run.
        Assert.Single(items);
        Assert.Equal("QUEEN Pinball - Technical Manual", items[0].Link!.LinkText);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_YieldsNothingAndDoesNotThrow()
    {
        var (scraper, _, _) = BuildScraper(h => h
            .Map(SolutionsUrl, _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        var (scraper, gate, _) = BuildScraper(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));
        gate.ThrowOnAcquire = new PolitenessException(PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public void Name_And_SourceId_MatchCanonicalRegistrations()
    {
        var (scraper, _, _) = BuildScraper(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));

        Assert.Equal("Pinball Brothers Freshdesk Documents", scraper.Name);
        Assert.Equal("pb_freshdesk", scraper.SourceId);
        Assert.Equal("Pinball Brothers", scraper.Manufacturer);
    }

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(PbFreshdeskDocumentScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None)) items.Add(item);
        return items;
    }

    private static (PbFreshdeskDocumentScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var freshdeskOptions = Options.Create(new FreshdeskOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) };

        var client = new FreshdeskSolutionsClient(
            httpClient, gate, politenessOpts, freshdeskOptions,
            NullLogger<FreshdeskSolutionsClient>.Instance);

        var scraper = new PbFreshdeskDocumentScraper(
            client, gate, politenessOpts,
            NullLogger<PbFreshdeskDocumentScraper>.Instance);

        return (scraper, gate, handler);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~PbFreshdeskDocumentScraperTests"`
Expected: FAIL — `PbFreshdeskDocumentScraper` does not exist (compile error).

- [ ] **Step 3: Implement PbFreshdeskDocumentScraper**

Create `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraper.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// Discovers PDF/file attachments (Manuals, Rulebooks, Schematics, Service
// Bulletins) on the Pinball Brothers Freshdesk support portal and yields a
// ScrapedItem per attachment. Text-only articles (no attachment) are skipped
// here — they flow through PbFreshdeskArticleSynthesizer instead (Task 7/8).
public sealed class PbFreshdeskDocumentScraper : PoliteScraperBase, ISourceScraper
{
    // Category-name substrings that identify a specific machine. Matched
    // against category names like "FAQs QUEEN" / "FAQ PREDATOR" — deliberately
    // substring-based since Pinball Brothers is inconsistent about the
    // "FAQ" vs "FAQs" prefix and singular/plural.
    private static readonly string[] KnownGameSlugs = ["alien", "queen", "abba", "predator"];

    private readonly FreshdeskSolutionsClient _client;

    public string Name => "Pinball Brothers Freshdesk Documents";
    public string Manufacturer => "Pinball Brothers";
    public string SourceId => IngestionSourceIds.PinballBrothersFreshdesk;

    public PbFreshdeskDocumentScraper(
        FreshdeskSolutionsClient client,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<PbFreshdeskDocumentScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Pinball Brothers Freshdesk document scraper starting");

        var folders = await TryDiscoverFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (folders is null) yield break;

        foreach (var folder in folders)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var summaries = await TryDiscoverArticlesAsync(folder, cancellationToken).ConfigureAwait(false);

            foreach (var summary in summaries)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                var article = await _client.FetchArticleAsync(summary, cancellationToken).ConfigureAwait(false);
                if (article is null || article.Attachments.Count == 0) continue;

                var gameSlug = MatchGameSlug(folder.CategoryName);
                var discoveryContext = $"Freshdesk Support Portal — {folder.FolderName}";

                foreach (var attachment in article.Attachments)
                {
                    yield return new ScrapedItem
                    {
                        Link = new DiscoveredLink
                        {
                            FileUrl = attachment.Url,
                            LinkText = article.Title,
                            DiscoveryContext = discoveryContext,
                            GameSlug = gameSlug,
                        },
                        SourceType = SourceType.PinballBrothersFreshdeskArticle,
                        DiscoveryUrl = article.Url,
                        DiscoveryContext = discoveryContext,
                    };
                }
            }
        }

        Logger.LogInformation("Pinball Brothers Freshdesk document scraper complete");
    }

    // Factored out of the iterator body per the codebase's established
    // pattern (see PbGamePageDocumentScraper.TryExtractLinks): a try/catch
    // around a yield-containing block is disallowed by the C# iterator
    // rules, so per-source-page failures are caught here and reported as a
    // sentinel (null / empty list) that the iterator can branch on freely.
    private async Task<List<FreshdeskFolder>?> TryDiscoverFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _client.DiscoverFoldersAsync(cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Pinball Brothers Freshdesk document scraper: folder discovery failed; aborting for this run.");
            return null;
        }
    }

    private async Task<List<FreshdeskArticleSummary>> TryDiscoverArticlesAsync(
        FreshdeskFolder folder, CancellationToken cancellationToken)
    {
        try
        {
            return (await _client.DiscoverArticlesInFolderAsync(folder, cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogWarning(ex,
                "Pinball Brothers Freshdesk document scraper: article discovery failed for folder '{Folder}'; skipping this folder.",
                folder.FolderName);
            return [];
        }
    }

    private static string? MatchGameSlug(string categoryName)
    {
        var lower = categoryName.ToLowerInvariant();
        foreach (var slug in KnownGameSlugs)
        {
            if (lower.Contains(slug, StringComparison.Ordinal)) return slug;
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~PbFreshdeskDocumentScraperTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraper.cs tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraperTests.cs
git commit -m "feat(scraper) add PbFreshdeskDocumentScraper for attachment-bearing articles"
```

---

### Task 6: DI registration + SourceAliases entry

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskScrapingExtensions.cs`
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs:368-397` (the `SourceAliases` dictionary)
- Modify: `src/PinballWizard.Cli/Program.cs` (wire `AddPinballBrothersFreshdeskScraping` alongside the existing `AddPinballBrothersScraping` call)
- Test: `tests/PinballWizard.Infrastructure.Tests/SourceAliasContractTests.cs` and `ScraperSourceIdContractTests.cs` (no edits needed — both discover scrapers via reflection; this task's job is to make them pass, not to add new assertions)

**Interfaces:**
- Consumes: `PbFreshdeskDocumentScraper` (Task 5), `FreshdeskSolutionsClient` (Task 3), `FreshdeskOptions` (Task 1).
- Produces: `IServiceCollection.AddPinballBrothersFreshdeskScraping(IConfiguration)` extension method.

- [ ] **Step 1: Add the SourceAliases entry (test-first via the existing contract test)**

Run the existing contract test now to confirm it currently fails once Task 5's scraper is registered in DI — but since DI wiring hasn't happened yet, `SourceAliasContractTests` only scans types via reflection (not DI), so it will already fail as soon as `PbFreshdeskDocumentScraper` exists as a type in the assembly, regardless of DI registration:

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~SourceAliasContractTests"`
Expected: FAIL — `PbFreshdeskDocumentScraper → "Pinball Brothers Freshdesk Documents"` is not in `ScraperOrchestrator.KnownSourceCanonicalNames`.

- [ ] **Step 2: Add the SourceAliases entry**

In `src/PinballWizard.Application/ScraperOrchestrator.cs`, add to the `SourceAliases` dictionary (after `["multimorphic"] = "Multimorphic"`, before the `opdb` comment block):

```csharp
        ["pb_freshdesk"] = "Pinball Brothers Freshdesk Documents",
```

- [ ] **Step 3: Run the contract tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~SourceAliasContractTests|FullyQualifiedName~ScraperSourceIdContractTests"`
Expected: PASS (both suites — `ScraperSourceIdContractTests` already passes since `IngestionSourceIds.PinballBrothersFreshdesk` was added in Task 1).

- [ ] **Step 4: Implement the DI registration extension**

Create `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskScrapingExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// DI registration helpers for the Pinball Brothers Freshdesk support-portal
// scraper. Separate from PinballBrothers.ServiceCollectionExtensions because
// this targets a different host (pinballbrothers.freshdesk.com, not
// pinballbrothers.com) with its own HttpClient and politeness configuration.
public static class FreshdeskScrapingExtensions
{
    public static IServiceCollection AddPinballBrothersFreshdeskScraping(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FreshdeskOptions>()
            .Bind(configuration.GetSection(FreshdeskOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<FreshdeskSolutionsClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var opts = sp.GetRequiredService<IOptions<FreshdeskOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<PbFreshdeskDocumentScraper>();
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PbFreshdeskDocumentScraper>());

        // The synthesizer (Task 7) is registered here too since it shares
        // this same HttpClient/FreshdeskSolutionsClient registration.
        services.AddTransient<PbFreshdeskArticleSynthesizer>();

        return services;
    }
}
```

- [ ] **Step 5: Wire into Program.cs**

In `src/PinballWizard.Cli/Program.cs`, find the line `builder.Services.AddPinballBrothersScraping(builder.Configuration);` (around line 1678) and add immediately after it:

```csharp
    builder.Services.AddPinballBrothersScraping(builder.Configuration);
    builder.Services.AddPinballBrothersFreshdeskScraping(builder.Configuration);
```

(Task 7 will add the `PbFreshdeskArticleSynthesizer` class this registers — until then this line will fail to compile. Proceed to Task 7 immediately; do not run a full build between Step 5 and Task 7's Step 3.)

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskScrapingExtensions.cs src/PinballWizard.Application/ScraperOrchestrator.cs src/PinballWizard.Cli/Program.cs
git commit -m "feat(di) register Pinball Brothers Freshdesk scraper and source alias"
```

---

### Task 7: PbFreshdeskArticleSynthesizer

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskArticleSynthesizer.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/PbFreshdeskArticleSynthesizerTests.cs`

**Interfaces:**
- Consumes: `FreshdeskArticle` (Task 2), `IChunker`/`ChunkRequest`/`Chunk` (existing), `ExtractedDocument`/`ExtractedPage`/`ExtractionStatus` (existing).
- Produces: `PbFreshdeskArticleSynthesizer.Synthesize(FreshdeskArticle article, ChunkRequest chunkRequest) : IReadOnlyList<Chunk>`. Task 8 (CLI verb) consumes this.

- [ ] **Step 1: Write the failing synthesizer tests**

Create `tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/PbFreshdeskArticleSynthesizerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class PbFreshdeskArticleSynthesizerTests
{
    private static HybridChunker NewChunker() =>
        new(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance);

    private static PbFreshdeskArticleSynthesizer NewSynthesizer() =>
        new(NewChunker(), NullLogger<PbFreshdeskArticleSynthesizer>.Instance);

    private static FreshdeskArticle SampleArticle(string? bodyText = null) => new()
    {
        Title = "Volume is \"flickering\" up/down",
        Url = "https://pinballbrothers.freshdesk.com/support/solutions/articles/80000596607-volume-is-flickering-up-down",
        Folder = new FreshdeskFolder("FAQs ALIEN", "ALIEN - General", "https://pinballbrothers.freshdesk.com/support/solutions/folders/80000432961"),
        BodyText = bodyText ?? "After game boot, I see volume is changing rapidly up and down. 1. Check the fuses in playfield controller box. 2. Check and reseat cables from playfield.",
    };

    private static ChunkRequest SampleRequest() => new(
        MachineId: "mch_alien_g5b0e",
        MachineTitle: "Alien",
        Manufacturer: "Pinball Brothers",
        DocumentId: "pb_freshdesk_80000596607",
        DocumentUrl: "https://pinballbrothers.freshdesk.com/support/solutions/articles/80000596607-volume-is-flickering-up-down",
        DocumentType: DocumentType.SupportArticle);

    [Fact]
    public void Ctor_NullChunker_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new PbFreshdeskArticleSynthesizer(null!, NullLogger<PbFreshdeskArticleSynthesizer>.Instance));

    [Fact]
    public void Synthesize_NullArticle_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(null!, SampleRequest()));

    [Fact]
    public void Synthesize_NullChunkRequest_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            NewSynthesizer().Synthesize(SampleArticle(), null!));

    [Fact]
    public void Synthesize_SampleArticle_ReturnsNonEmptyChunks()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Synthesize_SampleArticle_TitleInAttributedText()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Volume is \"flickering\" up/down", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_SourceUrlInAttributedText()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains(
            "https://pinballbrothers.freshdesk.com/support/solutions/articles/80000596607-volume-is-flickering-up-down",
            allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_FolderNameInAttributedText()
    {
        // Folder name is the provenance breadcrumb for a support article —
        // "ALIEN - General" tells the reader which manufacturer knowledge
        // base section this came from.
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("ALIEN - General", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SampleArticle_BodyContentPresent()
    {
        var chunks = NewSynthesizer().Synthesize(SampleArticle(), SampleRequest());
        var allText = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Check the fuses in playfield controller box", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_EmptyBodyText_ReturnsEmpty_NoFabrication()
    {
        // Invariant #17: empty body must yield 0 chunks, not placeholder content.
        var article = SampleArticle(bodyText: "");
        var chunks = NewSynthesizer().Synthesize(article, SampleRequest());

        Assert.Empty(chunks);
    }

    [Fact]
    public void Synthesize_WhitespaceOnlyBodyText_ReturnsEmpty()
    {
        var article = SampleArticle(bodyText: "   \r\n   \t  ");
        var chunks = NewSynthesizer().Synthesize(article, SampleRequest());

        Assert.Empty(chunks);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~PbFreshdeskArticleSynthesizerTests"`
Expected: FAIL — `PbFreshdeskArticleSynthesizer` does not exist (compile error).

- [ ] **Step 3: Implement PbFreshdeskArticleSynthesizer**

Create `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskArticleSynthesizer.cs`:

```csharp
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// Converts a text-only FreshdeskArticle (no PDF attachment — troubleshooting
// Q&A, "how to" guides, update notes) into Chunk[] ready for AI Search
// indexing. Mirrors TwipNewsletterSynthesizer: builds a single-page
// ExtractedDocument from the article body and passes it to IChunker.
public sealed class PbFreshdeskArticleSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<PbFreshdeskArticleSynthesizer> _logger;

    public PbFreshdeskArticleSynthesizer(
        IChunker chunker,
        ILogger<PbFreshdeskArticleSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    // Returns an empty list when the article body is empty or whitespace
    // (logs a warning — no fabrication, per Invariant #17).
    public IReadOnlyList<Chunk> Synthesize(FreshdeskArticle article, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(article.BodyText))
        {
            _logger.LogWarning(
                "PbFreshdeskArticleSynthesizer: article '{Title}' has empty BodyText; skipping.",
                article.Title);
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
            "PbFreshdeskArticleSynthesizer: '{Title}' ({Folder}) → {Count} chunk(s) ({Tokens} tokens total).",
            article.Title, article.Folder.FolderName, chunks.Count, chunks.Sum(c => c.TokenCount));

        return chunks;
    }

    private static string BuildAttributedText(FreshdeskArticle article)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {article.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Pinball Brothers Support — {article.Folder.FolderName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {article.Url}");
        sb.AppendLine();
        sb.Append(article.BodyText);

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Build and run tests to verify they pass**

Run: `dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj` (confirms Task 6's `AddPinballBrothersFreshdeskScraping` call now compiles, since `PbFreshdeskArticleSynthesizer` exists).
Expected: Build succeeded.

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~PbFreshdeskArticleSynthesizerTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskArticleSynthesizer.cs tests/PinballWizard.Infrastructure.Tests/Scraping/PinballBrothers/Freshdesk/PbFreshdeskArticleSynthesizerTests.cs
git commit -m "feat(rag) add PbFreshdeskArticleSynthesizer for text-only support articles"
```

---

### Task 8: CLI verb --sync-pb-freshdesk-articles

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs` (add the option declaration near `syncTwipNewsletterOption`, register it on `rootCommand`, parse its value, and add the dispatch block mirroring `--sync-twip-newsletter`/`--sync-kineticist-tutorials`)

**Interfaces:**
- Consumes: `FreshdeskSolutionsClient` (Task 3), `PbFreshdeskArticleSynthesizer` (Task 7), `IMachineTitleLookupRepository` (existing), `IRagIndexer` (existing).
- Produces: the `--sync-pb-freshdesk-articles` CLI flag.

- [ ] **Step 1: Add the option declaration**

In `src/PinballWizard.Cli/Program.cs`, after the `twipSinceOption` declaration (around line 177), add:

```csharp
var syncPbFreshdeskArticlesOption = new Option<bool>("--sync-pb-freshdesk-articles")
{
    Description = "Fetch and index text-only Pinball Brothers Freshdesk support articles (troubleshooting Q&A, \"how to\" guides, update notes with no PDF attachment) as SupportArticle documents in AI Search. Attachment-bearing articles (Manuals, Rulebooks, Schematics, Service Bulletins) are handled separately by --source pb_freshdesk, not this verb. Machine linking uses IMachineTitleLookupRepository keyed on the Freshdesk category name (Alien/Queen/ABBA/Predator); General-category articles index under a synthetic 'pb_support' machine id. Idempotent: safe to re-run.",
};
```

- [ ] **Step 2: Register it on the root command**

Find `rootCommand.Options.Add(twipSinceOption);` (around line 261) and add immediately after:

```csharp
rootCommand.Options.Add(syncPbFreshdeskArticlesOption);
```

- [ ] **Step 3: Parse the value**

Find `var twipSince = parseResult.GetValue(twipSinceOption);` (around line 298) and add immediately after:

```csharp
var syncPbFreshdeskArticles = parseResult.GetValue(syncPbFreshdeskArticlesOption);
```

- [ ] **Step 4: Add the dispatch block**

Find the end of the `--sync-twip-newsletter` block (the closing `return;` around line 1379, immediately before the `--sync-p3-sdk-docs` comment). Insert this new block immediately after it:

```csharp
    // Handle --sync-pb-freshdesk-articles: text-only Pinball Brothers
    // Freshdesk support articles (no PDF attachment) as SupportArticle chunks
    // in AI Search. Shares FreshdeskSolutionsClient's live crawl with
    // PbFreshdeskDocumentScraper (--source pb_freshdesk) but only processes
    // articles with zero attachments — attachment-bearing articles are that
    // scraper's job. Idempotent: chunk_id hash is stable per article URL.
    if (syncPbFreshdeskArticles)
    {
        var freshdeskClient = host.Services.GetService<PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.FreshdeskSolutionsClient>();
        var freshdeskSynthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.PbFreshdeskArticleSynthesizer>();
        var freshdeskTitleLookups = host.Services.GetService<IMachineTitleLookupRepository>();
        var freshdeskIndexer = host.Services.GetService<IRagIndexer>();

        if (freshdeskClient is null || freshdeskSynthesizer is null || freshdeskTitleLookups is null || freshdeskIndexer is null)
        {
            Console.Error.WriteLine(
                "--sync-pb-freshdesk-articles requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("Discovering Pinball Brothers Freshdesk support folders...");

        var freshdeskFolders = await freshdeskClient.DiscoverFoldersAsync(cancellationToken);
        Console.WriteLine($"Found {freshdeskFolders.Count} folder(s). Discovering articles...");

        var freshdeskIndexed = 0;
        var freshdeskSkippedAttachment = 0;
        var freshdeskSkippedNoContent = 0;
        var freshdeskSkippedNoMachine = 0;
        var freshdeskFailed = 0;
        var freshdeskIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();
        string[] freshdeskKnownGameSlugs = ["alien", "queen", "abba", "predator"];

        foreach (var folder in freshdeskFolders)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var summaries = await freshdeskClient.DiscoverArticlesInFolderAsync(folder, cancellationToken);

            foreach (var summary in summaries)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var article = await freshdeskClient.FetchArticleAsync(summary, cancellationToken);
                if (article is null)
                {
                    freshdeskSkippedNoContent++;
                    continue;
                }

                // Attachment-bearing articles are PbFreshdeskDocumentScraper's
                // job (--source pb_freshdesk) — this verb only handles the
                // text-only remainder.
                if (article.Attachments.Count > 0)
                {
                    freshdeskSkippedAttachment++;
                    continue;
                }

                var categoryLower = folder.CategoryName.ToLowerInvariant();
                var matchedSlug = freshdeskKnownGameSlugs.FirstOrDefault(s => categoryLower.Contains(s, StringComparison.Ordinal));

                string machineId, machineTitle, manufacturer;
                if (matchedSlug is not null)
                {
                    var lookup = await freshdeskTitleLookups.GetByTitleAsync(matchedSlug, cancellationToken);
                    if (lookup is null || lookup.OpdbIds.Count == 0)
                    {
                        Console.Error.WriteLine(
                            $"  Freshdesk: no machine in catalog for slug '{matchedSlug}'; article '{article.Title}' skipped.");
                        freshdeskSkippedNoMachine++;
                        continue;
                    }
                    machineId = lookup.OpdbIds[0];
                    machineTitle = matchedSlug;
                    manufacturer = lookup.Manufacturers.Count > 0 ? lookup.Manufacturers[0] : "Pinball Brothers";
                }
                else
                {
                    // General-category article (FAQ, Getting Started, Warranty
                    // Terms) — not tied to a specific machine. Synthetic id
                    // mirrors TWIP's "pinball_news" pattern.
                    machineId = "pb_support";
                    machineTitle = "Pinball Brothers Support";
                    manufacturer = "Pinball Brothers";
                }

                var articleId = summary.Url.Split('/').Last().Split('-', 2)[0];
                var documentId = $"pb_freshdesk_{articleId}";

                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineId,
                    MachineTitle: machineTitle,
                    Manufacturer: manufacturer,
                    DocumentId: documentId,
                    DocumentUrl: article.Url,
                    DocumentType: PinballWizard.Core.Models.DocumentType.SupportArticle,
                    LastScrapedUtc: DateTimeOffset.UtcNow);

                var chunks = freshdeskSynthesizer.Synthesize(article, chunkRequest);
                if (chunks.Count == 0)
                {
                    freshdeskSkippedNoContent++;
                    continue;
                }

                try
                {
                    var result = await freshdeskIndexer.UpsertAsync(chunkRequest, chunks, freshdeskIndexerOptions, cancellationToken);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                        {
                            Console.Error.WriteLine(
                                $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.Title}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                        }
                        freshdeskFailed++;
                    }
                    else
                    {
                        Console.WriteLine($"  Indexed '{article.Title}' ({folder.FolderName}) → {chunks.Count} chunk(s)");
                        freshdeskIndexed++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Failed to index '{article.Title}': {ex.Message}");
                    freshdeskFailed++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--sync-pb-freshdesk-articles complete: indexed={freshdeskIndexed} skipped_attachment={freshdeskSkippedAttachment} skipped_no_content={freshdeskSkippedNoContent} skipped_no_machine={freshdeskSkippedNoMachine} failed={freshdeskFailed}");
        if (freshdeskFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Manual smoke check of the CLI help text**

Run: `dotnet run --project src/PinballWizard.Cli -- --help`
Expected: Output includes both `--sync-pb-freshdesk-articles` and `--source` (unchanged) in the option list.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git commit -m "feat(cli) add --sync-pb-freshdesk-articles verb for text-only support articles"
```

---

### Task 9: IngestionSource seed update

**Files:**
- Modify: `data/seeds/ingestion_sources.v1.json` (add `pb_freshdesk`, supersede `pb_bulletins`)

**Interfaces:**
- Consumes: nothing new.
- Produces: the `pb_freshdesk` seed entry consumed by `--seed-ingestion-sources` and read by the Admin UI's Sources page.

- [ ] **Step 1: Add the pb_freshdesk entry and supersede pb_bulletins**

In `data/seeds/ingestion_sources.v1.json`, replace the existing `pb_bulletins` entry (lines 203-215) with:

```json
  {
    "id": "pb_bulletins",
    "displayName": "Service Bulletins",
    "scraperImplKey": "pb_bulletins",
    "baseUrl": "https://pinballbrothers.freshdesk.com/",
    "enabled": false,
    "cadence": "none",
    "politenessOverrides": null,
    "sourceGroup": "Pinball Brothers",
    "discoveryStatus": "Superseded",
    "discoveryNotes": "Superseded by pb_freshdesk (2026-07-03), which covers Service Bulletins plus Manuals, Rulebooks, Schematics, and support articles across all four Freshdesk-hosted machines (Alien, Queen, ABBA, Predator). The 2026-05-26 API-key blocker was a red herring — the REST API needs a key, but a plain HTML scrape of /support/solutions/* does not (robots.txt verified to allow it).",
    "discoveryDate": "2026-07-03"
  },
  {
    "id": "pb_freshdesk",
    "displayName": "Freshdesk Support Portal",
    "scraperImplKey": "pb_freshdesk",
    "baseUrl": "https://pinballbrothers.freshdesk.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Pinball Brothers",
    "discoveryStatus": "Active",
    "discoveryNotes": "Covers Manuals, Rulebooks, Schematics, and Service Bulletins (attachment-bearing articles, via --source pb_freshdesk) plus troubleshooting Q&A / How-To / Update notes (text-only articles, via --sync-pb-freshdesk-articles) across all Freshdesk categories: General, FAQs ALIEN, FAQs QUEEN, FAQs ABBA, FAQ PREDATOR. robots.txt allows /support/solutions/* and explicitly carves out Allow: /helpdesk/attachments from the broader Disallow: /helpdesk/.",
    "discoveryDate": "2026-07-03"
  },
```

- [ ] **Step 2: Validate the JSON parses and the seeder accepts it**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~IngestionSourceSeederTests"`
Expected: PASS (the existing seeder contract tests read this file — this catches a malformed JSON edit or a duplicate id).

- [ ] **Step 3: Commit**

```bash
git add data/seeds/ingestion_sources.v1.json
git commit -m "feat(seed) add pb_freshdesk ingestion source, supersede pb_bulletins"
```

---

### Task 10: Full-suite verification

**Files:** none (verification only)

**Interfaces:** none

- [ ] **Step 1: Run the full Infrastructure, Application, and Cli test projects**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests tests/PinballWizard.Application.Tests tests/PinballWizard.Cli.Tests`
Expected: All tests pass, including `SourceAliasContractTests`, `ScraperSourceIdContractTests`, `ScraperOrchestratorClassifyTests`, and every new test added in Tasks 2/3/5/7.

- [ ] **Step 2: Run the project's standard pre-push CI-equivalent filter**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: All tests pass. (Per `feedback_run_full_ci_suite_before_push` — this is the full cross-project contract check, not just the three projects touched directly.)

- [ ] **Step 3: Confirm no drift in the CLI options contract test**

Run: `dotnet test tests/PinballWizard.Cli.Tests --filter "FullyQualifiedName~CliOptionsContractTests"`
Expected: PASS. Note: this test's `BuildRootCommand()` helper is a hand-maintained duplicate of `Program.cs`'s real root command (it already doesn't include `--sync-twip-newsletter`/`--sync-kineticist-tutorials`/`--sync-p3-sdk-docs` either) — it does not actually exercise `Program.cs`, so adding `--sync-pb-freshdesk-articles` there does not affect this test's pass/fail status. This is pre-existing staleness, out of scope for this plan — do not "fix" it here.

- [ ] **Step 4: No commit for this task** (verification only — if anything fails, return to the relevant task above and fix before proceeding)

---

## Deferred from the design spec (deliberate scope cut)

The approved design spec (`docs/superpowers/specs/2026-07-03-pb-freshdesk-ingestion-design.md`) mentioned fetching `sitemap.xml` once per run as a cheap freshness signal (`lastmod` per article) to skip re-fetching unchanged articles. This plan does **not** implement that: every article is fully re-crawled and re-fetched on every run regardless of whether it changed. This is a pure performance optimization, not a correctness requirement — the corpus stays accurate either way, just at the cost of re-fetching ~90 pages instead of only the changed ones on a typical weekly run. Cutting it keeps this plan's scope to the two document pipelines the user actually asked for. If Freshdesk's article count grows large enough that re-crawl time/politeness-delay becomes a problem, add `sitemap.xml`-based skip-if-unchanged as a follow-up task at that point — `FreshdeskSolutionsClient` already exposes discovery and fetch as separate methods, so threading a lastmod check between them is a contained change.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-03-pb-freshdesk-ingestion.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
