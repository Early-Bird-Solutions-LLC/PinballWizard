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

variable "aca_domain_verification_token" {
  description = "Azure Container Apps custom domain verification token. Value of the asuid.<domain> TXT record. Find in the ACA Custom Domains blade. Required for ACA cert renewal — do not remove."
  type        = string
  sensitive   = true
}

variable "dev_gate_enabled" {
  description = "Pre-launch gate (PL1). When true, the entire apex is behind Cloudflare Zero Trust Access (maintainer-only). Set to false and apply at launch to remove the gate atomically — pinwiz.ai is anonymous-public at launch per ADR-0009 Tier 1."
  type        = bool
  default     = true
}

variable "dev_gate_allowed_emails" {
  description = "Identities permitted through the pre-launch gate. These are LOGIN identities (one-time-PIN email), distinct from admin_email which is the security/CAA contact, not an authenticator."
  type        = list(string)
  default     = ["jim@earlybirdsolutions.com"]
}
