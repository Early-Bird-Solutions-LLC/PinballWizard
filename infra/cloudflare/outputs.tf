# outputs.tf
#
# Outputs exposed for cross-stack reference or operator use.
# Sensitive outputs are marked accordingly and not displayed in plan output.

output "origin_certificate_pem" {
  description = "Origin CA certificate PEM. Install on the Azure origin."
  value       = cloudflare_origin_ca_certificate.this.certificate
  sensitive   = true
}

output "origin_private_key_pem" {
  description = "Origin CA private key PEM. Install on the Azure origin. Do NOT log."
  value       = tls_private_key.origin.private_key_pem
  sensitive   = true
}

output "dnssec_ds_record" {
  description = "DS record to publish at the registrar to complete DNSSEC."
  value = {
    algorithm   = cloudflare_zone_dnssec.this.algorithm
    digest      = cloudflare_zone_dnssec.this.digest
    digest_type = cloudflare_zone_dnssec.this.digest_type
    key_tag     = cloudflare_zone_dnssec.this.key_tag
    ds          = cloudflare_zone_dnssec.this.ds
  }
}

output "zone_id" {
  description = "Cloudflare Zone ID — passthrough for use in other stacks."
  value       = var.zone_id
}
