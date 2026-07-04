$json = Get-Content 'c:\earlybird\PinballWizard\data\metadata\catalog.json' -Raw
$catalog = $json | ConvertFrom-Json
$docs = $catalog.documents

# Build a rich view: title, source, document_type, has_manual flag
$gameMap = @{}
foreach ($doc in $docs) {
    $title = $doc.game.title
    $source = $doc.source.source_type
    $dtype = $doc.classification.document_type
    $url = $doc.source.file_url
    $manufacturer = $doc.source.discovery_url -replace 'https?://([^/]+)/.*','$1'
    
    if (-not $gameMap.ContainsKey($title)) {
        $gameMap[$title] = @{
            Title = $title
            HasManual = $false
            Manufacturer = $manufacturer
            Documents = @()
        }
    }
    
    if ($dtype -eq 'Manual') {
        $gameMap[$title].HasManual = $true
    }
    
    $gameMap[$title].Documents += @{
        Type = $dtype
        Source = $source
        URL = $url
    }
}

# Output sorted list
$sorted = $gameMap.Values | Sort-Object Title

Write-Output "Total unique games: $($sorted.Count)"
Write-Output ""
Write-Output "=== GAMES WITHOUT MANUALS ==="
$noManual = $sorted | Where-Object { -not $_.HasManual }
Write-Output "Count: $($noManual.Count)"
$noManual | ForEach-Object {
    $types = ($_.Documents | ForEach-Object { $_.Type } | Sort-Object -Unique) -join ", "
    Write-Output "$($_.Title) [$($_.Manufacturer)] -- has: $types"
}

Write-Output ""
Write-Output "=== ALL GAMES (sorted) ==="
$sorted | ForEach-Object {
    $manualFlag = if ($_.HasManual) { "[M]" } else { "[ ]" }
    Write-Output "$manualFlag $($_.Title) [$($_.Manufacturer)]"
}
