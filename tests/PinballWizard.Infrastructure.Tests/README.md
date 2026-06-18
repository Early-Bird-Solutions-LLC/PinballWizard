# PinballWizard.Scraper.Tests

The single test project for the solution. Covers the domain layer (`Core`), application services (`Application`), persistence repositories (`Infrastructure.Persistence`), and the family-wide scraper-pipeline integration tests across every HTTP-based `ISourceScraper`.

## Layout

```text
tests/PinballWizard.Scraper.Tests/
├── Application/                # tests for orchestrators, use cases
├── Core/                       # tests for domain entities and value objects
├── Domain/                     # additional domain-layer test fixtures
├── Persistence/                # repository and Cosmos plumbing tests
├── Scraping/
│   ├── _TestInfra/             # FakePolitenessGate, QueueingHttpMessageHandler — shared
│   ├── Ap/, BarrelsOfFun/,     # per-manufacturer scraper-pipeline integration tests
│   │   ChicagoGaming/, Jjp/,
│   │   Multimorphic/,
│   │   PinballBrothers/,
│   │   Spooky/, Stern/         # — see "Stern Playwright asymmetry" below
│   ├── JsonLd/, OpenGraph/     # shared parser tests (JSON-LD, Open Graph)
│   └── Polite/                 # PoliteScraperBase + IPolitenessGate primitives
└── Sync/                       # OPDB sync, scraper reconciliation, ingestion-source seeder tests
```

## Family-wide scraper-pipeline integration test infrastructure

`Scraping/_TestInfra/` provides two pieces of shared infrastructure that every HTTP-based scraper test wires up:

- **`FakePolitenessGate`** — implements `IPolitenessGate` for tests. Records every Acquire / Report call so tests can assert the politeness contract is respected (per-host throttle, robots.txt honored, 429 abort) without making real network requests.
- **`QueueingHttpMessageHandler`** — `HttpMessageHandler` that returns canned responses in queue order keyed by request URL prefix. Lets a test stage an exact set of HTML / JSON / 404 / 5xx responses for a scraper to consume and assert on.

The proven 5-test template (first established in [`Scraping/ChicagoGaming/CgcGamePageScraperTests.cs`](Scraping/ChicagoGaming/CgcGamePageScraperTests.cs)) pins these invariants for every covered scraper:

1. Yield order — items are emitted in the order the scraper visits pages
2. Full provenance — each yielded `ScrapedItem` carries `Source` / `DiscoveryUrl` / `DiscoveryContext` / `GameSlug` populated end-to-end
3. Gate-vs-wire URL equality — the URL the politeness gate sees matches the URL HttpClient actually requests
4. Per-page failure isolation — one bad page does not abort the entire run; the scraper logs and continues
5. `PolitenessException` propagation — both `Acquire` and `Report` paths surface gate failures correctly

8 of the 10 `ISourceScraper` implementations are covered by this template:

- `ManualsScraper` (Stern, HttpClient — see `Scraping/Stern/ManualsScraperTests.cs`)
- `JjpProductScraper` (`Scraping/Jjp/`)
- `ApGamePageScraper` (`Scraping/Ap/`)
- `SpookyGamePageScraper` (`Scraping/Spooky/`)
- `PbGamePageScraper` (`Scraping/PinballBrothers/`)
- `BofProductScraper` (`Scraping/BarrelsOfFun/`)
- `MultimorphicProductScraper` (`Scraping/Multimorphic/`)
- `CgcGamePageScraper` (`Scraping/ChicagoGaming/`)

## Stern Playwright asymmetry

The two remaining `ISourceScraper`s are **deliberately not covered** by the family-wide template:

- **`GamePageScraper`** — Stern's per-game pages (`/game/{slug}/`). Vue.js SPA with three tabs per game (Promotional Materials, Game Code, Specs & Manual) that require button clicks to render content.
- **`ServiceBulletinScraper`** — Stern's service-bulletin index. Vue.js SPA with scroll-to-load behavior.

Both extend `PolitePlaywrightScraperBase` and drive a real Chromium browser via Playwright's `IBrowserContext` / `IPage` APIs. They never call `HttpClient.GetAsync` — so wiring `QueueingHttpMessageHandler` at the typed-`HttpClient` layer doesn't intercept any of their actual page-load traffic. The shared infra cannot exercise these scrapers without a parallel `FakePlaywrightContext` or equivalent fixture that stubs Playwright's browser-driving surface.

### What coverage exists instead

- **Unit-level coverage of parsing helpers** — extraction logic that operates on rendered HTML strings (selectors, text normalization, edition extraction) is unit-tested in isolation against captured HTML fixtures.
- **End-to-end validation against the live site** — Stern Playwright scrapers are validated by running the actual scraper against `sternpinball.com` and inspecting the produced `ScrapedItem` records. This is the operational coverage path used pre-merge for any change touching the Playwright scrapers.
- **Politeness-primitive coverage** — `PolitePlaywrightScraperBase` shares its politeness-gate, robots.txt, and User-Agent invariants with `PoliteScraperBase`. The shared primitives are covered by the `Scraping/Polite/` tests.

### Why this trade-off is acknowledged here, not silently deferred

The asymmetry is deliberate and documented (this section). The pinning test in [`Scraping/Stern/SternPlaywrightAsymmetryDocumentationTests.cs`](Scraping/Stern/SternPlaywrightAsymmetryDocumentationTests.cs) asserts that this README still names the asymmetry — if the section is removed or the README is deleted, the test fails. The operator either restores the documentation or replaces the test with real Playwright-route coverage.

### Revisit criteria

Build a `FakePlaywrightContext` (or equivalent) and remove this asymmetry when **any** of the following becomes true:

- The Stern Playwright scrapers gain enough behavior change to make end-to-end-only validation costly (e.g., regular DOM-selector churn that breaks scrapers without test signal)
- A second manufacturer adopts a Playwright-driven scraper, multiplying the coverage gap
- The build cost of the fake-Playwright fixture drops below ~4 hours (e.g., a Microsoft.Playwright official testing helper lands)

When that happens: build the fixture, write the 5-test template against `GamePageScraper` and `ServiceBulletinScraper`, delete this asymmetry section, and delete the `SternPlaywrightAsymmetryDocumentationTests` pinning test alongside it.

## Engineering conventions

- **Test naming:** `Method_State_Expectation` for behavior tests (e.g., `SeedAsync_ReRun_AppliesConfigButPreservesRuntimeFields`). Documentation / pinning tests use the subject as the class name and a single-fact method describing the invariant being pinned.
- **Mocking:** NSubstitute. Use `Substitute.For<TInterface>()` and `.Returns(...)`. Do not introduce alternative mocking libraries.
- **Loggers in tests:** `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance`.
- **Behavior over structure:** a test named "rejects merch" must include merch in the input fixture; one named "deduplicates" must include duplicates that actually trigger dedup. `/local-review` checks this; do not regress.

## Phase-2 specific scope

The asymmetry resolution shipped via Phase 2 § Scope item 8 (route ii — documentation + pinning test). See [`docs/build-spec.md`](../../docs/build-spec.md).
