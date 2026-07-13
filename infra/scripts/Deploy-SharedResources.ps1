<#
.SYNOPSIS
    Deploys the pinwiz.ai shared-tier Azure resources via an Azure Deployment Stack.

.DESCRIPTION
    Orchestrates the deployment of `infra/main-shared.bicep` to the personal
    Earlybird Azure subscription using an Azure Deployment Stack. Deployment
    Stacks track every resource in the template and automatically delete orphaned
    resources when they are removed from Bicep — preventing the silent drift that
    plain `az deployment` creates.

    After the stack create/update succeeds, the script automatically runs
    `--ensure-cosmos-containers` against the just-deployed Cosmos account using
    the captured cosmosAccountEndpoint + cosmosAccountResourceId stack outputs.
    This prevents the class of live incident where a new container added to
    CosmosOptions.Containers (code) is never created in the deployed environment
    because no operator ran the CLI manually — the root cause of the
    catalog_stats + catalog_stats_leases outage that crash-looped the RAG worker
    and emptied /admin/machines (2026-06-15). The deployer identity running this
    script has control-plane RBAC (ARM SDK path via ArmCosmosProvisioner); the
    runtime managed identity deliberately does NOT (per ADR-0012). Use
    -SkipEnsureContainers to bypass this step when testing infra-only changes.

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

.PARAMETER SkipEnsureContainers
    Skip the post-deploy `--ensure-cosmos-containers` step. By default the
    script runs the CLI against the just-deployed Cosmos account after every
    successful stack create/update so that new containers added to
    CosmosOptions.Containers are created automatically. Pass this flag when
    you are doing a WhatIf-style infra-only check or when you know the
    container set has not changed and want a faster turnaround.

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

    [Parameter()]
    [switch]$SkipEnsureContainers,

    # Deploy even though the resolved parameters leave developerObjectId empty,
    # which STRIPS the developer's data-plane role assignments (see the
    # developerObjectId guard below). Only pass this when that is genuinely what
    # you want — e.g. a service-principal deploy that grants no human any roles.
    [Parameter()]
    [switch]$AllowNoDeveloperRbac,

    # Image tags for the Wizard web app and Api. If not supplied, the script
    # reads the currently-deployed image from the running ACA app so a manual
    # Bicep re-deploy does not revert the image to the placeholder.
    # The CI/CD deploy workflow always supplies explicit :{sha} tags — never :latest.
    [Parameter()]
    [string]$WizardImageTag = '',

    [Parameter()]
    [string]$ApiImageTag = '',

    [Parameter()]
    [string]$RagIndexerImageTag = '',

    # CLI image tag powering the linker + OPDB sync ACA Jobs. If not supplied,
    # the script reads the image off the currently-deployed linker job so a
    # manual Bicep re-deploy does not revert the jobs to the placeholder.
    [Parameter()]
    [string]$CliImageTag = ''
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
# developerObjectId guard (#744)
# -----------------------------------------------------------------------------
# shared.bicep grants the developer data-plane roles (Cosmos Data Contributor,
# Search Index Data Contributor, Cognitive Services OpenAI User) gated on
# `!empty(developerObjectId)`. The COMMITTED main-shared.<env>.bicepparam sets it
# to '' — deliberately, because this repo is PUBLIC and a personal AAD object id
# does not belong in it. The real value lives only in the gitignored
# main-shared.<env>.local.bicepparam.
#
# The failure mode that keeps recurring: deploy from a tree WITHOUT that local
# file, the stack resolves developerObjectId='', and — because the stack owns
# those assignments and runs deleteResources — it silently DELETES them. Nothing
# fails; the roles are simply gone, and the next local CLI run against live dies
# with an opaque 403 that reads like an outage. It has happened at least twice.
#
# So refuse. A destructive default must be an explicit choice, not a silent one
# (invariant #17: degrade visibly, never silently).
$developerObjectId = ''
$paramMatch = Select-String -Path $parametersFile -Pattern "^\s*param\s+developerObjectId\s*=\s*'([^']*)'" |
    Select-Object -First 1
if ($paramMatch) {
    $developerObjectId = $paramMatch.Matches[0].Groups[1].Value
}

