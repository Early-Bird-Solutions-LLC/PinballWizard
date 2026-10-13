#!/usr/bin/env bash
# SessionStart advisory: local commits that exist ONLY on this machine.
#
# Why this exists: on 2026-09-04 six branches were deleted while a session was
# working. Their commits survived only as unreachable objects and had to be
# recovered by hand. Nothing had ever made that exposure visible — a branch
# carrying unpushed commits looks identical to one without in `git branch`, and
# once its worktree is removed it reads as abandoned.
#
# `.claude/rules/parallel-sessions.md` already says "commit early — an unpushed
# commit survives a discard". This is the missing half: a commit that was never
# PUSHED survives nothing at all. So this reports live state rather than
# restating the rule, and derives the answer from git on every run rather than
# keeping a written list of protected branches — such a list rots the moment a
# branch is pushed or created, and a stale safety list is worse than none.
#
# ADVISORY ONLY: always exits 0, never blocks. Also OFFLINE — it reads existing
# refs and never fetches, so it cannot stall session start or fail on a flaky
# network.
#
# That offline tradeoff cuts BOTH ways, and stating only the benign half would be
# dishonest. Over-reporting: a branch pushed from another clone since the last fetch
# is still listed. UNDER-reporting: a stale remote-tracking ref — the ordinary
# aftermath of a merged-and-deleted PR branch, before anyone prunes — makes commits
# look safe when the remote no longer has them. A clean report therefore means "no
# remote ref THIS CLONE KNOWS OF is missing them", not a guarantee.
#
# Two things are deliberately out of scope, said plainly because the report would
# otherwise read as exhaustive: commits on a DETACHED HEAD (an interrupted rebase, a
# bisect) are invisible, since only refs/heads/ is enumerated; and a remote whose URL
# is itself a local path satisfies the walk despite living on this same machine.
#
# A repo with NO remote configured reports every branch, every session. That is
# correct rather than a bug — with no remote, no commit here exists anywhere else,
# which is precisely the condition this warns about — but it is noisy enough to
# surprise someone, so it is stated here and pinned by a control in the test script.
set -uo pipefail

# Silence is how this script says "nothing is at risk". So any path that CANNOT
# ANSWER must say so out loud instead of falling through to the quiet exit —
# otherwise a broken object store, a missing git, or an argv overflow reports exactly
# like a clean repo. Reproduced before this existed: deleting one tip object made
# `rev-list` exit 128 and the hook print nothing while unpushed work sat right there.
# Still exits 0 (advisory, never blocks); it just refuses to imply "verified".
fail_open() {
    printf 'check-unpushed-branches: COULD NOT VERIFY (%s).
' "$1"
    printf '  Treat unpushed work as UNKNOWN, not absent, before deleting any branch.
'
    exit 0
}

# The hook command resolves the SCRIPT via CLAUDE_PROJECT_DIR while the repo is
# resolved from cwd — two sources of truth for one location, in a tool whose wrong
# answer is silence. Pin them together.
cd "${CLAUDE_PROJECT_DIR:-.}" 2>/dev/null || fail_open "project dir unreachable"

# Distinguish "cannot run git" from "not a repo". The first is a broken guard; the
# second is the one case where silence is genuinely correct.
command -v git >/dev/null 2>&1 || fail_open "git not on PATH"
git rev-parse --git-dir >/dev/null 2>&1 || exit 0

# Reachability, not name matching. A branch with no `origin/<same-name>` may still
# be fully pushed — merged under another name, or its tip contained in some other
# remote ref. "Is this tip reachable from ANY remote ref?" is the question that
# actually matters. Measured on this repo, the name-based approximation called
# eight of nineteen branches unpushed when only four were, and false positives are
# what train people to ignore a warning.
if ! branch_map=$(git for-each-ref --format='%(objectname) %(refname:short)' refs/heads/ 2>/dev/null); then
    fail_open "could not enumerate local branches"
fi
[ -n "$branch_map" ] || exit 0

