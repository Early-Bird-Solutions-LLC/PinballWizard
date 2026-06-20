---
name: iac-deploy
id-prefix: IAC
status: active
applies-to:
  - "infra/**"
---

# IaC Deploy Standard

Phase gate, stack settings, subscription guard, and shell discipline for
controlled, cost-safe infrastructure deployment.

**RULE IAC-01** (two-tier-phase-gate)
WHEN:   adding or modifying `infra/main-shared.bicep` or any subscription-scoped Bicep template
THEN:   Phase 2 resources (App Insights, Key Vault, ACR, AI Search, Azure OpenAI, Storage) are
        gated behind `deployPhase2 bool = false`; the gate flips only in the PR that lands the
        first consuming feature — "we might need it" is not sufficient per guardrails.md § Scope
        discipline; the parameter description MUST carry the destructive-toggle warning (flip
        true→false deletes Key Vault into soft-delete, destroys blob data, AI Search index, ACR
        images)
NEVER:  provision Phase 2 resources unconditionally; flip `deployPhase2 = true` without a
        consuming feature PR
CHECK:  git diff --name-only origin/main...HEAD | grep -E "^infra/main-.*\.bicep$" | xargs -r grep -L "deployPhase2 bool = false" || echo CLEAN
        NOTE: only subscription-scoped entry-point templates (`infra/main-*.bicep`) are checked, and only when changed. Module Bicep files (`infra/modules/*.bicep`) receive `deployPhase2` as a parameter without a `= false` default — they are caller-scoped and legitimately omit it; tfstate templates are unrelated to the Phase 2 gate.
SEV:    🔴
REF:    INVARIANTS#16 · ADR-0013

**RULE IAC-02** (stack-settings)
WHEN:   adding or modifying an infra deploy entry-point script (e.g., `infra/scripts/*.ps1`)
THEN:   every `az stack sub create` / `az stack group create` call passes
        `--action-on-unmanage deleteResources` (orphaned resources are deleted on next deploy)
        and `--deny-settings-mode none` (portal edits permitted for this dev showcase);
        use `Deploy-SharedResources.ps1` as the canonical deploy surface — not bare `az stack` calls
NEVER:  omit `--action-on-unmanage deleteResources` (leaves orphan resources invisible to the stack);
        use `az deployment sub/group create` (covered by DLV-02, REF'd here)
CHECK:  git diff --name-only origin/main...HEAD | grep "^infra/scripts/.*\.ps1$" | xargs -r grep -L "action-on-unmanage deleteResources" || echo CLEAN
        NOTE: only changed PowerShell deploy scripts are checked; also see DLV-02 for the bare-deployment prohibition.
SEV:    🔴
REF:    INVARIANTS#16 · ADR-0013 · DLV-02

**RULE IAC-03** (subscription-guard)
WHEN:   adding a new infra deploy entry-point script
THEN:   the script MUST embed the ADR-0010 subscription/tenant guard — read `az account show`,
        compare `tenantId` and `id` against the hard-coded Earlybird values
        (tenant `9793cd0f-2b27-4757-9986-1f7f1e35864a`, subscription
        `b1f33f17-74a9-4ecc-b46c-c4f31776b840`), and abort with a clear error if either differs;
        a `-SkipGuard` escape hatch is allowed for script-development sandboxes but MUST print
        an unmissable warning and MUST NOT be passed in CI workflows
NEVER:  skip the guard on a new deploy entry point; hard-code a different subscription as the
        expected target without a superseding ADR
CHECK:  (qualitative — /local-review)
        NOTE: a grep for the guard pattern on changed scripts is possible, but new entry-point
        scripts may legitimately omit the guard only if they invoke a helper that itself embeds it
        (no such helper currently exists). Qualitative review is the reliable gate.
SEV:    🔴
REF:    ADR-0010 · feedback_personal_identity_only

**RULE IAC-04** (powershell-for-resource-ids)
WHEN:   writing or running a shell command that passes a Cosmos (or any ARM) resource ID
        (`/subscriptions/...`) as a CLI argument or environment variable
THEN:   use PowerShell (`pwsh`) — not Git-Bash or `sh` — for that command; resource IDs are
        ARM paths starting with `/subscriptions/`; MSYS (Git-Bash) rewrites them to
        `C:/Program Files/Git/subscriptions/...`, silently corrupting the value
NEVER:  pass `/subscriptions/...` resource IDs via Git-Bash or POSIX sh; set
        `Cosmos__AccountResourceId` or equivalent from a Bash shell on Windows
CHECK:  (qualitative — /local-review)
        NOTE: the corruption is silent and environment-specific (Windows Git-Bash only);
        a grep cannot distinguish correct PowerShell invocations from a shell variable that
        happens to contain the same string.
SEV:    🔴
REF:    INVARIANTS#6

## Definition of Done

- IAC-01: all changed Bicep subscription templates declare `deployPhase2 bool = false`; Phase 2 resources are gated; parameter description carries the destructive-toggle warning.
- IAC-02: all changed deploy scripts pass `--action-on-unmanage deleteResources` and `--deny-settings-mode none` to every stack command; no bare `az deployment` (see DLV-02).
- IAC-03: every new deploy entry-point script embeds the ADR-0010 subscription/tenant guard before any Azure call.
- IAC-04: Cosmos and ARM resource IDs are always set and consumed from PowerShell, never from Git-Bash on Windows.
