# AI/ML Ideas Catalog — Future Phase Possibilities

> **Status:** Brainstorm catalog. None of the entries below are committed
> to a phase yet — they are documented so that when scope conversations
> happen, the option set is visible rather than re-derived from scratch
> each time.
>
> **Decision recorded:** 2026-05-02. The three starred candidates
> (§3) get deeper treatment as the strongest contenders for promotion to
> a locked phase alongside Dream Game ([concept](dream_game_concept.md))
> and Strategy Tracker ([concept](strategy_tracker_concept.md)).

## 1. Why this catalog exists

Information *about* pinball is not scarce. Manuals exist on manufacturer
sites, scores live on IFPA / Match Play, machines are catalogued on OPDB,
locations are mapped on Pinball Map, opinions fill Pinside, repair tips
fill YouTube and forums.

**The project's competitive moat is not the data — it's what AI uniquely
unlocks on top of the data.** Pattern recognition at scale, multimodal
reasoning over manuals + photos + audio + video, generative content
grounded in the real corpus, personalization that no static catalog can
deliver. Every feature in this catalog is evaluated against that lens:
*does this do something that AI uniquely makes possible, or is it a
wrapper around an existing data product?*

The two already-locked novel features (Dream Game, Strategy Tracker)
satisfy that test. So do the three starred candidates below. The rest of
the catalog ranges from "obviously nice and worth building someday" to
"interesting but maybe not differentiating." All are recorded — no idea
is lost — but the focus is on the three flagged for deeper evaluation.

## 2. Already-locked AI/ML features (for context)

| Feature | Status | Phase |
| --- | --- | --- |
| **Wizard Q&A (RAG with citations)** | Locked, foundational | Phase 4 |
| **OCR score capture** (camera → Vision LLM → score) | Locked | Phase 5 |
| **Dream Game generator** ([spec](dream_game_concept.md)) | Locked, Phase 5+ | Phase 5+ / v2 |
| **Strategy Tracker** ([spec](strategy_tracker_concept.md)) | Locked, Phase 5+ | Phase 5+ (Passport module) |

Everything below is **incremental** to those — adding to the moat, not
replacing it.

## 3. The catalog

Organized by capability category. Stars indicate strength of fit on the
(uniqueness × on-mission ÷ build-cost) axis. ★★ items get a deep-dive in
§4; ★ items in this section are also strong but get only an inline note.

### 3.1 Generative & creative

| Idea | Notes |
| --- | --- |
| ★ **Generative ruleset variations** | Take a real game's published ruleset and generate fan rule variants ("make Stranger Things harder," "convert Iron Maiden to co-op"). Wizard cites which rules were preserved/changed; outputs a printable rule card. Same RAG + creative-prompt pattern as Dream Game; no image gen needed. **Sibling of Dream Game; Phase 5+.** |
| Translite / cabinet art for licensed-IP what-ifs | "What would a Wu-Tang Clan pinball look like?" Pure visual gen. Same IP guardrails + image-cost concerns as Dream Game; would ride along as part of Dream Game's image tier. |
| AI callouts / voice acting samples | TTS / voice cloning for "imagine The Dude calling out 'Multiball!'" Voice cloning is the most legally fraught creative-output category. **Defer to v3+ at earliest** until IP policy is rock solid. |

### 3.2 Pattern recognition / computer vision

| Idea | Notes |
| --- | --- |
| _Score photo OCR_ | **Already in plan.** Camera → Vision LLM → score + machine identification. Phase 5. Foundation for several ideas below. |
| ★★ **Playfield video analysis — automated shot tracking from gameplay video** | Holy grail for Strategy Tracker. Phone clip → CV identifies shots / multiballs / modes started. Removes the last data-entry friction. **Deep dive in §4.A.** |
| **Wear / condition assessment from photos** | Buyer uploads photos of a used machine; AI assesses cabinet/playfield wear + missing parts + estimated cost-to-restore. Pairs with PinballPrices integration. Audience: buying/selling community. **Phase 5+ alongside Trade Matchmaker.** |
| ★ **Service bulletin diagnosis from photos / videos / audio** | "My machine is making this sound" or "my coil looks like this" → AI cross-references corpus of service bulletins + manuals + Pinside repair threads to suggest causes + which document section to read. Real time-saver vs forum-trawling. **Deep dive in §4.C.** |

