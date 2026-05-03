# Convenience launcher for the Aspire AppHost. Mirrors Neighborli's
# start-apphost.ps1 — Push-Location into the AppHost csproj, run, restore
# the original working directory on exit.

Push-Location "$PSScriptRoot\src\PinballWizard.AppHost"
try {
    dotnet run
} finally {
    Pop-Location
}
