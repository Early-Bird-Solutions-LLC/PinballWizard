# access.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §8 (Zero Trust for Admin).
#
# Cloudflare Access protects internal surfaces (staging, admin) with SSO
# and policy. Free tier covers up to 50 users.
#
# In v5, application-scoped policies are INLINE on the application
# resource (cloudflare_zero_trust_access_application.policies) — they
# are no longer separate cloudflare_access_policy resources.
#
# ─────────────────────────────────────────────────────────────────────
# Pre-launch gate — email-only access to pinwiz.ai until the public
# frontend ships (Phase 4+). This is a customer-facing showcase: the
# apex must not resolve to a half-built app a prospect could stumble
# onto. The gate restricts the entire zone to the maintainer's email
# via a One-time PIN identity provider.
#
# Toggle off (var.prelaunch_gate_enabled = false) only when the public
# Wizard frontend is ready to be seen. Removing these resources opens
# pinwiz.ai to the world.
#
# Both resources were previously created out-of-band and adopted into
# tofu state; the HCL below matches their live configuration exactly.
# ─────────────────────────────────────────────────────────────────────

resource "cloudflare_zero_trust_access_identity_provider" "otp" {
  count = var.prelaunch_gate_enabled ? 1 : 0

  account_id = var.account_id
  name       = "One-time PIN"
  type       = "onetimepin"

  # config is a required argument but every nested field (redirect_url)
  # is provider-computed for the onetimepin type, so it is passed empty.
  # scim_config is invalid for onetimepin and is omitted entirely.
  config = {}
}

resource "cloudflare_zero_trust_access_application" "prelaunch_gate" {
  count = var.prelaunch_gate_enabled ? 1 : 0

  account_id                 = var.account_id
  name                       = "pinwiz.ai pre-launch gate"
  domain                     = var.domain
  type                       = "self_hosted"
  session_duration           = "24h"
  app_launcher_visible       = false
  auto_redirect_to_identity  = true
  http_only_cookie_attribute = true
  allowed_idps               = [one(cloudflare_zero_trust_access_identity_provider.otp).id]

  # self_hosted_domains is materialized in state but the v5 provider
  # rejects setting it alongside destinations — destinations is the
  # supported surface. Provider reconciles the legacy field on apply.

  # Both the apex and the proxied www CNAME (see dns.tf) must be gated —
  # www → apex is a separate Cloudflare hostname and would otherwise
  # bypass the pre-launch gate entirely.
  destinations = [
    {
      type = "public"
      uri  = var.domain
    },
    {
      type = "public"
      uri  = "www.${var.domain}"
    },
  ]

  policies = [
    # Service Auth — evaluated before the identity policy below. Lets the E2E
    # suite drive the REAL edge (pinwiz.ai, i.e. through Cloudflare's WAF, cache,
    # and security headers) instead of only the ACA origin, which is all the CI
    # canary can reach today. Without this, an automated run hits the OTP screen
    # and the only way through is a human pasting a mailed code — a human
    # credential in an automation path, with a 24h session lifetime.
    #
    # decision = "non_identity" is the API value for what the dashboard labels
    # "Service Auth". It MUST be non_identity: an "allow" policy would send the
    # request to the IdP and prompt for a login anyway.
    {
      name       = "E2E service token"
      decision   = "non_identity"
      precedence = 1
      include = [
        {
          service_token = {
            token_id = one(cloudflare_zero_trust_access_service_token.e2e).id
          }
        },
      ]
    },
    {
      name       = "Maintainer only"
      decision   = "allow"
      precedence = 2
      include = [
        {
          email = {
            email = var.maintainer_email
          }
        },
      ]
    },
  ]
}

# Credential for non-interactive access through the gate above. The holder sends
# CF-Access-Client-Id / CF-Access-Client-Secret on every request; Cloudflare
# validates them itself and never redirects to the OTP identity provider.
#
# client_secret is returned by the API exactly ONCE, at creation, and is
# unreadable afterwards — capture it from the tofu output on first apply and
# store it outside the repo. Rotate by bumping client_secret_version.
#
# NOTE: this satisfies Cloudflare *Access* only. Super Bot Fight Mode
# (sbfm_definitely_automated = "block" in waf.tf) runs in a separate pipeline
# that a service token does NOT exempt, so a HEADLESS browser can still be
# blocked at the edge even holding a valid token. The E2E runner therefore drives
# a headed browser when targeting pinwiz.ai. Deliberately no WAF skip rule: that
# would widen the bot surface of a public showcase site to save a window.
resource "cloudflare_zero_trust_access_service_token" "e2e" {
  count = var.prelaunch_gate_enabled ? 1 : 0

  account_id = var.account_id
  name       = "pinwiz.ai E2E test runner"
  duration   = "8760h" # 1 year (provider default); rotate via client_secret_version
}

# This block is a TEMPLATE — uncomment and adjust when staging exists.

# resource "cloudflare_zero_trust_access_application" "staging" {
#   account_id       = var.account_id
#   name             = "pinwiz.ai staging"
#   domain           = "staging.${var.domain}"
#   type             = "self_hosted"
#   session_duration = "8h"
#
#   # CORS / app appearance settings can go here.
#   app_launcher_visible = false
#
#   policies = [
#     {
#       name     = "Admin access"
#       decision = "allow"
#       include = [
#         {
#           email = {
#             email = var.admin_email
#           }
#         }
#       ]
#       require = [
#         # Require an authentication method that supports phishing-resistant
#         # MFA. If the IdP is GitHub or Google with hardware keys, this is
#         # covered upstream — but enforcing AAL here is belt-and-braces.
#       ]
#     }
#   ]
# }
