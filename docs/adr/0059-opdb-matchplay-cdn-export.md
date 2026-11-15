# ADR-0059: OPDB catalog via Match Play CDN export

**Status:** Accepted  
**Date:** 2026-11-15  
**Deciders:** Jim Keeley

## Context

The weekly `pinwiz-job-opdb` ACA job failed on 2026-11-08 and 2026-11-15 with
`HttpRequestException: 404` from `OpdbClient.OpenExportStreamAsync` against
`https://opdb.org/api/export` (see scraper-health audit 2026-11-15).

On 2026-10-01, OPDB shut down the `opdb.org/api/*` surface. Catalog access
moved to Match Play:

- Bulk export (preferred): CDN JSON at
  `https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json` (V1) /
  `opdb-v2.json` (V2)
- Optional per-entry API: `https://api.matchplay.events/api/opdb/entry/{id}`

PinballWizard's client still targeted the retired `/api/export` flat array
and snake_case wire shape. Continuing to call opdb.org cannot succeed.

## Decision

1. **Sole catalog source:** `OpdbOptions.ExportUrl` defaults to the Match Play
   **V1** CDN URL (`latest-opdb.json`). `StreamAllMachinesAsync` GETs that URL
   only — no fallback to opdb.org `/api/export`, V2, or slim.
2. **Envelope parse, fail closed:** Deserialize
   `{ machineGroups, machines, aliases }`. Wrong shape (V2 `entries`, legacy
   top-level array, empty-all-three) throws `InvalidOperationException` with a
   remediation message. Never yield an empty catalog from a misconfigured URL.
3. **CamelCase DTOs:** Align `OpdbMachineDto` / group / manufacturer property
   names with the Match Play V1 wire format.
4. **Group titles from export:** Seed the group-title cache from
   `machineGroups` before yielding machines, eliminating the ~1,200 polite
   per-segment GETs that the old API required (and that no longer exist on
   opdb.org).
5. **CDN auth:** Do not attach `Opdb__ApiToken` to the CDN GET (public object;
   wrong credential surface). Token remains available for optional Match Play
   entry API calls via `OpdbOptions.BaseUrl`.
6. **ACA env:** Job sets `Opdb__BaseUrl=https://api.matchplay.events/` and
   `Opdb__ExportUrl` to the V1 CDN URL.

## Alternatives considered

- **Silent multi-URL fallback** (try opdb.org, then CDN): Rejected — violates
  the locked "fallbacks must not hide failures" invariant and would mask
  config drift.
- **Adopt V2 `entries` envelope now:** Rejected for this fix — larger mapper
  change (`entryType`, nested people/images). V1 preserves machines / aliases /
  groups separation the sync already understands. Revisit when V1 is retired.
- **Match Play entry API only (no CDN):** Rejected — Match Play docs require
  CDN for bulk OPDB; entry API is for single lookups.

## Consequences

- Weekly OPDB sync works again after image/deploy picks up this change.
- Group-title resolution during sync is primarily in-process (export-seeded);
  ACA job wall-clock drops vs the old polite per-segment loop.
- Operators must rotate Match Play API tokens into `Opdb-ApiToken` if they
  rely on entry/changelog endpoints; export-only sync needs no token.
- Docs / runbooks that still say `https://opdb.org/api/` need follow-up edits
  (local-dev, secret-rotation).

## Related

- Scraper health audit 2026-11-15 (OPDB `/api/export` 404 = NEW)
- [Match Play data exports](https://docs.matchplay.events/data-exports)
- Tilt Forums OPDB shutdown notice (2026-10-01)
- ADR-0029 (version-aware OPDB ingestion / group titles)
- DL-0003 (original `/api/export` vs paginated `/api/machines` fix)