if ([string]::IsNullOrWhiteSpace($developerObjectId)) {
    if ($AllowNoDeveloperRbac) {
        Write-Host '[!] developerObjectId is EMPTY and -AllowNoDeveloperRbac was passed.' -ForegroundColor Yellow
        Write-Host '    This deploy will REMOVE the developer data-plane role assignments.' -ForegroundColor Yellow
    }
    else {
        Write-Error @"
developerObjectId is empty in: $parametersFile

Deploying now would STRIP the developer data-plane role assignments (Cosmos Data
Contributor, Search Index Data Contributor, Cognitive Services OpenAI User) — the
stack owns them, and with an empty developerObjectId it deletes them. Local CLI
runs against live would then fail with an opaque 403. See issue #744.

Fix — create the gitignored local override with your object id:

  cp infra/main-shared.$Environment.bicepparam infra/main-shared.$Environment.local.bicepparam
  # then set, in that new file:
  #   param developerObjectId = '<your az ad signed-in-user object id>'
  # get it with:  az ad signed-in-user show --query id -o tsv

Verify afterwards with:  pwsh ./infra/scripts/Check-DeveloperRbac.ps1

If you genuinely intend a deploy that grants no human these roles (e.g. a
service-principal-only deploy), re-run with -AllowNoDeveloperRbac.
"@
        exit 1
    }
}
else {
    Write-Host "  developerObjectId present ($($developerObjectId.Substring(0,8))…) — stack will own the developer data-plane roles." -ForegroundColor DarkGray
}

# -----------------------------------------------------------------------------
# Tooling check
# -----------------------------------------------------------------------------

Write-Host '[1/6] Checking tooling...' -ForegroundColor Cyan

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

Write-Host '[2/6] Verifying az context against the personal Earlybird tenant...' -ForegroundColor Cyan

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

Write-Host '[3/6] Building Bicep template (syntax check only)...' -ForegroundColor Cyan
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

