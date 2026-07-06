---
status: Superseded
phase: Cross-cutting
owner: Jim
last-reviewed: 2026-05-16
supersedes: ""
---

# Quality Bar

> **Point-in-time artifact (2026-05-16).** This document reflects the state of the codebase as of that date; see [build-spec.md](build-spec.md) and [guardrails.md](guardrails.md) for current authoritative guidance.

The durable checklist for any change landing in PinballWizard. Skim this before
opening a PR. CI enforces most of it; the rest is on the author and reviewer.

> **Build settings:** see [`Directory.Build.props`](../Directory.Build.props).
> The repo uses `latest-recommended` analyzers with warnings-as-errors. A small
> `<NoWarn>` list documents transitional and permanent suppressions, each with
> a removal criterion.

## Code

- Zero warnings. `latest-recommended` analyzers enforced; `<NoWarn>` is
  documented and shrinks over time.
- `TreatWarningsAsErrors=true`. Don't add new entries to `<NoWarn>` without a
  comment explaining why and what would let us remove it.
- No `TODO` or `FIXME` committed. Open an issue and link it instead.
- No commented-out code blocks. If history matters, the commit log is the
  archive.
- No `Console.WriteLine` outside `Program.cs`'s user-facing top-level output.
  Use `ILogger` everywhere else.

## Tests

- Unit tests for every new branch / piece of logic.
- Integration tests when the change wires components together.
- Coverage trends up, not down. Bring the number with the PR; don't promise
  follow-ups.
- No `[Skip]` without a linked issue explaining the conditions for re-enabling.
- xUnit `Method_Scenario_Result` naming convention is the norm (CA1707 is
  permanently suppressed for test projects).

## Docs

- Public API has XML doc comments.
- `README.md`, `CLAUDE.md`, and anything under `docs/` reflect code reality.
  No drift.
- No client/professional references. No personal email addresses. The
  sanitization workflow is your last-line defense, not your first.

## Build

- `dotnet restore --locked-mode PinballWizard.slnx` succeeds. If you change
  packages, regenerate `packages.lock.json` files in the same commit and
  explain the change in the commit body.
- Deterministic build (`Deterministic=true` and `ContinuousIntegrationBuild`
  on CI). Don't introduce non-deterministic timestamps, GUIDs, or paths into
  artifacts.
- Source link works (`Microsoft.SourceLink.GitHub` is on by default). Don't
  break it.

### Local-build gotcha (Windows + Git Bash)

If you run `dotnet build /warnaserror` from Git Bash on Windows, MSYS path
conversion mangles `/warnaserror` into a Windows path (`C:/Program Files/Git/...`)
and the build silently ignores the flag. Two workarounds:

```bash
MSYS_NO_PATHCONV=1 dotnet build PinballWizard.slnx /warnaserror
# or use the property form, which MSYS leaves alone:
dotnet build PinballWizard.slnx -p:TreatWarningsAsErrors=true
```

PowerShell users are unaffected. CI runs on Linux and is unaffected.

## Commits

- Conventional format: `<type>(<scope>) <message>`.
  - Valid types: `feat`, `fix`, `chore`.
  - Scope is a module name (e.g., `scraper`, `downloading`), never a ticket ID.
- Include a body paragraph that explains *why*, not *what*.
- No co-author lines.
- Never push directly to `main`. Branch protection is enforced; the local
  hook will block it; CI will refuse to run; reviewers will refuse to merge.

## PRs

- Description includes:
  - **Summary** — what changes and why.
  - **Test Plan** — how it was verified, with concrete commands or steps.
  - **Out of Scope** — anything intentionally not addressed.
- Don't merge while CI is red. Don't restart CI hoping it'll pass — fix the
  root cause.
- No self-approving (GitHub blocks it on main; even where it doesn't, don't).

## Security

- No secrets in code. Configuration through environment variables and
  `appsettings.{Environment}.json` (gitignored if it contains secrets).
- The sanitization workflow must be green. If it flags a false positive,
  narrow the rule rather than broadening the exclusion list.
- Dependabot updates are accepted promptly; treat them like any other PR.
- CodeQL findings are addressed before merge — fix, justify with a `// lgtm`
  comment, or open a tracking issue.

## When the Quality Bar Bends

`Directory.Build.props` carries a documented `<NoWarn>` list of transitional
suppressions. Every entry has a removal criterion. Anyone fixing the
underlying issue should also delete the corresponding `NoWarn` entry in the
same PR. The goal is for the list to shrink, not grow.
