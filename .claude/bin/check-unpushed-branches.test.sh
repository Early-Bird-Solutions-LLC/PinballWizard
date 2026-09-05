#!/usr/bin/env bash
# Controls for check-unpushed-branches.sh.
#
# Usage:  bash .claude/bin/check-unpushed-branches.test.sh [path-to-script]
#
# Builds a throwaway repo with a real remote under $TMPDIR and asserts observable
# behaviour. Takes the script path as an argument specifically so it can be run
# against an OLDER revision — `git show <sha>:.claude/bin/check-unpushed-branches.sh`
# — to confirm a control genuinely fails on the code it was written for. A control
# never seen to fail proves nothing.
#
# Control 5 exists because the first version of this hook shipped a false negative:
# stage 1 dropped any branch with a same-named remote ref, so a branch AHEAD of its
# tracking branch — the most common way commits end up local-only — was never
# reported. Controls 1-4 all passed while that bug was present. Hence 5 and 6.
#
# The default branch here is 'trunk', not 'main': the repo's branch-protection hook
# refuses commits on a branch named main, and it is right to, even in a scratch repo.
set -uo pipefail

SCRIPT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/check-unpushed-branches.sh}"
[ -f "$SCRIPT" ] || { echo "no such script: $SCRIPT"; exit 2; }

ROOT=$(mktemp -d 2>/dev/null) || { echo "mktemp failed"; exit 2; }
trap 'rm -rf "$ROOT"' EXIT

git init --bare -q "$ROOT/remote"
git init -q -b trunk "$ROOT/work"
cd "$ROOT/work" || exit 2
git config user.email test@example.invalid
git config user.name "Control Harness"
git config commit.gpgsign false

# Refuse to touch anything that is not the scratch repo. The real repo was damaged
# once by a command that assumed its working directory; nothing here assumes it.
#
# THE REAL GUARD IS `cd "$ROOT/work" || exit 2` ABOVE. What follows is a backstop,
# and an earlier version of it was both unsound and destructive: it wrote a marker
# into cwd and THEN checked that the marker existed, so from any repo root it
# created the very file that made itself pass — and the next line clobbered that
# repo's .gitignore. A review reproduced it destroying a real one.
#
# So: verify FIRST, write nothing until verified. Identity is compared with
# `test -ef` (same inode) rather than string paths, because under Git Bash
# `mktemp -d` yields `/tmp/...` while `git rev-parse --show-toplevel` yields
# `C:/Users/...`; those can never compare equal, and a guard that always aborts is
# just an outage.
harness_top=$(git rev-parse --show-toplevel 2>/dev/null) || {
    echo "ABORT: not inside a git repo"; exit 2
}
[ -n "$harness_top" ] && [ "$harness_top" -ef "$ROOT/work" ] || {
    echo "ABORT: refusing to run outside the scratch repo (cwd resolves to: ${harness_top:-<none>})"
    exit 2
}
printf '.scratch-harness-marker
' >> .gitignore

pass=0; fail=0

# Every invocation goes through here, so the advisory contract (exit 0, silent
# stderr) is asserted on ALL controls rather than only the ones using check().
# Mutation testing found controls 6, 7b, 8 and 9 surviving an `exit 1` mutant
# because they called "$SCRIPT" directly and looked at stdout alone.
HOOK_OUT=""
run_hook() { # run_hook <control-name> [cwd]
    local name="$1" dir="${2:-$PWD}" rc
    HOOK_OUT=$(cd "$dir" && "$BASH" "$SCRIPT" 2>"$ROOT/stderr.txt"); rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "  FAIL  $name (advisory contract: exited $rc, must be 0)"; fail=$((fail+1)); return 1
    fi
    if [ -s "$ROOT/stderr.txt" ]; then
        echo "  FAIL  $name (advisory contract: wrote to stderr)"
        echo "        stderr: $(head -c 200 "$ROOT/stderr.txt")"; fail=$((fail+1)); return 1
    fi
    return 0
}

