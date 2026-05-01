# DOM Validation — sternpinball.com (as of 2026-05-01)

## Method

**Empirical validation was not possible in this session.** Both shells (Bash
and PowerShell) are sandboxed away from network and process execution in the
agent harness used to produce this document, so the planned approach —
launching headless Chromium via Playwright (or `node`/`dotnet` driving
Playwright) to render each Vue.js page and dump `document.body.outerHTML` —
could not be carried out.

What is possible from this session:

- Read the existing C# scrapers and the JS heuristics they evaluate inside
  the browser.
- Reason carefully about what those heuristics actually require the DOM to
  look like in order to succeed.
- Identify the specific facts a follow-up session needs to capture, and the
  shortest set of DOM probes that would unblock fixing the scrapers.

The remainder of this document therefore captures **what each scraper
expects** plus **the minimum DOM facts a future round must confirm**, rather
than empirical findings. Treat every "Recommended fix" below as a hypothesis
that still needs an actual rendered-page check.

### How to do the empirical pass next time

Run any of the following from a shell that is allowed to reach the network
and spawn Chromium:

1. **Use the project's own Playwright** (preferred — already wired):

   ```
   dotnet run --project src/PinballWizard.Scraper -- --source games --dry-run --verbose
   ```

   Then add a one-shot debug helper that calls
   `await page.ContentAsync()` for the three sample game pages and the
   bulletins page and writes the HTML to `data/debug/<slug>.html`. (Don't
   commit the helper — it's a probe.)

2. **Or run an out-of-tree Node script** (faster iteration):

   ```
   npx playwright install chromium    # if not already installed
   npx -y playwright@1.49 codegen https://sternpinball.com/game/stranger-things/
   ```

   `codegen` lets you click around the live page and watch which selectors
   Playwright generates — that is gold for the tab-clicker work in
   particular.

3. **Or `playwright codegen` against the bulletins page** to confirm whether
   the list is a `<table>`, a list of cards, an infinite-scroll feed, or
   has a "Load More" button. Watch the Network tab for XHR requests — if
   the bulletins are loaded from a JSON endpoint, scraping that endpoint
   directly is dramatically simpler than scrolling.

The three game pages and one bulletins page from the brief are the right
sample:

- `https://sternpinball.com/game/stranger-things/` — active flagship; should
  exercise all three tabs.
- `https://sternpinball.com/game/metallica/` — older title; may have a
  different layout or a missing tab.
- `https://sternpinball.com/game/john-wick/` — newest layout; sanity check.
- `https://sternpinball.com/support/service-bulletins/`

---

## Source 1: Game Pages

### Tab navigation

**What the code does today** (`GamePageScraper.ClickTabAsync`,
`src/PinballWizard.Scraper/Scrapers/GamePageScraper.cs:300-331`): tries six
selectors in order, stopping at the first match:

```
button:has-text('<TabName>')
a:has-text('<TabName>')
[role='tab']:has-text('<TabName>')
.tab:has-text('<TabName>')
[class*='tab']:has-text('<TabName>')
li:has-text('<TabName>')
```

The tab names passed in are the exact strings `"Promotional Materials"`,
`"Game Code"`, and `"Specs & Manual"`
(`GamePageScraper.cs:88-93`).

**What's known to be brittle:**

- `:has-text()` does a substring match against the rendered text. If
  Stern's button label is `"Promotional Materials"` the selectors above
  hit. If the button label is something more compact like `"Promo"` or
  `"Promotional"`, none of the six selectors hit and the tab is silently
  skipped — `ClickTabAsync` returns `false`, `ScrapeTabAsync` logs at
  `Debug` level only, and the run continues with zero links from that tab.
- The `&` in `Specs & Manual` is a real character. If Stern renders it as
  `&amp;` somewhere in the JS payload but as `&` in the rendered DOM,
  Playwright's `:has-text` should still match (it compares on the rendered
  text). Worth confirming once.
- The fallback `li:has-text('<TabName>')` is dangerous — any list item
  containing that text matches, including sidebar links or footer entries.
  `ClickAsync` on the wrong element silently navigates somewhere unhelpful
  and the tab content never loads. If the right `<li>` is ever an ancestor
  of the right `<button>`, the order of selectors above means we click the
  outer `<li>` first, which is usually wrong.

**DOM facts to confirm next round (per sample page):**

1. What HTML element actually carries the tab label? `<button>`, `<a>`,
   `<li>`, or a `<div role="tab">`?
