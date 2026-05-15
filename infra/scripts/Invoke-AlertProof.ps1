<#
.SYNOPSIS
    Proves each of the 5 PinballWizard alert rules by injecting synthetic
    App Insights telemetry directly via the v2/track REST endpoint.

.DESCRIPTION
    Sends telemetry events to the App Insights workspace that push each metric
    above (or below) its alert threshold. After injection, each alert evaluates
    on its normal cycle (5 min for latency / 5xx / cost / dead-letters;
    1 hour for availability) and emails jim@earlybirdsolutions.com.

    This script is the H-Alerts pre-launch gate drill per build-spec.md
    Phase 6 § Operational hand-offs. Record each email receipt timestamp
    in docs/decision-log.md.

    After proof, synthetic data ages out of the 48-h evaluation window
    automatically. No manual cleanup needed.

    ALERTS PROVEN:
      1. pinwiz-alert-latency-p95       — customMetrics: pinwiz.ai.duration_ms
      2. pinwiz-alert-5xx-rate          — requests: /api/wizard/ 5xx error rate
      3. pinwiz-alert-daily-cost        — customMetrics: pinwiz.ai.cost_usd_cents
      4. pinwiz-alert-dead-letters      — customMetrics: pinwiz.rag.changefeed_dead_letter_total
      5. pinwiz-alert-availability      — availabilityResults: synthetic failures

.PARAMETER IKey
    App Insights instrumentation key. Defaults to the dev workspace key.

.PARAMETER IngestionEndpoint
    App Insights ingestion endpoint. Defaults to the East US 2 endpoint.

.PARAMETER AlertIndex
    If specified, runs only that alert (1–5). Useful for re-running a single
    proof without re-triggering all alerts.

.EXAMPLE
    pwsh ./infra/scripts/Invoke-AlertProof.ps1
    Runs all 5 alert proofs in sequence.

.EXAMPLE
    pwsh ./infra/scripts/Invoke-AlertProof.ps1 -AlertIndex 1
    Proves only the latency alert.
#>

[CmdletBinding()]
param(
    [string]$IKey             = 'c275b795-18b2-4d26-81a9-14e7aa0e6401',
    [string]$IngestionEndpoint = 'https://eastus2-3.in.applicationinsights.azure.com',
    [int]$AlertIndex          = 0   # 0 = all
)

$ErrorActionPreference = 'Stop'
$trackUrl  = "$IngestionEndpoint/v2/track"
$iKeyClean = $IKey -replace '-', ''   # instrumentation key without hyphens for name field

function Send-Telemetry {
    param([object[]]$Items)
    $body = $Items | ConvertTo-Json -Depth 10 -AsArray
    $response = Invoke-RestMethod -Method Post -Uri $trackUrl `
        -Body $body -ContentType 'application/json' -ErrorAction Stop
    return $response
}

function New-MetricItem {
    param([string]$MetricName, [double]$Value, [int]$Count = 1)
    return @{
        iKey = $IKey
        name = "Microsoft.ApplicationInsights.$iKeyClean.Metric"
        time = (Get-Date).ToUniversalTime().ToString('o')
        data = @{
            baseType = 'MetricData'
            baseData = @{
                ver     = 2
                metrics = @(@{ name = $MetricName; value = $Value; count = $Count })
            }
        }
    }
}

function New-RequestItem {
    param([string]$Url, [string]$ResultCode, [bool]$Success)
    return @{
        iKey = $IKey
        name = "Microsoft.ApplicationInsights.$iKeyClean.Request"
        time = (Get-Date).ToUniversalTime().ToString('o')
        data = @{
            baseType = 'RequestData'
            baseData = @{
                ver          = 2
                id           = [System.Guid]::NewGuid().ToString()
                name         = "GET $Url"
                duration     = '00:00:01.000'
                responseCode = $ResultCode
                success      = $Success
                url          = $Url
            }
        }
    }
}

function New-AvailabilityItem {
    param([bool]$Success, [string]$Location = 'East US')
    return @{
        iKey = $IKey
        name = "Microsoft.ApplicationInsights.$iKeyClean.Availability"
        time = (Get-Date).ToUniversalTime().ToString('o')
        data = @{
            baseType = 'AvailabilityData'
            baseData = @{
                ver         = 2
                id          = [System.Guid]::NewGuid().ToString()
                name        = 'PinballWizard /alive ping'
                duration    = '00:00:05.000'
                success     = $Success
                runLocation = $Location
                message     = if ($Success) { 'OK' } else { 'Connection refused — placeholder image port mismatch (H-Alerts drill)' }
            }
        }
    }
}

# =============================================================================
# Alert 1 — pinwiz-alert-latency-p95
# Query: customMetrics | where name == "pinwiz.ai.duration_ms" | summarize p95=percentile(value, 95)
# Threshold: > 5000 ms
# Eval: every 5 min, 48-h window
# =============================================================================
if ($AlertIndex -eq 0 -or $AlertIndex -eq 1) {
    Write-Host "`n[1/5] Injecting latency metrics (pinwiz.ai.duration_ms = 9000 ms × 20)..." -ForegroundColor Cyan
    $items = 1..20 | ForEach-Object { New-MetricItem -MetricName 'pinwiz.ai.duration_ms' -Value 9000 }
    Send-Telemetry -Items $items | Out-Null
    Write-Host "  Sent. Alert evaluates every 5 min. Watch for email: 'PinballWizard — Wizard latency p95 > 5s'" -ForegroundColor Green
}

