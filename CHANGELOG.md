# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0](https://semver.org/spec/v2.0.0.html).
Pre-1.0 versions may include breaking changes in any release; the
catalog schema is not yet considered stable.

## [Unreleased]

### Added

- **Repo hygiene foundation**: PR template, issue templates (bug / feature),
  `CODEOWNERS`, `SECURITY.md`, this `CHANGELOG.md`. Closes the
  documented-vs-reality gaps in
  [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) Track A.1.
- **Parallel execution plan**:
  [`docs/parallel_execution_plan.md`](docs/parallel_execution_plan.md) —
  identifies two gating PRs (Cosmos schema, PoliteScraper base) that unlock
  five concurrent tracks, with critical path Gate 1 → Track D → Track E.
- **AI/ML ideas catalog**:
  [`docs/ai_ml_ideas.md`](docs/ai_ml_ideas.md) — ~15 future-phase AI/ML
  feature concepts with three starred deep-dives (Playfield video analysis,
  AI pinball coach, Service bulletin diagnosis).
- **Phase 5+ feature concepts**:
  [`docs/dream_game_concept.md`](docs/dream_game_concept.md) (RAG-grounded
  fan-concept pinball machine generator) and
  [`docs/strategy_tracker_concept.md`](docs/strategy_tracker_concept.md)
  (competitive-player strategy library + analytics + AI-assisted refinement).
- **Architecture refinements** locked into
  [`docs/infra_analysis.md`](docs/infra_analysis.md): MudBlazor strict, Entra
  External ID for admin RBAC v1 (social login when passport ships), Admin
  Control Plane built into the main Blazor app behind `/admin`, IngestionSources
  whitelist as Cosmos data.
- **Static metadata extractor**: parses OG / JSON-LD / `contact-for-availability`
  shop links to populate editions, MSRP, availability, `DatePublished`,
  `ReleaseYear` from machine-consumer metadata instead of speculative DOM
  selectors.
- **Stale-title healing**: cookie-banner titles and 3x edition duplicates
  removed; `CatalogBuilder.LinkDocumentsToGames` now always-syncs game titles
  to heal `catalog.json`.

### Changed

- **Architecture pivot**: single-project `PinballWizard.Scraper` →
  Clean Architecture multi-project layout (`PinballWizard.Core`,
  `PinballWizard.Application`, `PinballWizard.Infrastructure`,
  `PinballWizard.Cli`). `IFileDownloader` interface defined in Application,
  implemented in Infrastructure.

### Fixed

- `GamePageScraper` returning 0 file links — `LinkRaw`, `EditionRaw`,
  `BulletinRaw` converted from positional records to classes with init-able
  properties + `[JsonPropertyName]` attributes for Playwright 1.12.0
  compatibility.
- `--source games` filter now uses an explicit alias map for
  `manuals` / `games` / `bulletins` and warns on unknown filters.
- `--build-catalog` wired to `BuildCatalogAsync`, reconciling
  `DocumentRecord.File` entries against disk while preserving
  `Timeline.LastDownloadedAt`.
- Catalog writes now atomic — `SaveCatalogAsync` /
  `SaveGameCatalogAsync` write to `.tmp` then `File.Move` to prevent
  corruption on interruption.

[Unreleased]: https://github.com/Early-Bird-Solutions-LLC/PinballWizard/compare/HEAD...HEAD
