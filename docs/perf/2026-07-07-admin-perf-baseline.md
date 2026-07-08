# Admin Surface — Performance Baseline (2026-07-07)

**Status:** Part 1 (shared bundle) + Part 2 (per-page, 13/16 routes) captured; only the single live-edge Lighthouse run (§2.3) is outstanding
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

Per-page metrics are captured against the **local live-stack** topology
(`LiveStackFixture`): the real Api + Web run locally against **live Azure** Cosmos/AI Search, driven
by Playwright Chromium. In that topology there is **no Cloudflare gate and no Entra login** (Web runs
in Development → permissive auth), so all admin routes are reachable. The capture is codified as an
on-demand test (`tests/PinballWizard.Web.Tests/E2E/AdminPerfBaselineCaptureE2E.cs`,
`Category=E2E` → excluded from CI); re-run per §3.

Captured 2026-07-07 against `main` @ `30c55c1`. Detail-page ids are **derived at runtime** from the
first row link of the parent list (no hardcoded ids): `sources/stern`,
`documents/twip_what-are-pinball-legends`.

> **Honesty caveats:**
> - Local `dotnet run` (Development) does **not** run publish-time Brotli/Gzip precompression, so the
>   *Transfer* column is **uncompressed** and is **not** a basis for any size conclusion — those come
>   from §1 (published + compressed). It is kept only as a rough per-page relative indicator.
> - The **`/admin` row absorbs cold-start** (first route navigated → JIT + first Cosmos connection
>   warmup). Its 3.1 s is a first-hit artifact, not steady-state; the other rows reflect a warm host.
> - LCP is not captured here (needs a pre-load observer); it is covered for the representative page by
>   the live Lighthouse run (§2.3).

### 2.2 Results (captured)

| Route | Kind | DCL (ms) | Load (ms) | FCP (ms) | DOM nodes | Requests | Transfer\* (KB) |
|---|---|--:|--:|--:|--:|--:|--:|
| `/admin` (Dashboard) | dashboard | 3126† | 3126† | 1204† | 237 | 13 | 395.3 |
| `/admin/sources` | list | 426 | 426 | 412 | 466 | 12 | 374.3 |
| `/admin/manufacturers` | list | 878 | 878 | 868 | 288 | 12 | 374.3 |
| `/admin/machines` | list (heavy grid) | 87 | 87 | 84 | 461 | 12 | 374.3 |
| `/admin/documents` | list | 736 | 736 | 732 | 436 | 12 | 374.3 |
| `/admin/document-triage` | list | 95 | 95 | 84 | 481 | 12 | 374.3 |
| `/admin/link-overrides` | list | 55 | 74 | 84 | 306 | 12 | 374.3 |
| `/admin/jobs` | list (authz) | 58 | 58 | 44 | 140 | 12 | 374.3 |
| `/admin/monitoring` | list | 142 | 142 | 116 | 287 | 15 | 440.9 |
| `/admin/settings` | tabs | 58 | 64 | 60 | 182 | 12 | 374.3 |
| `/admin/corpus` | list | 936 | 936 | 68 | 207 | 12 | 374.3 |
| `/admin/sources/stern` | detail | 40 | 51 | 56 | 123 | 11 | 353.1 |
| `/admin/documents/twip_…legends` | detail | 213 | 214 | 200 | 163 | 13 | 394.2 |
| `/admin/machines/{opdbId}` | detail | — | — | — | — | — | **skipped** |
| `/admin/jobs/{jobName}` | detail | — | — | — | — | — | not sampled‡ |
| `/admin/jobs/{jobName}/executions/{…}` | detail | — | — | — | — | — | not sampled‡ |

\* Uncompressed (local Development) — relative indicator only, not a size conclusion (see §2.1).
† First route navigated — includes host cold-start; not steady-state.
‡ Job detail/execution ids are not derivable from a static list link; deferred (low value — same
shared bundle, small DOM).