2. What is the **exact** rendered text? (Look for non-breaking spaces or
   ampersand entities.)
3. What is the parent container's class? Stern is a WordPress site running
   a custom Vue component, so the wrapper is plausibly something like
   `.game-tabs`, `.tab-nav`, `.elementor-tabs__nav`, or a Vuetify
   `.v-tabs` shell. A class on the parent gives us a tight,
   game-page-specific anchor.
4. Are tabs always all three? In particular, do `metallica` and any vault
   game show a "Game Code" tab? The CLAUDE.md "Open bugs" already flags
   the suspicion that older titles may have only two tabs.
5. Is the active tab toggled with a class (`.active`, `.is-active`,
   `aria-selected="true"`) we can use to wait for content to render
   instead of the current blunt `WaitForTimeoutAsync(1500)`?

**Recommended fix (next round, after empirical check):**

- Replace the six-selector cascade with a single `getByRole('tab', { name:
  ... })` call (Playwright 1.49 has stable role locators) **or** a single
  CSS selector keyed off the tab-nav container's confirmed class. Pick one
  and delete the rest — the `li:has-text()` fallback is a footgun.
- After clicking, `await` an explicit signal of activation (e.g.
  `[aria-selected="true"]:has-text('<TabName>')` or a known panel
  selector) instead of a fixed sleep.
- Distinguish "tab not present on this game" from "tab present but click
  failed". The former is normal for older games; the latter is a bug we
  want to surface.

**Evidence:** unavailable in this session — captured above as "facts to
confirm".

### Editions

**What the code does today** (`GamePageScraper.ExtractEditionsAsync`,
`GamePageScraper.cs:163-226`): runs an in-page JS expression that

1. selects all containers matching `[class*="edition"], [class*="model"],
   [class*="version"], .product-option, .game-model`,
2. for each container, reads name from `h2, h3, h4, [class*="name"],
   [class*="title"]`,
3. reads price from `[class*="price"], [class*="msrp"]`,
4. reads description from `p, [class*="desc"], [class*="body"]`,
5. if no containers matched, falls back to scanning all `h2, h3` headings
   for the regex `/\b(pro|premium|limited edition|le)\b/i`.

It returns `null` (not an empty array) when nothing matches, which the C#
side treats as "no editions". The result is mapped into `EditionInfo` with
fields `Name`, `Msrp`, `Description` only — **image URLs and "unique
features" are not captured at all today**, even though the brief asks
where they live.

**Why this is fragile:**

- `[class*="model"]` is an extremely broad substring match. A page region
  with a class like `model-viewer-canvas` would be treated as an edition
  container and yield a meaningless name.
- The selectors assume editions are siblings (one container per edition).
  If Stern uses a single carousel/slider container with internal slides,
  `containers` is length 1 and the function returns one row that is the
  union of all editions concatenated.
- The fallback regex catches the literal word "Pro" anywhere in `<h2>` or
  `<h3>`, including breadcrumbs and unrelated marketing copy.
- Even when it succeeds, the price text is captured raw — it likely
  includes a leading `$` and possibly thousands separators or the literal
  string `"MSRP:"`. There is no normalization.

**DOM facts to confirm next round:**

1. Whether each edition (Pro / Premium / LE) is its own container, and
   what the container's class is.
2. The class on the price element specifically (we want a tight selector,
   not `[class*="price"]`).
3. Where the **image URLs** live — `<img src>` inside the card, or a CSS
   `background-image` on a `<div>` (CSS backgrounds cannot be scraped via
   `getAttribute('src')`; you have to read computed style or the inline
   style attribute).
4. Where the **unique features** list lives — a `<ul>` inside the card?
   Or a separate "Features" comparison table elsewhere on the page? This
   is the single biggest question for the metadata model.
5. What does an MSRP look like exactly? Is it always `$X,YYY` or is there
   a "Contact for pricing" string for LE?
6. Does the page render different DOM for sold-out / archived / vault
   games? Metallica is the canary here.

**Recommended approach (next round, after empirical check):**

- Pin the edition container to a confirmed class (e.g. `.edition-card`)
  rather than a substring match. Drop `[class*="model"]` and
  `[class*="version"]` entirely.
- Add fields to `EditionInfo` for `ImageUrl` and `Features` (List<string>)
  — leave them nullable so older code still compiles.
