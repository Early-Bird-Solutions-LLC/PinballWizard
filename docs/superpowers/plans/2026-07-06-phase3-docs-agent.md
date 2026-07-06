# Phase 3 — Push-Triggered Docs Agent + Guard + ADR-0051 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A GitHub Actions agent that, on each merge to main (plus a weekly sweep), reads the merged diff and opens an auto-mergeable docs-only PR when a descriptive doc has gone untruthful — bounded mechanically by an allowlist guard so auto-merge is safe — and an ADR recording why this is a CI agent and not a Foundry product agent.

**Architecture:** Reuse the repo's existing keyless Claude-Code-in-CI pattern verbatim (GitHub OIDC → Anthropic WIF exchange → `anthropics/claude-code-action@v1`, per `claude.yml` and ADR-0047). The agent may edit only descriptive docs (allowlist), never ADRs/decision-log/code/workflows; it flags decision-worthy changes as human follow-ups instead of authoring them. A separate `docs-agent-guard.yml` required check fails any `docs-agent/*` PR that touches a file outside the allowlist, and instantly passes on non-agent branches (no noop companion needed). Failure opens a pinned issue (canary.yml pattern). No self-retrigger: merged agent PRs touch only docs paths, which don't match the push trigger's `src/**`/`infra/**` filter.

**Tech Stack:** GitHub Actions, `anthropics/claude-code-action@v1`, GitHub OIDC, `gh` CLI, jq/curl (WIF exchange), Markdown (ADR).

## Global Constraints

