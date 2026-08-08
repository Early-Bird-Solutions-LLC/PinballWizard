# PinballWizard Infrastructure

Azure infrastructure for PinballWizard (PinWiz.ai), managed entirely through Bicep IaC.

## Prerequisites

- Azure subscription with Owner or Contributor + User Access Administrator roles
- Azure CLI (`az`) v2.60+ with Bicep extension
- GitHub repository with Actions enabled
- Docker for building container images
- A resource group created: `rg-pinballwizard-prod` (or `rg-pinballwizard-dev`)

## Architecture

| Resource | Module | SKU (prod) |
|----------|--------|------------|
| User-assigned Managed Identity | `managed-identity.bicep` | -- |
| Log Analytics + App Insights | `log-analytics.bicep` | PerGB2018 |
| Key Vault | `key-vault.bicep` | Standard |
| Storage (Blob + Table) | `storage.bicep` | Standard LRS |
| Azure AI Search | `ai-search.bicep` | Basic |
| Azure AI Foundry (Hub + Project) | `ai-foundry.bicep` | Basic |
| Document Intelligence | `document-intelligence.bicep` | S0 |
| Speech Services | `speech-services.bicep` | S0 |
| Container Registry | `container-registry.bicep` | Basic |
| Container Apps Environment | `container-apps-env.bicep` | Consumption |
| Container App (Scraper) | `container-app-scraper.bicep` | 0.5 vCPU, 1 GiB |
| Container App (Processor) | `container-app-processor.bicep` | 1.0 vCPU, 2 GiB |
| Container App (Web) | `container-app-web.bicep` | 0.5 vCPU, 1 GiB |
| Event Grid | `event-grid.bicep` | -- |

## First-Time Setup

### 1. Create the Resource Group

```bash
az group create \
  --name rg-pinballwizard-prod \
  --location eastus2
```

### 2. Configure GitHub OIDC Authentication

Create an App Registration for GitHub Actions with federated credentials:

```bash
# Create app registration
az ad app create --display-name "pinballwizard-github-actions"

# Note the appId from output, then create a service principal
az ad sp create --id <appId>

# Add federated credential for main branch
az ad app federated-credential create \
  --id <appId> \
  --parameters '{
    "name": "github-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:<owner>/PinballWizard:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# Add federated credential for pull requests
az ad app federated-credential create \
  --id <appId> \
  --parameters '{
    "name": "github-pr",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:<owner>/PinballWizard:pull_request",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# Grant Contributor role on the resource group
az role assignment create \
  --role "Contributor" \
  --assignee <appId> \
  --scope /subscriptions/<subscriptionId>/resourceGroups/rg-pinballwizard-prod

# Grant User Access Administrator for RBAC assignments
az role assignment create \
  --role "User Access Administrator" \
  --assignee <appId> \
  --scope /subscriptions/<subscriptionId>/resourceGroups/rg-pinballwizard-prod
```

### 3. Set GitHub Secrets

In your GitHub repository settings, add these secrets:

| Secret | Value |
|--------|-------|
| `AZURE_CLIENT_ID` | App registration client ID |
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Target subscription ID |

### 4. Push Container Images (First Deploy)

Before the first Bicep deployment, push placeholder images to ACR so the Container Apps can reference them:

```bash
# Login to ACR
az acr login --name pwacr

# Build and push all images
docker build -t pwacr.azurecr.io/pinballwizard-scraper:latest -f Dockerfile.scraper .
docker build -t pwacr.azurecr.io/pinballwizard-processor:latest -f Dockerfile.processor .
docker build -t pwacr.azurecr.io/pinballwizard-web:latest -f Dockerfile.web .

docker push pwacr.azurecr.io/pinballwizard-scraper:latest
docker push pwacr.azurecr.io/pinballwizard-processor:latest
docker push pwacr.azurecr.io/pinballwizard-web:latest
```

**Note:** The ACR must be deployed first (via a partial Bicep deployment or manually) before pushing images.

## Manual Deployment via CLI

### Deploy to Production

```bash
az stack group create \
  --name pinballwizard \
  --resource-group rg-pinballwizard-prod \
  --template-file infra/main.bicep \
  --parameters infra/environments/prod.bicepparam \
  --deny-settings-mode denyWriteAndDelete \
  --action-on-unmanage deleteAll \
  --yes
```

### Deploy to Dev

```bash
az stack group create \
  --name pinballwizard-dev \
  --resource-group rg-pinballwizard-dev \
  --template-file infra/main.bicep \
  --parameters infra/environments/dev.bicepparam \
  --deny-settings-mode none \
  --action-on-unmanage detachAll \
  --yes
```

### Deploy with a Specific Image Tag

```bash
az stack group create \
  --name pinballwizard \
  --resource-group rg-pinballwizard-prod \
  --template-file infra/main.bicep \
  --parameters infra/environments/prod.bicepparam \
  --parameters imageTag=abc12345 \
  --deny-settings-mode denyWriteAndDelete \
  --action-on-unmanage deleteAll \
  --yes
```

## How the Deployment Stack Works

This project uses **Azure Deployment Stacks** (`az stack group create`) rather than raw `az deployment group create`. Key benefits:

- **Deny settings**: `denyWriteAndDelete` prevents accidental manual changes to managed resources
- **Unmanage policy**: `deleteAll` cleans up resources removed from the template
- **Drift protection**: Resources managed by the stack cannot be modified outside of IaC
- **Idempotent**: Running the same command again updates resources to match the template

To view the current stack state:

```bash
az stack group show \
  --name pinballwizard \
  --resource-group rg-pinballwizard-prod
```

To list managed resources:

```bash
az stack group show \
  --name pinballwizard \
  --resource-group rg-pinballwizard-prod \
  --query "resources[].id" -o tsv
```

## Adding/Updating Key Vault Secrets

Secrets are initially created with placeholder values by the Bicep template. To set real values:

```bash
# Set a secret value
az keyvault secret set \
  --vault-name pw-kv-prod \
  --name google-oauth-client-id \
  --value "<real-value>" \
  --content-type "text/plain" \
  --expires "$(date -u -d '+90 days' +%Y-%m-%dT%H:%M:%SZ)"
```

**Important:** All secrets MUST have:
- `contentType` set (Azure Policy enforced)
- Expiry within 90 days (Azure Policy enforced)

### Current Secrets

| Secret Name | Used By | Purpose |
|-------------|---------|---------|
| `google-oauth-client-id` | Web | Google OAuth app client ID |
| `google-oauth-client-secret` | Web | Google OAuth app client secret |
| `jwt-signing-key` | API | JWT token signing key |
| `ai-search-admin-key` | Processor/API | Search admin key (if needed beyond managed identity) |

## Module Dependency Graph

```
managed-identity
  |
  +-- key-vault
  +-- storage
  +-- ai-search
  +-- ai-foundry (also depends on key-vault, storage, log-analytics)
  +-- document-intelligence
  +-- speech-services
  +-- container-registry

log-analytics
  |
  +-- container-apps-env

container-apps-env + container-registry + managed-identity
  |
  +-- container-app-scraper
  +-- container-app-processor
  +-- container-app-web

storage + container-app-processor
  |
  +-- event-grid
```

## Naming Convention

Pattern: `{prefix}-{resource}-{env}`

Examples:
- `pw-identity-prod` -- Managed identity
- `pw-search-prod` -- Azure AI Search
- `pw-web-prod` -- Web Container App
- `pwstorageprod` -- Storage account (alphanumeric only)
- `pwacr` -- Container Registry (alphanumeric only)
