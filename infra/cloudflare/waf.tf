# waf.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §5 (WAF & Bot Management).
#
# In v5, all WAF configuration is expressed as cloudflare_ruleset resources
# attached to specific request-handling phases. Managed and custom rulesets
# live in different phases and are configured as separate resources.

# ─────────────────────────────────────────────────────────────────────
# Managed rulesets — deploy the Cloudflare-published rules
# ─────────────────────────────────────────────────────────────────────
# Important: deploy in LOG mode first for 48-72 hours, watch the WAF events
# dashboard for false positives, then change action to BLOCK in a follow-up
# PR. The configuration below shows the final (post-staging) state.

resource "cloudflare_ruleset" "zone_waf_managed" {
  zone_id     = var.zone_id
  name        = "Zone WAF — managed rulesets"
  description = "Cloudflare Managed, OWASP Core, and Exposed Credentials Check rulesets"
  kind        = "zone"
  phase       = "http_request_firewall_managed"

  rules = [
    {
      action      = "execute"
      description = "Execute Cloudflare Managed Ruleset"
      expression  = "true"
      enabled     = true
      action_parameters = {
        id = local.managed_ruleset_cloudflare_managed
      }
    },
    {
      action      = "execute"
      description = "Execute OWASP Core Ruleset (PL1 — start low, tune up)"
      expression  = "true"
      enabled     = true
      action_parameters = {
        id = local.managed_ruleset_owasp_core
        overrides = {
          # Paranoia Level 1 only — PL2+ produces excess false positives
          # until baselined. Promote after analytics tuning.
          categories = [
            {
              category = "paranoia-level-2"
              enabled  = false
            },
            {
              category = "paranoia-level-3"
              enabled  = false
            },
            {
              category = "paranoia-level-4"
              enabled  = false
            },
          ]
        }
      }
    },
    {
      action      = "execute"
      description = "Execute Exposed Credentials Check Ruleset"
      expression  = "true"
      enabled     = true
      action_parameters = {
        id = local.managed_ruleset_exposed_credentials
      }
    },
  ]
}

# ─────────────────────────────────────────────────────────────────────
# Bot management — Super Bot Fight Mode (Pro plan)
# ─────────────────────────────────────────────────────────────────────
# The legacy `fight_mode` flag is NOT writable on this Pro zone — the API
# rejects it with "zone does not have access to fight mode" (it only
# echoes back read-only under stale_zone_configuration). Pro zones
# configure bot protection through the Super Bot Fight Mode (sbfm_*) and
# the newer protection fields below.
#
# Values mirror the live zone state so the import is behaviour-preserving,
# with one deliberate hardening: sbfm_definitely_automated is set to
# "block" (was "allow") so definitely-automated traffic is dropped rather
# than passed. Appropriate for a pre-launch, access-gated showcase.
#
# Import: tofu import cloudflare_bot_management.this <zone_id>

resource "cloudflare_bot_management" "this" {
  zone_id = var.zone_id

  # JavaScript Detections OFF while the enforced CSP is hash-strict and the
  # site sits behind Cloudflare Access (issue #356, decision-log 2026-06-11).
  # JSD injects an INLINE script into every HTML response; Cloudflare's only
  # documented strict-CSP accommodation is nonce propagation (it stamps the
  # nonce parsed from the CSP response header onto the injected script),
  # which requires a fresh per-request nonce — impossible from our static
  # Transform Rule header — and hash allowances for JSD are not supported.
  # Behind the Access OTP gate the JS bot-signal is near-zero marginal value
  # (no WAF rule keys on js_detection.passed). Revisit at public launch:
  # options are a Worker-minted per-request nonce or accepting the lost
  # signal.
  enable_js = false

  # Blocks AI-training crawlers (GPTBot, CCBot, …) only. Verified bots
  # (Googlebot, Bingbot, social link-preview fetchers) are exempt via
  # sbfm_verified_bots = "allow" below, so the project's intentional
  # OG / JSON-LD metadata serving to legitimate consumers is unaffected.
  ai_bots_protection = "block"
  crawler_protection = "enabled"

  # Super Bot Fight Mode classification handling.
  sbfm_definitely_automated       = "block" # hardened from live "allow"
  sbfm_verified_bots              = "allow" # don't penalise known-good crawlers
  sbfm_static_resource_protection = false   # don't gate JS/CSS/img assets

  # Content scraping protection — left at the live value ("disabled").
  # Flipping this on can interfere with legitimate metadata consumers
  # (OG / JSON-LD readers) the project intentionally serves; revisit
  # post-launch with telemetry.
  content_bots_protection = "disabled"

  # Keep Cloudflare's managed robots.txt on. The live zone already has
  # this enabled; omitting it lets the provider default it to false on
  # import, silently turning it off. Consistent with the project's
  # polite-by-construction posture — the managed robots.txt advertises
  # crawler/AI-bot policy for pinwiz.ai to well-behaved clients.
  is_robots_txt_managed = true
}

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
