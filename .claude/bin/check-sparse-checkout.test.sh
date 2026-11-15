#!/usr/bin/env bash
# Controls for check-sparse-checkout.sh (#918).
#
# Usage:  bash .claude/bin/check-sparse-checkout.test.sh [path-to-script]
set -uo pipefail

SCRIPT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/check-sparse-checkout.sh}"
[ -f "$SCRIPT" ] || { echo "no such script: $SCRIPT"; exit 2; }

ROOT=$(mktemp -d 2>/dev/null) || { echo "mktemp failed"; exit 2; }
trap 'rm -rf "$ROOT"' EXIT

git init -q -b trunk "$ROOT/work"
cd "$ROOT/work" || exit 2
git config user.email test@example.invalid
git config user.name "Control Harness"
git config commit.gpgsign false

harness_top=$(git rev-parse --show-toplevel 2>/dev/null) || {
    echo "ABORT: not inside a git repo"; exit 2
}
[ -n "$harness_top" ] && [ "$harness_top" -ef "$ROOT/work" ] || {
    echo "ABORT: refusing to run outside the scratch repo (cwd resolves to: ${harness_top:-<none>})"
    exit 2
}

echo 'seed' > README.md
mkdir -p infra/modules
echo 'bicep' > infra/modules/shared.bicep
git add -A
git commit -qm 'seed'

pass=0; fail=0

HOOK_OUT=""
run_hook() {
    local name="$1" rc
    HOOK_OUT=$("$BASH" "$SCRIPT" 2>"$ROOT/stderr.txt"); rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "  FAIL  $name (advisory contract: exited $rc, must be 0)"; fail=$((fail+1)); return 1
    fi
    if [ -s "$ROOT/stderr.txt" ]; then
        echo "  FAIL  $name (advisory contract: wrote to stderr)"
        echo "        stderr: $(head -c 200 "$ROOT/stderr.txt")"; fail=$((fail+1)); return 1
    fi
    return 0
}

# 1. Clean repo: silent
if run_hook "1. clean repo silent"; then
    if [ -z "$HOOK_OUT" ]; then
        echo "  PASS  1. clean repo is silent"; pass=$((pass+1))
    else
        echo "  FAIL  1. clean repo should be silent; got: $HOOK_OUT"; fail=$((fail+1))
    fi
fi

# 2. Enable foreign sparse-checkout cone from #918; hook must disable + speak
mkdir -p .git/info
cat > .git/info/sparse-checkout <<'EOF'
/*
!/*/
/plugins/
!/plugins/*/
/plugins/redis-development/
EOF
git config core.sparseCheckout true

if run_hook "2. foreign cone detect+disable"; then
    ok=1
    printf '%s' "$HOOK_OUT" | grep -q 'DETECTED core.sparseCheckout=true' || ok=0
    printf '%s' "$HOOK_OUT" | grep -q '/plugins/redis-development/' || ok=0
    sparse_after=$(git config --bool --get core.sparseCheckout 2>/dev/null || echo false)
    [ "$sparse_after" = "true" ] && ok=0
    # Staging must work after disable — the original silent-failure shape.
    echo 'changed' >> infra/modules/shared.bicep
    if ! git add infra/modules/shared.bicep 2>"$ROOT/add.err"; then
        ok=0
    fi
    if [ -s "$ROOT/add.err" ] && grep -qi 'sparse-checkout' "$ROOT/add.err"; then
        ok=0
    fi
    if git diff --cached --name-only | grep -qx 'infra/modules/shared.bicep'; then
        :
    else
        ok=0
    fi
    if [ "$ok" -eq 1 ]; then
        echo "  PASS  2. foreign cone detected, disabled, git add works"; pass=$((pass+1))
    else
        echo "  FAIL  2. foreign cone handling"
        echo "        out: $HOOK_OUT"
        echo "        sparse_after=$sparse_after"
        echo "        add.err: $(cat "$ROOT/add.err" 2>/dev/null)"
        echo "        staged: $(git diff --cached --name-only)"
        fail=$((fail+1))
    fi
fi

# 3. After disable, second run is silent again
git checkout -- infra/modules/shared.bicep >/dev/null 2>&1 || true
git reset -q HEAD
if run_hook "3. second run silent"; then
    if [ -z "$HOOK_OUT" ]; then
        echo "  PASS  3. second run is silent after disable"; pass=$((pass+1))
    else
        echo "  FAIL  3. expected silence after disable; got: $HOOK_OUT"; fail=$((fail+1))
    fi
fi

# 4. Re-init sparse (must use `git sparse-checkout init` — after disable,
#    config.worktree keeps core.sparseCheckout=false and overrides a bare
#    `git config core.sparseCheckout true` in .git/config).
git sparse-checkout init --cone >/dev/null 2>&1
printf '/*\n' > "$(git rev-parse --git-path info/sparse-checkout)"
if run_hook "4. generic sparse on"; then
    ok=1
    printf '%s' "$HOOK_OUT" | grep -q 'DETECTED core.sparseCheckout=true' || ok=0
    sparse_after=$(git config --bool --get core.sparseCheckout 2>/dev/null || echo false)
    [ "$sparse_after" = "true" ] && ok=0
    if [ "$ok" -eq 1 ]; then
        echo "  PASS  4. generic sparseCheckout=true is disabled"; pass=$((pass+1))
    else
        echo "  FAIL  4. generic sparse handling"
        echo "        out: $HOOK_OUT"
        echo "        sparse_after=$sparse_after"
        fail=$((fail+1))
    fi
fi

echo
echo "check-sparse-checkout controls: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
