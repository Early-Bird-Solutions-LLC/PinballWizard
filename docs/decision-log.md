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

## 2026-06-12 — Retrieval top-K and minimum score go runtime-mutable; the RAG settings tab activates

**Decision:** `rag.retrieval_top_k` (1–20) and `rag.retrieval_minimum_score` (0.0–1.0) join `WellKnownSettings`, resolved through the same stored-override → `IOptions` layering and consumed at call time by `SearchCorpusTool` — one `IRuntimeSettings` snapshot per tool invocation (the tool is a singleton living outside `AiRouter`'s snapshot scope, so it resolves its own; the repository's TTL cache keeps that ~free). A model-supplied top-K still wins, clamped; the runtime value applies when the model omits it. Sub-agents (Repair/Rules/Valuation) inherit automatically — same singleton via DI. With real consumers wired, `/admin/settings`' RAG tab replaces its placeholder with live controls (this PR), keeping the no-dead-config rule intact in both directions.

**Alternatives considered:** threading retrieval values from `AiRouter`'s snapshot through the agent-framework tool-call boundary — rejected: the framework drives tool invocation internally and the threading would couple the router to tool internals; per-invocation resolution at the tool is the narrower seam.

**Revisit when:** retrieval tuning needs per-topic profiles (a single global top-K stops fitting), or eval data shows min-score changes shifting citation precision enough to warrant guarded rollout.

**Related:** PR-B1/PR-B2 entries above, SearchCorpusTool (Phase 4 W4-1 design notes), built in parallel by a worktree agent per the 2026-06-12 parallel-tracks dispatch.

## 2026-06-12 — /admin/settings ships with two live tabs and two honest placeholders (PR-B2)

**Decision:** The settings page (admin settings plan, Phase 2) edits the PR-B1 key set through `IAdminSettingsRepository`: Guardrails tab (confidence-threshold slider, cost-ceiling field) and Conversation tab (max turns). Per-setting affordances: default hint, override provenance (`updatedBy` + `updatedAtUtc`), reset-to-default behind a confirm (delete = revert), dirty-tracked save that writes only changed rows after `WellKnownSettings.TryValidate`. `updatedBy` resolves from the cascading `AuthenticationState` (newly wired via `AddCascadingAuthenticationState` on the auth-configured path); the no-tenant dev path records an explicit local-dev marker instead of a fake name. A failed settings load renders an error state that says the Wizard itself is unaffected and offers no dead controls.

**The placeholder tabs are deliberate:** RAG Retrieval and Prompt Templates render explanatory empty states rather than disabled knobs. Wiring controls before their keys have call-time consumers would ship settings that silently do nothing — the dead-config class /local-review exists to catch. They activate with their consumers (retrieval keys) and Phase 3 (prompts + ADR-0018 amendment).

**Alternatives considered:** separate pages per area (the repo's pages-not-tabs norm) — rejected here per the plan's explicit carve-out: these are fields, not grids, and four shallow pages would bury two controls each; hiding the placeholder tabs entirely — rejected: the roadmap visibility is showcase-relevant and prevents "where do I tune retrieval?" confusion.

**Revisit when:** retrieval keys gain consumers (tab 3 goes live), Phase 3 lands (tab 4), or the setting count outgrows the four-tab layout.

**Related:** thoughts/shared/plans/AB-259-admin-settings-page.md (Phase 2), PR-B1 entry above, ADR-0008 (MudBlazor strict), ADR-0009 (AdminOnly).

## 2026-06-12 — Runtime-mutable Wizard settings: Cosmos overrides layered over IOptions (PR-B1)

**Decision:** The `/admin/settings` surface gets its storage + read path (admin settings plan, Phase 1). A new `admin_settings` Cosmos container (partition `/key` = id, point reads only; no TTL — auto-expiry would silently revert operator decisions; default indexing — tens of tiny docs) holds overrides as `{key, value, updated_at_utc, updated_by}`. `IAdminSettingsRepository` (Application) + `CosmosAdminSettingsRepository` (Infrastructure) front it with Conflux's proven 2-minute TTL cache including negative entries, evicted after successful writes. `IRuntimeSettings.GetSnapshotAsync` resolves the layering rule — stored override → `IOptions<AiFoundryOptions>` default — once per ask, and `AiRouter` consumes the snapshot for `ConfidenceThreshold`, `PerCallCostCeilingUsdCents`, and `MaxConversationTurns` (the guardrail + history-trim reads). Changes apply within one cache window, no restart. `WellKnownSettings` is the closed key set with server-side ranges (0.3–0.95 / 1–100¢ / 1–20, the last pinned under the API's 20-turn guard).

**Deliberately absent keys (no dead config):** `ChatDeploymentName`/`AgentModels` wait for Phase 3's agent-cache invalidation hook; `SemanticCacheMaxEntries` is construction-time (restart-apply — wire when the page can say so honestly); `EmbeddingDeploymentName` is permanently excluded (re-index required). A stored-but-unparsable value (only reachable outside the validated write path) degrades to the default with a warning naming the row; repository failures propagate — an ask fails loudly rather than silently running on defaults while the operator believes an override is live (invariant #17).

**Alternatives considered:** Azure App Configuration — right answer in an APS-fleet context, but a new always-on resource + SDK for three keys inside a $400/mo envelope when Cosmos is already wired and idle-cheap; rejected for now. `IOptionsMonitor` + config reload — only re-reads the providers the host already has (env vars on ACA require a revision bump = restart); doesn't deliver runtime mutation. Per-read Cosmos without cache — three point reads per ask for data that changes monthly; rejected.

**Revisit when:** Phase 3 lands (prompt overrides + agent-cache eviction unlock the model-selection keys), or the key count grows past what a hand-rolled registry maintains comfortably (App Config threshold).

**Related:** thoughts/shared/plans/AB-259-admin-settings-page.md (Phase 1), ADR-0012 (container creation path), ADR-0015 (ceiling semantics unchanged), ADR-0025 (point-read + metering conformance), Conflux AppConfigurationService (pattern source).

## 2026-06-12 — The Wizard becomes a conversation: client-held history, chat-thread UI

