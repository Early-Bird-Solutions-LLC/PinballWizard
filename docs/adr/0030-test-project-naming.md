# 0030 — Test project naming and structure conventions

**Status:** Accepted
**Date:** 2026-05-29

## Context

PinballWizard had two test projects with names that did not reflect the
production code they exercised:

- `PinballWizard.Scraper.Tests` — tested Core, Application, *and*
  Infrastructure together under a name tied to Phase 1's scraper-centric
  mental model.
- `PinballWizard.Web.Tests` — tested both `PinballWizard.Api` and
  `PinballWizard.Web` in a single project.

As the solution grew to ten production projects the mismatch became
load-bearing: it was unclear which test project covered a given production
class, new contributors had no obvious home for a test they were writing,
and the CI coverage report attributed coverage to the wrong logical layer.

Three additional production projects (`PinballWizard.Cli`,
`PinballWizard.ServiceDefaults`, and the Aspire projects) had no test
coverage at all, with no documented rationale for whether that was
intentional.

## Decision

### 1. One test project per production project, named with a type suffix

| Test type | Suffix | Example |
|---|---|---|
| Unit tests | `.Tests` | `PinballWizard.Core.Tests` |
| Integration tests | `.IntegrationTests` | `PinballWizard.Infrastructure.IntegrationTests` |
| End-to-end tests | `.E2ETests` | `PinballWizard.Web.E2ETests` |

The production project name is always the prefix. A test project named
`PinballWizard.Application.Tests` tests `PinballWizard.Application` — nothing
else. A test project named `PinballWizard.Infrastructure.IntegrationTests`
runs tests that require live external dependencies (Cosmos emulator, Azure
AI Search, etc.) and is allowed to reference multiple production projects.

### 2. Unit vs integration vs E2E boundary

**Unit (`.Tests`):** No real I/O. All external dependencies are substituted
(NSubstitute). Tests run in under 10 ms each. No network, no file system, no
database. The Cosmos emulator does not start.

**Integration (`.IntegrationTests`):** Requires at least one real external
dependency — the Aspire Cosmos emulator, Azurite, or a live Azure resource.
Tests may be slower. A project at this level typically references multiple
production projects (e.g., `Infrastructure` + `Application`) because it is
testing the seam between them.

**E2E (`.E2ETests`):** Browser-driven or full-stack. Playwright navigates a
running application. Tests may take seconds each. The current
`PinballWizard.Web.Tests` accessibility tests (`A11y/`) are the canonical
example.

### 3. Which production projects warrant test projects

Create a test project when the production project contains logic:
conditional branching, data transformation, a non-trivial algorithm, or an
error-handling path. Skip when the project is purely composition —
`Program.cs` wiring, `AppHost` orchestration, WASM bootstrap.

Current mapping:

| Production project | Test project | Rationale |
|---|---|---|
| `PinballWizard.Core` | `PinballWizard.Core.Tests` | Domain models, value objects |
| `PinballWizard.Application` | `PinballWizard.Application.Tests` | Orchestration, RAG pipeline, linking, AI routing |
| `PinballWizard.Infrastructure` | `PinballWizard.Infrastructure.Tests` | Scrapers, Cosmos repositories, AI providers |
| `PinballWizard.Api` | `PinballWizard.Api.Tests` | Endpoint middleware, problem details, SSE streaming |
| `PinballWizard.Web` | `PinballWizard.Web.Tests` | Blazor components (bUnit), accessibility (Playwright + axe-core) |
| `PinballWizard.Cli` | `PinballWizard.Cli.Tests` | Command dispatch, option contract, DI resolution |
| `PinballWizard.ServiceDefaults` | `PinballWizard.ServiceDefaults.Tests` | Resilience pipeline config, health check endpoints |
| `PinballWizard.RagIngestionWorker` | — | Pure DI composition; no business logic |
| `PinballWizard.Web.Client` | — | WASM bootstrap only |
| `PinballWizard.AppHost` | — | Aspire orchestration; no logic |

### 4. Coverage measurement

Entry-point / composition projects (`PinballWizard.Cli`,
`PinballWizard.RagIngestionWorker`) are excluded from the coverage gate in
`tests/coverage.runsettings`. Coverage of `Program.cs`-style DI wiring is
not meaningful and would penalise the gate without signalling real quality.
The exclusion must be explicit (named pattern in the `<Exclude>` block) and
accompanied by a comment explaining why.

### 5. Solution file organisation

All test projects live under the `/Tests/` solution folder in
`PinballWizard.slnx`. The folder order mirrors the Clean Architecture
dependency graph: Core → Application → Infrastructure → Api → Web →
Cli → ServiceDefaults.

## Consequences

- A contributor looking for tests for `PinballWizard.Application.Linking`
  goes to `tests/PinballWizard.Application.Tests/Linking/` — no ambiguity.
- Adding a new production project requires a conscious decision: create a
  test project (and document why), or explicitly skip (and document why).
  The mapping table in § 3 is the record of that decision.
- The CI coverage report attributes coverage to the correct assembly,
  making the per-project line rates in the threshold gate meaningful.
- Integration tests that cross layer boundaries (e.g., scraper → Cosmos
  round-trip) belong in `PinballWizard.Infrastructure.IntegrationTests`
  when that project is created; they should not be mixed into the unit
  `.Tests` project.
