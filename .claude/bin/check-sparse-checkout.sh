#!/usr/bin/env bash
# SessionStart guard: foreign sparse-checkout silently blocking git add (#918).
#
# Symptom (issue #918): a worktree spontaneously acquires a restrictive
# sparse-checkout whose cone includes `/plugins/redis-development/` — a path
# that does not exist in this repo. `git add` then declines to stage matched
# paths with advisory (often non-failing) output, so agent/scripted flows can
# commit while silently omitting edited files. It has also fired mid-rebase and
# made a committed file briefly absent from disk.
#
# Root writer is outside this repo (foreign plugin layout). Until that is found,
# degrade visibly: detect `core.sparseCheckout=true`, disable it, and say so.
# Auto-disable is the documented workaround and has never been observed to lose
# files (file counts verified identical before/after on multiple occurrences).
#
# ADVISORY ONLY: always exits 0, never blocks session start. Offline — no fetch.
set -uo pipefail

fail_open() {
    printf 'check-sparse-checkout: COULD NOT VERIFY (%s).
' "$1"
    printf '  If git add silently skips paths, run: git sparse-checkout disable
'
    exit 0
}

command -v git >/dev/null 2>&1 || fail_open "git not on PATH"

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0

sparse_on=$(git config --bool --get core.sparseCheckout 2>/dev/null || true)
# git config exits 1 when the key is unset; treat unset/false as clean.
case "${sparse_on:-false}" in
    true|TRUE|True|1) ;;
    *) exit 0 ;;
esac

sparse_file=$(git rev-parse --git-path info/sparse-checkout 2>/dev/null) \
    || fail_open "cannot resolve info/sparse-checkout path"

foreign=""
if [ -f "$sparse_file" ] && grep -qF '/plugins/redis-development/' "$sparse_file" 2>/dev/null; then
    foreign=" (matches #918 foreign cone: /plugins/redis-development/)"
fi

before_count=$(git ls-files 2>/dev/null | wc -l | tr -d ' ') \
    || fail_open "cannot count tracked files before disable"

printf 'check-sparse-checkout: DETECTED core.sparseCheckout=true%s
' "$foreign"
printf '  This can silently drop paths from git add (#918). Disabling now…
'

if ! git sparse-checkout disable 2>/dev/null; then
    printf '  FAILED to disable. Run manually: git sparse-checkout disable
'
    exit 0
fi

after_on=$(git config --bool --get core.sparseCheckout 2>/dev/null || true)
after_count=$(git ls-files 2>/dev/null | wc -l | tr -d ' ') || after_count="?"

case "${after_on:-false}" in
    true|TRUE|True|1)
        printf '  WARNING: core.sparseCheckout still true after disable — investigate manually.
'
        ;;
    *)
        printf '  Disabled. Tracked-file count before=%s after=%s
' "$before_count" "$after_count"
        if [ "$before_count" != "$after_count" ] && [ "$after_count" != "?" ]; then
            printf '  WARNING: file count changed — inspect the working tree before committing.
'
        fi
        ;;
esac

exit 0
