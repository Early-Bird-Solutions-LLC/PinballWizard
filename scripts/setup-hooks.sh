#!/usr/bin/env bash
# One-shot setup: point this clone's git config at the tracked .githooks/
# directory and make the hooks executable.
#
# Run once after cloning:
#   bash scripts/setup-hooks.sh
#
# Idempotent — safe to re-run.

set -euo pipefail

cd "$(dirname "$0")/.."

if [ ! -d .githooks ]; then
    echo "✗ .githooks/ not found. Run from the repo root or clone the full repo." >&2
    exit 1
fi

git config core.hooksPath .githooks
chmod +x .githooks/* 2>/dev/null || true

echo "✓ Git hooks activated (core.hooksPath = .githooks)"
echo "  Active hooks:"
ls -1 .githooks | grep -v -E '^(README|\.)' | sed 's/^/    /'
