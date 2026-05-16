# rate_limit.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §6 (Rate Limiting).
#
# In v5, rate limiting is expressed as rules in the http_ratelimit phase
# of a zone ruleset. Each rule defines its own characteristics, period,
# and threshold — multiple rules can coexist in the same ruleset.

resource "cloudflare_ruleset" "rate_limits" {
  zone_id     = var.zone_id
  name        = "Zone rate limits"
  description = "Per-route rate limits scoped per client IP"
  kind        = "zone"
  phase       = "http_ratelimit"

  rules = [
    # Global ceiling — catches obvious abuse across the whole zone.
    {
      action      = "block"
      description = "Global rate limit — 600 req/min per IP"
      expression  = "true"
      enabled     = true
      ratelimit = {
        characteristics     = ["ip.src", "cf.colo.id"]
        period              = 60
        requests_per_period = 600
        mitigation_timeout  = 600
      }
    },

    # Future RAG/chat endpoint — every request costs real money.
    # The rule is in place ahead of the endpoint launching, so the
    # cost ceiling is reviewed before the endpoint goes live.
    {
      action      = "block"
      description = "Chat/RAG endpoint — 30 req/min per IP"
      enabled     = true
      expression  = <<-EOT
        (starts_with(http.request.uri.path, "/api/chat") or
         starts_with(http.request.uri.path, "/api/query"))
      EOT
      ratelimit = {
        characteristics     = ["ip.src", "cf.colo.id"]
        period              = 60
        requests_per_period = 30
        mitigation_timeout  = 600
      }
    },

    # Future authentication endpoints. Tight limit, long mitigation —
    # credential stuffing is patient.
    {
      action      = "managed_challenge"
      description = "Auth endpoints — 5 req/min per IP, then challenge"
      enabled     = true
      expression  = <<-EOT
        (starts_with(http.request.uri.path, "/api/auth") or
         http.request.uri.path eq "/login")
      EOT
      ratelimit = {
        characteristics     = ["ip.src", "cf.colo.id"]
        period              = 60
        requests_per_period = 5
        mitigation_timeout  = 3600
      }
    },
  ]
}
