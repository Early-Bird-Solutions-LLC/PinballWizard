---
name: frontend-blazor
id-prefix: FE
status: active
applies-to:
  - "src/PinballWizard.Web/**"
  - "src/PinballWizard.Web.Client/**"
  - "**/*.razor"
  - "docs/ui/design-system/**"
  - "docs/ui/themes/**"
---

# Frontend-Blazor Standard

Blazor render-mode discipline, MudBlazor-strict component posture, SSE streaming
contract, graceful degradation, and audio mute-by-default for the PinballWizard
public and admin surfaces.

**RULE FE-01** (render-mode-correctness)
WHEN:   adding or modifying a routable Blazor page (.razor with `@page`)
THEN:   any page carrying a genuine interactivity signal (`@onclick`/`OnClick=`/`RowClick=`/`@bind-Value`/`IDialogService`/`<MudDialog`) MUST declare `@rendermode InteractiveServer`; error/degraded surfaces (Error.razor, TiltErrorBoundary) stay static and use real `Href` anchors, never circuit-dependent `OnClick`
NEVER:  ship a routable page whose interactive controls are silently dead because `@rendermode` is absent; use a bare `OnClick=` navigation button (not an Href anchor) on a static-render page
CHECK:  dotnet test --filter "FullyQualifiedName~RenderModeConventionTests" --no-build
SEV:    🔴
REF:    INVARIANTS#14 · ADR-0034 · RenderModeConventionTests · LayoutProviderRenderModeTests

**RULE FE-02** (mudblazor-providers-pinned-interactive)
WHEN:   adding or modifying a layout that serves at least one interactive page (i.e. a layout whose child pages carry `@rendermode InteractiveServer`)
THEN:   all four MudBlazor providers in that layout (`MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider`) MUST carry `@rendermode="InteractiveServer"` on their element tag
NEVER:  leave a provider as a static island in a layout that hosts interactive pages — a static `MudPopoverProvider` under an interactive page circuit causes the PR #401 "Missing <MudPopoverProvider />" crash
CHECK:  dotnet test --filter "FullyQualifiedName~LayoutProviderRenderModeTests" --no-build
SEV:    🔴
REF:    ADR-0034 · LayoutProviderRenderModeTests · PR #401 outage

**RULE FE-03** (mudblazor-strict)
WHEN:   adding a new Razor component or modifying an existing one
THEN:   use MudBlazor components for all chrome (layout, navigation, dialogs, snackbars, data grids, alerts, skeletons); custom non-MudBlazor components are permitted ONLY within the four locked delight surfaces: `WizardAnswerStream`, `RefusalPanel` (and its sub-views), `CitationStrip`/`CitationCard`/`CitationGroup`, `TiltPage`/`TiltErrorBoundary`
NEVER:  introduce a second Blazor component library (Radzen, Syncfusion, Fluent UI, etc.) without a superseding ADR; write a custom non-MudBlazor component outside the four delight surfaces
CHECK:  (qualitative — /local-review category 12: "New custom (non-MudBlazor) Razor component outside the four locked delight surfaces")
SEV:    ⚠️
REF:    INVARIANTS#14 · ADR-0008 · ADR-0026 §6

**RULE FE-04** (sse-streaming-contract)
WHEN:   adding or modifying the `/api/wizard/ask:stream` endpoint or any code path that writes to the SSE response stream
THEN:   the endpoint MUST use `text/event-stream` transport (NOT SignalR, NOT WebSocket); every SSE event payload MUST be `AnswerChunk`-shaped JSON (discriminated via `$type`); the stream MUST always terminate with a `Final` chunk on every code path including refusal and exception paths; a `Refusal` chunk MUST be followed by a `Final` chunk
NEVER:  use `AddSignalR`/`MapHub`/`HubConnection` for the wizard ask surface; emit a raw text delta (non-JSON) or plain string as an SSE event payload; allow the stream to exit without emitting `Final`
CHECK:  dotnet test --filter "FullyQualifiedName~AnswerChunkContractTests" --no-build
        NOTE: SignalR/WebSocket absence in src/ is confirmed by: rg -n "AddSignalR|MapHub|new HubConnectionBuilder" src/ || echo CLEAN
