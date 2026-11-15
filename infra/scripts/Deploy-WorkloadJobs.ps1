<#
.SYNOPSIS
    Deploys the pinwiz.ai ACA Jobs workload stack (ADR-0060).

.DESCRIPTION
    Orchestrates `infra/main-jobs.bicep` via an Azure Deployment Stack at
    **resource-group** scope (`az stack group create`). The template references
    platform resources by naming convention and never creates or preflights
    Microsoft.CognitiveServices — so image-only repoints cannot trip Foundry
    RTFP (715-123420) or strip platform RBAC.

    INVARIANT: this script MUST NOT use `az deployment`. All mutations go
    through `az stack group create`. ACA jobs are NEVER repointed with
    `az containerapp job update --image` (CLAUDE.md / DLV-02).

    Ordering: the platform stack (`pinwiz-shared-{env}`) must already exist.
    See docs/runbooks/migrate-aca-jobs-to-workload-stack.md before the first
    live cutover (detachResources sequencing).

.PARAMETER Environment
    Target environment. Must be `dev` or `prod`.

.PARAMETER WhatIf
    Validate only (`az stack group validate`). No mutations.

.PARAMETER SkipGuard
    Skip the subscription/tenant guard. NEVER use in normal operation.

.PARAMETER CliImageTag
    Full CLI image reference. If empty, auto-discovers from a running job
    (same convention as Deploy-SharedResources.ps1).

.EXAMPLE
    pwsh ./infra/scripts/Deploy-WorkloadJobs.ps1 -Environment dev -WhatIf

.EXAMPLE
    pwsh ./infra/scripts/Deploy-WorkloadJobs.ps1 -Environment dev `
      -CliImageTag 'pinwizacrdevbuutj.azurecr.io/pinwiz-cli:abc123'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [Parameter()]
    [switch]$WhatIf,

    [Parameter()]
    [switch]$SkipGuard,

    [Parameter()]
    [string]$CliImageTag = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$EXPECTED_TENANT_ID       = '9793cd0f-2b27-4757-9986-1f7f1e35864a'
$EXPECTED_SUBSCRIPTION_ID = 'b1f33f17-74a9-4ecc-b46c-c4f31776b840'

$stackName = "pinwiz-jobs-$Environment"
$rg        = "rg-pinwiz-shared-$Environment"

$scriptDir = $PSScriptRoot
$infraDir  = Split-Path -Parent $scriptDir

. (Join-Path $scriptDir 'BicepParamOverlay.ps1')

$templateFile    = Join-Path $infraDir 'main-jobs.bicep'
$committedParams = Join-Path $infraDir "main-jobs.$Environment.bicepparam"
$localOverride   = Join-Path $infraDir "main-jobs.$Environment.local.bicepparam"

# #400: never wholesale-replace committed params with *.local.bicepparam.
# Workload stack has no personal overlays today — local file is ignored with
# a warning if present (cliImageTag comes from -CliImageTag / discovery).
$paramSource = Resolve-BicepParamSource `
    -CommittedPath $committedParams `
    -LocalPath $localOverride `
    -AllowedLocalParams @()
$parametersFile = $paramSource.ParametersFile
$paramOverlays = @($paramSource.OverlayArgs)

if (-not (Test-Path $templateFile)) {
    throw "Template file not found: $templateFile"
}
if (-not (Test-Path $parametersFile)) {
    throw "Parameters file not found: $parametersFile"
}

Write-Host '[1/4] Checking tooling...' -ForegroundColor Cyan
$azVersion = az version --output json 2>$null | ConvertFrom-Json
if (-not $azVersion) {
    throw 'Azure CLI (az) is not installed or not on PATH.'
}
Write-Host "  Azure CLI: $($azVersion.'azure-cli')"

Write-Host '[2/4] Verifying az context against the personal Earlybird tenant...' -ForegroundColor Cyan
$ctx = az account show --output json 2>$null | ConvertFrom-Json
if (-not $ctx) {
    throw 'Not logged in to Azure. Run `az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a` first.'
}

$contextOk = $true
if ($ctx.tenantId -ne $EXPECTED_TENANT_ID) {
    $contextOk = $false
    Write-Host "  Tenant mismatch: expected $EXPECTED_TENANT_ID, got $($ctx.tenantId)" -ForegroundColor Red
}
if ($ctx.id -ne $EXPECTED_SUBSCRIPTION_ID) {
    $contextOk = $false
    Write-Host "  Subscription mismatch: expected $EXPECTED_SUBSCRIPTION_ID, got $($ctx.id)" -ForegroundColor Red
}
if (-not $contextOk) {
    if ($SkipGuard) {
        Write-Host '  -SkipGuard was specified. PROCEEDING ANYWAY.' -ForegroundColor Yellow
    }
    else {
        throw 'Subscription guard tripped — aborting (ADR 0010).'
    }
}
else {
    Write-Host "  Tenant/subscription OK ($($ctx.name))" -ForegroundColor Green
}

Write-Host '[3/4] Building Bicep template...' -ForegroundColor Cyan
az bicep build --file $templateFile
if ($LASTEXITCODE -ne 0) {
    throw 'Bicep build failed.'
}
Write-Host '  Bicep build: OK' -ForegroundColor Green

$placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'
if ([string]::IsNullOrEmpty($CliImageTag)) {
    $discovered = az containerapp job list -g $rg `
        --query "[?starts_with(name, 'pinwiz-job-linker')].template.containers[0].image | [0]" -o tsv 2>$null
    if ($discovered -and $discovered -ne $placeholderImage) {
        $CliImageTag = $discovered
        Write-Host "  cliImageTag: $CliImageTag (auto-discovered)" -ForegroundColor DarkGray
    }
    else {
        throw "Could not resolve cliImageTag. Pass -CliImageTag explicitly."
    }
}
else {
    Write-Host "  cliImageTag: $CliImageTag (caller-supplied)" -ForegroundColor DarkGray
}

Write-Host "[4/4] Deployment Stack '$stackName' (RG scope: $rg)..." -ForegroundColor Cyan
Write-Host '  NOTE: this stack does NOT include CognitiveServices/Foundry.' -ForegroundColor DarkGray

if ($WhatIf) {
    az stack group validate `
        --name $stackName `
        --resource-group $rg `
        --template-file $templateFile `
        --parameters $parametersFile `
            @paramOverlays `
            cliImageTag="$CliImageTag" `
        --action-on-unmanage deleteResources `
        --deny-settings-mode none
    if ($LASTEXITCODE -ne 0) {
        throw 'Workload stack validation failed.'
    }
    Write-Host 'Validation complete. No changes applied.' -ForegroundColor Green
}
else {
    az stack group create `
        --name $stackName `
        --resource-group $rg `
        --template-file $templateFile `
        --parameters $parametersFile `
            @paramOverlays `
            cliImageTag="$CliImageTag" `
        --action-on-unmanage deleteResources `
        --deny-settings-mode none `
        --yes
    if ($LASTEXITCODE -ne 0) {
        throw 'Workload stack create/update failed.'
    }
    Write-Host "Deployment Stack '$stackName' updated successfully." -ForegroundColor Green
}
