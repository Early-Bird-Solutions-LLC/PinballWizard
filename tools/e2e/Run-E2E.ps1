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
    [string]$AiSearchEndpoint = '',
    [string]$AiFoundryProjectEndpoint = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')

if (-not $CosmosAccountEndpoint -or -not $AiSearchEndpoint -or -not $AiFoundryProjectEndpoint) {
    Write-Host "Discovering live-stack endpoints from $ApiAppName..." -ForegroundColor DarkGray
    $envJson = az containerapp show -n $ApiAppName -g $ResourceGroup `
        --query "properties.template.containers[0].env" -o json | ConvertFrom-Json
    $envMap = @{}
    foreach ($e in $envJson) { $envMap[$e.name] = $e.value }

    if (-not $CosmosAccountEndpoint)      { $CosmosAccountEndpoint      = $envMap['Cosmos__AccountEndpoint'] }
    if (-not $AiSearchEndpoint)           { $AiSearchEndpoint           = $envMap['AiSearch__Endpoint'] }
    if (-not $AiFoundryProjectEndpoint)   { $AiFoundryProjectEndpoint   = $envMap['AiFoundry__ProjectEndpoint'] }
}

if (-not $CosmosAccountEndpoint -or -not $AiSearchEndpoint -or -not $AiFoundryProjectEndpoint) {
    throw 'Could not resolve live-stack endpoints. Pass -CosmosAccountEndpoint / -AiSearchEndpoint / -AiFoundryProjectEndpoint explicitly.'
}

Write-Host "  Cosmos:  $CosmosAccountEndpoint" -ForegroundColor DarkGray
Write-Host "  Search:  $AiSearchEndpoint" -ForegroundColor DarkGray
Write-Host "  Foundry: $AiFoundryProjectEndpoint" -ForegroundColor DarkGray

$env:Cosmos__AccountEndpoint = $CosmosAccountEndpoint
$env:AiSearch__Endpoint = $AiSearchEndpoint
$env:AiFoundry__ProjectEndpoint = $AiFoundryProjectEndpoint

# Build once so the fixture's `dotnet run` calls are up-to-date checks,
# and ensure the Playwright chromium binary is present.
dotnet build (Join-Path $repoRoot 'PinballWizard.slnx') -v q
& (Join-Path $repoRoot 'tests/PinballWizard.Web.Tests/bin/Debug/net10.0/playwright.ps1') install chromium

dotnet test (Join-Path $repoRoot 'tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj') `
    --no-build `
    --filter 'Category=E2E' `
    --logger 'console;verbosity=normal'