check() { # check <name> <expected: quiet|flags> [branch-that-must-appear]
    local name="$1" expect="$2" needle="${3:-}" out rc
    # stderr and exit status are captured, not discarded. "Always exits 0, never
    # blocks" is the hook's central contract, and without this a script that died on
    # startup would PASS every "quiet" control - silence would mean broken, not clean.
    out=$("$BASH" "$SCRIPT" 2>"$ROOT/stderr.txt"); rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "  FAIL  $name (advisory contract: exited $rc, must be 0)"; fail=$((fail+1)); return
    fi
    if [ -s "$ROOT/stderr.txt" ]; then
        echo "  FAIL  $name (advisory contract: wrote to stderr)"
        echo "        stderr: $(head -c 200 "$ROOT/stderr.txt")"; fail=$((fail+1)); return
    fi
    if [ "$expect" = quiet ] && [ -z "$out" ]; then
        echo "  PASS  $name"; pass=$((pass+1)); return
    fi
    if [ "$expect" = flags ] && [ -z "$needle" ]; then
        echo "  FAIL  $name (harness misuse: 'flags' requires a needle, else it passes on any output)"
        fail=$((fail+1)); return
    fi
    if [ "$expect" = flags ] && [ -n "$out" ] && case "$out" in *"$needle"*) true ;; *) false ;; esac; then
        echo "  PASS  $name"; pass=$((pass+1)); return
    fi
    echo "  FAIL  $name"
    echo "        expected: $expect ${needle:+containing '$needle'}"
    echo "        got: [${out:-<empty>}]"
    fail=$((fail+1))
}

echo one > a.txt; git add .; git commit -qm one
git remote add origin "$ROOT/remote"
git push -q origin trunk:main 2>/dev/null; git fetch -q origin 2>/dev/null

echo "Controls for: $SCRIPT"
check "1. all work reachable from a remote -> silent" quiet

git checkout -qb feature-a; echo two > b.txt; git add .; git commit -qm two; git checkout -q trunk
check "2. branch with no remote counterpart -> flagged" flags "feature-a"
check "2b. and labelled as having no worktree" flags "no worktree found"

git push -q origin feature-a 2>/dev/null; git fetch -q origin 2>/dev/null
check "3. same branch once pushed -> silent" quiet

# The commit here is what gives this control teeth. Without it odd-name points at a
# commit that is already on origin/main, so stage 2 excludes it whether or not the
# differently-named push ever happened - the control passed with the push line
# deleted, i.e. it asserted nothing beyond what control 1 already covers.
git checkout -qb odd-name 2>/dev/null
echo renamed > r.txt; git add .; git commit -qm renamed
git push -q origin odd-name:a-different-remote-name || { echo "  SETUP FAIL: rename-push failed"; fail=$((fail+1)); }
git fetch -q origin 2>/dev/null
git checkout -q trunk
check "4. reachable ONLY via a differently-named remote ref -> not flagged" quiet

# REGRESSION CONTROL. feature-a is pushed and at parity; add a local commit on top.
# Its name still matches origin/feature-a, so a name-based stage 1 drops it and the
# hook reports clean over genuinely local-only work.
# THREE commits, not one. With a fixture of one, an implementation that hardcodes
# "1" — or one that counts something else entirely and happens to get 1 — passes
# control 6 unchanged. Mutation testing confirmed exactly that.
git checkout -q feature-a
echo three > c.txt; git add .; git commit -qm three
echo four > c2.txt; git add .; git commit -qm four
echo five > c3.txt; git add .; git commit -qm five
git checkout -q trunk
check "5. branch AHEAD of its tracking branch -> flagged" flags "feature-a"

# The count must be the unpushed commits, not everything off the default branch,
# and must track the real number rather than a constant.
run_hook "6. reports the unpushed count" || true
out="$HOOK_OUT"
if printf '%s' "$out" | grep -q "feature-a  (3 unpushed commit"; then
    echo "  PASS  6. reports the unpushed count (3), not commits-off-default"; pass=$((pass+1))
else
    echo "  FAIL  6. reports the unpushed count (3), not commits-off-default"
    echo "        got: [$(printf '%s' "$out" | grep feature-a)]"; fail=$((fail+1))
fi

# Return to a clean baseline: control 5 deliberately left feature-a ahead.
git push -q origin feature-a 2>/dev/null; git fetch -q origin 2>/dev/null

