# Sibling Themes — Derivation Sketches

> **Status:** v1 sketches on `Dev-WebUiThemesBrainstorm`. Each sibling is a directional sketch — none are locked. Modern LCD remains the only fully-specified theme. Sibling themes graduate to their own spec under `docs/ui/themes/[name].md` when they earn it.

## Purpose

Modern LCD is the locked default theme (see [`docs/ui/themes/modern-lcd.md`](modern-lcd.md)). The original brief envisioned multiple user-selectable themes — DMD Classic, Cabinet, Backbox, etc. — each evoking a different *era* or *form factor* of the medium. **This doc sketches the directional intent for each sibling so the locked Modern LCD design system can absorb them without re-architecting.**

These are sketches, not specs. They commit:
- The aesthetic each sibling evokes
- The palette tilt vs. Modern LCD's locked palette
- The type direction
- When a user would pick this theme
- The tactile / interactive register each expresses
- The anti-pattern each must avoid

They do NOT commit:
- Concrete palette hex values (those get computed when a sibling earns a full spec, with WCAG AA verification)
- Final type stack
- Implementation tokens
- Specific audio sample sets (the *profile* is sketched; the *recordings* are an ADR-level decision per sibling at v2-audio time)

## The derivation principle

**Siblings are skins, not different apps.** They inherit ALL of Modern LCD's structural and behavioral locks:

| Inherited (must not change in any sibling) | Variable (where siblings express character) |
| --- | --- |
| Posture (community resource, plurality, appearance of favoritism, polite-by-construction) | Color palette (with WCAG AA preserved) |
| Information architecture (header / question input / answer zone / citation cards / footer) | Type stack (display + body + mono families) |
| Citation-as-hero with flipper-button CTAs | Motion timing and easing curves (not motion vocabulary) |
| Inline pinball-insert citation markers | Texture / decoration accents |
| Refusal-panel anatomy + routing-recommendation peer parity | Optional cabinet-chrome framing on desktop |
| Coverage-transparency surface ("What we cover") | **Tactile & Interactive Variables** — visual + audio register of dynamic lighting, hover triggers, success/error states (the *expression* of mechanics every sibling executes) |
| Accessibility floors (WCAG AA contrast, motion-reduced fallbacks, keyboard nav) | **Audio profile** (era-specific instrumentation, sample sets, sound character) |
| **Tactile mechanics existence** — every sibling executes pull-to-refresh-as-plunger, cursor-tracked GI, tilt warnings on error/spam, and match sequences on high-friction success. The *which* is locked; the *how* is sibling-variable. | — |
| **Audio existence** (when v2 ships audio) — every sibling carries the audio layer; the opt-in toggle (`pinwiz.sound`, mute-by-default per ADR-0026) is inherited. | — |

**If a sibling proposal would break an inherited lock, the proposal is wrong — not the lock.** The siblings are about expressing pinball's range; the structural commitments are about being a good app.

### What "Tactile & Interactive Variables" means

The locked mechanics:

- **Pull-to-Refresh as plunger lane.** On mobile, pulling down compresses a sibling-themed spring graphic; releasing fires a silver ball up the screen and triggers refresh. Every sibling executes the metaphor; the rendering is era-specific.
- **Cursor tracking / dynamic GI.** The mouse cursor acts as a ball or flashlight. Passing over structural elements triggers rollover hover states. In darker siblings, mouse coordinates drive a CSS radial gradient that illuminates nearby playfield inserts or translite layers. Reduced-motion users get static rollover treatment instead.
- **Tilt warnings (error / spam states).** Rapid clicking, severe form errors, or aggressive scrolling trigger a subtle CSS screen shake plus a thematic "TILT" / "WARNING" UI flash. Honors `prefers-reduced-motion` (shake collapses to a single-frame state change; flash remains).
- **Match sequences (success states).** High-friction task completion (refusal recovery click-through, settings save, opt-in audio toggle) triggers a brief, era-appropriate visual celebration in the spirit of a pinball match sequence. Time-boxed to ≤600ms per the locked motion budget.
- **Era-specific audio (v2).** When audio ships, each sibling carries an instrumentation profile that matches its era — synth chips, mechanical chimes, modern samples, ambient crowd noise. Mute-by-default; opt-in toggle inherited from ADR-0026.

