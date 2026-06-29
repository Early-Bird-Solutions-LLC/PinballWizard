# 0006 — Clean Architecture multi-project layout

**Status:** Accepted
**Date:** 2026-05-02 (codifies a decision implemented in PR #20, branch `Dev-CleanArchitecturePivot`)

## Context

Through Phase 1 the project lived as a single console project,
`PinballWizard.Scraper`. As Phase 2 work begins (Cosmos repositories,
AI Search clients, Azure OpenAI clients, multiple manufacturer
scrapers, a future Blazor Web App, an Azure Function processing the
Cosmos Change Feed), a single project would have to host:

- Domain entities
- Use cases / orchestration
- Infrastructure (HTTP, browser automation, file I/O, Azure SDKs)
- The CLI entry point
- Eventually: the web app, the change-feed function

That's not viable. The dependency direction would become tangled, the
test surface would mix unit and integration concerns, and adding a
second deployment unit (the web app) would force an extraction
mid-stream.

## Decision

We adopt a **Clean Architecture multi-project layout** under `src/`:

| Project | Depends on | Contains |
| --- | --- | --- |
| `PinballWizard.Core` | (nothing) | Entities + value objects + domain interfaces. The pure domain. |
| `PinballWizard.Application` | `Core` | Use cases, orchestration, AI router (when added), repository interfaces, query services. |
| `PinballWizard.Infrastructure` | `Core` + `Application` | Concrete I/O — Stern scrapers, Playwright wiring, file downloader, Cosmos repositories (when added), AI Search clients (when added), Azure OpenAI clients (when added), HTTP clients to OPDB / Pinball Map / Match Play / IFPA (when added). |
| `PinballWizard.Cli` | `Core` + `Application` + `Infrastructure` | Entry point with DI wiring; `Program.cs`, `appsettings.json`. |

Subsequent additions follow the same pattern:

- `PinballWizard.Web` — depends on `Application`, eventually adds the
  Blazor Web App + ASP.NET Core API
- `PinballWizard.Functions` — depends on `Application` + `Infrastructure`,
  hosts the Cosmos Change Feed processor

Tests live under `tests/` mirroring the source structure. We allow a
single test project today (`PinballWizard.Scraper.Tests`) and split
per-layer if it gets unwieldy.

The dependency direction is enforced by project references — Core has
none, Application references only Core, Infrastructure references both.
Any code that wants to reach across the wrong direction (e.g., Core
referencing Infrastructure) is a design smell that the layout makes
impossible to compile.

## Consequences

**Positive:**
- **Clear seam between policy and mechanism.** Use cases are testable
  with mocked repositories; concrete I/O is testable separately.
- **Multiple deployment units share a single domain.** The CLI scraper,
  the web app, and the change-feed function all consume the same `Core`
  + `Application` abstractions. Domain logic is written once.
- **Mockability.** `IFileDownloader` (Application) is implemented by
  `FileDownloader` (Infrastructure). Tests mock the interface.
- **Future-proof.** When we add a Phase 2 deployment unit (web app or
  function), it slots into the existing layout instead of forcing a
  major refactor.
- **Portfolio-friendly.** Clean Architecture is a recognizable shape —
  reviewers can navigate the project without a map.

**Negative:**
- **More projects, more `.csproj` files, more `.slnx` entries.** Mostly
  a one-time cost.
- **Risk of cargo-culting.** Clean Architecture is overkill for a
  truly small project. We adopt it here because we know the project is
  growing into Phase 2 multi-host territory; for a project that will
  always be a single console, we'd resist the pivot.
- **`Core` project has nothing in it that depends on .NET-specific
  concepts.** This is by design but means the project will sometimes
  feel anemic. We resist the temptation to add infrastructure types
  there.

## Migration

The pivot from single-project to multi-project shipped as PR #20
(branch `Dev-CleanArchitecturePivot`) as a **structural-only PR** —
files moved, namespaces updated, no behavior change. Existing tests
(115 / 115 passing pre-pivot) continued to pass post-pivot, validating
the layout against the existing test surface before any new feature
code landed on it.

## Alternatives considered

- **Single project, internal namespace organization.** Rejected for
  the reasons above — fine for v1, not viable for the Phase 2 shape.
- **Vertical slice architecture (one project per feature).** Rejected
  — works well for medium-large applications with many use cases per
  bounded context, but our use cases are few and our infrastructure
  surface is large; horizontal slicing fits better.
- **Onion Architecture.** Effectively the same idea as Clean
  Architecture under a different name; we chose the Clean
  Architecture vocabulary because it's the more common reference point.

## References

- [`project_phased_build_sequence.md`](../../../../Users/JimKeeley/.claude/projects/c--earlybird-PinballWizard/memory/project_phased_build_sequence.md) — Phase 1.0 (Solution Scaffolding) describes this pivot.
- PR #20 — implementation.
