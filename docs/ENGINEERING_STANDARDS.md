# Engineering Standards — PinballWizard

> **Status: HISTORICAL (Phase 1 design draft).** This document captured the engineering standards as drafted during Phase 1 when PinballWizard was a single-container Docker scraper. The same engineering bar still applies — it just lives elsewhere now: [`docs/vision.md`](vision.md) records the showcase posture, [`docs/guardrails.md`](guardrails.md) records the locked rules (polite scraping, provenance, personal-identity-only, etc.), [`docs/build-spec.md`](build-spec.md) records the per-phase scope and exit criteria, [`docs/quality-spec.md`](quality-spec.md) records the per-phase quality gates, and [`docs/decision-log.md`](decision-log.md) records sub-ADR decisions. ADRs under [`docs/adr/`](adr/) capture architectural decisions with significant trade-offs. **For current canonical guidance, read those files instead of this one** — they reflect the Phase 2 architecture pivot to Azure Container Apps + Cosmos + AI Search and the showcase quality bar that supersedes the Phase 1 framing below.

---

> This document defines the engineering standards for PinballWizard. While this is a personal hobby project, the codebase doubles as a public portfolio piece. Every choice should hold up to the kind of scrutiny a senior engineer at a prospective client would apply when reviewing the repository on GitHub.
>
> **The bar:** a reviewer who clones this repo on a fresh machine should see that (1) it works on the first try, (2) the code reads cleanly with consistent conventions, (3) the testing is real and meaningful, and (4) operational concerns — logging, errors, configuration, security, observability — have been thought through. Nothing here is enterprise-for-its-own-sake. Every standard exists to serve clarity, correctness, or reviewer confidence.

---

## 0. Guiding Principles

1. **Match the standard to the scale.** This is a single-container scraper that talks to one website and produces JSON. It is not a distributed platform. We adopt enterprise *disciplines* (testing, observability, resilience, security hygiene) but we do not adopt enterprise *machinery* (service mesh, event bus, microservices) where it would be inappropriate. Knowing the difference is itself a senior engineering signal.
2. **Code is read more than it is written.** Optimize for the reviewer, not the author. If a clever line saves five lines of code but costs the reader thirty seconds of comprehension, it is not clever.
3. **Make the right thing the easy thing.** Lint rules, analyzers, `Directory.Build.props`, CI checks, and templates should make it impossible (or at least uncomfortable) to deviate from the standards in this document. Discipline that depends on remembering is discipline that fails.
4. **Be a polite citizen.** PinballWizard scrapes a third-party site. Rate limiting, conditional requests, identifying user agents, respecting `robots.txt`, and graceful failure are not nice-to-haves — they are baseline professional ethics.
5. **The catalog is a contract.** `catalog.json` is the API boundary between Phase 1 (this scraper) and Phase 2 (RAG). Changes to its schema are versioned, documented, and tested.

---

## 1. C# / .NET Code Standards

### 1.1 Language and project file settings

