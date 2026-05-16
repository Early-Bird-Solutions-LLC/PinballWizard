# waf.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §5 (WAF & Bot Management).
#
# In v5, all WAF configuration is expressed as cloudflare_ruleset resources
# attached to specific request-handling phases. Managed and custom rulesets
# live in different phases and are configured as separate resources.

# ─────────────────────────────────────────────────────────────────────
# Managed rulesets — REQUIRES CLOUDFLARE PRO ($20/mo)
# ─────────────────────────────────────────────────────────────────────
# OWASP Core Ruleset and Exposed Credentials Check are not available on
# the Free plan. Cloudflare applies basic DDoS + managed protection
# automatically on Free — this resource adds configurable control over
# those managed rulesets, which requires Pro or higher.
#
# To enable: upgrade the zone to Pro, then uncomment this resource.
# The custom WAF rules below work on the Free plan.

# resource "cloudflare_ruleset" "zone_waf_managed" {
#   zone_id     = var.zone_id
#   name        = "Zone WAF — managed rulesets"
#   description = "Cloudflare Managed, OWASP Core, and Exposed Credentials Check rulesets"
#   kind        = "zone"
#   phase       = "http_request_firewall_managed"
#
#   rules = [
#     {
#       action      = "execute"
#       description = "Execute Cloudflare Managed Ruleset"
#       expression  = "true"
#       enabled     = true
#       action_parameters = {
#         id = local.managed_ruleset_cloudflare_managed
#       }
#     },
#     {
#       action      = "execute"
#       description = "Execute OWASP Core Ruleset (PL1 — start low, tune up)"
#       expression  = "true"
#       enabled     = true
#       action_parameters = {
#         id        = local.managed_ruleset_owasp_core
#         overrides = {
#           categories = [
#             { category = "paranoia-level-2", enabled = false },
#             { category = "paranoia-level-3", enabled = false },
#             { category = "paranoia-level-4", enabled = false },
#           ]
#         }
#       }
#     },
#     {
#       action      = "execute"
#       description = "Execute Exposed Credentials Check Ruleset"
#       expression  = "true"
#       enabled     = true
#       action_parameters = {
#         id = local.managed_ruleset_exposed_credentials
#       }
#     },
#   ]
# }

# ─────────────────────────────────────────────────────────────────────
# Custom rules — application-specific WAF rules
# ─────────────────────────────────────────────────────────────────────
# Rule order matters: first matching rule wins. Follow the pattern of:
#   1. Skip rules (carve-outs for known-good traffic) — FIRST
#   2. Block rules (definite-bad)
#   3. Challenge rules (ambiguous, want a human signal)

resource "cloudflare_ruleset" "zone_waf_custom" {
  zone_id     = var.zone_id
  name        = "Zone WAF — custom rules"
  description = "Application-specific WAF rules for pinwiz.ai"
  kind        = "zone"
  phase       = "http_request_firewall_custom"

  rules = [
    {
      action      = "block"
      description = "Block scanner reconnaissance paths"
      enabled     = true
      expression  = <<-EOT
        (http.request.uri.path in {
          "/.env"
          "/.git/config"
          "/.git/HEAD"
          "/wp-admin"
          "/wp-login.php"
          "/phpmyadmin"
          "/server-status"
          "/.aws/credentials"
          "/config.php"
        })
      EOT
    },
    {
      action      = "block"
      description = "Block requests with Host header that isn't ours"
      enabled     = true
      expression  = <<-EOT
        (http.host ne "${var.domain}" and http.host ne "www.${var.domain}")
      EOT
    },
    {
      action      = "managed_challenge"
      description = "Challenge requests with missing or low-entropy User-Agent"
      enabled     = true
      expression  = <<-EOT
        (http.user_agent eq "" or len(http.user_agent) lt 10)
        and not (starts_with(http.request.uri.path, "/.well-known/"))
      EOT
    },
  ]
}