# TWO STAGES, and what stage 1 is allowed to decide is the whole subtlety.
#
# Stage 2 (the reachability walk) is authoritative but is the most expensive thing
# here, growing with ref count. Stage 1 exists ONLY to shrink its input, and may
# therefore drop a branch only when that branch is provably safe.
#
# It gates on SHA EQUALITY, not name equality. An earlier version matched on name
# — "it has an origin/<name>, so it must be pushed" — which is false for a branch
# AHEAD of its own tracking branch: local `foo` at X, `origin/foo` at W, X not on
# any remote. Those commits exist nowhere but this machine, which is the exact
# exposure this hook was written for and the single most common way work ends up
# local-only (push a branch, then keep committing). Name matching vetoed them
# before the walk ever saw them, so the hook printed a confident clean report over
# precisely the state it exists to catch. A silent false negative in a safety tool
# is worse than no tool.
#
# Matching a "<sha> <name>" pair costs the same as matching a name: a branch level
# with its remote counterpart is still dropped for free, so the fast path survives
# — everything pushed still returns before the walk runs. Ahead and diverged
# branches fall through to stage 2, which decides correctly.
#
# Pure-bash matching, no per-branch spawn. Per-branch subprocess creation dominated
# runtime in profiling on this Windows / Git-Bash host: a version spawning one `grep`
# per branch measured slower than the unfiltered walk it was meant to accelerate.
# Exact figures are load- and platform-dependent and deliberately not quoted here —
# the durable point is that spawns, not git, are the cost to budget. `lstrip=3`
# (rather than stripping the substring "origin/") avoids mangling refs like
# `origin/feat/origin/x` and avoids `refs/remotes/origin/HEAD` collapsing to a bare
# `origin` entry.
remote_pairs=$(git for-each-ref --format='%(objectname) %(refname:lstrip=3)' refs/remotes/origin/ 2>/dev/null)
remote_haystack=$'\n'"$remote_pairs"$'\n'

candidates=""
while read -r sha name; do
    [ -n "$name" ] || continue
    case "$remote_haystack" in
        *$'\n'"$sha $name"$'\n'*) : ;;          # tip identical to its remote counterpart
        *) candidates="$candidates $sha" ;;     # ahead, diverged, or no counterpart
    esac
done < <(printf '%s\n' "$branch_map")
[ -n "${candidates// /}" ] || exit 0

# --stdin rather than argv: this is the authoritative check, and passing SHAs as
# arguments hits the Windows ~32k command-line limit at roughly 800 branches, where
# the failure mode is a clean-looking empty result. Status is checked too, because an
# unreadable object or a broken remote ref must not read as "all clear".
# shellcheck disable=SC2086 -- word splitting of the SHA list is intended here.
if ! unreachable=$(printf '%s
' $candidates | git rev-list --no-walk --stdin --not --remotes 2>/dev/null); then
    fail_open "reachability walk failed — unpushed work UNKNOWN, not absent"
fi
[ -n "$unreachable" ] || exit 0

# Hoisted: one call, reused for every flagged branch.
worktree_refs=$(git worktree list --porcelain 2>/dev/null)

report=""
count=0
for sha in $unreachable; do
    # A tip may carry several branch names; report each.
    while IFS= read -r branch; do
        [ -n "$branch" ] || continue

        # Count what the header actually claims: commits on this branch reachable
        # from NO remote ref. `origin/main..$branch` would be a different quantity
        # — it includes commits already pushed on other remote branches — and
        # printing it beside "exist NOWHERE but this machine" would overstate the
        # exposure. It also needs no origin/main fallback, so it is correct on a
        # clone whose default branch is named something else.
        # Fully qualified: an unqualified short name is ambiguous when a PATH of the
        # same name exists ("fatal: ambiguous argument 'docs': both revision and
        # filename"), which degrades the one number the report shows to "?". This repo
        # has top-level docs/, src/, tests/ and infra/, and the hook runs with cwd at
        # the project root, so a branch named after any of them would hit it.
        at_risk=$(git rev-list --count "refs/heads/$branch" --not --remotes 2>/dev/null || echo "?")

        # Whether a worktree holds it. No worktree is the acute case: invisible to
        # `git worktree list`, reads as abandoned, and is the shape of thing a
        # cleanup sweep deletes. Worded as "none found" rather than asserting there
        # is none — a detached-HEAD worktree emits no `branch refs/heads/<name>`
        # line, so absence here is absence of evidence.
        case $'\n'"$worktree_refs"$'\n' in
            *$'\n'"branch refs/heads/$branch"$'\n'*) where="worktree present" ;;
            *) where="no worktree found — reads as abandoned" ;;
        esac

        report="${report}    ${branch}  (${at_risk} unpushed commit(s); ${where})"$'\n'
        count=$((count + 1))
    done < <(while read -r s n; do [ "$s" = "$sha" ] && printf '%s\n' "$n"; done < <(printf '%s\n' "$branch_map"))
done

[ "$count" -eq 0 ] && exit 0

printf 'Unpushed local commits — no remote this clone knows of has a copy (ADVISORY):\n\n'
printf '%s' "$report"
printf '\n  Deleting one of these branches destroys work no remote has a copy of.\n'
printf '  To protect a branch:  git push -u origin <branch>\n'
printf '  Do NOT prune a branch merely because it has no worktree.\n'

exit 0
