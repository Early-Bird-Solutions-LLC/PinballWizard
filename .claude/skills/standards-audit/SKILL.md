---
name: standards-audit
description: Mechanical pre-commit/pre-push gate. Resolves the diff to applicable PinballWizard standards by glob, runs each applicable rule's CHECK command, and emits a verdict table. Refuses to proceed on any 🔴 fail. Runs alongside the qualitative /local-review. Invoke before any commit or push of production code.
---

# /standards-audit — mechanical standards gate

Enforcement counterpart to `/local-review`. `/local-review` judges design
qualitatively; this skill runs the deterministic CHECK commands.
Contract: [`../../standards/pinball-standards-protocol.md`](../../standards/pinball-standards-protocol.md).

## When to invoke

- Pre-commit (staged diff) and pre-push / pre-PR (branch diff), per the
  protocol's session lifecycle. Replaces the mechanical half of PR-AUDIT.

## Procedure

1. **Compute the changed-file set:**

   ```bash
   git diff --name-only origin/main...HEAD
   git diff --name-only
   git status --short
   ```

2. **Resolve applicable standards.** For each `.claude/standards/*/STANDARD.md`,
   read its frontmatter `applies-to:` globs. A standard is applicable if any
   changed file matches any glob. If none match, report
   **"clean — no governed surface touched"** and stop.

3. **Run each applicable rule's CHECK.** For every RULE block in an applicable
   `STANDARD.md`, run its `CHECK:` command. Rules whose CHECK is
   `(qualitative — /local-review)` are reported as `QUAL` (deferred to
   `/local-review`), not run here.

4. **Emit the verdict table** — one row per rule:

   ```
   === Standards Audit (branch: <branch>) ===
   RULE       SEV  RESULT  EVIDENCE
   POLITE-01  🔴   PASS    no bare HttpClient verb in Scraping/
   COSMOS-02  🔴   FAIL    CrossPartitionQueryAllowListTests: 1 failing
   TEST-02    🔴   QUAL    deferred to /local-review
   ...
   Verdict: <N> 🔴 fail / <M> ⚠️ fail / <K> pass / <Q> qual
   ==========================================
   ```

5. **Gate.** Any 🔴 FAIL ⇒ refuse to proceed; name the rule ID + evidence +
   the REF, and stop before commit/push. ⚠️ FAIL ⇒ report and require a
   one-line justification to continue.

## What this skill does NOT do

- It does not replace `/local-review` (qualitative design review) — run both.
- It does not auto-fix — it reports and gates.
- It does not score or emit a compliance % (no fleet machinery).