SEV:    🔴
REF:    INVARIANTS#14 · ADR-0026 §2 §4 §5 · AnswerChunkContractTests

**RULE FE-05** (problemdetails-errors)
WHEN:   adding or modifying an API error response path, an exception handler, or the `/error` Blazor page
THEN:   API-layer errors return RFC 9457 ProblemDetails extended with `extensions["requestId"]` (from `Activity.Current.TraceId`); the Blazor `/error` page is pinball-themed (`TiltPage`/`TiltErrorBoundary`) and surfaces the `requestId`; catch-all `/{**slug}` routes to `/error?reason=not-found` so 404s never show the framework default page
NEVER:  return an unstructured error body from the API; render the ASP.NET Core default error page for a user-facing route; omit `requestId` from a ProblemDetails extension object
CHECK:  (qualitative — /local-review category 3: "graceful degradation / RFC 9457 ProblemDetails with requestId; pinball-themed /error page")
SEV:    ⚠️
REF:    INVARIANTS#14 · ADR-0026 §9

**RULE FE-06** (audio-muted-by-default)
WHEN:   adding or modifying any audio asset, `SoundController`, or component that invokes the Web Audio API
THEN:   audio MUST default to muted (`SoundController.IsMuted = true`); the opt-in toggle persists the user preference to localStorage; no audio asset auto-plays on page load
NEVER:  set `IsMuted = false` as the default; add an `<audio autoplay>` element; initialize the Web Audio API without the user's explicit opt-in via `SoundController`
CHECK:  rg -n "IsMuted\s*=\s*false|autoplay\b|AutoPlay\s*=\s*true" src/PinballWizard.Web/ || echo CLEAN
SEV:    🔴
REF:    INVARIANTS#14 · ADR-0026 §6 "Explicitly NOT adopted" · SoundControllerTests

**RULE FE-07** (palette-pinned-modern-lcd)
WHEN:   adding or modifying `PinballTheme.cs`, the daytime-route theme constants, or the `:root` theme tokens in `src/PinballWizard.Web/wwwroot/app.css`
THEN:   the Modern LCD semantic palette stays pinned to its spec values (amber primary, atomic-green grounded, saturated-red refusal, magenta mode, warm-near-black surface family); a change to any pinned hex updates `PinballThemeContractTests` / `DaytimeRouteThemeContractTests` in the same commit; each accent keeps a non-color signal so the palette degrades safely
NEVER:  add a sixth semantic accent without deleting one — the palette is closed; adding a role requires a design-authority (`docs/ui/themes/modern-lcd.md`) + ADR change, not a silent new `Color` slot; drift a pinned hex without updating the contract test
CHECK:  dotnet test --filter "FullyQualifiedName~PinballThemeContractTests|FullyQualifiedName~DaytimeRouteThemeContractTests" --no-build
SEV:    🔴
REF:    ADR-0008 · docs/ui/themes/modern-lcd.md · docs/ui/design-system/README.md · PinballThemeContractTests · DaytimeRouteThemeContractTests

**RULE FE-08** (theme-design-system-sync)
WHEN:   modifying the implemented theme — `PinballTheme.cs`, the daytime-route constants, or the `:root` tokens in `src/PinballWizard.Web/wwwroot/app.css`
THEN:   re-sync the design-system mirror so `docs/ui/design-system/tokens.css` is updated in the SAME PR; the repo theme is the source of truth and the design-system directory (and its claude.ai/design project) is the mirror
NEVER:  change a theme token without re-syncing `docs/ui/design-system/tokens.css`; treat the design-system directory as authoritative over the theme code
CHECK:  if git diff --name-only origin/main...HEAD | rg -q "PinballTheme\.cs$|Web/wwwroot/app\.css$"; then git diff --name-only origin/main...HEAD | rg -q "docs/ui/design-system/tokens\.css$" && echo CLEAN || echo "FAIL: theme changed but design-system tokens.css was not re-synced"; else echo CLEAN; fi
        NOTE: change-coupling tripwire — a theme-token change requires the mirrored `tokens.css` in the same diff; the exact value match is confirmed in /local-review.
