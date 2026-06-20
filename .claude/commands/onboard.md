---
description: Print the repo working agreement for a new contributor or evaluator
---
<!-- authored-for: PinballWizard (not vendored) -->

# Onboard

Print the following orientation for a newcomer or evaluator. Keep it concise and scannable.

---

## What this repo is

**PinballWizard** is a customer-facing showcase / reference application that demonstrates enterprise-class AI architecture end-to-end: Clean Architecture, .NET Aspire, Azure (Cosmos, AI Search, Azure OpenAI / Foundry, Container Apps), Entra External ID, IaC, and event-driven RAG. The pinball domain is the vehicle — the engineering rigour is the point.

- **Phase 1** (live): polite, manufacturer-fanned-out scraper → Cosmos with rich provenance metadata
- **Phase 4** (current): event-driven RAG pipeline with source-cited Q&A, agent orchestration via Microsoft Foundry

Key docs:
- `CLAUDE.md` — full project context, architecture overview, working conventions
- `docs/vision.md` — product / showcase intent
- `docs/adr/` (index: `docs/adr/README.md`) — architecture decision records
- `.claude/INVARIANTS.md` — locked non-negotiable invariants

---

## PR / contribution flow

1. **Feature branch** — never commit directly to `main`
2. **`/local_review`** (qualitative, Step 0) — spawns the `local-review` skill; treat 🔴 findings as blocking
3. **`.claude/PR-AUDIT.md`** (12-item mechanical checklist, Step 1) — must pass before PR creation
4. **Commit** — conventional format `<type>(<scope>): <message>`; personal identity only (`94459922+jkeeley2073@users.noreply.github.com`); **NO** `Co-Authored-By: Claude` trailer on any commit in this repo
5. **`gh pr create`** — GitHub PRs only; add and verify the `claude-code` label after creation
6. **No Jira, no Azure DevOps, no work-item time-tracking** — tickets live in GitHub Issues

---

## Claude Code config

The `.claude/` directory is self-contained and CI-guarded:

- **`.claude/commands/`** — slash commands (this file is one of them)
- **`.claude/skills/`** — skills (local-review, smart-commit, etc.)
- **`.claude/agents/`** — specialist sub-agents
- **`.claude/rules/`** — standing project rules
- **`.claude/README.md`** — config directory orientation
- `docs/claude-code.md` (ADR-0040) — rationale + ownership model

CI guard jobs:
- **Claude Config Guard** workflow — runs `scripts/check_claude_frontmatter.py` and `scripts/assert_no_excluded_aps_skills.py` on every PR touching `.claude/`
- All `.claude/commands/*.md` files must start with `---` on line 1 (byte-0 frontmatter rule)

---

## Key invariants (`.claude/INVARIANTS.md` is authoritative)

- **Provenance is sacred** — every scraped item traces back to its source URL; never drop `Source` / `DiscoveryUrl` / `GameSlug`
- **Polite-by-construction scraping** — all outbound HTTP routes through `IPolitenessGate`; no bare `HttpClient.GetAsync` in scraper code; `robots.txt` honoured unconditionally
- **Fallbacks must not hide failures** — degrade visibly, log + meter the failure; never present synthetic content as real output
- **Personal identity only** — commits must use the personal GitHub no-reply email above; the work account must never touch this repo
- **Deployment Stacks only** — `az stack sub/group create`; never `az deployment sub/group create`
- **Cosmos schema via ARM, items via data-plane SDK** — no Cosmos containers in Bicep; `--ensure-cosmos-containers` is the canonical creator

---

*Read `CLAUDE.md` in full before making substantive changes. When in doubt: would a sceptical prospective customer read this code or commit and gain confidence, or lose it?*
