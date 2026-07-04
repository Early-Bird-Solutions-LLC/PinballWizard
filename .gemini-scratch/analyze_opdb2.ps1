$opdb = Get-Content 'c:\earlybird\PinballWizard\data\cache\opdb-export.json' -Raw | ConvertFrom-Json

# Focus on SS/DMD era games (solid-state+ -- those that actually have service manuals)
# Flip EM/mechanical games are documented very differently
$ssGames = $opdb | Where-Object { $_.type -in @("ss","dmd") }
$emGames = $opdb | Where-Object { $_.type -eq "em" }
$hybridGames = $opdb | Where-Object { $_.type -notin @("ss","dmd","em") }

Write-Output "Total OPDB: $($opdb.Count)"
Write-Output "Solid-state (ss/dmd): $($ssGames.Count)"
Write-Output "Electro-mechanical (em): $($emGames.Count)"  
Write-Output "Other types: $($hybridGames.Count)"
Write-Output ""

# SS games by manufacturer (sorted by count)
$byManuf = $ssGames | Group-Object { $_.manufacturer.name } | Sort-Object Count -Descending
Write-Output "=== SS/DMD games by manufacturer (top 25) ==="
$byManuf | Select-Object -First 25 | ForEach-Object {
    Write-Output "$($_.Count.ToString().PadLeft(4))  $($_.Name)"
}

Write-Output ""
Write-Output "=== All SS/DMD games with IPDB IDs (for manual lookup) ==="
$ssWithIpdb = $ssGames | Where-Object { $_.ipdb_id -ne $null -and $_.ipdb_id -gt 0 }
Write-Output "Count: $($ssWithIpdb.Count)"

# Export the list we actually need to search for manuals
Write-Output ""
Write-Output "=== Key manufacturers for manual searching (SS era) ==="
$keyManufacturers = @("Williams","Bally","Gottlieb","Stern","Stern Electronics","Data East","Sega","Jersey Jack Pinball","Spooky Pinball","American Pinball","Chicago Gaming","Capcom","Game Plan","Atari","Midway","Pinball Brothers","Dutch Pinball","Multimorphic","Barrels of Fun","deeproot","Haggis Pinball","Heighway Pinball")

foreach ($m in $keyManufacturers) {
    $games = $ssGames | Where-Object { $_.manufacturer.name -eq $m } | Sort-Object manufacture_date
    if ($games.Count -gt 0) {
        Write-Output ""
        Write-Output "--- $m ($($games.Count) SS games) ---"
        $games | ForEach-Object {
            $year = if ($_.manufacture_date) { $_.manufacture_date.Substring(0,4) } else { "????" }
            Write-Output "  [$year] ipdb:$($_.ipdb_id.ToString().PadLeft(5)) opdb:$($_.opdb_id) - $($_.name)"
        }
    }
}
