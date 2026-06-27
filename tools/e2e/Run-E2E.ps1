# Run-E2E.ps1 — run the local end-to-end suite (Category=E2E).
#
# Launches the REAL Api + Web apps against the live dev Azure stack and
# drives a real browser through the landing + ask flows (see
# tests/PinballWizard.Web.Tests/E2E/). Requires:
#   - an authenticated Azure CLI session (az login) with access to the
#     pinwiz dev resource group (DefaultAzureCredential picks it up)
#   - Playwright chromium installed (the script installs it if missing)
#
# Endpoints are auto-discovered from the deployed Api container app so
# nothing instance-specific is hardcoded here. Override any of them via
# parameters when testing against a different stack.
#
# Cost note: the ask-flow test makes one real model call per run.
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-pinwiz-shared-dev',
    [string]$ApiAppName = 'pinwiz-ca-api-dev',
    [string]$CosmosAccountEndpoint = '',
    [string]$CosmosAccountResourceId = '',
    [string]$AiSearchEndpoint = '',
    [string]$AiSearchIndexName = '',
    [string]$AiFoundryProjectEndpoint = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')

if (-not $CosmosAccountEndpoint -or -not $CosmosAccountResourceId -or
    -not $AiSearchEndpoint -or -not $AiSearchIndexName -or
    -not $AiFoundryProjectEndpoint) {
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
}

if (-not $CosmosAccountEndpoint -or -not $CosmosAccountResourceId -or -not $AiSearchEndpoint -or -not $AiSearchIndexName -or -not $AiFoundryProjectEndpoint) {
    throw 'Could not resolve live-stack endpoints. Pass -CosmosAccountEndpoint / -CosmosAccountResourceId / -AiSearchEndpoint / -AiSearchIndexName / -AiFoundryProjectEndpoint explicitly.'
}

Write-Host "  Cosmos:       $CosmosAccountEndpoint" -ForegroundColor DarkGray
Write-Host "  Cosmos ResId: $CosmosAccountResourceId" -ForegroundColor DarkGray
Write-Host "  Search:       $AiSearchEndpoint ($AiSearchIndexName)" -ForegroundColor DarkGray
Write-Host "  Foundry:      $AiFoundryProjectEndpoint" -ForegroundColor DarkGray

# The web app needs Cosmos + AiSearch directly (for admin pages) as well as the
# Api (for the ask flow). AiFoundry is passed to the Api only; LiveStackFixture
# strips it from the web process to avoid triggering the gated Foundry DI branch.
$env:Cosmos__AccountEndpoint    = $CosmosAccountEndpoint
$env:Cosmos__AccountResourceId  = $CosmosAccountResourceId
$env:AiSearch__Endpoint         = $AiSearchEndpoint
$env:AiSearch__IndexName        = $AiSearchIndexName
$env:AiFoundry__ProjectEndpoint = $AiFoundryProjectEndpoint

# Build once so the fixture's `dotnet run` calls are up-to-date checks,
# and ensure the Playwright chromium binary is present.
dotnet build (Join-Path $repoRoot 'PinballWizard.slnx') -v q
& (Join-Path $repoRoot 'tests/PinballWizard.Web.Tests/bin/Debug/net10.0/playwright.ps1') install chromium

dotnet test (Join-Path $repoRoot 'tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj') `
    --no-build `
    --filter 'Category=E2E' `
    --logger 'console;verbosity=normal'
