# Phase 5 Wave 1 Drift Audit — `Dev-WebUiBrainstormResume`

> **Point-in-time artifact (2026-05-09).** This document reflects the state of the codebase as of that date; see [build-spec.md](build-spec.md) and [guardrails.md](guardrails.md) for current authoritative guidance.
>
> **Status:** Audit produced 2026-05-09 on `Dev-WebUiBrainstormResume` (off `5c4366b`). Item 3 of the brainstorm-resume queue. Compares the shipped Phase 5 Wave 1 chrome scaffold (`src/PinballWizard.Web/`) against the locked design system (`docs/ui/themes/modern-lcd.md`, `docs/ui/screens/*.md`, [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md)) to surface drift before Wave 2 components calcify against incorrect defaults.
>
> **Authority:** the design system + ADRs are the source of truth. This audit's findings are change requests against the shipped code, not against the spec.
>
> **Scope:** Wave 1 chrome (the five files under `src/PinballWizard.Web/Components/` shipped in PR-F0/F1/F2). Out of scope: Wave 2 delight surfaces (`WizardAnswerStream`, `RefusalPanel`, `CitationStrip` family, `TiltPage`) — they haven't shipped, so they can't drift; the screen-spec-vs-implementation check happens when each lands.

## Verdict-tag legend

- ✅ **No drift** — shipped matches spec (or is a deliberate, in-spec choice).
- ⚠️ **Minor drift** — fixable in a single PR; non-blocking for Wave 2 if scheduled before user-facing polish.
- 🔴 **Major drift** — must fix before Wave 2 components lock against the wrong tokens / wrong patterns. Blocking.
- ⏳ **Not-yet-shipped** — surface or feature is Wave 2/3 work and absent by design. Tracked here so it isn't forgotten, not flagged as drift.

## Findings by component

### 1. `PinballTheme.cs` — visual tokens

Spec authority: [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md) § Visual system (palette, type), [`docs/ui/screens/answer-with-citations.md`](ui/screens/answer-with-citations.md) § Locked visual tokens.

