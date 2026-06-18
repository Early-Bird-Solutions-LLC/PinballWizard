# Contributing to PinballWizard

Thanks for your interest. PinballWizard is a small, focused project — a polite scraper for sternpinball.com that produces a deduplicated, fully-attributed catalog of every document the site publishes. The codebase is intentionally compact, and we try to keep it that way. Read this guide before opening a pull request.

## Local Setup

You need:

- [.NET 10 SDK](https://dotnet.microsoft.com/) (the repo is pinned via `global.json`)
- A working git client

```bash
git clone https://github.com/Early-Bird-Solutions-LLC/PinballWizard.git
cd PinballWizard
bash scripts/setup-hooks.sh    # activate the pre-push hook (one-time)
dotnet restore
dotnet build
dotnet test
```

`scripts/setup-hooks.sh` points your local git config at `.githooks/` and installs a `pre-push` hook that blocks direct pushes to `main` / `master` / `develop`. This is local enforcement of the branch-protection convention; the same rule should also be enabled server-side via GitHub branch protection. The hook is idempotent — safe to re-run.

The `GamePageScraper` and `ServiceBulletinScraper` use Playwright. After your first build, install the Chromium browser binary once:

```bash
dotnet run --project src/PinballWizard.Scraper -- --install-playwright
```

The scraper writes everything under `./data/` by default (downloads, metadata, logs). Set `DATA_PATH` to relocate it for Docker or local sandboxing.

## Running the Scraper

A few useful invocations while developing:

```bash
# Dry-run smoke test against the manuals page — discovers links, writes nothing.
dotnet run --project src/PinballWizard.Scraper -- --source manuals --scrape-only --dry-run

# See the current catalog state without scraping.
dotnet run --project src/PinballWizard.Scraper -- --status

# Scope to a single source while iterating on its scraper.
dotnet run --project src/PinballWizard.Scraper -- --source bulletins --scrape-only

# Reconcile the catalog against files on disk (clears stale `file` entries).
dotnet run --project src/PinballWizard.Scraper -- --build-catalog
```

The full CLI is documented in [README.md](README.md#cli-flags).

## Branch Naming

Team convention: `Dev-PascalCaseDescription`. No ticket prefix is required for this repo — it's not in our Jira tracker.

```
Dev-FixServiceBulletinPagination
Dev-AddPdfPageCountExtraction
Dev-EnterpriseQuality
```

Never commit directly to `main`. The CI pipeline and branch-protection rules will reject it; the `branch-guard` hook will too if you have it installed.

## Commit Format

We use a lightweight conventional-commit style — no colon, no co-author lines.

```
<type>(<scope>) short imperative summary

A paragraph (or two) explaining WHY this change is needed. The diff
already shows what; the message should explain the reasoning, the
trade-offs, and any context a future reader will want.
```

Valid `<type>` values: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`. The `<scope>` is a short module name (`scraper`, `catalog`, `downloader`, `http`, etc.) — never a ticket ID.

Recent examples from the history:

```
refactor(http) replace hand-rolled retry with Microsoft resilience pipeline
feat(catalog) link manuals to known games by filename slug
feat(downloader) add HTTP retry with exponential backoff
docs add HTTP resilience research and recommendation
chore initial scraper, docs, and tests for Pinball Wizard Phase 1
```

Keep the first line at 72 characters or fewer. Always include a body paragraph. Don't add `Co-Authored-By` trailers.

## Quality Bar

The repository enforces a strict quality bar:

- `TreatWarningsAsErrors=true` — every analyzer warning is a build break
- `latest-recommended` .NET analyzer rule set
- Locked-mode NuGet restore (`packages.lock.json` files are checked in)
- Tests must pass; CodeQL must be green; the project must build clean

Full details, including the exception list and the rationale behind each rule, live in [`docs/quality-bar.md`](docs/quality-bar.md).

If your change requires suppressing a rule, justify the suppression in `Directory.Build.props` with a comment and a removal criterion. Don't bury it in `#pragma warning disable` blocks.

## Test suites

`dotnet test` runs the whole suite. Tests live in [per-layer projects](README.md) (ADR-0030); the browser-driven Web tests carry an xUnit `Category` trait so you can scope a run with `--filter`.

The browser-driven categories need the Chromium binary installed once after a build:

```bash
pwsh tests/PinballWizard.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
```

| Category | What it covers | Needs | In PR CI? |
| --- | --- | --- | --- |
| _(untagged)_ | unit + bUnit component tests | nothing | yes (Build/test/coverage) |
| `Accessibility` | axe WCAG 2.1 AA over public **and** admin pages (SSR HTML, in-process) | Chromium | yes (UI-tests job) |
| `Circuit` | a real in-process Blazor circuit — proves the admin interactive controls respond (what bUnit can't show) | Chromium + a Web-project build (for the static-asset manifest) | yes (UI-tests job) |
| `Snapshots` | responsive-layout snapshot checks | Chromium | yes (UI-tests job) |
| `E2E` | full browser → real app → **live Azure** (Cosmos / AI Search / Foundry); one real model call per ask | a live/deployed stack + `az login` — **skipped by default** | no — runs in the scheduled canary |

Scope a category locally, e.g. `dotnet test --filter "Category=Circuit"`.

**Why E2E skips by default.** `E2EFactAttribute` runs the `E2E` tests only when the suite is pointed at a real stack — either `E2E__BaseUrl` (a deployed target) or all of `Cosmos__AccountEndpoint` / `AiSearch__Endpoint` / `AiFoundry__ProjectEndpoint` (local spawn). With neither set, a bare `dotnet test` reports them **Skipped** by design: they need live Azure and each ask costs a real model call. To run them locally:

```bash
az login                       # authenticate to the pinwiz dev stack
pwsh tools/e2e/Run-E2E.ps1     # auto-discovers endpoints, installs Chromium, runs Category=E2E
```

PR CI excludes `Category=E2E`; the deployed canary ([`.github/workflows/canary.yml`](.github/workflows/canary.yml)) runs the same four every 6 h against the live FQDN.

## Pull Requests

Every PR description must contain three sections:

- **Summary** — what changed, why, and which user-visible behaviour is affected
- **Test Plan** — what you ran, what you observed, and (for any new behaviour) the test that protects it
- **Out-of-Scope** — anything explicitly *not* addressed by this change, so reviewers don't expect it

CI must be green before review. We don't allow self-approval — GitHub blocks it on this repo, and the policy stands either way.

Small, focused PRs are easier to review and easier to revert. If a change touches more than ~400 lines, consider splitting it.

## Reporting Issues

Open an issue at <https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues> with:

- **Steps to reproduce** — exact CLI invocation, configuration, environment
- **Expected behaviour**
- **Actual behaviour** — including the relevant portion of the log output

Bugs that touch live-site scraping are best reported with the sternpinball.com URL or game slug that triggered the problem; the catalog and provenance trail in `data/metadata/catalog.json` is usually the fastest way to triangulate.

## Questions

Architecture decisions and current state are documented in [`CLAUDE.md`](CLAUDE.md) and the `docs/` directory. If something there is wrong or unclear, that's a documentation bug — please file it.
