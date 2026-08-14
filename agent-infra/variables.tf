variable "subscription_id" {
  description = "Azure subscription that owns the isolated agent resource group."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", var.subscription_id))
    error_message = "subscription_id must be a lowercase canonical GUID."
  }
}

variable "tenant_id" {
  description = "Microsoft Entra tenant for the isolated agent environment."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", var.tenant_id))
    error_message = "tenant_id must be a lowercase canonical GUID."
  }
}

variable "location" {
  description = "Azure region for all isolated agent resources."
  type        = string
  default     = "swedencentral"
}

variable "resource_group_name" {
  description = "Dedicated lifecycle and cost boundary for the hosted agent."
  type        = string
  default     = "rg-edgegrd-agent"
}

variable "prefix" {
  description = "Lowercase alphanumeric naming prefix."
  type        = string
  default     = "edgegrdagent"

  validation {
    condition     = can(regex("^[a-z0-9]{4,20}$", var.prefix))
    error_message = "prefix must contain 4-20 lowercase alphanumeric characters."
  }
}

variable "foundry_project_name" {
  description = "Foundry project child-resource name."
  type        = string
  default     = "proj-edgegrd-agent"
}

variable "model_deployment_name" {
  description = "Model deployment resource name used by the hosted agent."
  type        = string
  default     = "gpt-4o"
}

variable "model_name" {
  description = "Foundry model catalog name."
  type        = string
  default     = "gpt-4o"
}

variable "model_version" {
  description = "Pinned model version; never silently substituted."
  type        = string
  default     = "2024-11-20"
}

variable "model_capacity" {
  description = "GlobalStandard capacity in thousands of tokens per minute."
  type        = number
  default     = 10

  validation {
    condition     = var.model_capacity > 0
    error_message = "model_capacity must be greater than zero."
  }
}

variable "publisher_principal_id" {
  description = "Object ID allowed to create and populate the approved Search corpus."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", var.publisher_principal_id))
    error_message = "publisher_principal_id must be a lowercase canonical GUID."
  }
}

variable "hosted_agent_principal_id" {
  description = "Hosted-agent identity object ID after deployment. Null keeps every existing-stack assignment disabled."
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

variable "sentinel_app_principal_id" {
  description = "SentinelApp user-assigned identity object ID. Null keeps hosted endpoint invocation and user-delegation assignments disabled."
  type        = string
  default     = null
  nullable    = true

  validation {
    condition = var.sentinel_app_principal_id == null || can(regex(
      "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
      var.sentinel_app_principal_id
    ))
    error_message = "sentinel_app_principal_id must be null or a lowercase canonical GUID."
  }
}

variable "hosted_agent_name" {
  description = "Exact deployed Hosted Agent name used to construct the single-agent RBAC scope."
  type        = string
  default     = "jwt-sentinel-gate-explainer"

  validation {
    condition     = var.hosted_agent_name == "jwt-sentinel-gate-explainer"
    error_message = "hosted_agent_name must remain the explicitly reviewed jwt-sentinel-gate-explainer resource."
  }
}

variable "application_gateway_resource_id" {
  description = "Exact existing Application Gateway scope for the optional post-deployment Reader assignment."
  type        = string

  validation {
    condition = can(regex(
      "^/subscriptions/[0-9a-f-]+/resourceGroups/rg-edgegrd/providers/Microsoft.Network/applicationGateways/agw-edgegrd$",
      var.application_gateway_resource_id
    ))
    error_message = "application_gateway_resource_id must be the explicitly approved agw-edgegrd resource ID."
  }
}

variable "log_analytics_workspace_resource_id" {
  description = "Exact existing Stage 1 Log Analytics scope for optional post-deployment log reads."
  type        = string

  validation {
    condition = can(regex(
      "^/subscriptions/[0-9a-f-]+/resourceGroups/rg-edgegrd/providers/Microsoft.OperationalInsights/workspaces/law-edgegrd$",
      var.log_analytics_workspace_resource_id
    ))
    error_message = "log_analytics_workspace_resource_id must be the explicitly approved law-edgegrd resource ID."
  }
}

variable "budget_amount" {
  description = "Monthly soft budget. Azure budgets alert but do not stop spending."
  type        = number
  default     = 150

  validation {
    condition     = var.budget_amount > 0
    error_message = "budget_amount must be greater than zero."
  }
}

variable "budget_start_date" {
  description = "First day of the current month in RFC3339 form."
  type        = string
  default     = "2026-08-01T00:00:00Z"
}

variable "budget_contact_emails" {
  description = "Recipients for agent resource-group budget notifications."
  type        = list(string)
  default     = ["passadis@outlook.com"]

  validation {
    condition     = length(var.budget_contact_emails) > 0 && alltrue([for email in var.budget_contact_emails : can(regex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", email))])
    error_message = "At least one valid budget contact email is required."
  }
}

variable "tags" {
  description = "Additional non-sensitive resource tags."
  type        = map(string)
  default     = {}
}