The mechanics are universal; the sibling expresses them in its own register.

---

## Sibling: DMD Classic

> **Era:** late 80s through mid-90s. **Mood:** nostalgic, hobbyist-warm, peak pinball. **The "I grew up on Twilight Zone" theme.**

### Aesthetic

The dot-matrix display dominated pinball for over a decade. Sparse pixel-art animation, monochrome amber on black, score-broadcast typography rendered in dots. DMD Classic faithfully evokes this without literally rendering everything as dot-matrix (which would tank readability for long-form answers).

### Palette tilt
- Background: pure black `#000000` — the one theme where pure black is correct (DMDs were genuinely *off* when not lit, not "almost off").
- Primary accent: amber `#ff8800`-range — slightly more orange than Modern LCD's `--accent-primary`. THE DMD color.
- Secondary accents: muted, used sparingly. The DMD era was largely monochrome — restraint is character, not lack of imagination.
- Subtle pixel-glow / bloom effect on accent text.

### Type direction
- Display: a true bitmap-style pixel font (e.g., Press Start 2P or a custom DMD-emulating face). Used SPARINGLY — only score-broadcast moments and citation-card source identity.
- Body: paradoxically, still a clean readable sans (Inter). Paragraph reading in pixel font would be punishing. The pixel font is decoration; the body is utility.
- Mono: same pixel font as display, smaller size.

### Tactile & Interactive expression
- **Pull-to-refresh:** spring tension visualized as dot-pattern density compression; release fires a low-res silver ball sprite (rendered in pixel-amber) ascending the screen, leaving a 1-frame dot-trail.
- **Cursor / GI:** cursor leaves a brief 1–2px amber afterimage; rollover pulses element borders in amber. No radial gradient — DMDs didn't ambient-glow, they just lit dots.
- **Tilt warnings:** 1-pixel snap-shake (DMDs jittered in single-pixel increments, never sub-pixel); giant dot-matrix `TILT` callout flashes amber-on-black, full-screen.
- **Match sequences:** monochrome amber `MATCH` or `EXTRA BALL` banner animates across the screen in the cadence of a real DMD score-celebration loop.
- **Era audio (v2):** Yamaha YM2151-style FM synth chips — bitcrushed bleeps for hover, an FM-synth match jingle, the classic three-note ascending "ball locked" chime for refresh, FM-percussion thud for tilt.

### When a user picks this
- Hobbyists who own DMD-era machines.
- Nostalgia-heavy use sessions (browsing a 1993 Bally machine, reading a Williams service bulletin).
- Special-occasion or seasonal use.

### Anti-pattern
- Rendering everything in pixel font. The era's *aesthetic*, not its *limitations*. Body paragraphs in pixel font would be unreadable and signal "design school project" rather than "thoughtful homage."
- Sub-pixel motion in the tactile layer. DMDs jittered in whole-pixel increments. Smooth tweening on the pull-to-refresh ball or the tilt shake breaks the era.

---

## Sibling: Cabinet

> **Era:** timeless. **Mood:** tactile, warm, physical. **The "I'm in front of the machine" theme.**

### Aesthetic

The cabinet itself — wood-grain side art, brushed metal lockdown bar, coin-door textures, the hardware. Cabinet treats the screen as if you're standing at the machine, with the UI framed by suggestion of a real cabinet.

### Palette tilt
- Background: warm dark woods (`#2a1f15`-range), subtle wood-grain texture confined to the page bezel — never on body-text surfaces.
- Accents: brushed metal grays, polished playfield greens, classic flipper-button red and yellow.
- Higher-saturation accent moments — the lit "inserts" feel literally lit through translucent plastic.