# =============================================================================
# Alert 2 — pinwiz-alert-5xx-rate
# Query: requests | where url contains "/api/wizard/" | summarize errorRate = ...
# Threshold: > 5%
# Eval: every 5 min
# =============================================================================
if ($AlertIndex -eq 0 -or $AlertIndex -eq 2) {
    Write-Host "`n[2/5] Injecting 5xx requests (19 failures + 1 success = 95% error rate)..." -ForegroundColor Cyan
    $items = @()
    # 19 server errors
    1..19 | ForEach-Object {
        $items += New-RequestItem -Url 'https://pinwiz.ai/api/wizard/ask:stream' -ResultCode '500' -Success $false
    }
    # 1 success (so count() > 0; pure 5xx would divide by 1 anyway but this is cleaner)
    $items += New-RequestItem -Url 'https://pinwiz.ai/api/wizard/ask:stream' -ResultCode '200' -Success $true
    Send-Telemetry -Items $items | Out-Null
    Write-Host "  Sent. Alert evaluates every 5 min. Watch for email: 'PinballWizard — 5xx error rate > 5%'" -ForegroundColor Green
}

# =============================================================================
# Alert 3 — pinwiz-alert-daily-cost
# Query: customMetrics | where name == "pinwiz.ai.cost_usd_cents" | summarize dailyCents = sum(value)
# Threshold: > 1500 cents ($15/day)
# Eval: every 15 min (48-h window)
# =============================================================================
if ($AlertIndex -eq 0 -or $AlertIndex -eq 3) {
    Write-Host "`n[3/5] Injecting cost metrics (pinwiz.ai.cost_usd_cents = 2000 cents = $20)..." -ForegroundColor Cyan
    $items = @(New-MetricItem -MetricName 'pinwiz.ai.cost_usd_cents' -Value 2000 -Count 1)
    Send-Telemetry -Items $items | Out-Null
    Write-Host "  Sent. Alert evaluates every 15 min. Watch for email: 'PinballWizard — Daily cost > $15'" -ForegroundColor Green
}

# =============================================================================
# Alert 4 — pinwiz-alert-dead-letters
# Query: customMetrics | where name == "pinwiz.rag.changefeed_dead_letter_total" | summarize depth = sum(value)
# Threshold: > 50
# Eval: every 5 min (1-h window)
# =============================================================================
if ($AlertIndex -eq 0 -or $AlertIndex -eq 4) {
    Write-Host "`n[4/5] Injecting dead-letter metrics (pinwiz.rag.changefeed_dead_letter_total = 75)..." -ForegroundColor Cyan
    $items = @(New-MetricItem -MetricName 'pinwiz.rag.changefeed_dead_letter_total' -Value 75 -Count 1)
    Send-Telemetry -Items $items | Out-Null
    Write-Host "  Sent. Alert evaluates every 5 min. Watch for email: 'PinballWizard — RAG dead-letter depth > 50'" -ForegroundColor Green
}

# =============================================================================
# Alert 5 — pinwiz-alert-availability
# Query: availabilityResults | summarize successRateTenths = toint(...* 1000)
# Threshold: < 995 (99.5%)
# Eval: every 1 hour (48-h window)
# =============================================================================
if ($AlertIndex -eq 0 -or $AlertIndex -eq 5) {
    Write-Host "`n[5/5] Injecting availability failures (20 failures = 0% success rate)..." -ForegroundColor Cyan
    $items = @()
    1..10 | ForEach-Object { $items += New-AvailabilityItem -Success $false -Location 'East US' }
    1..10 | ForEach-Object { $items += New-AvailabilityItem -Success $false -Location 'West US' }
    Send-Telemetry -Items $items | Out-Null
    Write-Host "  Sent. Alert evaluates every 1 HOUR. Watch for email: 'PinballWizard — Availability < 99.5% (48-h rolling)'" -ForegroundColor Yellow
    Write-Host "  NOTE: this is the slowest alert — may take up to 60 min after injection." -ForegroundColor Yellow
}

Write-Host "`n============================================================" -ForegroundColor Cyan
Write-Host "Synthetic telemetry injected. Expected email timeline:" -ForegroundColor Cyan
Write-Host "  ~5 min  — latency, 5xx, dead-letters alerts" -ForegroundColor White
Write-Host "  ~15 min — daily cost alert" -ForegroundColor White
Write-Host "  ~60 min — availability alert (1-hour eval cycle)" -ForegroundColor White
Write-Host "" -ForegroundColor White
Write-Host "Record each receipt timestamp in docs/decision-log.md." -ForegroundColor White
Write-Host "After proof, synthetic data ages out of the 48-h window automatically." -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Cyan
