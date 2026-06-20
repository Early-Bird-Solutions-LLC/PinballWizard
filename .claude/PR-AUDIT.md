# PinballWizard — PR Self-Audit (pre-push, BLOCKING)

Before pushing any PR that adds production code, run both gates and treat 🔴
as blocking. The mechanical checklist that used to live here is now the
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

## Recording the outcome

The PR description records: `/local-review` finding counts (🔴 fixed, ⚠️
fixed/deferred) and the `/standards-audit` verdict line. The PR template at
`.github/PULL_REQUEST_TEMPLATE.md` includes these lines.
