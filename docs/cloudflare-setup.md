---
status: Active
phase: Phase-6
owner: Jim
last-reviewed: 2026-06-18
supersedes: ""
---

# Cloudflare Setup — pinwiz.ai

Configuration reference for the Cloudflare account protecting and proxying `pinwiz.ai`.

**All configuration is IaC-managed** as of 2026-05-16. The canonical source of truth is
`infra/cloudflare/` — not this document and not the Cloudflare dashboard. Any change made
in the dashboard without a corresponding PR is drift; the weekly CI drift-detection workflow
will flag it.

Sensitive values (Zone ID, Account ID, API tokens, certificate data, private keys) are
omitted throughout. Retrieve them from Key Vault (`pinwiz-kv-dev-buutj`), the operator's
secure credential store, or the OpenTofu state output.

---

## Architecture

```text
Browser
  │  HTTPS (Cloudflare Universal SSL cert — auto-issued, auto-renewed)
  ▼
Cloudflare Edge  (proxy, WAF, Bot Fight, DDoS, rate limits, headers)
  │  HTTPS (Origin CA cert — issued by Cloudflare CA, stored in Key Vault)
  ▼  [ssl=strict — origin presents the Cloudflare Origin CA cert; see §SSL/TLS below]
Azure Container Apps  (pinwiz-ca-wizard-dev)
  │
  ▼
Application (Blazor Web App + ASP.NET Core API)
```

