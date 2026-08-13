#!/usr/bin/env bash
#
# Fail-closed preflight for subagent-driven development (SDD) artifact handoffs.
#
# WHY THIS EXISTS
# ---------------
# During the ADR-0054 S0 work, three near-misses happened in one sitting:
#
#   1. A task brief silently over-captured 1178 lines instead of 192, because the
#      plan's headings (`### Task S1.1`) did not match the extractor's `Task <digits>`
#      sentinel, so the task boundary never fired.
#   2. `task-brief` correctly failed (exit 2, "no such plan file") and wrote nothing —
#      which left the PREVIOUS program's brief on disk. It was then read as if fresh.
#      The controller nearly dispatched a subagent to "Recolor the three centralized
#      status helpers" while executing a machine-resolution contract.
#   3. A reviewer was handed a stale report describing an unrelated task
#      ("Thread `search` through IJobLogReader") and had to notice it itself.
#
# None of these was a tool bug. Each was an artifact that LOOKED authoritative and that
# nothing falsified — the same failure class as PR #752 (see docs/learning-from-failure.md
# and issue #758): a fabricated premise that passed every gate because every gate
# validated internal consistency rather than reality.
#
# The fix is not vigilance. Vigilance is what failed. The fix is to make the bad state
# unrepresentable:
#
#   * DELETE BEFORE GENERATE — if generation fails, no stale artifact survives to be
#     mistaken for a fresh one. A missing file fails loudly; a stale file lies quietly.
#   * RUN INSIDE THE WORKTREE — `sdd-workspace` resolves via `git rev-parse --show-toplevel`,
#     which returns the WORKTREE root. Running from the worktree gives each parallel stream
#     its own physically-isolated scratch dir. Running from the main tree makes N concurrent
#     streams share one directory and clobber each other.
#   * VERIFY, DON'T ASSUME — assert the brief's first heading is the task we asked for, and
#     that exactly one task heading is present (no boundary leak).
#
# PLUGIN VERSION RESOLUTION
# -------------------------
# The superpowers plugin cache is searched for the highest installed semver directory
# under $HOME/.claude/plugins/cache/claude-plugins-official/superpowers/. The resolved
# version drives calling-convention selection (see below). Set SDD_SKILL_SCRIPTS to an
# explicit directory to bypass resolution entirely (useful for shims or local overrides;
# version is then detected from script content).
#
# SIGNER-CHANGE DETECTION
# -----------------------
# From plugin version 6.2.0 the sdd-workspace script requires a PLAN_FILE argument;
# earlier versions took no arguments and returned a flat .superpowers/sdd/ directory.
# Version 6.2.0+ uses per-plan subdirectories (.superpowers/sdd/<plan-slug>/).
#
# We prefer explicit version comparison (>= 6.2.0) over content-sniffing so that a
# future breaking change fails loudly rather than silently picking the wrong form.
# Content-sniffing is reserved for the SDD_SKILL_SCRIPTS override path where no
# version number is available.
#
# USAGE
#   sdd-preflight.sh brief   <worktree-dir> <plan-file-relative-to-worktree> <task-number>
#   sdd-preflight.sh report  <worktree-dir> <task-number>
#
# `brief`  prints two lines on success:  BRIEF=<abs path>  /  REPORT=<abs path>
#          Both paths are inside the worktree. Any check failing => non-zero exit, no dispatch.
#
# `report` verifies the implementer actually wrote its report for THIS task before the
#          report is handed to a reviewer. Non-zero exit => do not dispatch the review.
#
set -euo pipefail

# ---- Plugin resolution -------------------------------------------------------

_PLUGINS_BASE="$HOME/.claude/plugins/cache/claude-plugins-official/superpowers"
_PLUGIN_VER=""   # resolved version; empty when SDD_SKILL_SCRIPTS override is active

if [ -n "${SDD_SKILL_SCRIPTS:-}" ]; then
  SKILL_SCRIPTS="$SDD_SKILL_SCRIPTS"
  # Version unknown for the override path — signature will be detected from script content.
