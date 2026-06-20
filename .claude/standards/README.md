# PinballWizard Standards

Machine-checkable, enforcement-first standards for autonomous-session
control. Posture and shared rules: [`pinball-standards-protocol.md`](pinball-standards-protocol.md).

| Domain | Prefix | Status | applies-to (summary) |
|---|---|---|---|
| [provenance](provenance/STANDARD.md) | `PROV-` | active | scrapers, catalog, RAG chunk mappers |
| [polite-scraping](polite-scraping/STANDARD.md) | `POLITE-` | active | `src/**/Scraping/**` |
| [persistence-cosmos](persistence-cosmos/STANDARD.md) | `COSMOS-` | active | `src/**/Persistence/**`, Cosmos options/repos |
| [observability-and-honest-failure](observability-and-honest-failure/STANDARD.md) | `OBS-` | active | fallback paths, health, logging, metrics |
| [testing](testing/STANDARD.md) | `TEST-` | active | `tests/**`, contract tests |
| [delivery](delivery/STANDARD.md) | `DLV-` | active | commits, `infra/scripts/**`, runbooks, build |
| [rag-agent](rag-agent/STANDARD.md) | `RAG-` | active | AI/RAG/Foundry/AI-Search code, agent prompts |
| [frontend-blazor](frontend-blazor/STANDARD.md) | `FE-` | active | `src/PinballWizard.Web(.Client)/**`, `*.razor` |
| [community-posture](community-posture/STANDARD.md) | `COMM-` | active | community_resources seed, agent prompts, destination resolver |
| [iac-deploy](iac-deploy/STANDARD.md) | `IAC-` | active | `infra/**` |

**Wave 2 complete.** All converted domains are active; future domains will follow the same structure. Full invariant index: [`../INVARIANTS.md`](../INVARIANTS.md).

Run [`/standards-audit`](../skills/standards-audit/SKILL.md) (mechanical) and `/local-review` (qualitative) before any push.
