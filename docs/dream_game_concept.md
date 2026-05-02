# Dream Game — Phase 5 Marquee Feature (Concept Spec)

> **Status:** Documented for future implementation. **Phase 5+** (lands with
> the Blazor frontend at earliest; can be deferred to post-launch v2 if
> scope pressure demands). Not in scope for Phase 1.x or any work prior to
> the RAG pipeline being live.
>
> **Decision recorded:** 2026-05-02. See project memory
> `project_dream_game_concept.md` for the locked guardrails.

## What it is

A user-facing "Dream Game" generator. The user describes a pinball machine
that doesn't exist but they would love to play — as much or as little input
as they want — and the platform produces a full conceptual design:

- **Theme & narrative** — story, mode structure, character/lore mapping
- **Playfield concept** — top-down layout description, major shots and
  loops, ramp/orbit topology, target arrangement (descriptive + sketched,
  not CAD-quality)
- **Mechanisms ("mechs")** — physical toys, magnets, drop targets,
  spinners, multi-level playfields; each grounded in real precedent from
  the corpus
- **Ruleset** — modes, multiballs, wizard mode, scoring structure
- **Artwork concepts** — playfield art direction, cabinet art, translite
  art (style/abstraction, not direct character likenesses — see IP
  guardrails below)

**Worked example.** "My favorite band is Phish. Phish has a rock opera
called Gamehenge. My dream game is Phish-themed with Gamehenge as the
narrative." → the agent generates modes around the Gamehenge characters
(Wilson, Colonel Forbin, the Lizards), a Helping Friendly Book multiball,
a setlist-driven scoring system, and a playfield concept with mechs
inspired by real Stern designs from the corpus.

The user iteratively refines: "make the wizard mode harder," "the cabinet
art should feel more psychedelic-70s than modern," "swap the magnet for a
diverter," etc.

## Why it fits the architecture (this is the elegant part)

The Phase 2 RAG infrastructure (Azure AI Search Basic + Cosmos + Azure
OpenAI completion routing) is *exactly* the substrate Dream Game needs.
Same retrieval pipeline, different prompt template:

| Mode | Retrieval | Generation |
| --- | --- | --- |
| Wizard Q&A | catalog chunks → prompt | answer with citations |
| Dream Game | catalog chunks → prompt | novel design that *cites the analogues* |

Grounding generation in the real corpus keeps the **provenance ethos
intact** instead of departing from it. Every Dream Game output should
include "your Helping Friendly Book multiball is structured like
Stranger Things' Upside Down multiball [link]" style references back to
real Stern designs that informed the generation. This is what makes the
feature on-mission rather than a tacked-on creative toy.

## Guardrails (locked from day one)

These are not optional. If image generation cannot be safely metered, ship
text-only first. If IP framing cannot be made airtight, gate the feature
behind login + acceptance of terms.

### Cost guardrails

- **Text-first MVP.** Initial release ships text-only output: theme,
  narrative, playfield description, mech list, ruleset, art *direction*
  (not generated images). Text generation is cheap (~$0.01–0.10/session
  with `gpt-4o-mini` / `gpt-4.1` routing — a rounding error against the
  $400/mo cap).
- **Image generation is opt-in and quota-gated.** When images are added
  (DALL-E 3, Flux Pro, or whatever the right model is at the time), they
  are:
  - Behind a **per-user monthly quota** (e.g., free tier: 0 images;
    authenticated tier: 3–5 generated images/month; paid tier if
    introduced: higher)
  - Behind **Entra External ID login** (already locked for v1) — anonymous
    Dream Game requests get text only
  - Throttled per session and per IP at the Cloudflare layer
- **Hard budget allocation.** Image generation gets its own line item in
  the cost cap. If the monthly budget is hit, image generation degrades
  gracefully to "quota exhausted, text design still available" instead of
  blowing past the $400/mo ceiling.

### IP / copyright guardrails

- **Fan concept framing.** Every Dream Game output is labeled "Fan
  concept — not a product, not for commercial use" prominently in the UI
  and in any export/share artifact.