else
  if [ ! -d "$_PLUGINS_BASE" ]; then
    echo "sdd-preflight: superpowers plugin cache not found: $_PLUGINS_BASE" >&2
    exit 1
  fi
  _PLUGIN_VER=$(ls "$_PLUGINS_BASE" \
    | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' \
    | sort -V \
    | tail -1)
  if [ -z "$_PLUGIN_VER" ]; then
    echo "sdd-preflight: no versioned plugin dirs found under $_PLUGINS_BASE" >&2
    exit 1
  fi
  SKILL_SCRIPTS="$_PLUGINS_BASE/$_PLUGIN_VER/skills/subagent-driven-development/scripts"
fi

die() { echo "sdd-preflight: $*" >&2; exit 1; }

# ---- Signature detection -----------------------------------------------------

# Returns 0 (true) if semver $1 >= $2.
# Uses GNU sort -V (available in Git Bash on Windows).
_semver_ge() {
  local top
  top=$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)
  [ "$top" = "$1" ]
}

# Returns 0 (true) when the installed sdd-workspace uses the 6.2.0+ signature
# (requires a PLAN_FILE argument; workspace is per-plan-scoped).
#
# When the resolved plugin version is known, we compare it against 6.2.0 explicitly —
# so a future breaking change in a 7.x release will not silently fall through to the
# wrong form; it will produce a version-mismatch error that is easy to diagnose.
#
# When SDD_SKILL_SCRIPTS overrides the scripts dir (version unknown), we detect the
# new signature by checking for the PLAN_FILE string in the script.
_new_workspace_sig() {
  if [ -n "$_PLUGIN_VER" ]; then
    _semver_ge "$_PLUGIN_VER" "6.2.0"
  else
    # Override path: inspect script content. Fail closed (return false) if absent.
    grep -q 'PLAN_FILE' "$SKILL_SCRIPTS/sdd-workspace" 2>/dev/null
  fi
}

# ---- Commands ----------------------------------------------------------------

cmd_brief() {
  local wt=$1 plan=$2 n=$3
  local ws brief report slug root_top base

  [ -d "$wt" ] || die "worktree not found: $wt"
  # Everything below runs INSIDE the worktree so sdd-workspace resolves to the
  # worktree's own scratch dir — this is what isolates parallel streams.
  cd "$wt" || die "cannot enter worktree: $wt"

  [ -x "$SKILL_SCRIPTS/task-brief" ] || [ -f "$SKILL_SCRIPTS/task-brief" ] \
    || die "task-brief not found at $SKILL_SCRIPTS (set SDD_SKILL_SCRIPTS)"

  # Compute workspace directory BEFORE checking plan existence, so we can honour
  # DELETE BEFORE GENERATE unconditionally.
  #
  # For 6.2.0+ sdd-workspace requires the plan file to exist, but we need to
  # delete stale artifacts before that check. We derive the path ourselves (same
  # logic: <repo-root>/.superpowers/sdd/<plan-basename-without-.md>/) and create
  # the directory ourselves after the plan check.
  #
  # For pre-6.2.0 the no-arg sdd-workspace does not read the plan, so we call it
  # directly; it also creates the flat .superpowers/sdd/ directory.
  if _new_workspace_sig; then
    slug=$(basename "$plan" .md)
    root_top=$(git rev-parse --show-toplevel 2>/dev/null) \
      || die "cannot determine git root from worktree: $wt"
    base="$root_top/.superpowers/sdd"
    ws="$base/$slug"
  else
    ws=$(bash "$SKILL_SCRIPTS/sdd-workspace")
  fi

  brief="$ws/task-${n}-brief.md"
  report="$ws/task-${n}-report.md"

  # DELETE BEFORE GENERATE — and do it BEFORE any other validation can abort us.
  # If we die on a bad plan path while a previous program's brief is still on disk,
  # the next reader consumes that stale file as if it were fresh. That is the exact
  # near-miss this script exists to prevent, so the removal must be unconditional.
  rm -f "$brief" "$report"

  [ -f "$plan" ] || die "plan not found inside worktree: $wt/$plan (stale artifacts removed)"

  # For 6.2.0+ ensure the plan-scoped workspace dir exists (task-brief uses the
  # explicit OUTFILE path and does not call sdd-workspace itself when given 3 args).
  if _new_workspace_sig; then
    mkdir -p "$ws"
    printf '*\n' > "$base/.gitignore" 2>/dev/null || true
  fi

  # task-brief creates the output file via awk redirect before checking whether the
  # task heading was found, so a not-found exit still leaves an empty file on disk.
  # Remove it immediately so the "nothing stale left behind" invariant is exact.
  if ! bash "$SKILL_SCRIPTS/task-brief" "$plan" "$n" "$brief" >/dev/null; then
    rm -f "$brief"
    die "task-brief failed for task $n — no brief written, nothing stale left behind"
  fi

  [ -s "$brief" ] || die "brief is empty: $brief"

  # The brief must BE the task we asked for. Guards against a plan whose headings
  # don't match the extractor's sentinel, and against any silent fallback.
  local first
  first=$(head -1 "$brief")
  echo "$first" | grep -qE "^#+[[:space:]]+Task[[:space:]]+${n}([^0-9]|$)" \
    || die "brief's first heading is not Task ${n} — got: ${first}"

  # Exactly one task heading => the extractor's boundary fired.
  #
  # NOTE the regex: `Task[[:space:]]` with NO digit requirement. An earlier version of this
  # check counted only `Task <digits>` headings — and therefore could not see a swallowed
  # `### Task S3.1`, which is precisely the non-numeric heading that caused the original
  # 1178-line over-capture. A check that cannot detect the bug it was written for is not a
  # check. Count EVERY task-shaped heading.
  local count
  count=$(grep -cE '^#+[[:space:]]+Task[[:space:]]' "$brief" || true)
  [ "$count" -eq 1 ] \
    || die "brief for task ${n} contains ${count} task headings — boundary leaked (it swallowed following tasks); every task in the plan must have its own '### Task <integer>:' heading, numbered sequentially"

  echo "BRIEF=$brief"
  echo "REPORT=$report"
}

