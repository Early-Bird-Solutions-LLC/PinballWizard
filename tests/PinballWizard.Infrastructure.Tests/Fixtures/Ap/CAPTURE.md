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