Every `.csproj` (or `Directory.Build.props` once central) sets the following at minimum:

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <LangVersion>latest</LangVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <WarningsAsErrors />
  <WarningsNotAsErrors />
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <AnalysisMode>AllEnabledByDefault</AnalysisMode>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);CS1591</NoWarn> <!-- missing XML doc on internal types -->
</PropertyGroup>
```

Justification for each in the README's Architecture section so reviewers see we made these decisions deliberately.

### 1.2 Style and formatting

- A `.editorconfig` at the repository root governs all formatting. Anything the editor doesn't auto-fix must fail CI.
- File-scoped namespaces (`namespace Foo.Bar;`) — never block-scoped.
- One top-level type per file. Filename matches type name.
- `using` directives outside the namespace, sorted, with `System.*` first.
- `var` only when the right-hand side makes the type obvious. Otherwise the explicit type. No religion either way — readability decides.
- `internal sealed class` is the default. `public` is opt-in (anything in a public surface area must be intentional). `sealed` is opt-out (only unseal when inheritance is genuinely required).
- Records for DTOs and value-like types. Classes for entities with identity or behavior.
- Primary constructors are fine for DTOs and simple services; avoid them when the constructor needs validation, ordering, or non-trivial logic.
- No regions. Ever.

### 1.3 Naming

- `PascalCase` for types, methods, properties, constants, public fields.
- `camelCase` for locals and parameters.
- `_camelCase` for private fields. No Hungarian notation, no `m_`.
- Async methods end in `Async`. The only exception is event handlers and `Main`.
- Acronyms over two letters are PascalCased: `HtmlParser`, not `HTMLParser`. `Id`, not `ID`. `Url`, not `URL`.
- Avoid abbreviations except universally understood ones (`Url`, `Id`, `Db`, `Http`).

### 1.4 Async, cancellation, and I/O

- Every public method that performs I/O is async and accepts a `CancellationToken` as its **last parameter**, with no default value on internal APIs and `default` only on public top-level entry points.
- `CancellationToken` is threaded through every call. Never `Task.Wait()`, `.Result`, `.GetAwaiter().GetResult()` outside of explicit `Main` bootstrapping.
- `ConfigureAwait` is unnecessary in this app (no sync context), but document the choice in `ENGINEERING_STANDARDS.md` so reviewers don't flag it.
- Use `IAsyncEnumerable<T>` for streams of results (e.g., scraper yields documents one at a time). Use `ValueTask` only on hot paths where it's been measured to matter.
- Long-running scrapes must be cancellable. The `IHostApplicationLifetime.ApplicationStopping` token is propagated to every scraper.

### 1.5 Error handling

- **Exceptions are for exceptional conditions.** Expected outcomes (a page returns 404, a download is unchanged, a selector is missing) are modeled as return values: `Result<T>`, discriminated unions (sealed type hierarchies or `OneOf`), or domain-specific records like `DownloadOutcome.Unchanged | DownloadOutcome.Updated | DownloadOutcome.Failed`.
- Catch `Exception` only at well-defined boundaries: the top of a scraper run, the top of a CLI command. Log with full stack trace, fail the unit of work, continue the larger run if appropriate.
- Never catch and swallow. Never `catch { }`. Never re-throw with `throw ex` (use bare `throw`).
- Custom exceptions only when callers need to programmatically differentiate. They must inherit from a domain base type (e.g., `PinballWizardException`).
- No exceptions for control flow. Ever.

### 1.6 Disposal and resource management

- `using` declarations (`using var foo = ...`) over `using` blocks where the scope is the rest of the method.
- Anything implementing `IDisposable` or `IAsyncDisposable` is wrapped at the point of acquisition. No "I'll dispose this later."
- Long-lived resources (Playwright browser, HttpClient) are owned by the DI container with `IAsyncDisposable` shutdown handled by the host.

---

## 2. Project Structure

### 2.1 Solution layout

```
PinballWizard/
├── .editorconfig
├── .gitignore
├── .gitattributes
├── global.json                    # Pinned SDK version
├── Directory.Build.props          # Shared project properties
├── Directory.Packages.props       # Central Package Management
├── PinballWizard.sln
├── README.md
├── LICENSE
├── CONTRIBUTING.md
├── SECURITY.md
├── CHANGELOG.md
├── Dockerfile
├── docker-compose.yml
├── crontab
│
├── .github/
│   ├── workflows/
│   │   ├── ci.yml
│   │   ├── codeql.yml
│   │   └── docker.yml
│   ├── dependabot.yml
│   ├── pull_request_template.md
│   └── ISSUE_TEMPLATE/
│
├── docs/
│   ├── architecture.md
│   ├── data-model.md
│   ├── operations.md
│   ├── adr/                       # Architecture Decision Records
│   │   ├── 0001-record-architecture-decisions.md
│   │   ├── 0002-deterministic-document-ids.md
│   │   └── ...
│   └── diagrams/
│
├── src/
│   └── PinballWizard/
│       ├── PinballWizard.csproj
│       ├── Program.cs
│       └── ...
│
└── tests/
    ├── PinballWizard.UnitTests/
    └── PinballWizard.IntegrationTests/
