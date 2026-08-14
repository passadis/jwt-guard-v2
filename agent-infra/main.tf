resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

locals {
  foundry_account_name = "aif-${var.prefix}-${random_string.suffix.result}"
  search_service_name  = "srch-${var.prefix}-${random_string.suffix.result}"
  common_tags = merge({
    application = "jwt-sentinel"
    component   = "hosted-agent"
    environment = "parity"
    managed-by  = "terraform"
    stage       = "agent-phase-1"
  }, var.tags)
}

resource "azurerm_resource_group" "agent" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.common_tags
}

resource "azapi_resource" "foundry_account" {
  type      = "Microsoft.CognitiveServices/accounts@2025-06-01"
  name      = local.foundry_account_name
  parent_id = azurerm_resource_group.agent.id
  location  = azurerm_resource_group.agent.location

  identity {
    type = "SystemAssigned"
  }

  body = {
    kind = "AIServices"
    sku = {
      name = "S0"
    }
    properties = {
      allowProjectManagement        = true
      customSubDomainName           = local.foundry_account_name
      disableLocalAuth              = true
      publicNetworkAccess           = "Enabled"
      restrictOutboundNetworkAccess = false
    }
  }

  tags                   = local.common_tags
  response_export_values = ["identity.principalId", "properties.endpoint"]
}

resource "azapi_resource" "foundry_project" {
  type      = "Microsoft.CognitiveServices/accounts/projects@2025-06-01"
  name      = var.foundry_project_name
  parent_id = azapi_resource.foundry_account.id
  location  = azurerm_resource_group.agent.location

  identity {
    type = "SystemAssigned"
  }

  body = {
    properties = {
      description = "Isolated JWT Sentinel hosted-agent parity project."
      displayName = "JWT Sentinel Hosted Agent"
    }
  }

  tags                   = local.common_tags
  response_export_values = ["identity.principalId", "properties.endpoints"]
}

resource "azurerm_cognitive_deployment" "model" {
  name                 = var.model_deployment_name
  cognitive_account_id = azapi_resource.foundry_account.id

  model {
    format  = "OpenAI"
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.model_capacity
  }

  version_upgrade_option = "NoAutoUpgrade"
}

resource "azurerm_log_analytics_workspace" "agent" {
  name                         = "law-edgegrd-agent"
  location                     = azurerm_resource_group.agent.location
  resource_group_name          = azurerm_resource_group.agent.name
  sku                          = "PerGB2018"
  retention_in_days            = 30
  local_authentication_enabled = false
  tags                         = local.common_tags
}

resource "azurerm_application_insights" "agent" {
  name                = "appi-edgegrd-agent"
  location            = azurerm_resource_group.agent.location
  resource_group_name = azurerm_resource_group.agent.name
  workspace_id        = azurerm_log_analytics_workspace.agent.id
  application_type    = "web"
  # Hosted Agent monitoring injects a platform-reserved connection string and
  # currently uses connection-string ingestion unless an explicit Entra-aware
  # exporter is configured. Keep local ingestion auth enabled for Phase 1.
  local_authentication_enabled = true
  tags                         = local.common_tags
}

resource "azurerm_consumption_budget_resource_group" "agent" {
  name              = "budget-edgegrd-agent"
  resource_group_id = azurerm_resource_group.agent.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  dynamic "notification" {
    for_each = toset([50, 80, 100])
    content {
      enabled        = true
      threshold      = notification.value
      operator       = "GreaterThanOrEqualTo"
      threshold_type = "Actual"
      contact_emails = var.budget_contact_emails
    }
  }
}