| Token / surface | Spec value | Shipped value | Verdict | Fix |
|---|---|---|---|---|
| `--bg-base` (page background) | `#0c0b0e` (warm near-black) | `#121212` (Material neutral dark) | 🔴 | Set `PaletteLight.Background = "#0c0b0e"`. The neutral-grey shipped value reads colder than the LCD-bezel intent. |
| `--bg-surface` (panel interiors) | `#161519` | `#1E1E1E` | 🔴 | Set `Surface = "#161519"`. |
| `--bg-surface-hi` (hover/focus panels) | `#1f1d22` | (not present in MudPalette) | 🔴 | Add as a custom CSS variable in `app.css`; MudPalette doesn't model surface elevation explicitly. Per [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 6, custom CSS variables for delight tokens are permitted (chrome stays MudBlazor-strict; tokens that the chrome doesn't have a slot for live as `:root` custom properties). |
| `--text-primary` | `#f4f1ea` (warm off-white) | `#F0F0F0` | 🔴 | Set `TextPrimary = "#f4f1ea"`. Clinical-grey reads "medical app" per the spec rationale. |
| `--text-secondary` | `#9a9590` | `#BDBDBD` | 🔴 | Set `TextSecondary = "#9a9590"`. Same warmth-vs-neutral drift. |
| `--accent-primary` (amber) | `#ff9a1f` | `#F5A623` | ⚠️ | Set `Primary = "#ff9a1f"`. Both read as arcade amber; spec value is locked against JJP-game reference, shipped is Material's `amber 700`. Close-but-not-spec — fix when convenient, doesn't block Wave 2. |
| `--accent-grounded` (citations) | `#34d96a` (atomic green / GI-glow) | (Material `Success = #4CAF50`) | 🔴 | Set `Success = "#34d96a"`. The spec is explicit: cyan was tempting but reads as "tech," not pinball. Material's success-green re-introduces the wrong register. |
| `--accent-refusal` | `#ff3b30` (saturated red) | (Material `Error = #F44336`) | 🔴 | Set `Error = "#ff3b30"`. Same drift direction. |
| `--accent-mode` (magenta) | `#e13bd9` | (no token exists) | 🔴 | MudPalette has no slot for "mode/topic" — add as a `:root` CSS custom property `--pw-accent-mode` in `app.css`. The Wave 2 `WizardAnswerStream` and `RefusalPanel` will need it for the left-flipper "view in answer" CTA per `answer-with-citations.md` § Element-specific behaviors. |
| `--border-quiet` | `#2a282d` | `#2A2A2A` (close, neutral) | ⚠️ | Set `Divider = "#2a282d"`. Within ~1 hue of the spec; cosmetic. |
| `--border-glow-*` (alpha-60 over base) | `#ff9a1f99` / `#34d96a99` / `#e13bd999` / `#ff3b3099` | (no tokens exist) | 🔴 | Add as `:root` custom properties in `app.css`. Consumed by hover/focus glow pulses per the motion vocabulary. |
| **Display font** | Barlow Condensed (700 primary / 500 secondary) | Roboto (default) | 🔴 | Add Barlow Condensed via Google Fonts `<link>` in `App.razor` head; set `Typography.H1..H6.FontFamily` to `["Barlow Condensed", "Roboto", "sans-serif"]`. Spec's spike resolved this against Saira / Oswald / Anton — none of that decision is in production yet. |
| **Body font** | Inter | Roboto | ⚠️ | Add Inter via Google Fonts; set `Typography.Default.FontFamily` to `["Inter", "Roboto", "sans-serif"]`. Roboto is acceptable but not spec. |
| **Mono font** | JetBrains Mono | (none specified) | 🔴 | Add JetBrains Mono via Google Fonts; declare `--pw-font-mono` in `app.css` and apply to citation IDs / machine slugs / URLs in provenance trails. Wave 2 `CitationCard` renders `mch_a1b2c3d4...`-style IDs that need mono. |
| **Tabular figures** site-wide | `font-feature-settings: "tnum"` on all numerics | (not configured) | ⚠️ | Add `font-feature-settings: "tnum";` on `:root` in `app.css`. The Wizard renders dates, prices, citation indices, percentages — all should be tabular. |
| **`prefers-reduced-motion: reduce`** override | All `--motion-*` durations override to `0ms` | (not configured) | 🔴 | Add the media-query block in `app.css`. Required by spec (motion-reduced fallback) and by accessibility posture. |
| **Force `IsDarkMode="true"`** | Dark default; theme switching for siblings (Daytime Route is light) | Forced via attribute on `MudThemeProvider` | ⚠️ | Replace with a theme-state service backed by localStorage (Wave 3 obligation when the theme picker / Settings screen ships). For Wave 1 the forced attribute is acceptable; mark as a Wave 3 obligation, not a current-PR fix. |

**Summary:** **9 🔴 / 4 ⚠️ / 0 ✅** in tokens. The shipped theme is *spirit-aligned* (warm amber + dark + recessed-edge cabinet vibe) but *value-unaligned*. Most of these are one-line MudPalette assignments + a single `app.css` block of `:root` custom properties. Recommended single-PR fix: **PR-T1 "Modern LCD spec → MudTheme + app.css token alignment"**.

### 2. `MainLayout.razor` — chrome composition

