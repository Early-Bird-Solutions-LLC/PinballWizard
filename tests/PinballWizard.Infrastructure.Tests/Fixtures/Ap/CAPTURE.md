# AP support-page fixtures — CAPTURED FROM LIVE SOURCE

**Do not hand-author files in this directory.** They are captured verbatim from the
live source so tests assert against reality, not against an assumed shape.

This exists because of #758: PR #752 shipped a slug-derivation rule built on a
`{Title}-SB-{NNN}` filename pattern that does not exist on this site. Its unit tests
asserted against invented URLs encoding the same false premise, so they passed green
and every review gate validated the fabrication. Only a live probe caught it.

| | |
|---|---|
| Source URL | https://www.american-pinball.com/support/ |
| Captured (UTC) | 2026-07-13T13:27:59Z |
| support-page.captured.html | 119136 bytes |
| bulletin-urls.captured.txt | 38 URLs |

## Re-capture

```bash
curl -s -A "PinballWizardBot/1.0 (+https://pinwiz.ai)" "https://www.american-pinball.com/support/" > support-page.captured.html
curl -s -A "PinballWizardBot/1.0 (+https://pinwiz.ai)" "https://www.american-pinball.com/support/"   | grep -oE 'https?://s4\.american-pinball\.com/[^"'"'"' ]+\.pdf' | sort -u > bulletin-urls.captured.txt
```

If a re-capture changes these files, the AP parsing rules must be re-validated against
the new shape — that is the point of checking them in.

## Sitemap index and game-page category — CAPTURED FROM LIVE SOURCE

Captured 2026-09-24. `https://www.american-pinball.com/sitemap.xml` responds
301 to `https://americanpinball.com/sitemap.xml`, which Yoast redirects to
`sitemap_index.xml`. That document is a sitemap index, not a flat
`/games/{slug}` urlset. Game pages are the seven WordPress posts in the
`game-page` category (id 114); manuals and news share `post-sitemap.xml`.

| File | Source URL |
|---|---|
| sitemap-index.captured.xml | https://americanpinball.com/sitemap_index.xml |
| post-sitemap.captured.xml | https://americanpinball.com/post-sitemap.xml |
| page-sitemap.captured.xml | https://americanpinball.com/page-sitemap.xml |
| category-sitemap.captured.xml | https://americanpinball.com/category-sitemap.xml |
| post_tag-sitemap.captured.xml | https://americanpinball.com/post_tag-sitemap.xml |
| partner-sitemap.captured.xml | https://americanpinball.com/partner-sitemap.xml |
| release-status-sitemap.captured.xml | https://americanpinball.com/release-status-sitemap.xml |
| author-sitemap.captured.xml | https://americanpinball.com/author-sitemap.xml |
| game-page-category.captured.json | https://americanpinball.com/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug |
| game-page-posts.captured.json | https://americanpinball.com/wp-json/wp/v2/posts?categories=114&per_page=100&page=1&_fields=slug,link |

`houdini-manual` is in the post sitemap and is not a game-page post. Discovery
tests must keep it out.

## Per-game support pages — CAPTURED FROM LIVE SOURCE

Captured 2026-09-28 between 17:07Z and 17:12Z with `PinballWizardBot/1.0 (+https://pinwiz.ai)`.
`https://www.american-pinball.com/support/` responds 301 to
`https://americanpinball.com/support/`. The redesigned index no longer lists
bulletin PDFs (none on `s4.american-pinball.com`, none anywhere). It links to
one support hub per game. Those hubs are the WordPress child pages of the
`support` page (id 2631). `register` and `updates` are children too, but they
are not games. On each hub, every document is an Elementor loop card for one
WordPress post. The card carries WordPress `post_class` output, for example
`type-post category-service-bulletin tag-houdini`. The bulletin PDFs are on
HubSpot's file CDN (`48804760.fs1.hubspotusercontent-na1.net`). robots.txt
disallows only `/wp-admin/`.

| File | Source URL | Bytes |
|---|---|---|
| support-index.captured.html | https://americanpinball.com/support/ | 214435 |
| support-page-lookup.captured.json | https://americanpinball.com/wp-json/wp/v2/pages?slug=support&_fields=id,slug,link,parent | 91 |
| support-child-pages.captured.json | https://americanpinball.com/wp-json/wp/v2/pages?parent=2631&per_page=100&page=1&_fields=id,slug,link | 799 |
| support-houdini.captured.html | https://americanpinball.com/support/houdini/ | 292639 |
| support-barry-os-bbq-challenge.captured.html | https://americanpinball.com/support/barry-os-bbq-challenge/ | 244574 |

Houdini has 16 post cards: 6 `service-bulletin` (2 of them are YouTube-only),
6 `electrical`, 3 `manual`, and 1 `code-update`. That gives 10 bulletin PDFs.
Barry O's BBQ Challenge publishes no bulletin posts. Byte counts are as
downloaded; git normalizes CRLF to LF on commit.

`support-page.captured.html` and `bulletin-urls.captured.txt` (the 2026-07-13
section) record the pre-redesign flat page. `ApDocumentClassificationTests`
replays `bulletin-urls.captured.txt`, and the HTML is the capture that list was
derived from, so both stay. Those `s4.american-pinball.com` URLs return 404
as of 2026-09-28.

### Re-capture

```bash
UA="PinballWizardBot/1.0 (+https://pinwiz.ai)"
curl -s -A "$UA" https://americanpinball.com/support/ > support-index.captured.html
curl -s -A "$UA" "https://americanpinball.com/wp-json/wp/v2/pages?slug=support&_fields=id,slug,link,parent" > support-page-lookup.captured.json
curl -s -A "$UA" "https://americanpinball.com/wp-json/wp/v2/pages?parent=2631&per_page=100&page=1&_fields=id,slug,link" > support-child-pages.captured.json
curl -s -A "$UA" https://americanpinball.com/support/houdini/ > support-houdini.captured.html
curl -s -A "$UA" https://americanpinball.com/support/barry-os-bbq-challenge/ > support-barry-os-bbq-challenge.captured.html
```
