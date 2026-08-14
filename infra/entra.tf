# Three app registrations:
#   api    — the protected resource. Its client ID + api:// URI are what the
#            gateway's JWT validation config checks as audience.
#   spa    — the browser client (MSAL.js, auth-code + PKCE).
#   daemon — confidential client used server-side by the "simulate" tool to
#            mint valid / wrong-audience tokens via client credentials.

resource "random_uuid" "api_scope" {}
resource "random_uuid" "api_approle" {}

locals {
  # Stable and reviewable: this ID is part of the API permission contract.
  agent_scenario_execute_role_id = "727d25a8-f526-4974-b8c1-a682e57d5a45"
}

resource "azuread_application" "api" {
  display_name     = "${var.prefix}-api"
  sign_in_audience = "AzureADMyOrg"

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = random_uuid.api_scope.result
      value                      = "access_as_user"
      admin_consent_display_name = "Access JWT Sentinel"
      admin_consent_description  = "Allows the app to call JWT Sentinel on behalf of the signed-in user."
      user_consent_display_name  = "Access JWT Sentinel"
      user_consent_description   = "Allows the app to call JWT Sentinel on your behalf."
      type                       = "User"
      enabled                    = true
    }
  }

  app_role {
    id                   = random_uuid.api_approle.result
    value                = "Gateway.Simulate"
    display_name         = "Gateway Simulate"
    description          = "Allows daemon clients to obtain tokens for gate simulations."
    allowed_member_types = ["Application"]
    enabled              = true
  }

  app_role {
    id                   = local.agent_scenario_execute_role_id
    value                = "agent.scenario.execute"
    display_name         = "Agent Scenario Execute"
    description          = "Allows only the approved Hosted Agent runtime to call JWT Sentinel's fixed evidence broker."
    allowed_member_types = ["Application"]
    enabled              = true
  }

  # The api:// URI is owned by azuread_application_identifier_uri below;
  # without this, every apply of this resource resets identifierUris to []
  # and clients get AADSTS500011 for api://<clientId> scopes.
  lifecycle {
    ignore_changes = [identifier_uris]
  }
}

resource "azuread_application_identifier_uri" "api" {
  application_id = azuread_application.api.id
  identifier_uri = "api://${azuread_application.api.client_id}"
}

# Pre-authorize the Azure CLI so `az account get-access-token
# --scope api://<clientId>/.default` mints user tokens for the gate without a
# consent prompt — the easiest way to get paste-able demo tokens.
resource "azuread_application_pre_authorized" "azure_cli" {
  application_id       = azuread_application.api.id
  authorized_client_id = "04b07795-8ddb-461a-bbee-02f9e1bf7b46" # Azure CLI
  permission_ids       = [random_uuid.api_scope.result]
}

resource "azuread_service_principal" "api" {
  client_id = azuread_application.api.client_id
}

resource "azuread_application" "spa" {
  display_name     = "${var.prefix}-spa"
  sign_in_audience = "AzureADMyOrg"

  single_page_application {
    redirect_uris = [
      "https://${local.ui_hostname}/",
    ]
  }

  required_resource_access {
    resource_app_id = azuread_application.api.client_id

    resource_access {
      id   = random_uuid.api_scope.result
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "spa" {
  client_id = azuread_application.spa.client_id
}

# Tenant-wide admin consent so demo users never hit a consent prompt.
resource "azuread_service_principal_delegated_permission_grant" "spa_to_api" {
  service_principal_object_id          = azuread_service_principal.spa.object_id
  resource_service_principal_object_id = azuread_service_principal.api.object_id
  claim_values                         = ["access_as_user"]
}

resource "azuread_application" "daemon" {
  display_name     = "${var.prefix}-daemon"
  sign_in_audience = "AzureADMyOrg"

  required_resource_access {
    resource_app_id = azuread_application.api.client_id

    resource_access {
      id   = random_uuid.api_approle.result
      type = "Role"
    }
  }
}

resource "azuread_service_principal" "daemon" {
  client_id = azuread_application.daemon.client_id
}

resource "azuread_application_password" "daemon" {
  application_id = azuread_application.daemon.id
  display_name   = "sentinel-simulator"
  end_date       = timeadd(timestamp(), "2160h") # ~90 days; demo-grade

  lifecycle {
    ignore_changes = [end_date]
  }
}

# Admin-consented app role grant — required for client-credentials tokens
# against a custom API audience.
resource "azuread_app_role_assignment" "daemon_simulate" {
  app_role_id         = random_uuid.api_approle.result
  principal_object_id = azuread_service_principal.daemon.object_id
  resource_object_id  = azuread_service_principal.api.object_id
}

# Gate 2 broker grant. This is an Entra application permission, not Azure RBAC.
# The exact hosted runtime principal is supplied explicitly during the isolated
# existing-stack plan; null keeps the assignment absent.
resource "azuread_app_role_assignment" "hosted_agent_broker" {
  count = var.hosted_agent_principal_id == null ? 0 : 1

  app_role_id         = local.agent_scenario_execute_role_id
  principal_object_id = var.hosted_agent_principal_id
  resource_object_id  = azuread_service_principal.api.object_id
}
