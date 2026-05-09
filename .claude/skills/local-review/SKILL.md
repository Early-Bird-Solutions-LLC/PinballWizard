---
name: local-review
description: Run a structured pre-push code review of the current branch's diff against main. Produces a verdict-tagged critique (✅/⚠️/🔴) covering design, drift, error handling, security smells, and test quality. Runs BEFORE the 7-item PR self-audit checklist. Invoke any time you are about to push a non-trivial PR.
---

# /local-review — Pre-push code review

## When to invoke

Before pushing **any** PR that adds production code (new files, new public API, new behavior). Doc-only PRs and pure dependency bumps may skip.

This skill is **step 0** of the PR self-audit flow defined in `CLAUDE.md` § PR self-audit. The 7-item mechanical checklist runs after this — it catches dead config and identity issues; this skill catches design, architecture, and drift issues that a checklist cannot.

## What it does

Spawns a `general-purpose` agent with a structured review prompt against the staged + branched diff. The agent produces a written critique organized by category, with verdicts and specific file:line citations. You then either:

- **Fix** each 🔴 must-fix finding (blocking — do not push until resolved)
- **Fix or defer** ⚠️ minor findings (defer must include a one-line justification)
- **Acknowledge** ✅ no-concerns categories (no action needed)

After the review, run the 7-item self-audit (mechanical preconditions), then commit and push.

## How to invoke

When the user types `/local-review`, do the following:

1. **Preflight checks.** Abort with a clear message if any of these fail — running an agent against a degenerate input wastes a tool call and produces noise:

   ```bash
   git rev-parse --is-inside-work-tree         # must be inside a git repo
   git rev-parse --verify origin/main           # origin/main must resolve locally
   ```

   Then attempt `git fetch origin main` to refresh; if it fails (offline, auth), warn but continue using the local `origin/main`.

   Capture the diff scope:

   ```bash
   git diff origin/main...HEAD --stat           # summary of branched commits
   git diff --stat                              # summary of uncommitted changes
   git status --short                           # untracked files
   git branch --show-current                    # current branch name
   ```

   If **all** of `git diff origin/main...HEAD --stat`, `git diff --stat`, and `git status --short` are empty, abort: "No diff to review against `origin/main` — branch is clean."

2. **Spawn a `general-purpose` agent** with the review prompt below. **Substitute the four placeholders** before passing — the agent receives a fully-resolved string, never the literal angle-bracket tokens:

   - `<BRANCH_NAME>` → output of `git branch --show-current`
   - `<BRANCHED_DIFF_STAT>` → output of `git diff origin/main...HEAD --stat` (or "(no committed changes on this branch yet)" if empty)
   - `<UNCOMMITTED_DIFF_STAT>` → output of `git diff --stat` (or "(no uncommitted changes)" if empty)
   - `<UNTRACKED_LIST>` → output of `git status --short` (or "(no untracked files)" if empty)

   The agent reads the actual files in full (not just the diff hunks) so it can judge against surrounding context. Use `subagent_type="general-purpose"` and `description="Pre-push local review"`.

3. **Relay the agent's findings to the user verbatim** (don't summarize away severity tags). Then propose: address findings, defer with justification, or proceed.

4. **Record the outcome** — when opening the PR, include a "Local review" line in the PR description: `Local review: N 🔴 findings (fixed), N ⚠️ findings (fixed: M, deferred: K — justifications below)`.

## The review prompt to feed the agent

Pass this prompt to the `general-purpose` agent **with the four placeholders fully substituted** — never pass the literal angle-bracket tokens:

```text
Pre-push code review of the PinballWizard changes on the current branch
(c:\projects\PinballWizard, branch: <BRANCH_NAME>). Compare against
origin/main. Read the files in full — diffs lose surrounding context.

Branched commits (vs origin/main):
<BRANCHED_DIFF_STAT>

Uncommitted changes:
<UNCOMMITTED_DIFF_STAT>

Untracked files:
<UNTRACKED_LIST>

Critique each of the following categories. For each, give a verdict
(✅ no concerns / ⚠️ minor / 🔴 must fix before push) and cite specific
file paths + line numbers. Don't say "looks good" — actively look for
problems. If you find none of consequence, say so explicitly.

1. **Design & architecture**: Clean Architecture conformance (any leaks
   of Infrastructure types into Application, Application into Core,
   Cosmos types into scrapers, etc.). Are abstractions at the right
   layer? Is the new public surface minimal?

2. **Test quality**: Do tests exercise behavior or just structure? A
   test named "rejects merch" must include merch in the input. Any
   tests that just re-state what the code does? Edge cases obviously
   missing? Test names follow the project's `Method_State_Expectation`
   convention?

3. **Error handling & blast radius**: Are exceptions handled at the
   right boundaries? Any swallowed exceptions (`catch { }` /
   `catch (Exception) { }` with no log)? Any case where a single bad
   record / page / row would abort the whole run when it shouldn't?
   Any `OperationCanceledException` paths swallowed?

4. **Sibling drift**: If this PR copies a sibling pattern (manufacturer
   scrapers, repository implementations, ADRs), diff against the
   closest existing implementation. Look for: different log message
   shapes, different ctor null-check patterns, missing TryExtractAsync
   wrappers, different yield/break semantics, unused fields. Drift is
   the silent failure mode.

5. **Politeness invariants** (HTTP code only): Every outbound HTTP
   request must route through `IPolitenessGate` (the scraper extends
   `PoliteScraperBase` and uses `GetStringPolitelyAsync` /
   `SendPolitelyAsync`). Polite User-Agent set on the typed
   `HttpClient`. Robots.txt path defaulted. No `HttpClient.GetAsync`
   bypass. (Not applicable to non-HTTP changes.)

6. **Provenance preservation**: Per the project's locked principle,
   every piece of data must be traceable to its source URL. Any data
   path that drops `Source` / `DiscoveryUrl` / `DiscoveryContext` /
   `GameSlug` is a 🔴.

7. **Comments policy**: Comments should explain WHY, not WHAT. Stale
   "TODO: ..." pointing at fixed work? "This used to..." references?
   (Do NOT flag missing XML doc comments — see
   `memory/feedback_no_xml_docs.md`; XML docs are explicitly out of
   scope for this project.)

8. **Security smells**: Any logging of secrets / tokens / connection
   strings / PII? Any raw input into SQL / command / shell paths
   without sanitization? Any unsafe deserialization? Any auth check
   bypass paths?

9. **Performance smells**: Sync-over-async (`.Result` /
   `.GetAwaiter().GetResult()` outside main entry points)? N+1 loops
   on a repository? Unbounded async loops without cancellation
   propagation? Allocation in hot paths that have a no-alloc
   alternative?

10. **Configuration discipline**: Every new `*Options` property
    actually read by code (the `PinballMachinesCollectionSlug` bug
    that motivated this skill was a dead config). Magic numbers that
    should be config? `[Required]` on properties that are required?
    `[Range]` on bounded numerics?

11. **Cosmos surface conformance** (when the PR touches Cosmos —
    new `Container` registration, new `IRepository<T>`, new query,
    `CosmosClientOptions` change, or a new write path): verify
    against [ADR-0025](docs/adr/0025-cosmos-for-user-delight.md).
    Specific checks:
    - Cross-partition query on a user-facing path without explicit
      RU-cost justification AND latency budget in the PR description
      → 🔴 (use a point-read or add a materialized-view container).
    - New write-heavy container without a selective indexing policy
      → ⚠️ (default policy indexes every property; usually wasteful).
    - New repo method calling Cosmos SDK directly without routing
      through `CosmosRepository<T>.ExecuteWithMetricsAsync` (or a
      base method that does) → ⚠️ (RU + duration + `CosmosException.Diagnostics`
      capture won't show up on `pinwiz.cosmos.*` / structured log
      scope).
    - 2nd writer of `machines` (or any container with a documented
      single-writer property in ADR-0011) without ETag conditional
      writes via `ItemRequestOptions.IfMatchEtag` → 🔴 (lost-update
      protection per ADR-0025 § 7).
    - `EnableContentResponseOnWrite=true` re-introduced (the SDK
      default) without a caller that consumes `response.Resource`
      → ⚠️ (wastes a round-trip + RU per write).
    - New container without a documented TTL decision (set OR
      explicitly null with rationale) → ⚠️.

For each 🔴 finding, give a specific recommended fix (not just "this
is broken"). For ⚠️ findings, give the fix plus the cost-of-deferring
so the human can choose.

End with a one-line summary: "X 🔴 / Y ⚠️ / Z categories ✅".
Return the critique under 1500 words.
```

## What the skill does NOT do

- It is **not** a substitute for `/security-review` (deeper dedicated security pass) or `/ultrareview` (multi-agent cloud review). Both remain available for when the change warrants more rigor.
- It does **not** run tests or build the project. Those are separate steps in the dev cycle and should already be green before invoking this skill.
- It does **not** auto-fix findings — it produces a critique you act on. Auto-fix would make it harder to learn from the patterns the review surfaces.

## Background

Motivated by the dead `PinballMachinesCollectionSlug` config that shipped through three PRs (#31 / #32 / #33) before being caught in a session-start audit run during PR #34 work. A push-time review with this prompt would have caught it the first time. The 7-item self-audit checklist (mechanical) was the first response; this skill (qualitative) is the second. Memory: `feedback_pre_pr_self_audit.md`.
