# Task 2 Report — Allowlist fence guard + matcher

**Date:** 2026-07-06
**Commit:** `974bebb` — ci(docs-agent) allowlist fence guard + matcher
**Branch:** docs/refresh-and-docs-agent

---

## Files Created

| File | Purpose |
|---|---|
| `.github/docs-agent-allowlist.txt` | Glob patterns governing what paths docs-agent PRs may touch |
| `.github/scripts/allowlist-match.mjs` | Dependency-free Node.js matcher; exit 0 = allowed, 1 = denied |
| `.github/workflows/docs-agent-guard.yml` | Required check; passes instantly on non-`docs-agent/*` branches |

---

## Allowlist ordering rationale

Last-match-wins semantics mean pattern order is critical. The order is:

```
README.md                       # explicit allow
CLAUDE.md                       # explicit allow
docs/engineering-manifest.json  # explicit allow
docs/**                         # allow all docs/* (broad allow — must precede denials)
!docs/adr/**                    # deny: ADRs are architecture records, not agent-editable
!docs/decision-log.md           # deny: decision log is human-curated
!docs/superpowers/**            # deny: SDD plans/specs are agent tooling, not product docs
```

`docs/**` MUST appear before the three `!docs/...` denial lines. If a path like
`docs/adr/0051-x.md` is evaluated:
1. `docs/**` matches → `allowed = true`
2. `!docs/adr/**` matches → `allowed = false` (last match wins = denied)

If the order were reversed (denials first, then `docs/**`), `docs/adr/0051-x.md` would
end on the `docs/**` allow and be incorrectly permitted.

---

## Regex compilation trace (key patterns)

| Pattern | After escaping | After `**`→space | After `*`→`[^/]*` | After space→`.*` | Final regex |
|---|---|---|---|---|---|
| `README.md` | `README\.md` | (no change) | (no change) | (no change) | `^README\.md$` |
| `docs/**` | `docs/**` | `docs/ ` | `docs/ ` | `docs/.*` | `^docs/.*$` |
| `docs/adr/**` | `docs/adr/**` | `docs/adr/ ` | `docs/adr/ ` | `docs/adr/.*` | `^docs/adr/.*$` |
| `docs/decision-log.md` | `docs/decision-log\.md` | (no change) | (no change) | (no change) | `^docs/decision-log\.md$` |
| `docs/superpowers/**` | `docs/superpowers/**` | `docs/superpowers/ ` | `docs/superpowers/ ` | `docs/superpowers/.*` | `^docs/superpowers/.*$` |

Note: The escape step escapes `.` but NOT `*` or `/` (neither is in the escape regex
`[.+^${}()|[\]\\]`). The `**`→space→`.*` two-step avoids corrupting `**` into
`[^/]*[^/]*` when the single-`*` substitution fires.

---

## Self-test results (all 9 cases, actual exit codes)

| Path | Expected | Actual | Pass? | Reasoning |
|---|---|---|---|---|
| `README.md` | 0 | 0 | PASS | Matches pattern 1 (allow); no later pattern overrides |
| `docs/vision.md` | 0 | 0 | PASS | Matches `docs/**` (allow); no denial pattern matches |
| `docs/adr/0051-x.md` | 1 | 1 | PASS | `docs/**` allow then `!docs/adr/**` deny; last = deny |
| `src/Program.cs` | 1 | 1 | PASS | No pattern matches; `allowed` stays `false` |
| `docs/decision-log.md` | 1 | 1 | PASS | `docs/**` allow then `!docs/decision-log.md` deny |
| `CLAUDE.md` | 0 | 0 | PASS | Matches pattern 2 (allow); no later pattern overrides |
| `docs/engineering-manifest.json` | 0 | 0 | PASS | Matches exact pattern 3, then `docs/**`; both allow; no denial matches |
| `docs/superpowers/plans/x.md` | 1 | 1 | PASS | `docs/**` allow then `!docs/superpowers/**` deny; last = deny |
| `.github/workflows/docs-agent.yml` | 1 | 1 | PASS | No pattern matches; `allowed` stays `false` |

**All 9 cases pass.**

---

## YAML validation

**Validator used:** `python -c "import yaml,sys; yaml.safe_load(open('...')); print('YAML valid: no errors')"`

**Result:** `YAML valid: no errors`

(Python's `yaml.safe_load` is available on the CI ubuntu-latest runner and is a reliable
structural validator; it catches syntax errors, indentation faults, and duplicate keys.)

---

## Guard workflow behaviour

