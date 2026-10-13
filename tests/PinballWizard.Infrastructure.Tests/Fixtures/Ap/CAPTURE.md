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
