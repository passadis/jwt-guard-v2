locals {
  log_analytics_reader_role_id = (
    "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/73c42c96-874c-492b-b04d-ab87d138a893"
  )
  privileged_monitoring_data_reader_role_id = (
    "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/dbc9c667-e97f-4491-aee6-90b9cf960190"
  )
}

# Hosted Agent server-side tracing is enabled only after Application Insights is
# connected at both the Foundry account and project scopes. The connection string
# remains in AzAPI's write-only sensitive_body and is never emitted as an output.
resource "azapi_resource" "foundry_account_app_insights_connection" {
  type      = "Microsoft.CognitiveServices/accounts/connections@2025-04-01-preview"
  name      = "${azapi_resource.foundry_account.name}-appinsights"
  parent_id = azapi_resource.foundry_account.id

  body = {
    properties = {
      category      = "AppInsights"
      target        = azurerm_application_insights.agent.id
      authType      = "ApiKey"
      isSharedToAll = true
      metadata = {
        ApiType    = "Azure"
        ResourceId = azurerm_application_insights.agent.id
      }
    }
  }

  sensitive_body = {
    properties = {
      credentials = {
        key = azurerm_application_insights.agent.connection_string
      }
    }
  }

  schema_validation_enabled = false
}

resource "azapi_resource" "foundry_project_app_insights_connection" {
  type      = "Microsoft.CognitiveServices/accounts/projects/connections@2025-04-01-preview"
  name      = azurerm_application_insights.agent.name
  parent_id = azapi_resource.foundry_project.id

  body = {
    properties = {
      category = "AppInsights"
      target   = azurerm_application_insights.agent.id
      authType = "ApiKey"
      # The project-level connection is intentionally private to this project.
      # Azure normalizes the live value to false; model it explicitly so
      # authorization-only plans do not resend unrelated connection settings.
      isSharedToAll = false
      metadata = {
        ApiType    = "Azure"
        ResourceId = azurerm_application_insights.agent.id
      }
    }
  }

  sensitive_body = {
    properties = {
      credentials = {
        key = azurerm_application_insights.agent.connection_string
      }
    }
  }

  schema_validation_enabled = false
}

# The Foundry project identity requires read access to surface traces and run
# trace-backed evaluations. Privileged Monitoring Data Reader is limited to this
# agent-owned Application Insights component because GenAI span content is
# treated as protected monitoring data.
resource "azurerm_role_assignment" "project_agent_app_insights_logs_reader" {
  scope              = azurerm_application_insights.agent.id
  role_definition_id = local.log_analytics_reader_role_id
  principal_id       = azapi_resource.foundry_project.output.identity.principalId
  principal_type     = "ServicePrincipal"
}

resource "azurerm_role_assignment" "project_agent_privileged_monitoring_reader" {
  scope              = azurerm_application_insights.agent.id
  role_definition_id = local.privileged_monitoring_data_reader_role_id
  principal_id       = azapi_resource.foundry_project.output.identity.principalId
  principal_type     = "ServicePrincipal"
}