### Type direction
- Display: classic American game-art lettering — geometric, slightly chunky, evokes hand-painted side-art.
- Body: same Inter for readability. Cabinet skins paragraphs but doesn't fight them.
- Mono: a typewriter-style mono — evokes the dot-matrix instruction cards inside coin doors.

### Tactile & Interactive expression
- **Pull-to-refresh:** the most literal plunger metaphor of the five — a chrome plunger rod compresses against a coil-spring graphic; release fires a silver ball upward with a brief wood-grain bezel rebound. Spring travel is visible.
- **Cursor / GI:** cursor coordinates drive a CSS radial gradient simulating an overhead arcade bulb sweeping across wood-grain. Passing over flipper-button CTAs and citation cards triggers a translucent "lit insert" treatment — backlit plastic. The most expressive GI execution after Backbox.
- **Tilt warnings:** full-cabinet jolt — page bezel jerks with a wood-knock effect; a stamped-metal `TILT` plaque slides down from the top edge with a single mechanical clack. One jolt only — repeated shaking would feel arcade-broken, not arcade-tactile.
- **Match sequences:** coin-door rattles open via a slight bezel scale + flash; a brass-and-red `MATCH` plaque animates with subtle reflection sweep.
- **Era audio (v2):** heavy mechanical coils, a knocker thump for match, a low brass chime for hover, a wood-thud + glass-rattle for tilt; cabinet GI hum at ~60Hz as ambient (very low volume, defeated by reduced-motion or low-stim accessibility settings).

### When a user picks this
- Users who want the warmest, most physical feeling.
- Long sessions where the warmth doesn't fatigue (vs. pure black, which can over an evening).
- Users who specifically value the *cabinet* as the primary form factor — collectors, restorers, route operators.

### Anti-pattern
- Heavy textures everywhere. Wood grain on every panel turns into 1990s skeuomorphism. Texture is for accents (header, footer, panel borders), never on body-text surfaces.
- Shaking the entire cabinet on every minor warning. Tilt is a *deliberate* outcome — escalating shake to every form-validation miss cheapens the metaphor.

---

## Sibling: Backbox

> **Era:** modern (translite-LCD hybrid). **Mood:** theatrical, layered, glowing. **The "the machine is alive" theme.**

### Aesthetic

The backbox is the playfield's visual showpiece — translite art layered over GI lighting, with playfield reflections and animated callouts. Backbox is the most visually maximal sibling — heavy use of layered glow, reflective treatments, lit-from-behind affordances.

### Palette tilt
- Background: deep blue-black (`#0a0e1a`-range) with a subtle gradient suggesting backbox glass and ambient room light.
- Accents: deeply saturated — magenta, cyan, atomic green — all with significant outer glow.
- Heavy use of soft outer glow on every accent surface (panels, CTAs, inline markers all emit subtle light).

### Type direction
- Display: a more theatrical / cinematic display face (similar to film-poster typography — geometric, condensed, with optional optical embellishments).
- Body: still Inter.
- Mono: still JetBrains Mono.

### Tactile & Interactive expression
- **Pull-to-refresh:** a translucent ball charges with magenta/cyan light as the spring compresses; release fires a glowing ball trail shooting upward across translite layers. The ball trail bleeds light onto adjacent UI as it travels.
- **Cursor / GI:** the most theatrical execution among siblings. Cursor drives a high-saturation radial spotlight over translite layers; rollover triggers backlit insert effects with bloom and outer glow on the hovered element. Multi-layer parallax of glow only (NOT of content — content stays flat per the inherited motion vocabulary).
- **Tilt warnings:** stage-lights flicker — backbox pulses with a strobe-suppressed-for-reduced-motion magenta/red wash; `TILT` displays as theatrical neon callout. Strobe defeated by `prefers-reduced-motion` to a single static red wash.
- **Match sequences:** full translite light show — multiple insert layers flash in sequence with bloom; `MATCH` callout animates with depth and glow. The closest the sibling set gets to a "celebration" — and the only place that depth is appropriate.
- **Era audio (v2):** modern Stern-style sample-based audio — orchestral hit for match, deep bass thump for tilt, layered synth callouts for hover and success, ambient backbox electronics hum at very low volume.

