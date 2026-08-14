output "resource_group_name" {
  value = azurerm_resource_group.agent.name
}

output "foundry_account_name" {
  value = azapi_resource.foundry_account.name
}

output "foundry_project_name" {
  value = azapi_resource.foundry_project.name
}

output "foundry_project_resource_id" {
  value = azapi_resource.foundry_project.id
}

output "foundry_project_endpoint" {
  value = "https://${azapi_resource.foundry_account.name}.services.ai.azure.com/api/projects/${azapi_resource.foundry_project.name}"
}

output "model_deployment_name" {
  value = azurerm_cognitive_deployment.model.name
}

output "search_service_name" {
  value = azurerm_search_service.agent.name
}

output "search_endpoint" {
  value = "https://${azurerm_search_service.agent.name}.search.windows.net"
}

output "agent_log_analytics_workspace_name" {
  value = azurerm_log_analytics_workspace.agent.name
}

output "agent_application_insights_name" {
  value = azurerm_application_insights.agent.name
}

output "foundry_application_insights_connection_name" {
  value = azapi_resource.foundry_project_app_insights_connection.name
}

output "hosted_agent_rbac_enabled" {
  value = local.hosted_agent_rbac_enabled
}

output "sentinel_app_invocation_enabled" {
  value = local.sentinel_app_invocation_enabled
}

output "hosted_agent_invocation_scope" {
  value = local.hosted_agent_scope
}

output "foundry_user_identity_impersonation_role_definition_id" {
  value = local.user_identity_impersonation_role_definition_id
}
