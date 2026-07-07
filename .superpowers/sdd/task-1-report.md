# Task 1 Report: PR-Time Container-Build Gate

**Status:** DONE_WITH_CONCERNS
**Commit:** `2d52bca` — ci(deploy) build all container images on PRs to catch Docker-context breaks

---

## Job Added

Added `container-build` job to `.github/workflows/ci.yml` as a sibling to `build-and-test` and `ui-tests`. The job:
- Runs a 4-leg matrix: `pinwiz-web`, `pinwiz-api`, `pinwiz-rag-indexer`, `pinwiz-cli`
- Each leg named `Build <image> image` (per-matrix status check naming)
- `push: false`, `load: false`, `fail-fast: false`
- Shares `type=gha,scope=${{ matrix.image }}` cache with `deploy.yml` for fast incremental builds
- No `target:` — full image build to validate the complete context/publish path

## Step 3: YAML Parse Result

```
ci.yml: valid YAML
```

`python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ci.yml: valid YAML')"` passed cleanly.

---

## Step 4: Green-Build Proof

**Result: web: BUILD FAILED**

**Expected by brief: web: BUILD OK** — this expectation could not be met.

**Root cause confirmed:** The current `.dockerignore` excludes `docs/` (line 36), and `*.md` (line 38). The web project's `.csproj` embeds files from `../../docs/` as `EmbeddedResource` items (`engineering-manifest.json`, `vision.md`, `ai-development-model.md`, `learning-from-failure.md`, `architecture-v2.md`, `glossary.md`, `docs/adr/*.md`). The Dockerfile does `COPY src/ src/` but has no `COPY docs/ docs/` step, so `/src/docs/` is absent inside the container and the publish step fails:

```
CSC : error CS1566: Error reading resource 'PinballWizard.Web.docs.engineering-manifest.json'
       -- 'Could not find a part of the path '/src/docs/engineering-manifest.json'.'
```

**The brief states "origin/main already contains the #720 Dockerfile fix."** Investigation confirms PR #720 has NOT been merged to origin/main as of this task's execution. The current head of `origin/main` is `0a32287` (fix(rag) #716), and neither the `.dockerignore` negations nor a `COPY docs/ docs/` Dockerfile step are present. This branch (`feat/sdlc-deploy-validation`) was created from that same commit.

**Interpretation:** The current tree IS the pre-fix state. The gate correctly catches the bug even on Step 4.

---

## Step 5: Red-on-Pre-Fix Proof

**CS1566 occurrences (expect >=1): 2**

Brief command run verbatim:
```bash
cp .dockerignore /tmp/dockerignore.bak
grep -v '^!docs/' .dockerignore > /tmp/di.tmp && mv /tmp/di.tmp .dockerignore
docker buildx build --file src/PinballWizard.Web/Dockerfile . 2>&1 | grep -c CS1566 | xargs -I{} echo "CS1566 occurrences (expect >=1): {}"
cp /tmp/dockerignore.bak .dockerignore
git diff --quiet .dockerignore && echo ".dockerignore restored OK" || (echo "RESTORE FAILED"; git checkout .dockerignore)
```

**Output:** `CS1566 occurrences (expect >=1): 2`
**Restore:** `.dockerignore restored OK`

**Important nuance:** Because the current `.dockerignore` has NO `!docs/` negation lines (it's already the pre-fix state), `grep -v '^!docs/'` produces an identical file — `git diff --quiet .dockerignore` shows zero changes. The "stripped" version and the current version are the same. The CS1566 count of 2 is therefore the same error that would occur in both Step 4 and Step 5, confirming the gate catches the incident in the current tree.

---

## Files Changed

- `.github/workflows/ci.yml` — 38 insertions, 0 deletions

---

## Self-Review

- Job YAML copied verbatim from brief (no invented values)
- All four Dockerfile paths confirmed to exist before adding to matrix (Step 1 passed: 4 OK)
- `fail-fast: false` ensures all four matrix legs run independently — a single image failure doesn't mask other images' failures
- Cache scope matches `deploy.yml` pattern exactly (`type=gha,scope=${{ matrix.image }}`)
- No `target:` used — whole Dockerfile executes, catching embedded-resource issues at publish stage
- Identity confirmed: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`
- No `Co-Authored-By` trailer (correct for this repo per pinball-workflows.md)
- `.dockerignore` confirmed clean after Step 5 run

---

## Concerns

### CONCERN 1 (Blocking for Step 4): PR #720 not merged — web image build is currently broken

The brief expected `web: BUILD OK` for Step 4, but the gate fires on the current tree. The fix requires two changes not yet in `origin/main`:
1. **`.dockerignore`**: add `!docs/` negations to re-include the specific files the web csproj embeds (or remove `docs/` exclusion entirely and add back only what's truly unnecessary)
2. **`src/PinballWizard.Web/Dockerfile`**: add `COPY docs/ docs/` before the `RUN dotnet publish` step so the embedded resource files exist at `/src/docs/` inside the container

Until PR #720 lands, the `container-build` CI job will fail on every PR targeting the web image. This is the correct behavior (the gate is working), but it blocks merging legitimate PRs until the underlying Docker context is fixed.

### CONCERN 2: Step 5 "stripped .dockerignore" identical to current

The pre-fix simulation couldn't differentiate "fixed" vs "broken" state because the fix (adding `!docs/` negations) was never applied. The demonstration shows the gate catches CS1566 in the current tree; a proper two-state proof requires PR #720 to be applied first.

### CONCERN 3 (Minor): `*.md` exclusion in .dockerignore also contributes

Line 38 (`*.md`) excludes all markdown files from the Docker context, including `docs/*.md` and `docs/adr/*.md`. The `docs/` exclusion on line 36 already covers all of docs/, so line 38 is redundant for that path — but it also excludes any root-level `.md` files the Dockerfile might need. Not a new issue; just noting it for PR #720's author.

---

## Final-review fix

**Files changed:**

- `.github/workflows/container-build.yml` — created (new dedicated workflow, 52 lines)
- `.github/workflows/ci.yml` — `container-build` job removed (38 lines deleted)
- `docs/superpowers/specs/2026-07-07-deploy-validation-in-sdlc-design.md` — Layer 1 section updated: removed `--target build`, added parenthetical (full build, no stage target), updated prose to say gate lives in dedicated `container-build.yml` (not `ci.yml`) so it bypasses `ci.yml`'s docs paths-ignore
- `.claude/PR-AUDIT.md` — Step 3 gains 2-sentence branch-protection handoff note (operator must add the four `Build <image> image` checks as required status checks)
- `.superpowers/sdd/task-1-report.md` — this section appended

**Validation results:**

- YAML parse: `both parse` (python yaml.safe_load on both workflow files)
- `grep -c 'container-build' .github/workflows/ci.yml`: `0`
- CS1566 count from `docker buildx build --file src/PinballWizard.Web/Dockerfile .`: `0` (the #720 fix is on this branch — web image builds clean)

**Concerns:** None. All three validation checks passed. The gate is now in its own workflow, immune to ci.yml's `docs/**` paths-ignore.
