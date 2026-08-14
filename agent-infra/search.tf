resource "azurerm_search_service" "agent" {
  name                          = local.search_service_name
  resource_group_name           = azurerm_resource_group.agent.name
  location                      = azurerm_resource_group.agent.location
  sku                           = "basic"
  replica_count                 = 1
  partition_count               = 1
  public_network_access_enabled = true
  local_authentication_enabled  = false
  semantic_search_sku           = "free"
  tags                          = local.common_tags

  identity {
    type = "SystemAssigned"
  }
}

# This PATCH records explicit non-consent to paid agentic retrieval. It is
# intentionally separate from semantic_search_sku and remains Terraform-owned.
resource "azapi_update_resource" "search_knowledge_retrieval" {
  type        = "Microsoft.Search/searchServices@2026-03-01-preview"
  resource_id = azurerm_search_service.agent.id

  body = {
    properties = {
      knowledgeRetrieval = "free"
    }
  }
}
