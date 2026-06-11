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
          #
          # Tuned 2026-06-11 (issue #356) to zero violations against the real
          # app — the original 'self'-everything policy produced ~48 report-only
          # violations per page load (DevTools Issues noise on the exact
          # audience this showcase serves). Directive rationale:
          #
          #   script-src — the XSS-load-bearing directive; stays strict.
          #     No 'unsafe-inline' / 'unsafe-eval', ever. The two SHA-256
          #     hashes allow the app's only inline scripts:
          #       1. tUlm…  theme/motion FOUC bootstrap (Components/App.razor)
          #       2. kgZ1…  mermaid.initialize()        (Pages/About.razor)
          #     The full jsDelivr URL allows the version-pinned Mermaid bundle
          #     (About page architecture diagram), which also carries an SRI
          #     integrity attribute so a tampered CDN response fails closed.
          #     Editing either inline script or bumping the Mermaid pin
          #     requires updating this policy — CspPolicySyncTests
          #     (PinballWizard.Web.Tests) pins source ↔ policy agreement.
          #   style-src — 'unsafe-inline' is the documented posture for
          #     MudBlazor (44 inline style attributes + 3 style elements on
          #     the landing page alone; dynamic, not hashable). Microsoft's
          #     Blazor CSP guidance: "If the app uses inline styles, specify
          #     unsafe-inline." Inline style injection is a far weaker vector
          #     than script, and script-src above stays strict.
          #   object-src 'none' — present in every Microsoft-recommended
          #     Blazor policy; closes the legacy <object>/<embed> vector.
          #   connect-src — explicit wss://pinwiz.ai for the Blazor Server
          #     SignalR circuit. Chromium treats 'self' as covering
          #     same-origin wss:, but that is not uniform across engines and
          #     a blocked circuit means a dead page. Scoped to the host —
          #     never blanket wss:.
          "Content-Security-Policy-Report-Only" = {
            operation = "set"
            value = join("; ", [
              "default-src 'self'",
              "script-src 'self' 'sha256-tUlm0hcIzvUo+8JL4TQFYJRvk6dkJyUDnz0hU0DxicI=' 'sha256-kgZ10ePtncA3i5/cE4HcaLI+YYi/z4LDy3TmlIQ9kzM=' https://cdn.jsdelivr.net/npm/mermaid@11.15.0/dist/mermaid.min.js",
              "style-src 'self' 'unsafe-inline'",
              "img-src 'self' data: https://sternpinball.com",
              "font-src 'self'",
              "connect-src 'self' wss://pinwiz.ai",
              "object-src 'none'",
              "frame-ancestors 'none'",
              "base-uri 'self'",
              "form-action 'self'",
              # upgrade-insecure-requests intentionally absent: the browser
              # ignores it in a Report-Only policy and logs a console warning
              # on every load. Reintroduce it when the policy is promoted to
              # an enforced Content-Security-Policy header (§7.2 rollout).
              # report-uri intentionally absent: the app never implemented a
              # /_csp-reports receiver, so every violation report 400'd —
              # pure console noise on each page load (observed 2026-06-10).
              # Violations remain visible in DevTools > Issues. If reporting
              # is wanted for the staged enforcement rollout (§7.2), add a
              # receiving endpoint first, then restore this directive.
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
