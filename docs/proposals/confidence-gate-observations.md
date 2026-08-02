# Confidence Gate — Field Observations (NOT an ADR)

> ## ⚠️ Retracted as an ADR — read this first
>
> This was drafted 2026-08-02 as "ADR-0017" on the belief that no such ADR existed. **That
> belief was wrong.** The local clone was disconnected from `origin` and **888 commits
> behind**; a real, **Accepted** `ADR-0017 — Confidence-threshold refusal: geometric-mean
> confidence + categorized "I don't know"` has existed since **2026-05-04**, along with 50+
> other ADRs.
>
> It has been moved out of `docs/adr/` so it cannot overwrite the genuine ADR, and its
> "Accepted / D1–D6 ratified" status is **void** — nothing here was ever ratified against
> the real design.
>
> **What is still valid:** the observed behaviors, all measured against the live deployed
> site at pinwiz.ai on 2026-08-02. Those happened.
>
> **What is not:** every inference about what the system was *designed* to do, and the
> claim that the contract was "never written down." It was written down in May. The real
> question is why deployed behavior diverges from it — which is a more useful question, and
> needs the real ADR-0017, ADR-0023 (citation-required-guardrail) and ADR-0024
> (two-stage-reranking) read first.
>
> Keep this file only as a record of the observations. Do not treat any "DECISION" heading
> below as decided.

- **Status:** Retracted — observations only
- **Date:** 2026-08-02
- **Issues referenced:** ISSUE-002, ISSUE-006, ISSUE-010

---

## Context

The landing page makes this promise to every visitor:

> "A geometric-mean composite of retrieval, model self-report, and citation coverage gates
> every answer. Below 0.65: structured refusal, never fabrication."

This is the product's central trust claim. Until now it existed only as that sentence — the
ADR it links to was never written (ISSUE-023), so the contract behind it was undefined.

Three defects observed on 2026-08-02 trace to that single gap. They are not independent
bugs; they are three code paths each guessing at an undefined rule.

### Observation 1 — a resolved catalog record didn't count as a source (ISSUE-006)

`?q=tell me about Challenger&machineId=G50L9-MDxXD` returned the refusal card:

> Related machines I know about: **Challenger**
> Why I can't answer: **No indexed source could be linked to back up an answer here.**

The machine was resolved, named on screen, and still reported as unsourced.

### Observation 2 — the same record type *is* cited elsewhere (ISSUE-002)

A recommendation answer cited **8 OPDB "Metadata" records at 53% match** — including
"The Jetsons — Metadata", for a machine that never appeared in the answer.

So OPDB metadata records are first-class citable sources on one path and invisible on
another. Both behaviors cannot be correct.

### Observation 3 — the gate itself is inconsistent between turns (ISSUE-010)

| Path | Result |
|---|---|
| Fresh query: *"show me games similar to Medieval Madness"* | **Refused** — LOW CONFIDENCE |
| Same intent as a follow-up | **Answered** — 5 machines, 8 citations at 53% |

Identical information need. One path refuses; the other answers from weak matches. The
advertised guarantee has a hole in the conversational flow users spend most of their time in.

### Why a geometric mean makes this acute

A geometric mean is **zero if any single factor is zero**. A machine with a valid catalog
record but no manuals or rulesheets scores zero on document retrieval, which forces the
composite to zero and produces refusal — regardless of how well the catalog answers the
question. Observation 1 is exactly this shape.

That is a property of the chosen formula, not a bug in it. But it means **source eligibility
must be defined per question type**, or the formula will keep refusing questions the system
can answer.

---

## Decision

### D1 — Two distinct source classes, both citable

The system holds two stores. Both are legitimate sources; they answer different questions.

| Class | Contents | Authoritative for |
|---|---|---|
| **Catalog** | OPDB machine records (manufacturer, year, type, editions, IPDB refs) | Identity and metadata questions |
| **Corpus** | Manufacturer manuals, service bulletins, rulesheets, scraped rules guides | Rules, strategy, repair, specifications |

A citation rendered in the sources panel MUST come from one of these. This is already true
in practice — it is recorded here so both code paths agree.

### D2 — Question intent selects which class is required — **ACCEPTED**

| Intent | Required class | Example |
|---|---|---|
| Metadata / identity | Catalog sufficient | "tell me about Challenger", "who made Wonka", "what year" |
| Rules / strategy | Corpus required | "how do I get multiball", "wizard mode requirements" |
| Repair / technical | Corpus required | "diagnose the left ramp motor" |
| Valuation | Pricing partner required | "what is it worth" |
| Recommendation / similarity | Catalog sufficient, **per D4** | "games similar to X" |

Rationale: refusing *"tell me about Challenger"* when a complete catalog record is in hand is
the system failing at its easiest possible question. Conversely, answering a repair question
from a catalog record alone would be exactly the fabrication the gate exists to prevent.

**Consequence for the composite:** the document-retrieval factor must not zero out the score
for a metadata-intent question. Either the factor is computed against the *required* class
for that intent, or intent selects a different composite.

### D3 — The gate runs identically on every turn — **ACCEPTED**

Confidence MUST be computed fresh for each turn against that turn's own retrieved evidence.
A follow-up MUST NOT inherit, reuse, or be exempted from the parent turn's score.

Rationale: a guarantee that lapses after turn 1 is not a guarantee. This is the direct fix
for ISSUE-010. Note it will make the follow-up path *stricter* than it is today — the
five-machine recommendation in Observation 2 would refuse under this rule unless D4 also
changes, which is why the two are decided together.

