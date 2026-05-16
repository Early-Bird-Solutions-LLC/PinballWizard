<#
.SYNOPSIS
    One-time bootstrap of the OpenTofu state backend for infra/cloudflare/.

.DESCRIPTION
    Deploys `infra/main-tfstate.bicep` to create the Azure Blob Storage account
    that holds OpenTofu state for the Cloudflare IaC stack.

    Run ONCE before the first `tofu init`. Do not re-run unless recovering from
    a complete loss of the storage account.

    Uses `az stack sub create` with `--action-on-unmanage detachResources` (not
    deleteResources) — a stack re-run will never delete the state file.

    Enforces the ADR 0010 subscription/tenant guard before deploying.

.PARAMETER GithubOidcSpObjectId
    Object ID of the GitHub Actions OIDC service principal. Used to grant
    Storage Blob Data Contributor on the tfstate container for CI runs.

    Pre-requisite: the app registration and federated credential must exist
    before running this script. See README section "GitHub Actions OIDC setup."

    Leave empty on first run to bootstrap without CI access; add the grant
    later by re-running with this parameter once the app registration is ready.

.PARAMETER WhatIf
    Run in what-if mode. Shows planned changes without applying them.

.EXAMPLE
    # Step 1 — bootstrap with developer access only (no CI yet)
    pwsh ./infra/scripts/Deploy-TfState.ps1

    # Step 2 — re-run after setting up the GitHub Actions app registration
    pwsh ./infra/scripts/Deploy-TfState.ps1 -GithubOidcSpObjectId <object-id>

.NOTES
    Requires: Azure CLI >= 2.61, Bicep CLI, pwsh 7+.
    Must be authenticated to the personal Earlybird tenant (ADR 0010).
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$GithubOidcSpObjectId = '',

    [Parameter()]
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# -----------------------------------------------------------------------------
# ADR 0010 guard — personal Earlybird subscription only
# -----------------------------------------------------------------------------

$EXPECTED_TENANT_ID       = '9793cd0f-2b27-4757-9986-1f7f1e35864a'
$EXPECTED_SUBSCRIPTION_ID = 'b1f33f17-74a9-4ecc-b46c-c4f31776b840'

$ctx = az account show | ConvertFrom-Json
if ($ctx.tenantId -ne $EXPECTED_TENANT_ID -or $ctx.id -ne $EXPECTED_SUBSCRIPTION_ID) {
    Write-Error @"
ADR 0010 guard: wrong tenant or subscription.
  Expected tenant:       $EXPECTED_TENANT_ID
  Expected subscription: $EXPECTED_SUBSCRIPTION_ID
  Active tenant:         $($ctx.tenantId)
  Active subscription:   $($ctx.id)

Run `az login` with your personal Earlybird account, then:
  az account set --subscription $EXPECTED_SUBSCRIPTION_ID
"@
    exit 1
}

Write-Host "Guard passed: deploying to personal Earlybird subscription." -ForegroundColor Green

# -----------------------------------------------------------------------------
# Resolve developer object ID
# -----------------------------------------------------------------------------

$developerObjectId = az ad signed-in-user show --query id -o tsv
Write-Host "Developer object ID: $developerObjectId"

# -----------------------------------------------------------------------------
# Paths
# -----------------------------------------------------------------------------

$scriptDir    = $PSScriptRoot
$infraDir     = Split-Path -Parent $scriptDir
$templateFile = Join-Path $infraDir 'main-tfstate.bicep'

# -----------------------------------------------------------------------------
# Build parameter object
# -----------------------------------------------------------------------------

$params = @(
    "developerObjectId=$developerObjectId"
)
if ($GithubOidcSpObjectId) {
    $params += "githubOidcSpObjectId=$GithubOidcSpObjectId"
}

$paramArgs = $params | ForEach-Object { "--parameters"; $_ }

# -----------------------------------------------------------------------------
# Deploy (or what-if)
# -----------------------------------------------------------------------------

$stackName = 'pinwiz-tfstate'

if ($WhatIf) {
    Write-Host "`nRunning what-if (no changes applied)..." -ForegroundColor Cyan
    az stack sub create `
        --name $stackName `
        --location eastus2 `
        --template-file $templateFile `
        @paramArgs `
        --action-on-unmanage detachResources `
        --deny-settings-mode none `
        --what-if
} else {
    Write-Host "`nDeploying tfstate backend stack '$stackName'..." -ForegroundColor Cyan
    az stack sub create `
        --name $stackName `
        --location eastus2 `
        --template-file $templateFile `
        @paramArgs `
        --action-on-unmanage detachResources `
        --deny-settings-mode none `
        --yes

    Write-Host "`nDone. Storage account: stpinballtfstate" -ForegroundColor Green
    Write-Host "Container:             tfstate"
    Write-Host "Next step:             cd infra/cloudflare && tofu init"
}
