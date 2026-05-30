<#
.SYNOPSIS
    Deploys the pinwiz.ai shared-tier Azure resources via an Azure Deployment Stack.

.DESCRIPTION
    Orchestrates the deployment of `infra/main-shared.bicep` to the personal
    Earlybird Azure subscription using an Azure Deployment Stack. Deployment
    Stacks track every resource in the template and automatically delete orphaned
    resources when they are removed from Bicep — preventing the silent drift that
    plain `az deployment` creates.

    Enforces the ADR 0010 subscription/tenant guard before any deployment occurs.
    If the active `az` context is NOT the personal Earlybird tenant + subscription,
    the script aborts with a clear message and does NOT touch Azure.

    INVARIANT: this script MUST NOT use `az deployment sub create` or
    `az deployment group create`. All resource mutations go through
    `az stack sub create`. See CLAUDE.md § Locked invariants #16.

.PARAMETER Environment
    Target environment. Must be `dev` or `prod`. Drives which `.bicepparam`
    file is used and is part of every resource name.

.PARAMETER WhatIf
    Validate the deployment without applying it (via `az stack sub validate`).
    Azure runs full template + parameter + resource validation but mutates
    nothing. Note: deployment stacks do not expose the old property-level
    what-if diff (the `--what-if` flag was removed from `az stack sub` in CLI
    2.7x); validate is the supported pre-apply safety check. Use on every PR
    before merging Bicep changes.

.PARAMETER SkipGuard
    Skip the subscription/tenant guard. NEVER use this in normal operation.
    Provided only for the case of testing the deploy script itself in
    a non-Earlybird subscription explicitly. Carries an unmissable warning.

.EXAMPLE
    pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
    Validates the deployment without making changes. Prints the resource diff.

.EXAMPLE
    pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
    Performs the actual deployment via the pinwiz-shared-dev Deployment Stack.

.NOTES
    Per the locked feedback memory `feedback_personal_identity_only.md`,
    this script must NEVER deploy to the day-job tenant. The hard guard
    on EXPECTED_TENANT_ID + EXPECTED_SUBSCRIPTION_ID below is the enforcement.

    Requires: Azure CLI (`az`) >= 2.61 (deployment stacks + what-if support),
    Bicep CLI (auto-installed by Azure CLI), pwsh 7+.

    Deployment Stack behaviour:
      --action-on-unmanage deleteResources  resources removed from Bicep are
                                            deleted on next deploy; the resource
                                            group itself is not deleted (safe for
                                            resources that predate the stack).
      --deny-settings-mode none             no read-only lock; portal edits are
                                            allowed (appropriate for a dev showcase).
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
    [switch]$SkipGuard,

    # Image tags for the Wizard web app and Api. If not supplied, the script
    # reads the currently-deployed image from the running ACA app so a manual
    # Bicep re-deploy does not revert the image to the placeholder.
    # The CI/CD deploy workflow always supplies explicit :{sha} tags — never :latest.
    [Parameter()]
    [string]$WizardImageTag = '',

    [Parameter()]
    [string]$ApiImageTag = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# -----------------------------------------------------------------------------
# Hard-coded guard values — personal Earlybird tenant + subscription only.
# Per ADR 0010 these MUST match the active az context before any deployment.
# Changing these IDs requires a superseding ADR.
# -----------------------------------------------------------------------------

$EXPECTED_TENANT_ID       = '9793cd0f-2b27-4757-9986-1f7f1e35864a'  # Earlybird
$EXPECTED_SUBSCRIPTION_ID = 'b1f33f17-74a9-4ecc-b46c-c4f31776b840'  # pinwiz.ai

# Stable Deployment Stack name (not timestamped — same name on every run so
# Azure updates the existing stack rather than creating a new deployment).
$stackName = "pinwiz-shared-$Environment"

# Resource group where the ACA apps live — used for image tag auto-discovery.
$rg = "rg-pinwiz-shared-$Environment"

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

# Deployment stacks require az >= 2.61 (what-if support added then).
$azMajor = [int]($azVersion.'azure-cli'.Split('.')[0])
$azMinor = [int]($azVersion.'azure-cli'.Split('.')[1])
if ($azMajor -lt 2 -or ($azMajor -eq 2 -and $azMinor -lt 61)) {
    throw "Azure CLI >= 2.61 required for deployment stack what-if support. Installed: $($azVersion.'azure-cli'). Run: az upgrade"
}

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
# Image tag resolution — preserve current deployed images on manual runs
# -----------------------------------------------------------------------------
# If caller didn't supply -WizardImageTag / -ApiImageTag, read what's currently
# running in ACA. This prevents a manual Bicep re-deploy from reverting the app
# to the quickstart placeholder after CI/CD has pushed the real image.
# CI/CD always supplies explicit :{sha} tags; the auto-discovery only kicks in
# for operator-initiated re-deploys (e.g. infra changes that don't touch the app).

$placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'