### D4 — Recommendations require per-entity citations — **ACCEPTED**

For any answer that recommends or compares machines, **each machine named MUST have at least
one citation whose subject is that machine.** A machine that cannot be cited must not be
named. Conversely, a citation whose subject appears nowhere in the answer must not be shown
(this is what put "The Jetsons" in the sources panel).

Rationale: the sources panel is an implicit claim that the answer is grounded. A named
machine with no citation of its own is the strongest form of the failure this ADR exists to
prevent — it looks *more* trustworthy than an uncited answer.

#### Correction — what D4 does *not* fix

An earlier draft implied D4 would have prevented the Medieval Madness recommendation failure.
Re-reading the captured evidence, it would not have. The sources panel listed OPDB metadata
records for **The Hobbit**, **Halloween** and others — i.e. the recommended machines largely
*did* each carry a citation. D4's effect on that answer is limited to removing the stray
**The Jetsons** citation.

The actual defect there was that **53%-similarity metadata matches produced thematically
wrong recommendations** — three horror machines and a kids' game offered as "similarly
themed" to a medieval-fantasy comedy. That is governed by **D5**, not D4. D4 and D5 are
complementary, and D5 is the load-bearing one for recommendation quality.

This matters for sequencing: shipping D4 alone would make that answer *look* tidier (one
fewer irrelevant citation) while leaving the bad recommendations fully intact.

#### Deeper question this exposes

"Similar theme" may not be a vector-similarity problem over metadata records at all — an OPDB
metadata record encodes manufacturer, year and edition, not theme or gameplay feel. If so, no
relevance floor fixes similarity search; it needs a different signal (theme tags, rulesheet
text, or an explicit curated relation). Worth investigating before tuning D5's threshold, and
possibly worth its own ADR.

### D5 — Relevance floor — **DEFERRED pending measurement**

The instruction was to go with recommendations and defaults. For D5 the recommendation *is*
"measure first" — so that recommendation is what's adopted here, rather than a number.

Picking a threshold from a single observation would be guessing at a value the system then
enforces on every answer. Too high silently converts a working product into one that
constantly refuses; too low changes nothing. Neither failure announces itself, and the
number would be indistinguishable from a measured one once written down. That is the
specific reason this stays open while the rest of the ADR is ratified.

**Interim behavior until measured:** no floor is enforced. D4 (per-entity citations) and D3
(consistent gating) ship first and independently. The relevance score is already surfaced in
the UI ("53% match"), so weak sourcing stays visible to users in the meantime.

#### Measurement task (blocks this decision)

Run the Tier 3 golden set (10–15 questions spanning metadata / rules / repair /
recommendation / known-unanswerable) and record, per query:

1. Every retrieved citation's relevance score and source class (catalog vs. corpus).
2. Whether a human judges the resulting answer **good / weak / wrong**.

Then choose the floor where "wrong" answers fall away and "good" ones survive — and record
the observed distribution in this ADR so the number is traceable to evidence rather than
to intuition.

Known data points so far: **53%** (recommendation citations that produced a bad answer),
**65%** (the Medieval Madness rules document, which produced a good answer). Two points,
one of each class — suggestive, nowhere near enough to set a threshold on.

> Do not conflate this with the advertised **0.65 composite** threshold. That gates the
> geometric-mean *composite*; D5 is a per-citation *relevance* floor. Different quantities,
> and the numeric coincidence with 65% above is exactly the kind of thing that invites a
> wrong shortcut.

### D6 — Refusal copy distinguishes the failure modes — **ACCEPTED**

The refusal card currently says "No indexed source could be linked" for two different
situations. They should read differently, because the useful next action differs:

| Situation | Message |
|---|---|
| Machine known, no rules/repair docs | "I know this machine but have no rules or service documentation for it — the community forums below will have more." |
| Machine not known at all | "I don't have this machine in the catalog." |
| Question outside coverage | Current generic copy |

Also: the "Related machines I know about" list currently shows the machine that *was asked
about* (ISSUE-006). It should list genuinely related machines, or the label should change.

---

## Consequences

### Positive

- The three defects collapse into one contract, testable as invariants (see
  `docs/testing/regression-test-spec.md`).
- Metadata questions become answerable from the catalog, removing a class of false refusal.
- The public promise on the landing page becomes something a reader can verify.

### Negative / risks

- **D3 makes the follow-up path stricter.** Answers that work today will start refusing.
  This is correct — they were ungrounded — but it will read as a regression to anyone who
  doesn't know why. Ship D3 and D4 together, and land D6's clearer copy at the same time so
  the new refusals explain themselves.
- **Over-refusal is a real risk** and is *not* obviously better than over-answering for a
  community resource. A product that constantly says "I can't help" fails differently but
  still fails. D5's floor is the main dial here; it should be set from measured data.
- **Recommendation quality is not fixed by this ADR.** With D5 deferred, the failure that
  produced horror machines for a medieval-fantasy query remains open. D3 and D4 make the
  system honest about *what it cited*; they do not make it better at *choosing what to
  recommend*. Don't read a ratified ADR-0017 as closing ISSUE-002.
- Intent classification (D2) is itself a model call and can be wrong. A misclassified repair
  question routed as metadata would answer from a catalog record — the exact fabrication
  risk being guarded against. Intent classification needs its own low-confidence path.

### Follow-up work

- ADR-0021 and ADR-0022 are also linked publicly and still unwritten (ISSUE-023).
- The `0.65` threshold and geometric-mean formula are recorded here as advertised but have
  not been re-derived; if D2 changes how factors are computed, the threshold needs revisiting.