### 3.3 Personalization & recommendation

| Idea | Notes |
| --- | --- |
| ★ **"Games you'd love" recommender** | Semantic + collaborative recommendations based on what user owns/plays/scores well on. Uses embeddings of game themes/rulesets/mechanics + collaborative signals from broader user base. Pinball Map tells you what's near; this tells you what's near *and* you'd love. **Phase 5+ once Passport has signal.** Cold-start problem is the main risk. |
| **Location-aware route planner** | "I'll be in Portland for 3 days; build me a pinball pilgrimage route." Composable from Pinball Map data + a prompt + user wishlist. **Phase 5; low cost.** |
| **Operator / route insights** | B2B feature for machine route owners: "which machines are underperforming relative to similar locations? which should I rotate?" Anonymized aggregate plays. **v3+; needs operator outreach.** |

### 3.4 Coaching / skill development

| Idea | Notes |
| --- | --- |
| ★ **AI pinball coach — personalized practice plans** | Given Strategy Tracker data + stated goals, generate a personalized practice plan (which shots to drill, which modes to focus, in what order). Cites both rule corpus and user's own data weaknesses. Competitive players pay actual money for human coaching. **Deep dive in §4.B.** |
| Live-during-play coaching (AR overlay) | Phone pointed at machine → real-time hints. Cool sci-fi demo, terrible v1: hardware/AR complexity, latency, distracts from play. **v∞.** |
| **Tournament prep — opponent scouting** | "I'm in a head-to-head with X next round; here's their public IFPA history, strongest games, weakest games, recent form." Rides along with IFPA integration. **Phase 5+ when IFPA lands.** |

### 3.5 Community knowledge synthesis

| Idea | Notes |
| --- | --- |
| ★ **Multi-source synthesis answer** | Extends Wizard Q&A from manufacturer-corpus-only to synthesizing across **manuals + service bulletins + Pinside threads + tournament data + YouTube transcripts (Whisper, deferred)**. "What's the meta strategy for Godzilla?" pulls from forums + tournament data + rules. **Phase 4–5, mostly already in plan; just needs source ingestion expansion.** |
| **Tournament narrative generation** | After a tournament, AI writes a narrative recap pulling scoring data + game choices + corpus context. Audience: tournament organizers / pinball media. **v2+; narrow audience.** |
| **"Explain this rule" — rules translation** | Rewrite dense rulesets at three levels (novice / intermediate / competitive), each cited back to the original rule sheet. **Phase 4; could be a default Wizard-Q&A response mode.** Trivial post-RAG. |

### 3.6 Anomaly detection / monitoring

| Idea | Notes |
| --- | --- |
| **Score legitimacy / fraud detection** | When public scores ship: ML model flags impossible/suspicious scores (mean +6σ, impossible mode-clears, photo-metadata mismatches). Lightweight community-trust signal. **v2+ alongside public scores.** |
| **AI-assisted source-site change detection** | Scrapers already detect HTML changes via hashes. AI extension: when a manufacturer page meaningfully changes (new game, restructured prices, new tab), AI summarizes "what changed and why it matters" instead of operator slogging through diffs. **Phase 4+; light cost; QoL win for project's own operations.** |

### 3.7 Pure-fun / community

| Idea | Notes |
| --- | --- |
| **Pinball Sommelier — game pairing** | "I just played Iron Maiden Premium for an hour; what should I follow it with?" Pairs by mechanical contrast / cooldown / theme. Pure conversational delight. **Phase 4 — Wizard tone variation; trivial cost.** |
| ★ **Pinball memory journal w/ AI narration** | User logs a few words about a session ("first ever 1B on Stranger Things, downtown Portland, with my dad"). AI weaves it into a journal entry with rich game/location context from the corpus. Over time builds a beautifully-written autobiography of the user's pinball life. **Phase 5+, Passport-adjacent.** Emotional/storytelling dimension nobody else is doing. |
| **Audio identification — "what game is this?"** | Smartphone hears the music/sound from a machine across the room → identifies game. Audio fingerprinting + corpus. **v3+; fun-but-niche.** |

---

## 4. Deep-dive — three top candidates

