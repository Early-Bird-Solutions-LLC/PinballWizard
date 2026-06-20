---
description: Report vendored .claude config files that have drifted behind their pinned upstream SHA
---
<!-- authored-for: PinballWizard (not vendored) -->

# Check Drift

Report on vendored `.claude` config files that have drifted behind their pinned upstream SHA.

## Command

Run the drift checker from the repo root:

```bash
python scripts/check_claude_config_drift.py .claude
```

## What it reports

For each vendored file (those with a `vendored-from:` header), the script compares the SHA pinned in the file's provenance comment against the current HEAD of the upstream `APS.JimClaudeCodeConfig` repo (which must be available locally at a sibling path).

Each file is reported as one of:

| Status | Meaning |
|---|---|
| **current** | The pinned SHA matches the upstream HEAD for that file — no drift |
| **behind** | The upstream file has been updated past the pinned SHA; this vendored copy may be stale |
| **source-missing** | The upstream `APS.JimClaudeCodeConfig` repo is not available locally — script degrades visibly, does NOT silently pass |

"Behind" does **not** mean the file is wrong — it means a human decision is required (see below).

## What to do with "behind" results

Drift is the accepted cost of the vendoring model (per ADR-0040). When a file shows **behind**:

1. Review the upstream diff: `git -C <upstream-repo-path> log --oneline <pinned-sha>..HEAD -- global/<path>`
2. Decide: re-vendor (the upstream change is relevant) or hold (the upstream change doesn't apply here)
3. **To re-vendor:** copy the upstream file, adapt it for PinballWizard, bump the `vendored-from:` header SHA to the new upstream HEAD, and re-run the frontmatter guard (`python scripts/check_claude_frontmatter.py .claude`)
4. **To hold intentionally:** leave the SHA as-is; document why in a comment in the file if the divergence is significant

This is a **manual judgment call** — not every upstream change is appropriate to pull in. The point of the drift check is visibility, not forced updates.

## Notes

- This is a **local-only command** — CI cannot access the private upstream repo, so drift checking is not automated. Run it periodically (e.g. before major config changes or when onboarding new skills).
- Authored (non-vendored) files like this one have no `vendored-from:` header and are not reported by the script.
- If the upstream repo path is wrong or missing, the script will print an error for each missing source rather than silently passing. A silent pass on missing sources would be a fallback that hides a failure — which is prohibited by project invariant #17.
