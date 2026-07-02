# Admin Sources — status clarity redesign

**Date:** 2026-07-02
**Surface:** `/admin/sources` (`AdminSources.razor`) — public-read showcase admin page
**Status:** Design approved, pending spec review

## Problem

The `/admin/sources` grid shows every ingestion source in one flat list with a
binary **Enabled / Disabled** status chip. Four rows show "Disabled", which reads
as *broken* or *overlooked* — but every one of them is a **documented, deliberate
decision**:

| Source | Real reason it is off |
| --- | --- |
| Jersey Jack — Service Bulletins | No bulletin content exists on the site (checked) |
| Spooky — Service Bulletins | No bulletin document type on the site (checked) |
| Chicago Gaming — Service Bulletins | Bulletins page covers arcade only, not pinball |
| Pinball Brothers — Service Bulletins | Bulletins exist on Freshdesk but need an API key |

Three distinct failures of the current UI:

1. **The binary chip flattens three very different realities** — genuinely active,
   "we checked and there is no such content" (NoSource), and "content exists but
   we are blocked" (Deferred). "Disabled" communicates none of this.
2. **Sub-feeds are interleaved with manufacturers.** A "Jersey Jack — Service
   Bulletins" row reads as "Jersey Jack is off."
3. **The reason is unreachable.** It lives in a `(NoSource)` / `(Deferred)` suffix
   crammed into the display name, plus rich `discoveryNotes` text that is
   **silently dropped at seed time** — the `IngestionSourceSeed` DTO and the
   `IngestionSource` domain entity do not carry it, so it never reaches Cosmos or
   the UI. The page cannot explain itself even in principle.

This is a prospect-facing surface (`[AllowAnonymous]`), so an unexplained wall of
"Disabled" actively erodes confidence.

## Root cause

`data/seeds/ingestion_sources.v1.json` already carries `discoveryStatus`,
`discoveryNotes`, and `discoveryDate` for the deferred/no-source entries — but the
seed pipeline throws them away. Fixing the display without persisting these fields
would mean re-deriving the reason from a name suffix, which is fragile (the seed
even contains corrupted em-dashes: `â€”`).

## Design

### 1. Data model — persist the reason (the root fix)

Add four fields to **both** `IngestionSourceSeed`
(`src/PinballWizard.Application/Sync/IngestionSourceSeed.cs`) and the domain entity
`IngestionSource` (`src/PinballWizard.Core/Domain/IngestionSource.cs`), and carry
them through `IngestionSourceSeeder` into Cosmos:

| Field | Type | Meaning |
| --- | --- | --- |
| `SourceGroup` | `string` | Manufacturer this source belongs to, e.g. `"Jersey Jack Pinball"`. Shared by the primary source and all of its sub-feeds. The grouping key. |
| `DiscoveryStatus` | `string?` | `Active` / `NoSource` / `Deferred`. Null is treated as `Active`. |
| `DiscoveryNotes` | `string?` | The human explanation already authored in the seed. |
| `DiscoveryDate` | `DateOnly?` | When the assessment was made. |

These are **config** fields: re-applied on every seed (like `DisplayName`,
`Enabled`, `Cadence`), while runtime counters (`LastRunAt`, `LastSuccessAt`,
`TotalDocumentsDiscovered`, `TotalRunFailures`, `ETag`) remain preserved on update
exactly as they are today.

`SourceGroup` is `required` on the seed (every source must declare its group).
`DiscoveryStatus/Notes/Date` are optional.

### 2. Status vocabulary — one chip, icon + label

Replace the binary Enabled/Disabled chip with a four-state chip. Each state carries
an **icon and a text label** so colour is never the sole carrier of meaning (WCAG
2.1 AA; repo neutrality rules).

| State | Chip (icon + label + colour) | Derivation |
| --- | --- | --- |
| **Active** | ● "Active" — success | `Enabled` and (`DiscoveryStatus` is `Active` or null) |
| **No source** | ○ "No source" — neutral/default | `!Enabled` and `DiscoveryStatus == NoSource` |
| **Deferred** | ⏸ "Deferred" — warning | `!Enabled` and `DiscoveryStatus == Deferred` |
| **Disabled** | ⊘ "Disabled" — plain | `!Enabled` and no discovery reason (manual off-switch fallback) |

