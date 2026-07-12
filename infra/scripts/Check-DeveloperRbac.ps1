<#
.SYNOPSIS
    Verifies (and optionally restores) the three developer data-plane RBAC role
    assignments required for local-live CLI runs against the pinwiz.ai dev
    environment.

.DESCRIPTION
    Local CLI operations against live Azure (e.g. --sync-tiltforums-rulesheets,
    --run-rag-backfill) authenticate via DefaultAzureCredential/AzureCliCredential.
    They require three data-plane role assignments that are stripped whenever a
    Deployment Stack runs without developerObjectId set. The committed
    main-shared.dev.bicepparam leaves developerObjectId='' for privacy, so any
    stack deploy from a fresh clone silently strips these grants — and the next
    local-live run 403s on the first Cosmos / AI Search / Foundry call.

    The three required grants:

      1. Cosmos DB Built-in Data Contributor (00000000-…-0002)
         — item CRUD + query on all containers (Microsoft.DocumentDB data-plane)
      2. Search Index Data Contributor (8ebe5a00-…)
         — index upserts for the RAG corpus (Azure RBAC)
      3. Cognitive Services OpenAI User (5e0bd9bd-…) on the Foundry account
         — chat + embedding inference (Azure RBAC)

    Run this script BEFORE starting a local-live session. See
    docs/local-development.md § Prerequisites.

    When a role is missing:
      - Without -Fix: prints the exact `az ...` command to restore it (copy-paste).
      - With -Fix:    runs the `az ... create` command directly (human-run; safe
                      because the hook only blocks agent Bash invocations).

    Exits 0 if all three roles are present (or were just fixed), non-zero if any
    are missing (so it can gate a shell profile check before a live-run session).

    DURABLE FIX: the ad-hoc grants created by this script will drift again on
    the next stack deploy. For a permanent fix, set developerObjectId in the
    gitignored main-shared.dev.local.bicepparam and run Deploy-SharedResources.ps1
    so the stack owns the assignments and re-creates them on every deploy. Delete
    the ad-hoc grants first (RoleAssignmentExists conflict) — see the "Durable fix"
    section at the end of this script's output.

.PARAMETER ResourceGroup
    Resource group containing all three resources.
    Default: rg-pinwiz-shared-dev

.PARAMETER CosmosAccount
    Cosmos DB account name.
    Default: pinwiz-cosmos-dev-buutj

.PARAMETER SearchService
    Azure AI Search service name.
    Default: pinwiz-search-dev-buutj

.PARAMETER FoundryAccount
    Azure AI Foundry (CognitiveServices/AIServices) account name.
    Default: pinwiz-foundry-dev-buutj

.PARAMETER DeveloperObjectId
    Entra Object ID of the developer principal to check. If omitted, resolved
    from the active az CLI session via `az ad signed-in-user show --query id`.

.PARAMETER Fix
    When specified, runs `az ... role assignment create` for each missing role.
    Without this flag the script is read-only — it only reports missing roles and
    prints the commands needed to restore them.

.PARAMETER SkipGuard
    Skip the ADR 0010 subscription/tenant guard. For script testing only — never
    use in normal operation. Carries an unmissable warning.

.EXAMPLE
    # Check only (no writes) — run before every local-live session
    pwsh ./infra/scripts/Check-DeveloperRbac.ps1

.EXAMPLE
    # Check and restore any missing assignments in one step
    pwsh ./infra/scripts/Check-DeveloperRbac.ps1 -Fix

.EXAMPLE
    # Check for a specific principal without fixing
    pwsh ./infra/scripts/Check-DeveloperRbac.ps1 -DeveloperObjectId 'fb4fdb3e-...'

.NOTES
    Requires: Azure CLI >= 2.61, pwsh 7+.
    Must be authenticated to the personal Earlybird tenant (ADR 0010).

    See also: infra/main-shared.dev.bicepparam (developerObjectId param),
              infra/modules/shared.bicep lines ~1238–1340 (Developer RBAC section),
              docs/local-development.md § Prerequisites (preflight note),
              GitHub issue #744 (root-cause analysis and durable fix plan).
#>

