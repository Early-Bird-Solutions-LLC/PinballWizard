# Decision log

Sub-ADR decisions for PinballWizard. Append-only. ADRs (in [`adr/`](adr/)) capture architectural decisions with significant trade-offs and alternatives. This log captures the smaller decisions: tool versions within a category, library choices, parameter values, naming conventions, threshold settings — anything worth retrieving later but too small to justify a full ADR.

Per [`guardrails.md`](guardrails.md) § "Decision log": format per entry is

```text
## YYYY-MM-DD — [Short title]
**Decision:** ...
**Alternatives considered:** ...
**Rationale:** ...
**Revisit when:** ...
**Related:** PR #XX, ADR-YYYY (if any)
```

Decisions reverse via a new entry that supersedes the prior one (with a back-reference); never edit history.

## When to add an entry vs. write an ADR

If **all four** of these are true, write an ADR (`adr/00NN-...md`) instead of a decision-log entry:

1. The decision has significant trade-offs.
2. Alternatives were genuinely considered (not default-accepted).
3. Consequences extend beyond the immediate PR.
4. Future readers (including future-Claude) would benefit from the permanent, formally-structured record.

Otherwise, this log is the right home.

---

<!-- New entries append below this marker, newest at the top. -->

## 2026-05-07 — Foundry deploy is two-pass + AI Search deferred from H1

**Decision:** The Phase 3 H1 hand-off (per [build-spec.md § Phase 3 § Operational hand-offs](../docs/build-spec.md)) is a **two-pass deploy**, gated by two new Bicep params on `infra/modules/shared.bicep` (and piped through `infra/main-shared.bicep`):

- `deployFoundryModelDeployments` (default `true`) — set `false` on the FIRST deploy of a fresh Foundry account; flip `true` on a subsequent deploy.
- `deployAiSearch` (default `true`) — set `false` until Phase 4 RAG actually consumes AI Search, OR when the chosen region is at SKU capacity.

H1 succeeded 2026-05-07 against `pinwiz-foundry-dev-hlpz4` / project `pinwiz-wizard`; smoke probe (`--ensure-azure-foundry`) verified the chat (`gpt-4o-mini`) + embedding (`text-embedding-3-large`) deployments are live; the `gpt-4-1` heavy deployment also provisioned cleanly (deployment name uses `-1` not `.1` because Foundry rejects `.` in deployment names; the underlying model is `gpt-4.1`).

**Alternatives considered:**

- **One-shot deploy of (account + project + 3 model deployments)**, originally specified in PR #86. Rejected: failed validation with `InvalidResourceProperties — Policy evaluation returned compliance: for model gpt-4o-mini/2024-07-18 with error:` (the `compliance` and `error` fields were both empty in the API response). Empirically: a fresh `Microsoft.CognitiveServices/accounts` of kind `AIServices` doesn't have its account-scoped Responsible-AI (RAI) policy infrastructure in place at validation time, and the model-deployment validator references it. Splitting the deploy into (1) account + project, (2) model deployments lets the RAI infrastructure initialize between passes.
- **Setting `raiPolicyName: 'Microsoft.DefaultV2'` explicitly** on the deployments. Tried, did not fix the issue (same empty-error validation failure). The default RAI policy attaches automatically once the account is materialized; specifying it on a fresh account doesn't bridge the validation gap.
- **Switching region** away from East US 2. Rejected: ADR-0005 locks East US 2; switching the entire deploy is a bigger architecture change than splitting one deploy into two.
- **Including AI Search in the H1 deploy** anyway. Rejected: the H1 attempt failed with `InsufficientResourcesAvailable` for `Microsoft.Search/searchServices` Basic SKU in East US 2. Phase 3 doesn't actually consume AI Search (Phase 4 RAG does), so deferring it via `deployAiSearch=false` saves ~$74/mo idle and unblocks H1 without compromising functionality. Phase 4 will flip it true when capacity allows or when the region is moved.

**Rationale:** Foundry's account-scoped RAI policy is a runtime-initialized component, not an ARM-template artifact. Bicep's incremental deploy mode handles the two-pass pattern cleanly: pass 2 detects the already-deployed account/project and only adds the model deployments. The pattern is documented in the param descriptions on `shared.bicep`. The `deployAiSearch` gate is a separate concern — it lets the operator skip AI Search when (a) Phase 4 hasn't started consuming it, or (b) the region is at SKU capacity. Both are situational; both have a clear default (`true`) that drops away naturally for steady-state operation.