- **Keyless auth only** — no `ANTHROPIC_API_KEY` secret exists (deleted per ADR-0047). Use the exact 3-step WIF exchange from `claude.yml`. The four `vars.ANTHROPIC_*` Actions Variables already exist.
- **Agents open PRs, never push to main** (repo convention). Permissions: `contents: write`, `pull-requests: write`, `id-token: write`.
- **WIF token lifetime is 600s** — scope each run to a single diff. Do not "fix" a timeout by inflating it without measuring (`timeout-debugging` rule); if weekly sweeps genuinely exceed it, extend lifetime in the Anthropic Console with justification.
- **Allowlist (agent may edit):** `docs/**` except `docs/adr/**` and `docs/decision-log.md`; `README.md`; `CLAUDE.md`; `docs/engineering-manifest.json`. Everything else is forbidden.
- Bot PRs get the `claude-code` label; expect `github-code-quality[bot]` review events (the PR-Feedback-Triage loop guard already handles these).
- Personal identity; branch `docs/refresh-and-docs-agent`. `.github/**` changes are CODEOWNERS-gated to `@JimKeeley` — human review required on this phase's PR.
- No-masking-fallbacks (invariant #17): a failed agent run must surface loudly (pinned issue), never silently no-op.

## File Structure

- Create `docs/adr/0051-agent-categories-foundry-vs-ci.md` — the decision record.
- Modify `docs/adr/README.md` — index the new ADR (guarded by `AdrReadme_IndexesEveryAdrFile`).
- Modify `docs/ai-development-model.md` — "Two kinds of agents" prose section.
- Create `.github/workflows/docs-agent.yml` — the agent.
- Create `.github/workflows/docs-agent-guard.yml` — the allowlist fence.
- Create `.github/docs-agent-allowlist.txt` — the single source of truth for allowed paths (read by the guard, referenced by the agent prompt).
- Test: `tests/PinballWizard.Core.Tests/Domain/DocConformanceTests` already covers the ADR-index invariant (verify green after ADR add).

---

### Task 1: ADR-0051 + index + dev-model echo

**Files:**
- Create: `docs/adr/0051-agent-categories-foundry-vs-ci.md`
- Modify: `docs/adr/README.md`
- Modify: `docs/ai-development-model.md`

**Interfaces:**
- Consumes: existing ADR format (see any recent ADR, e.g. `0047`, `0050`).
- Produces: the authoritative record referenced by the workflow header comment (Task 2) and the dev-model doc.

- [ ] **Step 1: Read an existing ADR for exact format**

Run: `sed -n '1,30p' docs/adr/0047-anthropic-wif-github-actions.md && sed -n '1,20p' docs/adr/README.md`
Expected: heading/status/context/decision structure + the README index row format.

- [ ] **Step 2: Write ADR-0051**

Create `docs/adr/0051-agent-categories-foundry-vs-ci.md`, Status **Accepted**, dated 2026-07-06. Content: the decision that PinballWizard has two agent categories — (1) **Foundry product agents** (serve user traffic on pinwiz.ai; ADR-0014/0015; managed identity, eval, model routing, SLOs) and (2) **Claude Code CI automation agents** (act on git events; ephemeral Actions runner; `claude-code-action@v1` + WIF per ADR-0047; open PRs). Include the differentiation table (trigger / acts-on / runtime / needs / blast-radius), the classification rule for future agents, the shared-invariant note (both model-agnostic; both keyless), the precedent (`claude.yml`, `pr-feedback-triage.yml` are category-2; the docs-agent is the third instance), and the rejected alternative (docs-agent in Foundry → reinvents repo PAT/git/PR plumbing in a product-runtime orchestrator). Reference ADR-0014, ADR-0015, ADR-0047. Keep it short (bias per the ADR README).

- [ ] **Step 3: Index it in the ADR README**

Add the `0051` row to `docs/adr/README.md` in the existing table/list format.

- [ ] **Step 4: Add the "Two kinds of agents" section to the dev-model doc**

In `docs/ai-development-model.md`, add a concise prose section framing "Foundry runs the product; Claude Code builds and maintains it," naming the docs-agent as the worked example and linking ADR-0051.

- [ ] **Step 5: Verify ADR-index conformance + links**

Run: `dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~DocConformanceTests" -v minimal && node tools/docs/check-links.mjs`
Expected: PASS + exit 0 (`AdrReadme_IndexesEveryAdrFile` green).

- [ ] **Step 6: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  add docs/adr/0051-agent-categories-foundry-vs-ci.md docs/adr/README.md docs/ai-development-model.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "docs(adr) ADR-0051 two agent categories: Foundry product vs CI automation"
```

---

### Task 2: The allowlist file + guard workflow

**Files:**
- Create: `.github/docs-agent-allowlist.txt`
- Create: `.github/workflows/docs-agent-guard.yml`

**Interfaces:**
- Produces: a required check `docs-agent-guard` that passes instantly on non-`docs-agent/*` branches and fails on any out-of-allowlist change on `docs-agent/*` branches.
- Consumes: nothing from earlier tasks (the allowlist file is authored here).

- [ ] **Step 1: Author the allowlist (glob patterns, one per line)**

Create `.github/docs-agent-allowlist.txt`:
```
README.md
CLAUDE.md
docs/engineering-manifest.json
docs/**
!docs/adr/**
!docs/decision-log.md
!docs/superpowers/**
```
(`!` = explicitly denied even though under `docs/**`. `docs/superpowers/**` is denied because those are working specs/plans, not product docs.)

- [ ] **Step 2: Write the guard workflow**

Create `.github/workflows/docs-agent-guard.yml`:
```yaml
name: Docs Agent Guard

# Required check. On PRs from docs-agent/* branches, fails if any changed file
# falls outside .github/docs-agent-allowlist.txt. On every other branch it
# passes instantly (no noop companion needed). This is the mechanical fence
# that makes docs-agent auto-merge safe — the agent's authority is bounded
# here, not by prompt obedience. See ADR-0051 / docs-agent.yml.

on:
  pull_request:
    types: [opened, synchronize, reopened]

permissions:
  contents: read

jobs:
  guard:
    name: Enforce docs-agent allowlist
    runs-on: ubuntu-latest
    steps:
      - name: Skip for non-agent branches
        id: gate
        run: |
          if [[ "${{ github.head_ref }}" == docs-agent/* ]]; then
            echo "enforce=true" >> "$GITHUB_OUTPUT"
          else
            echo "enforce=false" >> "$GITHUB_OUTPUT"
            echo "Not a docs-agent branch — guard passes."
          fi

      - name: Checkout
        if: steps.gate.outputs.enforce == 'true'
        uses: actions/checkout@v6
        with:
          fetch-depth: 0

      - name: Verify changed files are within allowlist
        if: steps.gate.outputs.enforce == 'true'
        run: |
          base="${{ github.event.pull_request.base.sha }}"
          head="${{ github.event.pull_request.head.sha }}"
          changed=$(git diff --name-only "$base" "$head")
          echo "Changed files:"; echo "$changed"
          # Evaluate each changed path against the allowlist using git's
          # pathspec matching (supports ** and ! negation via a temp gitignore).
          deny=".github/docs-agent-allowlist.txt"
          violations=""
          while IFS= read -r f; do
            [ -z "$f" ] && continue
            # A path is allowed iff `git check-ignore` (with allowlist as the
            # ignore file, inverted semantics) says it matches an allow pattern
            # and not a deny (!) pattern. Implement with a small matcher:
            if ! node .github/scripts/allowlist-match.mjs "$deny" "$f"; then
              violations="${violations}\n${f}"
            fi
          done <<< "$changed"
          if [ -n "$violations" ]; then
            echo -e "::error::docs-agent PR touches files outside the allowlist:$violations"
            exit 1
          fi
          echo "All changed files are within the allowlist."
```

- [ ] **Step 3: Write the tiny allowlist matcher (deterministic, testable)**

Create `.github/scripts/allowlist-match.mjs`:
```javascript
// Usage: node allowlist-match.mjs <allowlist-file> <path>
// Exit 0 if <path> is allowed, 1 if denied. Last matching pattern wins;
// lines starting with ! are denials. Uses minimatch-free glob via a small
// regex compile (no external deps in the runner).
import { readFileSync } from 'node:fs';
const [, , listFile, target] = process.argv;
const lines = readFileSync(listFile, 'utf8').split('\n').map(s => s.trim()).filter(Boolean);
function toRegex(glob) {
  let re = glob.replace(/[.+^${}()|[\]\\]/g, '\\$&')
               .replace(/\*\*/g, ' ')
               .replace(/\*/g, '[^/]*')
               .replace(/ /g, '.*');
  return new RegExp('^' + re + '$');
}
let allowed = false;
for (const line of lines) {
  const deny = line.startsWith('!');
  const pat = deny ? line.slice(1) : line;
  if (toRegex(pat).test(target)) allowed = !deny;
}
process.exit(allowed ? 0 : 1);
```

- [ ] **Step 4: Sanity-check the matcher locally**

Run:
```bash
node .github/scripts/allowlist-match.mjs .github/docs-agent-allowlist.txt README.md; echo "README=$?"          # 0
node .github/scripts/allowlist-match.mjs .github/docs-agent-allowlist.txt docs/vision.md; echo "vision=$?"      # 0
node .github/scripts/allowlist-match.mjs .github/docs-agent-allowlist.txt docs/adr/0051-x.md; echo "adr=$?"     # 1
node .github/scripts/allowlist-match.mjs .github/docs-agent-allowlist.txt src/Program.cs; echo "src=$?"        # 1
node .github/scripts/allowlist-match.mjs .github/docs-agent-allowlist.txt docs/decision-log.md; echo "dl=$?"    # 1
```
Expected: `README=0 vision=0 adr=1 src=1 dl=1`.

- [ ] **Step 5: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  add .github/docs-agent-allowlist.txt .github/workflows/docs-agent-guard.yml .github/scripts/allowlist-match.mjs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "ci(docs-agent) allowlist fence guard + matcher"
```

