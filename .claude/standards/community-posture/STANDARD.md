---
name: community-posture
id-prefix: COMM
status: active
applies-to:
  - "data/seeds/community_resources.v1.json"
  - "data/seeds/pinside_slug_aliases.v1.json"
  - "src/PinballWizard.Application/Ai/Agents/*.md"
  - "src/PinballWizard.Application/Ai/Refusal/**"
  - "src/PinballWizard.Web/**"
---

# Community-Posture Standard

PinballWizard routes users outward to community venues — outbound traffic is a
feature, not leakage. This standard encodes the posture as checkable rules:
plural sets, alphabetical ordering, a closed topic taxonomy, no engagement
metrics, and a hand-curated Pinside alias table.

**RULE COMM-01** (plurality-thresholds)
WHEN:   a `(RefusalCategory × QuestionTopic)` cell fires a recovery payload, OR `community_resources.v1.json` is modified
THEN:   the recovery payload returns ≥3 entries for `marketplace` categories and ≥2 entries for every other non-singular category (`machine_reference`, `forums`, `tournament_and_play`, `tool`)
NEVER:  render a single-CTA recovery for any non-singular category; reduce a category below its minimum in the seed JSON
CHECK:  dotnet test --filter "FullyQualifiedName~CommunityResourcesContractTests" --no-build
        NOTE: CommunityResourcesContractTests.Marketplace_Category_Has_At_Least_3_Entries and Machine_Reference_Has_At_Least_2_Entries assert the seed thresholds; CommunityResourceLoader also enforces at startup (fail-fast).
SEV:    🔴
REF:    INVARIANTS#15 · ADR-0027 § 3 · feedback_destination_plurality

**RULE COMM-02** (no-editorial-ranking)
WHEN:   adding or modifying a community resource entry, refusal recovery payload, or agent prompt that names community venues
THEN:   ordering within a plural set is alphabetical by display name (computed by the loader, not baked into JSON entry order); all cards in a plural set use identical visual treatment; no entry is marked "primary," "featured," or "recommended"
NEVER:  order by frequency-of-use, click-rate, or any non-alphabetical / non-randomized scheme; use "we recommend X," "the best place is Y," "you should go to Z," or any superlative ("best," "biggest," "most popular") in descriptions or agent prose; label any entry as "primary" or "featured"
CHECK:  dotnet test --filter "FullyQualifiedName~CommunityResourcesContractTests.Descriptions_Avoid_Superlatives" --no-build
        NOTE: superlative guard in CommunityResourcesContractTests; within-set alphabetical ordering is enforced structurally in CommunityResourceLoader (name-sorted at load); agent prose is qualitative — /local-review category 13
SEV:    🔴
REF:    INVARIANTS#15 · ADR-0027 § 2 · ADR-0027 § 6 · feedback_avoid_appearance_of_favoritism

**RULE COMM-03** (questiontopic-closed-enum)
WHEN:   adding a new `QuestionTopic` value, a new switch-case for an undeclared topic, or a new refusal-routing path not covered by the existing 6-value taxonomy (`Repair`, `Gameplay`, `Market`, `Location`, `Tournament`, `General`)
THEN:   include an ADR-0027 amendment in the same PR; update the refusal-routing matrix, the seed JSON `topics[]` field, the agent prompts, and all contract tests in the same change
NEVER:  soft-add a topic via a `RefusalPanel` edge-case or string literal without amending ADR-0027; bypass the matrix with an ad-hoc routing path
CHECK:  git diff --name-only origin/main...HEAD | rg "QuestionTopic|RefusalPanel|community_resources" | xargs -r rg -n "QuestionTopic\." -- || echo CLEAN
        NOTE: once QuestionTopic is implemented, the CHECK should also run `dotnet test --filter "FullyQualifiedName~QuestionTopicEnumClosedTests"` — that test class is planned in ADR-0027 § 12 but not yet authored; add it when the enum ships.
SEV:    🔴
REF:    INVARIANTS#15 · ADR-0027 § 5

**RULE COMM-04** (no-engagement-metrics)
WHEN:   adding or modifying any UI component, Razor page, telemetry instrument, or agent prompt
THEN:   aggregate cost/capacity/drift telemetry is acceptable; per-session and per-user behavioral surfaces are not
NEVER:  surface "trending questions," "popular machines," "most-asked," "recommended for you," signup gate, first-run tour, session-history persistence (localStorage / cookies / server-side session store), per-user click-trail, or sponsor / paid-placement tier for any community resource
CHECK:  (qualitative — /local-review category 13)
        NOTE: a source-code grep for these terms fires on legitimate comment references (e.g. ADR-0027 amendment notes in WizardAnswerStream.razor) — the rule is enforced by design posture and PR review, not a grep.
SEV:    🔴
REF:    INVARIANTS#15 · ADR-0027 § 1 · ADR-0027 § 10

**RULE COMM-05** (no-runtime-pinside-probes)
WHEN:   any code path resolves a Pinside URL for a specific machine title
THEN:   look up the slug exclusively from `data/seeds/pinside_slug_aliases.v1.json`; fall back to a Pinside search URL (`/forum/all-pinball/topics?search=<title>`) when the alias is absent, and surface the gap in the `/about` coverage disclosure
NEVER:  derive the Pinside machine-page slug by runtime string manipulation, HTTP probing, or scraping; call any Pinside endpoint programmatically (their UA policy prohibits it and it violates the polite-by-construction invariant)
CHECK:  git diff --name-only origin/main...HEAD | rg "Pinside|pinside" | xargs -r rg -n "pinside\.com.*slug|ResolvePinsideSlug\|\.GetAsync\|\.SendAsync" -- | rg -v "pinside_slug_aliases" || echo CLEAN
SEV:    🔴
REF:    INVARIANTS#15 · INVARIANTS#2 · ADR-0027 § 8 · feedback_polite_scraping

**RULE COMM-06** (seed-schema-required-fields)
WHEN:   adding or modifying an entry in `data/seeds/community_resources.v1.json`
THEN:   every entry carries all required fields: `category` (value in the closed enum), `name`, `url` (absolute, lowercase hostname), `description` (superlative-free), `covers_question_types` (array)
NEVER:  omit a required field; set `category` to a value not in the canonical set (`marketplace`, `machine_reference`, `news_and_culture`, `forums`, `tournament_and_play`, `manufacturer_pages`); use a relative URL; include superlatives in `description`
CHECK:  dotnet test --filter "FullyQualifiedName~CommunityResourcesContractTests" --no-build
        NOTE: CommunityResourcesContractTests covers schema_version, required fields, absolute/lowercase URLs, canonical categories, and superlative-free descriptions; CommunityResourceLoader adds fail-fast startup validation.
SEV:    🔴
REF:    INVARIANTS#15 · ADR-0027 § 7 · CommunityResourcesContractTests

## Definition of Done

- COMM-01: `CommunityResourcesContractTests` passes; no single-CTA recovery payload for non-singular categories.
- COMM-02: `CommunityResourcesContractTests.Descriptions_Avoid_Superlatives` passes; within-set ordering is alphabetical; `/local-review` category 13 finds no editorial language.
- COMM-03: any new `QuestionTopic` value ships with an ADR-0027 amendment in the same PR.
- COMM-04: no engagement-metric surfaces in UI, telemetry, or agent prose; `/local-review` category 13 is clean.
- COMM-05: no runtime Pinside probes in the diff; slug resolution routes through `pinside_slug_aliases.v1.json`.
- COMM-06: `CommunityResourcesContractTests` passes; all required fields present in every seed entry.
