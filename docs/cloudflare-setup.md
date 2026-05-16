# Cloudflare Setup — pinwiz.ai

Configuration reference for the Cloudflare account protecting and proxying `pinwiz.ai`. All sensitive values (Zone ID, Account ID, API tokens, IP addresses) are omitted — retrieve them from the Cloudflare dashboard or the operator's secure credential store.

---

## Overview

Cloudflare sits in front of the Azure Container Apps (ACA) origin and provides:

- **DNS** with CNAME flattening at the apex (`@`)
- **Proxy** (orange cloud) — WAF, Bot protection, TLS termination to browser
- **SSL/TLS** Full (Strict) mode — encrypted and certificate-validated to origin
- **WAF** Cloudflare Managed Ruleset
- **Bot Fight Mode** — detects and challenges automated traffic
- **Transform Rule** — ACME challenge bypass for automatic cert renewal

---

## DNS Records

| Type | Name | Target | Proxy |
|---|---|---|---|
| CNAME | `@` (root) | ACA Wizard app FQDN (see Azure outputs) | Proxied (orange cloud) |

**CNAME flattening note:** Cloudflare uses CNAME flattening to serve the root apex (`pinwiz.ai`) as a CNAME, which is normally prohibited at the zone apex by the DNS spec. This is Cloudflare-managed and transparent.

**During cert operations:** ACA HTTP validation works through the proxy — no DNS toggle needed. If CNAME validation is ever used (e.g. subdomains), temporarily switch to DNS-only (grey cloud) for the duration of the validation, then switch back to proxied.

---

## SSL / TLS

**Mode: Full (Strict)**

| Layer | Description |
|---|---|
| Browser → Cloudflare | Cloudflare Universal SSL cert for `pinwiz.ai` (auto-issued, auto-renewed by Cloudflare) |
| Cloudflare → ACA origin | TLS, certificate validated against the ACA-managed cert for the origin hostname |

**Why Full (Strict) and not Full?** The ACA origin presents a valid, CA-signed Let's Encrypt certificate for `pinwiz.ai` (managed by ACA). Full (Strict) validates the cert hostname — the correct posture when the origin has a proper cert.

**Automatic HTTPS Rewrites:** Off (or bypassed for ACME paths — see Transform Rules). Leaving this on can interfere with Let's Encrypt HTTP-01 validation.

**Edge Certificates:** Cloudflare Universal SSL issues automatically for `pinwiz.ai` and `*.pinwiz.ai`. No operator action required after initial DNS activation; Cloudflare handles renewal.

---

## Security

### WAF — Cloudflare Managed Ruleset

Enabled. "Always active" — Cloudflare's machine-learning powered ruleset covering OWASP top-10 patterns and zero-day exploits. No custom rules required at v1 scale.

### Bot Fight Mode

Enabled. Detects and challenges automated bot traffic before it reaches the ACA origin.

### Block AI Bots

Enabled. Blocks bots classified as AI training crawlers.

**Note for axe-core / Playwright CI validation against the live surface:** If Bot Fight Mode blocks the CI probe IP, add a narrow IP-specific exemption rule rather than disabling Bot Fight globally. Document the exemption IP in `decision-log.md` (not here — the IP itself is sensitive). See Phase 6 risk P6-R3 in `docs/build-spec.md`.

---

## Transform Rules

### ACME Challenge Bypass

Required for ACA HTTP-01 certificate validation and automatic renewal through the Cloudflare proxy.

| Field | Value |
|---|---|
| Name | `ACME challenge bypass` |
| Match | URI Path contains `.well-known/acme-challenge` |
| Action | Disable Automatic HTTPS Rewrites for this path |

**Why this is needed:** Cloudflare's Automatic HTTPS Rewrites rewrite `http://` links to `https://`. Let's Encrypt's HTTP-01 challenge always uses `http://` on port 80. Without this rule, Cloudflare rewrites the challenge URL to HTTPS before ACA can serve the validation token, causing cert issuance and renewal to fail.

**Renewal:** ACA manages certificate renewal automatically. With this bypass rule in place and Bot Fight Mode not blocking the Let's Encrypt validation agent, renewals are fully automatic — no operator action required.

---

## Zero Trust Access — Pre-Launch Gate

Cloudflare Access sits in front of `pinwiz.ai` during development so the site is invisible to the public before launch. Only `jim@earlybirdsolutions.com` can reach it. Remove the policy at launch.

### Setup (dashboard — one-time)

The current API token covers zone-level scopes only. Zero Trust requires account-level access, so configure this through the dashboard.

