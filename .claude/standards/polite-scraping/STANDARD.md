---
name: polite-scraping
id-prefix: POLITE
status: active
applies-to:
  - "src/PinballWizard.Infrastructure/Scraping/**"
---

# Polite-Scraping Standard

Polite-by-construction is a marketing surface: visibly throttle, honor
robots.txt, prefer machine-consumer metadata. Politeness > performance.

**RULE POLITE-01** (gate-routing)
WHEN:   scraper code makes an outbound HTTP request
THEN:   route it through IPolitenessGate via PoliteScraperBase (GetStringPolitelyAsync / SendPolitelyAsync)
NEVER:  call HttpClient.GetAsync / GetStringAsync / PostAsync / SendAsync directly in scraper code
CHECK:  rg -n "\.(GetAsync|GetStringAsync|PostAsync|SendAsync)\(" src/PinballWizard.Infrastructure/Scraping/ | rg -v "Politely|PoliteScraperBase|RobotsTxtCache"
        NOTE: RobotsTxtCache is exempt — it IS the politeness infrastructure; routing its own robots.txt fetch through the gate is circular.
SEV:    🔴
REF:    INVARIANTS#2 · feedback_polite_scraping

**RULE POLITE-02** (robots-unconditional)
WHEN:   adding or modifying a source scraper
THEN:   honor robots.txt unconditionally; sites with Disallow:/ stay skipped until explicit permission
NEVER:  add a robots.txt bypass or an override flag that ignores Disallow
CHECK:  rg -ni "ignore.*robots|robots.*bypass|disallow.*override" src/PinballWizard.Infrastructure/Scraping/
SEV:    🔴
REF:    INVARIANTS#2

**RULE POLITE-03** (metadata-first)
WHEN:   extracting structured data from a source page
THEN:   exhaust OG / JSON-LD / sitemap / robots before reaching for DOM selectors
NEVER:  hand-roll DOM scraping when a machine-consumer metadata source is available
CHECK:  (qualitative — /local-review) — confirm JsonLd/OpenGraph/sitemap tried before DOM heuristics
SEV:    ⚠️
REF:    INVARIANTS#3 · feedback_machine_consumer_metadata_first

**RULE POLITE-04** (polite-base)
WHEN:   adding a new ISourceScraper
THEN:   extend PoliteScraperBase and set the polite User-Agent + default robots.txt path on the typed HttpClient
NEVER:  introduce a scraper that bypasses PoliteScraperBase
CHECK:  rg -n "class \w+Scraper" src/PinballWizard.Infrastructure/Scraping/ then confirm each extends PoliteScraperBase
SEV:    🔴
REF:    INVARIANTS#2

## Definition of Done

- POLITE-01: no bare HttpClient verb calls in scraper code (grep clean).
- POLITE-02: no robots bypass introduced.
- POLITE-03: metadata sources tried before DOM.
- POLITE-04: new scraper extends PoliteScraperBase.
