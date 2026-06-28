# 0047 — Anthropic Workload Identity Federation for GitHub Actions CI

**Status:** Accepted  
**Date:** 2026-06-28

## Context

Both CI workflows (`claude.yml` and `pr-feedback-triage.yml`) previously
authenticated to the Anthropic API using a long-lived `ANTHROPIC_API_KEY`
stored as a GitHub Actions secret. Long-lived secrets have a standing exposure
window: they are valid indefinitely, any maintainer with repo admin access can
read them, and rotating them requires coordinating every consumer. A leaked key
is misusable until someone notices and revokes it.

Anthropic launched Workload Identity Federation (WIF) in June 2026, enabling
OIDC-based keyless authentication. GitHub Actions issues a signed OIDC JWT per
workflow run. Anthropic validates the JWT against a configured federation rule
and returns a short-lived access token (10-minute TTL by default). No static
key is stored anywhere.

## Decision

Replace `secrets.ANTHROPIC_API_KEY` in both workflows with a two-step OIDC
exchange using GitHub's native OIDC provider:

1. **Fetch** a GitHub OIDC JWT with audience `https://api.anthropic.com` via
   `actions/github-script@v8` (`core.getIDToken`).
2. **Exchange** the JWT for an Anthropic access token via
   `POST https://api.anthropic.com/v1/oauth/token` with
   `grant_type: urn:ietf:params:oauth:grant-type:jwt-bearer`.

The four federation identifiers (`ANTHROPIC_FEDERATION_RULE_ID`,
`ANTHROPIC_ORGANIZATION_ID`, `ANTHROPIC_SERVICE_ACCOUNT_ID`,
`ANTHROPIC_WORKSPACE_ID`) are stored as GitHub Actions **Variables** (not
secrets — they are non-sensitive config).

### Anthropic Console configuration

| Field | Value |
|---|---|
| Issuer | `github-actions` (GitHub's OIDC provider) |
| JWKS source | `discovery` (auto-derived from issuer URL) |
| Subject pattern | `repo:Early-Bird-Solutions-LLC/PinballWizard:*` |
| Required claim: `repository_owner_id` | `280936209` (numeric — rename-safe) |
| Required claim: `repository_owner` | `Early-Bird-Solutions-LLC` |
| Required claim: `repository` | `Early-Bird-Solutions-LLC/PinballWizard` |
| Target service account | `pinballwizard-ci` |
| Workspace | Claude Code |
| OAuth scope | `workspace:developer` |
| Token lifetime | 600 seconds |

The org numeric ID (`280936209`) pins trust to the immutable owner: if the org
name is ever released and re-registered by another party, their tokens will not
match this rule.

## Alternatives considered

**Keep `ANTHROPIC_API_KEY` secret** — Simplest operationally, but maintains a
standing secret with no expiry. Rejected because WIF is now available and the
zero-standing-secret model is strictly better for a showcase demonstrating
security posture.

**Azure Managed Identity WIF** — Would route the OIDC exchange through an Azure
MI, adding an unnecessary Azure dependency to a CI-only concern. GitHub native
OIDC is direct and simpler. Rejected.

**SDK auto-refresh via `ANTHROPIC_IDENTITY_TOKEN_FILE`** — The Anthropic SDK
supports automatic token exchange and refresh when `ANTHROPIC_IDENTITY_TOKEN_FILE`
and the four `ANTHROPIC_*` env vars are set. However, `claude-code-action@v1`
wraps the SDK in a way where env var passthrough is not guaranteed. The manual
exchange approach (fetch → exchange → pass as `anthropic_api_key`) is robust
regardless of the action's SDK version. If a future version of the action
officially supports SDK-native WIF, this ADR should be revisited.

## Consequences

- **No static API key in GitHub secrets** — eliminates the standing exposure
  window. The existing `ANTHROPIC_API_KEY` secret should be deleted after the
  first successful CI run confirms the WIF path works.
- **Two extra CI steps per workflow run** — each run adds one
  `actions/github-script` step and one `curl`+`jq` shell step. Overhead is
  under 5 seconds.
- **Token lifetime is 10 minutes** — sufficient for typical `@claude` sessions
  and PR triage runs. If a long-running session exceeds this, the action will
  receive an auth error mid-run. Increase the token lifetime in the Anthropic
  Console (federation rule → Edit → Token lifetime) if this becomes a recurring
  problem in practice. Do not increase it speculatively.
- **Four GitHub Actions Variables required** — `ANTHROPIC_FEDERATION_RULE_ID`,
  `ANTHROPIC_ORGANIZATION_ID`, `ANTHROPIC_SERVICE_ACCOUNT_ID`,
  `ANTHROPIC_WORKSPACE_ID`. These are not secrets but must be present or the
  exchange step fails with an explicit `::error::` log line and non-zero exit.
- **Authentication events are auditable** — every exchange appears in the
  Anthropic Console under Workload identity → Authentication events, with
  timestamp and claim detail.
- **Revocation is instant** — deleting the `pinballwizard-claude-ci` federation
  rule in the Anthropic Console immediately blocks all future CI runs from
  obtaining a token, with no secret rotation needed.
- **Showcase value** — `/auth-demo` explains the approach for engineering-curious
  prospects, demonstrating security-by-construction rather than
  security-as-afterthought.