### When a user picks this
- Users who want maximum visual delight.
- Showcase / demo contexts where the visual impression matters most.
- Late-evening sessions where the "lit-up arcade" feeling resonates.

### Anti-pattern
- Crossing into "marketing site" territory. Backbox should feel like *the machine running*, not like a *promotional video*. Animation stays within the locked vocabulary — no autoplay videos, no parallax scrolling on content, no decorative motion that doesn't carry signal.
- Strobing on tilt without honoring reduced-motion. Photosensitivity is a hard floor; the strobe is a flourish, not a requirement.

---

## Sibling: Score Reel

> **Era:** electromechanical, pre-DMD (1930s through early 1980s). **Mood:** mechanical, paper-and-metal, classic. **The "Gottlieb in a malt shop" theme.**

### Aesthetic

Pre-DMD pinball used physical score reels — rotating drums showing painted numerals — and printed paper score cards on the apron. Score Reel evokes this mechanical era: cream backgrounds, painted numerals, paper-card surfaces with subtle texture. The most aesthetically distinct sibling from Modern LCD.

### Palette tilt
- Background: cream / aged paper (`#f4ecd8`-range). Score Reel and Daytime Route are the two light-mode siblings sketched.
- Accents: classic Gottlieb / Bally machine red and yellow, with brass and chrome metal accents for "machine" moments.
- High-contrast black ink for body text — typewriter or letterpress feel.

### Type direction
- Display: a score-reel-style numeral face for scores and counts; for headers, a vintage display sans evocative of mid-century arcade marquees.
- Body: a slightly warmer body face with subtle character (Source Serif Pro is a candidate — still readable but not generic).
- Mono: typewriter feel (e.g., Courier Prime).

### Tactile & Interactive expression
- **Pull-to-refresh:** classic plunger — a mechanical spring graphic compresses with paper-and-metal tension marks; release fires a silver ball, with painted-numeral score-reel digits rolling `0` → `1` → `2` as it ascends. The most mechanically-readable plunger of the five.
- **Cursor / GI:** minimal — Score Reel is light-mode and mechanical-era; no atmospheric GI. Hover triggers a stepper-relay shutter twitch on the affected element (a single-frame "tick"). No cursor afterimage — the era pre-dates electronic phosphor persistence.
- **Tilt warnings:** stiff mechanical clack — page jolts once; a paper `TILT` stamp drops onto the screen with an ink-bleed effect. Static after the drop; no strobe, no pulse. EM machines didn't blink — they stopped.
- **Match sequences:** rotating score-reel digits spin to alignment with audible mechanical roll; a small pinball-card stamp animates onto the apron-style footer with a printed-paper character.
- **Era audio (v2):** physical chimes (sampled from a 1965 Gottlieb), stepper-relay clicks for hover, score-reel mechanical roll for match, knocker thump for tilt. The most recognizably mechanical soundscape of the five — and the only sibling whose audio is closer to *recordings of hardware* than *synthesis*.

### When a user picks this
- Users with specific affinity for the EM era.
- Restorers researching pre-1980 machines.
- Users who want a light-mode option *with* era-theming (vs. Daytime Route which is era-neutral).

### Anti-pattern
- Going twee. Score Reel risks "vintage scrapbook" cliché. Restraint matters — the era was utilitarian and confident, not precious. No cursive, no flourishes, no fake aging on every surface.
- Smooth tweening on the score-reel digit roll. The reels were mechanical and indexed — they snapped to digits, they didn't ease.

---

## Sibling: Daytime Route

> **Era:** any. **Mood:** sunlit, public, social. **The "I'm at a pinball expo" theme.**

### Aesthetic

A light-mode sibling that doesn't lock to any specific era. Evokes a pinball convention floor, a sunlit barcade, a route stop in daylight — pinball as a *social* / *public* experience rather than an arcade or basement experience. The era-neutral counterpart to Score Reel's era-specific light mode.

