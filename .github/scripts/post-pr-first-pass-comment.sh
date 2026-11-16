#!/usr/bin/env bash
# Create-only PR conversation comment for the first-pass review agent (ADR-0069).
# Intentionally does NOT expose --edit-last / edit / delete — those would let
# prompt injection overwrite ADR-0041 triage or other github-actions[bot] comments.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" || $# -ne 1 ]]; then
  echo "usage: $0 <pr-number>   # markdown body on stdin" >&2
  exit 2
fi

pr="$1"
if [[ ! "$pr" =~ ^[0-9]+$ ]]; then
  echo "error: pr-number must be digits, got: $pr" >&2
  exit 2
fi

body="$(cat)"
if [[ -z "${body}" ]]; then
  echo "error: empty comment body on stdin" >&2
  exit 2
fi
if [[ "${body}" != "<!-- pr-first-pass-review -->"* ]]; then
  echo "error: body must start with <!-- pr-first-pass-review -->" >&2
  exit 2
fi

exec gh pr comment "$pr" --body "$body"
