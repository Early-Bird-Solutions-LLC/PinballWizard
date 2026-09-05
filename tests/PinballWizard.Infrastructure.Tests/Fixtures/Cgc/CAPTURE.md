# CGC homepage fixture — CAPTURED FROM LIVE SOURCE

**Do not hand-author files in this directory.** They are captured verbatim from the
live source so tests assert against reality, not against an assumed shape.

This exists because of #758: PR #752 shipped a slug-derivation rule built on a
filename pattern that does not exist on the live site. Its unit tests asserted against
invented URLs encoding the same false premise, so they passed green and every review
gate validated the fabrication. Only a live probe caught it.

Captured for #967. Chicago Gaming removed the `/coinop/` machines index that
`CgcMenuClient` had used for discovery: it now returns a hard 404 while the site root
returns 200 and the individual `/coinop/{slug}` game pages still resolve. Discovery
moves to the root, whose navigation links the shipped coin-op titles.

| | |
|---|---|
| Source URL | https://www.chicago-gaming.com/ |
| Captured (UTC) | 2026-09-05T16:31:00Z |
| homepage.captured.html | 10084 bytes |

Verified live at capture time (single request each):

| URL | Status |
|---|---|
| `https://www.chicago-gaming.com/` | 200 |
| `https://www.chicago-gaming.com/coinop/` | **404** |
| `https://www.chicago-gaming.com/coinop/attack-from-mars` | 200 |
| `https://www.chicago-gaming.com/coinop/medieval-madness` | 200 |
| `https://www.chicago-gaming.com/coinop/pulp-fiction` | 200 |

The capture contains **nine** `/coinop/` anchor elements resolving to **six** distinct
paths — `cactus-canyon`, `medieval-madness` and `pulp-fiction` each appear twice (nav
plus hero/teaser). Five distinct paths are machines; `/coinop/cactus-canyon/upgrade` is
a sub-page and must be rejected by `ParseMachineLinks`'s single-slug-segment rule. The
resulting count of 5 therefore exercises BOTH the sub-page rule and the `HashSet` dedup
in `CgcMenuClient` — 9 anchors reach 5 results only because duplicates collapse.

### Why the nav and not the sitemap

`/sitemap.xml` **does exist** (HTTP 200, valid XML, 192 `<loc>` entries as of
2026-09-05). It is not usable for discovery because it is **incomplete**: it lists only
`attack-from-mars`, `medieval-madness` and `monster-bash`. `cactus-canyon` and
`pulp-fiction` appear nowhere in it — zero occurrences of either string in the whole
file. That is the same omission `ChicagoGamingOptions` recorded in 2026-05, still true
in 2026-09, and it is why this project prefers the nav here despite normally preferring
machine-consumer metadata over DOM scraping.

Note the nav is a *navigation* source rather than a dedicated index, so it is more
fragile than the `/coinop/` index it replaces — a nav reshuffle changes discovery. If
CGC ever completes the sitemap or publishes a real machine listing, prefer it and
re-point `ChicagoGaming:MachinesIndexPath`.

## Re-capture

Uses the project's shipped `Politeness:UserAgent`, so a re-capture reproduces what the
scraper itself receives. (Verified 2026-09-05: the response is byte-identical under this
UA and the anchor set is unchanged, so the committed fixture is UA-faithful.)

```bash
curl -s -A "PinballWizard/0.1 (+https://github.com/Early-Bird-Solutions-LLC/PinballWizard; polite-scraper)" \
  "https://www.chicago-gaming.com/" > homepage.captured.html
```

If a re-capture changes this file, the CGC discovery rules must be re-validated against
the new shape — that is the point of checking it in.
