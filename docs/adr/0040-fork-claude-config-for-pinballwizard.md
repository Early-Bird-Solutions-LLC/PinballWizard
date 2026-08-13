# 0040 — Fork Claude Code config in-repo for PinballWizard

**Status:** Accepted
**Date:** 2026-06-19

## Context

Claude Code is configured at two levels: a user-global `~/.claude/` directory and
an optional in-repo `.claude/` directory that overrides or extends the global one.
Jim's global config contains the full APS standards corpus — rules, skills, and
agents scoped to Advantage Payment Services internal tooling (Jira, Azure DevOps,
APS-specific service standards). When a Claude session opens in this repo, those APS
artifacts load into context alongside the repo's own CLAUDE.md, adding noise that is
irrelevant to PinballWizard and confusing to any developer reading a PR description
that references APS ticket formats or Azure DevOps commands.

PinballWizard is a customer-facing showcase for Earlybird Solutions. It must be
self-contained: a prospect who forks the repo, or a contractor who clones it, should
get identical AI-assisted engineering behavior without needing Jim's personal global
setup. The config is itself a showcase artifact — evidence that the project is
engineered with the same rigour and intentionality as the application code.

## Decision

Fork the complete Claude Code configuration in-repo under `.claude/`, adapted for
this project's context:

- **Rules** (`no-guessing`, `timeout-debugging`, `parallel-sessions`,
  `pinball-workflows`) are copied from the global config with PinballWizard-specific
  overrides applied (GitHub not Azure DevOps, personal identity, no Jira).
- **Skills** (`commit`, `pr`, `pre-commit-workflow`, `local-review`, `screenshot`,
  `playwright-setup`, `ci-preview`, `context-management`) cover the full development
  workflow without any APS-internal references.
- **Commands** (14 slash-command definitions) provide short-form entry points into
  the skills.
- **Agents** (`codebase-analyzer`, `web-search-researcher`, `thoughts-analyzer`,
  `modernization-analyst`) cover the research and analysis tasks this project uses.
- **Repo-scoped APS suppression (Half B — shipped 2026-08-13):** a corresponding
  change in `APS.JimClaudeCodeConfig` scopes each APS rule with `paths:`
  frontmatter so the standards corpus only loads for files inside an APS repo
  directory (`**/APS.*/**` and `**/APS-*/**`). That change ships in a separate PR
  against the global config repo and is not part of this ADR's scope.

  > **Correction (2026-08-13, issue #835).** This section previously described the
  > guard as suppressing itself "when the working directory is inside
  > `c:\earlybird\`". **No such path-prefix mechanism ever existed** — the guard has
  > always been Claude Code's `paths:` frontmatter, which matches the *files in the
  > conversation*, not the shell's working directory. Half B's original commit added
  > `paths: "**/APS.*/**"` only to the 22 rules that had **no** `paths:` key at all;
  > 14 others already carried bare file-type globs (`**/*.cs`, `**/*.razor`, …) that
  > match any repo, so those kept firing here. That is why the corpus still loaded
  > after the repo moved off `c:\earlybird\` — the move was a coincidence, not the
  > cause. Fixed in config-repo PRs #275 and #277, which key every pattern on the
  > repo directory name so a future relocation cannot silently disable it again.

Every file vendored from the global config carries a `vendored-from:` header
comment pinning the upstream SHA. Two scripts (`scripts/check_claude_config_drift.py`
and `scripts/assert_no_excluded_aps_skills.py`) enforce that vendored files stay
current and that APS-internal artifacts never leak in.

## Alternatives considered

- **Keep the shared global config as-is.** Rejected: APS-specific rules and skills
  load into every PinballWizard session, and the config is invisible to anyone who
  clones the repo. Self-containment and showcase-visibility are not achievable this
  way.
- **Org-addon only (thin in-repo `.claude/` that only adds PinballWizard-specific
  rules).** Rejected: the APS corpus still loads globally, and the in-repo config
  does not suppress it. A prospect browsing the repo sees an incomplete picture.
- **Do nothing; rely on CLAUDE.md.** Rejected: `CLAUDE.md` provides context to the
  model but does not suppress global skills or rules. The APS noise problem remains,
  and the session behavior is still non-reproducible without Jim's personal setup.

## Consequences

**Positive:**

- Any developer who clones the repo gets the same Claude session behavior — no
  personal setup required.
- The configuration is visible in the repo and readable by a prospect or team member
  as evidence of engineering discipline.
- APS-internal tooling references (Jira, Azure DevOps, APS service standards) are
  absent from sessions in this repo, reducing confusion and context waste.

**Negative:**

- Vendoring drift: in-repo copies can fall behind the global config. Mitigated by
  `vendored-from:` headers and the drift-check script.
- APS skills that are genuinely useful here (e.g., `local-review`) must be kept in
  sync manually. The drift-check script surfaces this.
- Half B (repo-scoping the global config) is a separate PR against a separate repo
  and must ship before the suppression is fully effective. **Shipped 2026-08-13**
  (config-repo PRs #275, #277); before that, APS artifacts still loaded here and the
  in-repo config was the only override layer.

## References

- [`CLAUDE.md`](../../CLAUDE.md) — project-level context file; the canonical session-entry point for this repo
- [`scripts/check_claude_config_drift.py`](../../scripts/check_claude_config_drift.py) — compares each vendored file against its upstream SHA and reports staleness
- [`scripts/assert_no_excluded_aps_skills.py`](../../scripts/assert_no_excluded_aps_skills.py) — asserts that no APS-internal skills are present under `.claude/`
- [`.claude/README.md`](../../.claude/README.md) — index of the in-repo config structure
- **Half B** ships against the `APS.JimClaudeCodeConfig` repo (global config), not this repo — it scopes every APS rule's `paths:` frontmatter to `**/APS.*/**` + `**/APS-*/**` so the standards corpus loads only for files inside an APS repo directory (shipped 2026-08-13; see the correction note above — the earlier `c:\earlybird\` description was never accurate)
