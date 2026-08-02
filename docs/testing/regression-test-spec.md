# Regression Test Specification

**Purpose:** lock in the defects found on 2026-08-02 (see [`../ISSUE-LOG.md`](../ISSUE-LOG.md))
so they cannot silently return, and catch *new* instances of the same classes as features land.

**Audience:** whoever builds the E2E harness and CI pipeline. This document specifies **what
to assert**, not where the code lives or which runner to use — those belong to the harness.

**Status:** specification only. No test code written yet (deliberately — the harness is being
built in a parallel session; see "Ownership" below).

---

## Governing principle: assert the invariant, not the instance

The single decision that determines whether this suite ages well.

A test asserting "`/admin` does not overflow at 320px" catches exactly one bug, forever. A
test asserting "**no route overflows at any viewport**" catches that bug, the two others we
found, and every page not yet written.

| Don't assert | Assert |
|---|---|
| `/admin` doesn't overflow at 320px | No route overflows horizontally at any tested viewport |
| The featured strip fits at 1440 | No element scrolls horizontally while its viewport has spare width |
| Footer ADR links are ≥44px | No interactive element anywhere is smaller than 44×44 |
| `###` renders as `<h3>` | A markdown fixture corpus round-trips to expected element types |
| The Transformers option sets `machineId` | Selecting *any* typeahead option puts `machineId` in the URL |
| `/wizard` has an `<h1>` | Every route has exactly one `<h1>` and no skipped heading levels |

Routes should be **discovered** (crawl the nav and footer), not hard-coded, so a new page is
covered the day it ships rather than the day someone remembers to add it.

---

## Tier 1 — Unit (fast, no browser)

| ID | Invariant | Covers |
|---|---|---|
| U1 | Markdown fixture corpus renders to expected element types: ATX headings `##`–`####` → `<h2>`–`<h4>`; **never** literal `#` text in output | 008 |
| U2 | An ordered list with nested content between items renders as **one** `<ol>` with N children — not N lists of one child | 003 |
| U3 | Typeahead query construction: given a full question, the machine lookup term excludes stopwords. `"whats a good strategy for transformers"` must not match `Fore`, `No Good Gofers`, or `Invasion Strategy` | 007 |
| U4 | Typeahead ranking: exact/prefix title matches rank above scattered token matches | 007 |
| U5 | Source-eligibility (per ADR-0017 D1/D2): a metadata-intent question with a catalog record present resolves as *answerable*; a repair-intent question with catalog only resolves as *refuse* | 006 |
| U6 | Confidence composite: a zero document-retrieval factor does **not** force refusal for metadata intent | 006 |

---

## Tier 2 — E2E browser (per PR)

### Responsive — parameterized over discovered routes × viewports

Viewports: **1920, 1440, 1280, 1024, 768, 390, 320** (width; heights per the sweep).

| ID | Invariant | Covers |
|---|---|---|
| E1 | `document.documentElement.scrollWidth <= clientWidth + 1` for every route × viewport | 012, 016 |
| E2 | No element has `scrollWidth > clientWidth` with `overflow-x: auto\|scroll` **while `viewportWidth - clientWidth > 0`** — i.e. never scroll when there's room. Carousels below their intended breakpoint are the documented exception | 011, 017 |
| E3 | No interactive element (`button`, `a`, `[role=button]`) renders below 44×44 CSS px | 014 |
| E4 | On viewports ≤ 480px the nav rail is collapsed (not a persistent fixed-width column) | 013 |
| E5 | No leaf text node is clipped (`scrollWidth > clientWidth + 2`) | 018 |
| E6 | Every route has exactly one `<h1>`; heading levels never skip | 019 |

> **Baseline (measured 2026-08-02):** E1 fails on `/` at 320 (+11px) and `/admin` at 390
> (+67px) and 320 (+137px). E2 fails on `/` at all widths and `/documents` at 768.
> E3 fails everywhere (18–40 elements/route). `/about`, `/engineering`, `/status`,
> `/settings` and both answer states pass E1 at every width today — **don't let that regress.**

### Ask-box and conversation flow