- **Terms of service acceptance** before generating. ToS makes clear:
  user-supplied themes invoking copyrighted properties (bands,
  films, games, characters) are at the user's discretion; generated
  artwork uses *style / abstraction* and is not a likeness of any
  copyrighted character; outputs are not licensed for commercial use; the
  platform may decline to generate for properties it considers high-risk.
- **Style-not-likeness for art.** Image prompts bias toward art *direction*
  (color palette, era, mood, composition) rather than literal depictions
  of trademarked characters or copyrighted imagery. "Psychedelic 70s rock
  opera energy" is fine; "the Helping Friendly Book as drawn by Trey
  Anastasio" is not.
- **Decline list.** A small denylist for the most legally fraught
  properties — refuse cleanly with "we don't generate concepts for X;
  here's a list of community-friendly themes you might love instead."

### Scope guardrails

- **Not before Phase 5.** Dream Game depends on the RAG pipeline being
  live (Phase 4) and the Blazor frontend being functional (Phase 5). Do
  not start implementation prior to those.
- **Phase 5 vs post-launch is a real call.** Dream Game can be a v1
  marquee feature alongside the public Blazor launch, OR it can be
  deferred to a v2 release after the platform has shipped and proven its
  baseline. The decision should be made when Phase 4 lands and we know
  what the budget headroom and content-corpus quality actually look like
  in practice.

## Cost model (rough order of magnitude)

| Mode | Per-session cost | Monthly impact at 100 sessions/mo |
| --- | --- | --- |
| Text-only design (Sonnet/Haiku routing, ~5–10 prompts incl. refinements) | ~$0.05–0.20 | ~$5–20 |
| Text + 3 images (DALL-E 3 standard, $0.04/image) | ~$0.15–0.30 | ~$15–30 |
| Text + 5 images (Flux Pro, $0.05/image, with refinement re-rolls) | ~$0.50–1.50 | ~$50–150 |

Image generation dominates the cost equation at any meaningful scale. The
quota system is what keeps this feature inside the $400/mo cap.

## Implementation sketch (when Phase 5 arrives)

This is forward-looking, not a build spec — actual implementation will
revisit assumptions when it's time.

- **Application layer:** new use case `GenerateDreamGameAsync` in
  `PinballWizard.Application/`. Takes a `DreamGameRequest`
  (theme description, optional refinements, optional image generation
  flag) and returns a `DreamGameDesign` (structured: theme, narrative,
  playfield, mechs, ruleset, art direction, citations to corpus
  analogues).
- **AI Router:** reuses the same Semantic Kernel router that handles
  Wizard Q&A. Adds a "creative generation" sub-agent with a different
  prompt template but the same retrieval interface against AI Search.
- **Refinement loop:** Cosmos document per Dream Game design with version
  history; user refinements append to the version chain so design
  evolution is auditable and resumable.
- **Frontend (MudBlazor):** wizard-style multi-step UI (`MudStepper`),
  rich design view (`MudPaper` + tabs for narrative / playfield / mechs /
  rules / art), citation chips back to real games in the corpus.
- **Image generation worker:** ACA Job or Function triggered on
  user-initiated image render. Quota check against Cosmos
  `dream_game_quotas` container before invocation. Failed quota check
  returns a clear "you've used your monthly image budget; here's when it
  resets" response.

## What this feature should *not* become

- Not a substitute for the RAG-backed Wizard. The chat answering "how
  does Stranger Things multiball work?" stays the headline product.
- Not a commercial product mockup tool. Output is fan creative work.
- Not a generator that depicts copyrighted characters. Style and mood,
  not likeness.
- Not a free-tier resource sink. Generation costs are real; quotas are
  not negotiable.

## Open questions for Phase 5 design discussion

1. Is the image generation provider Azure OpenAI (DALL-E 3 via Azure), an
   external API (OpenAI direct, Stability, Black Forest Labs / Flux), or
   model-routing across them? Cost and IP-policy posture differ.
2. Does Dream Game output get its own Cosmos partition, or live alongside
   user passport data?
3. Is the refinement experience conversational (chat with the design) or
   form-based (edit fields in the structured output)? Likely both.
4. Public showcase: do users opt in to a "community Dream Games" gallery?
   Surfaces the marquee feature, but every published design needs the
   ToS / IP framing to apply visibly to its public form too.