Derivation lives in one pure helper (status enum from `(Enabled, DiscoveryStatus)`)
so it is unit-testable independently of the grid.

The shift that solves the reported confusion: **"No source" and "Deferred" replace
the alarming, meaningless "Disabled"** — they read as decisions, not failures.

### 3. Layout — grouped by manufacturer, reason inline

`AppDataGrid` grouped by `SourceGroup`. Each manufacturer renders as a group; its
primary source and sub-feeds render underneath, so a disabled sub-feed can never be
misread as a disabled manufacturer.

For any **non-Active** row, the reason and assessed date render as an
**always-visible muted caption** directly under the status — not behind a hover or
a click:

```
▾ Jersey Jack Pinball
    Jersey Jack Pinball                    ● Active      daily
    Per-Edition Support Docs               ● Active      weekly
    Service Bulletins                      ○ No source   —
      └ No service-bulletin section exists on jerseyjackpinball.com. (assessed 2026-05-26)
▾ Pinball Brothers
    Pinball Brothers                       ● Active      weekly
    Service Bulletins                      ⏸ Deferred    —
      └ Bulletins exist on Freshdesk but the API requires a key even for the public portal. (2026-05-26)
```

**Render mode.** This work builds on top of an in-flight change on
`fix/admin-sources-pager-rendermode` that moves the page from static SSR to
`@rendermode InteractiveServer` (so the grid pager is live). Because the page is
interactive, manufacturer groups are **collapsible** via `MudDataGrid`'s
`Groupable`, at no extra architectural cost.

Reasons remain **inline / always-visible** (not click-to-reveal): more accessible
(no hover/tap gymnastics, works on mobile), more showcase-friendly (a prospect
reads the "why" immediately), and it keeps the reason legible even when a group is
expanded. Collapsibility applies to the manufacturer grouping, not to the reason
text.

### 4. Seed-data cleanup (rides along)

In `data/seeds/ingestion_sources.v1.json`:

- Add `sourceGroup` to **every** entry, and `discoveryStatus` to every entry
  (`Active` for the currently-enabled manufacturers that omit it today).
- Fix the corrupted em-dashes (`â€”` → `—`) in display names.
- Strip the `(NoSource)` / `(Deferred)` suffixes out of display names — that
  information now lives in the status chip, so the name reads simply
  "Service Bulletins".

### 5. Testing (behaviour, not structure)

- **Seeder round-trip:** discovery + group fields persist through insert, and are
  re-applied on update while runtime counters are preserved (extend
  `IngestionSourceSeederTests`).
- **Status derivation:** each `(Enabled, DiscoveryStatus)` pair maps to the expected
  chip state, including null-⇒-Active and no-reason-⇒-Disabled fallbacks.
- **Render (bUnit):** a `NoSource` row renders the "No source" chip **and** its
  reason caption with the date; an `Active` row renders no caption; a fixture with a
  real manufacturer + sub-feed pair proves the rows group under one manufacturer
  header (grouping actually fires, not just column presence).

### 6. Out of scope

- Editing sources from this page (stays read-only).
- The `/admin/sources/{id}` detail page (unchanged).
- OPDB / Pinball Map (already `Active`, unaffected).
- The pager / render-mode change itself (owned by the base branch; this design
  consumes it, does not re-do it).

## Files touched

- `src/PinballWizard.Core/Domain/IngestionSource.cs` — 4 new fields
- `src/PinballWizard.Application/Sync/IngestionSourceSeed.cs` — 4 new fields
- `src/PinballWizard.Application/Sync/IngestionSourceSeeder.cs` — map new fields on insert + update
- `data/seeds/ingestion_sources.v1.json` — add fields, fix em-dashes, strip suffixes
- `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor` — status helper, grouping, chip, inline reason
- Tests: `IngestionSourceSeederTests`, `AdminSourcesTests`, + status-derivation unit