```

### 2.2 Central Package Management

`Directory.Packages.props` lists every package version exactly once. `.csproj` files reference packages by name only. This is non-negotiable — version drift across projects is an immediate red flag in a review.

### 2.3 Pinned SDK

`global.json` pins the SDK so a fresh clone produces an identical build:

```json
{
  "sdk": {
    "version": "9.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

### 2.4 One responsibility per project

Resist the urge to over-split. PinballWizard is small enough that one application project plus two test projects (unit + integration) is correct. Splitting `Scrapers` and `Provenance` into separate libraries to "look enterprise" would be cargo culting.

---

## 3. Dependency Injection and Composition

- All services are registered in `Program.cs` (or extension methods on `IServiceCollection` if it grows past ~30 lines).
- `AddHttpClient<T>()` for any class that uses `HttpClient`. Configure named clients with resilience pipelines.
- Singletons are explicitly justified in a comment. Default is scoped (or transient where ownership is unclear).
- No service locator pattern. No `IServiceProvider` injected into business logic.
- The composition root is the only place that knows concrete types. Scrapers depend on interfaces.
- `IOptions<T>` for configuration, never bare `IConfiguration` outside `Program.cs`.

---

## 4. Configuration

### 4.1 Strongly-typed options

Every config section is bound to a record with validation:

```csharp
public sealed record ScraperOptions
{
    public const string SectionName = "Scraper";

    [Required, Url]
    public required string BaseUrl { get; init; }

    [Range(1, 60_000)]
    public int RequestDelayMs { get; init; } = 2_000;

    [Range(1, 16)]
    public int MaxConcurrentDownloads { get; init; } = 3;

    [Required]
    public required string DataPath { get; init; }
}
```

Registration uses `AddOptionsWithValidateOnStart`:

```csharp
services
    .AddOptions<ScraperOptions>()
    .Bind(configuration.GetSection(ScraperOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

### 4.2 Configuration sources, in order

1. `appsettings.json` (committed, contains defaults)
2. `appsettings.{Environment}.json` (committed for `Development`, `Production`)
3. Environment variables (highest precedence — Docker uses these)
4. User secrets in `Development` (never committed)

No secrets in any committed file — ever. CI verifies this with a pre-commit secret scan.

---

## 5. Logging and Observability

### 5.1 Structured logging with Serilog

- Serilog with `Serilog.Extensions.Hosting` and `Serilog.Settings.Configuration`.
- Sinks: Console (always), Seq (optional, controlled by config), File (rolling, retention 30 days).
- All log statements use **structured properties**, not interpolated strings:
  - Good: `_logger.LogInformation("Downloaded {Filename} ({Bytes} bytes) in {ElapsedMs}ms", filename, bytes, ms);`
  - Bad: `_logger.LogInformation($"Downloaded {filename} ({bytes} bytes) in {ms}ms");`
- High-frequency log statements use **source-generated logging** for zero allocations:
  ```csharp
  [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
      Message = "Downloaded {Filename} ({Bytes} bytes) in {ElapsedMs}ms")]
  public static partial void LogDownloadCompleted(
      this ILogger logger, string filename, long bytes, long elapsedMs);
  ```
- Log levels:
  - `Trace` / `Debug`: developer noise. Off by default in production.
  - `Information`: meaningful state transitions (run started, source completed, file downloaded). Should average less than one entry per second under load.
  - `Warning`: degraded but recoverable (selector missed, retry succeeded after failure).
  - `Error`: unit of work failed but the run continues.
  - `Critical`: the run cannot continue.
- Correlation IDs (`RunId`) are pushed to `LogContext` at the top of every scraper run so all logs from a run can be filtered.

### 5.2 OpenTelemetry

- Traces and metrics through OpenTelemetry, exported to Console in development and OTLP in production.
- `ActivitySource` per logical component (`PinballWizard.Scrapers`, `PinballWizard.Downloads`, `PinballWizard.Catalog`).
- Custom metrics (counters and histograms) for: documents discovered, files downloaded, bytes downloaded, conditional-request hit rate, error counts by type.
- Health checks via `Microsoft.Extensions.Diagnostics.HealthChecks` exposed on a minimal HTTP endpoint inside the container (or stdout for the cron model).

### 5.3 What never goes in a log

URLs that contain credentials. File contents. Stack traces sent to anywhere except the file/console sink. Personally identifying information (irrelevant for this scraper, but include the rule for muscle memory).

---

## 6. Resilience

### 6.1 HTTP resilience pipeline

Use `Microsoft.Extensions.Http.Resilience` (Polly v8). Every named `HttpClient` gets a standard resilience handler:

- Retry: 3 attempts with exponential backoff (250ms, 500ms, 1s) + decorrelated jitter.
- Retry only on transient failures: 5xx, 408, 429 (with `Retry-After` honored), `HttpRequestException`, `TaskCanceledException` from a non-user token.
- Per-attempt timeout: 30 seconds.
- Total request timeout: 2 minutes.
- Circuit breaker: 50% failure ratio over 30 seconds opens for 30 seconds.
- A bulkhead caps concurrent outbound requests at the configured `MaxConcurrentDownloads`.

### 6.2 Scraper-level resilience

- A single failing source does not fail the run. Exceptions are caught at the source boundary, logged, recorded in the run summary, and the next source proceeds.
- A run that fails to write `catalog.json` exits non-zero so cron sends mail. Partial state is written to `catalog.json.tmp` and atomically renamed only on success.
- All file writes go through an atomic-write helper: write to `*.tmp`, fsync, rename.

### 6.3 Be polite

- A descriptive User-Agent: `PinballWizard/{version} (+https://github.com/jim/pinball-wizard)` — links back to the repo so the site owner can identify and contact us.
- Respect `robots.txt`. Parse it on first run, cache for the duration of the run.
- Conditional requests (`If-None-Match`, `If-Modified-Since`) on every re-fetch.
- A floor on `RequestDelayMs` between requests to the same host. Configurable, never zero.
- Backoff on 429 honors `Retry-After`. Three consecutive 429s aborts the source.

---

## 7. Testing

### 7.1 What we test

- **Unit tests** cover all logic that has branches, transforms, or invariants. Pure functions, parsers, the `DocumentRecord` ID hashing, the `FileOrganizer` URL-to-path mapping, the `CatalogBuilder` deduplication and cross-referencing, change-detection diffing.
- **Integration tests** cover the seam between code and the outside world using fixtures: HTML fixtures from sternpinball.com (saved offline, never live), a `WireMock.Net` server for HTTP, a temp directory for file I/O. Playwright tests use saved HTML fixtures via `page.SetContentAsync`.
- **Live smoke tests** are a separate marked category (`[Trait("Category", "Live")]`) excluded from CI by default. They hit the real site and verify a single document is discoverable end-to-end. Run manually before a release.

### 7.2 What we don't test

- DI registration (the runtime catches this).
- Trivial getters/setters on records.
- Generated code.

### 7.3 Conventions

- xUnit. NSubstitute for mocking. FluentAssertions for assertions. Bogus for fake data.
- Test naming: `MethodUnderTest_Scenario_ExpectedBehavior`. Example: `BuildDocumentId_SameUrlDifferentCase_ProducesSameId`.
- Arrange / Act / Assert with blank lines separating sections. No comments labeling them — the structure is the documentation.
- One logical assertion per test. `Should().BeEquivalentTo` counts as one.
- Test classes mirror source structure: `src/PinballWizard/Provenance/CatalogBuilder.cs` ↔ `tests/PinballWizard.UnitTests/Provenance/CatalogBuilderTests.cs`.
- No `Thread.Sleep` in tests. Use `TimeProvider` (abstract `DateTimeOffset.UtcNow` and timers behind `TimeProvider.System`).

### 7.4 Coverage

- Coverage is collected with Coverlet, reported to Codecov, and shown as a badge in the README.
- The coverage *number* is not a goal — coverage is a smoke detector, not a thermometer. Aim for the kind of coverage where adding a meaningful test is hard because the meaningful tests are already there. In practice this is 70–85% on this kind of project.
- The CI does not fail on coverage thresholds. It fails if total coverage *drops* by more than 2 percentage points from the main branch.

### 7.5 Determinism

- All tests are deterministic. No random data without a seeded `Random`. No real network. No real filesystem outside of `Path.GetTempPath()`.
- Tests run in parallel. Anything that uses shared state (the temp directory, environment variables) is in a serial collection.

---

## 8. Security

### 8.1 Secrets

- No secrets in source. No secrets in `appsettings.json`. No secrets in `Dockerfile`. No secrets in CI logs.
- `gitleaks` runs in CI on every PR and rejects commits that contain anything matching a credential pattern.
- The container reads runtime configuration only from environment variables and mounted config — never from baked-in files.

### 8.2 Dependencies

- `dotnet list package --vulnerable --include-transitive` runs in CI. Any vulnerability of severity High or Critical fails the build.
- Dependabot updates dependencies weekly. Major version bumps require an ADR.
- License scanning verifies no GPL/AGPL transitives sneak in (we'll publish under MIT).

### 8.3 Container

- Multi-stage Dockerfile. The final stage uses `mcr.microsoft.com/dotnet/runtime-deps:9.0-noble-chiseled` (or equivalent minimal Microsoft-hardened image).
- Runs as a non-root user (`USER 1000`).
- No shell access in the final image (chiseled images are shell-less).
- Healthcheck defined.
- Image is scanned by Trivy in CI; High/Critical CVEs fail the build.
- Tags follow `pinball-wizard:{semver}` and `pinball-wizard:sha-{git-sha}`.

### 8.4 Supply chain

- `global.json` pins the SDK.
- `Directory.Packages.props` pins all package versions.
- The CI workflow uses pinned action SHAs, not tags (`actions/checkout@v4` becomes `actions/checkout@<sha>`).

---

## 9. Performance

Performance work is justified by measurement, not vibes. That said, the following defaults cost nothing and are expected:

- All I/O is async with `CancellationToken`.
- Files are streamed, not buffered into memory. `HttpResponseMessage.Content.CopyToAsync(fileStream)` for downloads. SHA-256 is computed in a `CryptoStream` chained to the disk write — single pass, constant memory.
- `IAsyncEnumerable<DocumentRecord>` from scrapers, consumed lazily by the orchestrator.
- JSON serialization uses `System.Text.Json` with source generators (`JsonSerializerContext`) for the catalog types — zero reflection, fast cold start.
- The catalog build is incremental: existing `catalog.json` is loaded, the run's discoveries are merged, and the result is rewritten. We do not re-hash files we already have unchanged ETags for.

Benchmarks (BenchmarkDotNet) live in a `tests/PinballWizard.Benchmarks/` project, separate from unit tests. They are not run in CI but are committed so reviewers can see we measure.

---

## 10. Documentation

### 10.1 README.md

The README is the storefront. It must include, in this order:

1. One-paragraph project description.
2. Status badges (CI, coverage, latest release, license).
3. A 30-second quickstart that actually works on a fresh clone (`git clone && docker compose up`).
4. An architecture diagram (Mermaid, rendered inline by GitHub).
5. The provenance model, illustrated with a real `DocumentRecord`.
6. Operating instructions (CLI flags, environment variables, scheduled-run model).
7. Roadmap (Phase 1 status, Phase 2 preview).
8. Links to deeper docs in `docs/`.
9. License.

### 10.2 Architecture Decision Records

Every significant decision gets an ADR in `docs/adr/`. Use the [Nygard format](https://www.cognitect.com/blog/2011/11/15/documenting-architecture-decisions). Examples for this project:

- ADR 0001: Record architecture decisions
- ADR 0002: Deterministic document IDs derived from canonical file URL
- ADR 0003: Playwright over Puppeteer-Sharp for Vue.js pages
- ADR 0004: catalog.json as the Phase 1 ↔ Phase 2 contract
- ADR 0005: Standalone Azure infrastructure (no shared resources with any other project)
- ADR 0006: Conditional GETs over content hashing for change detection

ADRs are immutable once accepted. New decisions supersede old ones with a new ADR that links back.

### 10.3 XML doc comments

- All `public` types and members get `///` summary comments.
- `internal` types get them only when they would benefit a future maintainer (the parser, the catalog merge logic).
- Doc comments describe *intent and contract*, not implementation. "Returns the canonical document ID" — yes. "Calls `SHA256.HashData` on the lowercased URL" — no.

### 10.4 Diagrams

- Architecture diagrams use Mermaid in markdown. They render natively on GitHub and edit cleanly in PRs (no binary files).
- The C4 model (System Context, Container, Component) is overkill for this project — a single Container diagram in the README is sufficient.

---

## 11. CI/CD (GitHub Actions)

### 11.1 Required workflows

- **`ci.yml`** runs on every push and PR to `main`:
  - Checkout, setup .NET (from `global.json`), restore, build with `--no-restore`, test with coverage.
  - `dotnet format --verify-no-changes` — formatting violations fail the build.
  - `dotnet list package --vulnerable --include-transitive` — vulnerable packages fail the build.
  - `gitleaks detect` — leaked secrets fail the build.
  - Upload coverage to Codecov.
  - All jobs run on `ubuntu-latest` and complete in under 5 minutes for the project at this size.

- **`codeql.yml`** runs CodeQL static analysis on PRs and weekly.

- **`docker.yml`** runs on tags matching `v*.*.*`:
  - Build the multi-stage image.
  - Scan with Trivy.
  - Push to GHCR (`ghcr.io/jim/pinball-wizard:{tag}`).
  - Sign the image with cosign (keyless OIDC).
  - Generate SBOM with Syft, attach to the release.

### 11.2 Branch protection

`main` is protected:
- Require PR with at least one approving review (or self-approval if solo, but configured so it could be turned on).
- Require status checks to pass: `ci`, `codeql`.
- Require linear history.
- Require signed commits.
- No force pushes, no deletions.

### 11.3 Releases

- Semantic versioning. `v0.x.x` until the catalog schema is considered stable.
- Releases are cut from `main` via tag. `release-please` or `changesets` automates the changelog and version bump.
- Release notes are auto-generated from conventional commits and edited for human readability before publishing.

---

## 12. Containerization

### 12.1 Dockerfile principles

- Multi-stage build with a clear separation between SDK (build) and runtime (deploy).
- Final image is chiseled or distroless. No shell, no package manager, no extras.
- Non-root user with a known UID/GID.
- `HEALTHCHECK` defined and exits zero only when the app is genuinely ready.
- Layers ordered for cache efficiency: dependencies first, source last.
- `.dockerignore` matches `.gitignore` plus `bin/`, `obj/`, `node_modules/`, `data/`, `*.md`.
- Build args for version pinning (`ARG DOTNET_VERSION=9.0`).
- `LABEL` directives include `org.opencontainers.image.source`, `revision`, `created`, `licenses`, `description`.

### 12.2 Image size

The runtime image should be under 200MB. If it isn't, something is wrong (a forgotten dev dependency, an SDK image used by mistake).

### 12.3 Playwright

Playwright in Docker requires the Chromium dependencies to be present. The chiseled .NET image does not include them, so Playwright either:
- (a) installs into a separate, larger image just for the scraper container, with a justification ADR, or
- (b) uses Playwright's own Docker image as the base for the runtime stage.

Option (b) is cleaner for this project. ADR 0007 records that decision.

---

## 13. Repository Hygiene

The following files exist at the repository root, with non-trivial content:

| File | Purpose |
|---|---|
| `README.md` | The storefront. See §10.1. |
| `LICENSE` | MIT. |
| `CONTRIBUTING.md` | How to set up a dev environment, run tests, submit PRs, the commit convention. |
| `SECURITY.md` | How to report a vulnerability. Even a hobby project should have this — it signals professionalism. |
| `CHANGELOG.md` | Auto-generated, human-edited. Follows [Keep a Changelog](https://keepachangelog.com). |
| `.gitignore` | Standard .NET template plus `data/`, `*.local.json`, `.vscode/settings.json`. |
| `.gitattributes` | Line endings: `* text=auto eol=lf`. Lockfiles marked `linguist-generated`. |
| `.editorconfig` | Formatting rules. The single source of truth. |
| `Directory.Build.props` | Shared C# project properties (§1.1). |
| `Directory.Packages.props` | Central Package Management (§2.2). |
| `global.json` | Pinned SDK (§2.3). |

GitHub-specific files in `.github/`:

| File | Purpose |
|---|---|
| `pull_request_template.md` | Checklist: tests added, docs updated, ADR added if needed. |
| `ISSUE_TEMPLATE/bug_report.md` | Reproducible-bug template. |
| `ISSUE_TEMPLATE/feature_request.md` | Why-then-what template. |
| `dependabot.yml` | Weekly updates for nuget, github-actions, docker. |
| `CODEOWNERS` | Even with one owner, this is good practice. |

---

## 14. Commit and PR Conventions

### 14.1 Conventional Commits

Every commit message follows [Conventional Commits 1.0](https://www.conventionalcommits.org):

```
feat(scrapers): add ServiceBulletinScraper with scroll-to-load handling

Implements bulletin discovery against /support/service-bulletins/.
Uses Playwright with explicit scroll loop and idle detection.

Closes #42
```

Allowed types: `feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`. A `commitlint` GitHub Action enforces this on PRs.

### 14.2 PR discipline

- One logical change per PR. "And while I was in there..." goes in a separate PR.
- The PR description fills in the template: motivation, changes, testing, screenshots if UI is involved, breaking changes (none expected pre-1.0).
- The PR title is a Conventional Commit. The merge strategy is squash, so the PR title becomes the commit message on `main`.
- No merge commits on `main`. Linear history.

### 14.3 Code review (for the future, even if currently solo)

- Review the diff in the GitHub UI, not the IDE — same view a stranger would have.
- "Nit:" prefix for non-blocking style preferences. Anything else is a request for change.
- Approve only when you'd be comfortable maintaining the code yourself.

---

## 15. Definition of Done

A feature is *done* when:

- [ ] The code compiles with no warnings.
- [ ] All new code is covered by tests at the level appropriate to its risk (pure logic: unit; I/O: integration with fixtures).
- [ ] `dotnet format` produces no diff.
- [ ] Public API has XML doc comments.
- [ ] Logging is in place for the success path and the failure paths.
- [ ] Configuration is bound through `IOptions<T>` with validation.
- [ ] The README, architecture doc, or relevant ADR is updated if the change affects them.
- [ ] A CHANGELOG entry exists (auto-generated from the commit message is fine).
- [ ] CI passes, including coverage delta and vulnerability scan.
- [ ] The author has manually tested the happy path against the live site (or a fixture if live testing isn't appropriate).

---

## 16. Things We Are Deliberately Not Doing

A portfolio piece communicates judgment as much as skill. The following are intentional non-goals, and the README's Architecture section names them so reviewers know we considered them:

- **Microservices.** This is one container that runs on a schedule.
- **Event sourcing / CQRS.** The data model is a JSON file. Adding event sourcing here would be malpractice.
- **Distributed tracing across services.** There is one service. OpenTelemetry traces are still useful within the run, but cross-service tracing is not in scope.
- **A custom DSL for scraper definitions.** Three sources, three classes. Not enough to justify abstraction.
- **A UI.** The output is `catalog.json`. Phase 2 adds a UI; Phase 1 does not.
- **Shared infrastructure with any other project.** Deliberate boundary — this project gets its own resource group and lifecycle. ADR 0005 documents the reasoning.

---

## 17. Living Document

This document is versioned with the code. Pull requests that change the standards must update this file in the same PR. The standards in effect at any commit are the standards in this file at that commit. Disagreements with the standards are resolved by ADR, not by PR review comments.
