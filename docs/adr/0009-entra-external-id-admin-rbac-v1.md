# 0009 — Microsoft Entra External ID for admin RBAC in v1

**Status:** Accepted
**Date:** 2026-05-02

## Context

pinwiz.ai is a public anonymous community resource — most of it
(Wizard chat, search, browse, citations) requires no login. But the
platform also needs:

- An **admin surface** (`/admin/*`) for the IngestionSources whitelist
  (ADR 0007), telemetry views, and future user-management — used by
  the maintainer (and eventually a small group of curators).
- A future **end-user authentication path** for Digital Passport,
  scores, trade matchmaker, Strategy Tracker — features that store
  user-tied data and so cannot be anonymous.

We need an identity provider that:

1. Supports both internal (admin RBAC) and end-user (CIAM with social
   login) auth from the same tenant.
2. Integrates cleanly with ASP.NET Core / Blazor.
3. Doesn't require running our own auth service.
4. Has acceptable cost characteristics at v1 traffic volumes (the
   platform is anonymous-first, so monthly active authenticated users
   in v1 will be tens, not thousands).
5. Lets us defer end-user social-login configuration without leaving
   the admin surface unsecured.

## Decision

We use **Microsoft Entra External ID** (CIAM tenant) as the single
identity provider, with **two auth posture tiers**:

### Tier 1 — Anonymous reads (no auth required)

Wizard chat, search, browse, faceted filtering, citations. Cloudflare
Bot Fight Mode + IP rate limiting handles abuse. No login button is
shown for these flows.

### Tier 2 — Internal admin (Entra RBAC, **shipping in v1**)

The `/admin/*` route group requires authentication and authorization
against an Entra `GlobalAdmin` role (and any other internal roles we
define later — `SourceCurator`, `Reviewer`, etc.).

This is **not deferred to v2.** It ships with the first Blazor build
because the IngestionSources whitelist (ADR 0007) is admin-only and
must be safe from day one. Without this gate, the admin UI either
doesn't exist at v1 (limiting operations) or is exposed publicly
(unacceptable).

### Tier 3 — End-user CIAM (Entra federated identities, ships when passport features ship)

When Digital Passport / scores / trade-matchmaker / Strategy Tracker
features ship, they require end-user login via federated identities
configured in the same Entra External ID tenant:

- Google
- Apple
- Discord (the pinball community lives heavily on Discord, so this is
  non-negotiable)

The Entra tenant is **provisioned in v1** (because Tier 2 needs it),
but the federated identity providers and the user-facing login UI are
**configured when the consuming features ship**. This is config-only
work; no v2-specific architecture changes needed.

## Consequences

**Positive:**
- **One identity provider, two postures.** No need to integrate two
  separate auth systems (e.g., Azure AD for admin + Auth0 for end
  users). Simpler ops, simpler code, simpler reasoning.
- **Free tier covers v1.** Entra External ID's free tier covers tens
  of thousands of monthly active users — well within v1 needs.
- **First-class ASP.NET Core integration.** `AddAuthentication()` +
  `AddMicrosoftIdentityWebApp()` is the standard path; no custom
  middleware.
- **Admin is safe from day one.** The IngestionSources surface ships
  alongside the public UI without a "trust the URL is hidden" posture.
- **End-user features can ship incrementally** without auth migration
  — when Strategy Tracker is ready, we add Discord federation and
  ship; no new auth provider, no breaking changes.

**Negative:**
- **Vendor lock-in to Microsoft identity.** A migration off Entra
  would touch every authenticated route. We accept this — Entra
  External ID is well-maintained, the ASP.NET integration is
  first-party, and the alternative (running our own IdP or paying for
  Auth0) carries its own costs.
- **Tenant configuration overhead even before end-user features
  ship.** Provisioning Entra External ID, configuring the application
  registration, setting up the role definitions — that's v1 work even
  though most users won't log in until later.
- **Cosmos partition strategy for user-tied data assumes Entra
  external user IDs.** When end-user features ship, they partition
  by Entra `oid` (the immutable external user ID). This is a small
  upfront design constraint, not a migration risk.

## Alternatives considered

- **Auth0.** Rejected — adds a vendor, costs more at scale, weaker
  ASP.NET Core integration than first-party Entra.
- **Azure AD B2C.** Rejected — Entra External ID is its successor and
  has a clearer roadmap; new projects should not start on B2C.
- **Custom JWT-based auth with social logins via Passport.NET-style
  middleware.** Rejected — auth is not a place to roll our own. Bugs
  here become headlines.
- **Defer admin to v2 (no admin surface in v1).** Rejected — the
  IngestionSources whitelist (ADR 0007) needs to be runtime-editable
  from v1, and that requires a safe admin surface from v1.
- **Defer admin to v2 and use a hard-coded URL with an obscure path.**
  Rejected — security through obscurity, not acceptable for a public
  portfolio piece.

## References

- [`docs/infra_analysis.md`](../infra_analysis.md) §1 — Entra External
  ID listed as the identity provider.
- `project_phase2_architecture_decisions.md` (private project memory, not in this repo)
  — full auth strategy, tiers, and rationale.
- ADR 0007 — IngestionSources as Cosmos data; the surface that
  motivates Tier 2.