# One branch name being a PREFIX of another. Matching is done against a
# newline-delimited "<sha> <name>" haystack, so a substring match would let
# `feat-prefix` satisfy the lookup for `feat-prefix-longer` (or vice versa) and
# silently clear an unpushed branch. The delimiters are what prevent it; this pins
# that they do.
#
# CORRECTION, from mutation testing: the delimiters this control was written to
# protect are NOT exercised at stage 1. That haystack is keyed "<sha> <name>", so a
# substring false-match would need a remote ref at the SAME sha whose name merely
# extends the local one — in which case the tip genuinely is pushed and dropping it
# is right. The place prefix collision actually bites is the WORKTREE lookup, which
# matches on name alone; control 10 covers that. This control is kept as a
# regression guard on stage-1 shape, not as the delimiter proof it claimed to be.
#
# NOTE: a pre-push review asked for glob metacharacters (`*`, `?`, `[`) in branch
# names here instead, arguing they would be treated as wildcards inside the `case`
# patterns. That case is unreachable twice over: bash matches any QUOTED portion of
# a case pattern literally, and `git check-ref-format` REJECTS all three characters
# in a ref name, so such a branch cannot exist. Prefix collision is the nearest
# failure that git can actually produce, so it is what gets tested.
git checkout -qb feat-prefix; echo four > d.txt; git add .; git commit -qm four
git push -q origin feat-prefix 2>/dev/null; git fetch -q origin 2>/dev/null
git checkout -qb feat-prefix-longer; echo five > e.txt; git add .; git commit -qm five
git checkout -q trunk
check "7. prefix-colliding branch names -> the unpushed one is flagged" flags "feat-prefix-longer"

run_hook "7b. pushed prefix-sibling" || true
out="$HOOK_OUT"
if printf '%s' "$out" | grep -qE "^ +feat-prefix  \("; then
    echo "  FAIL  7b. the pushed prefix-sibling must NOT be flagged"
    echo "        got: [$(printf '%s' "$out" | grep feat-prefix)]"; fail=$((fail+1))
else
    echo "  PASS  7b. the pushed prefix-sibling is not flagged"; pass=$((pass+1))
fi

# A repo with no remote at all. Stage 1 drops nothing (the haystack is empty), so
# everything reaches stage 2, and `--not --remotes` with no remotes excludes nothing
# — every local branch is reported. That is the intended behaviour for a guard about
# work existing nowhere else, but it means a remote-less repo lights up on every
# session start, so it is pinned rather than left to be discovered.
NOREMOTE="$ROOT/noremote"
git init -q -b trunk "$NOREMOTE"
(
  cd "$NOREMOTE" || exit 2
  git config user.email test@example.invalid
  git config user.name "Control Harness"
  git config commit.gpgsign false
  echo x > x.txt; git add .; git commit -qm x
)
run_hook "8. no-remotes repo" "$NOREMOTE" || true
out="$HOOK_OUT"
if [ -n "$out" ] && printf '%s' "$out" | grep -q "trunk"; then
    echo "  PASS  8. repo with no remotes -> every branch reported"; pass=$((pass+1))
else
    echo "  FAIL  8. repo with no remotes -> every branch reported"
    echo "        got: [${out:-<empty>}]"; fail=$((fail+1))
fi

# The control this suite most needed and did not have. A data-loss guard whose
# silence means "nothing at risk" must never go silent because it FAILED. Before
# fail_open existed, deleting a single tip object made the reachability walk exit 128
# and the hook print absolutely nothing while unpushed work sat right there — a
# clean report over exactly the state the tool exists to catch.
CORRUPT="$ROOT/corrupt"
git init -q -b trunk "$CORRUPT/work"
git init --bare -q "$CORRUPT/remote"
(
  cd "$CORRUPT/work" || exit 2
  git config user.email test@example.invalid
  git config user.name "Control Harness"
  git config commit.gpgsign false
  echo a > a.txt; git add .; git commit -qm one
  git remote add origin "$CORRUPT/remote"
  git push -q origin trunk:main 2>/dev/null; git fetch -q origin 2>/dev/null
  git checkout -qb unpushed-work; echo b > b.txt; git add .; git commit -qm two
  git checkout -q trunk
)
corrupt_sha=$(cd "$CORRUPT/work" && git rev-parse unpushed-work)
rm -f "$CORRUPT/work/.git/objects/${corrupt_sha:0:2}/${corrupt_sha:2}"