cmd_report() {
  local wt=$1 n=$2

  [ -d "$wt" ] || die "worktree not found: $wt"
  cd "$wt" || die "cannot enter worktree: $wt"

  # Locate the workspace directory.
  #
  # For 6.2.0+ workspaces are per-plan-scoped; cmd_report does not know which plan
  # was used. We find the workspace by searching for the brief that cmd_brief wrote.
  # Zero or multiple matches are both errors — fail closed in both cases.
  #
  # For pre-6.2.0 (flat workspace), call sdd-workspace with no arguments.
  local ws brief report
  if _new_workspace_sig; then
    local matches=()
    for f in "$wt/.superpowers/sdd"/*/task-${n}-brief.md; do
      [ -f "$f" ] && matches+=("$f")
    done
    case ${#matches[@]} in
      0) die "no brief for task ${n} found under $wt/.superpowers/sdd — run 'brief' first (brief locates the plan-scoped workspace)" ;;
      1) ws=$(dirname "${matches[0]}") ;;
      *) die "ambiguous: found ${#matches[@]} briefs for task ${n} under $wt/.superpowers/sdd — cannot determine which plan-scoped workspace to verify; re-run 'brief' for the correct plan to disambiguate" ;;
    esac
  else
    ws=$(bash "$SKILL_SCRIPTS/sdd-workspace")
  fi

  brief="$ws/task-${n}-brief.md"
  report="$ws/task-${n}-report.md"

  # The implementer said DONE — but did it actually write a report, for THIS task?
  # A reviewer handed a stale report reviews fiction.
  [ -f "$report" ] || die "no report at $report — the implementer did not write one; do NOT dispatch a reviewer"
  [ -s "$report" ] || die "report is empty: $report"

  # The report must be newer than the brief it answers. An older file is a leftover.
  if [ -f "$brief" ] && [ "$report" -ot "$brief" ]; then
    die "report is OLDER than its brief — stale artifact from a previous run: $report"
  fi

  echo "REPORT_OK=$report"
}

main() {
  local sub=${1:-}
  case "$sub" in
    brief)
      [ $# -eq 4 ] || die "usage: sdd-preflight.sh brief <worktree-dir> <plan-file> <task-number>"
      cmd_brief "$2" "$3" "$4"
      ;;
    report)
      [ $# -eq 3 ] || die "usage: sdd-preflight.sh report <worktree-dir> <task-number>"
      cmd_report "$2" "$3"
      ;;
    *)
      die "usage: sdd-preflight.sh {brief|report} ..."
      ;;
  esac
}

main "$@"
