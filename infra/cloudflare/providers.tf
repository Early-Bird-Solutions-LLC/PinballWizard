# providers.tf
#
# Provider configuration. The Cloudflare API token is read from the
# CLOUDFLARE_API_TOKEN environment variable — never declared in HCL or
# .tfvars. In CI, the variable is sourced from GitHub Actions secrets.

provider "cloudflare" {
  # api_token is read from CLOUDFLARE_API_TOKEN env var automatically.
}

provider "azurerm" {
  features {}
  use_oidc = true
}
