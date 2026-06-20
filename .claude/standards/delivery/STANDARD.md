---
name: delivery
id-prefix: DLV
status: active
applies-to:
  - "**/*"
---

# Delivery Standard

Identity, deploy safety, and commit/PR hygiene for controlled delivery.
This standard's globs match all files; its rules gate the commit/push of any
change.

**RULE DLV-01** (personal-identity)
WHEN:   committing
THEN:   the commit authors as Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>
NEVER:  author a commit with the work email or any non-personal identity
CHECK:  git log -1 --format='%ae'   # must equal 94459922+jkeeley2073@users.noreply.github.com
SEV:    🔴
REF:    INVARIANTS#5 · feedback_personal_identity_only

**RULE DLV-02** (deployment-stacks-only)
WHEN:   adding or modifying an infra deploy script
THEN:   deploy via az stack sub create / az stack group create
NEVER:  use az deployment sub create / az deployment group create (orphans resources)
CHECK:  rg -n "az deployment (sub|group) create" infra/scripts/
SEV:    🔴
REF:    INVARIANTS#16 · feedback_deployment_stacks_only

**RULE DLV-03** (zero-warning-build)
WHEN:   completing a code change
THEN:   the build is zero-warning; treat new warnings as bugs
NEVER:  push code that introduces a new compiler/analyzer warning
CHECK:  dotnet build PinballWizard.slnx --nologo -warnaserror
SEV:    🔴
REF:    PR-AUDIT#6

**RULE DLV-04** (conventional-commit-no-attribution)
WHEN:   writing a commit message
THEN:   use conventional format `type(scope) message`; no Claude attribution trailer
NEVER:  add a Co-Authored-By: Claude / Generated-with trailer (does not match repo history)
CHECK:  git log -1 --format='%B' | rg -i "Co-Authored-By: Claude|Generated with" && echo "VIOLATION" || echo "CLEAN"
SEV:    ⚠️
REF:    pinball-workflows · feedback_personal_identity_only

**RULE DLV-05** (no-hardcoded-sub-ids)
WHEN:   adding or modifying a runbook script
THEN:   derive subscription via `az account show --query id -o tsv`
NEVER:  hardcode a subscription UUID or instance-specific resource suffix in docs/runbooks/
CHECK:  rg -ni "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}" docs/runbooks/
SEV:    🔴
REF:    PR-AUDIT#12

## Definition of Done

- DLV-01: commit identity is the personal noreply.
- DLV-02: no bare `az deployment` in infra scripts.
- DLV-03: zero-warning build.
- DLV-04: conventional commit, no Claude attribution.
- DLV-05: no hardcoded sub IDs in runbooks.
