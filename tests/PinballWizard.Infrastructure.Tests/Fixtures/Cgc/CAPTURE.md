# CGC fixtures — CAPTURED FROM LIVE SOURCE

**Do not hand-author files in this directory.** They are captured verbatim from the
live source so tests assert against reality, not against an assumed shape (TEST-05,
#758). The only change is line endings: the site serves mixed CRLF/LF, and
`.gitattributes` normalizes them to LF on commit.

This exists because of #758: PR #752 shipped a slug-derivation rule built on a
filename pattern that does not exist on the live site. Its unit tests asserted against
invented URLs encoding the same false premise, so they passed green and every review
gate validated the fabrication. Only a live probe caught it.

Captured for #967 / #995. Chicago Gaming removed the `/coinop/` machines index
(404 since ~2026-08); discovery now reads the site root. Individual
`/coinop/{slug}` game pages still resolve. The Pinball dropdown in the shared site
header links every coin-op machine, plus the Cactus Canyon upgrade sub-page, which
the parser rejects.

| File | Source URL | Captured (UTC) |
|---|---|---|
| homepage.captured.html | https://www.chicago-gaming.com/ | 2026-09-05T16:31:00Z |
| sitemap.captured.xml | https://www.chicago-gaming.com/sitemap.xml | 2026-09-28T17:00:29Z |
| coinop-attack-from-mars.captured.html | https://www.chicago-gaming.com/coinop/attack-from-mars | 2026-09-28T17:00:29Z |
| coinop-cactus-canyon.captured.html | https://www.chicago-gaming.com/coinop/cactus-canyon | 2026-09-28T17:00:29Z |
| coinop-medieval-madness.captured.html | https://www.chicago-gaming.com/coinop/medieval-madness | 2026-09-28T17:00:29Z |
| coinop-monster-bash.captured.html | https://www.chicago-gaming.com/coinop/monster-bash | 2026-09-28T17:00:29Z |
| coinop-pulp-fiction.captured.html | https://www.chicago-gaming.com/coinop/pulp-fiction | 2026-09-28T17:00:29Z |

Verified live at homepage capture time (single request each):

| URL | Status |
|---|---|
| `https://www.chicago-gaming.com/` | 200 |
| `https://www.chicago-gaming.com/coinop/` | **404** |
| `https://www.chicago-gaming.com/coinop/attack-from-mars` | 200 |
| `https://www.chicago-gaming.com/coinop/medieval-madness` | 200 |
| `https://www.chicago-gaming.com/coinop/pulp-fiction` | 200 |
| `/robots.txt` | 200. `User-agent: *` disallows only `/images`. No `Sitemap:` line. |
| `/sitemap_index.xml` | 404 |

The homepage capture contains **nine** `/coinop/` anchor elements resolving to **six**
distinct paths — `cactus-canyon`, `medieval-madness` and `pulp-fiction` each appear
twice (nav plus hero/teaser). Five distinct paths are machines;
`/coinop/cactus-canyon/upgrade` is a sub-page and must be rejected by
`ParseMachineLinks`'s single-slug-segment rule. The resulting count of 5 therefore
exercises BOTH the sub-page rule and the `HashSet` dedup in `CgcMenuClient` — 9
anchors reach 5 results only because duplicates collapse.

### Why the nav and not the sitemap

`/sitemap.xml` **does exist** (HTTP 200, valid XML). It is not usable for discovery
because it is **incomplete**: a 2019 xml-sitemaps.com snapshot that lists only
`attack-from-mars`, `medieval-madness` and `monster-bash`. `cactus-canyon` and
`pulp-fiction` appear nowhere in it. That omission is why discovery prefers the nav
despite the project's usual preference for machine-consumer metadata. The sitemap is
checked in to pin that evidence.

No captured page carries JSON-LD, and the root's Open Graph tags describe the site,
not the machines. Note the nav is a *navigation* source rather than a dedicated
index, so it is more fragile than the `/coinop/` index it replaces — a nav reshuffle
changes discovery. If CGC ever completes the sitemap or publishes a real machine
listing, prefer it and re-point `ChicagoGaming:MachinesIndexPath`.

## Re-capture

Uses the project's shipped `Politeness:UserAgent`, so a re-capture reproduces what the
scraper itself receives.

```bash
UA="PinballWizard/0.1 (+https://github.com/Early-Bird-Solutions-LLC/PinballWizard; polite-scraper)"
curl -s -A "$UA" https://www.chicago-gaming.com/ > homepage.captured.html
curl -s -A "$UA" https://www.chicago-gaming.com/sitemap.xml > sitemap.captured.xml
for s in attack-from-mars cactus-canyon medieval-madness monster-bash pulp-fiction; do
  sleep 2
  curl -s -A "$UA" "https://www.chicago-gaming.com/coinop/$s" > "coinop-$s.captured.html"
done
```

If a re-capture changes these files, re-validate the CGC discovery and extraction
rules against the new shape — that is the point of checking them in.
