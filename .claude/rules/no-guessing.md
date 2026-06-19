<!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/no-guessing.md @ 6dfd2cf
     adapted-for: PinballWizard (verbatim — universal engineering rule)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# No Guessing Rule

**Version:** 1.0 | **Scope:** All projects | **Non-Negotiable**

---

## The Rule

> **If you are not 100% certain of a value, API parameter, environment variable, configuration option, or tool behavior — STOP and verify before writing it into code, config, or infrastructure.**

This applies to ALL answers, not just code. Configuration values, env vars, Bicep parameters, CLI flags, and third-party tool behaviors are equally dangerous when guessed.

---

## Verification Hierarchy

Before using ANY value in code or config:

```
1. BEST: Read the actual source code of the tool/library
   - Go to GitHub, read the switch statement, enum, or validation
   - Example: lego AZURE_ENVIRONMENT → read providers/dns/azuredns/azuredns.go

2. GOOD: Read official documentation (current version)
   - Check the exact version being used (Dockerfile, package.json, .csproj)
   - Docs for v4.21 may differ from v4.15

3. ACCEPTABLE: Search the web for the specific error message
   - Include the exact error text in quotes
   - Include the tool name and version

4. NEVER: Guess from training data
   - "I believe this value is..."
   - "Typically this is set to..."
   - "In my experience..."
   - These are hallucinations for configuration values
```

---

## When This Triggers

This rule applies whenever you are about to write:

- An environment variable value (`AZURE_ENVIRONMENT=???`)
- A CLI flag or parameter (`--dns azure` vs `--dns azuredns`)
- A Bicep/ARM parameter value
- An API field value
- A configuration file entry
- A Docker base image tag
- A NuGet/npm package version
- Any value that a system will parse and validate

---

## Required Actions Before Writing Config Values

### For environment variables consumed by third-party tools:

```
1. Identify the exact tool and version (Dockerfile ARG, package.json, etc.)
2. Find the source code that reads this env var
3. Find the validation/switch/enum that checks the value
4. Use the EXACT value from the source code
5. Cite the source: "Per lego v4.21 azuredns.go line 42, valid values are..."
```

### For Azure resource properties:

```
1. Check az cli --help for the parameter
2. Or check the ARM REST API spec for valid values
3. Or check the Bicep type definition
4. Never guess enum values — they are case-sensitive
```

### For third-party tool configuration:

```
1. Check the tool's GitHub repo for the config parser
2. Check the tool's official docs for the EXACT version in use
3. If docs are ambiguous, read the source code
4. Web search the exact error message if you hit one
```

---

## Red Flags (STOP Immediately)

If you catch yourself doing ANY of these:

- ❌ Writing a value without having verified it from source/docs
- ❌ Saying "I believe", "I think", "typically", "usually" about a config value
- ❌ Trying a value to "see if it works" without evidence
- ❌ Changing a value after a failure without understanding WHY the previous value was wrong
- ❌ Making the same type of mistake twice (iterating through guesses)
- ❌ Using training data knowledge for tool-specific configuration

**Action:** STOP → Research → Verify → Then write

---

## Anti-Pattern: Guess-and-Check Loop

The worst pattern is:
```
Attempt 1: Set value to X (guessed) → Deploy → Fail
Attempt 2: Set value to Y (guessed) → Deploy → Fail
Attempt 3: Set value to Z (guessed) → Deploy → Fail
...
```

Each iteration wastes 5-15 minutes of deployment time. Three guesses = 45 minutes wasted.

The correct pattern is:
```
Step 1: Research the correct value (5 minutes)
Step 2: Set it correctly the first time
Step 3: Deploy once
```

---

## Accountability

When you make a configuration change, you MUST include:

1. **Source citation** — where you verified the value
2. **Confidence level** — "verified from source" vs "from docs" vs "best effort"
3. **If uncertain** — say "I'm not certain about this value. Let me verify before proceeding."

It is ALWAYS better to pause and research than to guess and iterate.

---

## Examples

### BAD (Guessing)
```
# Set AZURE_ENVIRONMENT for lego
export AZURE_ENVIRONMENT="public"  # ← guessed from training data
```

### GOOD (Verified)
```
# Set AZURE_ENVIRONMENT for lego azuredns provider
# Verified: lego v4.21 providers/dns/azuredns/azuredns.go uses
# cloud.AzurePublic when AZURE_ENVIRONMENT is empty or "public"
# Omitting it entirely — default is Azure Public Cloud
# export AZURE_ENVIRONMENT  ← intentionally not set
```

### BAD (Iterating through guesses)
```
Attempt 1: AZURE_ENVIRONMENT="public" → error: PUBLIC not found
Attempt 2: AZURE_ENVIRONMENT="AZUREPUBLICCLOUD" → untested, might work?
Attempt 3: Remove AZURE_ENVIRONMENT entirely → researched, correct
```

### GOOD (Research first)
```
1. Read lego source: azuredns.go line 42 shows valid values
2. Read go-autorest source: shows case transformation
3. Conclusion: omit the variable, default is correct
4. Deploy once
```