1. Go to **[one.dash.cloudflare.com](https://one.dash.cloudflare.com)** → your account → **Zero Trust**
2. **Access → Applications → Add an application** → choose **Self-hosted**
3. Fill in:
   - **Application name:** `PinballWizard Dev`
   - **Session duration:** 24 hours
   - **Application domain:** `pinwiz.ai` (include `www.pinwiz.ai` if the www CNAME is active)
4. **Next → Add a policy**
   - **Policy name:** `Earlybird only`
   - **Action:** Allow
   - **Include rule:** Emails → `jim@earlybirdsolutions.com`
5. Save and deploy

Anyone not matching the policy gets a Cloudflare Access login page — no ACA traffic reaches the origin.

### First-time login

Navigate to `https://pinwiz.ai` — Cloudflare redirects to an auth page. Enter `jim@earlybirdsolutions.com` and check your inbox for the one-time code. The session lasts 24 hours.

### Pre-launch removal

**Before announcing the site publicly**, delete the Access application:

1. Zero Trust → Access → Applications → `PinballWizard Dev` → Delete

This immediately opens the site to all traffic. Do not just disable the policy — delete the app so there is no residual config that could accidentally re-activate.

See also: `robots.txt` pre-launch task in [Phase 7 operator to-do](phase7-operator-todo.md).

---

## API Token (for automation / MCP server)

The Cloudflare API token enables Claude Code's Cloudflare skill and any automation scripts.

### Token Configuration

| Field | Value |
|---|---|
| Token name | `PinballWizard` |
| Zone | `pinwiz.ai` only (not all zones) |

**Permissions required:**

| Category | Permission | Level |
|---|---|---|
| Zone | DNS | Edit |
| Zone | Zone Settings | Edit |
| Zone | Transform Rules | Edit |
| Zone | Firewall Services | Edit |

### Client IP Address Filtering

**Recommended:** Restrict the token to your known static IP(s). Even if the token is accidentally exposed, it is unusable from any other IP. If your IP changes (dynamic IP, travel, VPN), you must update or regenerate the token.

To add: API Tokens → token → Edit → **Client IP Address Filtering** → add your IP(s) in CIDR notation.

Do not document specific IP addresses here. Store them with the token in your secure credential store.

### Token Storage

Store the token as a machine-level Windows environment variable — never in any committed file:

```powershell
# Run once in an elevated PowerShell session
[System.Environment]::SetEnvironmentVariable(
    'CLOUDFLARE_API_TOKEN',
    '<token-from-cloudflare-dashboard>',
    'Machine'
)
```

Restart Claude Code after setting the variable so the new machine env var is inherited.

**Do not** store the token in:
- Any file tracked by git
- `settings.json` (team-shared, committed to the APS config repo)
- Any app config file (appsettings.json, .env, etc.)

### MCP Server Setup

Add to `~/.claude/settings.local.json` (gitignored, per-user — safe for server config, no token present):

```json
{
  "mcpServers": {
    "cloudflare": {
      "command": "npx",
      "args": ["mcp-remote", "https://mcp.cloudflare.com/sse"]
    }
  }
}
```

The `CLOUDFLARE_API_TOKEN` machine env var is inherited automatically — no `env` block needed in the settings file.

---

## Custom Domain — ACA Cert Binding

The `pinwiz.ai` domain is bound to the ACA Wizard app with an ACA-managed Let's Encrypt certificate. This requires a two-pass Bicep deployment (see `docs/build-spec.md` § Phase 7 and the `wizardCustomDomainCertReady` parameter in `infra/main-shared.bicep`).

### Cert Provisioning (one-time, already complete)

1. **Pass 1** (`wizardCustomDomainCertReady=false`): ACA registers `pinwiz.ai` as a custom hostname with `bindingType=Disabled`.
2. ACA issues a Let's Encrypt cert via HTTP-01 validation through the Cloudflare proxy (requires the ACME bypass Transform Rule above).
3. **Pass 2** (`wizardCustomDomainCertReady=true`): ACA binding switches to `SniEnabled` with the issued cert.

### Cert Renewal (automatic)

ACA renews the Let's Encrypt cert automatically before expiry (~90 days). The HTTP-01 challenge flows through the Cloudflare proxy using the ACME bypass rule. No operator action required unless:
- The bypass rule is accidentally deleted → re-add it
- Bot Fight Mode blocks the Let's Encrypt validation agent → add IP exemption for Let's Encrypt

### Cloudflare SSL mode during cert operations

Keep Cloudflare proxy **active (orange cloud)** during cert renewal. The HTTP bypass rule handles validation. No proxy toggle needed.

---

## Operational Runbook References

| Scenario | Runbook |
|---|---|
| Site down / availability alert fires | `docs/runbooks/01-incident-response.md` |
| Cert renewal fails | Re-check ACME bypass Transform Rule; check Bot Fight exemptions |
| Need to temporarily disable proxy | DNS Records → CNAME `@` → Edit → proxy toggle (restore within same session) |
| API token compromised | Dashboard → API Tokens → Revoke token → regenerate → update machine env var → restart Claude Code |

---

## Change Log

| Date | Change | Operator |
| --- | --- | --- |
| 2026-05-15 | Initial setup: CNAME, Proxied, WAF, Bot Fight, Block AI Bots | Jim Keeley |
| 2026-05-15 | SSL/TLS Full (Strict) configured | Jim Keeley |
| 2026-05-15 | ACME challenge bypass Transform Rule added | Jim Keeley |
| 2026-05-15 | Custom domain `pinwiz.ai` bound to ACA with ACA-managed cert | Jim Keeley |
| 2026-05-15 | Zero Trust Access app created (`PinballWizard Dev`) — `pinwiz.ai` gated to `jim@earlybirdsolutions.com` only. **Remove before public launch** — see PL1 in `phase7-operator-todo.md` | Jim Keeley |
