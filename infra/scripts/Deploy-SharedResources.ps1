<#
.SYNOPSIS
    Deploys the pinwiz.ai shared-tier Azure resources.

.DESCRIPTION
    Orchestrates the deployment of `infra/main-shared.bicep` to the personal
    Earlybird Azure subscription. Enforces the ADR 0010 subscription/tenant
    guard before any deployment occurs — if the active `az` context is NOT
    the personal Earlybird tenant + subscription, the script aborts with a
    clear message and does NOT touch Azure.

.PARAMETER Environment
    Target environment. Must be `dev` or `prod`. Drives which `.bicepparam`
    file is used and is part of every resource name.

.PARAMETER WhatIf
    Run the deployment in what-if mode (Azure shows the diff but applies
    nothing). Use this on every PR before merging Bicep changes.

.PARAMETER SkipGuard
    Skip the subscription/tenant guard. NEVER use this in normal operation.
    Provided only for the case of testing the deploy script itself in
    a non-Earlybird subscription explicitly. Carries an unmissable warning.

.EXAMPLE
    pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
    Validates the deployment without making changes. Prints the resource diff.

.EXAMPLE
    pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
    Performs the actual deployment. Prompts for confirmation before applying.

.NOTES
    Per the locked feedback memory `feedback_personal_identity_only.md`,
    this script must NEVER deploy to the day-job tenant. The hard guard
    on EXPECTED_TENANT_ID + EXPECTED_SUBSCRIPTION_ID below is the
    enforcement.

    Requires: Azure CLI (`az`) >= 2.50, Bicep CLI (auto-installed by
    Azure CLI), pwsh 7+.
#>

# Note: NOT using SupportsShouldProcess=$true on [CmdletBinding(...)]. Doing so
# reserves -WhatIf as a PowerShell common parameter, which collides with our
# explicit [switch]$WhatIf below ('A parameter with the name "WhatIf" was
# defined multiple times for the command'). The script does not call
# $PSCmdlet.ShouldProcess() anywhere, so SupportsShouldProcess was dead anyway —
# we keep the explicit switch and consume it via `if ($WhatIf)` at line ~175.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [Parameter()]
    [switch]$WhatIf,

    [Parameter()]
    [switch]$SkipGuard
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# -----------------------------------------------------------------------------
# Hard-coded guard values — personal Earlybird tenant + subscription only.
# Per ADR 0010 these MUST match the active az context before any deployment.
# Changing these IDs requires a superseding ADR.
# -----------------------------------------------------------------------------

$EXPECTED_TENANT_ID       = '9793cd0f-2b27-4757-9986-1f7f1e35864a'  # Earlybird
$EXPECTED_SUBSCRIPTION_ID = '4dce9fdd-ea5f-4f67-9a00-80279e58659d'  # Earlybird personal

# -----------------------------------------------------------------------------
# Paths (script-relative so it runs from anywhere)
# -----------------------------------------------------------------------------

$scriptDir = $PSScriptRoot
$infraDir  = Split-Path -Parent $scriptDir

$templateFile   = Join-Path $infraDir 'main-shared.bicep'
$parametersFile = Join-Path $infraDir "main-shared.$Environment.bicepparam"
$localOverride  = Join-Path $infraDir "main-shared.$Environment.local.bicepparam"

if (Test-Path $localOverride) {
    Write-Host "Using LOCAL override parameters file: $localOverride" -ForegroundColor Yellow
    $parametersFile = $localOverride
}

if (-not (Test-Path $templateFile)) {
    throw "Template file not found: $templateFile"
}
if (-not (Test-Path $parametersFile)) {
    throw "Parameters file not found: $parametersFile"
}

# -----------------------------------------------------------------------------
# Tooling check
# -----------------------------------------------------------------------------

Write-Host '[1/5] Checking tooling...' -ForegroundColor Cyan

$azVersion = az version --output json 2>$null | ConvertFrom-Json
if (-not $azVersion) {
    throw "Azure CLI (az) is not installed or not on PATH. Install from https://learn.microsoft.com/cli/azure/install-azure-cli"
}
Write-Host "  Azure CLI: $($azVersion.'azure-cli')"
Write-Host "  Bicep:     $($azVersion.'azure-cli-extensions' -join ', ' )"

# -----------------------------------------------------------------------------
# Subscription / tenant guard (ADR 0010)
# -----------------------------------------------------------------------------

