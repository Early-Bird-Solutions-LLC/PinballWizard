# Kineticist gameplay-rules integration — design

**Status:** Proposed — pending Kineticist partnership confirmation.
**Date:** 2026-06-25

> Per [`docs/adr/README.md`](../../adr/README.md), an ADR records a *decided* thing and
> is written in past/present tense. This integration is **not yet decided** — it is
> contingent on a partnership conversation with Kineticist. This design doc captures the
> proposed approach and the preconditions; a formal ADR will be authored **when** the
> partnership is confirmed, recording the agreed terms and integration.

## 1. Context

The Domain-2 gameplay-rules ceiling is documented in
[`docs/knowledge-sources.md`](../../knowledge-sources.md) §3.7 and the decision brief
[`2026-06-25-domain2-rules-sourcing-decision.md`](2026-06-25-domain2-rules-sourcing-decision.md):
the Wizard cannot answer wizard-mode / mode-tree questions because no polite, public,
login-free **manufacturer** source publishes standalone rulesheets, and a live
reclassification pass over 567 corpus documents (2026-06-25) produced zero `Rulesheet`
promotions.

Community-maintainer research (2026-06-25; memory `project_domain2_rules_sourcing`)
identified **Kineticist** (kineticist.com) as the one resource that is both deep enough
and approachable:

- 4,000–5,000-word per-game guides with wizard modes, full mode trees, and a "Noah's
  Strats" competitive section; current through 2026.
- `robots.txt` explicitly **permits** AI access (`ai-train=yes`, tailored ClaudeBot
  permissions) — an intentional operator choice.
- An official **API + OpenAPI spec + MCP Server** for programmatic access, plus a
  "Partnership" contact channel (founder Colin Alsheimer).

(Tilt Forums has the deepest community content but is CC-BY-NC-SA NonCommercial, so it is
**route-outward only**, not ingestible — handled separately via the community-resource
panel.)

## 2. Decision (proposed)

**Integrate Kineticist gameplay-rules via their MCP server / API as a live TOOL in the
agent tool registry — NOT by ingesting their content into the RAG corpus.**

`architecture-v2.md` already frames the Wizard as a tool-using agent (the registry holds
`searchCorpus`, `getMachineByTitle`, etc.). A `kineticist_rules` tool slots in alongside
them: the agent calls it for gameplay-rules / wizard-mode questions, then synthesises an
answer that **cites and links back** to the specific Kineticist guide.

### Why tool-over-ingest

| | MCP/API tool (proposed) | Ingest into corpus (rejected for now) |
|---|---|---|
| **Community posture** | Every answer sends Kineticist attributed traffic — the route-outward posture *realised*, not worked around | Serves their labour through our UI; displaces a visit even with citation |
| **Freshness** | Always current; no stale copy | Requires a refresh pipeline; copy drifts |
| **Licensing** | We query + cite; we don't store/redistribute their text | Needs a storage/redistribution license grant |
| **Architecture** | First-class fit — MCP is a tool in the registry | New ingestion path + embeddings for someone else's content |
| **Cost** | Per-query tool call | Bulk embed of their corpus |
| **Maintenance** | Thin client + config | Pipeline + dedup + re-embed on their updates |

The only advantage of ingestion is offline retrieval latency, which does not outweigh the
posture, freshness, and licensing benefits of the tool approach.

## 3. Integration sketch (for when the partnership lands)

- A `kineticist_rules` function tool (or MCP client) in the Application tool registry,
  gated behind `KineticistOptions` (endpoint, auth/API key via Key Vault) — absent config
  ⇒ tool not registered (graceful, like the other backend-gated tools).
- The agent invokes it for gameplay-rules intents; the tool result carries the **source
  guide URL**, which flows into the answer's `Citation` (provenance is sacred — the
  citation links to Kineticist).
- The answer UX makes the Kineticist attribution prominent (consistent with the
  community-resource posture and the avoid-favoritism rules — attribution is to the
  source of the specific answer, not a promotion).
- Model selection is untouched (ADR-0015); this is a tool, not a model.

## 4. Preconditions (hard gates before any code)

1. **Written partnership / terms agreement** from Kineticist granting API/MCP access on
   terms compatible with a customer-facing showcase (attributed, link-back, no resale of
   their content).
2. A **formal ADR** authored at that point — recording the agreed terms, the scope, the
   attribution guarantee, and the tool design as *decided*.
3. Confirm the **MCP/API surface**: auth model, rate limits, cost, and the response shape
   (so the tool result can carry a citable source URL).

No code is written before (1) and (2).

## 5. Next action

Outreach to Colin Alsheimer (Appendix A). Jim sends it. This is the "ask directly" path
that `knowledge-sources.md` §7 principle 3 already advocates for valuable sources.

## 6. References

- [`2026-06-25-domain2-rules-sourcing-decision.md`](2026-06-25-domain2-rules-sourcing-decision.md) — the options brief
- [`docs/knowledge-sources.md`](../../knowledge-sources.md) §3.7 — the ceiling note
- [`docs/architecture-v2.md`](../../architecture-v2.md) — the agent + tool-registry frame
- [`docs/adr/0015`](../../adr/) — per-agent model selection (untouched by this)
- memory `project_domain2_rules_sourcing` — the maintainer research

---

## Appendix A — Outreach email draft (Jim to send)

**To:** Colin Alsheimer (via kineticist.com/contact — Partnership)
**Subject:** Partnership inquiry — citing Kineticist rules guides in a source-cited pinball assistant

Hi Colin,

I'm Jim Keeley (Earlybird Solutions). I've built **The Pinball Wizard** — a reference /
showcase AI assistant that answers pinball questions with **source-cited** answers. It's a
portfolio piece demonstrating enterprise AI architecture, not a commercial product
competing with anyone in the hobby.

One thing it deliberately *won't* do well yet is answer deep gameplay-rules / wizard-mode
questions — that depth lives in community-authored rulesheets, and I'm not willing to
scrape community labour. Kineticist stands out: your guides are exactly the depth players
want, you publish an API and an MCP server, and your robots policy welcomes AI access —
which tells me you've already thought about this kind of use.

Rather than ingest or copy your content, my preferred design is to call your **MCP / API
as a live tool**, so every answer the Wizard gives on a rules question is freshly sourced
from Kineticist and ends with a clear **citation and link back** to your guide. In other
words, it sends you attributed traffic on every rules question rather than substituting
for a visit to your site.

Would you be open to a short conversation about API / MCP access terms for that kind of
attributed, link-back integration? I'm happy to show you the app, the attribution UX, and
exactly how I'd surface Kineticist in an answer.

Thanks for building such a great resource for the hobby,

Jim Keeley
jim@earlybirdsolutions.com
