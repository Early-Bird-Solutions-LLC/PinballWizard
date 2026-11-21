#Requires -Version 7.0
# Behavioral checks for BicepParamOverlay.ps1 (run: pwsh -File this).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BicepParamOverlay.ps1')

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -ne $Actual) {
        throw "FAIL ${Name}: expected '${Expected}', got '${Actual}'"
    }
    Write-Host "OK ${Name}" -ForegroundColor Green
}

Assert-Equal 'true'  (Format-AzCliParameterValue -Value $true)  'bool true → JSON true'
Assert-equal 'false' (Format-AzCliParameterValue -Value $false) 'bool false → JSON false'
Assert-Equal '42'    (Format-AzCliParameterValue -Value 42)     'int passthrough'
Assert-equal 'abc'   (Format-AzCliParameterValue -Value 'abc')  'string passthrough'

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bicep-overlay-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $committed = Join-Path $tmp 'committed.bicepparam'
    $local = Join-Path $tmp 'local.bicepparam'
    Set-Content -LiteralPath $committed -Value "param enableOpsTriage = false`n"
    Set-Content -LiteralPath $local -Value @"
param enableOpsTriage = true
param opsTriageWebhookUri = 'https://example.test/hook?code=secret'
param ignoredParam = 'nope'
"@

    $resolved = Resolve-BicepParamSource `
        -CommittedPath $committed `
        -LocalPath $local `
        -AllowedLocalParams @('enableOpsTriage', 'opsTriageWebhookUri')

    $overlay = @($resolved.OverlayArgs)
    # -contains is case-insensitive; use -ccontains so true ≠ True.
    if ($overlay -cnotcontains 'enableOpsTriage=true') {
        throw "FAIL overlay bool: expected enableOpsTriage=true in [$($overlay -join ', ')]"
    }
    if ($overlay -ccontains 'enableOpsTriage=True') {
        throw 'FAIL overlay bool: PowerShell True leaked into az --parameters'
    }
    if ($overlay -cnotcontains 'opsTriageWebhookUri=https://example.test/hook?code=secret') {
        throw "FAIL overlay string: missing webhook uri in [$($overlay -join ', ')]"
    }
    if ($overlay -cmatch 'ignoredParam') {
        throw 'FAIL allowlist: ignoredParam should not overlay'
    }
    Write-Host 'OK Resolve-BicepParamSource bool/string overlays' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'All BicepParamOverlay checks passed.' -ForegroundColor Green
