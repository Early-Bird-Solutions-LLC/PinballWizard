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
# This file is a TEMPLATE — uncomment and adjust when staging exists.

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
