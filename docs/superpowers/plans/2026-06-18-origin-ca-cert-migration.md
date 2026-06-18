# Origin CA Cert Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the `pinwiz-ca-wizard-dev` ACA ingress present the long-lived Cloudflare Origin CA certificate instead of the auto-renewing Azure managed (Let's Encrypt) certificate, permanently eliminating the "Managed Certificate failed to renew" Advisor recommendation.

**Architecture:** The Cloudflare Origin CA cert (15-yr, already generated in the `infra/cloudflare` tofu stack, valid for `pinwiz.ai` + `*.pinwiz.ai`) is packaged from its PEM cert+key into a PFX and imported into Key Vault `pinwiz-kv-dev-buutj` as a certificate object. A new `Microsoft.App/managedEnvironments/certificates` resource references that KV cert via the existing `acaIdentity` user-assigned managed identity (which already holds Key Vault Secrets User). The Wizard app's custom-domain binding switches to this cert (`SniEnabled`). The Azure managed-cert resource and its two-pass `wizardCustomDomainCertReady` scaffolding are removed. `ssl=strict` on the Cloudflare zone — already set — becomes legitimately correct once the origin presents a Cloudflare-trusted cert.

**Tech Stack:** Bicep (`Microsoft.App/managedEnvironments/certificates@2024-03-01`), Azure Key Vault (certificate import), OpenTofu (Cloudflare stack — read-only source of the cert/key), Azure Deployment Stacks, PowerShell 7.

## Global Constraints

