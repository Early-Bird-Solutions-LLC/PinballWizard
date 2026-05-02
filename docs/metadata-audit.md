# Metadata Audit — PinballWizard scraper (as of 2026-05-02)

This doc inventories every metadata gap discovered in a triage of the
scraper, ordered by effort. The intent is to give future sessions one place
to consult when deciding what to wire up next, rather than rediscovering
the gaps from scratch each time.

## Project framing

PinballWizard's value proposition to source sites is a **win-win**: we
consolidate community-useful information, and source sites get
attribution-driven traffic from every Phase-2 RAG response we generate.
That balance only holds if our scraping is, and visibly looks like,
respectful and minimal. This audit is therefore organized around two
questions:

1. **What metadata are we already paying to fetch but throwing away?**
   (Modeled-but-unpopulated fields and string-folded-but-not-typed fields.)
2. **What metadata is the source site explicitly publishing for machine
   consumers that we currently ignore?**
   (Open Graph, Schema.org JSON-LD, `<meta>`, sitemap.xml, robots.txt.)

Both categories are higher ROI per line of code than expanding our DOM
selectors, and the second category is *more polite* than DOM scraping.

## Tier 1 — Modeled but never populated

These fields exist in the data model but are never assigned. Wiring them
up requires no new schema, only either (a) populating from data we already
fetch, or (b) extending an existing JS extractor.

