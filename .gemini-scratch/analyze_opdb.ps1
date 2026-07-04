$opdb = Get-Content 'c:\earlybird\PinballWizard\data\cache\opdb-export.json' -Raw | ConvertFrom-Json
$count = $opdb.Count
Write-Output "Total OPDB entries: $count"

$manuf = $opdb | ForEach-Object { $_.manufacturer.name } | Sort-Object -Unique
$manufCount = $manuf.Count
Write-Output "Unique manufacturers: $manufCount"
$manuf | ForEach-Object { Write-Output "  $_" }

Write-Output ""
Write-Output "=== IPDB-referenced games (have ipdb_id) ==="
$withIpdb = $opdb | Where-Object { $_.ipdb_id -ne $null }
$withIpdbCount = $withIpdb.Count
Write-Output "Count with ipdb_id: $withIpdbCount"

Write-Output ""
Write-Output "=== Sample: games manufactured by key manufacturers (first 10 each) ==="
$keyManuf = @("Williams","Bally","Gottlieb","Stern","Data East","Sega","Jersey Jack Pinball","Spooky Pinball","American Pinball","Chicago Gaming")
foreach ($m in $keyManuf) {
    $games = $opdb | Where-Object { $_.manufacturer.name -eq $m }
    $ct = $games.Count
    Write-Output ""
    Write-Output "--- $m ($ct games) ---"
    $games | Select-Object -First 5 | ForEach-Object { Write-Output "  opdb:$($_.opdb_id) ipdb:$($_.ipdb_id) - $($_.name) ($($_.manufacture_date))" }
}
