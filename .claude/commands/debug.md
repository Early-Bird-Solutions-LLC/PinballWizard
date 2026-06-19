<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/debug.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: replaced humanlayer-specific log paths/daemon DB/service names with PinballWizard equivalents: Aspire dashboard, Cosmos emulator, dotnet logs)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
description: Debug issues by investigating logs, Aspire state, and git history
---

# Debug

You are tasked with helping debug issues during manual testing or implementation. This command allows you to investigate problems by examining logs, Aspire dashboard state, and git history without editing files. Think of this as a way to bootstrap a debugging session without using the primary window's context.

## Initial Response

When invoked WITH a plan/ticket file:
```
I'll help debug issues with [file name]. Let me understand the current state.

What specific problem are you encountering?
- What were you trying to test/implement?
- What went wrong?
- Any error messages?

I'll investigate the logs, Aspire state, and git history to help figure out what's happening.
```

When invoked WITHOUT parameters:
```
I'll help debug your current issue.

Please describe what's going wrong:
- What are you working on?
- What specific problem occurred?
- When did it last work?

I can investigate logs, Aspire state, and recent changes to help identify the issue.
```

## Environment Information

You have access to these key locations and tools:

**Aspire AppHost:**
- Start: `./start-apphost.ps1` from repo root
- Dashboard: http://localhost:15888 (structured logs, traces, metrics)
- Cosmos Data Explorer: http://localhost:8081/_explorer/index.html
- Azurite Storage: accessible via connection string from Aspire

**Logs:**
- Aspire structured logs: via dashboard or OTel collector
- dotnet console output: from running `dotnet run --project src/PinballWizard.Cli`
- Application Insights: live Azure (requires deployed infra)

**Cosmos Emulator:**
- Endpoint: https://localhost:8081 (master key auth in emulator mode)
- Check containers: `dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers`

**Git State:**
- Check current branch, recent commits, uncommitted changes

## Process Steps

### Step 1: Understand the Problem

After the user describes the issue:

1. **Read any provided context** (plan or spec file):
   - Understand what they're implementing/testing
   - Note which phase or step they're on
   - Identify expected vs actual behavior

2. **Quick state check**:
   - Current git branch and recent commits
   - Any uncommitted changes
   - When the issue started occurring

### Step 2: Investigate the Issue

Spawn parallel Task agents for efficient investigation:

```
Task 1 - Check Build State:
1. Run: dotnet build 2>&1 | tail -30
2. Note any errors or warnings
3. Check if the project compiles cleanly
Return: Build status and any errors
```

```
Task 2 - Check Test State:
1. Run relevant tests: dotnet test --filter "ClassName=X" 2>&1 | tail -50
2. Note failures and error messages
3. Check test output for clues
Return: Test failures with error details
```

```
Task 3 - Git and File State:
1. Check git status and current branch
2. Look at recent commits: git log --oneline -10
3. Check uncommitted changes: git diff --stat
4. Verify expected files exist
Return: Git state and any file issues
```

### Step 3: Present Findings

Based on the investigation, present a focused debug report:

```markdown
## Debug Report

### What's Wrong
[Clear statement of the issue based on evidence]

### Evidence Found

**From Build/Test**:
- [Error/warning with file:line]
- [Test failure details]

**From Git/Files**:
- [Recent changes that might be related]
- [File state issues]

### Root Cause
[Most likely explanation based on evidence]

### Next Steps

1. **Try This First**:
   ```bash
   [Specific command or action]
   ```

2. **If That Doesn't Work**:
   - Restart Aspire AppHost: `./start-apphost.ps1`
   - Check Aspire dashboard for structured logs
   - Run `dotnet run --project src/PinballWizard.Cli -- --help` to verify CLI wiring

### Can't Access?
Some issues might be outside my reach:
- Aspire dashboard UI state
- Browser console errors (F12)
- Azure portal live data

Would you like me to investigate something specific further?
```

## Important Notes

- **Focus on manual testing scenarios** - This is for debugging during implementation
- **Always require problem description** - Can't debug without knowing what's wrong
- **Read files completely** - No limit/offset when reading context
- **No file editing** - Pure investigation only

## Quick Reference

**Build check:**
```bash
dotnet build src/PinballWizard.sln 2>&1 | tail -20
```

**Run specific tests:**
```bash
dotnet test tests/PinballWizard.Infrastructure.Tests --filter "DisplayName~SternManuals"
```

**CLI help:**
```bash
dotnet run --project src/PinballWizard.Cli -- --help
```

**Git State:**
```bash
git status
git log --oneline -10
git diff --stat
```

**Cosmos containers check:**
```bash
dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers
```

Remember: This command helps you investigate without burning the primary window's context. Perfect for when you hit an issue during manual testing and need to dig into logs, build output, or git state.