if ([string]::IsNullOrEmpty($RagIndexerImageTag)) {
    $discovered = az containerapp show -n "pinwiz-ca-ragindexer-$Environment" -g $rg `
        --query 'properties.template.containers[0].image' -o tsv 2>$null
    $RagIndexerImageTag = if ($discovered) { $discovered } else { $placeholderImage }
    Write-Host "  ragIndexerImageTag: $RagIndexerImageTag (auto-discovered from running ACA app)" -ForegroundColor DarkGray
}
else {
    Write-Host "  ragIndexerImageTag: $RagIndexerImageTag (caller-supplied)" -ForegroundColor DarkGray
}

# The CLI image runs on ACA Jobs (linker + OPDB sync), not an ACA App, so
# auto-discovery reads from the linker job. The job name carries a uniqueString
# suffix (pinwiz-job-linker-<5char>), so match by prefix rather than exact name.
# Both jobs share the same cliImageTag, so the linker job is a sufficient probe.
#
# IMPORTANT: after a fresh bootstrap all jobs show the placeholder image.
# In that case we fall back to querying ACR for the most recent SHA tag — the
# placeholder must NEVER reach Bicep as $CliImageTag or ARM will attempt to pull
# it from Docker Hub and fail with UNAUTHORIZED.
if ([string]::IsNullOrEmpty($CliImageTag)) {
    $discovered = az containerapp job list -g $rg `
        --query "[?starts_with(name, 'pinwiz-job-linker')].template.containers[0].image | [0]" -o tsv 2>$null

    if ($discovered -and $discovered -ne $placeholderImage) {
        $CliImageTag = $discovered
        Write-Host "  cliImageTag:        $CliImageTag (auto-discovered from running linker ACA Job)" -ForegroundColor DarkGray
    }
    else {
        # Job is on the placeholder — query ACR for the latest real SHA tag.
        Write-Host "  cliImageTag:        Linker job is on the placeholder; querying ACR for latest tag..." -ForegroundColor Yellow
        $acrName = az acr list -g $rg --query '[0].name' -o tsv 2>$null
        if ($acrName) {
            $latestTag = az acr repository show-tags -n $acrName --repository 'pinwiz-cli' `
                --orderby time_desc --top 5 -o tsv 2>$null |
                Where-Object { $_ -ne 'latest' } |
                Select-Object -First 1
            if ($latestTag) {
                $CliImageTag = "$acrName.azurecr.io/pinwiz-cli:$latestTag"
                Write-Host "  cliImageTag:        $CliImageTag (ACR fallback — most recent SHA tag)" -ForegroundColor Yellow
            }
            else {
                Write-Error "Could not resolve CLI image from ACR '$acrName'. Pass -CliImageTag explicitly:"
                Write-Error "  Deploy-SharedResources.ps1 -Environment $Environment -CliImageTag '<registry>/<repo>:<sha>'"
                throw 'Cannot determine cliImageTag.'
            }
        }
        else {
            Write-Error "Could not find an ACR in resource group '$rg'. Pass -CliImageTag explicitly:"
            Write-Error "  Deploy-SharedResources.ps1 -Environment $Environment -CliImageTag '<registry>/<repo>:<sha>'"
            throw 'Cannot determine cliImageTag.'
        }
    }
}
else {
    Write-Host "  cliImageTag:        $CliImageTag (caller-supplied)" -ForegroundColor DarkGray
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
    Write-Host '[4/6] Validating the Deployment Stack (no changes will be applied)...' -ForegroundColor Cyan
    Write-Host '  Note: az stack sub has no --what-if (removed in CLI 2.7x); using `validate`.' -ForegroundColor DarkGray
    az stack sub validate `
        --name $stackName `
        --location $location `
        --template-file $templateFile `
        --parameters $parametersFile `
            wizardImageTag="$WizardImageTag" `
            apiImageTag="$ApiImageTag" `
            ragIndexerImageTag="$RagIndexerImageTag" `
            cliImageTag="$CliImageTag" `
        --action-on-unmanage deleteResources `
        --deny-settings-mode none
    if ($LASTEXITCODE -ne 0) {
        throw 'Deployment Stack validation failed.'
    }
    Write-Host ''
    Write-Host '[5/6] Validation complete. No changes applied.' -ForegroundColor Green
    Write-Host '  [6/6] --ensure-cosmos-containers skipped under -WhatIf (no mutation).' -ForegroundColor DarkGray
}
else {
    Write-Host "[4/6] Deploying via Deployment Stack '$stackName'..." -ForegroundColor Cyan
    Write-Host '  action-on-unmanage: deleteResources (orphan resources are deleted on next deploy)' -ForegroundColor DarkGray
    Write-Host '  deny-settings-mode: none (portal edits permitted)' -ForegroundColor DarkGray

    az stack sub create `
        --name $stackName `
        --location $location `
        --template-file $templateFile `
        --parameters $parametersFile `
            wizardImageTag="$WizardImageTag" `
            apiImageTag="$ApiImageTag" `
            ragIndexerImageTag="$RagIndexerImageTag" `
            cliImageTag="$CliImageTag" `
        --action-on-unmanage deleteResources `
        --deny-settings-mode none `
        --yes
    if ($LASTEXITCODE -ne 0) {
        throw 'Deployment Stack create/update failed.'
    }

    Write-Host ''
    Write-Host "[5/6] Deployment Stack '$stackName' updated successfully." -ForegroundColor Green

    # Post-deploy RBAC assertion (#744). The guard above stops a deploy that WOULD strip
    # the developer roles; this confirms the deploy actually LEFT them in place. Without
    # it, a strip stays invisible until someone's next local-live CLI run dies on a 403
    # that looks like an outage — which is exactly how this was found, twice.
    if (-not [string]::IsNullOrWhiteSpace($developerObjectId)) {
        Write-Host ''
        Write-Host '  Verifying developer data-plane RBAC survived the deploy...' -ForegroundColor Cyan
        $rbacCheck = Join-Path $PSScriptRoot 'Check-DeveloperRbac.ps1'
        # Check-DeveloperRbac.ps1 takes no -Environment: its resource-name defaults are the
        # dev account names (the 'buutj' suffix is not derivable from the env name). So only
        # auto-verify for dev; a prod stack would need its own account names passed in.
        if ((Test-Path $rbacCheck) -and $Environment -eq 'dev') {
            & $rbacCheck -DeveloperObjectId $developerObjectId
            if ($LASTEXITCODE -ne 0) {
                Write-Warning @"
The deploy succeeded but the developer data-plane role assignments are NOT all present.
Local CLI runs against live will fail with a 403 until this is fixed. See issue #744.
Re-grant with:  pwsh ./infra/scripts/Check-DeveloperRbac.ps1 -Fix
"@
            }
        }
        elseif ($Environment -ne 'dev') {
            Write-Host "  (RBAC verification is dev-only — Check-DeveloperRbac.ps1's account defaults are dev names.)" -ForegroundColor DarkGray
        }
        else {
            Write-Host '  (Check-DeveloperRbac.ps1 not found — skipping RBAC verification.)' -ForegroundColor DarkGray
        }
    }

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
    }
    else {
        Write-Host '    (failed to retrieve outputs; run az stack sub show --name $stackName manually)' -ForegroundColor Yellow
    }

    # -------------------------------------------------------------------------
    # [6/6] Ensure Cosmos containers exist after every successful deploy.
    #
    # WHY HERE: CosmosOptions.Containers is the canonical list of containers
    # the runtime expects to exist. New containers are added to that list in
    # code, but the deployed Cosmos account is not updated until an operator
    # manually runs --ensure-cosmos-containers. This gap caused a live outage
    # (2026-06-15): catalog_stats + catalog_stats_leases were never created
    # after the PR that introduced them (#410), crash-looping the RAG worker
    # and emptying /admin/machines.
    #
    # WHY OPTION A (dotnet run, not an ACA Job): the deploy identity running
    # this script has Subscription Owner → Cosmos DB Operator (control-plane
    # RBAC), which is exactly what ArmCosmosProvisioner requires. The runtime
    # managed identity deliberately does NOT have control-plane RBAC per
    # ADR-0012 — so an ACA Job approach would need a second identity with
    # elevated permissions that ADR-0012 explicitly excludes from app
    # identities. dotnet run on the deploy machine is the clean path.
    #
    # IDEMPOTENCY: ArmCosmosProvisioner.EnsureCreatedAsync calls
    # CreateOrUpdateAsync for each container in CosmosOptions.Containers.
    # Containers that already exist with a matching partition key are no-ops.
    # Containers with a partition-key mismatch fail loudly (fatal drift).
    #
    # FAILURE SURFACE: any non-zero exit from the CLI is caught and re-thrown
    # so it surfaces as a deploy failure with a clear error message — not
    # silently swallowed. The deployer sees exactly which step failed.
    # -------------------------------------------------------------------------

    Write-Host ''
    Write-Host '[6/6] Ensuring Cosmos containers match CosmosOptions.Containers...' -ForegroundColor Cyan

    if ($SkipEnsureContainers) {
        Write-Host '  -SkipEnsureContainers was specified — skipping.' -ForegroundColor Yellow
    }
    else {
        $cosmosEndpoint   = $null
        $cosmosResourceId = $null

        if ($null -ne $outputs) {
            # $outputs was already parsed above from the az stack sub show call; re-use it.
            $cosmosEndpoint   = $outputs.cosmosAccountEndpoint.value
            $cosmosResourceId = $outputs.cosmosAccountResourceId.value
        }

        if ([string]::IsNullOrEmpty([string]$cosmosEndpoint) -or
            [string]::IsNullOrEmpty([string]$cosmosResourceId)) {
            Write-Host '  WARNING: cosmosAccountEndpoint or cosmosAccountResourceId not found in stack' `
                -ForegroundColor Yellow
            Write-Host '    outputs. Run manually when the outputs are available:' -ForegroundColor Yellow
            Write-Host '      $env:Cosmos__AccountEndpoint    = "<endpoint>"' -ForegroundColor Yellow
            Write-Host '      $env:Cosmos__AccountResourceId  = "<resourceId>"' -ForegroundColor Yellow
            Write-Host '      dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers' `
                -ForegroundColor Yellow
        }
        else {
            Write-Host "  Endpoint:    $cosmosEndpoint" -ForegroundColor DarkGray
            Write-Host "  ResourceId:  $cosmosResourceId" -ForegroundColor DarkGray

            # Resolve the repo root two levels above infra/scripts/ so dotnet
            # run works regardless of the caller's working directory.
            $repoRoot  = Split-Path -Parent $infraDir
            $cliProject = Join-Path $repoRoot 'src' 'PinballWizard.Cli' 'PinballWizard.Cli.csproj'

            $env:Cosmos__AccountEndpoint   = $cosmosEndpoint
            $env:Cosmos__AccountResourceId = $cosmosResourceId
            $env:DOTNET_ENVIRONMENT        = $Environment

            try {
                dotnet run --project $cliProject --no-launch-profile -- --ensure-cosmos-containers
                if ($LASTEXITCODE -ne 0) {
                    throw "--ensure-cosmos-containers exited with code $LASTEXITCODE."
                }
                Write-Host '  Cosmos containers: OK' -ForegroundColor Green
            }
            finally {
                # Always clear the env vars — don't leave Cosmos credentials
                # in the session after the script exits, regardless of outcome.
                Remove-Item Env:Cosmos__AccountEndpoint   -ErrorAction SilentlyContinue
                Remove-Item Env:Cosmos__AccountResourceId -ErrorAction SilentlyContinue
                Remove-Item Env:DOTNET_ENVIRONMENT        -ErrorAction SilentlyContinue
            }
        }
    }
}
