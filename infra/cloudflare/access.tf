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
    {
      name       = "Maintainer only"
      decision   = "allow"
      precedence = 1
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