| ID | Invariant | Covers |
|---|---|---|
| E7 | Selecting any typeahead option navigates to a URL containing `machineId=<selected id>` | 001 |
| E8 | The follow-up input is present in the DOM on answer render — no reveal click required | 004 |
| E9 | Follow-up controls appear **after** the answer in document order | 005 |
| E10 | Pressing Enter in the follow-up input submits it | 009 |
| E11 | Each conversation turn updates the URL; reload restores the full conversation without re-querying | 020 |
| E12 | No external link on any route returns 404 (covers the ADR cards) | 023 |
| E13 | Zero console errors on every route | regression guard |

---

## Tier 3 — LLM behavior evals (scheduled + on prompt/pipeline change)

**These cannot be exact-match tests.** Answers are non-deterministic; golden-text assertions
will flake, get marked flaky, and then get disabled — which is worse than having no test,
because the suite still reports green. Assert **properties that must hold regardless of
wording**, and run each case N times (suggest N=3) requiring the property to hold every time.

| ID | Invariant | Covers |
|---|---|---|
| L1 | When `machineId` is supplied, the response contains no disambiguation prompt for that entity | 001 |
| L2 | Every machine named as a recommendation has ≥1 citation whose subject is that machine | 002, ADR-0017 D4 |
| L3 | No citation is shown whose subject appears nowhere in the answer | 002 |
| L4 | A resolved catalog record ⇒ the gate never reports "no indexed source" | 006, ADR-0017 D2 |
| L5 | **The same question asked fresh vs. as a follow-up yields the same refuse/answer verdict** | 010, ADR-0017 D3 |
| L6 | No citation below the ADR-0017 D5 relevance floor is used to support a recommendation | 002 |
| L7 | Refusal copy matches the situation (machine-known vs. machine-unknown) | ADR-0017 D6 |

**L5 is the keystone.** It is roughly ten lines — ask X cold, ask X as a follow-up, compare
verdicts — and it covers the highest-severity issue in the log.

### Cost control

Each eval case fires a real LLM call. Keep the golden set small and stable (10–15 questions
spanning metadata / rules / repair / recommendation / known-unanswerable). Run on a schedule
and on prompt or pipeline changes — **not** on every PR.

---

## CI requirements

### The E2E suite must FAIL, not skip, when its configuration is absent

The site sits behind Cloudflare Access. Headless CI needs a **service token**
(`CF-Access-Client-Id` / `CF-Access-Client-Secret`) — the email-OTP path used for the manual
sweep cannot run unattended.

**If those credentials are missing, the E2E stage MUST fail.**

An auth-gated browser suite that self-skips on missing config reports green while asserting
nothing. A regression suite that does this becomes the thing *hiding* regressions — the
precise failure this document exists to prevent. Guard on an explicit CI signal: skip
locally when a developer has no token, fail in CI.

### Assert an executed-test count

A green run with too few tests executed is a failure. Parse the result count and fail below a
minimum. Without this, a filter change or a collection error silently empties the run and
still reports success.

### Gate placement

- **Tier 1 + Tier 2** — required check on every PR.
- **Tier 3** — scheduled, plus triggered on changes to prompts, retrieval, or the confidence
  gate. Report as a separate status so an eval blip doesn't block unrelated work.

---

## Ownership and sequencing

Two sessions are working in parallel (state as of 2026-08-02):

- **Pipeline session** — owns `.github/workflows/*.yml`, `infra/`, and the E2E harness. Work
  is currently uncommitted; `ci.yml` today runs `dotnet test PinballWizard.slnx` plus Bicep
  validation, with no E2E job yet.
- **This session** — owns `docs/ISSUE-LOG.md`, `docs/adr/`, and this specification. No
  workflow file, solution file, or test project touched, deliberately, to avoid collision.

**Handover available:** the Playwright measurement scripts used for the 2026-08-02 sweep
already implement E1, E2, E3 and E7 in working form (route × viewport loop returning a
metrics matrix). They should be handed to the pipeline session as harness input rather than
reimplemented here.

**Blocked on:** Wave 2 application code is uncommitted, so no test can be written against the
components under test until it lands.
