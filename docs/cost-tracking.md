---
status: Active
phase: Phase-6
owner: Jim
last-reviewed: 2026-05-16
supersedes: ""
---

# pinwiz.ai — Total Cost of Ownership

**Hard cap: $400/mo.** Azure anomaly alert at $300/mo (wired via `Invoke-AlertProof.ps1`).
This document tracks ALL costs — Azure and non-Azure — so the cap is enforced against the
real total, not just the Azure bill.

---

## Monthly cost summary

| Vendor | Service | Billing | Monthly (amortized) |
| --- | --- | --- | --- |
| **Cloudflare** | Registrar — `pinwiz.ai` | $140 / 2 years (IN-57809190, paid 2026-02-19) | **$5.83** |
| **Cloudflare** | Pro plan — WAF, Bot Fight, DDoS, CDN, rate limits | $240/year (annual, 20% saving vs monthly) | **$20.00** |
| **Azure** | AI Search Basic | $74/mo | **$74.00** |
| **Azure** | ACA Web App (Blazor + API, min=1 live) | ~$35/mo | **$35.00** |
| **Azure** | ACA Jobs (scraper + indexer, schedule-triggered) | <$1/mo | **$1.00** |
| **Azure** | Cosmos DB Serverless | $25–100/mo variable | **$25–100** |
| **Azure** | Azure OpenAI completions (gpt-4o + gpt-4.1 ~20%) | $10–40/mo variable | **$10–40** |
| **Azure** | Azure OpenAI embeddings | ~$0.50/mo incremental | **$0.50** |
| **Azure** | Container Registry Basic | $5/mo | **$5.00** |
| **Azure** | Storage (blobs + downloads) | $2–5/mo | **$2–5** |
| **Azure** | Application Insights (1GB/mo cap) | $2–5/mo | **$2–5** |
| **Azure** | Log Analytics (1GB/mo cap) | $2–3/mo | **$2–3** |
| **Azure** | Key Vault Standard | <$1/mo | **$1.00** |
| **Azure** | Functions (Cosmos Change Feed) | $5–20/mo | **$5–20** |
| **Microsoft** | Entra External ID (CIAM, free tier) | $0 | **$0** |
| | | | |
| | **Steady-state total (live)** | | **~$195–370/mo** |
| | **Hard cap** | | **$400/mo** |
| | **Headroom at midpoint** | | **~$120/mo** |

---

## Non-Azure costs detail

### Cloudflare Registrar — `pinwiz.ai`

| Field | Value |
| --- | --- |
| Invoice | IN-57809190 |
| Amount | $140.00 USD |
| Period | Feb 19, 2026 → Feb 18, 2028 (2 years) |
| Amortized | $70.00/year · $5.83/month |
| Next renewal | Feb 18, 2028 |
| Billed to | Early Bird Solutions, LLC |

### Cloudflare Pro plan

| Field | Value |
| --- | --- |
| Plan | Pro |
| Price | $240/year (annual subscription, activated 2026-05-16) |
| Amortized | $20/month (20% saving vs $25/month monthly billing) |
| Next renewal | 2027-05-16 (estimated) |
| What it unlocks | OWASP Core Ruleset, Exposed Credentials Check, Bot Fight Mode enhanced, 10 rate limit rules (vs 1 on Free), 225 Cloudflare rules |
| Required for | `cloudflare_ruleset.zone_waf_managed` + Pro-tier rate limit rules in `infra/cloudflare/waf.tf` and `rate_limit.tf` |

---

## Azure cost monitoring

Azure costs are monitored via:
- **Budget alert at $300/mo** — wired in Bicep (`infra/main-shared.bicep`) and verified by `infra/scripts/Invoke-AlertProof.ps1`
- **Application Insights + Log Analytics** — 1GB/mo cap enforced, diagnostic settings on all resources

The $300/mo alert gives ~$100 headroom to the hard cap before charges become a problem.
Non-Azure costs ($30.83/mo for Cloudflare registrar + Pro) sit outside Azure Cost Management
and are tracked manually here. Total exposure at the $300/mo Azure alert is ~$331/mo — well
within the $400/mo cap.