Cloudflare terminates TLS at the edge. Every request passes through the WAF, bot
detection, and rate limiter before reaching ACA. The ACA origin presents a Cloudflare
Origin CA certificate (trusted only by Cloudflare's edge — not by browsers directly).

---

## IaC reference

| File | What it manages |
| --- | --- |
| `infra/cloudflare/dns.tf` | DNS records, DNSSEC, CAA, SPF, DMARC, null MX, email routing, ACA verification |
| `infra/cloudflare/tls.tf` | Zone TLS settings, HSTS, Origin CA certificate, Authenticated Origin Pulls |
| `infra/cloudflare/waf.tf` | WAF managed rulesets (OWASP PL1, Exposed Credentials), custom WAF rules |
| `infra/cloudflare/rate_limit.tf` | Rate limiting rules |
| `infra/cloudflare/headers.tf` | Security response headers via Transform Rules |
| `infra/cloudflare/access.tf` | Zero Trust Access applications (template — commented out) |
| `infra/cloudflare/logpush.tf` | Log shipping to Azure Blob (inactive until `logpush_destination` is set) |

See `infra/cloudflare/PLAN.md` for the full strategy, tool rationale, and state backend
design. See `infra/cloudflare/README.md` for the developer workflow and token setup.

---

## Plan and apply workflow

```powershell
# Always work from the isolated worktree to avoid branch-switching conflicts
cd C:\projects\PinballWizard\.worktrees\cloudflare\infra\cloudflare

$env:CLOUDFLARE_API_TOKEN = [System.Environment]::GetEnvironmentVariable('CLOUDFLARE_API_TOKEN_PINWIZ', 'Machine')

~\.local\bin\tofu.exe plan    # review changes
~\.local\bin\tofu.exe apply   # apply after review
```

**Never make changes directly in the Cloudflare dashboard.** The weekly drift-detection
CI run (`cloudflare-plan.yml` on a schedule) will catch out-of-band changes and alert.

---

## DNS

All records are managed by `dns.tf`. Current record set:

| Type | Name | Content | Proxy | Purpose |
| --- | --- | --- | --- | --- |
| CNAME | `@` (apex) | ACA Wizard FQDN | Proxied | Routes traffic through Cloudflare |
| CNAME | `www` | `pinwiz.ai` | Proxied | www redirect |
| CAA | `@` | `0 issue letsencrypt.org` | DNS only | Restricts cert issuers |
| CAA | `@` | `0 issue pki.goog` | DNS only | Restricts cert issuers |
| CAA | `@` | `0 issue digicert.com` | DNS only | Restricts cert issuers |
| CAA | `@` | `0 iodef mailto:security@pinwiz.ai` | DNS only | CA violation notifications |
| MX | `@` | `.` (null MX) | DNS only | RFC 7505 — domain accepts no mail |
| TXT | `@` | `v=spf1 -all` | DNS only | No host authorized to send mail |
| TXT | `_dmarc` | `v=DMARC1; p=none; rua/ruf=...` | DNS only | DMARC report-only |
| TXT | `asuid.pinwiz.ai` | ACA domain verification token | DNS only | Required for ACA cert renewal |

### DNSSEC

Enabled. Cloudflare automatically published the DS record at the registrar (Cloudflare is
both DNS provider and registrar — no manual DS publication required). Status: active.

DS record details (algorithm 13 / ECDSA-SHA256, digest type 2 / SHA-256) are available
via `tofu output dnssec_ds_record` or the Cloudflare dashboard → DNS → Settings.

### Email routing

Explicitly disabled via `cloudflare_email_routing_settings` in `dns.tf`. The domain has
no mail use case. The null MX record enforces this at the DNS layer; email routing
enforcement prevents accidental re-enablement via the dashboard.

---

## SSL / TLS

Managed by `tls.tf`. Zone-level settings:

| Setting | Value | Notes |
| --- | --- | --- |
| SSL mode | `strict` | Requires a valid cert on the origin — see status below |
| Min TLS version | 1.2 | Drops TLS 1.0/1.1 |
| TLS 1.3 | On | |
| Always Use HTTPS | On | Redirects all HTTP to HTTPS |
| Automatic HTTPS Rewrites | On | |
| Opportunistic Encryption | On | |
| HSTS | Enabled | max_age=31536000 (1 year), include_subdomains, preload |

### SSL mode status

`ssl=strict` requires the ACA origin to present a certificate that is valid and either
trusted by Cloudflare's CA bundle or is a Cloudflare Origin CA certificate.

**Current state (2026-06-18):** the ACA ingress presents the **Cloudflare Origin CA
certificate** for `pinwiz.ai` (see below), so `ssl=strict` is satisfied end-to-end. No
`ssl=full` stopgap is needed.

This replaced the earlier Azure-managed (Let's Encrypt) certificate, whose auto-renewal
could not complete. Managed-cert renewal runs an ACME domain-control challenge against
`pinwiz.ai`, which is a *proxied* Cloudflare record, so the challenge lands on the
Cloudflare edge and never reaches the ACA ingress — renewal failed every cycle (Azure
Advisor flagged it 2026-06-18). The Origin CA cert removes ACME from the path entirely.
Full rationale: [ADR-0038](adr/0038-origin-ca-cert-for-aca-origin.md).

### Origin CA certificate

A 15-year RSA-2048 Cloudflare Origin CA certificate is generated by `tls.tf`:

- Valid for `pinwiz.ai` and `*.pinwiz.ai`
- Trusted ONLY by Cloudflare's edge — not by browsers or other CAs
- **Lives in the OpenTofu state**, exposed as the sensitive outputs
  `origin_certificate_pem` and `origin_private_key_pem` (`outputs.tf`). It is NOT
  committed to the repository. *(Earlier revisions of this doc claimed the cert/key were
  pre-stored in Key Vault as `cloudflare-origin-cert` / `cloudflare-origin-key` — that was
  never implemented; the only source of the material is tofu state.)*
- Imported into Key Vault as the certificate object `cloudflare-origin-pinwiz` by
  `infra/scripts/Import-OriginCaCertToKeyVault.ps1` (an operator step — the key material
  lives in tofu state, not source, so the import is not pure IaC).
- The ACA managed-environment certificate resource references that KV cert via
  `certificateKeyVaultProperties` + the `acaIdentity` managed identity; the Wizard custom
  domain binds to it (`SniEnabled`). See [ADR-0038](adr/0038-origin-ca-cert-for-aca-origin.md)
  and `infra/modules/shared.bicep`.

### Authenticated Origin Pulls (AOP)

Enabled via `cloudflare_authenticated_origin_pulls_settings`. AOP forces the ACA origin
to verify a Cloudflare-signed client certificate on every inbound request. Any request
that reaches the origin without this certificate (i.e., bypassing Cloudflare by hitting
the ACA hostname directly) fails the TLS handshake.

**Note:** AOP is enabled on the Cloudflare side but **not yet enforced at the origin**.
True enforcement requires the ACA ingress to validate Cloudflare's client certificate
(`ingress.clientCertificateMode` + app-level validation) — a separate, larger change.
Deferred and tracked as a follow-up; the Origin CA cert install (ADR-0038) is server-cert
only and does not turn on AOP enforcement.

---

## WAF (Cloudflare Pro plan — $240/year, activated 2026-05-16)

Managed by `waf.tf`. Two ruleset resources:

### Managed rulesets (`zone_waf_managed`)

Executes three Cloudflare-published managed rulesets in the `http_request_firewall_managed`
phase:

| Ruleset | Mode | Notes |
| --- | --- | --- |
| Cloudflare Managed Ruleset | Execute | Cloudflare's ML-powered rules covering OWASP top-10 and zero-days |
| OWASP Core Ruleset | Execute (PL1 only) | Paranoia Level 1 — low false-positive rate; promote PL2 after baseline |
| Exposed Credentials Check | Execute | Detects credential stuffing with known-breached credentials |

PL2–4 are explicitly disabled via `overrides` in the IaC. Promote after reviewing the WAF
Analytics dashboard for false positives.

### Custom WAF rules (`zone_waf_custom`)

Three custom rules in the `http_request_firewall_custom` phase:

| Rule | Action | Expression |
| --- | --- | --- |
| Block scanner paths | Block | Requests to `/.env`, `/.git/config`, `/wp-admin`, etc. |
| Block wrong Host header | Block | Host header is not `pinwiz.ai` or `www.pinwiz.ai` |
| Challenge low-entropy UA | Managed challenge | User-Agent is empty or < 10 chars (with `.well-known/` exemption) |

---

## Rate limiting (Cloudflare Pro — 2 rules)

Managed by `rate_limit.tf`. Pro plan allows 2 rules in `http_ratelimit`.

| Rule | Action | Threshold | Window |
| --- | --- | --- | --- |
| Global ceiling | Block | 600 req/min per IP + colo | 60s, 10min mitigation |
| Chat/RAG endpoint | Block | 30 req/min per IP + colo | 60s, 10min mitigation |

Auth endpoint rule (5 req/min) is deferred — the `/api/auth` endpoint doesn't exist yet.
Add it when auth ships; upgrading to Business plan (10 rules) removes the constraint.

---

## Security response headers

Managed by `headers.tf` via the `http_response_headers_transform` phase. Injected on all
responses:

| Header | Value |
| --- | --- |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Permissions-Policy` | Disables accelerometer, camera, geolocation, gyroscope, magnetometer, microphone, payment, usb |
| `Content-Security-Policy-Report-Only` | `default-src 'self'` + explicit allowlists; reports to `/_csp-reports` |
| `X-Powered-By` | Removed |
| `X-AspNet-Version` | Removed |
| `X-AspNetMvc-Version` | Removed |

**Note:** `Server` header removal is not possible via Transform Rules (Cloudflare API
rejects it as a protected system header, regardless of plan).

When CSP reports are clean after a week, promote from `Content-Security-Policy-Report-Only`
to enforced `Content-Security-Policy` via a PR to `headers.tf`.

---

## Zero Trust Access

`access.tf` contains a commented-out template for protecting a staging subdomain. Not
active in the current configuration — the pre-launch Access gate from Phase 7 was a
dashboard-only configuration and was removed before public launch.

To add Access protection in future: uncomment the resource in `access.tf`, add
`Account: Access: Apps and Policies: Edit` to the IaC API token, and apply.

---

## Logpush (inactive)

`logpush.tf` defines two Logpush jobs (HTTP requests + firewall events) that are
conditional on the `logpush_destination` variable. Currently empty — jobs are not created.

To activate: set `TF_VAR_logpush_destination` to an Azure Blob SAS URL in GitHub secrets
and redeploy. The SAS token goes into Key Vault; do not hardcode it in tfvars.

---

## API token

The IaC uses a Cloudflare API token named **ClaudeCodeJim**, stored as the machine-level
environment variable `CLOUDFLARE_API_TOKEN_PINWIZ`. The token is scoped to the `pinwiz.ai`
zone and the Earlybird account only.

Required permissions:

| Scope | Resource | Level |
| --- | --- | --- |
| Zone | DNS | Edit |
| Zone | Zone Settings | Edit |
| Zone | Transform Rules | Edit |
| Zone | Firewall Services | Edit |
| Zone | SSL and Certificates | Edit |
| Zone | Zone WAF | Edit |
| Zone | Logs | Edit |
| Zone | Email Routing Rules | Edit |
| Account | Account Settings | Read |
| Account | Logs | Edit |

No IP address filter — required for GitHub Actions CI runners.

Full setup procedure (including the Cloudflare UI IP-filter workaround using `0.0.0.0/0`)
is documented in `infra/cloudflare/README.md` § API token setup.

**Token rotation:** Cloudflare rotates the token value every time you save changes to its
permissions. After any permission edit, update `CLOUDFLARE_API_TOKEN_PINWIZ` in the
machine-level environment variable (elevated PowerShell required) and restart Claude Code.

---

## Costs

| Service | Billing | Monthly (amortized) |
| --- | --- | --- |
| Registrar — `pinwiz.ai` | $140 / 2 years (IN-57809190, Feb 2026) | $5.83 |
| Pro plan | $240/year (annual, activated 2026-05-16) | $20.00 |
| **Total Cloudflare** | | **$25.83/mo** |

See `docs/cost-tracking.md` for the full cost breakdown including Azure.

---

## Pending / follow-up items

| Item | Priority | Notes |
| --- | --- | --- |
| ~~Install Origin CA cert on ACA origin~~ | Done | Resolved 2026-06-18 via ADR-0038 — ACA ingress presents the Origin CA cert; `ssl=strict` satisfied |
| Enforce Authenticated Origin Pulls at the ACA origin | Medium | Needs ACA `ingress.clientCertificateMode` + app-level validation of Cloudflare's client cert (ADR-0038, deferred) |
| Promote CSP from Report-Only to enforced | Medium | After a week of clean CSP reports in Cloudflare dashboard |
| Promote OWASP PL1 to PL2 | Medium | After reviewing WAF Analytics for false positives |
| Auth endpoint rate limit rule | Low | Add when `/api/auth` ships |
| DMARC p=none → p=quarantine → p=reject | Low | After confirming no legitimate mail senders |
| Logpush to Azure Blob | Low | When log retention is required |

---

## Change log

| Date | Change | Via |
| --- | --- | --- |
| 2026-05-15 | Initial setup: CNAME, proxy, WAF, Bot Fight, Block AI bots, SSL Full Strict, ACME bypass rule | Dashboard |
| 2026-05-15 | Zero Trust Access pre-launch gate (`jim@earlybirdsolutions.com` only) | Dashboard |
| 2026-05-15 | Custom domain `pinwiz.ai` bound to ACA with ACA-managed Let's Encrypt cert | Dashboard + Bicep |
| 2026-05-16 | Full Cloudflare config migrated to IaC (OpenTofu + cloudflare/cloudflare v5) | `infra/cloudflare/` |
| 2026-05-16 | Zone upgraded to Pro plan ($240/year) | Dashboard |
| 2026-05-16 | DNSSEC enabled (DS record auto-published by Cloudflare registrar) | IaC + Dashboard |
| 2026-05-16 | CAA records added (letsencrypt.org, pki.goog, digicert.com) | IaC |
| 2026-05-16 | SPF `v=spf1 -all` + DMARC p=none + null MX added | IaC |
| 2026-05-16 | Email Routing disabled (was unconfigured; now IaC-managed) | IaC |
| 2026-05-16 | www CNAME added (proxied) | IaC |
| 2026-05-16 | OWASP PL1 + Exposed Credentials WAF rulesets activated | IaC |
| 2026-05-16 | Custom WAF rules: scanner block, host header block, low-UA challenge | IaC |
| 2026-05-16 | Rate limits: global 600/min + chat/RAG 30/min | IaC |
| 2026-05-16 | Security response headers via Transform Rules | IaC |
| 2026-05-16 | Authenticated Origin Pulls enabled | IaC |
| 2026-05-16 | Origin CA certificate (15yr, RSA-2048) generated (lives in tofu state only; not imported to Key Vault until 2026-06-18) | IaC |
| 2026-05-16 | TLS min version 1.2, Always Use HTTPS on, HSTS 1yr | IaC |
| 2026-05-16 | API token updated to ClaudeCodeJim with full IaC permission set | Dashboard |
| 2026-06-18 | Wizard custom domain rebound from Azure managed (Let's Encrypt) cert to Cloudflare Origin CA cert (ADR-0038); managed-cert renewal-through-proxy failure resolved | IaC (Bicep) + operator script |
