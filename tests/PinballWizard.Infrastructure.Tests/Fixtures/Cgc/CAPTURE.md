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

The capture contains six `/coinop/` anchors. Five are machines; `/coinop/cactus-canyon/upgrade`
is a sub-page and must be rejected by `ParseMachineLinks`'s single-slug-segment rule.

Note this is a *navigation* source rather than a dedicated index, which is more fragile
than what it replaces — a nav reshuffle changes discovery. If CGC publishes a real
machine listing or a sitemap (neither `/sitemap.xml` nor `/sitemap_index.xml` existed
at capture time; both 404), prefer it and re-point `ChicagoGaming:MachinesIndexPath`.

## Re-capture

```bash
curl -s -A "PinballWizardBot/1.0 (+https://pinwiz.ai)" "https://www.chicago-gaming.com/" > homepage.captured.html
```

If a re-capture changes this file, the CGC discovery rules must be re-validated against
the new shape — that is the point of checking it in.
