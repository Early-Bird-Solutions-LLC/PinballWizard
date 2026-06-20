<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/playwright-setup/prompt.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed Azure Playwright Testing integration — npm package, service config, az login prereq, workspace role, test:azure script; not applicable to this repo)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# /playwright-setup

Set up Playwright testing for this project.

## Usage

```
/playwright-setup
```

## What This Skill Does

1. **Analyzes your project** - Checks for existing test setup, package.json, TypeScript config
2. **Installs dependencies** - Adds @playwright/test
3. **Creates configuration files**:
   - `playwright.config.ts` - Base Playwright configuration
   - `.env.example` - Environment variable template
4. **Sets up test structure** - Creates tests/ directory with example test
5. **Adds npm scripts** - test, test:headed, test:ui commands
6. **Updates .gitignore** - Excludes test artifacts

## Options

When invoked, the skill will ask about:
- Test directory location (default: `./tests`)
- Base URL for tests
- Which browsers to configure
- Whether to create example page objects

## Prerequisites

- Node.js project with package.json

## After Setup

1. Copy `.env.example` to `.env` and fill in values
2. Run `npx playwright test` to execute tests locally
