# Design — PinballWizard owns its Claude Code configuration

**Date:** 2026-06-19
**Status:** Approved (brainstorming) → pending implementation plan
**Author:** Jim Keeley + Claude Code
**Scope:** Two repos — `PinballWizard` (Half A) and `APS.JimClaudeCodeConfig` (Half B)

> This is a process/design artifact, not a product doc. It lives under
> `docs/superpowers/specs/` alongside existing plans/specs. It can be relocated or
> gitignored without affecting the showcase surface.

---

## 1. Problem

Today, every Claude Code session — including sessions in this repo — loads the
**entire APS standards corpus** (~20 `aps-*-standard` rules: auth, SQL, Cosmos,
messaging, networking, PCI, etc.) plus APS work-tracking workflows (Jira, Azure
DevOps, work-item time-tracking). They reach this repo because the personal config
repo `APS.JimClaudeCodeConfig` installs into `~/.claude/` via symlinks
(`~/.claude/rules → global/rules`, `~/.claude/skills → global/skills`, …), and
that global content is org-agnostic.

None of it applies to PinballWizard. PinballWizard:

- lives in a **personal GitHub org** (`jkeeley2073`), not Azure DevOps;
- tracks work in **GitHub Issues**, not Jira (no DRS tickets, no time-tracking);
- commits under **personal identity** (`94459922+jkeeley2073@users.noreply.github.com`);
- is a **customer-facing showcase** held to enterprise standards, where the Claude
  Code setup is itself a demonstration artifact.

The repo already carries a thoughtful repo-local `.claude/` (CLAUDE.md, INVARIANTS.md,
PR-AUDIT.md, README.md, `skills/local-review/`, settings). But its own README admits
the gap:

> "The global Claude Code setup (at `~/.claude/`) adds skills for commit formatting,
> time tracking, PR creation, and pre-commit validation — those are personal workflow
> tools and aren't checked in here."

The goal is to close that gap: PinballWizard should **own its full Claude Code
config in-repo**, and the APS noise should **stop loading here**.

## 2. Goal / End State

When working in `c:\earlybird\PinballWizard`:

1. The only rules/skills/commands that are live are PinballWizard's own, committed in
   the repo, pristine, and documented as a showcase artifact.
2. The APS standards corpus and APS work-tracking workflows do **not** load into
   context.
3. Nothing in PinballWizard depends on `APS.JimClaudeCodeConfig` at runtime — the
   repo is self-contained and travels with itself (clone → full workflow available).
4. The setup is documented for a sceptical prospect: what is included, what is
   excluded, and **why**.

## 3. Honest constraints (load-bearing)

- **Claude Code always loads user-level `~/.claude/CLAUDE.md` and the symlinked
  `rules/`.** There is no native per-repo switch to ignore global config. Therefore
  "only PinballWizard config applies here" is achieved by **two complementary moves**,
  not one:
  - **(A)** Make the repo-local `.claude/` self-contained and authoritative (this is
    reliable and travels with the repo).
  - **(B)** Make the global APS rules **self-suppress** outside APS repos via `paths:`
    frontmatter (this is what actually stops the noise loading here).
- **Org/project addons are install-time, not per-repo dynamic** (`install.ps1:1149-1150`
  concatenates `orgs/<org>/CLAUDE-ADDON.md` / `projects/<project>/CLAUDE-ADDON.md`
  based on `-org`/`-project` install params). So the earlybird org addon is a
  secondary identity lever, not the primary suppression mechanism. The primary
  suppression mechanism is `paths:` scoping (B).
- **Path-scoping is an allowlist** (a rule fires only on matching globs). Suppressing a
  standard in PinballWizard without un-firing it for APS/Neighborli means the allowlist
  must enumerate the repo roots where the standard *should* fire and omit
  earlybird/PinballWizard. Preserving Neighborli firing is a verification requirement
  (§8).

## 4. Approach (confirmed)

Hybrid, both halves, in two separate PRs:

| Half | Repo | Branch | PR |
|---|---|---|---|
| **A — repo-local config** | `PinballWizard` | `chore/claude-config-ownership` | Self-contained `.claude/` + showcase docs |
| **B — global self-suppress** | `APS.JimClaudeCodeConfig` | new, off `origin/main` | `paths:` scoping on APS standards + `orgs/earlybird/CLAUDE-ADDON.md` |

Separate PRs because mixing a personal-config-repo change into a PinballWizard PR
would itself be a showcase smell.

**Confirmed decisions:**
- **Drift control:** provenance header on every vendored file **+** a drift-check
  (script/CI) that flags when the upstream source SHA has moved.
- **Breadth:** maximal — vendor rules **+** skills **+** commands **+** agents.
- **Half-B reach:** all ~20 APS standards path-scoped in one focused PR.

## 5. Half A — repo-local config (PinballWizard)

### 5.1 Target layout

```
PinballWizard/
├── CLAUDE.md                         # (exists) authoritative project memory — add pointer to .claude/rules
└── .claude/
    ├── README.md                     # (exists) rewrite §"skills layer" + add include/exclude table
    ├── INVARIANTS.md                 # (exists) unchanged
    ├── PR-AUDIT.md                   # (exists) unchanged
    ├── settings.json                 # (exists) extend if needed
    ├── rules/                        # NEW — vendored universal rules
    │   ├── no-guessing.md
    │   ├── timeout-debugging.md
    │   ├── parallel-sessions.md      # adapted (drop APS framing)
    │   └── pinball-workflows.md      # NEW — replaces mandatory-workflows.md (GitHub)
    ├── skills/                       # vendored workflow skills (+ existing local-review)
    │   ├── local-review/             # (exists) keep
    │   ├── commit/                   # from smart-commit, GitHub + personal identity
    │   ├── pr/                       # from smart-pr → gh pr create
    │   ├── pre-commit-workflow/      # adapted (no work-item gate)
    │   ├── context-management/
    │   ├── screenshot/
    │   ├── playwright-setup/
    │   └── ci-preview/
    ├── commands/                     # NEW — curated slash-commands
    │   ├── local_review.md
    │   ├── clean-context.md
    │   ├── create-spec.md
    │   ├── create_plan.md
    │   ├── implement_plan.md
    │   ├── validate_plan.md
    │   ├── research_codebase.md
    │   ├── create_worktree.md
    │   ├── debug.md
    │   ├── describe_pr.md
    │   ├── ship.md
    │   ├── push-only.md
    │   ├── pr-only.md
    │   └── quick-commit.md
    └── agents/                       # NEW — generic agents (most droppable category)
        ├── codebase-analyzer.md
        ├── thoughts-analyzer.md
        ├── web-search-researcher.md
        └── modernization-analyst.md
```

### 5.2 What comes over, and what does NOT

**Rules — included (universal engineering discipline):**

| Rule | Treatment |
|---|---|
| `no-guessing.md` | Copy verbatim (universal) |
| `timeout-debugging.md` | Copy verbatim (universal) |
| `parallel-sessions.md` | Adapt — keep the worktree-safety lesson, drop APS-repo-specific framing |
| `mandatory-workflows.md` → **`pinball-workflows.md`** | Rewrite for GitHub: `gh` PR flow, branch protection, `/local-review` → PR-AUDIT → commit → push, `claude-code` label, **no Jira / no work-item time-tracking / personal identity**. Consolidates rules already scattered across CLAUDE.md + memory. |

**Rules — excluded (and why):** every `*-standard.md`, `dotnet.md`, `bicep.md`,
`ui-design.md`, `frontend-react.md`, `api-conventions.md`, `coding-standard.md`,
`testing-standard.md`, `documentation-standard.md`, `azure-*.md` — these are the APS
fleet standards. PinballWizard's own bar lives in `CLAUDE.md`, `docs/quality-spec.md`,
`docs/guardrails.md`, `.claude/INVARIANTS.md`, and `docs/adr/`.

**Skills — included:** `commit` (←smart-commit), `pr` (←smart-pr),
`pre-commit-workflow`, `context-management`, `screenshot`, `playwright-setup`,
`ci-preview`, plus existing `local-review`.

