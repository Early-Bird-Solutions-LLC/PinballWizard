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
#   $env:AZURE_CONFIG_DIR = "$env:USERPROFILE\.azure-pinwiz"
#   az login --use-device-code
#   az account set --subscription "pinwiz.ai"
#
# One-time machine setup:
#   [System.Environment]::SetEnvironmentVariable("SILVERBALL_API_KEY", "<key>", "Machine")
#
# Emulated locally (no Azure needed): Cosmos DB, Azure Storage (Azurite).

# ── Azure auth — personal pinwiz.ai identity ──────────────────────────────
# Isolated config dir so the work/APS ~/.azure is never touched.
$env:AZURE_CONFIG_DIR = "$env:USERPROFILE\.azure-pinwiz"

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
$env:AiFoundry__ProjectEndpoint        = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiFoundry__EmbeddingDeploymentName = "text-embedding-3-large"
$env:AiSearch__Endpoint                = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:AiSearch__IndexName               = "pinwiz-rag-v1"

aspire run --apphost src\PinballWizard.AppHost
