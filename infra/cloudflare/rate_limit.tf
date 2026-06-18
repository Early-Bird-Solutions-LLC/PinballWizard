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
  description = "Per-route rate limits scoped per client IP, counted per Cloudflare colo"
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

    # NOTE: Cloudflare Pro caps the http_ratelimit phase at 2 rules. We
    # keep the two that protect *reachable* surface today: the zone-wide
    # ceiling above and the auth rule below.
    #
    # A third rule for the future RAG/chat endpoints (30 req/min on
    # /api/chat | /api/query) was intentionally removed — those endpoints
    # 404 until Phase 4, so a rule on them protects nothing, and the
    # ADR-0015 per-call cost ceiling is the primary cost guard regardless.
    # Re-introduce that rule in the PR that lands the chat endpoints,
    # trading out a slot or moving it to a WAF custom rule if the cap
    # still binds.

    # Authentication endpoints. Tight limit, long mitigation —
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
        # cf.colo.id is required by the API (error code 20155 without it).
        characteristics     = ["ip.src", "cf.colo.id"]
        period              = 60
        requests_per_period = 5
        mitigation_timeout  = 3600
      }
    },
  ]
}
