#Requires -Version 7.0
<#
.SYNOPSIS
    Resolve committed vs gitignored .local.bicepparam without silent drift (#400).

.DESCRIPTION
    A .bicepparam file is a complete replacement, not a merge. Historically
    Deploy-*.ps1 swapped the committed file for *.local.bicepparam when present,
    so any param added to the committed file was silently ignored for operators
    with a stale local copy (issue #400 / cicdDeployPrincipalId incident).

    Resolution rule:
      1. Always use the committed parameters file as --parameters.
      2. From *.local.bicepparam, apply ONLY allowlisted personal overlays
         (e.g. developerObjectId) as additional key=value --parameters.
      3. Warn loudly about any other params declared in the local file —
         they are ignored so committed additions always win.

    Dot-source from Deploy-SharedResources.ps1 / Deploy-WorkloadJobs.ps1.
#>

function Get-BicepParamAssignments {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{}
    }

    $map = [ordered]@{}
    # param name = 'value'  |  param name = true/false  |  param name = 123
    $pattern = '^\s*param\s+(\w+)\s*=\s*(.+?)\s*$'
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match $pattern) {
            $name = $Matches[1]
            $raw = $Matches[2].Trim()
            # Strip // trailing comments
            if ($raw -match '^(.*?)\s+//') {
                $raw = $Matches[1].Trim()
            }
            $map[$name] = $raw
        }
    }
    return $map
}

function ConvertFrom-BicepParamLiteral {
    param([Parameter(Mandatory)][string] $Raw)

    if ($Raw -match "^'(.*)'$") {
        # Unescape bicep single-quoted '' → '
        return $Matches[1] -replace "''", "'"
    }
    if ($Raw -eq 'true') { return $true }
    if ($Raw -eq 'false') { return $false }
    if ($Raw -match '^-?\d+$') { return [int]$Raw }
    return $Raw
}

function Resolve-BicepParamSource {
    <#
    .OUTPUTS
        Hashtable with:
          ParametersFile  - path to committed .bicepparam (always)
          OverlayArgs     - string[] of name=value for az --parameters
          LocalPath       - path to local override if present, else $null
    #>
    param(
        [Parameter(Mandatory)]
        [string] $CommittedPath,

        [Parameter(Mandatory)]
        [string] $LocalPath,

        # Param names that may be taken from the local file and layered on
        # top of the committed file. Everything else in local is ignored.
        [string[]] $AllowedLocalParams = @('developerObjectId')
    )

    if (-not (Test-Path -LiteralPath $CommittedPath)) {
        throw "Committed parameters file not found: $CommittedPath"
    }

    $result = @{
        ParametersFile = $CommittedPath
        OverlayArgs    = [System.Collections.Generic.List[string]]::new()
        LocalPath      = $null
    }

    if (-not (Test-Path -LiteralPath $LocalPath)) {
        return $result
    }

    $result.LocalPath = $LocalPath
    $localMap = Get-BicepParamAssignments -Path $LocalPath
    $allowed = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$AllowedLocalParams,
        [StringComparer]::OrdinalIgnoreCase)

    $applied = [System.Collections.Generic.List[string]]::new()
    $ignored = [System.Collections.Generic.List[string]]::new()

    foreach ($name in $localMap.Keys) {
        if ($allowed.Contains($name)) {
            $value = ConvertFrom-BicepParamLiteral -Raw ([string]$localMap[$name])
            # az --parameters name=value; booleans/ints stringify fine
            $result.OverlayArgs.Add("$name=$value")
            $applied.Add($name)
        }
        else {
            $ignored.Add($name)
        }
    }

    Write-Host "Using committed parameters file: $CommittedPath" -ForegroundColor DarkGray
    Write-Host "  Local overlay: $LocalPath" -ForegroundColor Yellow
    if ($applied.Count -gt 0) {
        Write-Host "  Applied local overlays: $($applied -join ', ')" -ForegroundColor Yellow
    }
    else {
        Write-Host '  No allowlisted local overlays found (expected developerObjectId).' -ForegroundColor Yellow
    }
    if ($ignored.Count -gt 0) {
        Write-Host @"
  [!] Ignoring $($ignored.Count) non-allowlisted param(s) from local override:
      $($ignored -join ', ')
      Committed values win for those names (#400). Move durable env facts into
      the committed .bicepparam; keep only personal values (developerObjectId)
      in *.local.bicepparam.
"@ -ForegroundColor Yellow
    }

    return $result
}