Write-Host '[2/5] Verifying az context against the personal Earlybird tenant...' -ForegroundColor Cyan

$ctx = az account show --output json 2>$null | ConvertFrom-Json
if (-not $ctx) {
    throw 'Not logged in to Azure. Run `az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a` first.'
}

$contextOk = $true
$contextProblems = @()

if ($ctx.tenantId -ne $EXPECTED_TENANT_ID) {
    $contextOk = $false
    $contextProblems += "  Tenant mismatch:       expected $EXPECTED_TENANT_ID, got $($ctx.tenantId) ($($ctx.tenantDefaultDomain))"
}
if ($ctx.id -ne $EXPECTED_SUBSCRIPTION_ID) {
    $contextOk = $false
    $contextProblems += "  Subscription mismatch: expected $EXPECTED_SUBSCRIPTION_ID, got $($ctx.id) ($($ctx.name))"
}

if (-not $contextOk) {
    Write-Host ''
    Write-Host '  +-----------------------------------------------------------+' -ForegroundColor Red
    Write-Host '  |  SUBSCRIPTION GUARD TRIPPED (per ADR 0010)                |' -ForegroundColor Red
    Write-Host '  |                                                           |' -ForegroundColor Red
    Write-Host '  |  This repo deploys ONLY to the personal Earlybird tenant. |' -ForegroundColor Red
    Write-Host '  |  Refusing to proceed.                                     |' -ForegroundColor Red
    Write-Host '  +-----------------------------------------------------------+' -ForegroundColor Red
    Write-Host ''
    $contextProblems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ''
    Write-Host '  To fix:'
    Write-Host '    az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a'
    Write-Host "    az account set --subscription $EXPECTED_SUBSCRIPTION_ID"
    Write-Host ''

    if ($SkipGuard) {
        Write-Host '  -SkipGuard was specified. PROCEEDING ANYWAY.' -ForegroundColor Yellow
        Write-Host '  This is a script-development override. Do not use it in normal ops.' -ForegroundColor Yellow
    }
    else {
        throw 'Subscription guard tripped — aborting.'
    }
}
else {
    Write-Host "  Tenant:       $($ctx.tenantId) ($($ctx.tenantDefaultDomain))" -ForegroundColor Green
    Write-Host "  Subscription: $($ctx.id) ($($ctx.name))" -ForegroundColor Green
    Write-Host "  Signed in as: $($ctx.user.name) [$($ctx.user.type)]" -ForegroundColor Green
}

# -----------------------------------------------------------------------------
# Bicep build (syntax check)
# -----------------------------------------------------------------------------

Write-Host '[3/5] Building Bicep template (syntax check only)...' -ForegroundColor Cyan
az bicep build --file $templateFile
if ($LASTEXITCODE -ne 0) {
    throw 'Bicep build failed. Fix syntax errors before deploying.'
}
Write-Host '  Bicep build: OK' -ForegroundColor Green

# -----------------------------------------------------------------------------
# Deployment
# -----------------------------------------------------------------------------

$deploymentName = "pinwiz-shared-$Environment-$(Get-Date -Format 'yyyyMMddHHmmss')"
$location       = 'eastus2'

if ($WhatIf) {
    Write-Host '[4/5] Running what-if (no changes will be applied)...' -ForegroundColor Cyan
    az deployment sub what-if `
        --name $deploymentName `
        --location $location `
        --template-file $templateFile `
        --parameters $parametersFile
    if ($LASTEXITCODE -ne 0) {
        throw 'what-if failed.'
    }
    Write-Host ''
    Write-Host '[5/5] What-if complete. No changes applied.' -ForegroundColor Green
}
else {
    Write-Host '[4/5] Deploying...' -ForegroundColor Cyan

    if ($PSCmdlet.ShouldProcess("subscription $EXPECTED_SUBSCRIPTION_ID", "Deploy pinwiz shared resources ($Environment)")) {
        az deployment sub create `
            --name $deploymentName `
            --location $location `
            --template-file $templateFile `
            --parameters $parametersFile `
            --output table
        if ($LASTEXITCODE -ne 0) {
            throw 'Deployment failed.'
        }
        Write-Host ''
        Write-Host "[5/5] Deployment complete. Deployment name: $deploymentName" -ForegroundColor Green
    }
}
