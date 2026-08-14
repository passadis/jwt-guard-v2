# Microsoft Foundry (AI Services) account + model deployment. The Container
# App's managed identity calls it with Entra auth — no keys in config.

resource "azurerm_ai_services" "main" {
  name                  = "aif-${var.prefix}-${random_string.kv.result}"
  location              = azurerm_resource_group.main.location
  resource_group_name   = azurerm_resource_group.main.name
  sku_name              = "S0"
  custom_subdomain_name = "aif-${var.prefix}-${random_string.kv.result}"
}

resource "azurerm_cognitive_deployment" "model" {
  name                 = var.model_deployment_name
  cognitive_account_id = azurerm_ai_services.main.id

  model {
    format  = "OpenAI"
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.model_capacity
  }
}