**Decision:** Multi-turn conversations ship in three PRs per `thoughts/shared/plans/AB-259-multi-turn-conversation.md` (Jim's decisions 2026-06-11: client-held history + chat-thread UI). PR-A1 (#374): `IAiRouter` history overloads — prior turns prepended as user/assistant ChatMessages (capped `MaxConversationTurns`=8 oldest-first, per-field `MaxConversationTurnContentChars`=4096 because history is client-supplied), semantic cache bypassed in both directions on history (metered `pinwiz.ai.cache.bypass_multiturn`), and citation inheritance — a follow-up answered from conversation context inherits the most recent cited turn's citations flagged `Inherited=true`, run before confidence computation; no citations anywhere still refuses. PR-A2 (#379): `WizardAskRequest.History` + a >20-turn 400 guard. PR-A3: the thread UI — completed turns render above the input through the same TokenRenderer/CitationStrip surfaces, "Ask a follow-up" keeps the thread (refusal/error turns never join it), "New conversation" clears it, inherited citations carry an "earlier in this conversation" chip, client turn cap mirrors the API guard.

**Conversation state lives nowhere:** circuit component state + the request that carries it (ADR-0027 follow-up 2026-06-12 records why that is not the banned session-history surface; ADR-0015/0026 follow-ups 2026-06-11 record the cache and contract amendments).

**Test-intent preservation:** the repeat-ask cache-hit E2E now resets via "New conversation" — the follow-up button would make its second ask multi-turn, which bypasses the cache and would have silently destroyed the coverage that test exists for. The new follow-up E2E asks a pronoun-only question ("who designed it") — unanswerable without history — as the live proof the context rides the wire.

**Alternatives considered:** Foundry agent threads (`WizardAnswer.FoundryThreadId`) — native but the 1.4.0 session surface is unstable (SDK issue #2688), revisit when stable; server-side Cosmos conversation container (architecture-v2 § 8) — right for authenticated long-term history later, requires ADR-0027 amendment; hashing history into the cache key — hit probability ~zero, all cost no benefit over bypass.

**Revisit when:** the SDK thread surface stabilizes (server-side threads would shrink request payloads), eval data shows inherited-citation answers degrading precision, or `cache.bypass_multiturn` dominates ask volume (cost pressure).

**Related:** PR #374, #379, PR-A3; ADR-0015/0026/0027 follow-ups; issue #371 (markdown rendering — same surface, separate fix).

## 2026-06-12 — /admin gated by the GlobalAdmin Entra app role (AdminOnly policy)

**Decision:** Every `/admin/*` page carries `[Authorize(Policy = "AdminOnly")]`, where the policy requires the `GlobalAdmin` Entra app role (the role name ADR-0009 defined for v1 admin RBAC) (`RequireRole` — Microsoft.Identity.Web maps app-role claims to `ClaimTypes.Role`). This supersedes the earlier posture pinned by `AuthorizationContractTests` that per-page `[Authorize]` on admin routes was redundant: the `FallbackPolicy` proves *authentication*, and that was an acceptable bar for read-mostly grids — not for the runtime settings surface that follows (admin-settings plan, Phase 0), which mutates live Wizard behavior. The contract tests now pin the inverse: a new admin page WITHOUT the policy fails at authoring time, and coverage extends to all five admin pages (DocumentTriage and LinkOverrides were previously untested).

**Two halves:** this code half is inert until the infra half lands — `AzureAd:TenantId`/`ClientId` are not yet set on the deployed Container App (auth is skipped entirely on the no-tenant path, where the policy is registered permissively so pages render in local dev; that branch is the documented dev posture, not a fallback). The infra half: create the `GlobalAdmin` app role on the Entra app registration, assign Jim, set the AzureAd config on the ACA app. Until then the Cloudflare Access OTP gate remains the only effective protection — unchanged from the status quo.

**Alternatives considered:** (a) keep FallbackPolicy-only and gate just the future settings page — leaves the mutating triage/link-override actions on the weaker bar, rejected; (b) narrow the FallbackPolicy itself to the role — would lock public pages behind the role too, requiring [AllowAnonymous] inversion churn, rejected; (c) path-based middleware check — stringly-typed, invisible to the contract tests, rejected.

**Revisit when:** the infra half lands (then E2E-smoke the 403-without-role path), or additional roles appear (e.g., a read-only operator role splitting the triage surface).

**Related:** thoughts/shared/plans/AB-259-admin-settings-page.md (Phase 0), AuthorizationContractTests, ADR-0009 (Entra External ID scaffolding).

## 2026-06-11 — CSP promoted to enforced; Bot Management JS Detections disabled (the only edge-injected violator)

**Decision:** The edge CSP ([infra/cloudflare/headers.tf](../infra/cloudflare/headers.tf)) is promoted from `Content-Security-Policy-Report-Only` to enforced `Content-Security-Policy`, with `upgrade-insecure-requests` reintroduced (Report-Only policies ignore it). In the same change, Bot Management JavaScript Detections is switched off (`enable_js = false`, [infra/cloudflare/waf.tf](../infra/cloudflare/waf.tf)). `CspPolicySyncTests.EdgePolicy_IsEnforced_NotReportOnly` pins the promotion.

**Why JSD had to go first:** JSD injects an *inline* script into every HTML response on the Cloudflare path. Per Cloudflare's docs, the only strict-CSP accommodation is nonce propagation — Cloudflare parses a nonce from the CSP response header and stamps it onto the injected script — which requires a fresh per-request nonce. Our header is a static Transform Rule that cannot mint one (a fixed nonce is cryptographically meaningless), hash allowances for JSD are not supported (open Cloudflare feature request), and nonce-via-`<meta>` is documented as unsupported. Under enforcement the JSD script would simply be refused. Disabling it is safe here: JSD only feeds `cf.bot_management.js_detection.passed` (no WAF rule keys on it — enforcement against bots does not occur from JSD alone), and behind the Cloudflare Access OTP gate the JS bot-signal is near-zero marginal value.

**Promotion evidence:** the policy was tuned to zero violations 2026-06-11 (decision-log entry "CSP posture", PR #357) and simulated flat-zero across all public routes; the scoped-CSS bundle added by PR #372 is same-origin (`style-src 'self'`); the remaining DevTools noise on pinwiz.ai was exclusively the JSD inline script, removed by this change. The §7.2 week-long soak was shortened deliberately: with no report-uri receiver, soak produces no signal a re-run simulation doesn't.

**Alternatives considered:** (a) Keep Report-Only forever — noise + zero protection, rejected (an evaluator inspecting headers sees a policy that never enforces). (b) Enforce with `'unsafe-inline'` to accommodate JSD — guts the XSS-load-bearing directive, explicitly discouraged by Cloudflare, rejected. (c) Worker-minted per-request nonce — the correct public-launch design if JSD is wanted back; deferred, tracked in #356. (d) Keep JSD and accept the console error under enforcement — silently broken bot signal + permanent DevTools noise, rejected.

**Revisit when:** public launch (JSD value increases without the Access gate — revisit the Worker-nonce option), or Cloudflare ships hash support for JSD injection.

**Related:** issue #356, PR #357 (tuning), PR #372 (scoped-CSS bundle), CLOUDFLARE_PRELAUNCH_CHECKLIST §7.2.

## 2026-06-11 — Pre-rendered SVG replaces client-side Mermaid on the About page

**Decision:** The `/about` architecture diagram is a pre-rendered SVG committed at `src/PinballWizard.Web/wwwroot/img/about-architecture.svg`, served statically via `MudImage`. The diagram source of truth is [docs/diagrams/about-architecture.mmd](diagrams/about-architecture.mmd) (regeneration command in its header); `PreRenderedDiagramTests` pins SVG ↔ source agreement via a SHA-256 of the `.mmd` embedded as a comment on the SVG's second line, so editing the source without re-rendering fails CI with the expected hash printed. Consequences: About.razor drops `@rendermode InteractiveServer` (it only existed for Mermaid) and its `HeadContent` script block; the edge CSP's `script-src` shrinks to `'self'` + the single App.razor bootstrap hash — no third-party script host remains anywhere in the policy, and `CspPolicySyncTests` now asserts that stays true. Scope: app-served diagrams only (About was the only one); fenced mermaid blocks in `docs/` stay native — GitHub renders them, and they have no CSP or privacy surface.

**Alternatives considered:** (a) Status quo (CDN + version pin + SRI, PR #357) — integrity was closed, but every /about load still leaked visitor IPs to jsDelivr, inconsistent with the repo's self-hosted-fonts precedent (`SelfHostedFontsTests`, citing the Google Fonts GDPR ruling), and shipped a 3.3 MB script for a diagram that changes a few times a year. (b) Self-hosting `mermaid.min.js` — attempted and blocked: the minified bundle false-positives the sanitization scan's PEM private-key rule (loose three-marker match, trivially true across megabyte-long minified lines); admitting it requires narrowing that rule to the canonical dashed PEM form, a security-gate change parked for deliberate review in #356. (c) CSP nonces instead of hashes/URLs — structurally impossible here: the header is a static Cloudflare Transform Rule that cannot mint per-request values (becomes viable only if CSP emission moves to the origin or a Worker). (d) Build-step rendering (regenerate the SVG in CI) — deferred as over-engineering for one diagram; the hash test makes drift loud, which is the part automation would buy.

**Rationale:** Strictly better on every axis the CSP work was optimizing: the only third-party script dependency disappears (supply-chain + privacy), the page sheds its interactive render mode (static content renders at first paint, no circuit), and the diagram becomes exactly as version-controlled as the code (the SVG diff shows in review). The .mmd keeps the diagram editable — this is not a screenshot committed to the repo.

**Revisit when:** an app-served diagram needs interactivity (pan/zoom/click-through), or the count of pre-rendered diagrams grows past ~3 (then add the CI render step from (d) instead of hand-running mermaid-cli).

**Related:** issue #356 (CSP rollout + self-host deferral), PR #357 (the pin+SRI predecessor this supersedes), decision-log 2026-06-11 CSP posture entry, `PreRenderedDiagramTests`, `CspPolicySyncTests`.

## 2026-06-11 — INCIDENT: asks died while answers computed — scale-from-zero, SSE-vs-resilience retry storms, a success-fabricating placeholder, and a citation render race

**Incident:** Through the morning of 2026-06-11 (first failed wake 11:52Z) the deployed site intermittently returned no answer at all, then — after the transport fixes — repeat asks rendered answers with no citations. Three independent transport/render defects cascaded, and a fourth (the placeholder fallback) hid them from users and operators alike.

**Root cause (four compounding defects):**

1. **Scale-from-zero cannot serve a 3-minute boot.** The Api Container App ran `minReplicas: 0`; ContainerAppSystemLogs show ~2.5–3 min from KEDA wake to listening. Every wake cycle burned out against client timeouts (observed 11:52Z / 12:23Z / 12:53Z) — the app repeatedly scaled up, missed the window, and scaled back to zero. Boot-time root cause is tracked separately (#361).
2. **The SSE endpoint never flushed headers before the agent's first chunk.** Time-to-first-byte equaled model first-token latency, so HTTP attempt timeouts measured "the whole answer" instead of "the connection opened."
3. **Two stacked standard-resilience pipelines retried a non-idempotent LLM run.** ServiceDefaults' `ConfigureHttpClientDefaults` pipeline (50s attempt / 120s total) stacks ON TOP of the per-client pipeline (10s attempt / 30s total). Defect 2 made the 10s attempt timeout fire mid-answer; retries re-ran the agent (cost + load feedback storm), and the user-visible "00:02:00" failure was the outer 120s total giving up.
4. **Transport failures rode the Wave-1 demo placeholder into production.** `WizardStreamingClient` converted send failures into the hardcoded "Hello world!" demo stream — uncited, confidence 1.0 — rendered as if the Wizard had answered. Users saw garbage, operators saw green, and the day's canary failures read as flakes.

Once transport was healthy, a fifth defect surfaced: **cache-hit answers raced `MudHidden`'s per-instance async JS breakpoint resolution.** A cache hit emits 1 TextDelta + Final in ~1s; `CitationStrip`'s MudHidden wrappers hadn't resolved their breakpoints yet, so citations were present on the wire (proven by raw SSE capture) but absent from the DOM. Measured live: 0 links at +2s, 2 links at +12s. Single-ask canaries never caught it — a single ask is always a cache miss.

**Resolution:** PR #360 — `minReplicas: 1` (stack-deployed 13:27Z). PR #363 — (a) `StartAsync` + an SSE comment preamble flushes headers at accept, restoring attempt-timeout semantics to "headers only"; (b) `RemoveAllResilienceHandlers()` on the streaming client + a retry-disabled pipeline (15s attempt / 30s total) + `HttpClient.Timeout = Infinite`; (c) transport failures now propagate — the demo placeholder is confined to an explicit 503 path. PR #365 — `CitationStrip` breakpoints via CSS media query (960px) instead of MudHidden; a repeat-ask E2E (`AskFlow_RepeatedQuestion_CachedAnswerStillRendersCitations`) pins the cache-hit chunk shape. Verified post-deploy: repeat-ask canary green against the live site (2026-06-11).

**Rule adopted (2026-06-11): fallbacks must not hide failures.** Defect 4 is the canonical violation: a fallback that fabricates success destroys the signal that something is broken, and the underlying defect compounds. Any degraded path must (a) be visibly degraded to the user, (b) never present synthetic/placeholder content as real output, (c) log + meter the underlying failure. Codified as invariant #17 in `.claude/INVARIANTS.md` and a 🔴 check in `/local-review` (error-handling category). A same-day audit of every fallback path found 2 violations (#366 — landing page renders static fallback as live data, which masked #351 for days; #367 — the demo stream itself), 6 review items (#368), and 9 compliant paths (`SearchCorpusTool`'s degradation pattern is the model citizen).

**Lessons:** (1) SSE + standard HTTP resilience is a category error unless headers flush early and retries are off — and retrying a non-idempotent LLM call storms. (2) `ConfigureHttpClientDefaults` resilience stacks on top of per-client handlers; opting out requires `RemoveAllResilienceHandlers`, not different per-client settings. (3) Anything that must be visible at first paint after a fast render needs CSS media queries, not MudHidden (async JS breakpoint per instance). (4) The cache-hit chunk shape (1 TextDelta + Final, ~1s) is a distinct test surface that single-ask canaries never exercise. (5) A masking fallback converts every downstream defect into "flaky" noise — see the rule above.

**Revisit when:** #361 lands a sub-minute boot (revisit `minReplicas`), the resilience packages stabilize `RemoveAllResilienceHandlers` (drop the EXTEXP0001 pragma), or audit remediation (#366–#368) completes.

**Related:** PR #360, PR #363, PR #365; issues #361, #364 (closed by #365), #366, #367, #368; invariant #17 (`.claude/INVARIANTS.md`); ADR-0026 (streaming contract).

## 2026-06-11 — Docs-only PRs vs. required checks: fully path-filtered workflows report nothing; no-op companions added

**Decision:** Required branch-protection checks whose workflows are path-filtered get a no-op companion workflow with the **same `name:` key and same job name**, triggered on the **inverse path set**, succeeding immediately ([ci-docs-noop.yml](../.github/workflows/ci-docs-noop.yml), [codeql-docs-noop.yml](../.github/workflows/codeql-docs-noop.yml)). The four path lists (two `paths-ignore`, two inverse `paths`) are kept in lockstep — the rule is pinned in comments in all four files.

**What forced it:** PR #355, the first docs-only PR after #346 added the path filters, sat `BLOCKED` indefinitely. A workflow that never triggers produces **no check run at all** — GitHub does not synthesize a "skipped" run, so the required check stays in "Expected" forever. The carve-out claimed in the previous ci.yml/codeql.yml comments does not exist for fully filtered workflows (this entry corrects #346's assumption). Compounding accident: the required `Analyze (csharp)` check was being satisfied by the **Code Quality preview's** dynamic run, which emits an identically-named check with no path filters — with that preview slated for disable (entry below, issue #356), docs PRs were about to lose even the accidental rescue.

**Alternatives considered:** (a) Remove the path filters — burns a full build+test lane (~7 min) plus a CodeQL analysis (~3 min) on every docs PR; rejected, the filters exist for good reason. (b) Job-level early-exit guard inside ci.yml (always trigger, detect docs-only, skip steps) — single-workflow cleanliness but still spins a runner per docs PR and complicates the real lane; the companion pattern is GitHub's documented workaround ("Troubleshooting required status checks § Handling skipped but required checks") and keeps the real workflows untouched. (c) Keep relying on the Code Quality preview's name collision — an undocumented accident, rejected.

**Mixed-PR semantics:** a PR touching code + docs triggers both workflows; branch protection follows the latest-completed run with that check name, which is the real build (minutes) finishing after the no-op (seconds). Drift self-polices: ignore-list-grows-first produces a loudly stuck PR; noop-paths-grow-first still runs the real lane.

**Revisit when:** GitHub ships native handling for skipped-but-required checks, or the repo moves to rulesets/merge queue with different check semantics.

**Related:** PR #358 (fix), PR #355 (the stuck PR — its merge 3 minutes after the fix landed is the end-to-end proof), PR #346 (introduced the filters + the wrong assumption), issue #356.

## 2026-06-11 — CSP posture: strict `script-src` via hashes, MudBlazor `style-src` concession, Mermaid pinned + SRI, staged enforcement

**Decision:** The edge-injected CSP ([infra/cloudflare/headers.tf](../infra/cloudflare/headers.tf)) is tuned to **zero violations** against the real app and will be promoted from `Content-Security-Policy-Report-Only` to enforced `Content-Security-Policy` (+ `upgrade-insecure-requests`) after a clean soak (CLOUDFLARE_PRELAUNCH_CHECKLIST §7.2, tracked in issue #356). Locked directive posture:

- `script-src 'self'` + two SHA-256 hashes (theme/motion FOUC bootstrap in App.razor; `mermaid.initialize()` in About.razor) + the exact version-pinned Mermaid URL. **Never** `'unsafe-inline'`/`'unsafe-eval'`/`'unsafe-hashes'` — this is the XSS-load-bearing directive.
- `style-src 'self' 'unsafe-inline'` — the documented posture for MudBlazor (44 dynamic inline style attributes on the landing page alone; not hashable). Microsoft's Blazor CSP guidance endorses it verbatim; inline-style injection is a far weaker vector than script.
- `object-src 'none'` (per every Microsoft-recommended Blazor policy), `connect-src 'self' wss://pinwiz.ai` (SignalR circuit on engines that don't extend `'self'` to WebSocket schemes; host-scoped, never blanket `wss:`).
- Mermaid is pinned (`@11.15.0`) with SRI `integrity` + `crossorigin="anonymous"` — previously loaded **unpinned** (silent major-version upgrades in prod) with no tamper protection.
- `CspPolicySyncTests` (PinballWizard.Web.Tests) pins the cross-file contract: recomputes the inline-script hashes from the .razor sources, asserts headers.tf carries them and the pinned URL + full SRI literal, asserts `script-src` never regains `unsafe-*`, and closes the set of files allowed to carry inline scripts.

**Why:** the original `'self'`-everything Report-Only policy logged ~48 violations per page load into DevTools — permanent Issues-panel noise for the exact audience (technical evaluators, prospective clients) this showcase serves — and could never be promoted because MudBlazor can't satisfy `style-src 'self'`. Measured 48 → 0 via simulation (the edge policy injected onto the deployed app via Playwright route interception, `securitypolicyviolation` capture, all public routes).

**Alternatives considered:** (a) Status quo eternal Report-Only — noise + zero protection, rejected. (b) Enforce as-written — breaks all MudBlazor styling, rejected. (c) Drop the CSP — a security-literate evaluator inspecting headers sees nothing deliberate; rejected, the enforced policy is itself a showcase artifact. (d) **Nonces** — structurally impossible here: the header is a static Cloudflare Transform Rule that cannot mint per-request values; hashes are the static-header equivalent (nonces become viable only if CSP emission ever moves to the origin or a Worker). (e) **Self-hosting Mermaid** for consistency with the self-hosted-fonts privacy precedent (`SelfHostedFontsTests` — third-party CDN loads leak visitor IPs) — attempted and deferred: the 3.3 MB minified bundle false-positives the sanitization scan's PEM private-key-header rule — a loose wildcard that fires when its three marker words appear anywhere in order on a line, trivially true across megabyte-long minified lines (and, fittingly, true of this entry's first draft, which quoted the pattern verbatim and failed its own scan); self-hosting requires first narrowing that rule to the canonical dashed PEM header form, a security-gate change that needs its own deliberate review (parked in #356). SRI + the version pin close the *integrity* risk meanwhile; the residual is the privacy-consistency question on /about only.

**Maintenance contract:** bumping Mermaid = update the URL + `integrity` in About.razor, the URL in headers.tf, and the SRI literal in `CspPolicySyncTests` (the test failure message walks through it). Editing either inline script (even whitespace) changes its hash — the test fails with the recompute instruction.

**Revisit when:** the §7.2 promotion lands (then add `upgrade-insecure-requests` and decide on a `report-to` receiver); the JS surface grows beyond one bootstrap + Mermaid; or CSP emission moves origin-side (reopens nonces and per-environment policies).

**Related:** PR #357 (tuning + contract test), issue #356 (measurement, recommendation, deferred items), PRs #348/#349 (prior CSP noise fixes), the 2026-06-11 Code Quality entry below (the preview's dynamic run also figured in the required-checks incident above).

## 2026-06-11 — Two CodeQL runs per PR are different validations: `codeql.yml` (required gate) vs. the GitHub Code Quality preview (optional)

**Decision:** [`codeql.yml`](../.github/workflows/codeql.yml) remains the repo's required static-analysis gate; the per-PR "Code Quality: PR #N" run is GitHub's **Code Quality preview** — a separate, settings-driven validation that is *not* load-bearing for branch protection and may be disabled in Settings → Code security → Code quality without losing any required check.

**The two pipelines, confirmed from this repo's runs (PR #354 head SHA) and GitHub docs:**

| Aspect | `codeql.yml` (advanced setup) | Code Quality preview |
| --- | --- | --- |
| Trigger | Workflow in-repo; PR/push to main + weekly cron | Dynamic workflow `dynamic/github-code-scanning/codeql` (`event: dynamic`), created by the Settings toggle — no file in the repo, no API |
| Languages | csharp only | csharp **and** javascript-typescript (auto-detected) |
| Queries | `security-and-quality` suite | Curated CodeQL *quality* rules (maintainability + reliability) per [GitHub's preview announcement](https://github.blog/changelog/2025-10-28-github-code-quality-in-public-preview/) |
| Config | Honors [`.github/codeql/codeql-config.yml`](../.github/codeql/codeql-config.yml) (5 documented query-filter suppressions, `obj`/`bin` paths-ignore, locked-mode restore) | Ignores the repo config file; configurable only via the Settings page (language checkboxes, runner type) per [Enabling GitHub Code Quality](https://docs.github.com/en/code-security/how-tos/maintain-quality-code/enable-code-quality) |
| Results store | Code scanning alerts (Security tab) — verified via `code-scanning/analyses` API: the **only** uploader, category `.github/workflows/codeql.yml:analyze` | Separate quality-findings store: "Security and quality" tab dashboard + `github-code-quality[bot]` PR comments with Copilot Autofix per [CodeQL-powered analysis for Code Quality](https://docs.github.com/en/code-security/reference/code-quality/codeql-detection) |
| Checks | `Analyze (csharp)` — **required** by branch protection | `Code Quality: PR #N` run (jobs `Analyze (csharp)` + `Analyze (javascript-typescript)`) + a `CodeQL` check from the `github-advanced-security` app — not required |

So the per-PR pair of `Analyze (csharp)` runs is a *partial* duplicate: the C# analysis is genuinely run twice, but under different query sets, different configs, and feeding different result stores.

**Alternatives considered:** (a) Keep both — costs a duplicate C# analysis plus a JS analysis per PR in Actions minutes, and the preview produced **zero** findings across PRs #350–#354 while emitting non-retryable failure noise during the 2026-06-10 GitHub API incident; its only coverage delta is JS/TS analysis of a single 36-line `wwwroot/app.js`. (b) Drop `codeql.yml` and rely on the preview — rejected: loses the required-check gate, the weekly scheduled scan, locked-mode restore, and the curated false-positive suppressions (the preview cannot read the repo config). (c) Add `javascript-typescript` to `codeql.yml`'s matrix to preserve the preview's only coverage delta before disabling — deferred: not worth a per-PR job for one trivial interop file; reconsider if the JS surface grows.

**Rationale:** The preview's quality angle is already substantially covered for C# by the `security-and-quality` suite in the required workflow, and where the preview goes beyond it (quality scores, Autofix-on-quality-findings, org dashboard), it has surfaced nothing on this codebase while it cannot honor our documented suppressions. While the feature is in preview (unbilled, evolving, no API), it is informational-only here.

**Revisit when:** Code Quality reaches GA (billing + config model will change), the JS/TS surface grows beyond trivial Blazor interop, or the feature gains support for repo-level config / suppressions — then re-evaluate enabling it as the quality surface alongside the security-focused required gate.

**Related:** PR #346 (pipeline optimization, where the duplicate run was first flagged), handoff 2026-06-11 (`thoughts/.../AB-259/2026-06-11_02-52-20`).

## 2026-06-10 — INCIDENT: Blazor circuits dead at >1 replica — missing documented ACA hosting config (session affinity + shared Data Protection)

**Incident:** Reported by operator ~17:40Z ("no answers and the suggested questions don't show as links"). The deployed wizard app rendered as a static prerender: question input visible but the Ask button never enabled, nothing interactive. Container logs showed `AntiforgeryValidationException: The key {…} was not found in the key ring` at the exact report time, plus the startup warning `EphemeralXmlRepository` (Data Protection keys held in-memory per process). The app was at 2 replicas (scale 1–3) with no ingress session affinity: page HTML served by replica A carried tokens encrypted with A's ephemeral key ring; the circuit handshake load-balanced to replica B, which couldn't decrypt them — every circuit died. The site had worked earlier in the day only because it happened to be at 1 replica.

**Root cause:** ADR-0026's Blazor Web App decision was deployed without Microsoft's documented Container Apps hosting requirements — BOTH ingress session affinity ("you must enable sticky sessions", learn.microsoft.com/azure/container-apps/dotnet-overview § Configure Blazor Server) AND a shared Data Protection key ring (blob-persisted, Key Vault–wrapped; learn.microsoft.com/aspnet/core/blazor/host-and-deploy/server § Azure Container Apps). Azure SignalR Service does not remove the affinity need for Blazor (`ServerStickyMode.Required`).

**Resolution:** PR #344 — `stickySessions.affinity: 'sticky'` on the wizard ingress; `dataprotection` blob container; `pinwiz-dataprotection` RSA-2048 KV key (wrap/unwrap only); UAMI role assignments (Blob Data Contributor scoped to the container; KV Crypto Service Encryption User); `AZURE_CLIENT_ID` + `DataProtection__*` env vars; gated `AddDataProtection()` wiring in Web `Program.cs` (local dev keeps the ephemeral ring). Deployed via the `pinwiz-shared-dev` Deployment Stack 18:29Z. App-code health was proven locally end-to-end before the infra fix (circuit, ask flow, disambiguation, citations) — isolating the defect to hosting config. Time to recovery: ~50 min from report to stack deploy.

**Alerting gap (follow-up):** No alert fired — the 5xx alert is blind to a site that serves 200s with a dead circuit (and to the citation outage below, which served well-formed refusals). Follow-up: an end-to-end canary (scripted ask through the SSE endpoint asserting a cited answer) per runbook 01 § Post-incident item 2.

**Revisit when:** scaling posture changes (e.g., Azure SignalR Service if concurrent-circuit count outgrows single-replica affinity), or ACA ships affinity-aware autoscaling guidance.

**Related:** PR #344, ADR-0026 follow-up 2026-06-10, runbook 01-incident-response.

## 2026-06-10 — INCIDENT: 100% answer refusals — citation extractor never matched live camelCase JSON; URL migration removed its accidental fallback

**Incident:** From ~13:30Z–16:00Z every question on the deployed site returned the canned out-of-scope refusal. Committed eval evidence: citation_precision 0.967 at 13:29Z (`wizard.20260610T132948Z.json`) → 0.111 with 30/30 refusals (`wizard.20260610T150629Z.json`).

**Root cause (two compounding defects):** (1) `ToolTraceCitationExtractor`'s JsonElement arms probed PascalCase property names (`"Hits"`, `"OpdbId"`) but `AIFunctionFactory` serializes tool results camelCase — the structured arms never fired in production; unit tests stayed green because fixtures were serialized PascalCase (fixture-shape drift). (2) All citations therefore rode the regex fallback matching only `opdb.org/machines/{id}` URLs embedded in raw tool-result JSON; the same-day opdbSourceUrl migration to `/search?q={id}` (PR #339 + `tools/migrate-opdb-source-urls.csx`) removed every matchable URL → zero citations → every answer fell below the 0.65 confidence threshold → blanket refusals.

**Resolution:** PR #341 — case-insensitive property probing + deserialization (`JsonSerializerDefaults.Web`); URL regex accepts both schemes; binding `JsonException` degrades to the regex fallback instead of propagating; `RegexLegacyCitationExtractor` comparator widened identically; 6 live-shape regression tests (5 fail on unfixed code). Recovery proven by eval: precision 0.111 → 0.933 (`wizard.20260610T153932Z.json`), then 0.967 on the follow-up run. Time to recovery: ~2.5 h (detection lagged ~80 min because the last eval predated the data migration).

**Lessons:** (1) Serialization-boundary test fixtures must use the exact runtime serializer, not test-side defaults. (2) A live-data migration is a deploy-equivalent event — run the eval immediately after it, not only after code merges. (3) A confidence gate without citation-extraction observability turns an extraction bug into a silent total outage (see alerting-gap follow-up above).

**Revisit when:** Citation extraction moves to a structured tool-result contract (e.g., typed results once the SDK preserves types), making the regex fallback deletable.

**Related:** PR #341, ADR-0022 follow-up 2026-06-10, ADR-0016 follow-up 2026-06-10 (three-state refusal metric, PR #342), PR #339.

## 2026-06-10 — OPDB group-title lookups get a persistent on-disk cache (positive + negative)

**Decision:** `OpdbClient.GetMachineGroupTitleAsync` consults an on-disk cache (`OpdbOptions.GroupTitleCachePath`, default `data/cache/opdb-group-titles.json`; `GroupTitleCacheTtlSeconds`, default 14 days, whole-file mtime TTL) before issuing the polite `GET /api/machines/{groupSegment}`. Confirmed 404 / non-group results are cached as explicit nulls (negative entries). Transient HTTP failures are never cached — exceptions propagate before the cache write. The shared `WriteCacheFile` helper also fixes the export-cache persist failure (OneDrive marks synced files read-only; `MOVEFILE_REPLACE_EXISTING` fails with `ERROR_ACCESS_DENIED` — now clear-ReadOnly → Delete → Move).

**Alternatives considered:** Per-entry TTLs (over-engineering — franchise names are effectively immutable; whole-file mtime matches the export-cache precedent). End-of-run persistence (rejected: a crash mid-run loses every fetched title; per-entry persistence is cheap because new entries are rare in steady state). Lowering the 10s OPDB politeness delay (rejected outright — the delay is the documented 2026-05-04 decision; the correct lever is fewer requests, not faster ones).

**Rationale:** The in-memory per-run cache meant every fresh sync re-fetched all group segments: ~1,200 requests at 10s each ≈ 3.5h observed live 2026-06-10 before the run was abandoned (~12h projected). Steady-state syncs now make near-zero group-title requests — strictly more polite to OPDB.

**Revisit when:** The weekly ACA sync job lands (incremental sync — diffing export `updated_at` vs Cosmos `lastSyncedUtc` — is the remaining optimization), or OPDB starts emitting cache-validation headers on `/api/machines/{id}`.

**Related:** PR #332, ADR-0029 (follow-up 2026-06-10), decision-log 2026-05-04 (export cache + politeness override).

## 2026-05-26 — Phase 4.5 H5 eval baseline; ADR-0024 Cohere Rerank gate triggered

**Decision:** H5 eval run on the Phase 4.5 full corpus (30 questions, 7 curated machines) returned `citation_precision=0.478`, triggering the ADR-0024 cross-encoder gate (`< 0.50`). Proceeding with a W4 fix-up PR to wire `CohereRerankReranker` (Cohere Rerank-v3 via Foundry connection). Full H5 metrics: `citation_recall=0.500`, `citation_coverage=0.533`, `subagent_accuracy=0.167`, `refusal_correctness=0.933`. Results file: `data/eval/results/wizard.20260526T143313Z.json`.

**Alternatives considered:**

- **Treat 0.478 as close enough, skip Cohere.** Rejected — the gate threshold was set deliberately at 0.50; the ADR's purpose is to make this decision data-driven, not opinion-driven.
- **Re-evaluate the gate threshold.** Rejected — threshold was set before H5 numbers existed; raising it after the fact undermines the value of the gate.

**Rationale:** The ADR-0024 gate is the mechanism for deciding whether the cross-encoder layer is needed. H5 triggered it cleanly. The locked implementation path (Cohere Rerank-v3 via Foundry, `ICrossEncoderReranker` abstraction) is ready to execute.

**Revisit when:** H5b eval after Cohere integration lands. If `citation_precision ≥ 0.50` post-rerank, Phase 4.5 closes; if not, investigate retrieval-side root causes before proceeding to Phase 5.

**Related:** ADR-0024, `data/eval/results/wizard.20260526T143313Z.json`

## 2026-05-26 — Phase 4.5 deferred items logged at phase close

**Decision:** Three items deferred out of Phase 4.5 scope:

1. **Flyers (208 docs in corpus)** — chunking strategy TBD. Flyers are visually dense, short-text, promotional layouts. PdfPig extracts minimal text; ADI layout mode extracts more but produces noisy chunks. Decision deferred until a Phase 5+ eval question set explicitly targets flyer content to justify the investment.
2. **Other bucket (98 docs)** — classification TBD. `document_type=Other` items are a mixed bag (press kits, show programs, promotional PDFs). Require manual review to determine if they belong in separate document types or should be chunked with their closest sibling type.
3. **`NullTokenUsageReader` real implementation** — pending upstream fix in `azure-sdk-for-net#2688`. The `NullTokenUsageReader` stub in `Infrastructure/Integrations/Foundry/` is intentional; the real implementation cannot be wired until the SDK surfaces token usage in the Responses API response surface. Revisit when azure-sdk-for-net ships the fix.

**Alternatives considered:** N/A — these are explicit scope deferrals, not trade-off decisions.

**Rationale:** Phase 4.5's demonstrable artifact is manuals in the index with bounded long-tail failure rate and a meaningful H5 lift from H4. Flyers and Other documents expand scope without improving the core citation story; `NullTokenUsageReader` is blocked on an upstream SDK gap.

**Revisit when:** Phase 5+ eval questions target flyer content (flyers); manual review batch is scheduled (Other); azure-sdk-for-net#2688 is resolved (`NullTokenUsageReader`).

**Related:** `docs/superpowers/plans/2026-05-21-phase45-corpus-expansion.md` Task 16

## 2026-05-22 — Azure Document Intelligence instance provisioned for Phase 4.5 W1 OCR fallback

**Decision:** Provisioned a single Azure Document Intelligence resource (`pinwiz-docint-dev-buutj`, S0 tier, East US 2) as the ADI OCR fallback for `AzureDocumentIntelligenceExtractor`. The RAG indexer managed identity (`ad9ea109-c33a-4f53-88df-e1397922de42`) was granted `Cognitive Services User` on the resource. The endpoint (`https://pinwiz-docint-dev-buutj.cognitiveservices.azure.com/`) is injected via env var `DocumentIntelligence__Endpoint` on the `pinwiz-ca-ragindexer-dev` Container App. `FallbackDocumentTextExtractor` activates the ADI extractor only when this endpoint is configured; local dev without the env var stays PdfPig-only.

**Alternatives considered:**

- **Computer Vision OCR (Read API).** Rejected — superseded by Document Intelligence for document-class inputs; Document Intelligence's Read model handles multi-page PDFs with layout awareness that CV OCR lacks.
- **Form Recognizer (legacy).** Rejected — Document Intelligence is its successor; Form Recognizer is in maintenance mode.
- **Shared/multi-purpose DI resource.** Deferred — at this scale (one indexer, low-volume ingestion) a dedicated resource is simpler to reason about and avoids quota contention with other workloads.

**Rationale:** ADI S0 tier is pay-per-use at $1.50/1,000 pages on the Read model, consistent with the project's $300–$400/mo cost envelope. The `FallbackDocumentTextExtractor` decorator keeps ADI as a fallback-only code path; PdfPig handles the majority of PDFs that are digitally created (text layer present). ADI fires only when PdfPig returns `ExtractionStatus.OcrRequired` (no extractable text). The 404/cancellation/empty-content failure modes are covered by `AzureDocumentIntelligenceExtractorTests` (PR #266 + #267).

**Revisit when:**

- Monthly ADI page cost exceeds ~$30 (signals volume large enough to re-evaluate tier or batch-processing strategy).
- Document Intelligence introduces a serverless/consumption-pricing option that better matches the bursty-ingestion pattern.
- Phase 6 multi-region expansion requires a second DI resource or cross-region replication.

**Related:** PR #266 (ADI extractor + fallback decorator), PR #267 (pre-W2 quality fixes including `AzureDocumentIntelligenceExtractorTests`), ADR-0025 § 8 (Cosmos metrics — ADI calls go through `CosmosMetricsHelper` equivalently for its own instrumentation).

## 2026-05-09 — eBay excluded from community_resources.v1.json (CI URL-liveness false positive)

**Decision:** PR-R3's `data/seeds/community_resources.v1.json` does NOT include eBay even though it's a major used-pinball marketplace. The marketplace category meets its ≥3 plurality minimum (per ADR-0026 § 6 + `feedback_destination_plurality.md`) via Facebook Marketplace + Mr. Pinball + Pinside Market.

**Alternatives considered:**

- **Include eBay; whitelist 500 in the workflow's BLOCKED list.** Rejected — 500 in the BLOCKED list means a legitimate community-resource outage would silently pass CI. The workflow's job is to catch real failures; eBay is the false-positive case, not the canonical-broken case.
- **Include eBay; skip eBay in the workflow probe loop with an exception.** Rejected — special-casing one URL hides the problem instead of solving it. If we have to skip a URL, we shouldn't be linking to it as a "verified live community resource."
- **Use a different eBay URL pattern (search endpoint vs. category endpoint vs. saved-search RSS).** Rejected — eBay's bot-detection is CDN-level; every public eBay URL gets the same 5xx-to-non-browsers treatment. Verified by manual probing during R3 fix.
- **Render eBay only at frontend-time (not in the seed).** Held as a future option — see "Revisit when" below. The frontend would link directly without the seed-file probe owning validation.

**Rationale:** eBay's CDN returns HTTP 500 to non-browser User-Agents (CI runners, curl HEAD/GET). The actual eBay listing pages are live and working in browsers. The R3 workflow's BLOCKED list (statuses treated as "live but bot-protected, log a warning, don't fail") is `403 / 405 / 429 / 999` — these are unambiguous bot-detection signals. **500 is deliberately NOT in that list** because a legitimate 500 from a community resource SHOULD fail the workflow so we catch real outages. Adding 500 to BLOCKED would mask real failures.

The marketplace category retains plural coverage (Facebook + Mr. Pinball + Pinside Market) — Pinside in particular aggregates listings from eBay for users who want that specific channel.

**Revisit when:**

- eBay changes CDN behavior to allow HEAD/GET from non-browser User-Agents (annual spot-check).
- A frontend-side approach lands that doesn't require the seed-file to own validation (e.g., a per-render "marketplace links" component that probes opportunistically with browser-like fetch — separate from CI).
- A user-research signal indicates eBay is the missing surface they expected to see (we have other ways to send users to eBay, but if specific eBay coverage becomes important, revisit).

**Related:** PR #165, ADR-0026, `feedback_destination_plurality.md`, `feedback_avoid_appearance_of_favoritism.md`. Memory: `project_ebay_excluded_from_community_resources.md`.

## 2026-05-09 — Title→OPDB-ID materialized view: dual-write + parallel-arrays + Cosmos-id-safe normalization

**Decision:** PR 5 of the Cosmos for User Delight track ships a `machine_title_lookups` Cosmos container as the materialized view backing `MachineGroundingTool.GetMachineByTitleAsync`'s point-read path per [ADR-0025 § 4](adr/0025-cosmos-for-user-delight.md). Three sub-decisions sit below the ADR threshold and are recorded here:

1. **Dual-write from `OpdbSyncService` (single writer + session consistency).** Maintenance pattern is a per-machine dual-write inside the existing pass-1 loop: machine row first, then the lookup row. Failure of the lookup write is caught + logged at warning, NOT propagated — the machine row has already landed and the cross-partition fallback in `MachineGroundingTool` keeps queries working until the next sync repopulates the row. Rename detection captures `existing.Title` BEFORE `OpdbMachineMapper.MergeOpdbFieldsInto` runs, then if the normalized prior title differs from the new normalized title, removes the entry from the OLD lookup row (deleting the row when it becomes empty).
2. **Parallel-arrays `opdbIds: string[]` + `manufacturers: string[]` over a list of nested objects.** Title collisions (two machines that normalize to the same title — `Godzilla` is the canonical case) get a single lookup row with parallel index-aligned arrays. The entity exposes `UpsertEntry(opdbId, manufacturer)` / `RemoveEntry(opdbId)` helpers so callers can't accidentally desynchronize the two lists. Trade-off: parallel arrays are lighter on the Cosmos wire format (smaller doc per machine), but the C# API needs the helpers to keep the invariant. The list-of-records alternative would be cleaner C# but spends extra bytes per row and requires a custom `JsonConverter` to avoid leaking the .NET shape into the wire format.
3. **Cosmos-id-safe normalization: lowercase + trim + escape `/`, `\`, `?`, `#` to `_`.** Cosmos document ids reject those four characters; partition-key values accept them but we want `id == partitionKey value` so reads are pure point-lookups with no secondary index. The substitution is one-way (no reverse transform) — the canonical title lives on `Machine.Title`; the lookup never needs to reconstruct the original. Two distinct titles that collide under the substitution (e.g., `AC/DC` vs `AC_DC`) are stored as two entries on the same row, which is the same collision shape the schema already supports for genuine same-title collisions.

**Alternatives considered:**

- **Change-Feed-driven projection from `machines` → `machine_title_lookups`** (instead of dual-write). Rejected for now: a single projection doesn't earn the Change Feed processor's complexity (lease container, hosted service, replay handling). When a 2nd `machines` materialized view lands (Phase 4.5+), switch to Change-Feed-driven so the writer doesn't need to know about every projection. Documented as a revisit trigger in ADR-0025 § 1.
- **Composite index on `c.title` in `machines`** (cheaper to ship; no second container). Rejected — still cross-partition, just faster cross-partition. ~10-20ms savings vs ~50-145ms with the lookup container. Doesn't earn the Wizard answer flow's latency budget.
- **Hash the title to derive the id** (sidesteps the forbidden-character question). Rejected — unreadable in Data Explorer; debugging a "why does title X resolve to row Y?" question requires re-running the hash. The substitution-based normalization keeps the row keys human-readable.
- **List of `MachineTitleLookupEntry` records** (cleaner C# than parallel arrays). Rejected per the rationale in sub-decision 2 above.

**Rationale:** Dual-write is the simplest pattern that works for a single writer; the more sophisticated Change-Feed-driven projection earns its keep when there are multiple projections to keep in sync, not for one. Parallel arrays match the wire format the plan specified and keep the doc size minimal. Cosmos-id-safe normalization is the smallest deviation from "lowercase + trim" that makes `id == partitionKey value` always valid.

**Revisit when:**

- A 2nd materialized view off `machines` lands → switch this projection AND the new one to Change-Feed-driven, with the W3-2 hosted service abstraction handling lease ownership.
- A title-collision case surfaces in eval data with semantic ambiguity the agent can't resolve from the first entry → consider returning up-to-N matches from `MachineGroundingTool` instead of just the first.
- A future analytic query against the lookup container by raw title (not normalized) → the one-way substitution would prevent reconstruction; would need to add a `rawTitles: string[]` field. Defer until such a query actually emerges.

**Related:** ADR-0025 § 4, [`MachineTitleLookup`](../src/PinballWizard.Core/Domain/MachineTitleLookup.cs), [`MachineTitleLookupRepository`](../src/PinballWizard.Infrastructure/Persistence/Cosmos/MachineTitleLookupRepository.cs), [`OpdbSyncService.UpdateTitleLookupAsync`](../src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs)

## 2026-05-08 — Latest .NET / C# 14 features audit; collection-expression sweep

**Decision:** Audit the codebase for adoption of the latest stable .NET 10 / C# 14 language and library features. Two material findings, one obvious-win modernization landed, the rest of the codebase already on the current idiom.

**Findings:**

1. **Already current** (no change needed):
   - `<TargetFramework>net10.0</TargetFramework>` solution-wide via [`Directory.Build.props:13`](Directory.Build.props#L13).
   - `<LangVersion>latest</LangVersion>` opts every project into C# 14 ([`Directory.Build.props:16`](Directory.Build.props#L16)).
   - SDK pinned to `10.0.100` with `rollForward: latestFeature` in [`global.json`](../global.json) — picks up minor SDK bumps automatically.
   - **File-scoped namespaces** everywhere — zero `namespace X { }` block forms across `src/` and `tests/` (verified by grep).
   - **Records used liberally** — every immutable data carrier uses `public sealed record` (positional or with-init). 20+ types audited.
   - **No `string.Format` calls** — every formatted string uses interpolation (`$"..."`).
   - **No sync-over-async smells** — zero `.Result` / `.GetAwaiter().GetResult()` in production code paths.
   - **No `Dictionary<,>()` empty-initializer sites** that would benefit from collection expressions (the dictionaries that exist are intentionally constructed with capacity hints, custom comparers, or item lists that don't target-type cleanly to `[]`).
   - **`async`/`await` patterns** consistently use `ConfigureAwait(false)` in Infrastructure, `await using` for `IAsyncDisposable`, and `CancellationToken` plumbed through.

2. **Modernized in this PR:**
   - **`Array.Empty<T>()` → `[]`** — 32 sites across 14 files. Collection expressions (C# 12+) target-type to the same allocation-free empty collection that `Array.Empty<T>()` produced, but read more contemporary and compose better with future collection-expression patterns. Applies in record positional args, method returns, `??` null-coalesce defaults, and ternary expressions.
   - **CA1859 fix surfaced by the sweep** — two private static helpers in Infrastructure (`EvaluationHarness.ExtractCitationIds`, `PdfPigDocumentTextExtractor.ExtractOutline`) returned `IReadOnlyList<T>` but only ever produced concrete `List<T>` internally. The analyzer was previously masked by the mixed-type `Array.Empty<T>()` (`T[]`) early-return; replacing with `[]` (target-typed to `List<T>` matching the other branch) made CA1859 fire. Promoted the return types to `List<T>` per the analyzer's recommendation. No public API impact (both are private).

**Deferred (not modernized in this PR; documented for future review):**

- **`new List<T>()` empty-initializer sites (~51)** — could become `[]` target-typed to `List<T>` in C# 12+. Mostly benign, but per-site target-type analysis is needed (e.g., `var x = new List<T>();` doesn't target-type — `var x = []` is invalid; would need `List<T> x = []`). Stylistic upgrade with low per-site value; defer to a future style sweep if/when the team adopts a collection-expression-first conventions doc.
- **`using (var x = ...) { ... }` statement form (4 sites in `FileDownloader.cs` / `OpdbClient.cs` / `PinballMapClient.cs`)** — could become `using var x = ...;` declarations (C# 8) where the scope ends at the enclosing method block. The 4 sites are inside `async`/`await using` blocks where the scope-management is intentional (the resource needs to dispose mid-method, not at method end). Keep as-is.
- **C# 14 `field` keyword for backing-field-only auto-property bodies** — searched; no current properties have explicit backing fields that the `field` keyword would simplify. Adopt if a future property needs `set`/`init` validation that requires the backing-field reference.
- **Primary constructors on classes with multiple readonly fields** — possible refactor for `AiRouter` / `ConfidenceCalculator` / similar. Stylistic; defer until the showcase `customer-facing read-clean` posture surfaces a specific class where the primary-ctor form materially improves readability vs. the explicit ctor with `ArgumentNullException.ThrowIfNull` guards.
- **Required members on options classes** — `AiSearchOptions.Endpoint`, `AiFoundryOptions.ProjectEndpoint` could be `required` to fail-fast at construction rather than `[Url]` + `Validate(...)` at startup. The current pattern (data annotations + `ValidateOnStart`) gives clearer diagnostics than the `required` violation message. Keep as-is.

**Rationale:** The codebase was already drafted against C# 14 / .NET 10 from project inception (Phase 0 set the LangVersion=latest + net10.0 baseline). The remaining modernization opportunities are stylistic refinements rather than missing-feature gaps. The `Array.Empty<T>()` → `[]` sweep is the one universally-applicable upgrade — every site target-types correctly, every site reads better, no behaviour change. The CA1859 fix is a correctness improvement that would have been surfaced by any code review against current analyzer rules. Deferred items are tracked here so a future reader knows what was considered and consciously deferred vs. simply overlooked.

**Verification:**

- `dotnet build PinballWizard.slnx` — 0 warnings, 0 errors after both the sweep and the CA1859 fixes.
- `dotnet test` — 807/807 passing; zero behaviour change (collection expressions and `Array.Empty<T>()` produce equivalent empty-collection allocations under target-typing).
- Tests + production code identically modernized so no asymmetric idiom drift between layers.

**Revisit when:** A new C# version (15+) ships with materially-relevant features (e.g., the C# 14 `extensions` blocks could simplify some scraper-helper namespaces if the language committee finalizes that surface), or when a code review on a new PR surfaces a specific old-idiom site that would read better under a current feature. The deferred list above is the natural starting point for any future "modernization round 2" PR.

**Related:** PR (this one) — Array.Empty sweep + CA1859 fixes; [`Directory.Build.props`](../Directory.Build.props) (LangVersion + TargetFramework baseline); [`global.json`](../global.json) (SDK pin).

## 2026-05-08 — Foundry stack on latest GA (verified) + provider-agnostic query embedding via IQueryEmbedder

**Decision:** Three architectural facts confirmed and one new abstraction locked, surfaced during PR review of Phase 4 W3-3 (the AI Search hybrid-retrieval query client):

1. **Foundry stack is on the latest GA, project-endpoint surface only (verification, no change).** PinballWizard's AI orchestration uses `Azure.AI.Projects` 2.0.1 (GA April 2026) + `Microsoft.Agents.AI.Foundry` 1.4.0 (GA), constructing agents via `AIProjectClient.AsAIAgent(...)`. **Hub-based projects are NOT used and never were** — `AiFoundryOptions.ProjectEndpoint` ([src/PinballWizard.Core/Configuration/AiFoundryOptions.cs:17-22](../src/PinballWizard.Core/Configuration/AiFoundryOptions.cs#L17-L22)) accepts only the `*.services.ai.azure.com/api/projects/<project>` shape; connection strings and hub URLs are explicitly out per [ADR-0014 § Decision lines 148-150](adr/0014-microsoft-foundry-orchestration.md#L148-L150).

2. **Chat / agent layer is already provider-agnostic via deployment-name indirection (verification, no change).** [`AiFoundryOptions.AgentModels`](../src/PinballWizard.Core/Configuration/AiFoundryOptions.cs#L41-L44) is a string-keyed deployment-name dictionary; [`FoundryAgentFactory.ConstructAgents:128-132`](../src/PinballWizard.Infrastructure/Integrations/Foundry/FoundryAgentFactory.cs#L128-L132) calls `projectClient.AsAIAgent(model: deploymentName, ...)` where `model` is an opaque deployment-name string. Foundry MaaS hosts Anthropic Claude (Sonnet/Opus), Mistral, Cohere, Meta Llama, and OpenAI on the same `chat.completions` consumer surface. Swapping the Wizard or any sub-agent to e.g. `claude-sonnet-4-5` is config-only: register the deployment in the Foundry portal, set `AgentModels:Wizard=claude-sonnet-4-5` in app config, and add a matching row to [`AiFoundryOptions.PricingTable`](../src/PinballWizard.Core/Configuration/AiFoundryOptions.cs#L83-L97) for cost attribution. **No code changes** to support multi-provider chat.

3. **Embedding layer is now provider-agnostic via `IQueryEmbedder` abstraction (NEW).** Phase 4 W3-3 introduces [`Application/Ai/Retrieval/IQueryEmbedder`](../src/PinballWizard.Application/Ai/Retrieval/IQueryEmbedder.cs) with default impl [`Infrastructure/Rag/Retrieval/AzureOpenAIQueryEmbedder`](../src/PinballWizard.Infrastructure/Rag/Retrieval/AzureOpenAIQueryEmbedder.cs) wrapping `OpenAI.Embeddings.EmbeddingClient`. `AiSearchRagRetriever` depends on the abstraction, not the SDK type. A future ADR can swap to Cohere Embed (Foundry-MaaS-hosted) or any other provider by registering an alternative `IQueryEmbedder` impl without touching the retriever or its tests. The vector dimensionality must match the AI Search index's `content_embedding` field (3072d under [ADR-0021](adr/0021-ai-search-index-schema.md)); a dimension-changing swap is a schema-breaking ADR-0021 v1→v2 cutover.

**Alternatives considered:**

- **Bind retriever directly to `EmbeddingClient`** (the original W3-3 design before user feedback at PR review). Rejected: locks the embedding layer to one SDK type with no abstraction seam. Future provider swap would require touching three files (retriever ctor + DI + live test) instead of one (new `IQueryEmbedder` impl + DI swap). Violates the project's "no quick fixes" + customer-facing-showcase posture.

- **Add a richer `IEmbeddingService` covering both query-side and document-side embedding** (anticipating W2-3 indexer territory). Rejected for now: speculative beyond W3-3's immediate consumer. When W2-3 lands, it can either share `IQueryEmbedder` (the same shape works for batched documents) or extend with a sibling abstraction; today's commit reserves the simpler surface for the actual consumer rather than designing for hypothetical use.

- **Defer the abstraction until a non-OpenAI embedding need is concrete.** Rejected: the cost of adding it now is ~30 LoC (one interface + one wrapper); the cost of bolting it on later is a refactor that touches the retriever's ctor + DI + tests + any other future consumers. The architectural-affordance argument is also material — the showcase code now visibly reads as "embedding is one of several swappable pieces" rather than "embedding is OpenAI."

**Rationale:** Multi-provider chat is already the default state thanks to Foundry MaaS mechanics — the `AgentModels` indirection makes provider choice a config concern. Multi-provider embedding requires a code seam; adding it now while the retriever surface is being shaped is cheap and matches the project's quality bar. The verification of (1) and (2) prevents future-Claude (and prospective customers reading the code) from re-litigating "are we on hub-or-project Foundry?" and "is the chat layer locked to OpenAI?" — questions surfaced at PR review and which deserve a permanent answer in the canonical decision record rather than living only in PR conversation.

**Verification evidence:**

- Foundry-latest: [`PinballWizard.Infrastructure.csproj:20`](../src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj#L20) (`Azure.AI.Projects` 2.0.1) + [line 29](../src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj#L29) (`Microsoft.Agents.AI.Foundry` 1.4.0).
- Project-endpoint-only: [`AiFoundryOptions.ProjectEndpointKey` constant + `[Url]` attribute](../src/PinballWizard.Core/Configuration/AiFoundryOptions.cs#L15-L22); [ADR-0014 line 148-150](adr/0014-microsoft-foundry-orchestration.md).
- Chat-provider-agnostic: [`FoundryAgentFactory.ConstructAgents:128-132,148-152`](../src/PinballWizard.Infrastructure/Integrations/Foundry/FoundryAgentFactory.cs) — `model:` is an opaque deployment-name string in every `AsAIAgent` call.
- Embedding-abstraction: [`AiSearchRagRetriever:28-46`](../src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs) depends on `IQueryEmbedder`, not `EmbeddingClient`.

**Revisit when:** A non-OpenAI embedding model (e.g. Cohere Embed) becomes a concrete requirement — at that point register a sibling `IQueryEmbedder` impl, expose the choice via an option (e.g. `AiSearchOptions.EmbeddingProvider`), and ensure the W2-3 indexer uses the same impl so retriever-side and indexer-side dimensionality stays aligned. Or if Foundry deprecates / re-shapes its project-endpoint surface (currently GA — unlikely in the Phase 4 horizon).

**Related:** PR (this one) — Phase 4 W3-3 AiSearchRagRetriever; [ADR-0014](adr/0014-microsoft-foundry-orchestration.md) (Foundry orchestration); [ADR-0015](adr/0015-cost-routing-and-semantic-cache.md) § Per-`AIAgent` model selection (deployment-name indirection); [ADR-0020](adr/0020-embedding-model.md) (text-embedding-3-large @ 3072d locked); [ADR-0021](adr/0021-ai-search-index-schema.md) § Schema (content_embedding 3072d).

## 2026-05-07 — Phase 3 H2 eval baseline captured; ConfidenceThreshold stays at ADR-0017's draft 0.65

**Decision:** The Phase 3 H2 hand-off (build-spec § Phase 3 § Operational hand-offs item 2) ran against deployed Foundry and produced a v1 baseline at `data/eval/results/wizard.20260507T162529Z.json`. Aggregate metrics: `citation_precision=0.133`, `citation_recall=0.133`, `subagent_accuracy=0.033`, `refusal_correctness=0.300`. **`AiFoundryOptions.ConfidenceThreshold` is NOT moved from [ADR-0017](adr/0017-confidence-threshold-refusal.md)'s draft value of 0.65.** ADR-0017 is unchanged.

**Alternatives considered:**

- **Lower the threshold to ~0.20** so the current geomean composite (~0.277 for citation-less answers; up to ~0.55 for cited answers) crosses it. Rejected: the threshold's purpose is to refuse fabrication when grounding is missing. Lowering it to make refusal-rate look better would silently allow ungrounded answers — exactly the failure mode ADR-0017's safety invariant exists to prevent. The H2 metrics are floored not because the threshold is wrong, but because upstream surfaces aren't producing the citations the threshold expects.
- **Mark ADR-0017 as "calibration deferred to Phase 4"** with a follow-up entry. Considered. The follow-up framing is correct (calibration *is* deferred), but ADR-0017 itself doesn't change — the threshold value, the geometric-mean composition, the refusal-shape contract, and the "calibrated when a real baseline exists" criterion all stay. A follow-up on ADR-0017 would be redundant with this decision-log entry.
- **Treat the H2 baseline as the v1 reference and call it done.** Selected. The 0.133 / 0.133 / 0.033 / 0.300 numbers ARE the regression-detection floor as specified in ADR-0016 — any Phase 4 number above them is improvement; the absolute numbers are meaningless until the upstream gaps close.

**Rationale:** The H2 baseline surfaced two upstream gaps (documented in [build-spec.md § Phase 3 § Retrospective](build-spec.md) lessons 4 + 5) that floor every metric:

1. **Connected-agents dispatch is non-functional.** `Wizard.md` instructs the Wizard to dispatch to Valuation/Rules/Repair, but `FoundryAgentFactory` constructs all four agents as standalone `AIAgent` instances with only `getMachineByTitle` attached — no actual sub-agent dispatch wiring. The Wizard either calls the function tool directly (and answers as itself, with `WizardAnswer.SubAgentUsed = "Wizard"` per the PR 4 placeholder, scoring 0 on subagent_accuracy unless the ground-truth expected "Wizard") OR refuses with the agent's own OutOfScope text (scoring 0 on citation_precision/recall).
2. **Eval ground-truth OPDB IDs aren't verified against deployed Cosmos.** PR 8's subagent curated plausible OPDB-format IDs from machine titles, but the deployed catalog has different actual IDs. When the agent successfully calls `getMachineByTitle("Godzilla")` it gets the catalog's record (e.g., a Sega 1998 entry instead of the Stern 2021 entry the ground-truth expected); citation_precision / citation_recall score 0 on a structurally-correct lookup.

Calibrating the threshold against a baseline that's floored by upstream gaps would tune for the gap, not the steady-state behavior. The right path is fix the gaps in Phase 4 then re-run the eval; if the post-fix baseline meets the ADR-0017 calibration target (citation_precision ≥ 0.7, recall ≥ 0.6, over-eager-refusal ≤ 20%) at 0.65, no calibration is needed; if not, calibrate at that point.

**Revisit when:** Phase 4 ships items 1 + 2 from [build-spec.md § Phase 4 § Inherited Phase 3 follow-ups](build-spec.md) (connected-agents wiring + eval ground-truth re-curation). Re-run `--eval` against deployed Foundry; if calibrated value moves >0.05 from 0.65, append a follow-up to ADR-0017 recording the post-calibration value.

**Related:** PR #93 (Phase 3 closeout, this PR), [build-spec.md § Phase 3 § Retrospective](build-spec.md), [ADR-0017](adr/0017-confidence-threshold-refusal.md), [ADR-0016](adr/0016-evaluation-harness.md), [`data/eval/results/wizard.20260507T162529Z.json`](../data/eval/results/wizard.20260507T162529Z.json).

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

**Rationale:** Local `grep -E -i <pattern>` against synthetic placeholder strings (`jim<at>earlybird-placeholder.invalid`, `noreply<at>earlybirdsolutions.invalid`, `pattern-test<at>distilledtech.com` — written with `<at>` instead of `@` here so this very file doesn't trip the workflow it documents) piped via stdin (no disk writes, no commits) exercises the *exact same* matcher the workflow uses (`grep -E -i "$WORK_EMAIL_PATTERN"` at sanitization.yml:115). Both positive (string matches → rule fires) and negative (similar-but-non-matching strings) cases are confirmed:

| Rule | Pattern | Positive case | Negative case |
| ---- | ------- | ------------- | ------------- |
| Personal email | `jim<at>earlybird` | `jim<at>earlybird-placeholder.invalid` → match ✅ | `unrelated-text<at>otherdomain.example` → no match ✅ |
| Personal domain | `<at>earlybirdsolutions` | `noreply<at>earlybirdsolutions.invalid` → match ✅ | `noreply<at>earlybird.io` → no match ✅ |
| Work email | `<at>distilledtech\.com` | `pattern-test<at>distilledtech.com` → match ✅ | `someone<at>distilledtechXcom` → no match (escape works) ✅ |

> **Note for future authors:** the `<at>` masking above is intentional. Writing the literal `@` form anywhere in the repo (outside `sanitization.yml`, which excludes itself from the scan) re-creates the recursive trap that PR `[sanitization-docs-fix]` resolved on 2026-05-08. When discussing the patterns, use the masking convention or refer the reader to `sanitization.yml` for the verbatim regex.

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

## 2026-05-15 — H-Alerts pre-launch drill: all 5 alert rules proven to fire

**Decision:** H-Alerts hand-off complete. All 5 alert rules proven to fire end-to-end (alert rule → action group `pinwiz-ops-alerts-dev` → email at `jim@earlybirdsolutions.com`).

**Method:** Synthetic telemetry injected via `infra/scripts/Invoke-AlertProof.ps1`. App Insights `disableLocalAuth` was temporarily set to `false` for the duration of the injection (~2 min window) then restored to `true` via the Deployment Stack (`Deploy-SharedResources.ps1 -Environment dev`). All injected data ages out of the 48-h evaluation window automatically.

**Alert fire timestamps (UTC):**

| Alert | Fired (UTC) | Resolved (UTC) |
| --- | --- | --- |
| `pinwiz-alert-latency-p95` — Wizard latency p95 > 5s | 2026-05-15T12:28:00Z | 2026-05-15T12:54:01Z (auto-resolved) |
| `pinwiz-alert-5xx-rate` — 5xx error rate > 5% | 2026-05-15T12:28:45Z | 2026-05-15T12:54:46Z (auto-resolved) |
| `pinwiz-alert-dead-letters` — RAG dead-letter depth > 50/h | 2026-05-15T12:29:40Z | 2026-05-15T13:40:39Z (auto-resolved) |
| `pinwiz-alert-daily-cost` — Daily cost > $15 | 2026-05-15T13:01:23Z | — |
| `pinwiz-alert-availability` — Availability < 99.5% | 2026-05-15T13:20:14Z | — |

Alerts 1–3 also auto-resolved once the synthetic data aged out of their evaluation windows — proving both the fire and the clear paths work end-to-end.

**H-Alerts hand-off complete.** All 5 alert rules proven to fire; email routing to `jim@earlybirdsolutions.com` confirmed.

**Revisit when:** Real application traffic generates genuine metrics (Phase 7). Alert thresholds may need tuning based on actual baseline.

**Related:** `infra/scripts/Invoke-AlertProof.ps1`, PR #215 (availability test), PR #207 (alert rules).

## 2026-05-15 — H-DR-Cosmos pre-launch drill: restore latency measured

**Decision:** H-DR-Cosmos hand-off complete. Cosmos point-in-time restore drill executed against the dev environment.

**Drill details:**
- Source account: `pinwiz-cosmos-dev-hlpz4` (Continuous 7-day backup, enabled 2026-05-14)
- Restore point: `2026-05-15T12:22:31Z` (30 min before drill, known-good state)
- Target account: `pinwiz-cosmos-dev-hlpz4-restore`
- Restore initiated: `2026-05-15T12:56:00Z`
- Restore completed: `2026-05-15T12:58:00Z` (provisioningState: Succeeded)
- **Wall-clock restore duration: ~2 minutes**
- Corpus at restore time: `pinwiz` database, `machines` + `ingestion_sources` containers, ~2,300 OPDB machine records

**Validation:** Restored account confirmed `pinwiz` DB present; `machines` (partition: `/manufacturer`) and `ingestion_sources` (partition: `/partitionKey`) containers present with correct partition keys. No cutover performed (drill only — source account is healthy).

**Cleanup:** Restore account deleted post-validation (`az cosmosdb delete`). No code cause; no root cause investigation needed.

**Runbook fix applied:** `docs/runbooks/03-cosmos-restore.md` had wrong `az cosmosdb restore` flag (`--account-name` for target; correct is `--target-database-account-name`). Fixed in the same session.

**Revisit when:** Corpus grows significantly (Phase 4.5 full-corpus expansion). Expect restore time to scale with data volume.

**Related:** `docs/runbooks/03-cosmos-restore.md` (updated Last walked + flag fix).

## 2026-05-15 — H-DR-Search pre-launch drill: procedure validated

**Decision:** H-DR-Search hand-off complete. AI Search rebuild procedure validated against the dev environment.

**Drill details:**
- AI Search service: `pinwiz-search-dev-hlpz4` (Basic SKU, East US)
- Index `pinwiz-rag-v1`: **does not exist** at drill time (RAG ingestion pipeline not yet deployed — Phase 7 work)
- Worker `pinwiz-ca-ragindexer-dev`: placeholder image, minReplicas=0 (no active replicas)

**Steps exercised:**
- Step 1 (triage): index 404 confirmed via REST — no corruption, index simply not yet created
- Step 2 (stop worker): worker already at 0 replicas; `az containerapp update --min-replicas 0` confirmed operational
- Step 3 (delete index): N/A — no index exists
- Step 4 (restart with ReconcileOnStartup): `az containerapp update --set-env-vars "RagIngestion__ReconcileOnStartup=true" --min-replicas 1` confirmed; worker scaled to 1 successfully
- Step 5 (lease lag): no telemetry (placeholder image produces no OTel data)
- Step 7 (cleanup): `RagIngestion__ReconcileOnStartup=false`, `minReplicas=0` restored

**Note on Deployment Stack drift:** All `az containerapp update` commands in this runbook make direct out-of-band changes that the next `Deploy-SharedResources.ps1` run will overwrite. This is by design — CLI for fast-response, Bicep for permanent state. `RagIngestion__ReconcileOnStartup` is intentionally transient and is not in the Bicep.

**Full rebuild time (est.):** Not measured at drill time — index empty. Build-spec estimates < 30 min for the curated-subset corpus (~30 machines × ~100 chunks). Measure on first real rebuild in Phase 7.

**Revisit when:** Phase 7 deploys the real RAG worker image and populates the index. Re-walk the full runbook at that point to measure actual rebuild-to-zero-lag time.

**Related:** `docs/runbooks/04-ai-search-rebuild.md` (updated Last walked + Deployment Stack note + corrected ACA app name + AAD auth for index deletion).

## 2026-05-15 — H-Dash: Application Insights workbook deployed and verified

**Decision:** H-Dash hand-off complete. "PinballWizard Ops" workbook deployed and verified in the Azure portal.

**Workbook URL:** `https://portal.azure.com/#@9793cd0f-2b27-4757-9986-1f7f1e35864a/resource/subscriptions/4dce9fdd-ea5f-4f67-9a00-80279e58659d/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.Insights/workbooks/ecabee92-c5ef-5e2f-8597-9a2ad352804d/workbook`

**State at verification (2026-05-15):** 7 tiles rendered. All tiles show "no data" — expected while the Wizard ACA app runs a placeholder image (no real `pinwiz.ai.*` / `pinwiz.rag.*` metrics emitted yet). One tile ("RAG changefeed health") showed a KQL parse error (`latest` is a reserved KQL token) — fixed in PR #215 (column alias renamed to `currentValue`). The workbook will show live signal once Phase 7 deploys the real app image.

**Revisit when:** Phase 7 deploys the real Wizard image. Verify all 7 tiles populate with real signal at that point.

**Related:** PR #207 (workbook Bicep), PR #215 (KQL fix + availability test).

## 2026-05-15 — Custom domain cert: HTTP validation for Cloudflare-compatible auto-renewal

**Decision:** Switch `pinwiz-wizard-cert` managed certificate from `domainControlValidation: 'CNAME'` to `'HTTP'` (PR following initial CNAME issuance).

**Problem with CNAME validation + Cloudflare proxy:** ACA validates CNAME ownership by resolving the domain directly. With Cloudflare proxy active (orange cloud), the domain resolves to Cloudflare's IPs, not the ACA FQDN. This means: (a) CNAME validation requires Cloudflare to be temporarily in DNS-only mode, and (b) every Let's Encrypt renewal (~90 days) would require the same manual toggle — or automation to call the Cloudflare API.

**HTTP validation:** ACA serves the ACME challenge token at `http://{domain}/.well-known/acme-challenge/<token>`. Requests flow: Let's Encrypt → Cloudflare proxy → ACA ingress. ACA handles the challenge at the ingress level (the container does not need to serve it). Works transparently with the proxy active for both initial issuance and renewals.

**Cloudflare prerequisite:** Add a Transform Rule (or Page Rule) to bypass the Cloudflare HTTPS redirect for the challenge path, otherwise Cloudflare rewrites `http://pinwiz.ai/.well-known/acme-challenge/*` to HTTPS before it reaches ACA, and Let's Encrypt's HTTP-01 challenge fails.

Cloudflare rule (Settings → Transform Rules → Rewrite URL):
- **Match:** URI path contains `.well-known/acme-challenge`
- **Action:** Off (skip "Always Use HTTPS" for this path)

Or via Cloudflare Configuration Rules: disable "Automatic HTTPS Rewrites" for that path.

**Revisit when:** Migrating to Cloudflare Origin Certificate (15-year validity, no ACME renewal) — would be the Option 2 follow-up documented in the renewal plan conversation.

## 2026-05-22 — Catalog document-to-game linking: three-pass strategy and inline vs. deferred execution

**Decision:** Implement document-to-game linking as three ordered passes in `CatalogBuilder.LinkDocumentsToGames` / `ResolveCoverPageLinksAsync`, executed inline (synchronously awaited before catalog save). Pass 1: xref-URL slug extraction. Pass 2: `Source.LinkText` edition fallback. Pass 3: cover-page ADI text extraction for remaining unlinked PDFs.

**Alternatives considered:**

- *Single-pass filename heuristic only (status quo):* Left ~7 documents per scrape run unlinked. Acceptable technically but weakens RAG citation fidelity — an unlinked document has no `GameSlug` and cannot be attributed to a game in Phase 2 answers.
- *Deferred CLI flag (`--resolve-covers`):* Would keep `ScrapeAsync` leaner and allow Pass 3 to run as a scheduled ACA Job. Rejected at current scale (~7 unlinked docs) because inline adds negligible latency and avoids a separate operational step. Scale-watch rule (see below) defines the switchover threshold.
- *All three passes inline vs. Pass 3 deferred from the start:* Chose inline-for-now with an explicit scale-watch rule rather than speculative ACA Job infrastructure. The revisit criterion is concrete and observable.
- *Longest-match tie-breaking (Pass 2 + 3):* Ties leave the document unlinked rather than picking arbitrarily. This is consistent across passes and avoids silently wrong attribution — a provenance miss is safer than a provenance lie.

**Rationale:** Provenance is the differentiator for Phase 2 RAG citations. Every document that reaches the chunker without a `GameSlug` produces a citation that says "source: unknown game." Pass 1 is zero-cost (dictionary lookup on already-loaded xref data). Pass 2 is a string scan with no I/O. Pass 3 is the only pass with real cost (PDF open + page extraction), and its cost is bounded by the number of unlinked PDFs, which is small and expected to remain so as xref coverage improves.

`SyncGameReferenceToCanonical` was also fixed to sync `GamePageUrl` from the canonical `GameRecord` rather than preserving whatever `BuildGameReference` wrote. `BuildGameReference` hardcodes `https://sternpinball.com/game/{slug}/` for all manufacturers — a latent provenance bug that only surfaces for non-Stern games whose slug happens to match a Stern slug pattern. Now healed on every `--build-catalog` run.

**Scale-watch rule (authoritative — also in `memory/project_adi_inline_scale_watch.md`):** Switch Pass 3 to a deferred `--resolve-covers` CLI command / ACA Job if either: (a) the unresolved count after a scrape run exceeds ~50 documents, or (b) inline Pass 3 execution adds more than ~30 seconds to the scrape run. Monitor via `pinwiz.catalog.unlinked_documents{resolution_pass="adi_pending"}`.

**Revisit when:** Post-merge backfill run (`dotnet run -- --build-catalog`) shows the actual post-Pass-3 unresolved count. If count is 0 or near-0 and stays there as new manufacturers are added, the scale-watch rule can be relaxed. If count grows past the threshold, implement the deferred path.

## 2026-05-30 — Per-assembly coverage policy: tiered floors replacing single aggregate

**Decision:** Adopt tiered per-assembly coverage floors. Core/Application ≥80%, Api/ServiceDefaults ≥75%, Infrastructure/Web ≥65%, Cli/RagIngestionWorker excluded. Aggregate floor stays at 70% as the mechanically-enforced CI gate.

**Problem with single aggregate:** The 70% gate was passing (74% aggregate) while two assemblies sat below a reasonable floor — Infrastructure at 66% and Web at 65%. These have structural coverage ceilings from architecturally untestable code: Playwright-driven scrapers (browser I/O), Cosmos SDK error paths delegated to SDK retry policies, and Razor render-tree components (pure parameter-in / HTML-out, not worth unit testing).

**Alternative rejected:** Raise Infrastructure and Web to 70%+ by adding tests. Rejected — marginal tests would test Playwright page navigation and SDK internals, not application behavior. Violates the "tests assert behavior, not structure" rule.

**Why not mechanically enforce per-assembly:** irongut/CodeCoverageSummary only supports a single aggregate threshold. The PR coverage table already shows per-assembly rates on every PR — cultural enforcement via review is sufficient.

**Ratchet rule applies:** Both aggregate and per-assembly floors can only move up. Permanent lowering requires a new decision-log entry.

**Phase 5 baselines (2026-05-30):** Core 75%, Application 90%, Api 84%, ServiceDefaults 84%, Infrastructure 66%, Web 65%, aggregate 74%.

**Related:** quality-spec.md § Code quality / Coverage policy, tests/coverage.runsettings, .github/workflows/ci.yml.

**Related:** PR #271, `memory/project_adi_inline_scale_watch.md`, ADR-0012 (Cosmos ARM vs data-plane — relevant if Pass 3 is later wired into the RAG ingestion pipeline).