SEV:    ⚠️
REF:    docs/ui/design-system/README.md (mirror contract) · ADR-0026 §6

**RULE FE-09** (citation-as-hero-and-cta-parity)
WHEN:   adding or modifying a citation surface (`CitationStrip`/`CitationCard`/`CitationGroup`), a `RefusalPanel` recovery CTA set, or any component rendering peer community-resource destinations
THEN:   citation cards render full-fidelity and uncollapsed by default — citation-as-hero makes provenance visible (see `PROV-01`); peer outbound destinations get visually identical CTAs (no elevated "primary" among peers — the visual expression of `COMM-02` no-editorial-ranking)
NEVER:  collapse or truncate the citation stack behind a "show sources" toggle by default; give one peer destination a visually dominant CTA (size / color / placement) while de-emphasizing the others
CHECK:  (qualitative — /local-review category 12 + category 13: citation stack uncollapsed/full-fidelity; peer CTAs visually identical)
SEV:    🔴
REF:    PROV-01 (provenance sacred) · COMM-02 (no-editorial-ranking) · ADR-0026 §6 · docs/ui/design-system/README.md

**RULE FE-10** (no-js-mutation-of-blazor-owned-dom)
WHEN:   adding or modifying app-authored JavaScript — any `*.js` under `src/PinballWizard.Web/wwwroot/`, or an inline `<script>` block in `Components/App.razor`
THEN:   drive layout/theme state by toggling classes / data-attributes / CSS custom properties on `<html>` (`document.documentElement`), or by setting the CSS variable MudBlazor already consumes (e.g. `--mud-drawer-width-left`); read/write only the app's own `[data-testid]` / `[data-pw-*]` hook elements
NEVER:  `querySelector`/`getElementById` a MudBlazor-rendered element (any `.mud-*` class) and mutate it (`.style`, `.setAttribute`, `.classList`, `.innerHTML`, removal) — Blazor reconciles that DOM against its render tree and MudBlazor drives its layout off CSS variables; reaching in fights hydration and silently kills the admin circuit (the reverted collapse attempt did exactly this)
CHECK:  dotnet test --filter "FullyQualifiedName~NoJsMutationOfBlazorOwnedDomTests" --no-build
SEV:    🔴
REF:    ADR-0034 (render-mode doctrine) · memory `reference_js_dom_mutation_breaks_admin_circuit` · NoJsMutationOfBlazorOwnedDomTests

## Definition of Done

- FE-01: every interactive routable page declares `@rendermode InteractiveServer`; error/degraded surfaces stay static with `Href` anchors. Enforced by `RenderModeConventionTests`.
- FE-02: every layout that hosts interactive pages pins all four MudBlazor providers to `@rendermode="InteractiveServer"`. Enforced by `LayoutProviderRenderModeTests`.
- FE-03: MudBlazor is the sole chrome library; custom components outside the four delight surfaces require a new ADR.
- FE-04: `/api/wizard/ask:stream` is SSE-only with `AnswerChunk`-shaped JSON; `Final` always closes the stream. Enforced by `AnswerChunkContractTests`.
- FE-05: API errors are RFC 9457 ProblemDetails with `requestId`; `/error` is the pinball-themed tilt page.
- FE-06: audio defaults to muted; no auto-play. Enforced by `SoundControllerTests`.
- FE-07: the Modern LCD palette stays pinned to spec (closed 5-accent set); hex drift fails `PinballThemeContractTests`/`DaytimeRouteThemeContractTests`.
- FE-08: a theme-token change re-syncs `docs/ui/design-system/tokens.css` in the same PR (mirror stays current).
- FE-09: citation cards full-fidelity & uncollapsed; peer outbound CTAs visually identical (no favoritism).
- FE-10: app-authored JS never selects/mutates MudBlazor-owned `.mud-*` DOM; layout state is driven through `<html>` or the CSS variables MudBlazor consumes. Enforced by `NoJsMutationOfBlazorOwnedDomTests`.