---

### Task 3: The docs-agent workflow

**Files:**
- Create: `.github/workflows/docs-agent.yml`

**Interfaces:**
- Consumes: the WIF exchange steps (copy verbatim from `claude.yml`), the allowlist file (Task 2), ADR-0051 (Task 1, referenced in header comment).
- Produces: `docs-agent/*` PRs with the `claude-code` label and auto-merge enabled.

- [ ] **Step 1: Author the workflow**

Create `.github/workflows/docs-agent.yml`:
```yaml
name: Docs Agent

# Push-triggered documentation updater. On each merge to main touching code or
# infra, reads the merged diff and opens an auto-mergeable docs-only PR when a
# DESCRIPTIVE doc has gone untruthful. Weekly sweep catches misses.
#
# This is a CI development-automation agent, NOT a Foundry product agent — it
# acts on git events and edits markdown, so it lives in Actions with
# claude-code-action, not in Foundry. See ADR-0051 for the category boundary.
#
# Auth: GitHub OIDC → Anthropic WIF (ADR-0047), identical to claude.yml. No
# static key. Authority is bounded by .github/docs-agent-allowlist.txt and
# enforced by docs-agent-guard.yml — this is why auto-merge is safe.

on:
  push:
    branches: [main]
    paths:
      - 'src/**'
      - 'infra/**'
      - '.github/workflows/**'
  schedule:
    - cron: '23 8 * * 1'   # Mondays 08:23 UTC — weekly full sweep (low-traffic slot; confirm)
  workflow_dispatch:

permissions:
  contents: write
  pull-requests: write
  id-token: write

concurrency:
  group: docs-agent-main
  cancel-in-progress: false   # each merge deserves its own analysis; queue, don't drop

jobs:
  docs-agent:
    name: Update docs from merged changes
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v6
        with:
          fetch-depth: 0   # need history for the diff range

      - name: Fetch GitHub OIDC token
        uses: actions/github-script@v9
        with:
          script: |
            const token = await core.getIDToken('https://api.anthropic.com');
            core.setSecret(token);
            core.exportVariable('GHA_OIDC_TOKEN', token);

      - name: Exchange for Anthropic access token
        env:
          FEDERATION_RULE_ID: ${{ vars.ANTHROPIC_FEDERATION_RULE_ID }}
          ORGANIZATION_ID: ${{ vars.ANTHROPIC_ORGANIZATION_ID }}
          SERVICE_ACCOUNT_ID: ${{ vars.ANTHROPIC_SERVICE_ACCOUNT_ID }}
          WORKSPACE_ID: ${{ vars.ANTHROPIC_WORKSPACE_ID }}
        run: |
          PAYLOAD=$(jq -n \
            --arg grant_type "urn:ietf:params:oauth:grant-type:jwt-bearer" \
            --arg assertion "$GHA_OIDC_TOKEN" \
            --arg federation_rule_id "$FEDERATION_RULE_ID" \
            --arg organization_id "$ORGANIZATION_ID" \
            --arg service_account_id "$SERVICE_ACCOUNT_ID" \
            --arg workspace_id "$WORKSPACE_ID" \
            '$ARGS.named')
          ACCESS_TOKEN=$(curl -sS https://api.anthropic.com/v1/oauth/token \
            -H "content-type: application/json" \
            -d "$PAYLOAD" | jq -r .access_token)
          if [ -z "$ACCESS_TOKEN" ] || [ "$ACCESS_TOKEN" = "null" ]; then
            echo "::error::Anthropic WIF token exchange failed"
            exit 1
          fi
          echo "::add-mask::$ACCESS_TOKEN"
          echo "ANTHROPIC_ACCESS_TOKEN=$ACCESS_TOKEN" >> "$GITHUB_ENV"

      - name: Compute diff range
        id: range
        run: |
          if [ "${{ github.event_name }}" = "push" ]; then
            echo "range=${{ github.event.before }}..${{ github.sha }}" >> "$GITHUB_OUTPUT"
          else
            # sweep/dispatch: last 7 days
            since=$(git rev-list -1 --before='7 days ago' main || echo "")
            echo "range=${since:+$since..}${{ github.sha }}" >> "$GITHUB_OUTPUT"
          fi

      - name: Run docs agent
        uses: anthropics/claude-code-action@v1
        with:
          anthropic_api_key: ${{ env.ANTHROPIC_ACCESS_TOKEN }}
          prompt: |
            You are the PinballWizard docs updater (a CI automation agent — see ADR-0051).
            Analyze the merged change range ${{ steps.range.outputs.range }} (use `git diff` / `git log`).

            Question: does this change make any DESCRIPTIVE documentation untruthful?
            Descriptive docs are factual surfaces: README.md, CLAUDE.md, and docs/** EXCEPT
            docs/adr/** and docs/decision-log.md (those record human decisions — you must
            never author them).

            If NO doc is affected: do nothing, open no PR, exit.

            If docs ARE affected:
              1. Edit ONLY files within .github/docs-agent-allowlist.txt.
              2. Make the minimal truthful correction — do not rewrite, do not editorialize.
              3. If the change looks like it warranted a NEW ADR or decision-log entry,
                 DO NOT write one. Instead note it under a "## Human follow-ups" heading
                 in the PR body.
              4. Create a branch named docs-agent/${{ github.sha }}, commit as
                 Jim Keeley <94459922+jkeeley2073@users.noreply.github.com> (no Claude
                 attribution trailer), push, and open a PR with:
                   - label: claude-code
                   - a body documenting: the analyzed range, each doc touched and WHY,
                     and any Human follow-ups.
              5. Enable auto-merge: `gh pr merge --auto --squash`.
            Never touch code, workflows, ADRs, or the decision log. Keep the diff small.
          claude_args: '--allowedTools "Bash,Edit,Read,Grep,Glob"'

      - name: Report failure
        if: failure()
        uses: actions/github-script@v9
        with:
          script: |
            const title = 'Docs Agent run failed';
            const body = `The docs-agent workflow failed on ${context.sha} (${context.eventName}). Run: ${context.serverUrl}/${context.repo.owner}/${context.repo.repo}/actions/runs/${context.runId}`;
            const existing = await github.rest.issues.listForRepo({ owner: context.repo.owner, repo: context.repo.repo, state: 'open', labels: 'docs-agent-failure' });
            if (existing.data.length) {
              await github.rest.issues.createComment({ owner: context.repo.owner, repo: context.repo.repo, issue_number: existing.data[0].number, body });
            } else {
              await github.rest.issues.create({ owner: context.repo.owner, repo: context.repo.repo, title, body, labels: ['docs-agent-failure'] });
            }
```