---

## Cost governance rules

1. **Every non-Azure vendor gets a line in this table.** When a new paid service is added
   (e.g. Postmark for transactional email, a data partnership with a fee), update this doc
   in the same PR that wires up the service.

2. **The $400/mo cap is total, not per-vendor.** Azure alert at $300/mo leaves $100 of
   headroom. Non-Azure costs ($30.83/mo currently) consume ~$31 of that headroom. Any new
   non-Azure cost must be evaluated against the remaining headroom before sign-up.

3. **Amortize multi-year prepayments.** Domain registration and similar upfront costs are
   expressed as monthly amortized figures in the summary table so the cap comparison is
   apples-to-apples.

4. **Renewal dates are tracked here.** Missing a renewal for the domain or the Pro plan
   would silently degrade the production site. The next renewal (domain: Feb 18, 2028) is
   a calendar item, not just a note.

5. **Free-tier dependencies are documented.** Entra External ID is free tier today. If usage
   grows past the free-tier threshold, cost impact must be assessed before it happens.

---

## Development-process economics

The tables above price one axis — **cost-to-run**: the Azure + Cloudflare bill to operate
pinwiz.ai, held under the $400/mo hard cap ($300 alert; ~$195–370/mo steady state). This
section names the other axis — **cost-to-build**: what it costs to author and maintain the app
when AI writes nearly all the code. They are different axes and are not conflated below.

**Why AI-authored delivery is cost-disciplined.**

- **Review economics.** The layered review (first-party `/local-review` + `/standards-audit`,
  then the independent CodeQL / code-quality safety net) is designed to catch issues *before* a
  human-reviewer round-trip, and far before a production incident. The cost gradient is real and
  directional — a pre-PR automated check is cheaper than a reviewer round-trip, which is cheaper
  than a prod incident and the guardrail work it triggers — even though the absolute per-review
  dollar cost for this repo is not separately metered (see the honest gap below).
- **Model-tier discipline.** Mechanical work runs on cheaper models; design, planning, and
  whole-branch review reserve the strongest model. This is the *build* tooling's discipline —
  distinct from the application's *runtime* model routing (next paragraph).
- **Compounding via memory + guardrails.** Each incident is converted into a mechanical guard
  (see [`learning-from-failure.md`](learning-from-failure.md)), so a class of bug is paid for
  once rather than every time it would recur — a cost lever specific to a project with
  institutional memory.

**The honest gap.** Dev-process token/$ spend is **not** currently metered per feature or per
session, so this document states no per-feature build-cost figure. It could be captured as a
future lever — per-session token accounting rolled up per PR — at which point real numbers would
replace this qualitative account. Until then, the build-axis claim is structural, not numeric.

**Cost-to-run cross-link (different axis).** The application's runtime AI spend is governed
separately by [ADR-0015](adr/0015-cost-routing-and-semantic-cache.md): per-agent model routing
(`gpt-4o` default, `gpt-4.1` on the ~15–20% escalation path), a per-call cost ceiling, and
an in-process semantic cache — all inside the $400/mo cap priced above. Those are cost-to-run
figures; they are not build cost.

## Deferred / future cost levers

| Option | Cost impact | Trigger to evaluate |
| --- | --- | --- |
| Disable AI Search when not actively indexing | −$74/mo | Build-only months with no corpus changes |
| Drop ACA web min from 1→0 during dev-only periods | −$35/mo | Extended dev cycle with no public traffic |
| Dream Game image generation | +$50–150/mo | Phase 5 decision point |
| Strategy Tracker | Cost-trivial | Fits within current cap headroom |
| App Gateway WAF v2 + Front Door | +$330+/mo | Multi-region or compliance requirement — explicitly deferred |
| VNet + Private Endpoints | +$30–50/mo | Compliance / payments use case — explicitly deferred |

---

*Last updated: 2026-05-16. Update this doc whenever a new paid service is added or a plan changes.*
