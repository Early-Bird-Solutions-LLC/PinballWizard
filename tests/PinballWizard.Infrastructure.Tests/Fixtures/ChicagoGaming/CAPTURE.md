# Chicago Gaming fixtures — CAPTURED FROM LIVE SOURCE

**Do not hand-author files in this directory.** They are captured verbatim from the
live source so tests assert against reality, not against an assumed shape (TEST-05,
#758).

Captured 2026-09-28T17:00:29Z for #967. The `/coinop/` machines index returned 404
from 2026-08-23, so discovery now reads the site root. The Pinball dropdown in the
shared site header links every coin-op machine, plus the Cactus Canyon upgrade
sub-page, which the parser rejects.

| File | Source URL |
|---|---|
| home.captured.html | https://www.chicago-gaming.com/ |
| sitemap.captured.xml | https://www.chicago-gaming.com/sitemap.xml |
| coinop-attack-from-mars.captured.html | https://www.chicago-gaming.com/coinop/attack-from-mars |
| coinop-cactus-canyon.captured.html | https://www.chicago-gaming.com/coinop/cactus-canyon |
| coinop-medieval-madness.captured.html | https://www.chicago-gaming.com/coinop/medieval-madness |
| coinop-monster-bash.captured.html | https://www.chicago-gaming.com/coinop/monster-bash |
| coinop-pulp-fiction.captured.html | https://www.chicago-gaming.com/coinop/pulp-fiction |

Probe results on the same day:

| URL | Status |
|---|---|
| /robots.txt | 200. `User-agent: *` disallows only `/images`. No `Sitemap:` line. |
| /coinop/ and /coinop | 404 |
| /sitemap_index.xml | 404 |
| /sitemap.xml | 200, but a 2019 xml-sitemaps.com snapshot. Its `/coinop/` entries are Attack From Mars, Medieval Madness, and Monster Bash only. |

The sitemap is checked in to pin why discovery does not use it. No captured page
carries JSON-LD, and the root's Open Graph tags describe the site, not the machines.

## Re-capture

```bash
UA="PinballWizardBot/1.0 (+https://pinwiz.ai)"
curl -s -A "$UA" https://www.chicago-gaming.com/ > home.captured.html
curl -s -A "$UA" https://www.chicago-gaming.com/sitemap.xml > sitemap.captured.xml
for s in attack-from-mars cactus-canyon medieval-madness monster-bash pulp-fiction; do
  sleep 2
  curl -s -A "$UA" "https://www.chicago-gaming.com/coinop/$s" > "coinop-$s.captured.html"
done
```

If a re-capture changes these files, re-validate the CGC discovery and extraction
rules against the new shape.
