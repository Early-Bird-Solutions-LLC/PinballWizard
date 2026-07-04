# Run-BugFinder.ps1 — Crawl the public PinWiz site and generate a bug report.
#
# Runs the BugFinder suite (Category=BugFinder) which:
#   1. Discovers all public pages via crawler + link-walking
#   2. Checks each page for functional bugs (console errors, network failures,
#      error-page redirects, load time, missing title/meta)
#   3. Runs an AI-powered UI expert review (GPT-4o vision, desktop + mobile)
#   4. Writes a markdown report to tools/e2e/bug-reports/
#
# MODES:
#   -TargetUrl   Drive a specific deployment directly (fastest; skips Az discovery)
#   (no args)    Autodiscover endpoints from the dev Azure container app
#
# EXAMPLES:
#   # Against deployed dev:
#   .\tools\e2e\Run-BugFinder.ps1 -TargetUrl https://pinwiz-ca-wizard-dev.graybay-045982b4.eastus2.azurecontainerapps.io
#
#   # Against production:
#   .\tools\e2e\Run-BugFinder.ps1 -TargetUrl https://pinwiz.earlybirdsolutions.com
#
#   # Auto-discover from Azure (requires az login):
#   .\tools\e2e\Run-BugFinder.ps1
#
[CmdletBinding()]
param(
    [string]$TargetUrl = '',
    [string]$ResourceGroup = 'rg-pinwiz-shared-dev',
    [string]$ApiAppName    = 'pinwiz-ca-api-dev',
    [string]$WebAppName    = 'pinwiz-ca-wizard-dev',
    [string]$CosmosAccountEndpoint    = '',
    [string]$CosmosAccountResourceId  = '',
    [string]$AiSearchEndpoint         = '',
    [string]$AiSearchIndexName        = '',
    [string]$AiFoundryProjectEndpoint = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')

# ── Mode 1: TargetUrl provided — skip Azure discovery ────────────────────────
if ($TargetUrl) {
    Write-Host "Bug finder → target: $TargetUrl" -ForegroundColor Cyan
    $env:E2E__BaseUrl = $TargetUrl.TrimEnd('/')

    # AiFoundry for UI review (optional — gracefully skipped if absent)
    if (-not $env:AiFoundry__ProjectEndpoint -and $AiFoundryProjectEndpoint) {
        $env:AiFoundry__ProjectEndpoint = $AiFoundryProjectEndpoint
    }

    if ($env:AiFoundry__ProjectEndpoint) {
        Write-Host "UI review: enabled (AiFoundry__ProjectEndpoint is set)" -ForegroundColor DarkGray
    } else {
        Write-Host "UI review: disabled (set AiFoundry__ProjectEndpoint to enable GPT-4o vision)" -ForegroundColor DarkYellow
    }
}
# ── Mode 2: Autodiscover from Azure ─────────────────────────────────────────
else {
    Write-Host "Discovering live-stack endpoints from $ApiAppName..." -ForegroundColor DarkGray

    $envJson = az containerapp show -n $ApiAppName -g $ResourceGroup `
        --query "properties.template.containers[0].env" -o json | ConvertFrom-Json
    $envMap = @{}
    foreach ($e in $envJson) { $envMap[$e.name] = $e.value }

    if (-not $CosmosAccountEndpoint)    { $CosmosAccountEndpoint    = $envMap['Cosmos__AccountEndpoint'] }
    if (-not $CosmosAccountResourceId)  { $CosmosAccountResourceId  = $envMap['Cosmos__AccountResourceId'] }
    if (-not $AiSearchEndpoint)         { $AiSearchEndpoint         = $envMap['AiSearch__Endpoint'] }
    if (-not $AiSearchIndexName)        { $AiSearchIndexName        = $envMap['AiSearch__IndexName'] }
    if (-not $AiFoundryProjectEndpoint) { $AiFoundryProjectEndpoint = $envMap['AiFoundry__ProjectEndpoint'] }

    if (-not $CosmosAccountEndpoint -or -not $AiSearchEndpoint) {
        throw 'Could not resolve live-stack endpoints. Pass -TargetUrl for direct targeting or ensure az login is active.'
    }

    # Discover web FQDN
    $webFqdn = az containerapp show -n $WebAppName -g $ResourceGroup `
        --query "properties.configuration.ingress.fqdn" -o tsv
    if (-not $webFqdn) {
        throw "Could not resolve FQDN for $WebAppName. Pass -TargetUrl explicitly."
    }

    $env:Cosmos__AccountEndpoint    = $CosmosAccountEndpoint
    $env:Cosmos__AccountResourceId  = $CosmosAccountResourceId
    $env:AiSearch__Endpoint         = $AiSearchEndpoint
    $env:AiSearch__IndexName        = $AiSearchIndexName
    $env:AiFoundry__ProjectEndpoint = $AiFoundryProjectEndpoint

    $env:E2E__BaseUrl = "https://$webFqdn"
    Write-Host "  Web:    https://$webFqdn" -ForegroundColor DarkGray
    Write-Host "  Cosmos: $CosmosAccountEndpoint" -ForegroundColor DarkGray
    Write-Host "  Search: $AiSearchEndpoint ($AiSearchIndexName)" -ForegroundColor DarkGray
    Write-Host "  Foundry:$AiFoundryProjectEndpoint" -ForegroundColor DarkGray
}

# ── Build + install Playwright ────────────────────────────────────────────────
Write-Host ""
Write-Host "Building solution..." -ForegroundColor DarkGray
dotnet build (Join-Path $repoRoot 'PinballWizard.slnx') -v q

$playwrightScript = Join-Path $repoRoot 'tests/PinballWizard.Web.Tests/bin/Debug/net10.0/playwright.ps1'
if (Test-Path $playwrightScript) {
    Write-Host "Ensuring Playwright chromium is installed..." -ForegroundColor DarkGray
    & $playwrightScript install chromium
}

# ── Run the bug finder ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Starting bug finder crawl..." -ForegroundColor Cyan
Write-Host ""

dotnet test (Join-Path $repoRoot 'tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj') `
    --no-build `
    --filter 'Category=BugFinder' `
    --logger 'console;verbosity=normal'

# ── Print report location ─────────────────────────────────────────────────────
$reportDir = Join-Path $repoRoot 'tools/e2e/bug-reports'
if (Test-Path $reportDir) {
    $latest = Get-ChildItem $reportDir -Filter 'bug-report-*.md' |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First 1
    if ($latest) {
        Write-Host ""
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
        Write-Host "  Report: $($latest.FullName)" -ForegroundColor Green
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
        Write-Host ""
        Get-Content $latest.FullName | Select-Object -First 30
    }
}