- [ ] **Step 2: Lint the workflow YAML**

Run: `node -e "require('js-yaml')" 2>/dev/null && npx --yes js-yaml .github/workflows/docs-agent.yml >/dev/null && echo OK || echo "install js-yaml or validate via actionlint"`
Preferred: `actionlint .github/workflows/docs-agent.yml` if available.
Expected: no syntax errors.

- [ ] **Step 3: Confirm no self-retrigger path**

Verify by inspection: the agent's PR edits only allowlist paths (docs/README/CLAUDE/manifest); none match the push trigger's `src/**`/`infra/**`/`.github/workflows/**` — so a merged agent PR cannot retrigger the agent. Document this in the commit body.

- [ ] **Step 4: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  add .github/workflows/docs-agent.yml
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "ci(docs-agent) push+weekly docs updater via WIF; auto-merge behind guard"
```

---

### Task 4: Branch-protection wiring + verification + PR

**Files:** none (repo settings + PR)

- [ ] **Step 1: Verify conformance + links still green**

Run: `dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~Conformance" -v minimal && node tools/docs/check-links.mjs`
Expected: PASS + exit 0.

- [ ] **Step 2: `/local-review` + `/standards-audit`**

Treat 🔴 as blocking. Note `.github/**` changes are CODEOWNERS-gated — this PR needs human review regardless.

