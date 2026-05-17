# logpush.tf
#
# Maps to CLOUDFLARE_PRELAUNCH_CHECKLIST.md §9 (Logging & Observability).
#
# Pushes Cloudflare HTTP request logs and firewall events to Azure Blob
# Storage for retention and forensic analysis.
#
# The destination URL includes a SAS token and is sensitive. It is passed
# in via the logpush_destination variable, which is itself populated from
# Azure Key Vault by the Bicep-managed infrastructure.
#
# Sensitive headers (Authorization, Cookie) are NOT exported.

resource "cloudflare_logpush_job" "http_requests" {
  count = var.logpush_destination == "" ? 0 : 1

  account_id       = var.account_id
  zone_id          = var.zone_id
  name             = "pinwiz-http-requests"
  dataset          = "http_requests"
  destination_conf = var.logpush_destination
  enabled          = true
  logpull_options  = "fields=BotScore,CacheCacheStatus,ClientIP,ClientRequestHost,ClientRequestMethod,ClientRequestPath,ClientRequestProtocol,ClientRequestReferer,ClientRequestUserAgent,EdgeEndTimestamp,EdgeResponseBytes,EdgeResponseStatus,EdgeStartTimestamp,RayID,WAFAction,WAFRuleID,WAFRuleMessage&timestamps=rfc3339"
  frequency        = "high"
  kind             = "edge"

  output_options = {
    output_type      = "ndjson"
    timestamp_format = "rfc3339"
  }
}

resource "cloudflare_logpush_job" "firewall_events" {
  count = var.logpush_destination == "" ? 0 : 1

  account_id       = var.account_id
  zone_id          = var.zone_id
  name             = "pinwiz-firewall-events"
  dataset          = "firewall_events"
  destination_conf = var.logpush_destination
  enabled          = true
  frequency        = "high"
  kind             = "edge"
}
