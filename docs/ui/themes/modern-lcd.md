# Modern LCD — Default Theme (Brainstorm v1)

> **Status:** Iterating on `Dev-WebUiThemesBrainstorm`. Not yet a committed design system.
> Locked decisions and open questions both live here. Update in place rather than spawning sibling docs.

## Thesis

The Wizard's UI should feel like *the pinball hobby itself* — the universal grammar of cabinets, DMDs, flipper buttons, score reels, lockdown bars — not Stern's brand, not any one game's art package. Themes are user-selectable visual skins that evoke an *era* or *form factor* of the medium. Even when Stern is currently the only data source, the chrome looks like it came from the same workshop that built every machine on every route.

**Modern LCD** is the default because it is the only theme flavor that reads as *contemporary* rather than *retro*. Every other candidate (DMD Classic, Score Reel, Cabinet, Backbox) telegraphs nostalgia or hobbyism. Modern LCD says "this is what 2026 pinball looks like" — which is what a sceptical prospect needs to read off the page in the first three seconds.

Other themes will be derived from the structure Modern LCD locks in. Until that structure is nailed, no derivative work starts.

## Posture — community resource, not destination

> **PinballWizard is a community resource. It routes traffic to source and community sites; it does not capture users.** Every interaction is an invitation to leave the Wizard and read the original — on Pinside, OPDB, IFPA, the manufacturer's own page. The default user journey is: ask question → read grounded answer → click out to a source. **A short session is a successful session.**

This is the same posture as polite-by-construction scraping (locked invariant 2), expressed at the UX layer: respect the upstream, send traffic home. It is the strategic differentiator vs. every generic RAG demo, which optimizes for engagement metrics and treats sources as fine print. The mechanics-not-IP theme thesis above is the *visual* manifest of the same role: the tool belongs to the whole hobby, not allied with any one brand or game.

Four rules this posture generates — they govern every design decision in this doc:

1. **Outbound is a feature, not friction.** Every cited source has a prominent outbound CTA. Outbound link affordances look *generous*, never stingy. The right flipper button (`VIEW THE ORIGINAL ▶`) is the locked hero example; the principle scales beyond it.
2. **Inline references are portals.** Machine names, manufacturer names, tournament references, document names in answer body all carry outbound links. Visual treatment is quiet (subtle ↗ icon, `accent-grounded` underline on hover) so paragraph readability survives — but the affordance is consistent and discoverable.
3. **Refusal directs out, too.** When confidence-threshold refuses to answer (ADR-0017), the refusal panel routes the user to community resources that *can* answer — Pinside thread on the relevant machine, IPDB/OPDB entry, manufacturer support page. The Wizard's "I don't know" becomes "but here's where to ask."
4. **Avoid any appearance of favoritism — visual treatment is the carrier.** Peer destinations get visually equal CTAs. No "primary destination" button styled differently from "secondary destination" within a plural set; they are siblings, not parent/child. Citations from every manufacturer render with identical treatment regardless of how much of the corpus comes from them. Coverage gaps are named honestly in refusal text rather than papered over. Per `feedback_avoid_appearance_of_favoritism.md` and the [Avoiding the appearance of favoritism](../../community-resources.md#avoiding-the-appearance-of-favoritism) section of the contract doc — which together generate destination plurality (which venues we surface) and coverage transparency (what we admit to having). This theme doc carries the *visual* expression: peer parity, identical citation treatment, honest refusal framing.

The system-level contract these rules build against — destination directory, resolution order per entity type, refusal-routing matrix, link-health policy — lives in [`docs/community-resources.md`](../../community-resources.md). This theme doc covers the *visual rendering* of those destinations; the contract doc covers *which destinations exist and when each is right*.

**Anti-patterns** (forbidden by this posture):

- Any feature that optimizes for session length, return visits, in-app retention, or "stickiness." Engagement-metric framing works against the principle.
- Walling content behind disclosures, tabs, or expanders that hide the path out. Sources, citations, and outbound links all stay full-fidelity by default.
- "Read more" patterns that re-route users to *more Wizard pages* instead of source sites. The Wizard is a router, not a destination.
- Any onboarding that asks for an email/login *before* the user has experienced the route-out journey. The first interaction must end at a community site, not at a sign-up form.
- **Visually favoring one destination over its peers within a plural set** — bigger button, more saturated color, higher placement, more decorative treatment, or any other visual cue that telegraphs preference. Within a routing-recommendation stack, peer destinations are visually identical.
- **Refusal text that implies the venues we cover are *better* than what we don't.** Refusal language names coverage gaps honestly ("we don't have direct sources for [X]") — never "this is out of scope" without explanation, never language that subtly steers users toward in-coverage alternatives.
- **Citing some manufacturers more visually prominently than others.** Even though Stern dominates the scraped corpus, every manufacturer's citation card renders with identical treatment. The corpus volume disparity is what we have; the visual treatment is what we choose.

## Flavor — locked

Three flavors of "modern LCD pinball" exist in the wild. We lock the foundation and the accents independently:

| Flavor | Reference | Role |
| --- | --- | --- |
| **App-native** | Multimorphic P3, modern Stern Insider Connected | **Foundation.** Generous negative space, LCD-as-information-surface. Carries the long-form Q&A. |
| **Broadcast** | Stern Godzilla, Jurassic Park, Foo Fighters | **Accents.** Punchy, sports-graphic confidence. Used at the moments that matter — answer reveal, citation appearance, mode-start when drilling into a topic. |
| **Cinematic** | JJP Wonka, Godfather, Elvis | **Occasional flourish.** Hero/landing surface, empty-states, transitions between major sections. Never the workhorse. |

This composition is the answer to the central tension: pinball LCDs show *short bursts*; the Wizard shows *paragraphs with citations*. App-native makes the paragraphs readable. Broadcast makes the answer feel *announced*. Cinematic gives the system room to breathe at moments when nothing else is happening.

## Citation as hero — the central UX object

The Posture section above establishes the *role* (community resource that routes outward). Citation cards are the *primary surface* through which that role is performed. They are also where Provenance-is-sacred (locked invariant 1) becomes visible to the user — the fidelity of the chain (scraper → catalog → chunker → vector index → answer) is the whole AI-story differentiator, and if the citation surface looks like an academic footnote, the differentiator goes invisible.

So: every answer ends with a stack of citation cards. Each card is a dedicated panel — full-width on mobile, generous on desktop. **The cards are the loudest objects on the page after the answer body itself.** They are not a footnote. They are not collapsed behind a disclosure. They are the playfield's biggest jackpot insert.

### Citation card anatomy

Each card carries six slots, in vertical order:

| Slot | Treatment |
| --- | --- |
| **Source-type tag** (`MANUAL` / `BULLETIN` / `GAME PAGE` / `OPDB`) | Small display-type pill, `accent-grounded` border + glow. Reads at a glance. |
| **Source identity** | Display-type header. Manufacturer + machine + document name. The "marquee" of the card. |
| **Excerpt** | Body type. The actual quoted span that grounds the claim. Left-bordered with `accent-grounded` to reinforce "this is what the source said." |
| **Provenance trail** | `text-secondary`, mono for the URL chain. Discovery URL → file URL, breadcrumb-style. Whisper, not shout — but always present. |
| **Timeline** | `text-secondary`, body. "Discovered X · Last verified Y." Builds trust by showing the system stays current. |
| **Flipper-button pair** | The hero CTA. See below. |

### Flipper-button CTA pair

The flipper buttons are the tactile anchor of the citation card — and the most distinctly *pinball* element in the entire UI. They evoke the actual cabinet buttons: rectangular pucks, slightly recessed into the cabinet face, backlit when in use, mechanically clicky.

**Pair semantics:**

| Button | Action | Backlight |
| --- | --- | --- |
| **Left flipper — `◀  VIEW IN ANSWER`** | Scrolls/highlights the inline citation marker in the answer body that cites this source. The "where did this come from in the response" affordance. | `accent-mode` — magenta. This is a navigation/mode action. |
| **Right flipper — `VIEW THE ORIGINAL  ▶`** | Opens the canonical source URL in a new tab. The strategic CTA — sends the user to the source site. | `accent-grounded` — atomic green. This is a confirmed-source action. The whole point of the system. |

**Visual treatment:**

- Rectangular with ~6px radius (more rounded than panels because real flipper buttons are more rounded). About 44–56px tall — finger-sized, not hairline.
- Inset look: subtle dark shadow inside the top edge to suggest the button is recessed into the cabinet face. The button face itself sits slightly below the cabinet plane.
- Backlit appearance: accent color washes the button face at low intensity at rest, peaks on hover, flares on press.
- Display-type label, ALL CAPS, condensed. The label leans toward the screen edge — left flipper has the arrow icon left of the text, right flipper has it right of the text. No rotation, no visual angle gimmicks; the icon placement does the work.
- One allowed motion exception: a 1–2px depression on click. Pinball flipper buttons literally depress when struck — the tactile click is the whole point. This is the only place "press motion" is allowed; everything else stays flat.

### Inline citation markers (in answer body)

The marker in the answer text is *minimal*. The card below is the hero; the inline marker is just a wayfinding anchor. Two candidates considered:

| Option | Look | Verdict |
| --- | --- | --- |
| **A — Numbered superscript** (`...persists after switch test passes¹.`) | Compact, conventional, academic. | ❌ Loses source identity. User must look down to know what `¹` is. Reads as footnote — undermines the hero treatment of the card. |
| **B — Named pill** (`...persists after switch test passes [GODZILLA SB-243].`) | Telegraphs source identity inline. Reads naturally. | ⚠️ Heavy — long pills break paragraph rhythm at 3+ citations per answer. |
| **C — Pinball-insert numbered marker (locked)** | Small circular or diamond-shaped insert with a number, styled like the lit plastic playfield inserts. `accent-grounded` glow. Hover reveals source name; click scrolls to the citation card and pulses its border. | ✅ Distinctly pinball, compact enough not to break paragraph rhythm, telegraphs "this is something to interact with" via the insert glow. The numbering still ties to the cards (which are also numbered for reference). |

**Locked: Option C.** Inline citations render as small numbered pinball-insert markers with `accent-grounded` glow. Hover reveals a tooltip with `[SOURCE TYPE]  Source name`. Click scrolls to and pulses the matching citation card.

This means the inline citation is just an anchor — small, glowing, recognizably *not* a footnote, but visually quiet enough that the answer body stays readable even at 5+ citations. The work happens at the card.

### Many-citations behavior

When an answer cites many sources, the card stack stays full-fidelity (every card shows the flipper pair) with a brief summary header above the stack — e.g. `SOURCES  ·  5 cited from 3 sites`. We resist the temptation to collapse cards behind a "show all sources" disclosure. The Posture section's anti-pattern list governs this directly: walling content behind expanders that hide the path out is forbidden. If the stack gets long, the answer was well-grounded, and that's the point.

### Outbound links in answer body

The card stack is the loud outbound surface. The answer body itself carries a quieter, parallel outbound layer: every reference to a *real entity* in body text is a portal.

The mapping of entity type → destination(s) lives in [`docs/community-resources.md`](../../community-resources.md) (the system contract). The visual treatment for the inline portal is uniform regardless of entity type:

- **Single primary destination per inline reference.** The contract returns a priority-ordered list; the inline portal binds to position 1. We do not render multiple links per inline mention — that breaks paragraph rhythm.
- **Quiet by default.** A subtle `accent-grounded` underline that brightens on hover. Small trailing `↗` icon (the universal "leaves the site" affordance) at the same color as the underline.
- **Hover reveals destination identity.** Tooltip shows the destination name (e.g., "Stern Pinball — official game page" or "OPDB entry"). The reader knows where the click will take them before clicking.
- **Optional secondary destinations via context menu / long-press.** When the contract returns multiple destinations for an entity, secondary destinations are reachable via right-click / long-press menu. Discoverable, not invasive.

Inline portals are not citations — they're *portals*. A reference may appear as a portal in body text and again as a citation card below; that's not duplication, that's two different routes to the same source surface. The body-text portal serves the reader who's skimming; the citation card serves the reader who wants to evaluate the grounding.

The treatment is deliberately quieter than the citation card flipper buttons because body text has to stay readable at paragraph length. The principle is "outbound is generous, not stingy" — but generous on the dedicated outbound surfaces (cards, refusal panels), discoverable but understated in body.

### Refusal that directs out

When the Wizard refuses (confidence-threshold per ADR-0017), the refusal panel does not just say "I can't answer." It routes the user to where they *can* get an answer. The destination set comes from the **refusal-routing matrix** in [`docs/community-resources.md`](../../community-resources.md#refusal-routing-matrix), which maps `(refusal category × question topic)` to an ordered list of community destinations using the closed 6-value question-topic enum (`repair` / `gameplay` / `market` / `location` / `tournament` / `general`).

#### Layout

The refusal panel sits in the same slot the answer panel would have occupied — full-width on mobile, generous on desktop. Top-down composition:

1. **Category label** as a confident broadcast callout. Display type, ALL CAPS, ~32px+ on desktop. `accent-refusal` border on the panel. Reads like a pinball callout (`TILT`, `BALL SAVED`, `MATCH AWARDED`) — declarative, not apologetic.
2. **Honest reason** in body type below. One sentence. No apology language ("Sorry, I couldn't..."), no hedging ("I might be wrong, but..."). Just states why the threshold wasn't met.
3. **Routing recommendations** as a stack of 2–3 outbound CTAs (see below). Sits below the reason with generous spacing — these are the actionable affordance, they should breathe.

#### Routing-recommendation CTA spec

The routing CTAs use the same recessed-cabinet-button family as the flipper buttons and the inline citation markers, but at a third size point — preserving the hero hierarchy of the citation-card flippers while staying coherent with them visually.

| Property | Spec |
| --- | --- |
| Shape | Recessed puck, ~36–40px tall (vs. flipper's 44–56px). Auto-width to label with consistent padding. |
| Backlight | `accent-grounded` — these are confirmed-grounded outbound destinations to community resources, same family as the right flipper. |
| Label | Display type, ALL CAPS, destination name + trailing `▶` icon. Per the open-questions resolution: name the destination, no verb prefix (`PINSIDE TECH FORUM ▶`, not `OPEN PINSIDE TECH FORUM`). |
| Press motion | 1px depression (vs. flipper's 1–2px). Smaller depression preserves hierarchy — these are still tactile-feeling, but quieter than the citation-card hero CTA. |
| **Peer parity** | **All routing-recommendation CTAs within a single refusal panel are visually identical** — same size, same padding, same backlight intensity, same label treatment. No "primary" CTA visually elevated above its peers. Per Posture rule 4 (avoid the appearance of favoritism). The order they appear in matches the contract's plural-set ordering convention (alphabetical or randomized; never editorially curated unless the contract documents a contextual rule). |
| Mobile | Stacked vertically, full-width. Order matches the contract's ordering convention. |
| Desktop | Side-by-side if 2–3 fit comfortably; otherwise stacked. **Order matches the contract's ordering convention regardless of layout.** |

#### Per-category framing

| Category | Category-label phrasing | Reason-text shape | Routing source |
| --- | --- | --- | --- |
| `LOW_CONFIDENCE` | `LOW CONFIDENCE` | "The available sources don't directly address this question." | Matrix row for the question's topic |
| `OUT_OF_SCOPE` | `OUT OF SCOPE` | "This question is about [topic], which the Wizard's current sources don't cover." | Topic-matched destinations from the `LOW_CONFIDENCE` rows (framing differs, routing doesn't) |
| `CONFLICTING_SOURCES` | `CONFLICTING SOURCES` | "Two cited sources give different answers. Both are shown above." | The conflicting citations remain as cards above + Pinside thread search recommendation |

For `CONFLICTING_SOURCES`, the panel shape is slightly different: the two citation cards stay in their normal slot (full-fidelity flipper-buttons-and-all), and the refusal panel sits *below* them with the framing "the Wizard refuses to choose between these — the community can." This is the strongest expression of the posture: the Wizard explicitly defers to the community to resolve disagreement.

The refusal panel is, in some ways, the purest expression of the Posture. The visual treatment honors that by making the routing recommendations large enough to be the first thing the eye lands on after the category label — they aren't a consolation prize, they're the answer.

## Visual system

### Palette (directional — hex values are placeholders pending a11y pass)

A small palette where each color *means something*, and the meaning persists everywhere.

| Token | Direction | Semantic role |
| --- | --- | --- |
| `bg-base` | Near-black with subtle warmth (`#0c0b0e` range, NOT pure `#000`) | The LCD bezel. Pure black reads as "phone OLED" not "machine in a dim arcade." |
| `bg-surface` | Subtle lift from base (`#161519` range) | Panel interiors, cards, info zones |
| `bg-surface-hi` | Further lift (`#1f1d22` range) | Hovered/active panels, citation cards |
| `text-primary` | High-luminance off-white with slight warmth (`#f4f1ea` range) | Body text. NOT clinical white — clinical reads as "medical app." |
| `text-secondary` | Lower-luminance neutral (`#9a9590` range) | Labels, metadata, timestamps |
| `accent-primary` | **Amber/orange** (`#ff9a1f` range) | Primary action — submit, confirm, "ask the Wizard." The single most "pinball" color in the world. Score reels → DMD → modern LCD all carry it. |
| `accent-grounded` | **Atomic green** (`#34d96a` range) | Citations, source-grounded answers, success. The "match award lit" GI glow. Cyan was tempting but reads as "tech," not pinball. |
| `accent-refusal` | **Saturated red** (`#ff3b30` range, NOT crimson) | Confidence-threshold refusal, validation errors. Distinct from system errors. |
| `accent-mode` | **Magenta** (`#e13bd9` range) | Mode/topic context, "you are now in machine X" navigation state. Appears across multiple modern games (Foo Fighters, Heist, Wonka) so reads as natively pinball. |
| `border-quiet` | 1px, low-luminance (`#2a282d` range) | Default panel borders, dividers. Never the Material 12%-opacity wisp. |
| `border-glow` | 1px, accent-tinted on hover/active | Border treatment that pulses on interaction. The signature "this is alive" detail. |

**Rules:**
- Color is *never* the only carrier of meaning. Every accent is paired with an icon, a label, or a structural cue.
- Every accent ↔ background combination passes WCAG AA (4.5:1 body, 3:1 large text). Where a placeholder fails, the placeholder moves, not the rule.
- The palette is closed. Adding a sixth semantic accent requires deleting one — accent inflation kills the "color means something" promise.

### Typography

| Role | Direction | Notes |
| --- | --- | --- |
| **Display — primary** | **Barlow Condensed 700** (locked 2026-05-08) | Headers, panel titles, flipper-button labels, refusal-panel category labels. Open-source (OFL), broadcast-condensed feel without the overused-blog-headline associations of Oswald/Bebas. Spike confirmed: clears every hard requirement (small-size legibility, full numerics, weight range, no overuse baggage) while staying inside the brief's brand zone. Saira drifts to gaming-HUD; Oswald is overuse-disqualified; Anton fails the 12–14px caps test. |
| **Display — secondary** | **Barlow Condensed 500** (locked) | Citation-card source identity, smaller panel headers — anywhere display weight 700 reads as too aggressive. Same family, lighter cut. |
| **Body** | Inter | Most readable grotesque on screen, ubiquitous, free, supports tabular figures. IBM Plex Sans is the alternate if Inter feels too "SaaS-default." |
| **Mono** | JetBrains Mono | Citation IDs, machine slugs (e.g. `mch_a1b2c3d4...`), technical data. The mono is the *only* DMD-nod we keep — it whispers "dot matrix" without dressing up as one. |

**Rules:**
- Display is reserved for *announcements* — page titles, panel labels, score-broadcast moments. Not body. Condensed-sans body is fatiguing.
- Tabular figures (`font-feature-settings: "tnum"`) applied site-wide on score-style numerics — counts, percentages, dates. Not opt-in per element. Pinball numbers don't jitter.
- Two display weights (700 primary, 500 secondary), one body weight family (400/500/600/700), one mono weight (400/500). Weight inflation is style inflation.
- **Future upgrade path** (not for v1): if Barlow ever feels too humanist, **DJR Forma DJR Banner** is the closest "broadcast LCD" upgrade (commercial, ~$200 Display Web license); **Klim Söhne Breit** is the prestige pick. Both rejected for now on cost-discipline + open-source-posture grounds. Revisit only if a future design pass identifies a concrete shortcoming Barlow can't solve.

### Panel grammar

- **Borders:** 1px solid `border-quiet` default. `border-glow` (accent-tinted, brief pulse) on hover/focus.
- **Corners:** 2px radius. Pure 0px reads "Atari brutal"; 8px+ reads "Material." 2px is "machined edge."
- **Spacing:** 8px base unit. Tight within zones (8–16px), generous between zones (24–32px). The whitespace between panels is what makes app-native readable; eat into it and we drift to gaming-HUD.
- **Dividers:** 1px solid, visible (not the wisp). Used to separate sections within a panel — citation list items, machine-detail tabs.
- **Elevation:** *Almost none.* Modern LCD pinball is flat-on-glass. We use background-tone shifts (`bg-surface` → `bg-surface-hi`) to indicate depth, not box-shadow. One exception: the answer panel may carry a very soft outer glow (accent-tinted, 4–8px blur, low opacity) at reveal time, fading to nothing within 600ms.

### Motion vocabulary

The screen is never truly still — but motion is *signal*, not decoration.

| Moment | Motion |
| --- | --- |
| **Question submit** | Submit button briefly fills with `accent-primary` glow, then dims as the request fires. Input field border pulses once. |
| **Loading (between submit and reveal)** | Answer panel placeholder appears with a slow pulse on its `border-glow` — not a spinner, not a progress bar. The pulse rate (~1.5s) signals "the machine is working, not stuck." |
| **Answer reveal** | Text counts in fast (no character-by-character cuteness — full block fades in over ~150ms). Citation tags appear with a 50ms stagger and a brief glow pulse on each. |
| **Citation card hover** | `border-glow` lights in `accent-grounded`. Flipper buttons' backlight intensifies from rest to peak. |
| **Inline citation marker hover** | The numbered pinball-insert marker pulses once and reveals a tooltip (`[SOURCE TYPE]  Source name`). |
| **Inline citation marker click** | Page scrolls smoothly to the matching citation card; the card's `border-glow` pulses twice in `accent-grounded`. |
| **Flipper-button press** | The button face depresses 1–2px and the backlight flares to peak luminance, then both settle back as the action fires. The *only* allowed "tactile click" motion in the system — earned because real flipper buttons literally depress. |
| **Mode-start (drill into a topic / machine)** | Rest of the UI dims briefly (~200ms to 60% opacity), the new content's panel border pulses once in `accent-mode`, then UI returns to full opacity. This is the broadcast moment. |
| **Refusal** | Distinct from error. Refusal panel slides in from below, `border-glow` in `accent-refusal`, stays static (no pulse — refusal is a deliberate outcome, not a process). |

**Rules:**
- Honor `prefers-reduced-motion`: pulses become static states, mode-start dim collapses to a crossfade, count-in becomes instant. The visual hierarchy must still work without motion.
- Forbidden: rotation, bounce, spring physics, hover-grow, scale-up wobbles, parallax. Pinball LCDs do not bounce. Motion is purposeful HUD behavior. The flipper-button press depression is the one earned exception (see above) — and it is *press*, not *hover*.
- Total motion budget per interaction: ~600ms ceiling. Anything longer feels like the system is showing off rather than responding.

## Surface inventory

The theme's job is to dress these specific surfaces. Each one is designed in context, not in the abstract.

| Surface | Role | Theme treatment |
| --- | --- | --- |
| **Question input** | The "drop in coin" surface | Single bright input field, `accent-primary` submit button. Persistent, never collapses. The thing the user always knows how to find. |
| **Answer panel** | The "playfield action callout" surface | Generous, readable, paragraph-friendly. Citations as inline-tags + end-list. Soft accent glow at reveal, fading. |
| **Citation cards** (the hero — see [Citation as hero](#citation-as-hero--the-central-ux-object)) | The strategic centerpiece. Every answer ends with a stack of full-fidelity cards — source-type tag, identity, excerpt, provenance trail, timeline, and the flipper-button CTA pair. The cards are the loudest objects on the page after the answer body itself. Resist any pressure to collapse them behind a disclosure. |
| **Machine detail view** | The "instant info" / per-machine surface | Title + manufacturer + year as score-broadcast-style header (display, large, condensed). Tabs for Manual / Bulletins / Specs / Provenance. Tab switch uses mode-start motion. |
| **Refusal state** | When the Wizard can't confidently answer (ADR-0017) | Categorized refusal panel, `accent-refusal`, named refusal category, suggested next step. Never apologetic, always actionable. |
| **Loading state** | Between question submit and answer reveal | Placeholder answer panel with pulsing `border-glow`. No skeleton text — skeleton text reads as "loading a Twitter feed," not "the machine is thinking." |
| **Error state** | System errors (transient — Cosmos timeout, Foundry retry, etc.) | Distinct from refusal. Less prominent. Quiet inline banner, `text-secondary`, retry affordance. |
| **Empty/landing state** | First load, before any question asked | The cinematic flourish lives here. Hero text in display, gentle ambient motion (a single pulse every few seconds on a graphic element — *one* delight beat, not a screensaver). |

## Risks (active)

| Risk | Antidote |
| --- | --- |
| **Gamer-HUD trap.** Modern LCD pushed too far becomes Razer-keyboard energy. | Pinball *broadcast*, not pinball *gaming*. Sports lower-thirds, not RGB-everything. No animated gradient borders, no scanline overlays on whole panels. |
| **Readability scaling.** Pinball LCDs show short bursts; Wizard answers are paragraphs. | App-native foundation is the answer. Display type stays reserved for headers and announcements; body is Inter through and through. |
| **Accessibility regression.** Saturated-on-near-black easily fails AA. | Every palette pair tested at AA from day one. No "we'll fix it later." Every accent paired with non-color signal. |
| **Motion fatigue.** Even good motion becomes annoying on the 50th question of a session. | All non-essential motion respects `prefers-reduced-motion`. Pulsing-loading is the *only* persistent-motion element; everything else is single-fire on event. |
| **"Is this even pinball?"** App-native foundation can drift into generic-dark-SaaS if the broadcast accents are timid. | The broadcast accent moments — answer reveal, citation appearance, mode-start — are non-negotiable. They're what stop this from being "just another dark theme with orange buttons." |
| **Engagement-metric drift.** Future feature pressure ("can we surface related questions?", "can we show 'others also asked'?", "can we add user accounts to track history?") will look reasonable in isolation but compound into a destination app. | Posture section is the test. Every proposed feature passes through: does this serve "route the user out to a community resource" or does it pull the user deeper into the Wizard? If the latter, defer or redesign. |
| **Refusal-as-dead-end.** Easiest implementation of refusal is a flat "I don't know" panel — which leaves the user stuck in the Wizard with no path forward. | Refusal panel must always carry routing recommendations (Pinside thread, OPDB entry, manufacturer page) matched to the question. The community-resource posture makes "I don't know but here's where to ask" a *better* outcome than a confident-but-wrong answer, not a worse one. |

## Open questions (iterate here)

- **Flipper-button shape geometry.** The locked direction is "rectangular puck with ~6px radius, recessed look, backlit." Worth a spike against three real reference points — modern Stern (Spike 2 cabinet button), JJP (their slightly more rounded button), and the classic Williams/Bally button — to see which silhouette reads most universally as "pinball flipper" rather than "specific manufacturer's flipper." Mechanics-not-IP applies to button shape too.
- **Citation card density on mobile.** Desktop has room for the full card layout. On mobile, the flipper-button pair stacks vertically? Or shrinks to side-by-side narrow buttons? Or collapses to a single right-flipper "VIEW THE ORIGINAL" with the left-flipper navigation function moved to a tap-on-the-card-header gesture? Needs a mobile mock before deciding.
- **Many-citation visual rhythm.** A 5–8 citation answer has a long card stack. Does a subtle alternating background tone (`bg-surface` ↔ `bg-surface-hi`) help readability without dimming the hero treatment? Worth prototyping. The rule that protects the principle: alternation must not look like "secondary cards are less important" — it's purely rhythm, not hierarchy.
- **Audio.** Pinball is half-sound. Should the empty/landing state have a single subtle GI-hum loop? Should the flipper-button press have a soft mechanical click? Default-off, opt-in, or default-on with mute? (Lean: default-off for v1 — audio is a delight feature for v2. But the flipper-press click is the most defensible single audio cue if we add any.)
- **Cabinet chrome on desktop.** On wide viewports, do we frame the app inside a subtle cabinet-bezel motif (very low contrast, decorative), or stay flat-edge-to-edge? (Lean: stay flat. Bezel is a "Cabinet" theme thing.)
- ~~**Display font final choice.**~~ **Resolved 2026-05-08:** Barlow Condensed locked (700 primary / 500 secondary). Spike compared against Saira Condensed, Oswald, and Anton; Saira drifts to gaming-HUD energy, Oswald is overuse-disqualified, Anton fails the 12–14px caps test. DJR Forma DJR Banner and Klim Söhne Breit noted as commercial upgrade paths if a future design pass identifies a shortcoming Barlow can't solve.
- **Light mode.** Does Modern LCD have a light variant, or is it dark-only by definition? (Lean: dark-only. A "Daytime Route" theme can exist as a separate sibling — light theme that evokes a sunlit arcade or pinball expo hall — rather than forcing Modern LCD to bend.)
- **Motion-reduced fallback for mode-start.** Crossfade is the default fallback. Worth prototyping a "border tick" alternative — a single-frame `border-glow` state change with no animation — to see if it carries the same "you've moved" signal without motion.
- **Motion-reduced fallback for flipper-press.** The 1–2px depression is tactile feedback, not decoration. Under `prefers-reduced-motion`, should it collapse to a backlight flash only, or stay (since it's the system's clearest "this happened" confirmation)? Lean: keep the depression but skip the backlight flare animation, replacing it with a single-frame state change.

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 draft | Initial brainstorm. Locks flavor (app-native + broadcast accents + cinematic flourish), sketches palette/type/panel/motion, inventories surfaces, names risks. |
| 2026-05-08 | v2 — citation as hero | Reframes citations as the central UX object (not a footnote). Adds full citation-card anatomy, locks flipper-button CTA pair (`◀ VIEW IN ANSWER` / `VIEW THE ORIGINAL ▶`), locks inline citation marker as a numbered pinball-insert (Option C). Updates motion vocabulary with the flipper-press depression exception. |
| 2026-05-08 | v3 — community-resource posture | User reframed: "we want to push users to community sites, not keep them in ours, this is a community resource." Promoted the strategic principle to a top-level **Posture** section above Flavor — outbound is a feature, refusal directs out, no engagement-metric framing. Added body-text outbound portals (machine names, manufacturers, tournaments link to source/community sites). Refusal panel now always carries routing recommendations. Risks gain engagement-metric-drift and refusal-as-dead-end entries. ASCII layout sketch removed (per `feedback_no_ascii_diagrams.md`). New global memory entry: `feedback_community_resource_posture.md`. |
| 2026-05-08 | v3.1 — community-resources contract extracted | Outbound destination directory + resolution-by-entity-type + refusal-routing matrix moved into a dedicated [`docs/community-resources.md`](../../community-resources.md) (system contract). This theme doc now references it for which destinations exist; it covers visual rendering only. Captures the locked distinction: *linking-to* a site (Pinside, etc.) is the inverse of *scraping-from* it — Pinside is fully linkable as a destination despite being deferred for scraping. |
| 2026-05-08 | v3.2 — refusal panel sketch + display font lock | **Refusal panel** expanded with full layout (category label / honest reason / routing recommendations stack), routing-recommendation CTA visual spec (recessed puck ~36–40px, `accent-grounded` backlight, 1px press depression, side-by-side on desktop / stacked on mobile) — same family as flipper buttons but smaller to preserve hero hierarchy. Per-category framing examples added for `LOW_CONFIDENCE` / `OUT_OF_SCOPE` / `CONFLICTING_SOURCES`. Refusal panel references the canonical 6-value question-topic enum from `community-resources.md`. **Display font** locked: Barlow Condensed 700 primary + 500 secondary, after a spike against Saira Condensed (gaming-HUD drift), Oswald (overuse-disqualified), Anton (fails 12–14px caps). DJR Forma DJR Banner + Klim Söhne Breit noted as commercial upgrade paths. |
| 2026-05-08 | v3.3 — appearance-of-favoritism woven through Posture | User: "lets also be sure we document clearly thruout that we need to avoid any appearance of favoritism." Added **Posture rule 4: Avoid any appearance of favoritism — visual treatment is the carrier.** (Peer destination CTAs visually identical; citations from every manufacturer rendered identically; coverage gaps named honestly in refusal text.) Three new anti-patterns: visually favoring one destination over peers, refusal text that implies covered venues are *better*, citing some manufacturers more visually prominently than others. Routing-recommendation CTA spec gains explicit **Peer parity** row — all CTAs within a refusal panel visually identical, ordering matches contract's alphabetical/randomized convention, never editorial. New global memory entry: `feedback_avoid_appearance_of_favoritism.md` (umbrella; covers ordering, visual parity, coverage transparency, manufacturer/brand parity, refusal framing). |