**Skills — excluded:** all `aps-*-standard`, `jira`, `work-item-time-tracking`,
`azure-devops-pipeline`, `teamcity`, `basecamp`, `linear`, `sonarqube` (repo is gate-
exempt), `ado-wiki-edit`, `investigate`, `vpn-troubleshoot`, `sso-troubleshoot`,
`ssl-certificate`, `azure-sql-optimizer`, `aps-devops-agent-pool`, `setup-azure`
(earlybird Azure isolation is handled by the existing single-subscription setup),
`spec-driven` (superseded by the in-repo `create-spec`/`create_plan` commands).

**Commands — included:** the curated list in §5.1.
**Commands — excluded:** all `aps-*`, `linear`, `sync-from-shared`,
`humanize-standards`, `ado-pr-review`, `sso-troubleshoot`, `setup-azure`,
`start`/`start-auto` (APS Jira session-start), `ship-auto` (carries APS assumptions —
revisit later).

**Agents — included:** the four generic agents. Note they overlap with built-in
subagent types; included for self-containment and showcase completeness, flagged as
the most droppable category if they add noise.

### 5.3 Adaptation translation (APS → PinballWizard)

| Concern | APS source behavior | PinballWizard behavior |
|---|---|---|
| Work tracking | Jira DRS-XXXX (mandatory) | GitHub Issues; ticket reference optional |
| Commit trailer | `Co-Authored-By: Claude …` on APS repos | none mandated; **personal identity author** required |
| PR tool | `az repos pr create` | `gh pr create` |
| PR label | (varies) | `claude-code` label + verification |
| Post-push | work-item-time-tracking (BLOCKING) | **removed** (no time tracking — see `feedback_skip_time_tracking`) |
| Identity | work account | `94459922+jkeeley2073@users.noreply.github.com` (INVARIANT) |
| Branch protection | block main/develop | block `main` |
| Pre-PR review | `/pr-review-toolkit:review-pr` + `local-pr-review.py` | keep `/local-review` + PR-AUDIT (already the repo's standard) |
| Deploy | varies | Deployment Stacks only (INVARIANT) |

### 5.4 Provenance + drift control

Every vendored file gets a header comment (HTML comment in `.md` skills so it doesn't
render in GitHub preview where appropriate):

```
<!-- vendored: APS.JimClaudeCodeConfig/global/skills/smart-commit/SKILL.md @ <sha>
     adapted-for: PinballWizard (GitHub / personal identity / no Jira)
     last-synced: 2026-06-19 — see docs/claude-code.md §Drift -->
```

Drift-check: a small script (`scripts/check-claude-config-drift.*`, language TBD in
plan — likely Python to match existing `~/.claude/bin` tooling, or pwsh to match
`infra/scripts`) that reads each vendored header's `@<sha>` and source path, and
reports whether the upstream file at that path has advanced past the recorded SHA.
Optionally wired into CI as a non-blocking informational check ("config drift: N files
behind upstream"). The drift check needs read access to `APS.JimClaudeCodeConfig`;
since that repo is not guaranteed present in CI, the CI variant degrades to "skipped —
upstream not available" rather than failing (no masking: it reports the skip).

## 6. Half B — global self-suppress (APS.JimClaudeCodeConfig)

### 6.1 Path-scope the APS standards

Add `paths:` frontmatter to each `global/rules/*-standard.md` (and the APS-flavored
`dotnet.md`, `bicep.md`, `ui-design.md`, `frontend-react.md`, `azure-pipelines.md`,
`infrastructure.md`, `api-conventions.md`, `coding-standard.md`, `testing-standard.md`,
`documentation-standard.md`) so they fire only on the repo roots where they apply.

**Scoping rule:** the allowlist enumerates APS (and, where the standard applies,
Neighborli) repo-root globs and **omits earlybird / PinballWizard**. Example baseline:

```yaml
paths:
  - "**/APS.*/**"
  - "**/aps/**"
  # add Neighborli globs only for standards that target Neighborli
```

The exact per-standard glob set is resolved in the plan against the fleet registry, and
verified to (a) still fire on a representative APS file and (b) **not** fire on a
PinballWizard file. Extends the started `fix/path-scope-aps-rules` branch (which scoped
`ui-design` + `documentation-standard` only).

This also covers the **`~/.claude/CLAUDE.md`-injected user instructions**: the spec
must confirm whether the standards reach context via the symlinked `rules/` dir
(scoped by `paths:`) or via a separate import in the user global CLAUDE.md. If the
latter, that import is made conditional/removed too. (Verification step in plan.)

### 6.2 earlybird org addon

Create `orgs/earlybird/CLAUDE-ADDON.md` mirroring `orgs/aps/` and `orgs/neighborli/`:

- Work tracking: GitHub Issues (no Jira)
- Commit format: `<type>(scope) #NN: message` (GitHub issue refs, optional)
- Identity: personal (`94459922+jkeeley2073@…`)
- Compute default: Azure Container Apps / ACA Jobs
- Deploy: Deployment Stacks only
- Budget posture: $300–400/mo cap
- Session-start: no APS `/start-auto` Jira flow

Note: install-time only; documented as such.

## 7. Showcase documentation

1. **Rewrite `.claude/README.md`:** replace the "aren't checked in here" paragraph;
   add a table of every included skill/rule/command/agent with a one-line *why*, and a
   short "what we deliberately excluded and why" list. Keep the existing strong
   narrative sections.
2. **New `docs/claude-code.md`:** architecture of the setup with a **Mermaid** diagram
   (per the no-ASCII-diagrams rule) showing how rules/skills/commands/agents/hooks
   compose, and the self-contained-repo vs global boundary. Include a "watch it work"
   section linking real PRs where the workflow fired.
3. **New ADR `docs/adr/0039-fork-claude-config-for-pinballwizard.md`** (MADR-lite):
   context (APS noise + showcase + self-containment), decision (fork config in-repo +
   path-scope global), alternatives considered (shared global; org-addon-only;
   do-nothing), consequences (vendoring drift, mitigated by provenance + drift-check).
   Add to `docs/adr/README.md` index.

## 8. Verification / testing

- **Noise gone:** start a fresh Claude session in PinballWizard after Half B; confirm
  no `aps-*-standard` content loads (inspect the session context / a SessionStart probe).
- **APS unaffected:** confirm a representative APS file still triggers its standards
  (open/edit a file under an `APS.*` path mentally or via a dry-run of the path match).
- **Neighborli unaffected:** confirm Neighborli-targeted standards still fire for
  Neighborli paths.
- **Self-contained:** the repo-local skills/commands resolve and run with
  `APS.JimClaudeCodeConfig` absent (simulate by path check; no runtime dependency).
- **Drift-check works:** point it at a deliberately stale header, confirm it reports
  "behind"; degrade cleanly when upstream is absent.
- **No leaked excludes:** an assertion (script/CI) that none of the excluded APS skills
  appear under `.claude/skills/`.
- **PR-AUDIT + /local-review** run clean on the Half-A PR.

## 9. Out of scope (YAGNI)

- Surfacing the setup on the public `pinwiz.ai` site (enhancement idea, not this PR).
- A generalized multi-repo config sync tool (the drift-check is read-only/report-only).
- Reworking the global install model or `/switch-config` command.
- Migrating any APS or Neighborli config.

## 10. Enhancement ideas (recorded, not committed)

1. Config drift-check surfaced as a `/local-review`-adjacent report.
2. CI assertion that `.claude/` skills parse and no excluded APS skill leaked.
3. `docs/claude-code.md` "watch it work" PR links (partially exists informally).
4. Public-site "how this was built" artifact for prospects (`pinwiz.ai/about`).

## 11. Open questions for plan stage

- Exact per-standard `paths:` globs (verified against fleet registry).
- Drift-check language (Python vs pwsh) and whether CI-wired now or later.
- Whether the APS standards reach context via symlinked `rules/` (scoped by `paths:`)
  or a separate user-CLAUDE.md import that must also be made conditional.
- Final commands/agents prune (maximal set chosen; trim during implementation if any
  carry un-adaptable APS assumptions).
