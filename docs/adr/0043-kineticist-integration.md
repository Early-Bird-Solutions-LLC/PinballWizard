# 0043 — Kineticist integration for gameplay-rules depth and catalog enrichment

**Status:** Accepted
**Date:** 2026-06-25

## Context

Domain-2 gameplay-rules depth ("what do I complete to reach a machine's wizard mode")
has no polite, public, login-free **manufacturer** source — a live reclassification pass
over 567 corpus documents on 2026-06-25 produced zero `Rulesheet` promotions (ADR-0042
context; the ceiling note in [`docs/knowledge-sources.md`](../knowledge-sources.md) §3.7).
Community-maintainer research identified **Kineticist** (kineticist.com) as the one
resource both deep enough (3,000–5,000-word per-game strategy guides with full mode trees
and wizard-mode steps) and approachable: it publishes an OPDB-keyed API + MCP server and
an AI-friendly `robots.txt` (`ai-train=yes`).

Founder Colin Alsheimer granted access by email (2026-06-25, thread "Re: [Partnership]
Jim Keeley via contact form"):

- **API access** — yes. A key (free tier, partner-tier on request) keyed by **OPDB id**,
  exposing the games catalog, design credits, editions/MSRP, tags, community fun scores +
  rating counts, and files (manuals/ROMs/schematics).
- **Interim rules-guide indexing** — yes ("do the interim for now"). The published
  tutorials render as **clean Markdown** when `.md` is appended to the page URL
  (e.g. `…/news/transformers-pinball-tutorial.md`), carrying title, author, and canonical
  URL.
- **Durable content API + Hype Index endpoint + per-game on-location counts** —
  interested/planned, not yet built (Colin is iterating). Jim's offer to consult /
  contribute code was accepted.

Full content/API inventory, alternatives, and the conditional paths:
[`docs/superpowers/specs/2026-06-25-kineticist-rules-integration-design.md`](../superpowers/specs/2026-06-25-kineticist-rules-integration-design.md).

## Decision

Integrate Kineticist behind `KineticistOptions` (Key-Vault-backed API key; DI-gated — when
the config is absent, nothing is registered, matching the other backend-gated tools),
across four tiers:

- **Tier A — catalog / ratings / files enrichment** via the API, keyed by OPDB id (facts,
  editions/MSRP, credits, tags, community fun scores + "best of" comparisons, files).
- **Tier B — guide deep-linking** — route a rules question the Wizard can't ground to the
  exact Kineticist guide for that machine, with attribution.
- **Tier C2 — interim rules grounding** — a polite scraper of the published tutorials via
  the `.md` endpoint (`/news/{slug}-tutorial.md`), classified `DocumentType.Rulesheet`
  (ADR-0042) and ingested into the RAG corpus under the permission granted above,
  **committed to migrate to a live tool (Tier C1) when Colin exposes guide content via the
  API.**
- **Tier D — Hype Index + on-location counts** — wired when Colin exposes them.

**The default is live-tool over ingest** (attributed traffic on every answer, always
current, no stored redistribution of his content). **Tier C2 is the single sanctioned
exception** — a time-boxed interim ingest, justified only because the content is not yet
in the API, gated on the granted written permission, and committed to retire on migration
to C1.

**Every Kineticist-sourced answer carries a `Citation`** to the canonical guide / game
page (author + URL) — provenance is sacred and the outbound attribution is the return
value to the partner. Kineticist is credited as a named **data partner** in the app
(the way OPDB is credited), with bidirectional OPDB-keyed links from machine records to
his game pages.

## Consequences

**Positive.** Closes the Domain-2 gameplay-rules ceiling with attributed, posture-clean
sourcing; broadly enriches catalog / ratings / files answers beyond Domain 2; the
OPDB-keyed join avoids the title-substring bug class (#506). The `.md` endpoint makes the
C2 crawl clean — Markdown with title, author, and canonical URL inline, so no HTML /
page-builder-shortcode parsing (unlike the Spooky/PB scrapers).

**Watch points.** The C2 corpus is a **stale copy** until migration to C1 — it needs a
refresh cadence and a tracked migration item. Partner rate-limit dependency for the bulk
enrichment pass. Attribution must ride **every** answer (enforced in the citation path,
not optional). Per-game on-location counts derive from Pinball Map — deferred, since the
Wizard already routes location queries out to Pinball Map.

## References

- [`0042-rulesheet-document-type.md`](0042-rulesheet-document-type.md) — the `Rulesheet`
  document type that Tier C2 ingests as
- [`0027-community-resource-posture.md`](README.md) — community-resource posture
  (attribution / route-outward / avoid-favoritism)
- [`0015`](README.md) — per-agent model selection (untouched by this)
- [`docs/superpowers/specs/2026-06-25-kineticist-rules-integration-design.md`](../superpowers/specs/2026-06-25-kineticist-rules-integration-design.md) — full design + inventory
- [`docs/superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md`](../superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md) — sourcing brief
- memory `project_domain2_rules_sourcing`
