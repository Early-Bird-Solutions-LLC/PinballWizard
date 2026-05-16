# locals.tf
#
# Computed values used across the stack. Centralizing them avoids drift
# between files and makes review easier.

locals {
  # Cloudflare-published managed ruleset IDs. These are stable across all
  # accounts — they identify the ruleset itself, not your deployment of it.
  # Verify against: https://developers.cloudflare.com/waf/managed-rules/reference/
  managed_ruleset_cloudflare_managed     = "efb7b8c949ac4650a09736fc376e9aee"
  managed_ruleset_owasp_core             = "4814384a9e5d4991b9815dcfc25d2f1f"
  managed_ruleset_exposed_credentials    = "c2e184081120413c86c3ab7e14069605"

  # CAA records. Only CAs we permit are listed. Update CAA at the same
  # time you change SSL provisioning — misaligned CAA breaks renewals.
  caa_issuers = [
    "letsencrypt.org",
    "pki.goog",
    "digicert.com",
  ]

  # Common tags applied where the provider supports them (limited surface).
  common_tags = {
    project     = "pinball-wizard"
    managed_by  = "opentofu"
    environment = "prod"
  }
}