- For images, prefer reading `<img src>` over computed background; if it's
  a background, read `getComputedStyle(el).backgroundImage` and strip the
  `url(...)` wrapper.
- Drop the "scan all h2/h3 for Pro|Premium|LE" fallback. Either we know
  the right selector or we log a structured warning so we can fix it —
  silent regex matches are how we end up with bad data in the catalog.
- Normalize MSRP: strip whitespace, currency symbol, commas; preserve raw
  text in a separate field for traceability ("provenance is sacred").

**Evidence:** unavailable in this session.

---

## Source 2: Service Bulletins

Page: `https://sternpinball.com/support/service-bulletins/`

### List structure

**What the code does today** (`ServiceBulletinScraper.ExtractBulletinsAsync`,
`ServiceBulletinScraper.cs:146-185`):

1. Selects every `a[href*=".pdf"]` and `a[href*="wp-content/uploads"]` on
   the page (a wide net, after `ScrollToLoadAllAsync` has done its work).
2. For each link, walks up to the nearest `tr, li, .bulletin,
   [class*="bulletin"], [class*="item"], article` ancestor and looks for
   sibling date and game elements inside it.
3. Date is read from `time, [class*="date"], td:nth-child(2)`.
4. Related games is read from `[class*="game"], td:nth-child(3), .games`.

Note that the six container selectors include a heterogeneous mix:
`<tr>` is a table row, `<li>` is a list item, `article` is semantic, and
the three `[class*=...]` patterns are speculative substring matches. The
JS picks the *closest* matching ancestor, so on a page where every link
is wrapped in both `<li>` and `<article>`, you'd get `<li>` (closer).
That's fine — but it means the `td:nth-child(2)/td:nth-child(3)` date and
game probes only fire when the closest ancestor is `<tr>`.

**What we don't know:**

- Whether the bulletin page renders as a real `<table>` (the `td:nth-
  child(N)` probes were written for that), as a list of cards (`<article>`
  or `<div class="bulletin">`), as a list of `<li>` rows, or as a
  WordPress widget that produces something else entirely.
- Whether each bulletin has only one PDF link or several (a "View" button
  plus a "Download" button, etc.). If multiple links per row, the current
  code emits multiple `DiscoveredLink` rows per bulletin — fine for the
  catalog (deduped by file URL hash) but worth confirming.

### Date + related game extraction