Spec authority: [`docs/ui/screens/answer-with-citations.md`](ui/screens/answer-with-citations.md) § Screen zones (inherited by `empty-landing.md`, `what-we-cover.md`, `machine-detail.md`). [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 6 (MudBlazor strict + custom-for-delight).

| Surface | Spec | Shipped | Verdict | Fix |
|---|---|---|---|---|
| Outer wrapper | `MudThemeProvider` + `MudLayout` | `MudThemeProvider` + `MudLayout` | ✅ | — |
| Top bar | `MudAppBar Elevation="1"` hosting brand mark + "What we cover" link | `MudAppBar Elevation="1"` hosting `BrandHeader` (which has 4 nav links — see § 3) | ⚠️ at this layer | The `MainLayout` choice to host `BrandHeader` is correct; the drift is *inside* `BrandHeader` (§ 3). |
| Header height | ~56px desktop / ~48px mobile | MudAppBar default (~64px desktop / ~56px mobile) | ⚠️ | MudAppBar's default is ~8px taller than spec. Add `Class="pw-appbar-tight"` and a CSS rule in `app.css` to override `min-height`. Cosmetic; defer to PR-T1 or PR-F-chrome-polish. |
| Persistent question input directly under header | Required (per `answer-with-citations.md` § Screen zones #2) | Absent | ⏳ | Question input is Wave 2 (`WizardAnswerStream` mount). Not Wave-1 drift. |
| Footer zone | Required (per § Screen zones #5: coverage summary + GitHub link + "What we cover" link) | Absent | 🔴 | The footer is part of every screen per the spec. Add a `BrandFooter.razor` chrome wrapper (MudBlazor strict — `MudText` + `MudLink`); mount inside `MainLayout` after `MudMainContent`. Doesn't need to wait for Wave 2 — the footer copy is locked from `empty-landing.md` § Section 4 ("The Wizard has first-party data on 8 active manufacturers and OPDB. Everything else routes to community resources. [What we cover →]") |
| `TiltErrorBoundary` wrap of `@Body` | Required (per [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 6 & § 9 — render-time exception fallback) | Wraps `@Body` inside `MudMainContent` | ✅ | — |
| `MudPopoverProvider` / `MudDialogProvider` / `MudSnackbarProvider` | Required by MudBlazor for dialogs / snackbars / popovers | All three present | ✅ | — |
| Forced `IsDarkMode="true"` | Dark default; theme-state-service for siblings later | Forced via attribute | ⚠️ | See § 1 — Wave 3 obligation, not Wave 1 drift. |

**Summary:** **3 ✅ / 3 ⚠️ / 1 🔴 / 1 ⏳.** The 🔴 (missing footer) is a clean separate PR — **PR-F-chrome-footer "BrandFooter chrome wrapper"** — that doesn't need to be entangled with the token-alignment PR.

### 3. `BrandHeader.razor` — header content

Spec authority: [`docs/ui/screens/answer-with-citations.md`](ui/screens/answer-with-citations.md) § Screen zones #1 ("Brand mark on the left, *'What we cover'* link on the right"). [`docs/ui/screens/empty-landing.md`](ui/screens/empty-landing.md) Section 1 (wordmark = `PINBALLWIZARD` all-caps display in hero, full-readable casing in header). [ADR-0027](adr/0027-community-resource-posture.md) § 1 (no engagement-metric framing, no captive nav).

| Surface | Spec | Shipped | Verdict | Fix |
|---|---|---|---|---|
| Brand mark (LEFT) | "Brand mark" — text wordmark in display font, full-readable casing in header (per `empty-landing.md` § Section 1: "full readable casing in the header brand mark") | `&#x25CF; PinballWizard` (unicode dot + plain text, MudText `Typo.h6`, default font) | ⚠️ | Replace with `<MudText Class="brand-mark" Typo="Typo.h6">PinballWizard</MudText>` and a CSS rule using `var(--pw-font-display)` (Barlow Condensed) once PR-T1 lands. The unicode dot is acceptable as a placeholder per the in-file comment; flag for refresh once a brand-mark asset arrives. |
| Right side: "What we cover" link only | Subtle, low-key — never the focal point (per § Screen zones) | 4 nav links: Home / Wizard / About / Status | 🔴 | **The 4-nav-link approach reads as a generic SaaS site, not a community resource.** "Home" + "Wizard" is internally redundant (the Wizard *is* the home / `/`). "Status" is a status-page link that doesn't belong in the prospect-facing chrome (per [ADR-0027](adr/0027-community-resource-posture.md) § 1: no captive UI; status visibility belongs on the answer surface or `/status` accessed via footer). The spec's chrome is intentionally minimal — *one* link to "What we cover" so the coverage-transparency posture surfaces immediately. **Fix: replace the 4-button nav with a single `MudButton Href="/about" ...>What we cover</MudButton>` (anchored right via `MudSpacer`). Move "Status" to the footer (a small `<a>` element), drop "Home" entirely (the brand mark on the left is the home link), and "Wizard" is also the home (`/` and `/wizard` will resolve to the same surface). |
| Routing (`Home`, `/`, `/wizard` semantics) | `/` is the empty/landing screen; `/wizard` is the same surface or a deep-link variant | `Home` → `/` and `Wizard` → `/wizard` are listed as separate destinations | 🔴 | Per [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 1 routing inventory: `/` = landing; `/wizard` = primary Wizard surface; `/wizard/q/{slug}` = deep-link. The header doesn't need to expose both — choose one (recommended `/wizard` as the canonical Wizard route, with `/` redirecting to it OR `/` rendering the same component with no question loaded). The brand-mark click should go to `/`. |
| Auth/admin links | None in public bar (admin lives in `AdminLayout` per the spec comment) | None | ✅ | — |
| Engagement-metric framing | Forbidden | None present | ✅ | — |

**Summary:** **2 ✅ / 1 ⚠️ / 2 🔴.** The two 🔴 items are tightly coupled (both about the nav-link set). Recommended fix: **PR-F-chrome-nav-rework "Replace 4-nav-link header with single 'What we cover' link"** — small, focused, ships in a couple of hours.

### 4. `WizardShell.razor` — content container

Spec authority: implicit in the screen specs (centered container, generous horizontal margins, max-width that comfortably accommodates the answer panel + citation card stack at desktop).

| Surface | Spec | Shipped | Verdict | Fix |
|---|---|---|---|---|
| Container width | Generous on desktop, comfortable on mobile (no specific px in spec) | `MudContainer MaxWidth="MaxWidth.Large"` (1280px) | ✅ | Reasonable for chrome. May want `MaxWidth.Medium` (960px) for a more LCD-bezel feel — flag for cosmetic review once tokens land. |
| Vertical padding | `--space-6` (48px) implied for the empty-state hero | `Class="py-6"` (MudBlazor `py-6` = 24px) | ⚠️ | MudBlazor's spacing scale (`py-6` = 24px) doesn't map 1:1 to the spec's `--space-6` (48px). Either: (a) use `Class="py-12"` (48px in MudBlazor) — but this collides with MudBlazor's idiomatic `py-6` for "generous padding," or (b) define `--pw-space-6: 48px` in `app.css` and use a custom Class. Cosmetic; defer to PR-T1. |
| `@layout MainLayout` | Required (chrome composition) | Present | ✅ | — |
| Class hook (`wizard-shell`) | n/a (spec is structural, not class-name-prescriptive) | `Class="wizard-shell py-6"` | ✅ | Class hook is fine; will be useful for component-targeted styles. |

**Summary:** **3 ✅ / 1 ⚠️ / 0 🔴.** Container is structurally correct; cosmetic padding-scale alignment lives with PR-T1.

### 5. `TiltErrorBoundary.razor` — render-time degradation

Spec authority: [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md) § Refusal that directs out (the per-category "TILT" / "BALL SAVED" / "MATCH AWARDED" callout register), [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 6 (delight surface) and § 9 (RFC 9457 + requestId surfacing).

| Surface | Spec | Shipped | Verdict | Fix |
|---|---|---|---|---|
| Render-time fallback | Required (replaces ASP.NET Core framework default) | Implemented | ✅ | — |
| TILT typography | "Display type, ALL CAPS, ~32px+ on desktop" (per refusal-panel category-label spec) | `MudText Typo="Typo.h4"` (~24px) with no display-font binding | ⚠️ | Once PR-T1 lands and `--pw-font-display` exists, switch to `Typo="Typo.h3"` (32px) and apply a class binding the display font. Currently TILT renders in default Roboto h4 — looks generic, not pinball-broadcast. |
| Body copy | One sentence, plain, no apology (per refusal-panel reason-text spec) | "Something rattled the machine. The rest of the wizard is still running." | ✅ | Voice is on-spec — no apology, declarative, gentle pinball metaphor. |
| `requestId` surfacing | Required (per [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 9) | `Activity.Current?.TraceId.ToString()` rendered in caption with `data-testid="tilt-request-id"` for test pinning | ✅ | — |
| Recovery affordance | "Reset and try again" — implicit in the boundary's `Recover()` method | `MudButton Variant="Variant.Text" OnClick="@Recover"` | ✅ | — |
| Card border treatment | Per spec: `accent-refusal` border on refusal panels | MudPaper with default elevation — no `accent-refusal` border treatment | ⚠️ | Add `Class="tilt-boundary"` (already present) and a CSS rule that draws `1px solid var(--pw-accent-refusal)` on the border once PR-T1 lands. Wave 1 ships without the spec's red border; doesn't read as wrong, but doesn't read as on-spec either. |
| Listed in ADR-0026 § 6 as one of four locked custom delight surfaces | Yes | Yes (per the in-file comment) | ✅ | — |

**Summary:** **5 ✅ / 2 ⚠️ / 0 🔴.** This is the tightest-aligned component in Wave 1 — voice and structure are on-spec; only the typography weight and the missing red border are deferred. Both fold cleanly into PR-T1 once tokens land.

## Aggregate verdict

| Component | ✅ | ⚠️ | 🔴 | ⏳ |
|---|---|---|---|---|
| `PinballTheme.cs` (tokens) | 0 | 4 | 9 | 0 |
| `MainLayout.razor` | 3 | 3 | 1 | 1 |
| `BrandHeader.razor` | 2 | 1 | 2 | 0 |
| `WizardShell.razor` | 3 | 1 | 0 | 0 |
| `TiltErrorBoundary.razor` | 5 | 2 | 0 | 0 |
| **Total** | **13** | **11** | **12** | **1** |

**Reading the score:** the chrome scaffold is structurally on-spec (component composition, ADR-0008 + ADR-0026 § 6 conformance, render-time fallback, MudBlazor-strict-with-one-permitted-custom-component). The drift concentrates in *visual tokens* (palette + fonts) and *the brand-header nav-link set*. Both are token-or-content drift — easier to fix than structural drift would have been.

The 🔴 finding count looks high (12) but **9 of them collapse into a single PR** (PR-T1 token alignment). The remaining 3 are split across two small PRs (header nav rework + chrome footer). Total fix surface is ~3 PRs to bring Wave 1 fully on-spec before Wave 2 ships.

## Fix-PR roadmap

Recommended sequencing — small, focused PRs that interleave well with the in-flight Wave 2 backend foundational track. Branch names follow the project convention (`Dev-` prefix, no ticket fallback):

### PR-T1 — Modern LCD spec → MudTheme + `app.css` token alignment

- **Branch:** `Dev-Phase5ModernLcdTokenAlignment`
- **Files touched:** `src/PinballWizard.Web/Components/Theming/PinballTheme.cs`, `src/PinballWizard.Web/wwwroot/app.css`, `src/PinballWizard.Web/Components/App.razor` (font `<link>`s), `tests/PinballWizard.Web.Tests/` (new `PinballThemeContractTests` pinning palette values to spec).
- **Closes:** all 9 🔴 + 4 ⚠️ token findings in § 1 + the typography drift in § 5.
- **Doesn't close:** the dark-mode-forced ⚠️ (Wave 3 obligation) and the cosmetic padding-scale drift in § 4 (folds in if convenient).
- **Test obligation:** `PinballThemeContractTests` — assert `Primary == "#ff9a1f"`, `Background == "#0c0b0e"`, etc. Pins the spec values mechanically so a future "let's tweak the palette" doesn't drift again.
- **Risk:** low. Pure visual change; no behavior change.

### PR-F-chrome-nav-rework — `BrandHeader` "What we cover" only

- **Branch:** `Dev-Phase5ChromeNavRework`
- **Files touched:** `src/PinballWizard.Web/Components/Theming/BrandHeader.razor` only.
- **Closes:** both 🔴 findings in § 3 (4-nav-link → single "What we cover"; remove `Home`/`Wizard`/`Status`).
- **Test obligation:** bUnit test asserting the rendered header has exactly one `<a>` to `/about` (the "What we cover" link) plus the brand mark linking to `/`. Mechanically prevents nav-link inflation regression.
- **Risk:** low. The `/` and `/wizard` routing question (do they resolve to the same surface or distinct ones?) needs a one-line decision in the PR description; that decision is already implied by [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) § 1 (`/` = landing, `/wizard` = primary surface).

### PR-F-chrome-footer — `BrandFooter` chrome wrapper

- **Branch:** `Dev-Phase5ChromeFooter`
- **Files touched:** new `src/PinballWizard.Web/Components/Layout/BrandFooter.razor`, edit `MainLayout.razor` to mount it after `MudMainContent`, `tests/PinballWizard.Web.Tests/Components/Layout/BrandFooterTests.cs`.
- **Closes:** the 🔴 missing-footer finding in § 2.
- **Copy locked from:** `docs/ui/screens/empty-landing.md` § Section 4. ("The Wizard has first-party data on 8 active manufacturers and OPDB. Everything else routes to community resources. [What we cover →]")
- **Test obligation:** bUnit test asserting the footer renders the coverage statement + the GitHub link + the "What we cover →" link. Per [ADR-0027](adr/0027-community-resource-posture.md) § 4 (coverage transparency is a first-class posture surface, not an afterthought).
- **Risk:** low. Net-additive chrome.

### Sequencing

PR-T1 first (the visual foundation; all later visual work depends on it). PR-F-chrome-nav-rework and PR-F-chrome-footer can land in any order (file-disjoint, low-conflict). All three should land **before** the first Wave 2 component (`WizardAnswerStream`, `RefusalPanel`, etc.) calcifies against the wrong tokens — the cost of fixing token drift after Wave 2 is much higher than fixing it now.

### Out of scope for the fix-PR roadmap

- **Theme-picker / Settings screen** (Wave 3 obligation) — the dark-mode-forced ⚠️ remains until then.
- **Brand-mark logo asset** (Wave 2/3 follow-up per the in-file comment) — the `&#x25CF; PinballWizard` placeholder stays.
- **Sibling theme prototypes** (Cabinet, Score Reel) — Item 4 of the brainstorm-resume queue; separate from the drift audit.
- **Wave 2 component drift** — components haven't shipped, so they can't drift. This audit re-runs against each Wave 2 component when it lands.

## Sign-off

Audit produced by the brainstorm-resume work on `Dev-WebUiBrainstormResume`. Findings are change requests against the shipped Wave 1 chrome — not against the spec. The screen specs and ADRs remain the source of truth.

When the fix-PR roadmap above lands, this audit should be **archived** (move to `docs/archive/phase5-drift-audit-2026-05-09.md` per the project's convention for time-bound artifacts) — its job is to catch the gap before Wave 2, not to live forever.

## Iteration log

| Date | Change | Rationale |
|---|---|---|
| 2026-05-09 | v1 — audit produced | Item 3 of the brainstorm-resume queue (`Dev-WebUiBrainstormResume`). Wave 1 chrome shipped via PR-F0/F1/F2 (#152 + sibs); the v2 brainstorm handoff flagged token drift but didn't audit other components. This audit closes that gap before Wave 2 begins. **Findings: 13 ✅ / 11 ⚠️ / 12 🔴 / 1 ⏳.** Most 🔴 collapse into PR-T1 (token alignment); chrome composition is structurally on-spec. |