| Field | Location | Current state | What's needed |
| --- | --- | --- | --- |
| `EditionInfo.UniqueFeatures` | [GameRecord.cs:35](../src/PinballWizard.Scraper/Models/GameRecord.cs#L35) | always `[]` | DOM survey + JS extractor; per-edition feature bullets |
| `EditionInfo.ImageUrls` | [GameRecord.cs:37](../src/PinballWizard.Scraper/Models/GameRecord.cs#L37) | always `[]` | DOM survey + JS extractor; per-edition cabinet/playfield/backbox imagery |
| `EditionInfo.Availability` | [GameRecord.cs:33](../src/PinballWizard.Scraper/Models/GameRecord.cs#L33) | **partial** ✓ | `sold_out` populated via `contact-for-availability` URL parsing ([StaticMetadataExtractor.cs](../src/PinballWizard.Scraper/Scrapers/StaticMetadataExtractor.cs)). `vault`/`archive` still pending — derive from `GameRecord.DiscoveredOn` |
| `EditionInfo.LimitedQuantity` | [GameRecord.cs:36](../src/PinballWizard.Scraper/Models/GameRecord.cs#L36) | always `null` | DOM survey; LE editions disclose quantity |
| `EditionInfo.Msrp` | [GameRecord.cs:32](../src/PinballWizard.Scraper/Models/GameRecord.cs#L32) | **wired** ✓ | Populated from `contact-for-availability` URL `price` query param |
| `GameRecord.DatePublished` / `ReleaseYear` | [GameRecord.cs:23-29](../src/PinballWizard.Scraper/Models/GameRecord.cs#L23-L29) | **wired** ✓ | Populated from JSON-LD `datePublished` via `StaticMetadataExtractor`. Triaged 2026-05-02: extraction was correct but `CatalogBuilder.MergeGameRecord` was dropping the new fields when merging into an existing record — fixed |
| `GameRecord.Status` | [GameRecord.cs:20](../src/PinballWizard.Scraper/Models/GameRecord.cs#L20) | always `null` | Cheap: derive from `DiscoveredOn` (`vault` / `archive` / `games_listing`) at `MergeGameRecord` time |
| `ClassificationInfo.ContentCategories` | [DocumentRecord.cs:77](../src/PinballWizard.Scraper/Models/DocumentRecord.cs#L77) | always `[]` | Heuristic from filename + link text + tab; rules-based, no extractor needed |
| `DownloadedFileInfo.PageCount` | [DocumentRecord.cs:106](../src/PinballWizard.Scraper/Models/DocumentRecord.cs#L106) | always `null` | Defer to Phase 2 (PdfPig); RAG indexing will need this anyway |
| `DownloadedFileInfo.MimeType` | [DocumentRecord.cs:105](../src/PinballWizard.Scraper/Models/DocumentRecord.cs#L105) | **wired** ✓ | None — populated from HTTP `Content-Type` at [CatalogBuilder.cs:390](../src/PinballWizard.Scraper/Provenance/CatalogBuilder.cs#L390) |

### Already-extracted-but-not-typed (Service Bulletins)

[ServiceBulletinScraper.cs:165-178](../src/PinballWizard.Scraper/Scrapers/ServiceBulletinScraper.cs#L165-L178)
extracts each bulletin's `date` and `relatedGames` from the DOM, but
[lines 197-200](../src/PinballWizard.Scraper/Scrapers/ServiceBulletinScraper.cs#L197-L200)
fold them into the `DiscoveryContext` string instead of populating typed
fields. A new `BulletinDetails` value object hung off `DocumentRecord` (or
a sibling to `Game`) would expose these to Phase 2 cleanly.

## Tier 2 — Highest-ROI: machine-consumer metadata we ignore

The single biggest improvement available is to read what Stern is
**already publishing for machine consumers**. These tags are explicitly
designed for external tools, are typically present in the static HTML
(no Vue render needed), and are far more stable than DOM selectors.

Using them is also the most polite scraping there is: we use what the
site offered us, not what we extracted under duress.

### Open Graph tags

```html
<meta property="og:title" content="Stranger Things | Stern Pinball">
<meta property="og:description" content="...">
<meta property="og:image" content="https://sternpinball.com/wp-content/.../hero.jpg">
<meta property="og:video" content="...">
<meta property="og:url" content="...">
<meta property="og:type" content="product">
```

Provides: clean title (already stripped of cookie-banner noise), short
description, hero image URL, sometimes embedded video URL, canonical URL.

### Schema.org JSON-LD

Modern sites typically emit a `<script type="application/ld+json">` block
with a `Product` schema (or similar) that includes `name`, `description`,
`image`, `brand`, `offers` (with price/MSRP), `productionDate`, and
sometimes `manufacturer`/`designer`. One JSON parse delivers what brittle
DOM selectors fight for.

This is the most reliable source of MSRP and almost certainly the most
reliable source of release year.

### Standard `<meta>` tags

- `<meta name="description">` — short description
- `<link rel="canonical">` — for deduplication
- `<meta name="twitter:image">` / `twitter:description` — fallback values

### Sitemap and robots.txt

- **sitemap.xml** — would replace the
  [`/games/` / `/games/archive/` / `/games/vault/`](../src/PinballWizard.Scraper/Scrapers/GameListingScraper.cs#L19-L24)
  Playwright walks with a single static-XML fetch. Faster, cheaper for
  Stern, less likely to drift if Stern restructures their listing pages.
- **robots.txt** — must be honored. Current scraper does not check.
  Adding a startup check that respects `Disallow:` and `Crawl-delay:` is
  the most visibly polite addition we can make and should land before any
  other public-facing changes.

## Tier 3 — On the page but not modeled

These are visible on game pages but neither extracted nor modeled. They
would need new schema, new extraction, and DOM survey work. Listed here
so they're not forgotten; many overlap with Tier 2 (e.g., `og:image` may
satisfy "hero image" without DOM work).

### Per game

- Release year / launch date — *probably available from JSON-LD*
- Designer (Stern always credits — community values this highly)
- Artist
- Software lead
- Mechanical lead
- Theme/license + license attribution (©Disney, ©Marvel, ©Universal) —
  important for community-sharing context
- Hero image, cabinet art, playfield art galleries — *probably available
  from `og:image` and Schema.org `image`*
- Short tagline + long "About" description — *short usually in
  `og:description` / `<meta name="description">`*
- Embedded video URLs (YouTube trailers/feature videos)
- Authorized dealer / "Where to buy" links

### Per edition

- Quantity produced for LE editions (publicly disclosed)
- Per-edition images and per-edition videos
- Edition-specific feature differentiation copy

### Per document (PDF metadata — Phase 2 via PdfPig)

- Title, author, creation/modification dates
- Number of pages
- Document language
- Embedded keywords
- Manual revision number (e.g., "Rev 2")
- Target firmware version

## Tier 4 — Politeness ethos additions

Not metadata per se, but adjacent and worth wiring at the same time:

- **robots.txt compliance check** at startup (see Tier 2). The right place
  for this is `ScraperOrchestrator.ScrapeAsync` before any scraper runs;
  cache the parsed result for the run.
- **Conditional-request comments** on `FileDownloader` — the ETag /
  `If-Modified-Since` plumbing is already there but not visibly
  documented; one comment block makes the politeness intent obvious to
  reviewers.
- **CLI startup banner** naming the source site, the User-Agent, and the
  polite practices in use (delay between requests, conditional downloads,
  robots.txt compliance). Fits the "code visibly demonstrates respect"
  principle.

## Things deliberately NOT to scrape

For completeness, things we should *not* capture even though they exist
on the source site:

- User-uploaded comments, reviews, forum posts
- Tracking pixels, analytics URLs, third-party embeds
- Anything behind login

## Suggested order of work

1. **robots.txt + Tier 2 OG/JSON-LD ingestion** — biggest ROI, lightest
   touch on Stern, lands the politeness ethos in code.
2. **Tier 1 plumbing**: `Status` (free from listing source),
   `ContentCategories` (heuristic), then DOM-survey-based
   `UniqueFeatures` / `ImageUrls` / `Availability` / `LimitedQuantity`.
3. **Service-bulletin typed fields** — promote `Date` / `RelatedGames`
   from the `DiscoveryContext` string into typed fields.
4. **PDF metadata via PdfPig** — defer to Phase 2; RAG indexing will need
   it anyway.
5. **Tier 3 game-level metadata** (designer, artist, theme, etc.) — only
   pursue what isn't already covered by Tier 2 ingestion.

## Why this isn't already in scraper_plan_v4.md

[scraper_plan_v4.md](scraper_plan_v4.md) describes the intended data
model and pipeline shape. This audit captures the actual implementation
gap as of 2026-05-02 — what is modeled but not wired, and what the
source site offers that we never asked for. It is meant to age out of
relevance as items get knocked off; treat it as a working document, not
a contract.