**Critical issue regardless of DOM:** even when these JS reads succeed,
`ServiceBulletinScraper` only **stuffs the date and game-name text into
the `DiscoveryContext` string** (see CLAUDE.md "Open bugs / gaps" item:
"extracts dates and related-game text into the discovery context string
only — never typed into model fields"). They never reach typed fields on
`DocumentRecord`. Continued reading of the file (lines 196 onward, where
the context string is concatenated) will confirm — but the brief already
calls this out and a fix needs to land regardless of what the DOM looks
like.

**DOM facts to confirm next round:**

1. Is the list a `<table>`, a `<ul>` of `<li>`s, a stack of
   `<article>`/`<div class="bulletin">` cards, or a WordPress
   shortcode-rendered widget?
2. Is each row a single PDF link or does it have multiple links + a
   meta-row?
3. What format is the date in? `2024-09-15`, `September 15, 2024`,
   `09/15/24`? Does the DOM include a `<time datetime="...">` ISO
   attribute (much easier to parse than free text)?
4. What does the game-name field look like? A single game, a comma-
   separated list, a comma-separated list of links? Linkable game slugs
   would let us cross-reference into `games.json`.
5. Is there a category / topic / model column the current code ignores?
6. Is there a bulletin **number** (e.g. "SB-2024-014") in the DOM that
   would make a stable identifier separate from the file URL hash?

### Pagination / load-more

**What the code does today** (`ServiceBulletinScraper.ScrollToLoadAllAsync`,
`ServiceBulletinScraper.cs:77-113` and `TryClickLoadMoreAsync`, lines
115-144):

- Scrolls to the bottom in a loop (max 50 iterations).
- After each scroll waits 1s, then counts PDF/upload anchors.
- If the count hasn't changed for 3 iterations, breaks.
- Each iteration also tries `button:has-text('Load More')`,
  `button:has-text('Show More')`, `a:has-text('Load More')`,
  `[class*='load-more']`, and `[class*='pagination'] button:last-child`.

This is a "spray and pray" strategy. The risks:

- If the page paginates with numbered page buttons (1 / 2 / 3 / Next),
  none of the load-more selectors fire and the scroll-stability check
  exits after 3 iterations, capturing only page 1.
- `[class*='pagination'] button:last-child` will reliably click the
  "Next" button if pagination exists with that class — but the same
  scroll-stability loop terminates 3 seconds after the click, before
  page 2 has finished rendering. So even when "Next" is found, only the
  first click's worth of new bulletins is captured.
- If new bulletins load on scroll AND a "Load More" button also exists
  (rare but possible — some Vue pagination components do both), the loop
  works but is wasteful.

**DOM facts to confirm next round:**

1. **The single most important question:** does scrolling load more, does
   a button load more, or do numbered page links navigate? Or — best case
   for us — is there a JSON XHR endpoint we could hit directly and skip
   scraping the rendered list entirely?
2. If a button: what's its actual text and class? Is `Load More`
   capitalized that way?
3. If pagination links: what's the URL pattern? `?page=2`?
   `/service-bulletins/page/2/`? Either is easier to crawl than to drive
   in-browser.
4. Is the total bulletin count visible on the page (e.g. "Showing 1-20 of
   147") so we can sanity-check completeness post-scrape?

**Recommended fix (next round, after empirical check):**

- If pagination is link-based, drop `ScrollToLoadAllAsync` entirely and
  loop over numbered URLs in the C# scraper — much more robust than
  scroll-stability heuristics.
- If load-more-button-based, replace the scroll loop with a click loop
  that explicitly waits for the button to disappear or for the bulletin
  count to grow before clicking again.
- If infinite-scroll, keep the scroll loop but lengthen the
  stable-iteration threshold for the bulletin page and add a structured
  log line each iteration so silent failures are visible.
- Also: check the Network tab for a JSON endpoint. If sternpinball.com's
  bulletin Vue component fetches from something like `/wp-json/.../
  bulletins` we should hit that directly — orders of magnitude simpler
  and more reliable than DOM scraping. (This is the single highest-value
  DOM probe to do first.)

**Evidence:** unavailable in this session.

---

## Summary of recommended code changes (next round)

All of these are **hypotheses** until the empirical DOM pass is done.
None of them should be applied as code changes from this document alone.

- **`GamePageScraper.cs:300-331`** — Replace the six-selector tab-clicker
  cascade with a single confirmed selector (likely a `getByRole('tab',
  { name: ... })` once Playwright is upgraded to 1.49+, or a tight CSS
  selector keyed off the tab-nav container's confirmed class). Drop the
  `li:has-text()` fallback. Replace the post-click `WaitForTimeoutAsync`
  with an explicit wait for the active-tab indicator (`aria-selected=
  "true"` or equivalent). Distinguish "tab absent" from "tab click
  failed" in logs.

- **`GamePageScraper.cs:163-226`** — Replace the speculative
  `[class*="edition|model|version"]` container selector with the
  confirmed edition-card class. Drop the regex-on-headings fallback.
  Extend `EditionInfo` with `ImageUrl` and `Features` once the empirical
  pass shows where those live. Normalize MSRP while preserving the raw
  string for provenance.

- **`ServiceBulletinScraper.cs:146-185`** — After confirming the actual
  list structure (table vs. cards vs. list), replace the heterogeneous
  container selector list with a single confirmed selector. Most
  importantly: **promote bulletin date and related-game-name from
  free-text in `DiscoveryContext` to typed fields on the bulletin model**
  (this is already on the open-bugs list in `CLAUDE.md`). Capture the
  bulletin number too if it exists in the DOM.

- **`ServiceBulletinScraper.cs:77-144`** — First, check whether a JSON
  endpoint backs the page; if so, scrape it directly and delete the
  scroll/load-more logic entirely. Otherwise, replace the
  scroll-stability heuristic with a load-more-button click loop or
  numbered-page URL loop, depending on which mechanism the page actually
  uses.

- **`GamePageScraper.cs:131-133`** — Title extraction uses
  `h1, .game-title, [class*='title']`. Same speculative-selector
  pattern; tighten once the real `<h1>` class is confirmed.

- **Cross-cutting** — Once Playwright is upgraded from 1.12.0 to 1.49+
  (already on the project's todo list), prefer `Page.GetByRole`,
  `GetByText`, and `GetByLabel` locators over CSS string-builders. They
  are auto-waiting and much harder to break with template tweaks on
  Stern's side.