out=$(cd "$CORRUPT/work" && "$BASH" "$SCRIPT" 2>/dev/null); rc=$?
if [ "$rc" -eq 0 ] && printf '%s' "$out" | grep -q "COULD NOT VERIFY"; then
    echo "  PASS  9. unanswerable walk says so rather than reporting clean"; pass=$((pass+1))
elif [ -z "$out" ]; then
    echo "  FAIL  9. unanswerable walk went SILENT — reads as a clean report"; fail=$((fail+1))
else
    echo "  FAIL  9. unanswerable walk: unexpected output/exit ($rc)"
    echo "        got: [$out]"; fail=$((fail+1))
fi

# THE WORKTREE LABEL — untested until now, in BOTH directions, and the more dangerous
# direction is false reassurance. Mutation testing showed a variant that matched
# without newline delimiters reporting "worktree present" for a branch that has none,
# because a prefix-colliding sibling DID have one. That is the acute case (a branch
# with no worktree reads as abandoned and gets pruned) reported as safe. The other
# direction matters too: a variant that always said "no worktree found" also passed
# every control, and false positives train people to ignore the warning.
#
# Note this is where prefix collision genuinely bites — the worktree lookup matches on
# NAME alone, unlike stage 1 which is keyed "<sha> <name>". Control 7 was originally
# credited with covering this; it does not.
git checkout -q trunk
git checkout -qb wt-held; echo w > w.txt; git add .; git commit -qm held
git checkout -qb wt-held-longer; echo wl > wl.txt; git add .; git commit -qm held-longer
git checkout -q trunk
# The worktree goes on the LONGER name, and the assertion below is that the SHORTER
# one reports none. Direction matters and I got it backwards first: a substring match
# tests whether the haystack CONTAINS "branch refs/heads/<name>", so the false hit is
# short-inside-long. With the worktree on the short name the mutant survives; with it
# on the long name the short name wrongly inherits "worktree present". Verified by
# mutation - see the note above.
git worktree add -q --detach "$ROOT/wt" >/dev/null 2>&1
(cd "$ROOT/wt" && git checkout -q wt-held-longer) >/dev/null 2>&1

run_hook "10. worktree label" || true
out="$HOOK_OUT"
held_line=$(printf '%s' "$out" | grep -E "^ +wt-held  \(" || true)
longer_line=$(printf '%s' "$out" | grep -E "^ +wt-held-longer  \(" || true)
if printf '%s' "$longer_line" | grep -q "worktree present"    && printf '%s' "$held_line" | grep -q "no worktree found"; then
    echo "  PASS  10. worktree label correct for a branch and its prefix-colliding sibling"; pass=$((pass+1))
else
    echo "  FAIL  10. worktree label wrong"
    echo "        wt-held-longer (expect 'worktree present'): [${longer_line:-<absent>}]"
    echo "        wt-held (expect 'no worktree found'):       [${held_line:-<absent>}]"
    fail=$((fail+1))
fi

# A branch name that collides with a PATH. Unqualified, `git rev-list --count docs`
# is ambiguous ("both revision and filename") and the count degrades to "?" — and
# because the error is swallowed inside the substitution, the stderr assertion does
# not catch it either. This repo has top-level docs/, src/, tests/ and infra/, and
# the hook runs with cwd at the project root.
mkdir -p docs && echo d > docs/d.txt && git add . && git commit -qm docsdir
git push -q origin trunk:main --force 2>/dev/null; git fetch -q origin 2>/dev/null
git checkout -qb docs; echo dd > dd.txt; git add .; git commit -qm oncdocs
git checkout -q trunk
run_hook "11. path-colliding branch name" || true
docs_line=$(printf '%s' "$HOOK_OUT" | grep -E "^ +docs  \(" || true)
if printf '%s' "$docs_line" | grep -qE "\(1 unpushed commit"; then
    echo "  PASS  11. branch named after a directory still reports a real count"; pass=$((pass+1))
else
    echo "  FAIL  11. branch named after a directory lost its count"
    echo "        got: [${docs_line:-<absent>}]"; fail=$((fail+1))
fi

echo
echo "  $pass passed, $fail failed"
[ "$fail" -eq 0 ]
