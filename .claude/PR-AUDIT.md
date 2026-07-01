# PinballWizard — PR Self-Audit (pre- and post-push, BLOCKING)

Before pushing any PR that adds production code, run both pre-push gates (Steps 0–1)
and treat 🔴 as blocking; after creating the PR, run the post-push code-scanning
triage (Step 2). The mechanical checklist that used to live here is now the
machine-checkable rule set under [`standards/`](standards/README.md);
`/standards-audit` runs it. Background: `memory/feedback_pre_pr_self_audit.md`.

## Step 0 — Qualitative review

Run `/local-review`. Fix every 🔴; fix-or-defer (with one-line justification)
each ⚠️. Catches design/architecture/drift a grep cannot.

## Step 1 — Mechanical standards audit

Run `/standards-audit`. It resolves the diff to applicable standards, runs
each rule's CHECK, and refuses to proceed on any 🔴 fail. This replaces the
former 14-item checklist — every item migrated to a rule:

- old items 2, 4, 5 → TEST-03, TEST-02 / TEST-01 (POLITE-* is new coverage with no PR-AUDIT predecessor)
- old item 8 → COSMOS-02..04
- old items 6, 7 → DLV-03, DLV-01
- old items 11, 12 → DLV-02, DLV-05
- old items 1, 3, 13, 14, 9, 10 → /local-review qualitative categories +
  wave-2 standards (frontend-blazor, community-posture) when promoted

## Step 2 — Post-push code-scanning triage (BLOCKING — after `gh pr create`)

GitHub's code scanning (`CodeQL` / `github-advanced-security[bot]` +
`github-code-quality[bot]`) runs **server-side, after the PR exists** — new-code-scoped,
so it flags only what this PR changed. Its findings are NOT visible to the
pre-push gates above. **Do not consider the PR done until you have fetched and
triaged them yourself** — a PR must never sit with un-actioned bot findings
waiting for a human to notice.

After creating the PR (and adding the `claude-code` label):

```bash
# 1. Wait for the code-scanning "Analyze" jobs to finish.
gh pr checks <PR> --watch --fail-fast=false

# 2. Fetch the findings (inline bot review comments on this PR).
gh api repos/{owner}/{repo}/pulls/<PR>/comments \
  --jq '.[] | select(.user.login|test("github-advanced-security|github-code-quality"))
        | "\(.path):\(.line // .original_line)  \(.body|gsub("\n";" ")|.[0:200])"'
```

Triage **every** finding — one of:

- **Fix** — push a commit `chore(<scope>) address PR #<PR> code-scanning findings`
  (convention B: no squash/force-push; the fix re-runs code scanning and resolves
  the alert). Prefer this for anything real.
- **Dismiss (false positive / won't-fix)** — only with a written justification,
  via the code-scanning alerts API (alert number `<N>` is the
  `.../code-scanning/<N>` in the finding's "Show more details" link):

  ```bash
  gh api repos/{owner}/{repo}/code-scanning/alerts/<N> -X PATCH \
    -f state=dismissed \
    -f dismissed_reason="<false positive|won't fix|used in tests>" \
    -f dismissed_comment="<why — cite the invariant / SUT-ownership / precedent>"
  ```

  `dismissed_reason` accepts exactly those three values (GitHub REST API). Dismiss
  only when the finding is genuinely wrong (e.g. an `IDisposable` whose ownership
  transfers to the system-under-test, which disposes it) — never to silence a real
  issue.

The gate clears only when code scanning is green OR every finding is fixed /
dismissed-with-reason.

> **Shift-left note:** enabling the matching Roslyn analyzers (`CA2000` dispose,
> `CA1031` generic-catch) in the `-warnaserror` build would catch two of these
> classes locally before push — but they currently surface **428** pre-existing
> violations (322 CA2000 + 106 CA1031), so that is a separate baseline-and-cleanup
> initiative, not a drop-in. Until then, Step 2 is the safety net.

## Recording the outcome

The PR description records: `/local-review` finding counts (🔴 fixed, ⚠️
fixed/deferred) and the `/standards-audit` verdict line. The PR template at
`.github/PULL_REQUEST_TEMPLATE.md` includes these lines. The final Post-PR
Verification output states the Step 2 result (code-scanning: green / N fixed /
M dismissed-with-reason).
