output "ui_url" {
  value = "https://${local.ui_hostname}"
}

output "protected_api_url" {
  value = "https://${local.api_hostname}"
}

output "appgw_public_ip" {
  value       = azurerm_public_ip.appgw.ip_address
  description = "Point the ui/api A records here if DNS is not in Azure DNS."
}

output "tenant_id" {
  value = data.azurerm_client_config.current.tenant_id
}

output "api_client_id" {
  value       = azuread_application.api.client_id
  description = "Audience the gateway validates (also api://<this>)."
}

output "spa_client_id" {
  value = azuread_application.spa.client_id
}

output "daemon_client_id" {
  value = azuread_application.daemon.client_id
}

output "agent_scenario_execute_role_id" {
  value       = local.agent_scenario_execute_role_id
  description = "Stable Entra application-role ID for the Hosted Agent evidence broker."
}

output "acr_name" {
  value = azurerm_container_registry.main.name
}

output "container_app_name" {
  value = azurerm_container_app.main.name
}

output "sentinel_app_name" {
  value       = azurerm_container_app.main.name
  description = "SentinelApp control-plane Container App name."
}

output "sentinel_gate_name" {
  value       = azurerm_container_app.gate.name
  description = "SentinelGate protected-plane Container App name."
}

output "resource_group" {
  value = azurerm_resource_group.main.name
}

output "key_vault_name" {
  value = azurerm_key_vault.main.name
}

output "cert_name" {
  value = local.cert_name
}

output "curl_401_demo" {
  value = "curl -i -X POST https://${local.api_hostname}/enter   # -> 401 from the gateway without a token"
}
