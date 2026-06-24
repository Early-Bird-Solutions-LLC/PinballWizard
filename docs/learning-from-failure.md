# Learning from failure — how incidents become permanent guarantees

This is the deep-dive behind the **Memory** and guardrails controls in
[`ai-development-model.md`](ai-development-model.md), and the complement to
[`docs/runbooks/01-incident-response.md`](runbooks/01-incident-response.md): the runbook is
*what to do during* a live incident; this document is *how the project learns so the same
failure cannot recur.*

## The loop in one screen

Every failure here — a red CI check, a production outage, or a near-miss caught in review —
runs through the same loop:

**failure → root cause → a memory entry → a *mechanical* guard → recurrence is now impossible.**

The root cause is found and written down as a [memory](ai-development-model.md) entry so the
knowledge survives the session that found it. Then — and this is the load-bearing step — the
lesson is converted into a **mechanical** guard: a test, a standing rule, or a CI gate that
fails loudly the next time the same mistake is attempted.

*Mechanical* is the whole point. A cultural "remember to check X" decays — and it decays
fastest across stateless AI sessions, where each session starts without the scar tissue of
the last. A failing build does not decay. The skeptic's sharpest question about AI-authored
code is *"won't it just make the same mistake again?"* — and the honest answer is *only once,
because the second attempt hits a guard.* This is that countermeasure, shown with real cases.

## The registry

A pointer index of real incident→guard conversions. Each row's durable rationale lives in a
memory entry (cited as `memory: <name>`); this table is the index, not a second copy.

| Incident | Date | Root cause | Guard that now prevents it | Type |
|---|---|---|---|---|
| Citation outage (camelCase fixture drift) | 2026-06-10 | Extractor probed PascalCase, but a live-data URL migration made runtime JSON camelCase and starved the regex fallback → 100% refusals with green unit tests | Run the evaluation harness after every live-data migration (`memory: project_citation_outage_2026_06_10`) | process gate |
| MudPopoverProvider circuit crash (prod outage, PR #401) | 2026-06 | Per-page interactivity under a static layout → "Missing MudPopoverProvider" circuit crash; a browser-gated E2E canary skip let it ship | [`RenderModeConventionTests`](../tests/PinballWizard.Web.Tests/StaticAssets/RenderModeConventionTests.cs) + [`LayoutProviderRenderModeTests`](../tests/PinballWizard.Web.Tests/StaticAssets/LayoutProviderRenderModeTests.cs) | build-time test |
| Worktree contamination (~30 agents of work lost) | 2026-06-10 | Two concurrent sessions shared one working tree; one session's "discard changes" wiped the other's uncommitted work | [`.claude/rules/parallel-sessions.md`](../.claude/rules/parallel-sessions.md) — one tree per session; never discard tracked changes you didn't make (`memory: feedback_worktree_contamination_pattern`) | standing rule |
| Required-check red on a new container (#481) | 2026-06 | A Cosmos container added without updating the test that pins the exact container set; a filtered local test subset missed it | Run the full CI-equivalent suite before push, pinned by [`CosmosOptionsTests`](../tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosOptionsTests.cs) (`memory: feedback_run_full_ci_suite_before_push`) | CI gate + test |
| Godzilla mislabel | resolved 2026-06-10 (PR #329) | Manufacturer-unqualified lookup + alias-ID drift mislabeled a machine | Manufacturer-qualified lookups + alias-ID normalization + relink/backfill (`memory: project_godzilla_mislabel_root_cause`) | fix + data guard |
| PR-merge race after a CI fix (#121) | 2026-06 | The merge fired before the new check registered, merging a still-red state | Wait-for-new-run / `--auto` discipline (`memory: feedback_pr_merge_race_after_fix`) | process rule |
| Six standing-state guardrails | 2026-06-10 | Drift in standing artifacts (hardcoded ADR ranges, metric hygiene, repo hygiene, handoffs) went unguarded | Doc-conformance tests + metric-hygiene rule + repo-hygiene gate + handoff validation (`memory: project_guardrails_2026_06_10`) | tests + gates |

## Deep dives

Three cases, one per failure class.

### Runtime crash → compile-time guarantee (MudPopoverProvider, PR #401)

**What happened.** A per-page-interactive admin page rendered under a static layout threw
"Missing MudPopoverProvider" and crashed the Blazor circuit — in production, on a
customer-facing surface.

**Root cause.** A render-mode/provider mismatch: the popover-capable components needed a
provider that the static layout did not supply. The failure shipped because the only thing
that would have caught it was a browser-gated end-to-end canary — and that canary was skipped.

**The guard.** [`RenderModeConventionTests`](../tests/PinballWizard.Web.Tests/StaticAssets/RenderModeConventionTests.cs)
(with [`LayoutProviderRenderModeTests`](../tests/PinballWizard.Web.Tests/StaticAssets/LayoutProviderRenderModeTests.cs)) —
an assembly-scanning test that runs as part of the ordinary build.

**Why that shape.** The bug escaped *because* its only guard was skippable. The durable fix
had to be a guard with no environment to skip and no browser to be flaky: a test that fails
the normal build. The earliest possible gate wins.

### Silent regression → process gate (citation outage, 2026-06-10)

**What happened.** The Wizard began refusing ~100% of queries — no citations — while the
entire unit-test suite stayed green.

**Root cause.** The citation extractor probed PascalCase JSON property names, but a live-data
URL migration changed the runtime payload shape to camelCase, which starved the regex
fallback. Nothing in the structural unit tests exercised the real, migrated data shape.

**The guard.** Run the evaluation harness — end-to-end, over real data — after every
live-data migration.

**Why that shape.** No unit test could have caught this; the contract that broke was the
shape of *live* data, not of the code. The right guard is a process gate bound to the exact
trigger (a data migration) that an eval, not a unit test, can verify.

### AI-orchestration hazard → isolation rule (worktree contamination)

**What happened.** Two concurrent Claude sessions shared a single working tree. One session's
routine "discard changes" wiped roughly 30 agents' worth of the other session's uncommitted
work — recovered only by replaying tool-call transcripts.

**Root cause.** A shared mutable workspace across parallel agents: from inside either session,
the other's edits look exactly like stray noise.

**The guard.** The [`parallel-sessions`](../.claude/rules/parallel-sessions.md) rule — one
working tree per session, and never discard tracked changes you did not make.

**Why that shape.** This is a failure class unique to multi-agent work, so the guard is a
standing rule the agent enforces every session. It is the concrete answer to *"how do multiple
AI agents work in parallel without destroying each other's work?"*

## What makes a good guard

The loop applies four criteria when converting a lesson into a guard:

- **Mechanical over cultural.** A note in a doc is a hope; a failing check is a guarantee.
  Favor the check.
- **Earliest gate that catches it.** build-time test > full test-suite > CI-only > human
  review > runbook. The earlier it fires, the cheaper the failure and the less it can slip.
- **Exercise the actual failure.** A regression test that does not reproduce the original bug
  is theater. The guard must fail on the real defect, not merely assert structure around it.
- **Record the why.** The rationale goes in a memory entry so a future cleanup doesn't quietly
  delete a guard whose origin was forgotten — the guard and its reason travel together.