- [ ] **Step 3: Push + PR**

```bash
gh pr create --label claude-code --title "ci(docs-agent) push-triggered docs updater + guard + ADR-0051" \
  --body "Adds docs-agent.yml (WIF, per-merge + weekly), docs-agent-guard.yml (allowlist fence), and ADR-0051. Requires adding 'Docs Agent Guard' as a required status check in branch protection after merge (Step 4 in the plan). /local-review + /standards-audit: <outcome>."
```

- [ ] **Step 4: After merge — make the guard a required check**

In GitHub branch-protection settings for `main`, add `Enforce docs-agent allowlist` (the guard job name) to required status checks. Because the guard passes instantly on non-agent branches, this does not block normal PRs and needs no noop companion. Verify by opening any trivial non-agent PR and confirming the guard reports success immediately.

- [ ] **Step 5: Verify label + first live run**

Run: `gh pr view --json labels`; after this PR merges, trigger `workflow_dispatch` on Docs Agent once to confirm the WIF exchange + PR-open path works end-to-end, then watch for the resulting `docs-agent/*` PR and confirm the guard + auto-merge behave.

## Self-Review

- **Spec coverage:** ADR-0051 + echoes (T1) ✓; allowlist fence + matcher (T2) ✓; WIF workflow, per-merge + weekly + dispatch, agent contract, failure-issue, no-self-retrigger (T3) ✓; required-check wiring + live verification (T4) ✓.
- **Placeholders:** WIF steps are copied verbatim from the real `claude.yml`; matcher and guard carry full code; cron slot flagged as "confirm" (open question from the spec, not a placeholder in logic).
- **Type consistency:** allowlist file path (`.github/docs-agent-allowlist.txt`) and matcher script path (`.github/scripts/allowlist-match.mjs`) are identical across the guard workflow (T2) and its self-test; the agent prompt references the same allowlist file. ✓
- **Security note:** `claude_args` restricts tools; `contents: write` is required for branch push but the guard + CODEOWNERS on `.github/**` bound the blast radius. The agent cannot edit workflows (allowlist denies everything outside docs/README/CLAUDE/manifest).
