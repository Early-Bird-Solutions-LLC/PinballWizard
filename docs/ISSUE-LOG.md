# PinballWizard — Issue Log

Batch triage log. Issues are **logged only** — no fixes applied until the batch is closed
and we work them together.

**Status key:** `OPEN` · `CONFIRMED` (reproduced under Playwright) · `FIXED` · `WONTFIX`

## Verification run — 2026-08-02

Live sweep against `https://pinwiz.ai` via Playwright MCP, authenticated through the
Cloudflare Access email-OTP gate (`pinwiz.ai pre-launch gate`). **5 LLM-firing probes**
plus free DOM/console assertions.

App console errors: **none**. (The two CSP image errors observed belong to Cloudflare's own
login page, not the app.)

---

# FILED — 2026-08-02

All validated, non-duplicate findings are now **GitHub issues #780–#791**. This file is the
triage record and evidence archive; **GitHub is the tracker**.

| Issue | Title | Covers |
|---|---|---|
| [#780](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/780) | Confidence gate differs fresh vs. follow-up | ISSUE-010 |
| [#781](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/781) | Answers don't stream — first token ~11.3s vs p95 <1s target | ISSUE-024 |
| [#782](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/782) | Typeahead selection doesn't set `machineId` | ISSUE-001 |
| [#783](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/783) | Citations for machines absent from the answer | ISSUE-002 |
| [#784](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/784) | Zero-content short-circuit refusal UX | ISSUE-006 |
| [#785](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/785) | Typeahead stopword matching (regression vs #686) | ISSUE-007 |
| [#786](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/786) | `###`/`---` literal; one `<ol>` per item (partial regression of #371) | ISSUE-003, 008 |
| [#787](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/787) | Follow-up composer: DOM absence, order, Enter | ISSUE-004, 005, 009 |
| [#788](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/788) | Responsive overflow on `/`, `/admin`, `/documents` | ISSUE-011, 012, 016, 017, 018 |
| [#789](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/789) | 23 touch targets under 44×44; no `<h1>` | ISSUE-014, 019 |
| [#790](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/790) | **Meta:** existing a11y/responsive gates pass while defects ship | ISSUE-025 |
| [#791](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/791) | `?q=` vs ADR-0026's `/wizard/q/{slug}` (question) | ISSUE-026 |

**Not filed, deliberately:**

- **ISSUE-013** (nav rail squeezes input to 148px) — consequence of #623's deliberate
  `Breakpoint.None`. A tradeoff to re-weigh by whoever set it, not a bug to file behind them.
- **ISSUE-020** (follow-ups don't update URL) — explicitly deferred by ADR-0026 to the Entra
  External ID passport. The single-question URL form is asked about in #791 instead.
- **ISSUE-002b** (thematically wrong recommendations) — `theme` deferred from the v1 index by
  ADR-0021; needs a v2 cutover decision, not a bug report.
- **ISSUE-015, ISSUE-023** — withdrawn, not defects.
- **ISSUE-021 (print), ISSUE-022 (SEO)** — parked until functional/delight work closes.

### ISSUE-024 resolved by measurement

Recorded because an earlier draft of this log asserted a streaming violation before measuring
it. Measured 2026-08-02 at 100ms sampling:

| Event | Time |
|---|---|
| "Wizard is thinking" + CANCEL appears | 838 ms |
| Thinking ends | 11,187 ms |
| Answer appears | 11,296 ms |
| Growth steps | **1** (504 chars, 0ms fill) |

So there **is** a progress state — the "blank panel" premise was wrong — but no token
streaming, and first token lands ~11× over ADR-0026's p95 <1s target.

---

# RE-BASED TRIAGE — 2026-08-02

Everything below this section was written against a **stale clone, 888 commits behind
`origin/main`**, before the project's real ADRs and its 700+ GitHub issues were visible.
Observations remain valid (they were measured on the live site); classifications did not.

Each finding was re-checked against (a) existing GitHub issues, open and closed, and
(b) the ADRs on `origin/main`. **This table supersedes the one below it.**

**Dedupe result: 15 of 19 findings are genuinely unfiled. Two are regressions against
CLOSED issues. Two are consequences of deliberate decisions.**

| # | Finding | Already filed? | Spec status | Disposition |
|---|---|---|---|---|
| **010** | Confidence gate differs fresh vs. follow-up | No | **VIOLATES ADR-0017** — "single, auditable code path" | **File — highest severity** |
| **024** | Answers take 15–25s; streaming not observed | No | **ADR-0026 specifies SSE, first-token p95 <1s** | **Verify first**, then file |
| **003** | Ordered list renders 1./1./1. | **Regression of #371** (closed) | — | **File as regression** |
| **007** | Typeahead matches question stopwords | **Regression of #686** (closed) | — | **File as regression** |
| **001** | Typeahead selection doesn't set `machineId` | No (#718 is the inverse case) | **Gap** — ADR-0049 ↔ ADR-0026 don't specify propagation | File |
| **002** | Spurious citation ("The Jetsons") not in answer | No | **Gap** — ADR-0023 requires ≥1 citation, not relevance | File (citation-relevance only) |
| **002b** | Thematically wrong recommendations, 53% matches | No | **Deferred by ADR-0021** (`theme` is v2) + within ADR-0024 | Not a bug — see theme note |
| **006** | LOW CONFIDENCE despite resolved OPDB record | No | **Likely ADR-0053 working as designed** | File as UX/contract mismatch |
| **008** | `###` / `---` render as literal text | Scope gap in #371's fix | **Gap** — ADR-0026 doesn't mandate CommonMark | File |
| **004** | Follow-up input absent from DOM until clicked | No | Unspecified | File (enhancement) |
| **005** | Follow-up buttons render above the answer | No | Unspecified | File |
| **009** | Enter doesn't submit follow-up | No | Unspecified | File |
| **011** | Featured strip scrolls with 1010px spare | No | Unspecified | File |
| **012** | Landing page overflows at 320px | No | Unspecified | File |
| **013** | 56px nav rail squeezes input to 148px | Consequence of **#623** (deliberate `Breakpoint.None`) | — | **Not a bug** — re-weigh tradeoff |
| **014** | 23 touch targets under 44×44 | No | — | File + see 025 |
| **016** | `/admin` overflows +137px at 320px | No | Unspecified | File |
| **017** | `/documents` table breaks at 768px only | No | Unspecified | File |
| **018** | "New conversation" label clipped at 320px | No | Unspecified | File |
| **019** | Answer page has no `<h1>` | No | Unspecified | File |
| **020** | Follow-ups don't update the URL | No | **Deferred by ADR-0026** to Entra passport | **Withdraw as bug** — but see 026 |
| **025** | axe-core / Lighthouse / responsive gates pass while these ship | No | — | **Investigate — meta-issue** |
| **026** | Live site serves `/wizard?q=` but ADR-0026 specifies `/wizard/q/{slug}` | No | Possible gap | Verify |
| ~~015~~ | ~~Cards bleed past viewport~~ | — | — | **Withdrawn** — not a defect |
| ~~023~~ | ~~ADR links 404~~ | — | — | **Withdrawn** — stale-clone artifact |

## The four findings that changed most

**ISSUE-024 (streaming) — potentially the highest user-impact item, but UNVERIFIED.**
ADR-0026 specifies SSE streaming at `/api/wizard/ask:stream`, a `pinwiz.ai.first_token_ms`
histogram targeting **p95 < 1s**, and `ToolCallStarted`/`ToolCallCompleted` breadcrumbs — its
stated rationale being that "a user staring at a blank panel for that interval reads the
system as broken." I measured 15–25s waits but **did not verify whether tokens streamed
progressively** — my polling loop only tested for completed text. *Do not file this as a
violation until the first-token time is actually measured.*

**ISSUE-006 — I overstated this.** I called it "provably wrong." ADR-0053 defines a
deterministic zero-content short-circuit that fires *before* the agent turn when `machineId`
is supplied and the RAG index holds zero chunks, returning `NoCitation` without an LLM call.
Challenger fits exactly. The genuine defect is narrower: the UI shows the machine and says
"Related machines I know about," implying answerability, while the spec's real guarantee is
"answers only when the index has ≥1 chunk." A contract-vs-expectation mismatch, not a broken
gate. Related: #749 (corpus coverage gap), #745 (ingestion gaps).

**ISSUE-013 — my recommendation contradicted a deliberate decision.** Issue #623 set
`Breakpoint.None` *on purpose* so the rail stays visible at all widths, because it previously
vanished below 960px. "Collapse it on mobile" would revert that. The 148px input at 320px is
a real cost of the tradeoff, to be re-weighed by whoever made it — not a bug to fix behind
their back.

**ISSUE-020 — withdraw as a bug.** ADR-0026 explicitly defers multi-turn conversation
persistence to "when the Entra External ID passport ships." It *does* specify
`/wizard/q/{slug}` as a shareable single-question URL, and the live site serves
`/wizard?q=...` — logged separately as **026** to verify.

## ISSUE-025 — the meta-issue worth more than any single defect

Issues **#182, #183, #184** (all closed) added **axe-core, Lighthouse, and responsive
Playwright snapshot gates** to CI. Those gates are live now, and they did not catch: 23
sub-44px touch targets, horizontal overflow on three routes, or a missing `<h1>`.

A snapshot baseline captured while the bugs were present would do exactly this — enshrining
them as the expected rendering forever. **Before writing any new gate, find out why the
existing ones pass.** #343 ("UI revamp: bring the front-end to showcase quality") was closed
with Lighthouse accessibility and responsive-snapshot criteria in its acceptance gates.

This also voids much of [`testing/regression-test-spec.md`](testing/regression-test-spec.md),
which was written to build gates that already exist.

## On `theme` — definitive

ADR-0021 excludes it deliberately: *"The schema doesn't include some plausibly-useful fields
(`prev_section_heading`… **`theme` for thematic faceting**, `year_released`…). Adding any of
these requires a v2 cutover. The trade-off… is intentional: ship lean, expand on evidence."*

So theme is the right signal and there is **no path to filter or facet by it without a v2
index and full re-ingestion.** ADR-0024's H5b finding matters here too: recall is
**routing-dominated, not retrieval-dominated** — which also explains ISSUE-010, since
follow-up routing differs from fresh routing.

## Evaluation coverage gaps (ADR-0016)

- **Single-turn only** — structurally cannot catch ISSUE-010.
- Question set is *"biased toward simple lookups"* with no recommendation/similarity queries
  — cannot catch the spurious-citation class.
- `pinwiz.refusal_correctness` is the right instrument for ISSUE-006, but whether any
  zero-chunk machine is in the ~30-question set is undocumented.

Related open issues: **#717** (reranker-sensitive eval fixture), **#588** (re-run hard eval
as sources grow), **#616** (refusal-rate denominator).

---

<details>
<summary>SUPERSEDED — original status table (written against the stale clone)</summary>

| # | Issue | Status |
|---|---|---|
| 001 | Dropdown selection doesn't reach the answer | **CONFIRMED** |
| 002 | Recommendations with no matching citations | **CONFIRMED** — root cause found, see 010 |
| 003 | Ordered list renders 1./1./1. | **CONFIRMED** — root cause found |
| 004 | Follow-up input hidden behind a button | **CONFIRMED** — not in DOM at all |
| 005 | Follow-up controls render above the answer | **CONFIRMED** in DOM order |
| 006 | LOW CONFIDENCE despite a resolved OPDB record | **CONFIRMED** — and now provably wrong |
| 007 | Typeahead matches on question stopwords | **NEW** |
| 008 | Markdown `###` headings render as literal text | **NEW** |
| 009 | Enter does not submit the follow-up input | **NEW** |
| 010 | Confidence gate differs first-turn vs. follow-up | **NEW — highest severity** |
| 011 | Featured strip capped at 900px, scrolls with room spare | **CONFIRMED** — root cause found |
| 012 | Landing page overflows horizontally at 320px | **CONFIRMED** |
| 013 | Fixed 56px nav rail crowds the ask box on mobile | **CONFIRMED** |
| 014 | 23 touch targets below the 44px minimum | **CONFIRMED** |
| ~~015~~ | ~~Featured cards bleed past the viewport edge~~ | **WITHDRAWN** — not a defect |
| 016 | `/admin` overflows on mobile (+137px at 320) | **CONFIRMED** — worst page |
| 017 | `/documents` table breaks at 768 only | **CONFIRMED** |
| 018 | "New conversation" label clipped at 320 | **CONFIRMED** (minor) |
| 019 | Answer page has no heading structure (no `<h1>`) | **CONFIRMED** |
| 020 | Follow-ups don't update the URL — unshareable | **CONFIRMED** |
| 023 | All 4 landing-page ADR links 404 (no `docs/` in repo) | **CONFIRMED** |
| 021 | Print styles | **DEFERRED** — after functional/delight |
| 022 | SEO | **DEFERRED** — after functional/delight |

</details>

---

## ISSUE-001 — Machine selected from the ask-box dropdown is not carried into the answer

| | |
|---|---|
| **Status** | CONFIRMED |
| **Area** | Web (ask box) → API query pipeline |
| **Severity** | High |

### Reproduction (verified 2026-08-02)

1. Home page, type `whats a good strategy for transformers` into the Question input.
2. Dropdown appears. Click **"Transformers: More Than Meets the Eye — Stern 2026"**.
3. Navigates to:

```
https://pinwiz.ai/wizard?q=whats%20a%20good%20strategy%20for%20transformers
```

**No `machineId` parameter.** The answer then asked which Transformers machine was meant
("Transformers by Stern (2011) — You might have meant this game…").

### Root cause (narrowed)

`machineId` **is** a supported, plumbed query parameter — ISSUE-006 shows
`?q=…&machineId=G50L9-MDxXD` working end-to-end. The fault is isolated to the **ask-box
dropdown failing to bind the selected machine's id before navigating**. It sets the
textbox value only.

Component is MudBlazor (`mud-select-input`, `role=listbox` / `role=option`,
`#tj8hhrd4p_itemN`). Fix is in whatever handles the option-selected event on the home
ask box.

### What "fixed" looks like

- Selecting an option sets `machineId` on the navigation URL.
- With `machineId` present, no disambiguation prompt for that entity.
- The resolved machine is visible so the user can see/change scope.
- Free-text with no selection keeps today's disambiguation behavior.

---

## ISSUE-002 — Multi-game answer recommends machines with no matching citations

| | |
|---|---|
| **Status** | CONFIRMED — see ISSUE-010 for the mechanism |
| **Area** | API retrieval / citation binding |
| **Severity** | High |

### Original report

Follow-up *"show me other similarly themed games"* (context: Medieval Madness) recommended
Attack From Mars, Monster Bash, Theatre of Magic — with **1 citation, for Medieval Madness
itself**. Not one recommended machine was sourced.

Blending tell: *"castle-esque saucer destruction mechanism"* for Attack From Mars — AFM's
saucer destruction is real, "castle-esque" is Medieval Madness bleeding across.

### Reproduction (verified 2026-08-02)

Same intent, run as a follow-up to *"what are the rules for Medieval Madness"*:

> Here are some similarly themed games… **The Hobbit** (2015), **Halloween** (2021),
> **The Texas Chainsaw Massacre (SE)** (2024), **Evil Dead** (2024), **Barnyard** (2017)

`SOURCES · 8 cited from 1 site` — all `opdb.org` **Metadata** records at **53% match**,
including **"The Jetsons — Metadata"**.

Two further problems visible in that output:

- **Semantic quality is poor.** Three horror machines and a kids' game returned as
  "similarly themed" to a medieval-fantasy comedy. The answer half-admits it: *"The others
  have overlapping storytelling or dark humor elements but vary widely in tone."*
- **53% matches are being accepted** as the basis for a five-machine recommendation, and
  a machine that didn't make the answer (The Jetsons) still appears as a citation.

### What "fixed" looks like

- Citations bind to the claim they support, not just to the conversation.
- A relevance floor for recommendation-shaped answers; 53% shouldn't qualify.
- Machines that don't appear in the answer don't appear as its sources.
- If nothing retrievable supports a recommendation, say so instead of asserting specifics.

---

## ISSUE-003 — Ordered list renders as "1. / 1. / 1."

| | |
|---|---|
| **Status** | CONFIRMED — root cause identified |
| **Area** | Web — markdown rendering |
| **Severity** | Low (cosmetic) |

### Root cause (verified in DOM)

The renderer emits a **separate `<ol>` per numbered item** when nested content sits between
items. From the Medieval Madness rules answer:

```
ol[0] → 1 item → "Destroy Castles:"
ol[1] → 1 item → "Qualify for the Wizard Mode (Battle for the Kingdom):"
```

Two lists of one item each, neither with a `start` attribute — so both render as "1.".
This is a renderer/markdown-nesting fault, not the model emitting `1.` repeatedly.

Consistent with observed behavior: lists **without** interleaved nested content number
correctly (the Transformers disambiguation rendered a single `<ol>` of 2 items as 1, 2).

---

## ISSUE-004 — Follow-up input should always be visible

| | |
|---|---|
| **Status** | CONFIRMED |
| **Area** | Web — conversation view |
| **Severity** | Medium |
| **Type** | Enhancement / UX |

### Verified behavior

Before clicking: `document.querySelectorAll('input[type=text],textarea')` in the answer
view returns **`[]`**. The input is **not in the DOM at all** — not merely hidden. After
clicking **"Ask a follow-up"**, an input appears (`aria-label="Question"`, placeholder
`"Ask a follow-up…"`).

### Desired behavior

Render the follow-up input directly. It's the default next action after reading an answer;
a reveal click costs a turn every time and hides the affordance entirely from anyone who
doesn't know follow-ups exist. **New conversation** stays an explicit button.

### Related — inconsistent control labels

Three different labels for the same control cluster across views:
`Ask a follow-up` · `Ask another question` (refusal cards) · `Ask the follow-up` (submit).
Worth settling on consistent wording in the same pass.

---

## ISSUE-005 — Follow-up controls render between the question and its answer

| | |
|---|---|
| **Status** | CONFIRMED in DOM order |
| **Area** | Web — conversation layout |
| **Severity** | Low/Medium |

### Verified

Document-order indices within `<main>` for the Transformers answer:

| Node | Index |
|---|---|
| question text | 70 |
| `Ask a follow-up` / `New conversation` buttons | 73 |
| answer body | 78 |

The **button row** sits between question and answer. Note the follow-up **input**, once
rendered, lands at index 111 — correctly after the answer. So only the button row is
misplaced.

Not a previous turn's controls: reproduced on a single-turn conversation with nothing
above it.

---

## ISSUE-006 — LOW CONFIDENCE despite a resolved OPDB record

| | |
|---|---|
| **Status** | CONFIRMED — and now provably wrong, not just suspicious |
| **Area** | API — confidence gating / citation eligibility |
| **Severity** | High |

### Reproduction (verified 2026-08-02)

`https://pinwiz.ai/wizard?q=tell%20me%20about%20Challenger&machineId=G50L9-MDxXD`

→ LOW CONFIDENCE card. `machineId` confirmed present in the URL at render time. Card text:

> Related machines I know about: **Challenger**
> Why I can't answer: **No indexed source could be linked to back up an answer here.**

### Why this is now provably wrong

The ISSUE-002 re-run cites **8 OPDB "Metadata" records** as sources — including for
machines that were never asked about. So **OPDB metadata records are first-class citable
sources in this system.** Challenger has a resolved OPDB record, displayed on screen, and
the gate still reported "no indexed source."

The system will cite an OPDB metadata record at 53% for a machine nobody asked about, and
refuse to cite one for the machine the user explicitly resolved. Those cannot both be right.

Compounding: *"tell me about &lt;machine&gt;"* is a **metadata** question — manufacturer,
year, type, editions — exactly what an OPDB record contains.

### Where to look

Homepage cites **ADR-0017** (`docs/adr/0017-confidence-threshold-refusal.md`):

> "A geometric-mean composite of retrieval, model self-report, and citation coverage gates
> every answer. Below 0.65: structured refusal, never fabrication."

A geometric mean is zero if **any** factor is zero — so a single zeroed component (likely
document-corpus retrieval, with no manuals for an obscure machine) forces refusal
regardless of a perfectly good catalog hit. That would explain this exactly. Verify against
the ADR and the implementation.

Also see **ADR-0021** (AI Search index schema) and **ADR-0022** (citation extraction).

### Secondary nit

"Related machines I know about: Challenger" lists the machine that *was asked about*. It
isn't related — it's the subject.

---

## ISSUE-007 — Typeahead matches on stopwords from the whole question

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — machine typeahead / search query construction |
| **Severity** | Medium |

### Verified

Typing `whats a good strategy for transformers` returns these 8 options:

| Suggestion | Why it matched |
|---|---|
| Transformers — Stern 2011 | legitimate |
| **Invasion Strategy — Komputer Dynamics 1976** | the word *"strategy"* |
| **No Good Gofers — Williams 1997** | the word *"good"* |
| Transformers The Pin — Stern 2012 | legitimate |
| Transformers: More Than Meets the Eye — Stern 2026 | legitimate |
| **Quest for Glory — For Amusement Only 2021** | the word *"for"* |
| **Fore — Bally 1973** | the word *"for"* |
| **Avatar: The Battle for Pandora — JJP 2024** | the word *"for"* |

Half the picker is noise. The lookup is matching the **entire question text** token-by-token
instead of extracting the machine entity, so common words drag in unrelated machines.

Also: option 0 carries `mud-selected-item` — the list arrives with the first entry
pre-selected, which risks a stray Enter picking the wrong machine.

### What "fixed" looks like

- Match on an extracted entity, or at minimum strip stopwords before querying.
- Rank exact/prefix title matches above scattered token hits.
- Reconsider pre-selecting option 0.

---

## ISSUE-008 — Markdown `###` headings render as literal text

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — markdown rendering |
| **Severity** | Medium (visible on most long answers) |

### Verified

The Medieval Madness rules answer contains, as **literal on-screen text**:

```
### Rules and Gameplay Overview
#### Main Objectives
#### Multiball Modes
#### Features
```

`main.querySelectorAll('h1…h6')` returns only the page's own two `H6` elements — the
renderer produced **no heading elements** from the answer markdown.

Bold (`<strong>` × 7), `<ul>` (× 4) and `<ol>` (× 2) all render correctly. So the pipeline
handles inline emphasis and lists but drops ATX headings — likely a restricted renderer
config or a sanitizer stripping heading tags. Same subsystem as ISSUE-003.

---

## ISSUE-009 — Enter does not submit the follow-up input

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — follow-up input |
| **Severity** | Medium |

### Verified

Typed `show me other similarly themed games` into the follow-up input, pressed **Enter** —
nothing happened. Text remained in the box, no request fired. Submission required clicking
**"Ask the follow-up"**.

The home page ask box does submit on selection/Enter, so the two inputs behave differently.
Enter is the expected submit gesture for a single-line question field.

Pairs naturally with ISSUE-004 — same component, same pass.

---

## ISSUE-010 — Confidence gate behaves differently on follow-ups than on first-turn queries

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | API — confidence gating / conversation context |
| **Severity** | **Highest in this batch** |

### The experiment

Same question, two paths:

| Path | Result |
|---|---|
| **Fresh query** — `?q=show me games similar to Medieval Madness` | **LOW CONFIDENCE refusal.** *"The indexed manuals and service bulletins don't contain enough detail to answer this confidently."* |
| **Follow-up** — after *"what are the rules for Medieval Madness"*, asked *"show me other similarly themed games"* | **Answered.** Five machines recommended, 8 OPDB citations at 53% match |

Same information need. One path refuses; the other answers from weak matches.

### Why this matters most

ADR-0017's refusal gate is the product's central safety claim — the homepage advertises
"Below 0.65: structured refusal, never fabrication." If that gate is weaker on the
follow-up path, then **the guarantee has a hole in exactly the conversational flow users
spend most of their time in**, and ISSUE-002's ungrounded recommendations are not a
retrieval accident — they are what the follow-up path does by design right now.

Whichever branch is wrong, they disagree, and the disagreement is invisible to the user.

### Open questions

- Does the follow-up path compute confidence at all, or inherit the parent turn's score?
  Inheriting would explain it exactly: turn 1 scored well on a well-sourced rules answer,
  turn 2 rode that score into a question with no support.
- Does the follow-up reuse turn 1's retrieved context instead of retrieving for the new
  question? The 53% OPDB metadata hits suggest *some* new retrieval ran, but weakly.
- Which branch is the intended behavior? Arguably the *fresh* one is too strict (a
  "similar games" question is answerable from catalog metadata) while the *follow-up* one
  is too loose. Decide the target before fixing either.

### Relationship to other issues

Root cause candidate for **ISSUE-002**. Shares the citation-eligibility surface with
**ISSUE-006** — one refuses despite a good catalog record, the other accepts weak catalog
records. Both point at the same gate needing a coherent definition of what counts as a
source.

---

## Responsive sweep — 2026-08-02

Viewports tested: **1920×1080, 1440×900, 1024×768, 390×844 (iPhone 12–15), 320×568
(iPhone SE / smallest common)**. Routes: `/`, `/documents`, `/engineering`.
No LLM calls — pure DOM measurement.

**`/documents` and `/engineering` are clean at every width tested** — zero overflow, zero
bleeding elements, zero stray scrollers. All findings below are on the **landing page**.

---

## ISSUE-011 — "Try asking about…" strip is hard-capped at 900px and scrolls with room to spare

| | |
|---|---|
| **Status** | NEW — confirmed, root cause identified |
| **Area** | Web — landing page CSS (`.featured-strip`) |
| **Severity** | Medium — visible on every desktop view |

### Root cause (measured)

```
.featured-strip            max-width: 900px   ← the culprit
.featured-strip__scroll-row  clientWidth: 900px, scrollWidth: 1140px
  display:flex · flex-wrap:nowrap · overflow-x:auto · gap:12px
  6 children × 180px fixed (flex: 0 0 auto) + 5 gaps × 12px = 1140px
.landing-page__section     clientWidth: 1232px   ← available space
```

The strip needs **1140px** and is given **900px**, while its own parent section is
**1232px** wide. So it scrolls despite 332px of unused room *inside its own section* — and
far more relative to the viewport:

| Viewport | Strip width | Content needs | Spare viewport width |
|---|---|---|---|
| 1920 | 900 | 1140 | **1010px unused** |
| 1440 | 900 | 1140 | 530px unused |
| 1024 | 900 | 1140 | 114px unused |

The cap is fixed, so the wasted space *grows* with screen size — the bigger the monitor,
the more absurd the scrollbar looks.

### Note

900px reads like a reading-measure constraint (sensible for prose) applied to a card
strip that should use the full section width. Raising it to ≥1140px removes the scrollbar
outright at desktop; letting the cells wrap (`flex-wrap: wrap`) would also fix it and
degrade better. Keeping the carousel behavior at genuinely narrow widths is correct — the
bug is only that it engages when there's room.

---

## ISSUE-012 — Landing page overflows horizontally at 320px

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — landing page layout |
| **Severity** | Medium-High — page-level horizontal scroll is a hard responsive failure |

### Measured

At **320×568** (viewport 310px):

```
documentElement.scrollWidth = 321   vs   clientWidth = 310   →  overflows by 11px
```

The whole page scrolls sideways. At 390px and above it does not (excess = 0), so the
break is between 320 and 390.

Note this is distinct from ISSUE-011: the featured strip is an *internal* scroller and
does not itself push the page wide. Something else overflows by 11px at this width.

---

## ISSUE-013 — Fixed 56px nav rail crowds the primary input on mobile

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — app shell / navigation |
| **Severity** | Medium-High — degrades the product's single most important control |

### Measured

The left icon rail stays visible and **56px wide at every viewport**, including phones:

| Viewport | Rail | Main content | **Question input** |
|---|---|---|---|
| 390×844 | 56px | 324px | **218px** |
| 320×568 | 56px | 254px | **148px** |

On a small phone the rail consumes **18% of total screen width**, and the ask box — the
one control the entire product is built around — is reduced to **148px**.

### What "fixed" looks like

- Collapse the rail below a breakpoint: hamburger/drawer, or a bottom tab bar.
- Let the ask box take full available width on small screens.

*(Credit where due: the input uses `font-size: 16px`, which correctly prevents iOS
auto-zoom on focus. That's the right call and shouldn't be lost in the fix.)*

---

## ISSUE-014 — Touch targets below the 44px minimum

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — global |
| **Severity** | Medium (accessibility) |

### Measured

**23 interactive elements** fall below the 44×44 CSS-px minimum (WCAG 2.5.5 / Apple HIG),
at every viewport including mobile. Sample:

| Element | Size |
|---|---|
| Nav rail items (Ask the Wizard, Documents, Engineering, …) | 56 × **40** |
| Header logo link | 90 × **26** |
| Sound mute button | **26 × 26** |
| "How attribution works →" | 155 × **16** |
| Footer ADR links (`ADRs ↗`, `ADR-0021 ↗`) | 45–80 × **14** |

The 14–16px-tall footer/ADR links are the worst offenders — near-unhittable on a phone.
The count is identical at 1440 and 390, so nothing scales up for touch.

---

## ~~ISSUE-015 — Featured cards bleed past the viewport edge~~ — WITHDRAWN

**Not a defect.** Originally logged from a partial measurement. The broadened sweep shows
these cells sit *inside* the `.featured-strip__scroll-row` scroller and are clipped by it —
`documentElement.scrollWidth` never exceeds the viewport because of them (`ovf: false` at
1920/1440/1280/1024/768/390). Off-screen cards in a horizontal carousel are the intended
behavior, not bleeding layout.

The measurable defect is **ISSUE-011** alone (the strip scrolling when it has room).
Nothing to fix here.

---

## ISSUE-016 — `/admin` overflows horizontally on mobile (worst page in the app)

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — admin dashboard (`.wizard-usage-strip`) |
| **Severity** | Medium-High — worse than the landing page |

### Measured

| Viewport | Page overflow | Worst offender |
|---|---|---|
| 768 and above | none | — |
| **390×844** | **+67px** | `.wizard-usage-strip__left` — "Questions answered · …" |
| **320×568** | **+137px** | same |

`/admin` is the only route that overflows at 390px, and its 320px overflow (137px) is
**12× the landing page's** (11px). The usage strip is a fixed horizontal row that never
stacks.

Whether this matters depends on whether admin is ever opened on a phone — but it's the
largest single responsive failure measured, and likely a one-line flex-wrap fix.

---

## ISSUE-017 — `/documents` table breaks at tablet width only (768px)

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — documents table |
| **Severity** | Medium |

### Measured

| Viewport | Result |
|---|---|
| 1920 / 1440 / 1280 / 1024 | clean |
| **768** | `mud-table-root` bleeds **61px**; `mud-table-container` scrolls (654 → 739) with **104px viewport spare** |
| 390 / 320 | clean |

The table is fine on desktop *and* fine on phones — it fails only in the tablet band. That
shape says the responsive treatment (presumably a card/stacked layout at a mobile
breakpoint) kicks in **below** 768 while the desktop table is already too wide **at** 768.
A gap between the two breakpoints.

Note the same signature as ISSUE-011: a container scrolling while 104px of viewport sits
unused.

Touch-target count also drops from 40 → 34 at 768, suggesting some controls are hidden or
restyled at that width — worth checking nothing becomes unreachable.

---

## ISSUE-018 — "New conversation" button label clipped at 320px

| | |
|---|---|
| **Status** | NEW — confirmed (minor) |
| **Area** | Web — answer/refusal view |
| **Severity** | Low |

At 320px on the refusal card, `.mud-button-label` for **New conversation** measures
`clientWidth 88` vs `scrollWidth 98` — the label is cut off by 10px. Only occurrence of
text clipping found anywhere in the sweep.

---

## ISSUE-019 — The answer page has no heading structure

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — answer semantics / accessibility |
| **Severity** | Medium (accessibility + SEO) |

### Measured

On a fully-rendered rich answer, `main.querySelectorAll('h1,h2,h3,h4,h5,h6')` returned
**two `<h6>` elements only**:

- `H6: what are the rules for Medieval Madness` (the question)
- `H6: ● PinballWizard` (the footer wordmark)

So the page has **no `<h1>`**, and the answer's own section headings don't exist as
headings at all — they render as literal `### Rules and Gameplay Overview` text
(**ISSUE-008**). The two defects compound: the document outline is empty.

### Why it matters

- Screen-reader users navigate long content by heading. A 900-word answer with zero
  headings must be read start-to-finish.
- The question — the most important text on the page — is marked up as `<h6>`, the
  *least* significant heading level.
- No `<h1>` on the primary content route also costs SEO.

### What "fixed" looks like

- Question becomes `<h1>` (or `<h2>` under a page-level `<h1>`).
- Answer markdown headings render as real `<h2>`/`<h3>` (fixes with ISSUE-008).
- Heading levels nest without skipping.

---

## ISSUE-020 — Follow-ups don't update the URL, so conversations can't be shared or reloaded

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — routing / conversation state |
| **Severity** | Medium-High (delight + data loss) |

### Measured

After submitting the follow-up *"show me other similarly themed games"*, the address bar
still read:

```
https://pinwiz.ai/wizard?q=what%20are%20the%20rules%20for%20Medieval%20Madness
```

The follow-up and its answer exist only in page state.

### Consequences

- **Reload loses the conversation** and re-runs only the original question.
- **The URL can't be shared** — sending it to someone shows only turn 1. For a
  community-resource product whose whole pitch is "routes you to the source," an
  unshareable answer is a missed core affordance.
- **Back/forward almost certainly don't step through turns** (not yet verified).
- Every re-render costs another LLM call, since there's no durable conversation to restore.

### What "fixed" looks like

- Each turn pushes history state (conversation id, or appended turns).
- Reload restores the conversation rather than re-querying.
- A shareable permalink per conversation.

---

## Responsive coverage matrix

7 routes × 7 viewports + 2 answer states × 7 viewports = **63 measurements**.
✅ clean · ⚠️ internal scroller with room spare · ❌ page-level horizontal overflow

| Route | 1920 | 1440 | 1280 | 1024 | 768 | 390 | 320 |
|---|---|---|---|---|---|---|---|
| `/` (landing) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ❌ +11px |
| `/about` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `/documents` | ✅ | ✅ | ✅ | ✅ | ⚠️ +61px bleed | ✅ | ✅ |
| `/engineering` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `/status` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `/settings` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `/admin` | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ +67px | ❌ +137px |
| **answer page** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **refusal card** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ label clip |

### The answer page is the best-built page in the app

Worth stating plainly, since it's the page that matters most: the rich answer view
(headings, nested lists, citation strip, sources panel) showed **zero** overflow, **zero**
bleeding elements, **zero** stray scrollers, and **zero** clipped text at every one of the
seven viewports. `.citation-strip` reflows cleanly — 912 → 654 → 292 → 222px.

`.citation-strip` is capped at ~912px on wide screens, the same reading-measure pattern as
`.featured-strip`'s 900px — but for prose and citations that cap is correct, and it does
not scroll. No action.

### Minor, not yet issues

- `/documents` `<table>` parent is `overflow-x: visible` at mobile widths; fits today but
  has no scroll container if columns are added.
- `/engineering` `<code>` blocks likewise `overflow-x: visible`; fine at current content
  length.
- Touch-target counts vary by route (18 on `/status` and `/settings`, 40 on `/documents`)
  and stay essentially constant across viewports — see ISSUE-014.

---

## ~~ISSUE-023 — All four "Engineering" ADR links point at files that don't exist~~ — WITHDRAWN, INVALID

> **Withdrawn 2026-08-02, same day.** The links are fine. The repository
> `github.com/Early-Bird-Solutions-LLC/PinballWizard` **exists, is PUBLIC, and was pushed
> today**. `docs/adr/` on `origin/main` contains **30+ ADRs including 0017, 0021 and 0022** —
> the exact three files this issue claimed were missing.
>
> **Root cause of the false report:** the local clone had **no `origin` remote configured**
> and was **888 commits behind**. `ls docs` returned nothing because this checkout is stale,
> not because the docs don't exist. Remote has since been added and fetched.
>
> Lesson recorded rather than deleted: "file absent from the working copy" was treated as
> "file absent from the project." Verify against the remote before concluding something
> doesn't exist.

<details>
<summary>Original (incorrect) report, retained for the record</summary>

### ~~All four "Engineering" ADR links point at files that don't exist~~

| | |
|---|---|
| **Status** | NEW — confirmed |
| **Area** | Web — landing page links / repo docs |
| **Severity** | Medium — public trust signals that 404 |

### The links

The landing page's four engineering cards each link to GitHub:

| Card | Target |
|---|---|
| Polite Scraping | `.../tree/main/docs/adr/` |
| RAG over Manuals | `.../blob/main/docs/adr/0021-ai-search-index-schema.md` |
| Confidence Refusal | `.../blob/main/docs/adr/0017-confidence-threshold-refusal.md` |
| Source-Cited Answers | `.../blob/main/docs/adr/0022-citation-extraction.md` |

### Verified 2026-08-02

**No `docs/` directory exists anywhere in the repository** — not in the committed tree, and
not in the main checkout's working tree *including all uncommitted Wave 2 work*:

```
$ ls docs        → (no docs dir)
$ ls docs/adr    → (nothing)
```

Additionally, `main` is at `8ad4422` (the scraper commit) — so `tree/main/docs/adr/` cannot
resolve regardless.

### Why it matters

These four cards are the landing page's credibility argument — they're where a skeptical
visitor goes to confirm the product does what it claims. Every one of them dead-ends. The
Confidence Refusal card is the worst case: it advertises the exact guarantee that
**ISSUE-010** shows is inconsistently applied, and links to the ADR that would define it.

### Note

Since the repo is private, an unauthenticated visitor gets a 404 that's indistinguishable
from "file missing" — but the files genuinely are missing, so this is not a permissions
artifact.

### What "fixed" looks like

- Write the ADRs (0017, 0021, 0022 at minimum — they're already cited publicly), or
- Point the cards somewhere real until the ADRs exist.

Overlaps with **ISSUE-010** (ADR-0017 is the contract that issue says is undefined) and the
regression-prevention plan's "write the contract down" step.

### Progress 2026-08-02 — 1 of 4 links addressed

[`docs/adr/0017-confidence-threshold-refusal.md`](adr/0017-confidence-threshold-refusal.md)
now exists at the exact path the Confidence Refusal card links to. **Still broken:**

- `tree/main/docs/adr/` (Polite Scraping) — resolves once `docs/adr/` reaches `main`
- `0021-ai-search-index-schema.md` (RAG over Manuals) — not written
- `0022-citation-extraction.md` (Source-Cited Answers) — not written

Note all four links target **`main`**, which is still at `8ad4422`. Even written ADRs stay
404 until the branch merges — the fix isn't complete until then.

### Escalation 2026-08-02 — the repository has no git remote

```
$ git remote -v
(no output)
```

This local repository has **no remote configured at all**, so nothing has been pushed to
`github.com/Early-Bird-Solutions-LLC/PinballWizard`. Combined with the missing `docs/`
directory, this means the four landing-page links are not "pointing at files that haven't
been written yet" — they may be pointing at a **repository that does not publicly exist**.

Cannot be confirmed from here: the repo might exist on GitHub, pushed from another machine
or clone. But it is not reachable from this working copy, so the assumption that merging to
`main` will fix the links does not hold on the evidence available.

**Verify before treating ISSUE-023 as scoped:** does
`github.com/Early-Bird-Solutions-LLC/PinballWizard` exist, and is it public? If it is
private, the links 404 for every visitor regardless of what is written or merged — and the
fix is to stop linking there, not to write more ADRs.

</details>

---

## ⚠️ Log-wide caveat — this log was written against a stale clone

Discovered 2026-08-02 while committing. The working copy used for all repo-side analysis was
**888 commits behind `origin/main`** with no remote configured.

**Still valid — measured against the live deployed site:**
ISSUE-001 through ISSUE-020 are observed behaviors of pinwiz.ai, reproduced under Playwright
with numbers recorded. Being out of date locally does not change what the deployed app did.

**Now unreliable — anything inferred from local repo state:**

- "Wave 2 code is uncommitted / not in this worktree" — an artifact of the stale clone.
- "The contract was never written down" — it was, in ADR-0017, on 2026-05-04.
- ISSUE-023 entirely (withdrawn above).
- The parallel-session conflict analysis, which assumed `.github/workflows/*` and `infra/`
  were new uncommitted work rather than long-established files.

**Before any further design or fix work**, re-read these ADRs on `origin/main` — several look
directly on-point and may already specify or resolve what this log treats as open:

| ADR | Why it matters here |
|---|---|
| 0016 — evaluation-harness | The eval tier in the regression spec may already exist |
| 0017 — confidence-threshold refusal | The real contract behind ISSUE-006 / ISSUE-010 |
| 0021 — AI Search index schema | Names `theme` as a facet field (see below) |
| 0023 — citation-required guardrail | Directly on ISSUE-002 |
| 0024 — two-stage reranking | Relevance quality; bears on the 53%-match failure |
| 0026 — user-delight frontend and streaming | The perceived-latency question |
| 0029 — version-aware answering | Bears on ISSUE-001's 2011-vs-2026 disambiguation |
| 0049 — findability and relevance ranking program | Likely supersedes the D5 discussion |

### This log is not the project's issue tracker

Also discovered 2026-08-02: the project already tracks work in **GitHub Issues** —
**700+ issues** (numbered past #762), actively labelled (`bug`, `claude-code`,
`enhancement`, `docs-agent-failure`), with issues opened as recently as today.

Two open issues look like probable overlaps with findings here:

- **#718** — "Wizard: clarify on unqualified title collisions (Cactus Canyon didn't)" —
  related to ISSUE-001, possibly the *inverse* case (failed to clarify when it should).
- **#655** — "Catalog: Medieval Madness has no manufacturerSlugs; Monster Bash / Attack from
  Mars generic manuals can't resolve edition ambiguity" — overlaps ISSUE-002's territory.

**Consequence:** this file should be a **staging document for triage**, not a parallel
tracker. Validated, non-duplicate findings belong in GitHub Issues where the rest of the
project's work lives. A second tracker that only one session knows about is how findings get
lost.

### The regression test spec also needs re-basing

`origin/main` already has **8 test projects** — `Api`, `Application`, `Cli`, `Core`,
`Infrastructure`, `PerfMetrics`, `ServiceDefaults`, `Web` — plus `coverage.runsettings`.
The stale clone showed only four, with different names.

[`testing/regression-test-spec.md`](testing/regression-test-spec.md) was written assuming
little existing test infrastructure and no evaluation harness. **ADR-0016 is an evaluation
harness ADR**, so its Tier 3 section may be substantially redundant. Do not build from that
spec until it has been reconciled against the real test projects and ADR-0016.

---

## Deferred backlog — after functional & delight work

Explicitly parked 2026-08-02. Not to be worked until the functional/delight issues above
are closed. Logged now so they aren't rediscovered later.

### ISSUE-021 — Print styles (DEFERRED)

| | |
|---|---|
| **Status** | DEFERRED |
| **Area** | Web — print stylesheet |

Not yet investigated. When picked up, the answer page is the only route that plausibly
matters — someone printing a rules or repair answer to carry to the machine is a real
pinball-workshop use case. Expected scope:

- Suppress nav rail, footer, sound/theme controls, and the follow-up composer.
- Keep citations and expand them to full URLs (a printed page can't be clicked).
- Avoid page breaks inside citation cards and mid-list.
- Ensure the cream/tan palette doesn't render as heavy background ink.

**Depends on ISSUE-019/008** — print benefits from real heading structure, so do those first.

### ISSUE-022 — SEO (DEFERRED)

| | |
|---|---|
| **Status** | DEFERRED |
| **Area** | Web — metadata / crawlability |

Not yet investigated beyond the missing `<h1>` already logged as **ISSUE-019**. Expected
scope when picked up:

- `<h1>` per route and a non-skipping heading outline (ISSUE-019 covers the answer page).
- Per-route `<title>` and `<meta name="description">` — titles already look good
  (`PinballWizard — Documents`, `— System Status`, etc.); descriptions unverified.
- Open Graph / Twitter card tags for shared links. **Blocked on ISSUE-020** — there's no
  point optimizing link previews while conversation URLs aren't shareable.
- `robots.txt` / `sitemap.xml`, canonical URLs.
- Structured data (`schema.org`) for machine pages, if machine detail routes ship.
- Server-rendered content for crawlers — worth confirming what Blazor Server emits to a
  bot that doesn't hold a SignalR circuit.

**Note:** the site currently sits behind a Cloudflare Access pre-launch gate, so nothing is
crawlable today regardless. SEO work only becomes meaningful at public launch — which is
part of why it's parked.

---

## Regression prevention

Two companion documents, both written in this worktree and touching nothing the parallel
pipeline session owns:

- **[ADR-0017 — Confidence Threshold, Source Eligibility, and Structured Refusal](adr/0017-confidence-threshold-refusal.md)**
  The contract behind ISSUE-002/006/010. Those three aren't independent bugs — they're three
  code paths each guessing at a rule nobody wrote down. **Accepted 2026-08-02**: D1, D2, D3,
  D4, D6 ratified; **D5 (relevance floor) deferred pending measurement** — it's the one
  decision that can't be made from current data, and guessing it would silently trade
  fabrication for over-refusal.

  **Note:** ratifying ADR-0017 does *not* close ISSUE-002. D3/D4 make the system honest
  about what it cited; they don't make it better at choosing what to recommend. The
  horror-games-for-medieval failure is governed by the deferred D5 — see the correction in
  the ADR's D4 section.

- **[Regression test specification](testing/regression-test-spec.md)**
  What to assert, as invariants rather than instances, across three tiers (unit / E2E /
  LLM evals), with per-issue traceability and CI requirements.

**Sequencing note:** the test spec is deliberately specification-only. The parallel session
owns the E2E harness and CI workflows; writing competing harness code here would collide on
`.github/workflows/*` and `PinballWizard.slnx`. The 2026-08-02 sweep scripts already
implement several assertions in working form and should be handed over as harness input.

---

## Notes for the fix pass

- **Wave 2 code is not in this worktree.** This worktree is at `1f93103` (Wave 1); the
  Chat/Wizard/API implementation is uncommitted in the other session. That work must be
  committed before any of these can be fixed here.
- **Suggested grouping:**
  - *Rendering* — 003, 008 (same markdown subsystem)
  - *Follow-up component* — 004, 005, 009 (+ label consistency)
  - *Selection plumbing* — 001, 007 (same ask-box component)
  - *Confidence & citations* — 010, 006, 002 (decide the gate's contract first; 010 is the
    keystone)
  - *Landing-page CSS* — 011, 012
  - *Responsive shell / a11y* — 013, 014 (breakpoint behavior + touch-target pass)
  - *Per-page responsive* — 016 (`/admin` flex-wrap), 017 (`/documents` 768 breakpoint), 018
- **Responsive verdict:** 5 of 7 static routes plus both answer states are clean at every
  width. The defects cluster on `/` (011, 012), `/admin` (016) and `/documents` at one
  breakpoint (017) — none are systemic, all are local CSS fixes.
- **Automation:** every check above is scriptable. Worth building as an E2E suite —
  requires a Cloudflare Access **service token** (`CF-Access-Client-Id` /
  `CF-Access-Client-Secret`) so it can run headless in CI without the email-OTP gate.
