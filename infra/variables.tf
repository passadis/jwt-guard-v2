variable "prefix" {
  description = "Short prefix for resource names (lowercase, alphanumeric)."
  type        = string
  default     = "jwtsent"
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "swedencentral"
}

variable "domain" {
  description = "Base DNS domain you control, e.g. contoso.com. UI and API hostnames are built from it."
  type        = string
}

variable "ui_subdomain" {
  description = "Subdomain for the web UI (public listener, no gateway JWT validation)."
  type        = string
  default     = "sentinel"
}

variable "api_subdomain" {
  description = "Subdomain for the JWT-protected demo API listener."
  type        = string
  default     = "sentinel-api"
}

# Optional: if the domain's DNS zone lives in Azure DNS, Terraform creates the
# A records automatically. Leave empty to create DNS records manually.
variable "dns_zone_name" {
  description = "Azure DNS zone name (usually equals var.domain). Empty = skip DNS record creation."
  type        = string
  default     = ""
}

variable "dns_zone_resource_group" {
  description = "Resource group of the Azure DNS zone."
  type        = string
  default     = ""
}

variable "dns_subscription_id" {
  description = "Subscription hosting the DNS zone (defaults to the deployment subscription)."
  type        = string
  default     = null
}

variable "model_deployment_name" {
  description = "Foundry model deployment name."
  type        = string
  default     = "gpt-4o"
}

variable "model_name" {
  description = "Model to deploy in Foundry."
  type        = string
  default     = "gpt-4o"
}

variable "model_version" {
  description = "Model version."
  type        = string
  default     = "2024-11-20"
}

variable "model_capacity" {
  description = "Deployment capacity (thousands of TPM for GlobalStandard)."
  type        = number
  default     = 50
}

variable "container_image" {
  description = "Container image for SentinelApp. Placeholder on first apply; scripts/deploy-app.ps1 pushes the real one."
  type        = string
  default     = "mcr.microsoft.com/k8se/quickstart:latest"
}

variable "gate_container_image" {
  description = "Container image for SentinelGate. Placeholder on first apply; scripts/deploy-app.ps1 pushes the real one."
  type        = string
  default     = "mcr.microsoft.com/k8se/quickstart:latest"
}

variable "gateway_config_generation" {
  description = "Opt-in full Application Gateway configuration resubmission generation. Leave at 0 normally; increment only after an approved restart and review the in-place plan."
  type        = number
  default     = 0

  validation {
    condition     = var.gateway_config_generation >= 0 && floor(var.gateway_config_generation) == var.gateway_config_generation
    error_message = "gateway_config_generation must be a non-negative integer."
  }
}

variable "hosted_agent_principal_id" {
  description = "Exact Hosted Agent runtime service-principal object ID authorized for the dormant SentinelApp broker. Null keeps the broker identity disabled."
  type        = string
  default     = null
  nullable    = true

  validation {
    condition = var.hosted_agent_principal_id == null || can(regex(
      "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
      var.hosted_agent_principal_id
    ))
    error_message = "hosted_agent_principal_id must be null or a lowercase canonical GUID."
  }
}

variable "hosted_agent_responses_endpoint" {
  description = "Exact immutable Hosted Agent Responses endpoint to pin in SentinelApp. Null keeps the endpoint absent."
  type        = string
  default     = null
  nullable    = true

  validation {
    condition = var.hosted_agent_responses_endpoint == null || can(regex(
      "^https://[a-z0-9-]+\\.services\\.ai\\.azure\\.com/api/projects/[A-Za-z0-9._~-]+/agents/[A-Za-z0-9._~-]+/endpoint/protocols/openai/responses\\?api-version=v1$",
      var.hosted_agent_responses_endpoint
    ))
    error_message = "hosted_agent_responses_endpoint must be null or the exact standard-port Azure AI Services Responses endpoint with only api-version=v1."
  }
}

variable "hosted_agent_version" {
  description = "Reviewed immutable Hosted Agent version pinned in SentinelApp. Null keeps the version absent."
  type        = number
  default     = null
  nullable    = true

  validation {
    condition     = var.hosted_agent_version == null || (var.hosted_agent_version > 0 && floor(var.hosted_agent_version) == var.hosted_agent_version)
    error_message = "hosted_agent_version must be null or a positive integer."
  }
}

variable "agent_mode" {
  description = "Operator-controlled Agent mode. Keep Embedded unless a separately reviewed shadow or promotion gate is approved."
  type        = string
  default     = "Embedded"

  validation {
    condition     = contains(["Embedded", "HostedShadow", "Hosted"], var.agent_mode)
    error_message = "agent_mode must be Embedded, HostedShadow, or Hosted."
  }
}

variable "hosted_shadow_tester_object_ids" {
  description = "Lowercase canonical Entra object IDs allowed to generate HostedShadow comparisons. Empty outside an approved shadow gate."
  type        = set(string)
  default     = []

  validation {
    condition = alltrue([
      for id in var.hosted_shadow_tester_object_ids : can(regex(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        id
      )) && id != "00000000-0000-0000-0000-000000000000"
    ])
    error_message = "hosted_shadow_tester_object_ids must contain only lowercase canonical non-empty GUIDs."
  }
}

locals {
  ui_hostname  = "${var.ui_subdomain}.${var.domain}"
  api_hostname = "${var.api_subdomain}.${var.domain}"
  rg_name      = "rg-${var.prefix}"
  # Unversioned Key Vault secret URI: rotating the cert (e.g. replacing the
  # self-signed bootstrap cert with a Let's Encrypt one under the same name)
  # is picked up by Application Gateway without a Terraform change.
  cert_name = "${var.prefix}-tls"
}
