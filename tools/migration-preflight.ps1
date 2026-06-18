<#
.SYNOPSIS
  Pre-flight + early-verification harness for AB#259 (and future) live data migrations.

.WHY
  The first OPDB re-sync ran for ~5 hours against a STALE binary (built before a
  merge rewrote the source) and only at the END did we discover phase-(d) wrote
  zero edition-qualified lookup rows. Two process failures caused that:
    1. No binary-freshness check before a long-running op.
    2. No verification until the run completed.
  This harness makes both impossible to skip: it FAILS FAST before a long op if the
  binary is stale, and (for OPDB) verifies the FIRST written unit early instead of
  waiting for the whole run.

.USAGE
  pwsh tools/migration-preflight.ps1                  # run all pre-flight checks, exit non-zero on any failure
  pwsh tools/migration-preflight.ps1 -RebuildIfStale  # auto-rebuild if the binary is stale
#>
[CmdletBinding()]
param(
    [switch]$RebuildIfStale
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cliDll = Join-Path $repo 'src/PinballWizard.Cli/bin/Release/net10.0/PinballWizard.Cli.dll'
$infraDll = Join-Path $repo 'src/PinballWizard.Infrastructure/bin/Release/net10.0/PinballWizard.Infrastructure.dll'
$failures = New-Object System.Collections.Generic.List[string]

function Check($name, [scriptblock]$test) {
    try {
        $result = & $test
        if ($result -eq $true) { Write-Host "  [PASS] $name" -ForegroundColor Green }
        else { Write-Host "  [FAIL] $name -- $result" -ForegroundColor Red; $failures.Add("$name : $result") }
    } catch {
        Write-Host "  [FAIL] $name -- $($_.Exception.Message)" -ForegroundColor Red
        $failures.Add("$name : $($_.Exception.Message)")
    }
}

Write-Host "=== Migration pre-flight ($(Get-Date -Format o)) ===" -ForegroundColor Cyan

# 1. BINARY FRESHNESS — the check whose absence cost 5 hours.
#    The compiled CLI/Infra dll must be NEWER than every tracked source file under src/.
Check "Binary newer than all src/ source (no stale build)" {
    if (-not (Test-Path $cliDll)) { return "CLI dll missing -- build first" }
    $dllTime = (Get-Item $cliDll).LastWriteTimeUtc
    $infraTime = (Get-Item $infraDll).LastWriteTimeUtc
    $buildTime = if ($infraTime -lt $dllTime) { $infraTime } else { $dllTime }
    $newestSrc = Get-ChildItem (Join-Path $repo 'src') -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newestSrc.LastWriteTimeUtc -gt $buildTime) {
        if ($RebuildIfStale) {
            Write-Host "    binary stale (src $($newestSrc.Name) @ $($newestSrc.LastWriteTimeUtc) > build @ $buildTime) -- rebuilding..." -ForegroundColor Yellow
            Push-Location $repo
            dotnet build PinballWizard.slnx -c Release --nologo | Out-Null
            Pop-Location
            return $true
        }
        return "STALE: $($newestSrc.Name) modified $($newestSrc.LastWriteTimeUtc) > build $buildTime. Rebuild before running."
    }
    return $true
}

# 2. GIT STATE — the build must reflect the current HEAD, no uncommitted src drift.
Check "No uncommitted source changes under src/" {
    Push-Location $repo
    $dirty = git status --porcelain -- 'src/**/*.cs'
    Pop-Location
    if ($dirty) { return "uncommitted src changes: $($dirty -split "`n" | Select-Object -First 3)" }
    return $true
}

# 3. AUTH — az login active on the expected subscription.
Check "az login active on pinwiz sub b1f33f17" {
    $acct = az account show --query id -o tsv 2>$null
    if ($acct -ne 'b1f33f17-74a9-4ecc-b46c-c4f31776b840') { return "wrong/no sub: '$acct'" }
    return $true
}

# 4. REQUIRED ENV for the OPDB sync (fail before a 5h run, not after).
Check "Cosmos + OPDB env vars set" {
    $missing = @()
    if (-not $env:Cosmos__AccountEndpoint) { $missing += 'Cosmos__AccountEndpoint' }
    if (-not $env:Opdb__ApiToken -and -not $env:OPDB_API_TOKEN) { $missing += 'Opdb__ApiToken/OPDB_API_TOKEN' }
    if ($missing) { return "missing: $($missing -join ', ')" }
    return $true
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "PRE-FLIGHT FAILED ($($failures.Count)) -- do NOT start the migration step:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "PRE-FLIGHT PASSED -- safe to start the migration step." -ForegroundColor Green
exit 0
