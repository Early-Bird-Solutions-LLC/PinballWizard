<!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/timeout-debugging.md @ 6dfd2cf
     adapted-for: PinballWizard (verbatim — universal engineering rule)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Timeout Debugging Rule

**Version:** 1.0 | **Scope:** All projects | **Non-Negotiable**

---

## The Rule

> **If something times out, the fix is NEVER to increase the timeout. The fix is to understand WHY it's timing out and resolve the root cause.**

Increasing a timeout is masking a problem, not solving it. A timeout exists because the operation should complete within that time. If it doesn't, something is broken.

---

## Required Steps Before Changing a Timeout

```
1. IDENTIFY what operation is timing out
2. MEASURE how long it actually takes (add timing/diagnostics)
3. DETERMINE why it takes that long
4. FIX the root cause
5. VERIFY the operation completes within the original timeout
6. ONLY THEN consider adjusting the timeout if the root cause is inherent latency that can't be reduced
```

---

## When It's Acceptable to Increase a Timeout

ONLY after ALL of these are true:

- You understand exactly what's happening during the timeout period
- The delay is inherent to the operation (not a bug or misconfiguration)
- You've optimized everything that can be optimized
- The new timeout value is justified with measurements (not guessed)
- You've documented why the timeout needs to be higher

Example: "Blazor Server through App Gateway adds 3-5s for TLS re-encryption + SignalR negotiation. Measured: page renders in 8s via proxy vs 2s direct. Increasing timeout from 15s to 20s with 12s margin."

---

## When It's NEVER Acceptable

- ❌ "It times out at 15s, let's try 30s"
- ❌ "Maybe the timeout is too short"
- ❌ "Increasing the timeout should fix it"
- ❌ Any timeout increase without measuring actual time and understanding the cause

---

## Diagnostic Steps

When something times out:

```
1. Add timing logs around the operation
2. Check what the system is DOING during the timeout period
3. Check network latency (if network-related)
4. Check server-side logs for errors during that window
5. Compare with a working scenario (what's different?)
6. Identify the bottleneck
```

---

## Examples

### BAD (Masking the problem)
```
// Blazor page doesn't render in 15s through App Gateway
// "Fix": increase timeout to 30s
private const int BlazorTimeout = 30000; // ← WRONG
```

### GOOD (Finding root cause)
```
// Blazor page doesn't render through App Gateway
// Investigation:
// 1. Login succeeds ✓
// 2. SignalR negotiate returns 200 ✓
// 3. WebSocket connects ✓
// 4. But components don't render
// Root cause: [identified specific issue]
// Fix: [address the actual problem]
```