### Palette tilt
- Background: warm off-white (`#faf6ef`-range), subtle "convention floor" feel.
- Accents: same six accent roles as Modern LCD but rebalanced for light backgrounds — amber, atomic green, red, magenta all desaturated slightly to maintain WCAG AA against the lighter background.
- Optional very-low-contrast background imagery (convention banners, pinball-themed wallpaper) framing the content area on desktop. Subtle enough not to fight readability.

### Type direction
- Display: a slightly less condensed face than Barlow Condensed — open, daytime-feel (Barlow regular is a candidate, or DM Sans Bold).
- Body: Inter (unchanged).
- Mono: JetBrains Mono (unchanged).

### Tactile & Interactive expression
- **Pull-to-refresh:** the cleanest, most contemporary plunger — minimal spring graphic compresses with a soft drop shadow; release fires a silver ball with a brief light trail. Refresh feels like the mechanical-but-modern motion of a route-stop machine, not a 1965 EM and not a glowing translite.
- **Cursor / GI:** subtle daylit treatment. Cursor influences a low-contrast warm highlight over content (no high-contrast spotlight; daylight context = restrained GI). Rollover triggers a soft amber wash on inserts. The most understated GI execution — daylight doesn't need illumination.
- **Tilt warnings:** gentle screen jitter; a soft amber `WARNING` callout instead of `TILT`. The daylit, public context calls for less alarm — convention-banner aesthetic, not arcade-jolt aesthetic.
- **Match sequences:** brief celebratory animation echoing convention-floor energy — a small banner unfurls or a confetti-pixel-burst within the locked motion vocabulary (no autoplay video, no parallax). Time-boxed to ≤400ms — daytime celebrations are quick.
- **Era audio (v2):** ambient distant crowd noise as low-volume background; a gentle expo-style ding for match; a subdued warning tone for tilt; soft click for hover. Daylight = calmer audio. The only sibling with persistent ambient audio — and the most likely to be muted by users in shared spaces, which is fine: the inherited mute-by-default toggle handles it.

### When a user picks this
- Daytime / outdoor / sunlit contexts where dark mode is hard to read.
- Users with light-mode preference for accessibility reasons (some users with astigmatism find dark text on light easier than light text on dark).
- Public-display contexts (the project shown on a kiosk, projector, demo monitor).

### Anti-pattern
- Becoming "default light SaaS." The accent palette and pinball-domain feel must remain. A light theme that loses the pinball signature is just a generic light theme — and the project already has plenty of competitors in that space (none of which are pinball-domain).
- Letting the ambient crowd-noise loop drift toward "elevator music." The ambience is a faint signal, not a soundtrack. If a user notices the loop, the loop is too loud.

---

## Theme picker — UI consideration

When sibling themes ship, users need a way to select their preferred theme. Constraints:

- Theme picker lives in **Settings**, not in the persistent header. Theme switching is a deliberate choice, not casual.
- The picker shows each available theme as a *labeled preview* — theme name, one-line aesthetic description, small visual swatch. NOT a full-screen preview.
- Selection persists per-user (localStorage; backend if accounts ship later).
- Default for any new user remains **Modern LCD** — most contemporary, most prospect-friendly, the locked default.
- Sibling themes display a small `BETA` or `EXPERIMENTAL` tag until they earn full specs.

The full theme-picker spec lives in `docs/ui/screens/settings.md` (not yet drafted).

---

## Sequencing — which sibling ships first?

Recommended order, easiest to most-difficult to ship:

| # | Theme | Why this order |
| --- | --- | --- |
| 1 | **Daytime Route** | Lowest-cost expansion. Same structure as Modern LCD, just inverted background + accent rebalance for AA. Earns its keep by giving accessibility-motivated users a light option without abandoning pinball-domain feel. Tactile expression is the most restrained → easiest to ship without breaking the design system. |
| 2 | **Backbox** | Extends Modern LCD's locked accent palette into more saturated/glowing territory. Same structural spec, more dramatic visual treatment. Highest production cost in the tactile layer (multi-layer GI + bloom on translite) but isolated to one sibling. |
| 3 | **DMD Classic** | Introduces a new typeface (pixel font) AND a constraint on the tactile layer (whole-pixel motion only). Worth the cost for the hobbyist segment. |
| 4 | **Cabinet** | Texture-heavy + most literal plunger metaphor + ambient GI hum. Needs careful implementation to keep readability intact and audio non-fatiguing. Most production-design risk. |
| 5 | **Score Reel** | Most aesthetically distinct + the only sibling whose audio profile leans on hardware recordings rather than synthesis. Effectively a second design system rather than a re-skin. Highest risk, highest reward. Defer until 1–4 prove the sibling pattern. |

