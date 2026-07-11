# frontend-blazor — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| FE-01 | render-mode-correctness | adding/modifying a routable Blazor page | 🔴 | INVARIANTS#14 · ADR-0034 |
| FE-02 | mudblazor-providers-pinned-interactive | adding/modifying a layout that serves interactive pages | 🔴 | ADR-0034 |
| FE-03 | mudblazor-strict | adding/modifying a Razor component | ⚠️ | INVARIANTS#14 · ADR-0008 · ADR-0026 §6 |
| FE-04 | sse-streaming-contract | adding/modifying the ask:stream endpoint or SSE write path | 🔴 | INVARIANTS#14 · ADR-0026 §2 §4 §5 |
| FE-05 | problemdetails-errors | adding/modifying API error responses or /error page | ⚠️ | INVARIANTS#14 · ADR-0026 §9 |
| FE-06 | audio-muted-by-default | adding/modifying audio assets or SoundController | 🔴 | INVARIANTS#14 · ADR-0026 §6 |
| FE-07 | palette-pinned-modern-lcd | modifying PinballTheme.cs / daytime constants / app.css :root | 🔴 | ADR-0008 · modern-lcd.md · PinballThemeContractTests |
| FE-08 | theme-design-system-sync | modifying a theme token | ⚠️ | design-system/README.md (mirror) · ADR-0026 §6 |
| FE-09 | citation-as-hero-and-cta-parity | modifying citation/refusal-CTA/peer-destination surfaces | 🔴 | PROV-01 · COMM-02 · ADR-0026 §6 |
| FE-10 | no-js-mutation-of-blazor-owned-dom | adding/modifying app-authored JS (wwwroot js, colocated razor.js, inline component script) | 🔴 | ADR-0034 · reference_js_dom_mutation_breaks_admin_circuit · NoJsMutationOfBlazorOwnedDomTests |
