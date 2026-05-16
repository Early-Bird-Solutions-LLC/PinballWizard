# imports.tf
#
# IMPORT BLOCKS — bring existing live infrastructure under IaC management
# without destroying or recreating it.
#
# Workflow (from PLAN.md §5):
#   1. ✅ Inventory complete — cf-inventory/ in repo root
#   2. ✅ HCL authored — dns.tf / tls.tf / waf.tf / rate_limit.tf / headers.tf
#   3. ➡  Run: tofu plan  (should show imports + intentional drift changes below)
#   4. ➡  Run: tofu apply
#   5. ➡  Run: tofu plan  (should show "No changes" after the drift is resolved)
#   6. ➡  Delete this file (or its contents) once state is clean
#
# Import ID format cheat sheet:
#   cloudflare_dns_record           "<zone_id>/<record_id>"
#   cloudflare_zone_setting         "<zone_id>/<setting_id>"
#   cloudflare_zone_dnssec          "<zone_id>"
#   cloudflare_ruleset              "<zone_id>/<ruleset_id>"
#
# ─── KNOWN DRIFT — what first apply will intentionally change ─────────
#
#   resource                             current → desired
#   cloudflare_dns_record.root           proxied=false → proxied=true
#   cloudflare_zone_setting.ssl          full → strict
#   cloudflare_zone_setting.always_use_https  off → on
#   cloudflare_zone_setting.min_tls_version   1.0 → 1.2
#   cloudflare_zone_setting.security_header   HSTS disabled → enabled (300s)
#   cloudflare_zone_dnssec.this          disabled → active
#
# PRE-APPLY CHECKLIST (one-time):
#   □ Disable Cloudflare Email Routing in dashboard before applying mx_null
#   □ Enable "Full (strict)" SSL only after origin cert is installed on ACA
#   □ Stage HSTS: start at max_age=300 (5 min), promote weekly per tls.tf comment
#   □ Add IaC-scoped Cloudflare API token to GitHub secret CLOUDFLARE_API_TOKEN
#   □ Add CF_ZONE_ID / CF_ACCOUNT_ID to GitHub secrets (or TF_VAR_* env vars)
#
# ─────────────────────────────────────────────────────────────────────

# ─── DNS records ─────────────────────────────────────────────────────

# Existing apex CNAME (currently DNS-only / not proxied).
# First apply will enable proxy — verify origin cert is ready first.
import {
  to = cloudflare_dns_record.root
  id = "13b7f7c8b15889652f0004d420669fe1/9c73df5cfaf696fed9a9f8e5aff6c0bf"
}

# ACA custom domain verification TXT.
# Content is the ACA domain verification token — store as TF_VAR_aca_domain_verification_token.
import {
  to = cloudflare_dns_record.aca_domain_verification
  id = "13b7f7c8b15889652f0004d420669fe1/0acb3e7bdeafbe45ae0f6984407861a4"
}

# ─── Email Routing ───────────────────────────────────────────────────

# Email Routing is already disabled (enabled=false, status=unconfigured).
# Import brings it under IaC management so it can't be re-enabled via
# the dashboard without going through code. No DNS changes on apply.
import {
  to = cloudflare_email_routing_settings.this
  id = "13b7f7c8b15889652f0004d420669fe1"
}

# ─── DNSSEC ──────────────────────────────────────────────────────────

# DNSSEC is currently disabled. Importing and applying will enable it.
# After apply, publish the DS record (from tofu output dnssec_ds_record)
# at the Cloudflare registrar: dashboard → pinwiz.ai → DNS → DNSSEC.
import {
  to = cloudflare_zone_dnssec.this
  id = "13b7f7c8b15889652f0004d420669fe1"
}

# ─── Zone settings ───────────────────────────────────────────────────

import {
  to = cloudflare_zone_setting.ssl
  id = "13b7f7c8b15889652f0004d420669fe1/ssl"
}

import {
  to = cloudflare_zone_setting.min_tls_version
  id = "13b7f7c8b15889652f0004d420669fe1/min_tls_version"
}

import {
  to = cloudflare_zone_setting.tls_1_3
  id = "13b7f7c8b15889652f0004d420669fe1/tls_1_3"
}

import {
  to = cloudflare_zone_setting.opportunistic_encryption
  id = "13b7f7c8b15889652f0004d420669fe1/opportunistic_encryption"
}

import {
  to = cloudflare_zone_setting.automatic_https_rewrites
  id = "13b7f7c8b15889652f0004d420669fe1/automatic_https_rewrites"
}

import {
  to = cloudflare_zone_setting.always_use_https
  id = "13b7f7c8b15889652f0004d420669fe1/always_use_https"
}

import {
  to = cloudflare_zone_setting.security_header
  id = "13b7f7c8b15889652f0004d420669fe1/security_header"
}

# ─── Resources to be CREATED (no existing resource to import) ────────
#
# The following resources in the .tf files have no live counterpart yet.
# tofu plan will show them as "to be created":
#
#   cloudflare_dns_record.www                  — new CNAME www → apex
#   cloudflare_dns_record.caa_issue[*]         — 3 new CAA records
#   cloudflare_dns_record.caa_iodef            — new CAA iodef
#   cloudflare_dns_record.spf                  — new TXT v=spf1 -all
#   cloudflare_dns_record.dmarc                — new TXT DMARC (p=none)
#   cloudflare_dns_record.mx_null              — new null MX (disable email routing first)
#   cloudflare_origin_ca_certificate.this      — new Origin CA cert (via tls.tf)
#   cloudflare_authenticated_origin_pulls_settings.this — AOP on
#   cloudflare_ruleset.zone_waf_managed        — custom rule invoking managed rulesets
#   cloudflare_ruleset.zone_waf_custom         — scanner-block + host-header + UA rules
#   cloudflare_ruleset.rate_limits             — global + chat + auth rate limits
#   cloudflare_ruleset.security_response_headers — security headers transform
