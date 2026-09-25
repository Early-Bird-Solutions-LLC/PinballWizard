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
WHEN:   a dependency this process is configured to use fails, or a code path would switch to a different provider so the operation can continue
THEN:   the failure propagates and fails the operation. The configured provider is an explicit choice. The other provider is not a recovery path. Synthetic, placeholder, or stale content is never presented as real output.
NEVER:  swallow a configured-dependency failure by switching providers and letting the operation succeed. Azure Playwright Workspace authentication failure must not launch local Chromium. A local Chromium failure must not connect to Azure Playwright. Logging and metering the failure does not make that switch honest. NEVER convert a transport/primary failure into fabricated success (the 2026-06-11 "Hello world!" leak).
CHECK:  dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~Obs01NoMaskingFallbackTests" --nologo
SEV:    🔴
REF:    INVARIANTS#17 · incident-2026-06-11 · PR#363 · PR#972

**RULE OBS-02** (health-endpoints)
WHEN:   adding or modifying a hosted service (Api / Web / Worker)
THEN:   /healthz and /alive remain exposed via ServiceDefaults
NEVER:  remove an existing health endpoint from a deployed app
CHECK:  rg -n "MapDefaultEndpoints|/healthz|/alive" src/
SEV:    🔴
REF:    CLAUDE.md (showcase obligations: observability first-class) · ServiceDefaults

**RULE OBS-03** (no-secrets-in-logs)
WHEN:   adding a log statement
THEN:   log structured context only — never secrets, tokens, connection strings, PII, or a raw entity/request object
NEVER:  interpolate a secret/PII value or a raw request object into a log message
CHECK:  (qualitative — /local-review cat 8) — no secret/PII/connection string interpolated into any log call
SEV:    🔴
REF:    CLAUDE.md (showcase obligations) · local-review cat 8

**RULE OBS-04** (metered-degradation)
WHEN:   a fallback/degraded path executes OR a Cosmos/AI call is made
THEN:   log + meter the underlying failure/latency so it can be root-caused (pinwiz.* instruments)
NEVER:  swallow a failure silently or drop it from telemetry
CHECK:  (qualitative — /local-review) — fallback path increments a meter / writes a structured error
SEV:    🔴
REF:    INVARIANTS#17 · CLAUDE.md (showcase obligations: metered degradation)

## Definition of Done

- OBS-01: a configured-dependency failure fails the operation. Switching providers (workspace ↔ local Chromium) does not pass, including when the failure was logged and metered. No fabricated success. The CHECK scans `src/**/*.cs` catch bodies (that glob includes `PlaywrightFactory.cs`) for a provider switch inside a catch.
- OBS-02: health endpoints intact.
- OBS-03: no secret/PII in logs (/local-review cat 8 passes).
- OBS-04: failures are logged + metered.