- **Non-`docs-agent/*` branches:** Step `gate` sets `enforce=false`; `Checkout` and
  `Verify` steps are skipped via `if: steps.gate.outputs.enforce == 'true'`. The job
  completes in seconds — no checkout, no diff, no Node invocation.
- **`docs-agent/*` branches:** full path-diff enforced. Any file outside the allowlist
  emits `::error::` and exits 1.
- **`permissions: contents: read`** — minimal scope; no write access.

---

## Self-review

- [x] `docs/**` precedes all `!docs/...` denials — last-match-wins ordering correct
- [x] Matcher is dependency-free (`node:fs` only; no npm install)
- [x] `.github/workflows/docs-agent.yml` denied (agent cannot edit its own runner config)
- [x] `.github/**` in general denied (no pattern covers it)
- [x] ADRs denied (`!docs/adr/**`)
- [x] `docs/superpowers/**` denied (SDD plans are not product docs)
- [x] `docs/decision-log.md` denied
- [x] `README.md`, `CLAUDE.md`, `docs/engineering-manifest.json` all allowed
- [x] All `docs/**` (not in a denied subtree) allowed
- [x] YAML validates cleanly
- [x] Committed as Jim Keeley `<94459922+jkeeley2073@users.noreply.github.com>`, no Claude trailer
- [x] No new `.md` documentation files (report is in `.superpowers/sdd/` per task spec)

---

## Phase 3 Task 2 — Security hardening (2026-07-06)

**Fixes applied:** 4 | **Commit:** see below

### Fix 1 — Script injection: head_ref and SHAs routed through env vars

`docs-agent-guard.yml` previously interpolated `${{ github.head_ref }}` directly
into the `run:` shell script (the documented Actions injection anti-pattern). A
crafted branch name could inject arbitrary shell commands and skip enforcement.

- Gate step: added `env: HEAD_REF: ${{ github.head_ref }}`; script references
  `"$HEAD_REF"` instead of the interpolated expression.
- Enforcement step: added `env: BASE_SHA: / HEAD_SHA:` for the two PR SHA
  references; script references `"$BASE_SHA"` / `"$HEAD_SHA"`.
- No other `${{ github.* }}` expressions remain in any `run:` block.

### Fix 2 — Path traversal: normalize target before matching

`allowlist-match.mjs` previously matched the raw argument, so `docs/../.github/workflows/evil.yml`
would evaluate against the `docs/**` allow pattern and be permitted. The target
path is now normalized by collapsing `.` and `..` segments via a `split/reduce/join`
before any pattern is evaluated. `docs/../.github/workflows/evil.yml` normalizes to
`.github/workflows/evil.yml` → no allow pattern matches → denied (exit 1).

### Fix 3 — Case-variant: glob regexes compiled case-insensitive

`toRegex` now passes `'i'` as the RegExp flag. Both allow and deny patterns apply
case-insensitively, so `docs/ADR/x.md` and `docs/Adr/x.md` both hit the
`!docs/adr/**` denial and exit 1. Uppercase allowlist entries (`README.md`,
`CLAUDE.md`) still match correctly under case-insensitive comparison.

### Fix 4 — Stale comment corrected

The enforcement step comment previously claimed "git pathspec matching via a temp
gitignore" — the implementation was never that. Updated to describe the actual
mechanism: "the allowlist-match.mjs Node.js matcher (supports ** and ! negation)".

### Self-test results (14 cases, all PASS)

| Path | Expected | Actual |
|---|---|---|
| `README.md` | 0 | 0 |
| `CLAUDE.md` | 0 | 0 |
| `docs/engineering-manifest.json` | 0 | 0 |
| `docs/vision.md` | 0 | 0 |
| `docs/adr/x.md` | 1 | 1 |
| `docs/decision-log.md` | 1 | 1 |
| `docs/superpowers/plans/x.md` | 1 | 1 |
| `src/Program.cs` | 1 | 1 |
| `.github/workflows/docs-agent.yml` | 1 | 1 |
| `docsX/foo.md` | 1 | 1 |
| `docs/a+b.md` | 0 | 0 |
| `docs/ADR/x.md` *(new)* | 1 | 1 |
| `docs/Adr/x.md` *(new)* | 1 | 1 |
| `docs/../.github/workflows/evil.yml` *(new)* | 1 | 1 |

### YAML validation

**Validator:** `python -c "import yaml,sys; yaml.safe_load(open(...)); print('YAML valid: no errors')"`
**Result:** `YAML valid: no errors`
