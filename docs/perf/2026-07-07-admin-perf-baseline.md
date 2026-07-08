# Admin Surface — Performance Baseline (2026-07-07)

**Status:** In progress — Part 1 (shared bundle) complete; Part 2 (per-page) capture pending
**Branch:** `docs/admin-perf-baseline`
**Origin:** Follow-up to the "do we need combine/minify?" question raised against the live admin.

---

## 0. Why this exists

A question was raised whether the site lacks an asset **combine/minify** step and, if so, what
adding one would buy. This doc establishes a hard baseline before any optimization work, so any
change can be measured against a real "before," and records the honest answer to the combine/minify
question.

The finding up front: the app is a **.NET 10 Blazor Web App** using **`MapStaticAssets()`**
([`Program.cs:403`](../../src/PinballWizard.Web/Program.cs)), which already provides at build/publish
time most of what a classic combine/minify pipeline was invented to deliver — **Brotli + Gzip
precompression** and **content-hash ETags**. MudBlazor ships pre-minified; component-scoped CSS is
already bundled by the framework into one file. Only the two hand-authored files (`app.css`,
`app.js`) are un-minified.

---

## 1. Shared bundle analysis (bundle-level — identical on every page)

Every admin (and public) page ships the **same** static CSS/JS chrome. This is therefore measured
**once**, not per page.

### 1.1 Method

- `dotnet publish src/PinballWizard.Web -c Release` — the real publish path. Confirmed it emits
  **59 `.br` + 59 `.gz`** precompressed variants, i.e. compression genuinely ships.
- Raw sizes read from the publish `wwwroot`.
- Compressed sizes computed with `brotli -q 11` and `gzip -9` on the published assets (representative
  of the static-precompression approach `MapStaticAssets` uses; the shipped `.br` variants are
  content-hash-named so are not matched 1:1 here — the figures are the compression basis, not a
  claim of byte-identical output).
- Minified sizes: `terser -c -m` (JS) and `clean-css-cli` (CSS).

### 1.2 What ships today (per cold page load, all pages)

| Asset (shared) | raw (B) | gzip-9 (B) | brotli-11 (B) | Minified already? |
|---|--:|--:|--:|---|
| `_content/MudBlazor/MudBlazor.min.css` | 607,935 | 65,221 | 41,380 | ✅ framework |
| `_framework/blazor.web.js` | 200,466 | 54,913 | 47,553 | ✅ framework |
| `_content/MudBlazor/MudBlazor.min.js` | 65,516 | 16,247 | 14,259 | ✅ framework |
| `PinballWizard.Web.styles.css` (scoped bundle) | 64,071 | 13,805 | 11,744 | ✅ framework-bundled |
| **`app.css`** | 28,888 | 7,154 | 5,915 | ❌ **no** |
| **`app.js`** | 8,126 | 2,705 | 2,227 | ❌ **no** |
| **TOTAL** | **975,002** | — | **123,078** | |

Compression alone already takes the CSS/JS chrome from ~952 KB raw to **~120 KB on the wire** (an
87% reduction) — before any minify/combine work.

### 1.3 What minification would add

Minifying only the two un-minified files:

| File | brotli today | brotli minified | wire saving |
|---|--:|--:|--:|
| `app.css` | 5,915 | 2,730 | **−3,185 B** |
| `app.js` | 2,227 | 609 | **−1,618 B** |
| **Total** | 8,142 | 3,339 | **−4,803 B (~4.7 KB)** |

That ~4.7 KB is **~3.9 % of the 120 KB shared CSS/JS bundle**. Real, but modest — and the bundle is
dominated by MudBlazor CSS (41 KB br) + `blazor.web.js` (48 KB br), which are framework-inherent,
already minified, and out of scope.

### 1.4 Verdict on combine / minify

| Option | Verdict | Rationale |
|---|---|---|
| **Minify `app.css` / `app.js`** | ⚠️ Small, clean win — do it *only* via a proper build step | ~4.7 KB brotli (~3.9 % of chrome). Worth it if implemented as a build-time task or the framework asset pipeline — **not** a bolt-on bundler that adds drift (showcase-repo bar). |
| **Combine files** | ❌ Skip | Edge is HTTP/2 (Cloudflare + ACA); request multiplexing makes concatenation a wash. Would also fight the framework asset pipeline. |
| **Fingerprinted `immutable` caching** | 🔎 Likely the bigger lever — confirm via live audit | Assets are referenced as plain `href="app.css"` ([`App.razor:8-12,28-30`](../../src/PinballWizard.Web/Components/App.razor)), **not** the fingerprinting `@Assets["app.css"]` helper. That helper yields `max-age=…, immutable` + content-hash filenames, eliminating repeat-visit revalidation. Exact current `Cache-Control` to be confirmed by the live Lighthouse "efficient cache policy" audit (§2.3). |

---

