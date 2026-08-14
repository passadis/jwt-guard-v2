locals {
  hosted_agent_rbac_enabled                      = var.hosted_agent_principal_id != null
  sentinel_app_invocation_enabled                = var.sentinel_app_principal_id != null
  hosted_agent_scope                             = "${azapi_resource.foundry_project.id}/agents/${var.hosted_agent_name}"
  foundry_agent_consumer_role_id                 = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/eed3b665-ab3a-47b6-8f48-c9382fb1dad6"
  user_identity_impersonation_role_definition_id = "9740bf1b-d365-4062-be42-df8d4b0194e9"
}

resource "azurerm_role_assignment" "project_foundry_user" {
  scope                = azapi_resource.foundry_account.id
  role_definition_name = "Foundry User"
  principal_id         = azapi_resource.foundry_project.output.identity.principalId
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "search_cognitive_services_user" {
  scope                = azapi_resource.foundry_account.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_search_service.agent.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "publisher_search_service_contributor" {
  scope                = azurerm_search_service.agent.id
  role_definition_name = "Search Service Contributor"
  principal_id         = var.publisher_principal_id
  principal_type       = "User"
}

resource "azurerm_role_assignment" "publisher_search_index_data_contributor" {
  scope                = azurerm_search_service.agent.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = var.publisher_principal_id
  principal_type       = "User"
}

# These three resources are intentionally absent from the foundation plan.
# They appear only after a real hosted-agent identity is supplied in a separate,
# reviewed post-deployment RBAC apply.
resource "azurerm_role_assignment" "hosted_agent_gateway_reader" {
  count                = local.hosted_agent_rbac_enabled ? 1 : 0
  scope                = var.application_gateway_resource_id
  role_definition_name = "Reader"
  principal_id         = var.hosted_agent_principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "hosted_agent_stage1_logs_reader" {
  count                = local.hosted_agent_rbac_enabled ? 1 : 0
  scope                = var.log_analytics_workspace_resource_id
  role_definition_name = "Log Analytics Reader"
  principal_id         = var.hosted_agent_principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "hosted_agent_search_reader" {
  count                = local.hosted_agent_rbac_enabled ? 1 : 0
  scope                = azurerm_search_service.agent.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = var.hosted_agent_principal_id
  principal_type       = "ServicePrincipal"
}

# Gate 2 caller permission. Foundry Agent Consumer grants only endpoint
# interaction and is scoped to this one Hosted Agent, not the project.
resource "azurerm_role_assignment" "sentinel_app_agent_consumer" {
  count = local.sentinel_app_invocation_enabled ? 1 : 0

  scope              = local.hosted_agent_scope
  role_definition_id = local.foundry_agent_consumer_role_id
  principal_id       = var.sentinel_app_principal_id
  principal_type     = "ServicePrincipal"
}

# x-ms-user-identity is required for server-derived per-user isolation. Current
# Foundry built-in roles deliberately omit this data action, so define exactly
# the one additional permission and keep its assignment at the same agent scope.
resource "azurerm_role_definition" "sentinel_app_user_identity_impersonation" {
  count = local.sentinel_app_invocation_enabled ? 1 : 0

  role_definition_id = local.user_identity_impersonation_role_definition_id
  name               = "JWT Sentinel Hosted Agent User Identity Impersonation"
  description        = "Lets SentinelApp pass a server-derived pseudonymous user identity to one Hosted Agent endpoint."
  scope              = azapi_resource.foundry_account.id
  assignable_scopes  = [azapi_resource.foundry_account.id]

  permissions {
    actions     = []
    not_actions = []
    data_actions = [
      "Microsoft.CognitiveServices/accounts/AIServices/agents/endpoints/UserIdentityImpersonation/action",
    ]
    not_data_actions = []
  }
}

resource "azurerm_role_assignment" "sentinel_app_user_identity_impersonation" {
  count = local.sentinel_app_invocation_enabled ? 1 : 0

  scope              = local.hosted_agent_scope
  role_definition_id = azurerm_role_definition.sentinel_app_user_identity_impersonation[0].role_definition_resource_id
  principal_id       = var.sentinel_app_principal_id
  principal_type     = "ServicePrincipal"
}
