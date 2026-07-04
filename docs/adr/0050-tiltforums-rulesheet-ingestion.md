# 0050 — Ingest Tilt Forums community rulesheets under the founder's public invitation

**Status:** Accepted
**Date:** 2026-07-03

## Context

Domain-2 gameplay-rules depth ("what reaches Godzilla's wizard mode") has no
polite public *manufacturer* source — a `--reclassify-documents` pass over 567
live docs on 2026-06-25 yielded zero `Rulesheet` promotions from manufacturer
content; manufacturers publish manuals and spec sheets, not per-game strategy
writeups. The deepest community-maintained rulesheet corpus is **Tilt Forums**
(tiltforums.com), a "Wiki Rulesheets" subcategory covering ~80-90 modern
machines across every manufacturer we track, each a single collaboratively-
edited wiki post (~4-5k words: modes, multiballs, wizard-mode paths).

Tilt Forums' Terms of Service license user contributions under
**CC-BY-NC-SA 3.0** ("User contributions are licensed under a Creative
Commons Attribution-NonCommercial-ShareAlike 3.0 Unported License", verified
2026-07-03 at `tiltforums.com/tos`). PinballWizard is a customer-facing
showcase (`CLAUDE.md`), which is arguably commercial-adjacent even though it
sells nothing directly — this is the reason a 2026-06-25 decision brief
(`docs/superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md`)
concluded "low odds of a commercial waiver" and recommended route-outward
links only, gated on written permission before any ingestion.

On 2026-06-30, founder/admin **Greg Dunlap (`gdd`)** posted
["The Future of Tilt Forums"](https://tiltforums.com/t/the-future-of-tilt-forums/10276),
announcing the forum's closure on 2026-09-01. The post states, addressed to
the public and to nobody in particular:

> "Mine the data, train your models. This is a resource that belongs to the
> community, and I want it to continue to be so."

This is a public, written, unambiguous invitation from the site's founder to
do exactly what this ADR authorizes. It is not a formal license amendment —
the rulesheets are multi-author wiki content, and Greg may not hold sole
authority to relicense every individual contributor's text — but it is a
strong, good-faith basis to proceed, especially combined with the mitigations
below. A direct outreach message to `gdd` requesting explicit confirmation of
our specific use case was drafted the same day as a courtesy follow-up; per
2026-07-03 direction, ingestion proceeds now rather than waiting on that reply.

## Decision

Ingest Tilt Forums' Wiki Rulesheets subcategory into the PinballWizard RAG
index via a synthesis pipeline (design:
`docs/superpowers/specs/2026-07-03-tiltforums-rulesheet-scraper-design.md` —
same shape as the existing Kineticist tutorials ingestion, not the
PDF-oriented scraper/download/linker pipeline), classified
`DocumentType.Rulesheet` (already proven in production via Kineticist),
subject to two non-negotiable constraints that hold regardless of what
permission is eventually confirmed in writing:

1. **Every answer built from this source cites and links back to the specific
   Tilt Forums topic** — never presented as PinballWizard's own content.
2. **Answers never reproduce more than a short excerpt** of the rulesheet
   text — grounding and summarization only, no full-text republication. This
   keeps us inside the spirit of Attribution-ShareAlike even where the
   NonCommercial term is ambiguous for our use case.

Scope for this decision is the ~80-90 Wiki Rulesheets topics only. General
discussion threads are explicitly out of scope pending a separate research
pass to assess whether they contain ingestion-worthy content.

## Consequences

- Domain-2 gameplay-rules depth — previously the corpus's weakest area — gets
  its first real content source, covering the majority of modern machines
  across all eight scraper-covered manufacturers plus vintage titles.
- We are proceeding on an informal public statement rather than a written
  reply addressed to us. This is a conscious risk acceptance, not an
  oversight: if Greg's reply (once received) narrows or revokes the
  invitation, the affected content must be pulled from the index and this ADR
  updated or superseded. The pending confirmation message is not withdrawn —
  it still gets sent as a courtesy and a paper-trail backstop.
- Because ingestion goes through the synthesis pipeline (no Cosmos
  `DocumentRecord`), these citations render identically to today's Kineticist
  citations: the external "open file ↗" link to the Tilt Forums topic works
  correctly, while the internal `/documents/{id}` page shows "Document not
  found" (there's no Cosmos record to look up). This is the established
  precedent for synthesis-path content, not a gap introduced by this
  decision. The codebase has no per-source-type citation styling at all, so
  Tilt Forums content is not visually distinguished from manufacturer
  content in the citation card — attribution lives in the link itself.
- This does not change the standing posture toward Pinside, IPDB, or PinWiki
  (`docs/superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md`) —
  those remain hard-no regardless of this decision; Tilt Forums' founder
  giving an explicit public go-ahead is what makes this case different, not a
  general relaxation of the community-content ingestion gate.
- The forum closes 2026-09-01 and its content migrates to a static
  GitHub-Pages-hosted archive. A future scrape/re-sync of the migrated
  location is expected but not designed here; this ADR covers ingestion from
  the live Discourse site only.