## 2. Per-page baseline (page-level — DOM / render / data)

Because the bundle is shared, per-page differences are **not** in CSS/JS bytes — they are in DOM
size, data payload (e.g. `/admin/machines` renders a large grid; the dashboard renders a few cards),
request count, and hydration/TBT on interactive pages. This section is the general "before" snapshot
for the whole admin surface (and directly supports the admin-consistency/delight pass, whose PRs 1/2/4
change rendering).

### 2.1 Capture method

Per-page metrics are captured against the **local live-stack** topology (`tools/e2e/Run-E2E.ps1` /
`LiveStackFixture`): the real Api + Web run locally against **live Azure** Cosmos/AI Search, driven by
Playwright Chromium. In that topology there is **no Cloudflare gate and no Entra login** (Web runs in
Development → permissive auth), so all 16 admin routes are reachable.

> **Honesty caveat:** local `dotnet run` (Development) does **not** run publish-time Brotli/Gzip
> precompression, so *transfer bytes* captured locally are **uncompressed** and would overstate the
> minify opportunity. Transfer-size conclusions therefore come from §1 (published + compressed), not
> from this section. The page-level metrics below (DOM nodes, timings, request count) are driven by
> rendering + real data and are representative.

### 2.2 Routes (16)

Metrics: DOMContentLoaded (ms), Load (ms), First Contentful Paint (ms), Largest Contentful Paint
(ms), DOM node count, request count. `— pending —` until the capture run.

| # | Route | Kind | DCL | Load | FCP | LCP | DOM nodes | Requests |
|---|---|---|--:|--:|--:|--:|--:|--:|
| 1 | `/admin` (Dashboard) | list | — | — | — | — | — | — |
| 2 | `/admin/sources` | list | — | — | — | — | — | — |
| 3 | `/admin/manufacturers` | list | — | — | — | — | — | — |
| 4 | `/admin/machines` | list (heavy grid) | — | — | — | — | — | — |
| 5 | `/admin/documents` | list | — | — | — | — | — | — |
| 6 | `/admin/document-triage` | list | — | — | — | — | — | — |
| 7 | `/admin/link-overrides` | list | — | — | — | — | — | — |
| 8 | `/admin/jobs` | list (authz) | — | — | — | — | — | — |
| 9 | `/admin/monitoring` | list | — | — | — | — | — | — |
| 10 | `/admin/settings` | tabs | — | — | — | — | — | — |
| 11 | `/admin/corpus` | list | — | — | — | — | — | — |
| 12 | `/admin/sources/{id}` | detail | — | — | — | — | — | — |
| 13 | `/admin/machines/{opdbId}` | detail | — | — | — | — | — | — |
| 14 | `/admin/documents/{documentId}` | detail | — | — | — | — | — | — |
| 15 | `/admin/jobs/{jobName}` | detail | — | — | — | — | — | — |
| 16 | `/admin/jobs/{jobName}/executions/{executionName}` | detail | — | — | — | — | — | — |

### 2.3 Live edge Lighthouse (production representative — one run)

A single Lighthouse run against **live pinwiz.ai** (in an authenticated browser, past the Cloudflare
OTP gate) captures the production edge picture the local stack cannot: real Brotli at the edge, and
the **"Serve static assets with an efficient cache policy"** audit that confirms/denies the
fingerprint gap in §1.4. To be recorded:

- FCP / LCP / TBT / Speed Index / Performance score — `— pending —`
- Minify CSS / Minify JavaScript opportunities — `— pending —`
- Enable text compression (expect: pass) — `— pending —`
- Efficient cache policy for static assets — `— pending —`

---

## 3. How to re-run (repeatability)

- **§1 bundle:** `dotnet publish src/PinballWizard.Web -c Release -o <out>`, then
  `brotli -q 11` / `gzip -9` the assets in `<out>/wwwroot`; `terser` / `clean-css-cli` for minified
  comparison.
- **§2 per-page:** local live-stack via `tools/e2e/Run-E2E.ps1` machinery + a Playwright pass that
  records `performance.getEntriesByType('navigation'|'resource')` + `document.getElementsByTagName('*').length`
  per route.
- **§2.3 live:** Lighthouse (Navigation, Desktop, Performance, Clear-storage) against pinwiz.ai in an
  authenticated browser.

---

## 4. Recommendations (pending per-page + live data)

1. **Prioritize the fingerprint/`immutable` caching change over combine/minify** if §2.3 confirms a
   weak cache policy — it's framework-native (`@Assets[]` / ImportMap), helps repeat visits most, and
   avoids a bundler.
2. **Minify `app.css`/`app.js`** as a small clean win, via a build-time step, not a bolt-on bundler.
3. **Do not combine** — no benefit under HTTP/2.
4. MudBlazor CSS + `blazor.web.js` dominate the bundle and are framework-inherent; no action.