### 2.3 Observations

- **Request count and shared-bundle transfer are flat across pages** (~12 requests, ~374 KB
  uncompressed) — confirming §1's point that the static bundle is identical everywhere; per-page cost
  is DOM + data + render, not assets.
- **The DOM stays small everywhere** (123–481 nodes). The "heavy" `/admin/machines` grid is **461
  nodes / 87 ms** — `AppDataGrid`'s `RowsPerPage=25` caps the rendered DOM regardless of catalog size
  (30k+ machines), so there is no large-list DOM problem to solve.
- **Warm render is sub-second** on every page except the cold-start dashboard row. `/admin/manufacturers`
  (868 ms FCP), `/admin/documents` (732 ms), and `/admin/corpus` (936 ms DCL) are the slowest warm
  pages — each does a live Cosmos/AI-Search aggregation on load; a natural place to look next if page
  latency (not asset weight) becomes the concern.
- **`/admin/machines` grid rows are not `<a href>`** — they navigate via row-click JS, so the
  detail-id derivation found no link (row marked *skipped*). Not a defect; noted for future capture
  tooling (derive machine ids from the API instead).

### 2.3 Live edge Lighthouse (production representative — one run)

A single Lighthouse run against **live pinwiz.ai** (in an authenticated browser, past the Cloudflare
OTP gate) captures the production edge picture the local stack cannot: real Brotli at the edge, and
the **"Serve static assets with an efficient cache policy"** audit that confirms/denies the
fingerprint gap in §1.4.

**Captured 2026-07-07, `pinwiz.ai/wizard` (Navigation, Desktop, Performance):**

| Metric | Value | Verdict |
|---|---|---|
| Performance score | **90** | 🟢 good |
| First Contentful Paint | 0.5 s | 🟢 |
| Largest Contentful Paint | 0.6 s | 🟢 |
| Total Blocking Time | 0 ms | 🟢 |
| Cumulative Layout Shift | **0.169** | 🟠 needs improvement (> 0.1) |

The loading metrics are excellent — **confirming asset weight is not the problem** (sub-second
FCP/LCP, zero blocking). The single blemish is **CLS 0.169**: content shifts as the page settles.
That is page behavior (layout not reserved / font swap / async content arriving), **not** asset size,
and is a more material UX win than the ~4.7 KB minify. The wizard page is representative for the
shared-bundle audits below; CLS itself is page-specific.

Still to record (scroll to the Diagnostics / Insights section of the same report):

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

## 4. Recommendations

Per-page data (§2) shows **no DOM or rendering hotspot** — the DOM is small everywhere, the grid is
paginated, and asset weight is flat across pages. So the (modest) opportunity is in **asset delivery**,
not page rendering. In priority order:

1. **Prioritize the fingerprint/`immutable` caching change over combine/minify** if §2.3 confirms a
   weak cache policy — it's framework-native (`@Assets[]` / ImportMap), helps repeat visits most, and
   avoids a bundler.
2. **Minify `app.css`/`app.js`** as a small clean win (~4.7 KB brotli, §1.3), via a build-time step,
   not a bolt-on bundler.
3. **Do not combine** — no benefit under HTTP/2.
4. MudBlazor CSS + `blazor.web.js` dominate the bundle and are framework-inherent; no action.
5. **Separately from asset work:** the only page-latency signals are the dashboard cold-start and a
   few live-aggregation pages (`/admin/manufacturers`, `/admin/documents`, `/admin/corpus`, all warm
   sub-second). Not an asset problem — track as a distinct backend/latency item only if it regresses.

**Net answer to the original question:** the app already has the substance of "combine & minify"
(Brotli/Gzip precompression, pre-minified MudBlazor, framework-bundled scoped CSS). Adding a classic
combine/minify pass buys ~4.7 KB brotli — real but small — while the higher-leverage, framework-native
move is fingerprinted `immutable` caching.