# Note: NOT using SupportsShouldProcess=$true — it would reserve -WhatIf as a
# PowerShell common parameter, conflicting with our explicit [switch]$WhatIf in
# sibling scripts (Deploy-SharedResources.ps1 documents the same reasoning).
[CmdletBinding()]
param(
    [Parameter()]
    [string]$ResourceGroup = 'rg-pinwiz-shared-dev',

    [Parameter()]
    [string]$CosmosAccount = 'pinwiz-cosmos-dev-buutj',

    [Parameter()]
    [string]$SearchService = 'pinwiz-search-dev-buutj',

    [Parameter()]
    [string]$FoundryAccount = 'pinwiz-foundry-dev-buutj',

    [Parameter()]
    [string]$DeveloperObjectId = '',

    [Parameter()]
    [switch]$Fix,

    [Parameter()]
    [switch]$SkipGuard
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# Stable role definition IDs — these are identical across all Azure subscriptions.
# Cosmos data-plane uses a separate RBAC namespace (sqlRoleAssignments under the
# database account resource), not Microsoft.Authorization. The well-known
# 'Cosmos DB Built-in Data Contributor' definition ID is fixed at …0002.
$COSMOS_ROLE_DEF_ID  = '00000000-0000-0000-0000-000000000002'   # Cosmos DB Built-in Data Contributor
$SEARCH_ROLE_DEF_ID  = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'   # Search Index Data Contributor
$FOUNDRY_ROLE_DEF_ID = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'   # Cognitive Services OpenAI User

# Per ADR 0010: this repo deploys only to the personal Earlybird tenant + subscription.
$EXPECTED_TENANT_ID       = '9793cd0f-2b27-4757-9986-1f7f1e35864a'
$EXPECTED_SUBSCRIPTION_ID = 'b1f33f17-74a9-4ecc-b46c-c4f31776b840'

# Status tokens used throughout — kept as string constants for clarity.
$STATUS_PASS    = 'PASS'
$STATUS_MISSING = 'MISSING'
$STATUS_FIXED   = 'FIXED'

# -----------------------------------------------------------------------------
# [1/4] ADR 0010 guard — personal Earlybird subscription only
# -----------------------------------------------------------------------------

Write-Host '[1/4] Verifying az context...' -ForegroundColor Cyan

$ctx = az account show --output json 2>$null | ConvertFrom-Json
if (-not $ctx) {
    throw 'Not logged in to Azure. Run: az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a'
}

$guardOk = ($ctx.tenantId -eq $EXPECTED_TENANT_ID) -and ($ctx.id -eq $EXPECTED_SUBSCRIPTION_ID)

if (-not $guardOk) {
    Write-Host ''
    Write-Host '  +-----------------------------------------------------------+' -ForegroundColor Red
    Write-Host '  |  SUBSCRIPTION GUARD TRIPPED (ADR 0010)                    |' -ForegroundColor Red
    Write-Host '  |  This repo targets the personal Earlybird tenant only.    |' -ForegroundColor Red
    Write-Host '  +-----------------------------------------------------------+' -ForegroundColor Red
    Write-Host ''
    Write-Host "  Expected tenant:       $EXPECTED_TENANT_ID" -ForegroundColor Red
    Write-Host "  Active  tenant:        $($ctx.tenantId) ($($ctx.tenantDefaultDomain))" -ForegroundColor Red
    Write-Host "  Expected subscription: $EXPECTED_SUBSCRIPTION_ID" -ForegroundColor Red
    Write-Host "  Active  subscription:  $($ctx.id) ($($ctx.name))" -ForegroundColor Red
    Write-Host ''
    Write-Host '  To fix:' -ForegroundColor Yellow
    Write-Host "    az login --tenant $EXPECTED_TENANT_ID" -ForegroundColor Yellow
    Write-Host "    az account set --subscription $EXPECTED_SUBSCRIPTION_ID" -ForegroundColor Yellow
    Write-Host ''

    if ($SkipGuard) {
        Write-Host '  -SkipGuard was specified. PROCEEDING ANYWAY.' -ForegroundColor Yellow
        Write-Host '  This is a script-development override. Do not use in normal ops.' -ForegroundColor Yellow
        Write-Host ''
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
# [2/4] Resolve developer object ID and build resource scopes
# -----------------------------------------------------------------------------

Write-Host ''
Write-Host '[2/4] Resolving developer object ID...' -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($DeveloperObjectId)) {
    $DeveloperObjectId = (az ad signed-in-user show --query id -o tsv 2>$null).Trim()
    if ([string]::IsNullOrWhiteSpace($DeveloperObjectId)) {
        throw 'Could not resolve developer object ID from the az CLI session. Pass -DeveloperObjectId explicitly.'
    }
    Write-Host "  Resolved from az CLI signed-in user: $DeveloperObjectId" -ForegroundColor DarkGray
}
else {
    Write-Host "  Using caller-supplied object ID: $DeveloperObjectId" -ForegroundColor DarkGray
}

$subscriptionId = $ctx.id

# Full resource IDs used as scopes for Azure RBAC queries and grants.
$cosmosResourceId = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.DocumentDB/databaseAccounts/$CosmosAccount"
$searchScope      = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Search/searchServices/$SearchService"
$foundryScope     = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$FoundryAccount"

Write-Host "  Cosmos:  $cosmosResourceId" -ForegroundColor DarkGray
Write-Host "  Search:  $searchScope"      -ForegroundColor DarkGray
Write-Host "  Foundry: $foundryScope"     -ForegroundColor DarkGray

# Track per-role status; updated as we check (and optionally fix) each one.
$cosmosStatus  = $STATUS_MISSING
$searchStatus  = $STATUS_MISSING
$foundryStatus = $STATUS_MISSING

# Accumulates fix commands for the copy-paste output when -Fix is not set.
$fixCommands = [System.Collections.Generic.List[hashtable]]::new()

# -----------------------------------------------------------------------------
# [3/4] Check (and optionally fix) each role assignment
# -----------------------------------------------------------------------------

Write-Host ''
Write-Host '[3/4] Checking role assignments...' -ForegroundColor Cyan

# --- Cosmos DB Built-in Data Contributor ---
# Uses the Cosmos data-plane RBAC namespace (az cosmosdb sql role assignment),
# not Azure RBAC (az role assignment). The role definition lives under the
# database account resource, not under Microsoft.Authorization.

Write-Host ''
Write-Host "  [Cosmos] Built-in Data Contributor ($COSMOS_ROLE_DEF_ID)..." -ForegroundColor DarkCyan

$cosmosListJson = az cosmosdb sql role assignment list `
    --account-name $CosmosAccount `
    --resource-group $ResourceGroup `
    --output json 2>$null

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($cosmosListJson)) {
    Write-Host '    WARNING: Could not list Cosmos sql role assignments.' -ForegroundColor Yellow
    Write-Host '    Verify the Cosmos account exists and your az context is correct.' -ForegroundColor Yellow
    $cosmosStatus = $STATUS_MISSING
}
else {
    $cosmosAssignments = $cosmosListJson | ConvertFrom-Json
    $cosmosMatch = $cosmosAssignments | Where-Object {
        $_.properties.principalId -eq $DeveloperObjectId -and
        $_.properties.roleDefinitionId -like "*$COSMOS_ROLE_DEF_ID"
    }

    if ($cosmosMatch) {
        Write-Host "    $STATUS_PASS" -ForegroundColor Green
        $cosmosStatus = $STATUS_PASS
    }
    else {
        Write-Host "    $STATUS_MISSING" -ForegroundColor Red

        $fixCmd = @"
az cosmosdb sql role assignment create ``
    --account-name $CosmosAccount ``
    --resource-group $ResourceGroup ``
    --role-definition-id $COSMOS_ROLE_DEF_ID ``
    --principal-id $DeveloperObjectId ``
    --scope $cosmosResourceId
"@
        $fixCommands.Add(@{ Label = 'Cosmos DB Built-in Data Contributor'; Command = $fixCmd })

        if ($Fix) {
            Write-Host '    Restoring via az cosmosdb sql role assignment create...' -ForegroundColor Yellow
            az cosmosdb sql role assignment create `
                --account-name $CosmosAccount `
                --resource-group $ResourceGroup `
                --role-definition-id $COSMOS_ROLE_DEF_ID `
                --principal-id $DeveloperObjectId `
                --scope $cosmosResourceId `
                --output none
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to create Cosmos role assignment (exit $LASTEXITCODE). See az error above."
            }
            Write-Host "    $STATUS_FIXED" -ForegroundColor Green
            $cosmosStatus = $STATUS_FIXED
        }
    }
}

# --- Search Index Data Contributor ---
# Standard Azure RBAC (Microsoft.Authorization). --include-inherited ensures
# we detect the assignment even if it was granted at resource-group scope.

Write-Host ''
Write-Host "  [Search] Index Data Contributor ($SEARCH_ROLE_DEF_ID)..." -ForegroundColor DarkCyan

$searchListJson = az role assignment list `
    --assignee $DeveloperObjectId `
    --scope $searchScope `
    --include-inherited `
    --output json 2>$null

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($searchListJson)) {
    Write-Host '    WARNING: Could not list Search role assignments.' -ForegroundColor Yellow
    Write-Host '    Verify the Search service exists and your az context is correct.' -ForegroundColor Yellow
    $searchStatus = $STATUS_MISSING
}
else {
    $searchAssignments = $searchListJson | ConvertFrom-Json
    $searchMatch = $searchAssignments | Where-Object {
        $_.roleDefinitionId -like "*$SEARCH_ROLE_DEF_ID"
    }

    if ($searchMatch) {
        Write-Host "    $STATUS_PASS" -ForegroundColor Green
        $searchStatus = $STATUS_PASS
    }
    else {
        Write-Host "    $STATUS_MISSING" -ForegroundColor Red

        $fixCmd = @"
az role assignment create ``
    --assignee $DeveloperObjectId ``
    --role $SEARCH_ROLE_DEF_ID ``
    --scope $searchScope
"@
        $fixCommands.Add(@{ Label = 'Search Index Data Contributor'; Command = $fixCmd })

        if ($Fix) {
            Write-Host '    Restoring via az role assignment create...' -ForegroundColor Yellow
            az role assignment create `
                --assignee $DeveloperObjectId `
                --role $SEARCH_ROLE_DEF_ID `
                --scope $searchScope `
                --output none
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to create Search role assignment (exit $LASTEXITCODE). See az error above."
            }
            Write-Host "    $STATUS_FIXED" -ForegroundColor Green
            $searchStatus = $STATUS_FIXED
        }
    }
}

# --- Cognitive Services OpenAI User on Foundry ---
# Standard Azure RBAC. The Foundry resource is a CognitiveServices/accounts of
# kind=AIServices (per shared.bicep line ~638). The role grants access to
# chat + embedding inference via the project endpoint.

Write-Host ''
Write-Host "  [Foundry] Cognitive Services OpenAI User ($FOUNDRY_ROLE_DEF_ID)..." -ForegroundColor DarkCyan

$foundryListJson = az role assignment list `
    --assignee $DeveloperObjectId `
    --scope $foundryScope `
    --include-inherited `
    --output json 2>$null

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($foundryListJson)) {
    Write-Host '    WARNING: Could not list Foundry role assignments.' -ForegroundColor Yellow
    Write-Host '    Verify the Foundry account exists and your az context is correct.' -ForegroundColor Yellow
    $foundryStatus = $STATUS_MISSING
}
else {
    $foundryAssignments = $foundryListJson | ConvertFrom-Json
    $foundryMatch = $foundryAssignments | Where-Object {
        $_.roleDefinitionId -like "*$FOUNDRY_ROLE_DEF_ID"
    }

    if ($foundryMatch) {
        Write-Host "    $STATUS_PASS" -ForegroundColor Green
        $foundryStatus = $STATUS_PASS
    }
    else {
        Write-Host "    $STATUS_MISSING" -ForegroundColor Red

        $fixCmd = @"
az role assignment create ``
    --assignee $DeveloperObjectId ``
    --role $FOUNDRY_ROLE_DEF_ID ``
    --scope $foundryScope
"@
        $fixCommands.Add(@{ Label = 'Cognitive Services OpenAI User (Foundry)'; Command = $fixCmd })

        if ($Fix) {
            Write-Host '    Restoring via az role assignment create...' -ForegroundColor Yellow
            az role assignment create `
                --assignee $DeveloperObjectId `
                --role $FOUNDRY_ROLE_DEF_ID `
                --scope $foundryScope `
                --output none
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to create Foundry role assignment (exit $LASTEXITCODE). See az error above."
            }
            Write-Host "    $STATUS_FIXED" -ForegroundColor Green
            $foundryStatus = $STATUS_FIXED
        }
    }
}

# -----------------------------------------------------------------------------
# [4/4] Summary
# -----------------------------------------------------------------------------

Write-Host ''
Write-Host '[4/4] Summary' -ForegroundColor Cyan
Write-Host ''

# Build rows for the summary table.
$rows = @(
    @('Cosmos DB',  'Built-in Data Contributor',       $cosmosStatus)
    @('AI Search',  'Search Index Data Contributor',   $searchStatus)
    @('AI Foundry', 'Cognitive Services OpenAI User',  $foundryStatus)
)

# Compute column widths from content.
$w0 = ($rows | ForEach-Object { $_[0].Length } | Measure-Object -Maximum).Maximum
$w1 = ($rows | ForEach-Object { $_[1].Length } | Measure-Object -Maximum).Maximum

$header = "  {0,-$w0}  {1,-$w1}  {2}" -f 'Resource', 'Role', 'Status'
$sep    = "  {0}  {1}  {2}" -f ('-' * $w0), ('-' * $w1), '------'

Write-Host $header -ForegroundColor White
Write-Host $sep    -ForegroundColor DarkGray

foreach ($row in $rows) {
    $color = switch ($row[2]) {
        $STATUS_PASS    { 'Green'  }
        $STATUS_FIXED   { 'Cyan'   }
        default         { 'Red'    }
    }
    Write-Host ("  {0,-$w0}  {1,-$w1}  {2}" -f $row[0], $row[1], $row[2]) -ForegroundColor $color
}

Write-Host ''

# Determine overall outcome.
$stillMissing = $rows | Where-Object { $_[2] -eq $STATUS_MISSING }

if (-not $stillMissing) {
    Write-Host '  All developer data-plane role assignments are present.' -ForegroundColor Green
    Write-Host '  You are cleared for local-live CLI operations.' -ForegroundColor Green

    if ($Fix -and ($cosmosStatus -eq $STATUS_FIXED -or $searchStatus -eq $STATUS_FIXED -or $foundryStatus -eq $STATUS_FIXED)) {
        Write-Host ''
        Write-Host '  NOTE: These ad-hoc grants will be stripped the next time Deploy-SharedResources.ps1' -ForegroundColor DarkGray
        Write-Host '  runs without developerObjectId set. For the durable fix, see below.' -ForegroundColor DarkGray
        Write-Host ''
        Write-Host '  DURABLE FIX (stack-owned assignments that survive every deploy):' -ForegroundColor Yellow
        Write-Host '    1. Add developerObjectId to the gitignored local override:' -ForegroundColor White
        Write-Host '         cp infra/main-shared.dev.bicepparam infra/main-shared.dev.local.bicepparam' -ForegroundColor DarkGray
        Write-Host "         # Then set: param developerObjectId = '$DeveloperObjectId'" -ForegroundColor DarkGray
        Write-Host '    2. Delete the ad-hoc grants to avoid RoleAssignmentExists conflict on next deploy:' -ForegroundColor White
        Write-Host "         az cosmosdb sql role assignment delete --account-name $CosmosAccount --resource-group $ResourceGroup --role-definition-id $COSMOS_ROLE_DEF_ID --principal-id $DeveloperObjectId --scope $cosmosResourceId" -ForegroundColor DarkGray
        Write-Host "         az role assignment delete --assignee $DeveloperObjectId --role $SEARCH_ROLE_DEF_ID --scope $searchScope" -ForegroundColor DarkGray
        Write-Host "         az role assignment delete --assignee $DeveloperObjectId --role $FOUNDRY_ROLE_DEF_ID --scope $foundryScope" -ForegroundColor DarkGray
        Write-Host '    3. Re-deploy (the stack re-creates all three assignments and owns them):' -ForegroundColor White
        Write-Host '         pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev' -ForegroundColor DarkGray
        Write-Host '    4. Verify:' -ForegroundColor White
        Write-Host '         pwsh ./infra/scripts/Check-DeveloperRbac.ps1' -ForegroundColor DarkGray
    }

    exit 0
}

# At least one role is still missing.
Write-Host '  One or more role assignments are missing.' -ForegroundColor Red
Write-Host ''

if ($Fix) {
    Write-Host '  -Fix was specified but some assignments could not be restored.' -ForegroundColor Red
    Write-Host '  Check the az errors above for details.' -ForegroundColor Red
}
else {
    Write-Host '  Run with -Fix to restore them automatically:' -ForegroundColor Yellow
    Write-Host '    pwsh ./infra/scripts/Check-DeveloperRbac.ps1 -Fix' -ForegroundColor White
    Write-Host ''
    Write-Host '  Or run each missing grant individually (copy-paste):' -ForegroundColor Yellow
    Write-Host ''
    foreach ($fc in $fixCommands) {
        if ($rows | Where-Object { $_[1] -like "*$($fc.Label.Split(' ')[0])*" -and $_[2] -eq $STATUS_MISSING }) {
            Write-Host "  # $($fc.Label)" -ForegroundColor DarkCyan
            Write-Host $fc.Command -ForegroundColor White
            Write-Host ''
        }
    }
}

Write-Host '  NOTE: After ad-hoc grants, see the DURABLE FIX steps above (use -Fix to see them).' -ForegroundColor DarkGray
Write-Host '  Ad-hoc grants are stripped on the next stack deploy without developerObjectId.' -ForegroundColor DarkGray
Write-Host ''

exit 1