**Revisit when:** Foundry's ARM contract changes such that fresh accounts bootstrap their RAI policy synchronously (would let us collapse to one-pass; revisit when Microsoft documents `Microsoft.CognitiveServices/accounts/deployments` validation as account-creation-aware). Or when Phase 4 RAG starts consuming AI Search — at that point flip `deployAiSearch=true` and re-deploy. If East US 2 is still capacity-constrained at Phase 4 launch, consider moving AI Search alone to East US (cross-region search-from-app is supported with a small latency penalty) rather than relocating the full stack.

**Related:** PR #86 (Foundry Bicep), this PR (the two-pass split + AI Search gate), [build-spec.md § Phase 3 § Operational hand-offs](../docs/build-spec.md), [ADR-0014](../docs/adr/0014-microsoft-foundry-orchestration.md), [ADR-0013](../docs/adr/0013-two-tier-bicep-deploy.md).

## 2026-05-04 — OPDB `/api/export` gets an on-disk cache + per-source politeness override

**Decision:** `OpdbClient.StreamAllMachinesAsync` now consults a configurable on-disk cache (default path `data/cache/opdb-export.json`, default TTL 1 hour) before issuing a network request. On cache hit the network is bypassed entirely; on cache miss the response is buffered, persisted to disk best-effort, and returned via a memory stream. The `opdb` ingestion-source seed manifest gains an explicit politeness override (`requestDelayMs: 10000` — 10s between successive OPDB requests) replacing the previous `null`.

**Alternatives considered:**

