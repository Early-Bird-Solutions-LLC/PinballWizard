# variables.tf
#
# Input variables for the Cloudflare stack. Real values live in
# terraform.tfvars (gitignored) or in CI as TF_VAR_<name> env vars.

variable "zone_id" {
  description = "Cloudflare Zone ID for pinwiz.ai. Find on the zone overview page in the dashboard."
  type        = string

  validation {
    condition     = can(regex("^[a-f0-9]{32}$", var.zone_id))
    error_message = "zone_id must be a 32-character lowercase hex string."
  }
}

variable "account_id" {
  description = "Cloudflare Account ID. Required for account-scoped resources (Access, Logpush, notifications)."
  type        = string

  validation {
    condition     = can(regex("^[a-f0-9]{32}$", var.account_id))
    error_message = "account_id must be a 32-character lowercase hex string."
  }
}

variable "domain" {
  description = "The apex domain managed by this stack."
  type        = string
  default     = "pinwiz.ai"
}

variable "origin_hostname" {
  description = "The Azure-hosted origin hostname (e.g. pinwiz-api-prod.azurewebsites.net). DNS records point here."
  type        = string
}

variable "admin_email" {
  description = "Email address for security contacts (CAA iodef, security.txt, notifications)."
  type        = string
  default     = "security@pinwiz.ai"
}

variable "logpush_destination" {
  description = "Logpush destination URL. For Azure Blob, format: azure://account.blob.core.windows.net/container?sv=... (SAS-bearing). Treat as sensitive."
  type        = string
  sensitive   = true
  default     = ""
}

variable "prelaunch_gate_enabled" {
  description = "When true, pinwiz.ai is restricted to var.maintainer_email via a Cloudflare Access pre-launch gate. Set to false only when the public frontend is ready to ship (Phase 4+). Removing the gate opens the apex to the world."
  type        = bool
  default     = true
}

variable "maintainer_email" {
  description = "Email allowed through the pre-launch gate. Personal Earlybird identity (see locked invariant #5)."
  type        = string
  default     = "jim@earlybirdsolutions.com"
}

variable "aca_domain_verification_token" {
  description = "Azure Container Apps custom domain verification token. Value of the asuid.<domain> TXT record. Find in the ACA Custom Domains blade. Required for ACA cert renewal — do not remove."
  type        = string
  sensitive   = true
}
