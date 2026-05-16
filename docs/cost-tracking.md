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
| **Azure** | Azure OpenAI completions (gpt-4o-mini + gpt-4.1 ~20%) | $10–40/mo variable | **$10–40** |
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
