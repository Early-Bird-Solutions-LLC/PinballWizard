$opdb = Get-Content 'c:\earlybird\PinballWizard\data\cache\opdb-export.json' -Raw | ConvertFrom-Json

# Key manufacturers that have SS/DMD-era games with likely digital manuals
$keyManuf = @(
    "Williams", "Bally", "Gottlieb", "Stern", "Stern Electronics",
    "Data East", "Sega", "Jersey Jack Pinball", "Spooky Pinball",
    "American Pinball", "Chicago Gaming", "Capcom", "Game Plan", "Atari",
    "Midway", "Pinball Brothers", "Dutch Pinball", "Multimorphic",
    "Barrels of Fun", "deeproot", "Haggis Pinball", "Heighway Pinball",
    "Zaccaria", "Alvin G. & Co", "Hankin", "Inder", "Peyper",
    "Juegos Populares", "Jeutel", "Premier Technology","Pinstar",
    "Innovative Concepts (ICE)","Pedretti Gaming","Bandai Namco",
    "Pinball Brothers","Riot Pinball"
)

$ssGames = $opdb | Where-Object { $_.type -in @("ss","dmd") }
$targetGames = $ssGames | Where-Object { $_.manufacturer.name -in $keyManuf }

Write-Output "SS/DMD games from key manufacturers: $($targetGames.Count)"
Write-Output ""

# Also pull all games from catalog.json to cross-reference what we already have
$catalog = Get-Content 'c:\earlybird\PinballWizard\data\metadata\catalog.json' -Raw | ConvertFrom-Json
$catalogTitles = $catalog.documents | ForEach-Object { $_.game.title } | Sort-Object -Unique

Write-Output "Already in our catalog: $($catalogTitles.Count) unique titles"
Write-Output ""

# Build the output - group by manufacturer, sorted
$results = @()
foreach ($game in $targetGames | Sort-Object { $_.manufacturer.name }, { $_.manufacture_date }) {
    $year = "????"
    if ($game.manufacture_date -and $game.manufacture_date.Length -ge 4) {
        $year = $game.manufacture_date.Substring(0,4)
    }
    $inCatalog = $catalogTitles -contains $game.name
    
    $results += [PSCustomObject]@{
        Manufacturer = $game.manufacturer.name
        Year = $year
        Name = $game.name
        IPDB = if ($game.ipdb_id) { $game.ipdb_id } else { "" }
        OPDB = $game.opdb_id
        InCatalog = $inCatalog
        Type = $game.type
    }
}

# Export to CSV for easy processing
$results | Export-Csv -Path '.gemini-scratch\opdb_ss_games.csv' -NoTypeInformation -Encoding UTF8
Write-Output "Exported $($results.Count) games to .gemini-scratch\opdb_ss_games.csv"

# Summary by manufacturer
Write-Output ""
Write-Output "=== Games by manufacturer ==="
$results | Group-Object Manufacturer | Sort-Object Count -Descending | ForEach-Object {
    $inCat = ($_.Group | Where-Object { $_.InCatalog }).Count
    Write-Output "$($_.Count.ToString().PadLeft(4)) total ($inCat in catalog)  $($_.Name)"
}

# Games NOT yet in our catalog
$notInCatalog = $results | Where-Object { -not $_.InCatalog }
Write-Output ""
Write-Output "=== Games in OPDB SS set but NOT in our catalog: $($notInCatalog.Count) ==="
