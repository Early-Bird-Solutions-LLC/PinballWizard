<!--
Thanks for contributing. Fill in all three sections below.
PR title must follow Conventional Commits: <type>(<scope>) short imperative summary
Valid types: feat, fix, chore, docs, refactor, test
Scope is a short module name (scraper, catalog, downloader, http, etc.) — never a ticket ID
-->

## Summary

<!--
What changed and why. One paragraph is usually enough. Focus on the WHY;
the diff already shows the WHAT. If this resolves an open issue, link it.
-->

## Test Plan

<!--
What you ran, what you observed, and (for any new behavior) the test that
protects it. Concrete commands or steps preferred over "tested locally."
For UI changes, attach screenshots or short clips.
-->

## Out of Scope

<!--
Anything intentionally NOT addressed by this PR, so reviewers don't ask
for it. If everything is in scope, write "nothing intentionally deferred."
-->

## Checklist

- [ ] CI is green (build + test + coverage + CodeQL + sanitization)
- [ ] PR title follows the Conventional Commits format above
- [ ] If this is a new architectural decision, an ADR has been added under [`docs/adr/`](../docs/adr/)
- [ ] If user-visible behavior changes, [`README.md`](../README.md) and/or [`docs/`](../docs/) are updated in the same PR
- [ ] If a memory in `~/.claude/projects/c--earlybird-PinballWizard/memory/` is now stale, it has been updated or removed in the same PR
- [ ] No `TODO` / `FIXME` / commented-out code committed
- [ ] No new entries in `<NoWarn>` without a comment explaining why and the removal criterion

### Pre-push self-audit (additive PRs)

Required for any PR that adds a scraper, options class, extension, or other additive surface. See [`CLAUDE.md` § PR self-audit](../CLAUDE.md#pr-self-audit-pre-push-blocking) and `memory/feedback_pre_pr_self_audit.md` for the why.

#### Step 0 — `/local-review` (qualitative)

- [ ] Ran `/local-review` and addressed every 🔴 finding before push
- [ ] Local review outcome:
  <!-- e.g., "0 🔴 / 2 ⚠️ (both fixed) / 8 categories ✅" -->
  <!-- For deferred ⚠️ items, list each with a one-line justification -->

#### Step 1 — `/standards-audit` (mechanical gate)

- [ ] Ran `/standards-audit`; no 🔴 rule failed (the former 7-item checklist is now machine-checked rules under `.claude/standards/`)
- [ ] `/standards-audit` verdict:
  <!-- e.g., "Verdict: 0 🔴 fail / 0 ⚠️ fail / 6 pass / 3 qual" -->
