# Deploy Validation in the SDLC — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a broken container build impossible to merge, make a failed post-merge deploy loudly self-report, and make "post-merge deploy green" part of the definition of done.

**Architecture:** Three independent layers. (1) A `container-build` job in `ci.yml` builds all four images on every PR (no push, GHA-cached) so a Docker-context break fails the PR. (2) A `report` job in `deploy.yml` opens/updates a deduplicated `deploy-failure` GitHub issue on failure and closes it on the next green deploy. (3) Documentation changes (`PR-AUDIT.md`, PR template, `pinball-workflows.md`) make deploy verification a required step of "done."

**Tech Stack:** GitHub Actions (YAML), `docker/build-push-action@v7`, `gh` CLI (matches the repo's `canary.yml` issue-automation idiom), Markdown docs.

## Global Constraints

- **Personal-identity commits**, `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no AI attribution trailer** (repo INVARIANT).
- **Mirror existing repo idioms** verbatim where they exist: the deploy build step (`deploy.yml` lines 88–154) for the image matrix + cache scopes; `canary.yml` lines 106–119 for the issue dedup/create pattern (`gh issue list --search … --json number --jq '.[0].number // empty'` → comment-else-create).
- **`type=gha` cache, `scope=${{ matrix.image }}`** — identical scope to `deploy.yml` so PR builds and deploy builds share the layer cache.
- **No new secrets.** The issue job uses the built-in `GH_TOKEN: ${{ github.token }}` with `permissions: issues: write` (exactly as `canary.yml`).
- **YAML must parse** and, where possible, pass `actionlint`. Workflow logic that can't be unit-tested is validated by a real run with a stated expected outcome — a gate that has never failed on the bug it targets is unproven (spec § Validation).
- Working directory: the worktree `c:\earlybird\PinballWizard\.worktrees\sdlc-deploy-validation` on branch `feat/sdlc-deploy-validation`. Paths below are repo-relative.
- **Do not use `target:` in the PR build** — not every Dockerfile names its first stage `build`; a full `push: false` build validates the whole context+publish path without assuming a stage name.

---

## File Structure

**Modify:**
- `.github/workflows/ci.yml` — add the `container-build` PR-gate job (Task 1).
- `.github/workflows/deploy.yml` — add the `report` deploy-health job (Task 2).
- `.claude/PR-AUDIT.md` — add "Step 3 — post-merge deploy verification" (Task 3).
- `.github/PULL_REQUEST_TEMPLATE.md` — add the deploy-green checklist item (Task 3).
- `.claude/rules/pinball-workflows.md` — reference Step 3 in the quick-reference table (Task 3).

**One-time operator setup (documented, executed in Task 2):**
- Create the `deploy-failure` GitHub label.
- Add `container-build` (all four matrix legs) to `main` branch-protection required checks.

---

### Task 1: PR-time container-build gate (`ci.yml`)

**Files:**
- Modify: `.github/workflows/ci.yml` (add one job under `jobs:`)

**Interfaces:**
- Produces: a `container-build` job with matrix legs `pinwiz-web`, `pinwiz-api`, `pinwiz-rag-indexer`, `pinwiz-cli` — the same four images `deploy.yml` builds. Each leg's status check is named `Build <image> image`.

- [ ] **Step 1: Confirm each Dockerfile path exists (no guessing the matrix)**

Run:
```bash
for f in src/PinballWizard.Web/Dockerfile src/PinballWizard.Api/Dockerfile \
         src/PinballWizard.RagIngestionWorker/Dockerfile src/PinballWizard.Cli/Dockerfile; do
  test -f "$f" && echo "OK  $f" || echo "MISSING $f"
done
```
Expected: four `OK` lines. (These are the exact paths from `deploy.yml` lines 93/96/105/116.)

- [ ] **Step 2: Add the `container-build` job to `ci.yml`**

Append this job at the end of the `jobs:` block in `.github/workflows/ci.yml` (sibling to `build-and-test` and `ui-tests`; match the file's 2-space indentation):

```yaml
  container-build:
    # Builds every container image on the PR (no push) so a Docker-context /
    # .dockerignore / Dockerfile / embedded-resource break fails HERE instead of
    # silently on the post-merge Deploy. CI's dotnet build sees the whole working
    # tree; only the Docker build filters context via .dockerignore — this job is
    # what closes that divergence (the 2026-07-06 web-deploy break, #689). Shares
    # the type=gha cache scope with deploy.yml so incremental builds are fast.
    name: Build ${{ matrix.image }} image
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        include:
          - image: pinwiz-web
            dockerfile: src/PinballWizard.Web/Dockerfile
          - image: pinwiz-api
            dockerfile: src/PinballWizard.Api/Dockerfile
          - image: pinwiz-rag-indexer
            dockerfile: src/PinballWizard.RagIngestionWorker/Dockerfile
          - image: pinwiz-cli
            dockerfile: src/PinballWizard.Cli/Dockerfile
    steps:
      - name: Checkout
        uses: actions/checkout@v6

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v4

      - name: Build ${{ matrix.image }} image (no push)
        uses: docker/build-push-action@v7
        with:
          context: .
          file: ${{ matrix.dockerfile }}
          push: false
          load: false
          cache-from: type=gha,scope=${{ matrix.image }}
          cache-to: type=gha,mode=max,scope=${{ matrix.image }}
```

- [ ] **Step 3: Validate the YAML parses**

Run:
```bash
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ci.yml: valid YAML')"
```
Expected: `ci.yml: valid YAML`. If `actionlint` is installed, also run `actionlint .github/workflows/ci.yml` and expect no errors.

- [ ] **Step 4: Prove the gate GOES GREEN on the fixed tree (locally)**

Run (from the worktree root — this is the same build the CI job runs, minus the cache):
```bash
docker buildx build --file src/PinballWizard.Web/Dockerfile --load=false . >/tmp/cb-web.log 2>&1 && echo "web: BUILD OK" || (echo "web: BUILD FAILED"; tail -5 /tmp/cb-web.log)
```
Expected: `web: BUILD OK` (origin/main already contains the #720 Dockerfile fix).

- [ ] **Step 5: Prove the gate WOULD HAVE CAUGHT the incident (red on the pre-fix state)**

The gate is unproven unless it fails on the bug it targets. Temporarily reproduce the pre-#720 `.dockerignore` (remove the `docs/*` negations) in a scratch copy and confirm the web build fails with CS1566:
```bash
cp .dockerignore /tmp/dockerignore.bak
# strip the three re-include negations to simulate the pre-fix state
grep -v '^!docs/' .dockerignore > /tmp/di.tmp && mv /tmp/di.tmp .dockerignore
docker buildx build --file src/PinballWizard.Web/Dockerfile . 2>&1 | grep -c CS1566 | xargs -I{} echo "CS1566 occurrences (expect >=1): {}"
cp /tmp/dockerignore.bak .dockerignore   # RESTORE — do not commit the stripped version
git diff --quiet .dockerignore && echo ".dockerignore restored OK" || (echo "RESTORE FAILED"; git checkout .dockerignore)
```
Expected: `CS1566 occurrences (expect >=1): 1` (or more), then `.dockerignore restored OK`. This demonstrates the job would have turned the #689 PR red.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci(deploy) build all container images on PRs to catch Docker-context breaks

CI's dotnet build sees the full tree; only the Docker build filters context via
.dockerignore, so a context break (like #689's embedded docs) passed CI and
failed only on the post-merge Deploy. This job builds all four images (no push,
gha-cached) on every PR. Verified it fails with CS1566 against the pre-fix
.dockerignore and passes on the fixed tree."
```

---

### Task 2: `deploy-failure` auto-issue on the post-merge Deploy (`deploy.yml`)

**Files:**
- Modify: `.github/workflows/deploy.yml` (add one job under `jobs:`)

**Interfaces:**
- Consumes: the existing `build-deploy`, `smoke`, and `e2e` job results.
- Produces: a `report` job that opens/updates/closes a `deploy-failure`-labeled issue.

- [ ] **Step 1: One-time — create the `deploy-failure` label (operator action)**

`gh issue create --label` fails if the label doesn't exist. Create it once:
```bash
gh label create deploy-failure --color B60205 \
  --description "Post-merge deploy/smoke/e2e failing on main" 2>&1 || echo "(label may already exist — OK)"
gh label list --search deploy-failure
```
Expected: the `deploy-failure` label is listed.

- [ ] **Step 2: Add the `report` job to `deploy.yml`**

Append at the end of the `jobs:` block in `.github/workflows/deploy.yml` (sibling to `build-deploy`, `smoke`, `e2e`). This mirrors `canary.yml`'s dedup idiom (search open issues → comment-else-create) and adds auto-close on success:

```yaml
  report:
    # Deploy-health reporter. Any failed leg (build/deploy, smoke, e2e) opens or
    # updates ONE deduplicated deploy-failure issue so a broken deploy is visible
    # without watching the Actions tab — honest failure surfacing per invariant
    # #17. A fully-green deploy closes any open issue, so the issue's open/closed
    # state tracks live deploy health. Mirrors canary.yml's alarm pattern.
    name: Report deploy health
    runs-on: ubuntu-latest
    needs: [build-deploy, smoke, e2e]
    if: ${{ always() }}
    permissions:
      contents: read
      issues: write
    env:
      GH_TOKEN: ${{ github.token }}
    steps:
      - name: Open or update the deploy-failure issue
        if: ${{ contains(needs.*.result, 'failure') }}
        run: |
          title="🚨 Deploy failing on main"
          run_url="${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}"
          body=$(printf 'Deploy failed on `%s`.\n\nRun: %s\nTime: %s\n\nTriage: open the run, find the failed job (build-deploy / smoke / e2e), and fix-forward or revert. This issue auto-closes when a deploy goes green.' \
            "${{ github.sha }}" "$run_url" "$(date -u +%Y-%m-%dT%H:%M:%SZ)")
          existing=$(gh issue list --state open --label deploy-failure --json number --jq '.[0].number // empty')
          if [ -n "$existing" ]; then
            gh issue comment "$existing" --body "Still failing on \`${{ github.sha }}\`. $run_url"
          else
            gh issue create --title "$title" --label deploy-failure --body "$body"
          fi

      - name: Close the deploy-failure issue on a green deploy
        if: ${{ !contains(needs.*.result, 'failure') && !contains(needs.*.result, 'cancelled') }}
        run: |
          for n in $(gh issue list --state open --label deploy-failure --json number --jq '.[].number'); do
            gh issue comment "$n" --body "✅ Resolved by \`${{ github.sha }}\` — deploy green. Closing."
            gh issue close "$n"
          done
```

- [ ] **Step 3: Validate the YAML parses**

Run:
```bash
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/deploy.yml')); print('deploy.yml: valid YAML')"
```
Expected: `deploy.yml: valid YAML`. If `actionlint` is present, run it too and expect no errors.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/deploy.yml
git commit -m "ci(deploy) open a deduplicated deploy-failure issue on a failed deploy

A failed post-merge deploy was silent — the 2026-07-06 web break sat through 9
merges until the operator noticed the live site was stale. This report job opens
(or comments on) one deploy-failure issue when any leg fails, and closes it on
the next green deploy. Mirrors canary.yml's alarm dedup pattern; built-in
GITHUB_TOKEN, no new secret."
```

- [ ] **Step 5: Runtime validation (after this PR merges — recorded, executed at merge time)**

Workflow behavior can't be unit-tested; validate on a real run. After merge, the first Deploy exercises the success path (issue closes / none opens). To validate the failure path without breaking a real deploy, run a `workflow_dispatch` on a scratch branch whose Dockerfile is intentionally broken, and assert: (a) exactly one `deploy-failure` issue opens; (b) a second failing run **comments** rather than opening a duplicate; (c) a subsequent green run **closes** it. Record the outcome in the PR thread. (This step is a documented validation, not a code change — do not block the doc tasks on it.)

---

### Task 3: Make "deploy green" part of the definition of done (docs)

**Files:**
- Modify: `.claude/PR-AUDIT.md` (add Step 3)
- Modify: `.github/PULL_REQUEST_TEMPLATE.md` (add checklist item)
- Modify: `.claude/rules/pinball-workflows.md` (reference Step 3 in the table)

**Interfaces:** none (documentation).

- [ ] **Step 1: Add "Step 3 — post-merge deploy verification" to `.claude/PR-AUDIT.md`**

After the existing "Step 2 — Post-push code-scanning triage" section (which ends before "## Recording the outcome"), insert:

```markdown
## Step 3 — Post-merge deploy verification (BLOCKING — "done" is not "merged")

Merging is not shipping. The post-merge `Deploy` workflow (build all four images
→ push to ACR → ACA revision swap → smoke `/alive` → E2E canary) is what puts the
change on the live site, and it can fail after a green PR (Docker context, RBAC,
env, revision rollover). **Work is not done until that deploy is green.**

After a merge to `main`:

```bash
# Find and watch the Deploy run for the merge commit.
DEPLOY_ID=$(gh run list --workflow=deploy.yml --branch main --limit 1 --json databaseId --jq '.[0].databaseId')
gh run watch "$DEPLOY_ID" --exit-status
```

- **Green** → done. Report the live change.
- **Failed** → triage immediately (root-cause + fix-forward, or revert). The
  `report` job also opens a `deploy-failure` issue automatically; annotate/close it
  as you resolve. Do NOT declare the work done.

At session start (or when picking up work), check for open deploy-failure issues
first — a red deploy blocks everyone's changes from going live:

```bash
gh issue list --state open --label deploy-failure
```
```

- [ ] **Step 2: Add the checklist item to `.github/PULL_REQUEST_TEMPLATE.md`**

In the `## Checklist` section, after the `- [ ] CI is green …` line, add:

```markdown
- [ ] Post-merge `Deploy` green (build → smoke `/alive` → E2E canary) — "done" is not "merged" (see `.claude/PR-AUDIT.md` Step 3)
```

- [ ] **Step 3: Reference Step 3 in `.claude/rules/pinball-workflows.md`**

In the "## 5. Quick reference" table, after the `PR checks / bot review comments appear` row, add:

```markdown
| After merge to `main` | Watch the `Deploy` run to green (PR-AUDIT Step 3); "done" ≠ "merged". Triage any `deploy-failure` issue. |
```

Also, in the "## 4a." heading region, append one line after the code-scanning subsection:

```markdown
## 4b. After merge — deploy verification (BLOCKING)

Merging ships nothing until the post-merge `Deploy` is green. Watch it to
completion and treat a failure like a code-scanning finding — fix-forward or
revert before calling the work done. Full mechanism: [`.claude/PR-AUDIT.md`](../PR-AUDIT.md) Step 3.
```

- [ ] **Step 4: Verify the doc links resolve**

Run:
```bash
grep -q "Step 3 — Post-merge deploy verification" .claude/PR-AUDIT.md && echo "PR-AUDIT Step 3: present"
grep -q "Post-merge .Deploy. green" .github/PULL_REQUEST_TEMPLATE.md && echo "PR template item: present"
grep -q "4b. After merge — deploy verification" .claude/rules/pinball-workflows.md && echo "pinball-workflows 4b: present"
```
Expected: all three "present" lines.

- [ ] **Step 5: Commit**

```bash
git add .claude/PR-AUDIT.md .github/PULL_REQUEST_TEMPLATE.md .claude/rules/pinball-workflows.md
git commit -m "docs(sdlc) make post-merge deploy verification part of done

PR-AUDIT gains Step 3 (watch the Deploy to green after merge; check for
deploy-failure issues at session start); the PR template and pinball-workflows
reference it. Encodes 'done means deployed', not 'done means merged'."
```

---

## Post-implementation

- [ ] **Branch protection (operator action, documented):** add the four `Build <image> image` checks from Task 1 to `main`'s required status checks (Settings → Branches → main → Require status checks). Until then the job runs and is visible but advisory. Note this in the PR description.
- [ ] **Full CI-equivalent suite** is unaffected (no C# changed), but run `python -c "import yaml; [yaml.safe_load(open(f)) for f in ['.github/workflows/ci.yml','.github/workflows/deploy.yml']]"` once more before push to confirm both workflows parse.
- [ ] The PR itself will exercise Task 1's new `container-build` job — confirm all four legs go green on the PR before merge (the gate validating itself).
