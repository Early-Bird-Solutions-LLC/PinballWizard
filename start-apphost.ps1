# Full-feature local dev launcher for PinballWizard AppHost.
#
# Launches via the Aspire CLI ("aspire run") so the running AppHost is
# registered with the CLI — enabling "aspire agent mcp" (and the committed
# .mcp.json) to attach and give AI coding agents live log/trace/resource
# access to the local environment.
#
# Live Azure services (no local emulator): AI Foundry, AI Search.
# These require a personal pinwiz.ai Azure sign-in. If you haven't signed
# in yet, or your token is stale, run:
#
#   $env:AZURE_CONFIG_DIR = "D:/Projects/APS.ClaudeCodeConfig/orgs/pinwiz/azure"
#   az login --use-device-code
#   az account set --subscription "pinwiz.ai"
#
# One-time machine setup:
#   [System.Environment]::SetEnvironmentVariable("SILVERBALL_API_KEY", "<key>", "Machine")
#
# Emulated locally (no Azure needed): Cosmos DB, Azure Storage (Azurite).

# ── Azure auth — personal pinwiz.ai identity ──────────────────────────────
# The PER-ORG isolated config dir, so the work/APS ~/.azure is never touched.
# This must be the canonical per-org path, not a ~/.azure-<name> dir: the
# az-isolation rule treats "set, but not a per-org dir" as a red flag, because
# it still gets contaminated by whatever tenant is active. Keeping this script,
# .vscode/settings.json and launch.json on one dir also means they share a
# single authenticated session instead of three drifting token caches.
$env:AZURE_CONFIG_DIR = "D:/Projects/APS.ClaudeCodeConfig/orgs/pinwiz/azure"

# AZURE_TOKEN_CREDENTIALS=dev tells DefaultAzureCredential to use only
# developer credentials (Azure CLI) and skip the managed-identity IMDS probe
# (169.254.169.254). Without this, the probe times out on non-Azure machines
# and causes Cosmos / AI Search writes to fail with MsalClientException.
# Azure.Identity ≥1.21.0 honours this env var.
$env:AZURE_TOKEN_CREDENTIALS = "dev"

# ── Silverball Labs live pricing ───────────────────────────────────────────
# Key stored as machine env var SILVERBALL_API_KEY (never in source).
# .NET config section separator = __ → maps to SilverballLabs:ApiKey.
# Read from registry directly so new machine vars are picked up even in
# sessions that started before the var was set.
$sblKey = [System.Environment]::GetEnvironmentVariable("SILVERBALL_API_KEY", "Machine")
if (-not $sblKey) { $sblKey = [System.Environment]::GetEnvironmentVariable("SILVERBALL_API_KEY", "User") }
if (-not $sblKey) { $sblKey = $env:SILVERBALL_API_KEY }
if ($sblKey) {
    $env:SilverballLabs__ApiKey = $sblKey
} else {
    Write-Host "[start-apphost] SILVERBALL_API_KEY not set — live pricing will be unavailable." -ForegroundColor Yellow
}

# ── Azure AI Foundry + AI Search (live, rg-pinwiz-shared-dev / buutj) ─────
# Absent = Foundry/Router not registered; Wizard returns 503; corpus stats
# page shows "unavailable". Set both to get a fully functional Wizard.
# Values from Deploy-SharedResources.ps1 outputs (sub b1f33f17, suffix buutj).
$env:AiFoundry__ProjectEndpoint        = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiFoundry__EmbeddingDeploymentName = "text-embedding-3-large"
$env:AiSearch__Endpoint                = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:AiSearch__IndexName               = "pinwiz-rag-v1"

# ── Launch + browser auto-open ─────────────────────────────────────────────
# Stream aspire run output line-by-line via a redirected stdout reader so we
# can detect the Aspire dashboard URL and open it automatically, while also
# getting the correct process exit code.
#
# Using a pipe (aspire | ForEach-Object) causes $LASTEXITCODE to reflect the
# pipeline result rather than aspire's exit — so errors are never detected.
# Reading stdout via Process.StandardOutput fixes that.
$startInfo = [System.Diagnostics.ProcessStartInfo]::new('aspire', 'run --apphost src\PinballWizard.AppHost')
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
# stderr is not redirected — it flows to this terminal unchanged.

$browserOpened = $false
$proc = [System.Diagnostics.Process]::Start($startInfo)
while (-not $proc.StandardOutput.EndOfStream) {
    $line = $proc.StandardOutput.ReadLine()
    Write-Host $line
    if (-not $browserOpened -and
        ($line -match 'dashboard' -or $line -match 'login') -and
        $line -match '(https?://localhost:\d+\S*)') {
        $browserOpened = $true
        $url = $Matches[1].TrimEnd('.')
        Write-Host "[start-apphost] Opening browser → $url" -ForegroundColor Cyan
        Start-Process $url
    }
}
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) {
    Write-Error "[start-apphost] aspire run exited with code $($proc.ExitCode)"
}