- Honor `X-RateLimit-Remaining: 0` proactively in `IPolitenessGate` (refuse to issue the next outbound request when the response indicates we're at the cap). Rejected for v1: the gate would still need to know when the window resets, which OPDB doesn't communicate via `X-RateLimit-Reset` or `Retry-After`. Inferred reset windows are guesswork; the cache is a more reliable answer.
- Use HTTP `If-None-Match` / ETag to send a conditional request and let OPDB return `304 Not Modified`. Rejected: OPDB's `200` response on `/api/export` doesn't include `ETag` or `Last-Modified` headers (verified live). With nothing to validate against, the cache must be time-based.
- Per-source politeness override only (no on-disk cache). Rejected as insufficient: `requestDelayMs` is a between-requests floor, not a between-`/api/export`-calls floor. It would over-throttle small endpoints (`/api/machines/{id}`) without solving the export-specific 1/hour rule.
- Set `requestDelayMs: 3600000` (1 hour) to forcibly limit OPDB to 1 request per hour. Rejected: over-throttles `GetMachineAsync` and any future small-endpoint calls. The cache eliminates the export-specific problem; 10s is a reasonable floor for the other endpoints.

**Rationale:** OPDB's published policy on `/api/export` is "once per hour" (<https://opdb.org/api>). Today's session burned 5+ export requests across debugging, dry-run, and apply attempts — each retry within the same hour returned 429, cascading into multi-hour cooldowns. The cache makes the rate limit a non-issue: any repeat invocation within the TTL (default 1 hour) reads the persisted body. Cache-miss writes happen best-effort with graceful degradation if the path is unwritable. The 10s `requestDelayMs` for OPDB is conservative — well under the documented 6-per-window throttle on smaller endpoints, but enough that adjacent calls don't thrash.

**Revisit when:** OPDB starts sending `X-RateLimit-Reset` or `Retry-After` headers (would let us replace the time-based cache with an event-driven backoff), or starts sending `ETag` on `/api/export` (would let us upgrade cache freshness checks to conditional GETs). Or if a future Phase 3+ AI feature needs sub-hour OPDB freshness — at which point shorten the TTL or add a CLI flag to bypass the cache.

**Related:** PR #76 (the `/api/export` fix this caches), PR #79 (the alias→edition fold whose 165 successful appends already exercise the export endpoint), the operator burn observed in this session's hand-off run (5+ export hits in ~3 hours).

## 2026-05-04 — Sanitization rules (Item 9) verified locally, not via synthetic-commit CI run

**Decision:** Phase 2 § Scope Item 9 hand-off ("synthetic-token verification") for the sanitization workflow's three email-rule branches is closed via local `grep -E -i` verification rather than via two synthetic test commits pushed to throwaway branches as originally specified in `build-spec.md`.

**Alternatives considered:**

- Push two synthetic test commits to throwaway branches, observe CI fail, delete branches (the originally-specified protocol). Rejected: even with `gh pr close --delete-branch`, the commits remain accessible by SHA via the GitHub API (referenced by the closed PR's commit history) for the ~90-day reflog garbage-collection window. The strings that *trigger* the patterns must by definition match the patterns the rules exist to block — pushing them anywhere on the remote, even briefly, defeats the rule's purpose at a small but real reputational cost. The first attempt at this protocol pushed the user's literal work email to PR #77 before the leak was caught and PR #77 was closed without merging.
- Skip verification entirely and trust the rule's wiring. Rejected: without active verification, the workflow's `if [ -n "${WORK_EMAIL_PATTERN:-}" ]` gate could silently no-op (e.g., the secret value is whitespace, malformed, or the named-secret lookup fails) and a future leak would land on `main` without anyone noticing.
- Mock the workflow's `run_rule` invocation in a unit test under `tests/`. Rejected: the workflow is bash, the project's test suite is xUnit + .NET — no natural place to put a bash test, and a CI YAML test that runs in a separate workflow against the sanitization YAML adds infrastructure for a one-time verification.

**Rationale:** Local `grep -E -i <pattern>` against synthetic placeholder strings (`jim@earlybird-placeholder.invalid`, `noreply@earlybirdsolutions.invalid`, `pattern-test@distilledtech.com`) piped via stdin (no disk writes, no commits) exercises the *exact same* matcher the workflow uses (`grep -E -i "$WORK_EMAIL_PATTERN"` at sanitization.yml:115). Both positive (string matches → rule fires) and negative (similar-but-non-matching strings) cases are confirmed:

| Rule | Pattern | Positive case | Negative case |
| ---- | ------- | ------------- | ------------- |
| Personal email | `jim@earlybird` | `jim@earlybird-placeholder.invalid` → match ✅ | `unrelated-text@otherdomain.example` → no match ✅ |
| Personal domain | `@earlybirdsolutions` | `noreply@earlybirdsolutions.invalid` → match ✅ | `noreply@earlybird.io` → no match ✅ |
| Work email | `@distilledtech\.com` | `pattern-test@distilledtech.com` → match ✅ | `someone@distilledtechXcom` → no match (escape works) ✅ |

The pattern's ERE validity check (sanitization.yml:109 — `printf '' \| grep -E "$WORK_EMAIL_PATTERN"`) returns `rc=1` (no match against empty input), not `rc=2` (malformed pattern), confirming the secret value is a well-formed ERE.

**Revisit when:** A change to the sanitization workflow's matcher logic (e.g., switching from `grep -E` to `ripgrep` or to a different regex flavor) — that would require re-validating the same patterns under the new matcher. Or if a PR ever lands on `main` with one of these patterns inside, indicating the workflow regressed silently.

**Related:** `.github/workflows/sanitization.yml` lines 87–119 (the rule definitions), `feedback_personal_identity_only.md` (the policy these rules enforce), `build-spec.md` Phase 2 § Hand-off outcomes (Item 9 status). PR #77 (the abandoned synthetic-commit attempt — closed without merge after the leak was caught; commits remain in GitHub reflog for ~90 days).

## 2026-05-04 — OPDB integration uses `/api/export`, not paginated `/api/machines`

**Decision:** `OpdbClient.StreamAllMachinesAsync` issues a single GET to `/api/export` and stream-parses the response array via `JsonSerializer.DeserializeAsyncEnumerable<OpdbMachineDto>`. The previously-shipped paginated implementation against `/api/machines?page=...&page_size=...` is removed, along with the now-unused `OpdbOptions.PageSize` property. The standard HTTP resilience handler in `ServiceDefaults` is bumped from 30s/10s (total/attempt) to 120s/50s with a 120s circuit-breaker sampling duration to accommodate the bulk-response endpoint.

**Alternatives considered:**

- Keep paginated `/api/machines?page=...` (PR `d9face6`'s shape). Rejected: the live OPDB API returns 404 on this URL — the endpoint does not exist. The PR `d9face6` unit tests pinned a self-defined contract (`SetResponseFor("/api/machines?page=1...")`) that the real API never honored; tests passed against a fiction.
- Use `/api/changelog` for incremental sync. Rejected for v1: changelog is incremental (recent changes only) and tracking watermarks adds complexity. `/api/export` is simpler and idempotent, fitting Phase 1's "full re-sync each run" semantics. Changelog is a Phase 4+ optimization candidate when the scraper graduates from cron-driven to event-driven.
- Per-client resilience override on `HttpStandardResilienceOptions` named `"OpdbClient-standard"`. Tried, did not take effect — the named-options key the standard handler uses when added via `ConfigureHttpClientDefaults` does not match the per-client name in the obvious way. Bumping the global default in `ServiceDefaults` is the simpler, deterministic fix; OPDB is not the only client that benefits (Stern Vue.js pages routinely take 15–25s with `networkidle` waits, well within the new 120s budget).

**Rationale:** The Phase 2 § Scope Item 4 operational hand-off (OPDB sync against deployed Cosmos) was the first time the OPDB integration hit the live API. The bulletins/games hand-off (PR #75) had already surfaced the same failure pattern — unit tests pinning a self-defined contract that the real API doesn't honor. Same lesson; same shape of fix. The export endpoint is the canonical OPDB bulk catalog (~2.4&#160;MB / ~2,360 machines as of 2026-05-04 — note: prior build-spec estimate of "~12k machines" was off by ~5×; the actual count is ~2.4k).

**Revisit when:** OPDB ships a paginated machines endpoint (would let us avoid bulk-loading the catalog on every sync), or a webhook / changelog-based incremental sync becomes the primary path (Phase 4+ event-driven RAG).

**Related:** PR `d9face6` (original OPDB integration — superseded for the bulk-machines path by this decision), PR #76 (this fix). See also `OpdbClient.cs` xmldoc on `StreamAllMachinesAsync` for the Activator/streaming details and `tests/PinballWizard.Scraper.Tests/Integrations/Opdb/OpdbClientTests.cs` for the contract tests that now pin `/api/export`.

## 2026-05-04 — Stern Playwright DTOs stay as classes, not records

**Decision:** `LinkRaw` (in `GamePageScraper`) and `BulletinRaw` (in `ServiceBulletinScraper`) — the DTO types Playwright deserializes `page.EvaluateAsync<T>()` results into — are `internal sealed class` with `[JsonPropertyName] public T Foo { get; set; }` properties. They are explicitly **not** positional records.

**Alternatives considered:**

- Positional records with `[property: JsonPropertyName(...)]` (PR #72's approach). Rejected: Playwright's `EvaluateArgumentValueConverter.ToExpectedType` calls `Activator.CreateInstance(t)` and walks properties — positional records have no parameterless ctor, so this throws `MissingMethodException` at runtime.
- Non-positional records with `init` setters. Rejected: Playwright's converter assigns properties via the setter at runtime, after the object already exists; `init` setters reject post-construction assignment.
- Custom `JsonConverter<T>` to force STJ deserialization. Rejected: Playwright's converter is hardcoded inside `EvaluateArgumentValueConverter` and does not consult STJ converters for typed deserialization.

**Rationale:** PR #72 reverted these to positional records on the assumption that Playwright 1.59 had switched to System.Text.Json. The post-merge live-site validation (Phase 2 § Scope item 6 hand-off, run 2026-05-04) surfaced the regression: `MissingMethodException: Cannot dynamically create an instance of type '…+BulletinRaw'. Reason: No parameterless constructor defined.` Stack trace pinpointed `EvaluateArgumentValueConverter.ToExpectedType`, confirming Playwright 1.59 still uses Activator-based deserialization (same as 1.12 from PR #34's original workaround). The PR #72 unit tests pinned STJ deserialization, which positional records satisfy — but Playwright never invokes STJ for typed `EvaluateAsync<T>` results. Tests pinned the wrong path.

**Revisit when:** A future Playwright release (post-1.59) genuinely switches to STJ-based deserialization for `EvaluateAsync<T>`. Indicator: source-link in the stack trace no longer references `EvaluateArgumentValueConverter`. Until then, this stays.

**Related:** PR #34 (original class workaround), PR #72 (failed records revert — superseded by this decision), PR currently open against this branch (this revert + Activator-based contract test). See also `tests/PinballWizard.Scraper.Tests/Scraping/Stern/SternPlaywrightDtoActivatorContractTests.cs` for the contract tests that now pin the actual Activator path.
