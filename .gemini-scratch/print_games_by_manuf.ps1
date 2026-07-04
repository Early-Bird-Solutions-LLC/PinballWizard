$data = Import-Csv 'c:\earlybird\PinballWizard\.gemini-scratch\opdb_ss_games.csv'

Write-Output "=== WILLIAMS SS/DMD GAMES ==="
$data | Where-Object { $_.Manufacturer -eq "Williams" } | Sort-Object Year, Name | ForEach-Object {
    Write-Output "$($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}

Write-Output ""
Write-Output "=== BALLY SS/DMD GAMES ==="
$data | Where-Object { $_.Manufacturer -eq "Bally" } | Sort-Object Year, Name | ForEach-Object {
    Write-Output "$($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}

Write-Output ""
Write-Output "=== GOTTLIEB SS/DMD GAMES ==="
$data | Where-Object { $_.Manufacturer -eq "Gottlieb" } | Sort-Object Year, Name | ForEach-Object {
    Write-Output "$($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}

Write-Output ""
Write-Output "=== DATA EAST SS/DMD GAMES ==="
$data | Where-Object { $_.Manufacturer -eq "Data East" } | Sort-Object Year, Name | ForEach-Object {
    Write-Output "$($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}

Write-Output ""
Write-Output "=== SEGA SS/DMD GAMES ==="
$data | Where-Object { $_.Manufacturer -eq "Sega" } | Sort-Object Year, Name | ForEach-Object {
    Write-Output "$($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}

Write-Output ""
Write-Output "=== CAPCOM SS/DMD GAMES ==="
$data | Where-Object { $_.Manufacturer -eq "Capcom" } | Sort-Object Year, Name | ForEach-Object {
    Write-Output "$($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}

Write-Output ""
Write-Output "=== JJP/SPOOKY/AP/CGC/BARRELS (Modern) SS/DMD GAMES NOT IN CATALOG ==="
$modernManuf = @("Jersey Jack Pinball","Spooky Pinball","American Pinball","Chicago Gaming","Barrels of Fun","Pinball Brothers","Dutch Pinball","Multimorphic","Heighway Pinball","Haggis Pinball","deeproot","Pedretti Gaming","Riot Pinball","Bandai Namco")
$data | Where-Object { $_.Manufacturer -in $modernManuf -and $_.InCatalog -eq "False" } | Sort-Object Manufacturer, Year, Name | ForEach-Object {
    Write-Output "$($_.Manufacturer) | $($_.Year)  IPDB:$($_.IPDB.PadLeft(5))  $($_.Name)"
}
