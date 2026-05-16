# dns.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §2 (DNS Configuration).
#
# Conventions:
#   - Apex records use name = "@"
#   - All web traffic is proxied; mail/SPF/DMARC records are DNS-only
#   - TTL is 1 (automatic) on proxied records; explicit short TTL on others

# ─────────────────────────────────────────────────────────────────────
# Apex records — point to the Azure origin via CNAME flattening
# ─────────────────────────────────────────────────────────────────────

resource "cloudflare_dns_record" "root" {
  zone_id = var.zone_id
  name    = "@"
  type    = "CNAME"
  content = var.origin_hostname
  ttl     = 1
  proxied = true
  comment = "Apex → Azure origin (CNAME flattened by Cloudflare)"
}

resource "cloudflare_dns_record" "www" {
  zone_id = var.zone_id
  name    = "www"
  type    = "CNAME"
  content = var.domain
  ttl     = 1
  proxied = true
  comment = "www → apex"
}

# ─────────────────────────────────────────────────────────────────────
# DNSSEC
# ─────────────────────────────────────────────────────────────────────
# Enables DNSSEC on the zone. After apply, take the DS record from the
# `cloudflare_zone_dnssec` output and publish it at the registrar.

resource "cloudflare_zone_dnssec" "this" {
  zone_id = var.zone_id
  status  = "active"
}

# ─────────────────────────────────────────────────────────────────────
# CAA records — restrict which CAs can issue certs for pinwiz.ai
# ─────────────────────────────────────────────────────────────────────

resource "cloudflare_dns_record" "caa_issue" {
  for_each = toset(local.caa_issuers)

  zone_id = var.zone_id
  name    = "@"
  type    = "CAA"
  ttl     = 3600
  comment = "CAA issue: ${each.key}"

  data = {
    flags = 0
    tag   = "issue"
    value = each.key
  }
}

resource "cloudflare_dns_record" "caa_iodef" {
  zone_id = var.zone_id
  name    = "@"
  type    = "CAA"
  ttl     = 3600
  comment = "CAA iodef — CAs notify here of violations"

  data = {
    flags = 0
    tag   = "iodef"
    value = "mailto:${var.admin_email}"
  }
}

# ─────────────────────────────────────────────────────────────────────
# Email authentication — even if we don't send mail
# ─────────────────────────────────────────────────────────────────────
# SPF: declare no host authorized to send mail from pinwiz.ai. Update if
# you later add a transactional mail sender.

resource "cloudflare_dns_record" "spf" {
  zone_id = var.zone_id
  name    = "@"
  type    = "TXT"
  content = "\"v=spf1 -all\""
  ttl     = 3600
  comment = "SPF — no host authorized to send mail"
}

# DMARC: collect reports first (p=none), promote later. Update rua/ruf
# to a mailbox you actually monitor (or a Postmark/Cloudflare collector).

resource "cloudflare_dns_record" "dmarc" {
  zone_id = var.zone_id
  name    = "_dmarc"
  type    = "TXT"
  content = "\"v=DMARC1; p=none; rua=mailto:dmarc@${var.domain}; ruf=mailto:dmarc@${var.domain}; adkim=s; aspf=s; fo=1\""
  ttl     = 3600
  comment = "DMARC — report-only initially; promote to quarantine then reject"
}

# Disable email autodiscovery to discourage spoofing tools from finding
# mail endpoints we don't have.

# ─────────────────────────────────────────────────────────────────────
# Email Routing — tracked (currently disabled)
# ─────────────────────────────────────────────────────────────────────
# pinwiz.ai has no mail use case. Email Routing is already disabled
# (enabled=false, status=unconfigured). This resource is imported to
# bring it under IaC management. The v5 provider marks `enabled` as
# read-only — state is tracked but enable/disable requires the dashboard
# or a direct API call until the provider exposes a write path.

resource "cloudflare_email_routing_settings" "this" {
  zone_id = var.zone_id
}

resource "cloudflare_dns_record" "mx_null" {
  zone_id  = var.zone_id
  name     = "@"
  type     = "MX"
  content  = "."
  priority = 0
  ttl      = 3600
  comment  = "Null MX — RFC 7505 — domain accepts no mail"

  # Email Routing must be confirmed disabled before creating null MX,
  # otherwise Cloudflare rejects it as conflicting with routing MX records.
  depends_on = [cloudflare_email_routing_settings.this]
}

# ─────────────────────────────────────────────────────────────────────
# Azure Container Apps custom domain verification
# ─────────────────────────────────────────────────────────────────────
# ACA requires a TXT record to verify domain ownership before issuing
# its managed certificate. This record was created manually and is now
# under IaC management. Do NOT delete — removing it breaks ACA cert renewal.

resource "cloudflare_dns_record" "aca_domain_verification" {
  zone_id = var.zone_id
  name    = "asuid.${var.domain}"
  type    = "TXT"
  content = var.aca_domain_verification_token
  ttl     = 60
  comment = "ACA custom domain verification — required for cert renewal"
}
