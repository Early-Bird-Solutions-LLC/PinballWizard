<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/ci-preview/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (verbatim)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
name: ci-preview
description: >-
  Preview CI checks locally before pushing
---

# CI Preview

Run common CI checks locally to catch issues before pushing to remote.

## Purpose

Save time by catching CI failures locally before waiting for pipeline runs.

## Checks to Run

Based on the detected project type, run appropriate checks:

### 1. Project Detection

First, detect the project type:
- `package.json` → Node.js/TypeScript project
- `*.csproj` or `*.sln` → .NET project
- `requirements.txt` or `pyproject.toml` → Python project
- `go.mod` → Go project
- `Cargo.toml` → Rust project

### 2. Lint Check

- **Node.js**: `npm run lint` or `npx eslint .`
- **.NET**: `dotnet format --verify-no-changes`
- **Python**: `ruff check .` or `flake8`
- **Go**: `go vet ./...`

### 3. Type Check

- **TypeScript**: `npx tsc --noEmit`
- **.NET**: Included in build
- **Python**: `mypy .` (if configured)

### 4. Build Check

- **Node.js**: `npm run build`
- **.NET**: `dotnet build`
- **Go**: `go build ./...`
- **Rust**: `cargo build`

### 5. Test Check

- **Node.js**: `npm test`
- **.NET**: `dotnet test`
- **Python**: `pytest`
- **Go**: `go test ./...`

### 6. Security Scan (Quick)

Look for obvious issues:
- Hardcoded secrets (API keys, passwords)
- SQL injection patterns
- XSS vulnerabilities in templates
- Outdated dependencies with known vulnerabilities

## Output Format

Provide a summary table:

```
=== CI Preview Results ===

| Check      | Status | Details              |
|------------|--------|----------------------|
| Lint       | ✅ PASS |                      |
| Types      | ✅ PASS |                      |
| Build      | ✅ PASS |                      |
| Tests      | ❌ FAIL | 2 tests failing      |
| Security   | ⚠️ WARN | 1 potential issue    |

Overall: FAIL - Fix test failures before pushing
```

## Instructions

1. Detect the project type from files in the current directory
2. Run each applicable check in sequence
3. Capture pass/fail status and any error messages
4. For failures, show the specific error output
5. Provide a summary with overall pass/fail recommendation
6. If everything passes, confirm it's safe to push

## Error Handling

- If a check tool is not installed, skip with warning
- If a check has no configuration (e.g., no eslint config), skip with note
- Always complete all checks even if some fail
- Provide actionable suggestions for fixing failures
