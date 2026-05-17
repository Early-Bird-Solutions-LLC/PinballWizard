# access.tf
#
# Pre-launch access gate (PL1 in docs/phase7-operator-todo.md).
#
# pinwiz.ai is an anonymous public community resource AT LAUNCH
# (ADR-0009 Tier 1 — Wizard chat / search / browse require no login).
# But until launch the entire site must be invisible to the public:
# it is a customer-facing showcase and an unfinished surface erodes
# the very confidence it exists to build.
#
# This file puts the whole apex behind Cloudflare Zero Trust Access,
# allowing only the maintainer (one-time-PIN email login). It REPLACES
# the old "manually delete the PinballWizard Dev Access app" launch
# step with a tracked IaC toggle: flip `dev_gate_enabled = false` and
# apply to remove the gate atomically at launch — no dashboard drift.
#
# Cloudflare provider v5: policies are INLINE on the application
# resource (a list of objects), not separate cloudflare_access_policy
# resources. Free tier covers up to 50 users.

# One-time-PIN identity provider. Lets the maintainer authenticate via
# an emailed code with no GitHub/Google IdP configured yet (Tier 3
# federated identities land when passport features ship — ADR-0009).
resource "cloudflare_zero_trust_access_identity_provider" "otp" {
  count = var.dev_gate_enabled ? 1 : 0

  account_id = var.account_id
  name       = "One-time PIN"
  type       = "onetimepin"
  config     = {}
}

# Static asset bypass applications — Blazor requires /_content/*, /_framework/*,
# /_blazor/*, /app.css, and /app.js to be served anonymously so the browser can
# load MudBlazor CSS, Blazor runtime JS, and app styles before any auth challenge.
# Access gates these paths by default because the apex application covers all paths;
# separate bypass applications with decision=bypass/everyone take precedence for
# these path prefixes, letting static assets through without a login challenge.
locals {
  static_bypass_paths = var.dev_gate_enabled ? [
    "/_content",
    "/_framework",
    "/_blazor",
  ] : []
}

resource "cloudflare_zero_trust_access_application" "static_bypass" {
  for_each = toset(local.static_bypass_paths)

  account_id       = var.account_id
  name             = "pinwiz.ai static bypass ${each.key}"
  domain           = "${var.domain}${each.key}"
  type             = "self_hosted"
  session_duration = "24h"

  app_launcher_visible = false

  policies = [
    {
      name     = "Allow static assets"
      decision = "bypass"
      include = [
        { everyone = {} }
      ]
    }
  ]
}

# Self-hosted application covering the entire apex. Anyone hitting any
# path on pinwiz.ai is challenged by Access and must satisfy the allow
# policy below before the request ever reaches the ACA origin.
# Static asset paths are excluded via the bypass applications above.
resource "cloudflare_zero_trust_access_application" "prelaunch_gate" {
  count = var.dev_gate_enabled ? 1 : 0

  account_id       = var.account_id
  name             = "pinwiz.ai pre-launch gate"
  domain           = var.domain
  type             = "self_hosted"
  session_duration = "24h"

  # Maintainer-only surface — never advertise it in the App Launcher.
  app_launcher_visible = false

  # Constrain login to the OTP IdP so a future federated IdP added for
  # end-user features (ADR-0009 Tier 3) does not silently widen the gate.
  allowed_idps              = [cloudflare_zero_trust_access_identity_provider.otp[0].id]
  auto_redirect_to_identity = true

  policies = [
    {
      name     = "Maintainer only"
      decision = "allow"
      include = [
        for addr in var.dev_gate_allowed_emails : {
          email = {
            email = addr
          }
        }
      ]
    }
  ]
}
