# headers.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §7 (Security Response Headers).
#
# Headers are injected at the edge via Transform Rules in the
# http_response_headers_transform phase. Doing this at the edge rather
# than the origin means the headers apply uniformly regardless of which
# backend served the response and survive origin redesigns.

resource "cloudflare_ruleset" "security_response_headers" {
  zone_id     = var.zone_id
  name        = "Security response headers"
  description = "Inject security headers on all responses"
  kind        = "zone"
  phase       = "http_response_headers_transform"

  rules = [
    {
      action      = "rewrite"
      description = "Set security headers on all responses"
      expression  = "true"
      enabled     = true

      action_parameters = {
        headers = {
          "X-Content-Type-Options" = {
            operation = "set"
            value     = "nosniff"
          }
          "X-Frame-Options" = {
            operation = "set"
            value     = "DENY"
          }
          "Referrer-Policy" = {
            operation = "set"
            value     = "strict-origin-when-cross-origin"
          }
          "Permissions-Policy" = {
            operation = "set"
            value     = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()"
          }
          # CSP: starts in Report-Only mode. Promote to enforced
          # Content-Security-Policy header after a week of clean reports.
          # See CLOUDFLARE_PRELAUNCH_CHECKLIST.md §7.2 for the staged rollout.
          "Content-Security-Policy-Report-Only" = {
            operation = "set"
            value = join("; ", [
              "default-src 'self'",
              "script-src 'self'",
              "style-src 'self'",
              "img-src 'self' data: https://sternpinball.com",
              "font-src 'self'",
              "connect-src 'self'",
              "frame-ancestors 'none'",
              "base-uri 'self'",
              "form-action 'self'",
              "upgrade-insecure-requests",
              "report-uri https://${var.domain}/_csp-reports",
            ])
          }
          # Strip identifying server headers if the origin returns them.
          # Note: the "Server" header cannot be removed via Transform Rules
          # — Cloudflare sets and protects it (API rejects 'remove' on
          # 'Server' with code 20087). It is already "Server: cloudflare"
          # at the edge, so the origin's value never reaches the client.
          "X-Powered-By" = {
            operation = "remove"
          }
          "X-AspNet-Version" = {
            operation = "remove"
          }
          "X-AspNetMvc-Version" = {
            operation = "remove"
          }
        }
      }
    },
  ]
}
