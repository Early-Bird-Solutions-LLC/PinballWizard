# 0003 — Playwright (.NET) over Puppeteer-Sharp for Vue.js scraping

**Status:** Accepted
**Date:** 2026-05-02 (codifies a decision implemented earlier in the project)

## Context

`sternpinball.com` serves two kinds of pages:

- **Static HTML** (the `/manuals/` listing) — directly parseable with
  AngleSharp from the response body of a single `HttpClient` request.
- **Vue.js single-page applications** (`/game/{slug}/` and
  `/support/service-bulletins/`) — the meaningful content (game tabs,
  bulletin lists with scroll-to-load) is rendered client-side after
  JavaScript executes, so an `HttpClient` request returns a near-empty
  shell.

We need a real browser engine to scrape the Vue.js pages. Two viable
options for a .NET project:

- [Playwright (.NET)](https://playwright.dev/dotnet/) — Microsoft's
  cross-browser automation library, official .NET bindings.
- [Puppeteer-Sharp](https://www.puppeteersharp.com/) — community .NET
  port of Google's Puppeteer; Chromium-only.

## Decision

We use **Playwright (.NET)** for all browser-driven scraping.

Rationale:

- **First-party support.** Playwright is maintained by Microsoft with
  official .NET bindings, version-aligned with the upstream
  Playwright project. Puppeteer-Sharp is a community port that has
  historically lagged behind upstream Puppeteer.
- **Multi-browser.** Playwright supports Chromium, Firefox, and WebKit
  out of the box. We use Chromium today; if Stern's site ever requires
  WebKit-specific behavior we can switch the launcher without
  rewriting test fixtures.
- **Better wait primitives.** `WaitForSelectorAsync`,
  `WaitForLoadStateAsync`, and `WaitForFunctionAsync` give deterministic
  signals to wait on; Puppeteer's older API encouraged
  fixed-millisecond timeouts, which we want to avoid for politeness
  reasons.
- **Active development.** Playwright ships releases multiple times per
  month with features and CDP improvements. Puppeteer-Sharp's release
  cadence is slower and lags upstream.
- **Better debugging story.** `PWDEBUG=1` opens an inspector;
  `--headed --slowmo` is straightforward; tracing and video capture
  built in.

## Consequences

**Positive:**
- One browser-automation library across all current and likely-future
  scrapers. New manufacturer sites don't require a second tool.
- The `PoliteScraper` base class (planned, see project memory
  `project_parallel_execution_plan.md` Gate 2) can encode browser
  lifecycle once and share it across all manufacturer scrapers.
- Playwright's tracing makes politeness violations (extra waits,
  duplicate page loads) visible during development.

**Negative:**
- Playwright's Chromium binary is large (~150 MB unpacked). The Docker
  image must either include it (current approach via Playwright's own
  Docker base image) or install it on first run.
- The project is currently pinned to Playwright 1.12.0 (4-year-stale)
  because of a records-vs-classes deserialization issue that
  Activator.CreateInstance hit. The fix to enable upgrading to 1.49+
  is in flight (`LinkRaw` etc. converted from positional records to
  classes with init-able properties). The choice of Playwright is not
  affected by this; the version pin is.

## Alternatives considered

- **Puppeteer-Sharp.** Rejected for the reasons above — community port,
  slower release cadence, single browser, weaker wait API.
- **Selenium.** Rejected — heavier-weight, the WebDriver protocol adds
  latency and a separate driver process, and the developer experience
  is worse for the kind of one-off scraping work this project does.
- **Pure HTTP scraping with manual JS execution / Headless Chrome
  via CDP.** Rejected — would amount to reinventing what Playwright
  already provides.
- **Rendering Vue.js server-side via a scraping service.** Rejected —
  out of scope for a hobby project that does not need horizontal
  scaling.