These three got deeper feasibility thinking. They are not yet committed
to a phase — just evaluated in enough depth to make promotion-or-not a
short conversation when the time comes.

### 4.A. Playfield video analysis (★★)

**One-liner:** Upload smartphone gameplay footage; computer vision
identifies which shots hit, multiballs reached, modes started — auto-
generating a session log without any manual entry.

**Why ★★:** This is the holy grail for the Strategy Tracker. Match Play
does some of this manually; nobody does it from raw video. True
zero-friction logging is the difference between a feature competitive
players *try* and one they *adopt*.

**Why it fits the architecture:**
- Same Vision LLM tier as the OCR score capture pipeline
- Same Cosmos session log writes as Strategy Tracker
- Output feeds Strategy Tracker analytics + AI Coach refinement
- Provenance ethos preserved: each detected event ("Demogorgon
  multiball started at 0:42") is timestamped to the source video frame

**Why it's hard:**
- Per-machine playfield layouts vary wildly. A vision model would need
  either (a) per-machine training data or (b) very strong few-shot prompts
  with a known playfield reference image. Neither is trivial.
- Frame-rate / camera-angle / lighting variability of phone footage.
- Cost: video frames at scale are expensive vs photo OCR. Quota-gating
  required just like Dream Game image gen.

**Plausible MVP shape:**
- Start with the **subset of machines** that have published playfield
  reference images (most modern Sterns do via promotional materials —
  already in our corpus). Match Play → top-25 most-played-in-tournament
  machines is a pragmatic starting list.
- User uploads 60 seconds max per session at MVP; longer footage = quota
  cost.
- Ship at "score + multiballs reached + modes started" granularity first.
  Per-shot tracking is v2 of this v2.
- Each detected event is **always presented with a confidence score**;
  user confirms/edits before the session log saves. AI assistance, not
  AI replacement.

**Decision criteria for promotion to a locked phase:**
- Prerequisite: Strategy Tracker is shipping and has demonstrated demand
- Prerequisite: a Vision LLM benchmark on 5–10 hand-labeled video clips
  shows ≥70% accuracy on score + multiball detection
- Cost ceiling fits inside the (then-current) monthly cap headroom

**Suggested phase:** **Phase 6+** as a research project that promotes to
a feature once accuracy + cost benchmarks pass.

---

### 4.B. AI pinball coach — personalized practice plans (★)

**One-liner:** Given Strategy Tracker data + the user's stated goals
("get above 1B on Godzilla," "place top 30% in next monthly"), generate
a personalized practice plan: which shots to drill, in what order, with
measurable check-in milestones.

**Why ★:** Direct extension of Strategy Tracker. Near-zero new
infrastructure. Competitive players pay for human coaching; AI coaching
grounded in their actual data + the rule corpus is novel and
differentiating.

**Why it fits the architecture:**
- Reuses Strategy Tracker data (sessions, strategies, analytics)
- Reuses Wizard router (creative + analytical sub-agent)
- Reuses AI Search retrieval over rule corpus
- Reuses MudBlazor Passport UI surface

**No new infrastructure.** The whole feature is an Application-layer
service + a prompt template + a new MudBlazor page.

**Output shape:**
- 2–4 week practice plan with daily/weekly drills
- Each drill cites (a) which rule it targets and (b) which weakness in
  user's session data motivated it
- Milestones: "after week 2, your median Demogorgon qualification rate
  should be above 60%" — measurable against ongoing session data
- Plan is a Cosmos document, versioned; user accepts/declines/customizes

**Why it's risky (and the mitigations):**
- LLM-generated practice plans could be generic / regurgitated. **Mit:**
  prompt requires every drill to cite specific user data + specific rule
  text, with the citation visible in the UI. No vague advice survives.
- Plans become stale as user improves. **Mit:** automatic re-evaluation
  every 2 weeks against new session data; UI suggests refresh.
- Risk of being seen as a coaching-marketplace competitor, which would
  push us into a different product. **Mit:** scope discipline —
  AI coach is a *companion* to human coaching, not a replacement; UI
  copy is explicit about this.

**Decision criteria for promotion to a locked phase:**
- Prerequisite: Strategy Tracker shipped with ≥3 months of user data on
  ≥some-bar number of users
- Strategy Tracker analytics are mature enough to feed coach prompts

**Suggested phase:** **Phase 5+** as a Strategy Tracker follow-on
release (call it Strategy Tracker v2 or Passport release 2).

---

### 4.C. Service bulletin diagnosis from photos / videos / audio (★)

**One-liner:** "My machine is making this sound" (audio upload) or "this
is what my coil looks like" (photo) → AI cross-references the corpus of
service bulletins + manuals + Pinside repair threads + YouTube repair
videos to suggest likely causes and which document section to read.

**Why ★:** Real time-saver vs current state (forum-trawling for hours).
Uses corpus we're already building — service bulletins are one of the
three planned scraper sources. Audience is the entire ownership /
operator community, not just competitive players. Could become the
single most-frequently-used feature.

**Why it fits the architecture:**
- Service bulletin scraping is already locked Phase 1 work
- Multimodal RAG is a natural extension of Phase 4 RAG (text+image+audio
  → text retrieval against the service-bulletin corpus)
- Same Wizard router, different prompt template

**Why it's *the most safety-sensitive* feature in the catalog:**
- Pinball repair involves AC line voltage, capacitor discharge,
  high-voltage transformers. **Bad advice can injure someone or burn
  down a basement.**
- This is exactly the case the locked architectural invariant addresses:
  *"I don't know" beats hallucination — manual wiring questions can be
  physically dangerous; threshold-driven refusal is non-negotiable.*
- Mitigations (locked from day one if this ever ships):
  - Hard confidence threshold; below it, refuse cleanly with "consult a
    qualified technician + here are the relevant service bulletins to
    read"
  - Every diagnostic answer **must** cite the specific bulletin or
    manual section it's drawing from — no synthesis without source
  - Visible, prominent disclaimer on every response: "AI suggestion,
    not professional repair advice"
  - Restrict scope: diagnosis + which-section-to-read is in scope;
    step-by-step repair instructions are **not** — those come from the
    bulletin itself, displayed verbatim with citation
  - Optional: gate behind "I'm a qualified technician / I accept these
    terms" acknowledgement

**Why it's expensive:**
- Audio + video processing costs more than text RAG
- Higher quality bar required (safety) means more expensive completion
  model (probably gpt-4.1 default, not gpt-4o-mini)
- Vision/audio LLM tier of Azure OpenAI needs to be provisioned and
  budgeted

**Decision criteria for promotion to a locked phase:**
- Service bulletin scraping is shipping and has produced a meaningful
  corpus
- Whisper transcription is no longer deferred (or YouTube auto-captions
  are good enough as a substitute) — needed for audio-clip ingestion +
  for ingesting repair video commentary
- Safety-review process is defined: who reviews diagnosis prompt
  templates, who signs off on the threshold, what happens if a
  user-reported incident traces back to AI advice
- Per-query cost benchmark fits the budget headroom

**Suggested phase:** **Phase 5+** for text-only diagnosis (photos +
manual cross-reference), **Phase 6+** for audio + video. Treat the two
tiers as separate features for sequencing purposes.

---

## 5. Selection criteria for promotion to a locked phase

When the time comes to pull an idea out of this catalog and into the
locked plan, the test is the same one applied to Dream Game and Strategy
Tracker:

1. **AI-uniquely-unlocks?** — Does this do something that AI makes
   uniquely possible, or is it a wrapper around an existing data
   product? If wrapper, no.
2. **Reuses existing architecture?** — New features that fit the
   existing Cosmos + AI Search + Wizard router pattern have a much
   lower bar than features requiring new infrastructure.
3. **On-brand provenance?** — Does the feature output cite its sources
   in the way the rest of the platform does? Citations are the soul of
   every Wizard output; features that can't cite shouldn't ship.
4. **Cost fits the cap?** — $400/mo cap is locked. Image / video / audio
   features need quota / metering / opt-in design from day one.
5. **Safety lens applied?** — Anything touching repair / wiring / health
   gets the threshold-driven refusal treatment. No exceptions.
6. **Scope-disciplined?** — Each feature has a tight v1 surface and an
   explicit "out of scope for v1" list. No features that creep into
   adjacent product categories.

Anything in this catalog that doesn't pass all six tests stays in the
catalog. That's what the catalog is *for*.
