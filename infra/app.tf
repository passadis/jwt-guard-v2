resource "azurerm_container_registry" "main" {
  name                = "acr${var.prefix}${random_string.kv.result}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "Basic"
}

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${var.prefix}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  # Azure normalizes Consumption environments to an explicit workload
  # profile. Model that server-side value so later plans do not propose
  # removing the profile as unrelated drift.
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
    minimum_count         = 0
    maximum_count         = 0
  }
}

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${var.prefix}-app"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
}

# SentinelGate has a separate identity with ACR pull only. It receives no
# Foundry, ARM Reader, Log Analytics, or daemon-secret permissions.
resource "azurerm_user_assigned_identity" "gate" {
  name                = "id-${var.prefix}-gate"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
}

resource "azurerm_role_assignment" "app_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_role_assignment" "gate_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.gate.principal_id
}

resource "azurerm_role_assignment" "app_openai" {
  scope                = azurerm_ai_services.main.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Agent tool: read the gateway's live entraJWTValidationConfigs via ARM.
resource "azurerm_role_assignment" "app_reader_rg" {
  scope                = azurerm_resource_group.main.id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Agent tool: KQL over the gateway access logs.
resource "azurerm_role_assignment" "app_law_reader" {
  scope                = azurerm_log_analytics_workspace.main.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_container_app" "main" {
  name                         = "ca-${var.prefix}"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.main.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  secret {
    name  = "daemon-secret"
    value = azuread_application_password.daemon.value
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    # Only the gateway may reach the app directly. With a NAT gateway on the
    # AppGW subnet, gateway-to-internet-backend traffic egresses via the NAT
    # public IP (not the frontend IP), so both are allowed.
    ip_security_restriction {
      name             = "appgw-frontend"
      action           = "Allow"
      ip_address_range = "${azurerm_public_ip.appgw.ip_address}/32"
    }
    ip_security_restriction {
      name             = "appgw-nat-egress"
      action           = "Allow"
      ip_address_range = "${azurerm_public_ip.nat.ip_address}/32"
    }
  }

  template {
    min_replicas = 1
    # Agent sessions are in memory and user-bound. Keep one replica until a
    # distributed session store is deliberately introduced.
    max_replicas = 1

    container {
      name   = "sentinel"
      image  = var.container_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "AZURE_CLIENT_ID" # DefaultAzureCredential -> UAMI
        value = azurerm_user_assigned_identity.app.client_id
      }
      env {
        name  = "AZURE_OPENAI_ENDPOINT"
        value = azurerm_ai_services.main.endpoint
      }
      env {
        name  = "MODEL_DEPLOYMENT"
        value = var.model_deployment_name
      }
      env {
        name  = "TENANT_ID"
        value = data.azurerm_client_config.current.tenant_id
      }
      env {
        name  = "API_CLIENT_ID"
        value = azuread_application.api.client_id
      }
      env {
        name  = "SPA_CLIENT_ID"
        value = azuread_application.spa.client_id
      }
      env {
        name  = "DAEMON_CLIENT_ID"
        value = azuread_application.daemon.client_id
      }
      env {
        name        = "DAEMON_CLIENT_SECRET"
        secret_name = "daemon-secret"
      }
      env {
        name  = "GATEWAY_RESOURCE_ID"
        value = "${azurerm_resource_group.main.id}/providers/Microsoft.Network/applicationGateways/agw-${var.prefix}"
      }
      env {
        name  = "LAW_WORKSPACE_GUID"
        value = azurerm_log_analytics_workspace.main.workspace_id
      }
      env {
        name  = "GATE_API_BASE"
        value = "https://${local.api_hostname}"
      }
      env {
        name  = "UI_BASE"
        value = "https://${local.ui_hostname}"
      }
      env {
        # Gate 2 deploys the candidate in rollback mode. Later mode changes are
        # separate reviewed Container App revisions.
        name  = "AGENT_MODE"
        value = var.agent_mode
      }
      dynamic "env" {
        for_each = var.hosted_agent_principal_id == null ? [] : [var.hosted_agent_principal_id]
        content {
          name  = "HOSTED_AGENT_PRINCIPAL_ID"
          value = env.value
        }
      }
      dynamic "env" {
        for_each = var.hosted_agent_responses_endpoint == null ? [] : [var.hosted_agent_responses_endpoint]
        content {
          name  = "HOSTED_AGENT_RESPONSES_ENDPOINT"
          value = env.value
        }
      }
      dynamic "env" {
        for_each = var.hosted_agent_version == null ? [] : [var.hosted_agent_version]
        content {
          name  = "HOSTED_AGENT_VERSION"
          value = tostring(env.value)
        }
      }
      dynamic "env" {
        for_each = length(var.hosted_shadow_tester_object_ids) == 0 ? [] : [join(",", sort(tolist(var.hosted_shadow_tester_object_ids)))]
        content {
          name  = "HOSTED_SHADOW_TESTER_OBJECT_IDS"
          value = env.value
        }
      }
    }
  }

  lifecycle {
    # scripts/deploy-app.ps1 owns the image after first deploy.
    ignore_changes = [template[0].container[0].image]

    precondition {
      condition     = (var.hosted_agent_responses_endpoint == null) == (var.hosted_agent_version == null)
      error_message = "hosted_agent_responses_endpoint and hosted_agent_version must either both be null or both be configured."
    }

    precondition {
      condition     = var.agent_mode == "Embedded" || (var.hosted_agent_responses_endpoint != null && var.hosted_agent_version != null)
      error_message = "Hosted and HostedShadow modes require the paired Hosted Agent endpoint and version."
    }

    precondition {
      condition     = var.agent_mode != "HostedShadow" || length(var.hosted_shadow_tester_object_ids) > 0
      error_message = "HostedShadow mode requires at least one explicitly approved tester object ID."
    }
  }
}

resource "azurerm_container_app" "gate" {
  name                         = "ca-${var.prefix}-gate"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.gate.id]
  }

  registry {
    server   = azurerm_container_registry.main.login_server
    identity = azurerm_user_assigned_identity.gate.id
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    ip_security_restriction {
      name             = "appgw-frontend"
      action           = "Allow"
      ip_address_range = "${azurerm_public_ip.appgw.ip_address}/32"
    }
    ip_security_restriction {
      name             = "appgw-nat-egress"
      action           = "Allow"
      ip_address_range = "${azurerm_public_ip.nat.ip_address}/32"
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "sentinel-gate"
      image  = var.gate_container_image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "PROTECTED_HOST"
        value = local.api_hostname
      }
      env {
        name  = "EXPECTED_TENANT_ID"
        value = data.azurerm_client_config.current.tenant_id
      }
    }
  }

  lifecycle {
    # scripts/deploy-app.ps1 owns the SentinelGate image after first deploy.
    ignore_changes = [template[0].container[0].image]
  }
}
