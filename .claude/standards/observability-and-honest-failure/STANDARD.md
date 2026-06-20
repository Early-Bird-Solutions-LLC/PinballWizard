---
name: observability-and-honest-failure
id-prefix: OBS
status: active
applies-to:
  - "src/**/*.cs"
---

# Observability & Honest-Failure Standard

Observability and operability are first-class. Fallbacks must not hide
failures. The system should look healthy from a dashboard, not just from
green tests.

**RULE OBS-01** (no-masking-fallback)
WHEN:   a code path has a degraded or fallback branch
THEN:   the degradation is visible to the user and never presents synthetic/placeholder/stale content as real output
NEVER:  convert a transport/primary failure into fabricated success (the 2026-06-11 "Hello world!" leak)
CHECK:  (qualitative — /local-review) — ask "if the primary path silently died, would anyone know?"
SEV:    🔴
REF:    INVARIANTS#17 · incident-2026-06-11 · PR#363

**RULE OBS-02** (health-endpoints)
WHEN:   adding or modifying a hosted service (Api / Web / Worker)
THEN:   /healthz and /alive remain exposed via ServiceDefaults
NEVER:  remove an existing health endpoint from a deployed app
CHECK:  rg -n "MapDefaultEndpoints|/healthz|/alive" src/
SEV:    🔴
REF:    INVARIANTS#17 (hard exception) · ServiceDefaults

**RULE OBS-03** (no-secrets-in-logs)
WHEN:   adding a log statement
THEN:   log structured context only — never secrets, tokens, connection strings, PII, or a raw entity/request object
NEVER:  interpolate a secret/PII value or a raw request object into a log message
CHECK:  (qualitative — /local-review cat 8) — no secret/PII/connection string interpolated into any log call
SEV:    🔴
REF:    INVARIANTS#17 (hard exception) · local-review cat 8

**RULE OBS-04** (metered-degradation)
WHEN:   a fallback/degraded path executes OR a Cosmos/AI call is made
THEN:   log + meter the underlying failure/latency so it can be root-caused (pinwiz.* instruments)
NEVER:  swallow a failure silently or drop it from telemetry
CHECK:  (qualitative — /local-review) — fallback path increments a meter / writes a structured error
SEV:    🔴
REF:    INVARIANTS#17

## Definition of Done

- OBS-01: degraded paths are visible; no fabricated success.
- OBS-02: health endpoints intact.
- OBS-03: no secret/PII in logs (/local-review cat 8 passes).
- OBS-04: failures are logged + metered.
