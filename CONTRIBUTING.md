# Contributing to PinballWizard

This guide covers local development setup, test conventions, and the PR workflow for PinballWizard.

Thanks for your interest. PinballWizard is a customer-facing showcase demonstrating enterprise-class AI solution architecture — a polite, multi-manufacturer scraper with a source-cited RAG Q&A layer (Blazor front end, .NET Aspire, Azure Cosmos / AI Search / OpenAI, Clean Architecture). Read this guide before opening a pull request.

## Local Setup

You need:

- [.NET 10 SDK](https://dotnet.microsoft.com/) (the repo is pinned via `global.json`)
- Docker Desktop (for the Cosmos preview emulator and Azurite storage emulator)
- A working git client

```bash
git clone https://github.com/Early-Bird-Solutions-LLC/PinballWizard.git
cd PinballWizard
bash scripts/setup-hooks.sh    # activate the pre-push hook (one-time)
dotnet restore
dotnet build
```

`scripts/setup-hooks.sh` points your local git config at `.githooks/` and installs a `pre-push` hook that blocks direct pushes to `main` / `master` / `develop`. This is local enforcement of the branch-protection convention; the same rule should also be enabled server-side via GitHub branch protection. The hook is idempotent — safe to re-run.

### Starting the local stack

For full local dev with Cosmos persistence and Azurite-backed blob storage, start the .NET Aspire 13.4.6 orchestrator:

```bash
pwsh ./start-apphost.ps1
```

First run pulls container images (Cosmos preview emulator + Azurite); subsequent runs reuse persistent volumes. The dashboard runs at the URL printed in the AppHost output (default `https://localhost:17110`).

### Playwright

The `GamePageScraper` and `ServiceBulletinScraper` use Playwright. After your first build, install the Chromium browser binary once:

```bash
dotnet run --project src/PinballWizard.Cli -- --install-playwright
```

The scraper writes everything under `./data/` by default (downloads, metadata, logs). Set `DATA_PATH` to relocate it for Docker or local sandboxing.

## Running the Scraper

A few useful invocations while developing:

```bash
# Dry-run smoke test against a single source — discovers links, writes nothing.
dotnet run --project src/PinballWizard.Cli -- --source manuals --dry-run

# Scope to a single source while iterating on its scraper.
dotnet run --project src/PinballWizard.Cli -- --source bulletins
```

The full CLI is documented in [README.md](README.md#cli-flags).

## Branch Naming

Convention: `Dev-PascalCaseDescription`. No ticket prefix is required for this repo — it's not in a Jira or Azure DevOps tracker.

```
Dev-FixServiceBulletinPagination
Dev-AddPdfPageCountExtraction
Dev-EnterpriseQuality
```

Never commit directly to `main`. The CI pipeline and branch-protection rules will reject it; the `branch-guard` hook will too if you have it installed.

## Commit Format

We use conventional-commit style with a colon separator. No co-author lines.

```
<type>(<scope>): short imperative summary

A paragraph (or two) explaining WHY this change is needed. The diff
already shows what; the message should explain the reasoning, the
trade-offs, and any context a future reader will want.
```

Valid `<type>` values: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `infra`. The `<scope>` is a short module name (`scraper`, `catalog`, `web`, `api`, `rag`, `ci`, etc.) — never a ticket ID.

Recent examples from the history:

```
feat(web): citation cards link to /documents/{id} for corpus chunks
feat(documents): add document-type filter to browse page
fix(catalog): self-correcting catalog_stats handler + clickable source URLs
chore(dev): full-feature AppHost launcher + browser auto-open
```

Keep the first line at 72 characters or fewer. Always include a body paragraph. Don't add `Co-Authored-By` trailers.

## Quality Bar

The repository enforces a strict quality bar:

- `TreatWarningsAsErrors=true` — every analyzer warning is a build break
- `latest-recommended` .NET analyzer rule set
- Locked-mode NuGet restore (`packages.lock.json` files are checked in)
- Tests must pass; CodeQL must be green; the project must build clean

Full details, including the exception list and the rationale behind each rule, live in [`docs/quality-bar.md`](docs/quality-bar.md).

### Dependency updates

Don't hand-bump packages for routine updates — **Renovate** raises grouped
version PRs (minor/patch auto-merge once CI is green) and **Dependabot** opens
security PRs. Major bumps are held for explicit approval on the Renovate
Dependency Dashboard and land as dedicated, individually reviewed PRs. See
[ADR-0037](docs/adr/0037-dependency-update-automation.md). Manual bumps are for
spikes and incidents, not routine maintenance.

If your change requires suppressing a rule, justify the suppression in `Directory.Build.props` with a comment and a removal criterion. Don't bury it in `#pragma warning disable` blocks.

## Test suites

`dotnet test PinballWizard.slnx` runs the whole suite. Tests live in [per-layer projects](README.md) (ADR-0030); the browser-driven Web tests carry an xUnit `Category` trait so you can scope a run with `--filter`.

The standard PR-CI-equivalent filter (excludes browser and E2E categories that need a live stack):

```bash
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"
```

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

PRs are created via `gh pr create` (GitHub). Every PR description must contain three sections:

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

Bugs that touch live-site scraping are best reported with the manufacturer URL or game slug that triggered the problem.

## Questions

Architecture decisions and current state are documented in [`CLAUDE.md`](CLAUDE.md) and the `docs/` directory. If something there is wrong or unclear, that's a documentation bug — please file it.