- **Personal identity only** — all git commits authored as `94459922+jkeeley2073@users.noreply.github.com`; never the work account. (Locked invariant #5.)
- **Personal subscription only** — every Azure/tofu operation runs against sub `b1f33f17-…` ("pinwiz.ai", tenant `9793cd0f-…`) in an **isolated** az CLI context (setup-azure pattern). The default session in this machine is the *work* "APS Subscription" — never operate on personal resources from it.
- **Deployment Stacks only** — deploys go through `az stack group create` (via `infra/scripts/Deploy-SharedResources.ps1`); never `az deployment group create`. (Locked invariant.)
- **Schema CRUD via ARM, item CRUD via data-plane SDK** — unrelated here, but the KV cert *import* is an operator data-plane action (`az keyvault certificate import`), not Bicep, by design (the PEM/key live in tofu state, not in source).
- **No secret in source** — the PFX, the PEM key, and the cert password never get committed. They flow tofu state → local temp → Key Vault only.
- **Showcase bar** — ADR for the decision; doc kept accurate; no tactical hacks; degrade visibly.
- **Cert+key are NOT yet in Key Vault** — despite `docs/cloudflare-setup.md` claiming otherwise. They exist only as tofu sensitive outputs `origin_certificate_pem` / `origin_private_key_pem`. Task 3 fixes the doc.

---

## File Structure

- `docs/adr/NNNN-origin-ca-cert-for-aca-origin.md` — **Create.** ADR recording the managed-cert → Origin CA cert decision (next free ADR number; check `docs/adr/README.md`).
- `infra/scripts/Import-OriginCaCertToKeyVault.ps1` — **Create.** Idempotent operator script: read tofu outputs → build PFX → import into KV as a certificate.
- `infra/modules/shared.bicep` — **Modify.** Replace the managed-cert resource + two-pass binding with a KV-referenced Origin CA cert resource and a single-pass `SniEnabled` binding.
- `infra/scripts/Deploy-SharedResources.ps1` — **Inspect / possibly modify.** Confirm how it passes `wizardCustomDomain` / the (removed) cert-ready flag; drop the flag plumbing if present.
- `infra/**/dev.bicepparam` (or `local.bicepparam`) — **Modify.** Remove `wizardCustomDomainCertReady`; ensure `wizardCustomDomain = 'pinwiz.ai'`.
- `docs/cloudflare-setup.md` — **Modify.** Correct the "stored in Key Vault" claims, update the SSL/TLS status + Origin CA section + change log + pending-items table.

---

## Task 1: Branch + ADR

**Files:**
- Create: `docs/adr/NNNN-origin-ca-cert-for-aca-origin.md`
- Read: `docs/adr/README.md` (for the next ADR number + index entry)

- [ ] **Step 1: Create a clean feature branch off `main`**

```bash
git fetch origin
git switch -c chore/origin-ca-cert-migration origin/main
```
Expected: new branch tracking a clean `main` base (not off `chore/mudblazor-9`).

- [ ] **Step 2: Find the next ADR number**

Read `docs/adr/README.md` and the highest-numbered file in `docs/adr/`. Use the next integer, zero-padded to 4 digits.

- [ ] **Step 3: Write the ADR** (MADR-lite — Status / Date / Deciders / Context / Decision / Consequences). Content must state:
  - Context: ACA managed (Let's Encrypt) cert cannot auto-renew because the apex is a *proxied* Cloudflare CNAME — the ACME/domain-control challenge lands on the Cloudflare edge, never the ACA ingress. The `asuid` TXT and CNAME are correct; the renewal path is the problem. The dashboard "ACME bypass rule" present at first issuance (2026-05-15) was not carried into the IaC migration (2026-05-16).
  - Decision: bind the custom domain to the existing 15-yr **Cloudflare Origin CA certificate** (trusted by Cloudflare's edge) instead, sourced from the `infra/cloudflare` tofu stack and stored in Key Vault.
  - Consequences: no ACME renewal through the proxy ever again; `ssl=strict` becomes correct; the origin cert is browser-untrusted by design (only Cloudflare's edge connects to it); cert renewal is now a 15-yr / manual concern (track in secret-rotation follow-ups); AOP enforcement at the origin remains deferred (out of scope).

- [ ] **Step 4: Add the ADR to the index** in `docs/adr/README.md`.

- [ ] **Step 5: Commit**

```bash
git add docs/adr/
git commit -m "docs(adr) Origin CA cert for ACA origin; retire auto-renewing managed cert"
```

---

## Task 2: Operator script — package Origin CA cert + import to Key Vault

**Files:**
- Create: `infra/scripts/Import-OriginCaCertToKeyVault.ps1`

**Interfaces:**
- Consumes: tofu outputs `origin_certificate_pem`, `origin_private_key_pem` (from `infra/cloudflare`); an authenticated personal-sub az context; the Key Vault name (default `pinwiz-kv-dev-buutj`).
- Produces: a Key Vault **certificate** named `cloudflare-origin-pinwiz` whose backing secret URI is `https://<kv>.vault.azure.net/secrets/cloudflare-origin-pinwiz` — Task 4's Bicep references this URI.

- [ ] **Step 1: Write the script.** Parameters: `-KeyVaultName` (default `pinwiz-kv-dev-buutj`), `-CertName` (default `cloudflare-origin-pinwiz`), `-CloudflareTfDir` (default `infra/cloudflare`). Behavior:
  1. `tofu -chdir=$CloudflareTfDir output -raw origin_certificate_pem` → temp `origin.crt`.
  2. `tofu -chdir=$CloudflareTfDir output -raw origin_private_key_pem` → temp `origin.key` (chmod/ACL-restrict; delete in `finally`).
  3. Build PFX in-memory with .NET (no OpenSSL dependency, no password persisted):
     ```powershell
     $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($crtPath, $keyPath)
     $pfxBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
     $pfxPath = [System.IO.Path]::GetTempFileName()
     [System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
     ```
  4. Import: `az keyvault certificate import --vault-name $KeyVaultName --name $CertName --file $pfxPath` (no `--password` — the in-memory PFX export above is unencrypted; if `az` requires one, generate an ephemeral password, pass via `--password`, and never log it).
  5. `finally`: securely delete `origin.key`, `origin.crt`, `$pfxPath`.
  6. Idempotency: if a cert version already exists with the same thumbprint, log and no-op.
  7. Echo the resulting secret URI for the operator to confirm against Bicep.

- [ ] **Step 2: Lint the script**

Run: `pwsh -NoProfile -Command "Invoke-ScriptAnalyzer -Path infra/scripts/Import-OriginCaCertToKeyVault.ps1"` (if PSScriptAnalyzer present; otherwise `pwsh -NoProfile -File infra/scripts/Import-OriginCaCertToKeyVault.ps1 -WhatIf` parse check).
Expected: no parse errors. (Full run is Task 4 — it needs the personal session + tofu state.)

- [ ] **Step 3: Commit**

```bash
git add infra/scripts/Import-OriginCaCertToKeyVault.ps1
git commit -m "infra(scripts) idempotent Origin CA cert -> Key Vault import for ACA origin"
```

---

## Task 3: Bicep — KV-referenced Origin CA cert + single-pass binding

**Files:**
- Modify: `infra/modules/shared.bicep` (cert resource ~1724-1736; customDomains block ~1770-1781; param `wizardCustomDomainCertReady` ~89)
- Modify: parameter file(s) under `infra/` that set `wizardCustomDomainCertReady` / `wizardCustomDomain`

**Interfaces:**
- Consumes: KV cert secret URI from Task 2 (`https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/cloudflare-origin-pinwiz`); existing `acaIdentity` (UAMI, has Key Vault Secrets User per `shared.bicep:381`); existing `acaEnvironment`.
- Produces: `wizardOriginCert.id` consumed by `customDomains[].certificateId`.

- [ ] **Step 1: Replace the managed-cert resource** (`wizardCustomDomainCert`, ~1724) with a KV-referenced cert:

```bicep
// Cloudflare Origin CA certificate for the Wizard custom domain. The origin
// presents this 15-yr cert to Cloudflare (ssl=strict); it is NOT browser-
// trusted by design — only Cloudflare's edge connects to the ACA ingress.
// Sourced from the infra/cloudflare tofu stack, imported to Key Vault by
// infra/scripts/Import-OriginCaCertToKeyVault.ps1. See ADR-NNNN.
resource wizardOriginCert 'Microsoft.App/managedEnvironments/certificates@2024-03-01' = if (deployPhase2 && !empty(wizardCustomDomain)) {
  parent: acaEnvironment
  name: 'pinwiz-wizard-origin-cert'
  location: location
  tags: tags
  properties: {
    certificateKeyVaultProperties: {
      identity: acaIdentity.id
      keyVaultUrl: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/cloudflare-origin-pinwiz'
    }
  }
}
```

- [ ] **Step 2: Replace the two-pass `customDomains` block** (~1770) with the single-pass binding:

```bicep
        customDomains: empty(wizardCustomDomain) ? [] : [
          {
            name: wizardCustomDomain
            bindingType: 'SniEnabled'
            certificateId: wizardOriginCert.id
          }
        ]
```

- [ ] **Step 3: Remove the now-dead `wizardCustomDomainCertReady` param** (~89) and any remaining references to it; update the param-file(s) and `Deploy-SharedResources.ps1` if they plumb it. The cert value comes from KV (available at deploy time) so there is no ARM circular dependency and no second pass.

- [ ] **Step 4: Build / lint the Bicep**

Run: `az bicep build --file infra/modules/shared.bicep` (or `bicep build`).
Expected: succeeds; no unused-param warning for `wizardCustomDomainCertReady` (because it's gone); no reference to `wizardCustomDomainCert`.

- [ ] **Step 5: Commit**

```bash
git add infra/modules/shared.bicep infra/**/*.bicepparam infra/scripts/Deploy-SharedResources.ps1
git commit -m "infra(bicep) bind Wizard custom domain to Cloudflare Origin CA cert from Key Vault; retire managed-cert two-pass"
```

---

## Task 4: Doc correction — cloudflare-setup.md

**Files:**
- Modify: `docs/cloudflare-setup.md` (Architecture ~32, SSL/TLS status ~126-163, change log ~315, pending items ~300)

- [ ] **Step 1: Correct the inaccurate KV claims** (lines ~140, ~152): the cert/key are sourced from tofu state and imported to KV by `Import-OriginCaCertToKeyVault.ps1` as cert `cloudflare-origin-pinwiz` — not pre-existing KV PEM secrets.

- [ ] **Step 2: Update the "SSL mode status" section** (~126): once the Origin CA cert is installed on the ACA origin, `ssl=strict` is correct and no `ssl=full` stopgap is needed. Remove/update the "PENDING CERT INSTALL" framing to reflect the install path via this migration. Update the "Origin CA certificate" subsection to reference the import script + ACA env cert resource.

- [ ] **Step 3: Update the pending-items table** (~300): mark "Install Origin CA cert on ACA origin" as resolved by this work; leave AOP origin-enforcement as a tracked follow-up.

- [ ] **Step 4: Add a change-log row** (~315) dated 2026-06-18: "Wizard custom domain rebound from Azure managed (Let's Encrypt) cert to Cloudflare Origin CA cert (Bicep + KV import). | IaC + operator script".

- [ ] **Step 5: Commit**

```bash
git add docs/cloudflare-setup.md
git commit -m "docs(cloudflare) correct Origin CA cert KV claims; reflect ACA origin cert install"
```

---

## Task 5: Deploy + verify (REQUIRES isolated personal-sub session)

> Prerequisite: an isolated az CLI context authenticated to sub `b1f33f17-…` (setup-azure pattern), and the `infra/cloudflare` tofu stack readable (state backend reachable) so the operator script can pull the cert outputs.

- [ ] **Step 1: Confirm context**

Run: `az account show --query "{name:name,id:id}" -o json`
Expected: `id` == the personal pinwiz.ai sub (`b1f33f17-…`), NOT `APS Subscription`. **Stop if it shows the work sub.**

- [ ] **Step 2: Populate Key Vault**

Run: `pwsh -NoProfile -File infra/scripts/Import-OriginCaCertToKeyVault.ps1`
Expected: cert `cloudflare-origin-pinwiz` present in `pinwiz-kv-dev-buutj`; script echoes the secret URI matching the Bicep `keyVaultUrl`.

Verify: `az keyvault certificate show --vault-name pinwiz-kv-dev-buutj --name cloudflare-origin-pinwiz --query "{sub:policy.x509CertificateProperties.subject,exp:attributes.expires}" -o json`
Expected: subject CN `pinwiz.ai`; expiry ~15 years out.

- [ ] **Step 3: Deploy via Deployment Stack**

Run: `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf`
Expected (What-If): creates `pinwiz-wizard-origin-cert`; updates `wizardApp` customDomains to `SniEnabled` → new cert; **deletes** the old `pinwiz-wizard-cert` managed cert (action-on-unmanage). Review the delete is intentional and confined to the managed cert.

Then run without `-WhatIf` to apply.

- [ ] **Step 4: Verify the origin presents the Origin CA cert**

Run (against the ACA ingress FQDN — see `wizardFqdn` output, with SNI `pinwiz.ai`):
```bash
echo | openssl s_client -connect <wizardFqdn>:443 -servername pinwiz.ai 2>/dev/null | openssl x509 -noout -issuer -subject -dates
```
Expected: issuer is the **Cloudflare Origin CA** (e.g. `CloudFlare Origin SSL Certificate Authority`), subject `pinwiz.ai`, validity ~15 years. NOT a Let's Encrypt issuer.

- [ ] **Step 5: Verify end-to-end through Cloudflare (ssl=strict holds)**

Run: `curl -sS -o /dev/null -w "%{http_code}\n" https://pinwiz.ai/alive` (expect to pass the Cloudflare Access OTP gate per `reference_pinwiz_smoke_automation.md` if it intercepts; a `200`/expected app code — and crucially **not** a `525 SSL handshake failed` — confirms strict mode is satisfied by the origin cert).

- [ ] **Step 6: Confirm the Advisor recommendation clears**

In the portal, the "Managed Certificate failed to renew" recommendation should drop off once the managed cert no longer exists (Advisor refreshes on its own cadence — may take a day). No managed cert = nothing to fail renewal.

- [ ] **Step 7: Time tracking** — skip (personal GitHub repo; see `feedback_skip_time_tracking.md`).

---

## Self-Review notes

- **Spec coverage:** cert sourcing+storage (Task 2), Bicep rebind + managed-cert removal (Task 3), doc fix (Task 4), cutover sequencing + verification (Task 5), ADR (Task 1), AOP explicitly deferred (ADR consequences + Task 4 pending item). All design sections covered.
- **No two-pass needed:** the original two-pass existed to break the ARM cycle around ACME validation timing. A KV-sourced cert has its value available at deploy time → no cycle → single pass. Confirmed against the customDomains→certificateId→cert→env dependency chain (acyclic).
- **RBAC:** `acaIdentity` already holds Key Vault Secrets User (`shared.bicep:381`), which is sufficient to read the cert's backing secret. No new role assignment required. (Verify at deploy: if the cert resource fails with a KV access error, confirm Secrets User propagation and that the KV is RBAC-auth, not access-policy.)
- **Known quirk:** MS docs note dynamic KV-reference issues for ACA env certs; mitigated here by constructing `keyVaultUrl` as a static string (not a runtime `reference()`), so no module workaround is needed. Re-evaluate only if the deploy errors.