if ([string]::IsNullOrEmpty($WizardImageTag)) {
    $discovered = az containerapp show -n "pinwiz-ca-wizard-$Environment" -g $rg `
        --query 'properties.template.containers[0].image' -o tsv 2>$null
    $WizardImageTag = if ($discovered) { $discovered } else { $placeholderImage }
    Write-Host "  wizardImageTag: $WizardImageTag (auto-discovered from running ACA app)" -ForegroundColor DarkGray
}
else {
    Write-Host "  wizardImageTag: $WizardImageTag (caller-supplied)" -ForegroundColor DarkGray
}

if ([string]::IsNullOrEmpty($ApiImageTag)) {
    $discovered = az containerapp show -n "pinwiz-ca-api-$Environment" -g $rg `
        --query 'properties.template.containers[0].image' -o tsv 2>$null
    $ApiImageTag = if ($discovered) { $discovered } else { $placeholderImage }
    Write-Host "  apiImageTag:    $ApiImageTag (auto-discovered from running ACA app)" -ForegroundColor DarkGray
}
else {
    Write-Host "  apiImageTag:    $ApiImageTag (caller-supplied)" -ForegroundColor DarkGray
}

# -----------------------------------------------------------------------------
# Deployment Stack create / update
# -----------------------------------------------------------------------------

$location = 'eastus2'

if ($WhatIf) {
    # `az stack sub create --what-if` was removed in Azure CLI 2.7x+ (the flag
    # no longer exists on the stacks command). The supported no-apply preview for
    # deployment stacks is now `az stack sub validate`, which runs the same
    # template + parameter validation Azure performs before a real create/update
    # (template compile, parameter binding, RBAC-assignment shape, resource API
    # validation) without mutating any resource. It does not render a
    # property-level resource diff the way the old subscription-level what-if did
    # — that capability is not exposed for stacks — but it is the canonical
    # pre-apply safety check and catches the failures a preview is meant to catch.
    Write-Host '[4/5] Validating the Deployment Stack (no changes will be applied)...' -ForegroundColor Cyan
    Write-Host '  Note: az stack sub has no --what-if (removed in CLI 2.7x); using `validate`.' -ForegroundColor DarkGray
    az stack sub validate `
        --name $stackName `
        --location $location `
        --template-file $templateFile `
        --parameters $parametersFile `
            wizardImageTag="$WizardImageTag" `
            apiImageTag="$ApiImageTag" `
        --action-on-unmanage deleteResources `
        --deny-settings-mode none
    if ($LASTEXITCODE -ne 0) {
        throw 'Deployment Stack validation failed.'
    }
    Write-Host ''
    Write-Host '[5/5] Validation complete. No changes applied.' -ForegroundColor Green
}
else {
    Write-Host "[4/5] Deploying via Deployment Stack '$stackName'..." -ForegroundColor Cyan
    Write-Host '  action-on-unmanage: deleteResources (orphan resources are deleted on next deploy)' -ForegroundColor DarkGray
    Write-Host '  deny-settings-mode: none (portal edits permitted)' -ForegroundColor DarkGray

    az stack sub create `
        --name $stackName `
        --location $location `
        --template-file $templateFile `
        --parameters $parametersFile `
            wizardImageTag="$WizardImageTag" `
            apiImageTag="$ApiImageTag" `
        --action-on-unmanage deleteResources `
        --deny-settings-mode none `
        --yes
    if ($LASTEXITCODE -ne 0) {
        throw 'Deployment Stack create/update failed.'
    }

    Write-Host ''
    Write-Host "[5/5] Deployment Stack '$stackName' updated successfully." -ForegroundColor Green

    # Print Bicep outputs so the operator can copy endpoints without a
    # separate az call. Stack outputs live at .properties.outputs.
    Write-Host ''
    Write-Host '  Outputs:' -ForegroundColor Cyan
    $outputsJson = az stack sub show `
        --name $stackName `
        --query 'properties.outputs' `
        -o json
    if ($LASTEXITCODE -eq 0 -and $outputsJson) {
        $outputs = $outputsJson | ConvertFrom-Json
        foreach ($prop in $outputs.PSObject.Properties) {
            $value = $prop.Value.value
            if (-not [string]::IsNullOrEmpty([string]$value)) {
                Write-Host ("    {0,-30} {1}" -f $prop.Name, $value)
            }
        }
        Write-Host ''
        Write-Host '  Smoke-test (Cosmos via Managed Identity):' -ForegroundColor Cyan
        $endpoint = $outputs.cosmosAccountEndpoint.value
        if (-not [string]::IsNullOrEmpty([string]$endpoint)) {
            Write-Host "    `$env:Cosmos__AccountEndpoint = '$endpoint'"
            Write-Host '    dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers'
        }
    }
    else {
        Write-Host '    (failed to retrieve outputs; run az stack sub show --name $stackName manually)' -ForegroundColor Yellow
    }
}
