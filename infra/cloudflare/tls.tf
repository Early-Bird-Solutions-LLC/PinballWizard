# tls.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §3 (TLS) and §4 (Origin Protection).
#
# Each zone setting is a separate resource in v5 of the provider (split
# out from the v4 cloudflare_zone_settings_override bulk resource).

# ─────────────────────────────────────────────────────────────────────
# Zone-level TLS settings
# ─────────────────────────────────────────────────────────────────────

resource "cloudflare_zone_setting" "ssl" {
  zone_id    = var.zone_id
  setting_id = "ssl"
  value      = "strict" # Full (strict) — the only acceptable mode
}

resource "cloudflare_zone_setting" "min_tls_version" {
  zone_id    = var.zone_id
  setting_id = "min_tls_version"
  value      = "1.2"
}

resource "cloudflare_zone_setting" "tls_1_3" {
  zone_id    = var.zone_id
  setting_id = "tls_1_3"
  value      = "on"
}

resource "cloudflare_zone_setting" "opportunistic_encryption" {
  zone_id    = var.zone_id
  setting_id = "opportunistic_encryption"
  value      = "on"
}

resource "cloudflare_zone_setting" "automatic_https_rewrites" {
  zone_id    = var.zone_id
  setting_id = "automatic_https_rewrites"
  value      = "on"
}

resource "cloudflare_zone_setting" "always_use_https" {
  zone_id    = var.zone_id
  setting_id = "always_use_https"
  value      = "on"
}

# ─────────────────────────────────────────────────────────────────────
# HSTS — STAGED ROLLOUT
# ─────────────────────────────────────────────────────────────────────
#
# !!! Read this comment before modifying !!!
#
# HSTS is a one-way commitment device. Once browsers cache the policy,
# they will refuse to connect over HTTP for `max_age` seconds.
#
# Rollout plan (see CLOUDFLARE_PRELAUNCH_CHECKLIST.md §3.2):
#   Week 1: max_age = 300       (5 min) — verify nothing breaks
#   Week 2: max_age = 86400     (1 day)
#   Week 3: max_age = 31536000  (1 year) with include_subdomains, preload
#
# The configuration below is the FINAL state. To stage the rollout,
# adjust max_age progressively across separate PRs. Do not submit to
# the HSTS preload list until this has been live at max for at least
# a week.

resource "cloudflare_zone_setting" "security_header" {
  zone_id    = var.zone_id
  setting_id = "security_header"

  value = {
    strict_transport_security = {
      enabled            = true
      max_age            = 31536000 # 1 year
      include_subdomains = true
      preload            = true
      nosniff            = true
    }
  }
}

# ─────────────────────────────────────────────────────────────────────
# Origin CA certificate — for the Azure origin to present to Cloudflare
# ─────────────────────────────────────────────────────────────────────
# This cert is trusted ONLY by Cloudflare's edge. An attacker who finds
# the origin IP cannot forge a valid cert chain to MITM it.

resource "tls_private_key" "origin" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

resource "tls_cert_request" "origin" {
  private_key_pem = tls_private_key.origin.private_key_pem

  subject {
    common_name  = var.domain
    organization = "PinballWizard"
  }

  dns_names = [
    var.domain,
    "*.${var.domain}",
  ]
}

resource "cloudflare_origin_ca_certificate" "this" {
  csr = tls_cert_request.origin.cert_request_pem
  # Order matches the materialized state (wildcard first). The provider
  # does an order-sensitive comparison on this list, so a reorder alone
  # forces a needless cert replacement — keep this aligned with state.
  hostnames          = ["*.${var.domain}", var.domain]
  request_type       = "origin-rsa"
  requested_validity = 5475 # 15 years

  lifecycle {
    # Renew only when explicitly bumped (e.g. by changing requested_validity).
    create_before_destroy = true
  }
}

# Note: the origin cert PEM and the private key need to be installed on
# the Azure origin. The PEM is in `cloudflare_origin_ca_certificate.this.certificate`;
# the key is in `tls_private_key.origin.private_key_pem`. Both must be
# handled out-of-band — do NOT commit these. Recommended: write them to
# an Azure Key Vault that the App Service reads on startup, via a Bicep
# template in infra/azure/.

# ─────────────────────────────────────────────────────────────────────
# Authenticated Origin Pulls (mTLS Cloudflare → origin)
# ─────────────────────────────────────────────────────────────────────
# Forces the origin to verify a client cert presented by Cloudflare on
# every request. Anyone who finds your origin IP and tries to bypass
# Cloudflare gets a TLS handshake failure.

resource "cloudflare_authenticated_origin_pulls_settings" "this" {
  zone_id = var.zone_id
  enabled = true
}