This is the recommended order, not a commitment. User priorities or specific use cases can re-order.

---

## What this doc explicitly does NOT do

- Lock any sibling palette at concrete hex values (that happens when a sibling earns its full spec and goes through WCAG AA verification).
- Lock any sibling type stack.
- Specify implementation tokens.
- Specify which audio sample sets ship — the *profile* sketches are directional; recordings are an ADR-level decision per sibling at v2-audio time.
- Cover sibling-specific features that don't exist in Modern LCD (siblings inherit; they don't extend).
- Address theme-aware imagery (e.g., theme-specific hero illustrations) — defer until at least one sibling ships and the patterns become clear.

---

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 sketches | Five sibling themes sketched (DMD Classic, Cabinet, Backbox, Score Reel, Daytime Route). Each gets aesthetic / palette tilt / type direction / when-to-pick / anti-pattern. Establishes the **derivation principle** (siblings inherit Modern LCD's structural + behavioral locks; they differ only in visual character — palette, type, motion timing, texture, optional bezel framing). Two light-mode siblings sketched: Score Reel (era-specific) and Daytime Route (era-neutral). Recommended sequencing: Daytime Route → Backbox → DMD Classic → Cabinet → Score Reel. None locked — Modern LCD remains the only fully-specified theme. |
| 2026-05-09 | v1.1 — three siblings now prototyped | Three of the five sibling themes have working HTML prototypes alongside their sketches: **DMD Classic** (`docs/ui/prototypes/answer-with-citations-dmd-classic.html`), **Daytime Route** (`docs/ui/prototypes/answer-with-citations-daytime-route.html`), and now **Backbox** (`docs/ui/prototypes/answer-with-citations-backbox.html`). Backbox uses Big Shoulders Display (cinematic / theatrical) for display type, deep blue-black with magenta/cyan/violet accents, and heavy outer-glow on every accent surface — proves the design system handles the "more is more" end of the spectrum. The fourth and fifth siblings (Cabinet, Score Reel) remain sketches; Cabinet is texture-heavy (highest implementation risk) and Score Reel is the most aesthetically distinct (effectively a second design system). Working theme picker at `docs/ui/prototypes/theme-picker.html` switches between Modern LCD + DMD Classic + Daytime Route live (Backbox not yet wired into the picker; trivial extension when desired). |
| 2026-05-09 | v1.2 — Tactile & Interactive Variables added | New conceptual category folded into the inherited / variable table: **Tactile mechanics existence** is inherited (every sibling executes pull-to-refresh-as-plunger, cursor-tracked GI, tilt warnings on error/spam, match sequences on success); the **expression** of those mechanics is sibling-variable. **Audio** split: existence inherited (every sibling carries the audio layer, mute-by-default toggle inherited from ADR-0026); profile variable (era-specific instrumentation per sibling). Each of the five sibling sketches gains a "Tactile & Interactive expression" subsection naming the concrete execution per mechanic — including DMD's whole-pixel motion constraint, Cabinet's GI hum + plunger chrome, Backbox's multi-layer translite light show, Score Reel's hardware-sample audio leaning on real chimes, and Daytime Route's restrained ambient crowd noise. New anti-patterns added per sibling where the tactile layer creates new failure modes (DMD sub-pixel motion, Cabinet over-shake, Backbox un-honored strobe, Score Reel digit easing, Daytime Route audio drift). Sequencing rationale updated to reflect tactile-layer cost. |
