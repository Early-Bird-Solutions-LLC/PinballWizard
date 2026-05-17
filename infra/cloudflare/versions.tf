# versions.tf
#
# Provider version pins and remote state backend.
#
# The `terraform` block is consumed identically by OpenTofu and HashiCorp
# Terraform. We target OpenTofu but the config remains portable.

terraform {
  required_version = ">= 1.8.0"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.15"
    }
    # Used only for completeness if/when we cross-reference Azure resources
    # (e.g. a Logpush job that writes to an Azure Blob container). Remove
    # if not needed.
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  # Remote state on Azure Blob Storage. The container and storage account
  # are created out-of-band by infra/azure/tfstate.bicep (bootstrap).
  #
  # Authentication uses OIDC in CI and Azure CLI locally — no shared keys.
  backend "azurerm" {
    resource_group_name  = "rg-pinball-tfstate"
    storage_account_name = "stpinballtfstate"
    container_name       = "tfstate"
    key                  = "cloudflare/pinwiz.ai.tfstate"
    use_oidc             = true
    use_azuread_auth     = true
  }
}
