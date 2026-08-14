# Application Gateway managed through AzAPI: the JWT validation properties
# (entraJWTValidationConfigs / entraJWTValidationConfig on routing rules) are
# preview-only and exist from API version 2025-03-01; the azurerm provider
# does not expose them yet.
#
# Layout:
#   listener ui-https  (sentinel.<domain>)      -> SentinelApp only, without gateway JWT validation
#   listener api-https (sentinel-api.<domain>)  -> SentinelGate only, with JWT validation action Deny
#   listener http-80                            -> permanent redirect to ui-https
# The two listeners never share a backend pool.

locals {
  agw_name = "agw-${var.prefix}"
  agw_id   = "${azurerm_resource_group.main.id}/providers/Microsoft.Network/applicationGateways/${local.agw_name}"

  sub = {
    gwip     = "${local.agw_id}/gatewayIPConfigurations"
    feip     = "${local.agw_id}/frontendIPConfigurations"
    feport   = "${local.agw_id}/frontendPorts"
    ssl      = "${local.agw_id}/sslCertificates"
    listener = "${local.agw_id}/httpListeners"
    pool     = "${local.agw_id}/backendAddressPools"
    settings = "${local.agw_id}/backendHttpSettingsCollection"
    probe    = "${local.agw_id}/probes"
    redirect = "${local.agw_id}/redirectConfigurations"
    jwt      = "${local.agw_id}/entraJWTValidationConfigs"
  }
}

resource "azapi_resource" "appgw" {
  type      = "Microsoft.Network/applicationGateways@2025-05-01"
  name      = local.agw_name
  location  = azurerm_resource_group.main.location
  parent_id = azurerm_resource_group.main.id

  schema_validation_enabled = false

  # Incrementing this reviewed generation changes a visible tag and causes
  # AzAPI to resubmit this resource's complete body through API 2025-05-01.
  # It is an opt-in recovery action after an approved gateway restart and is
  # not changed during normal operation.
  tags = {
    jwt-sentinel-config-generation = tostring(var.gateway_config_generation)
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.appgw.id]
  }

  lifecycle {
    # A generation increment must be an in-place update. If any provider or
    # configuration change would replace the gateway, fail planning instead.
    prevent_destroy = true
  }

  body = {
    properties = {
      sku = {
        name = "Standard_v2"
        tier = "Standard_v2"
      }
      autoscaleConfiguration = {
        minCapacity = 1
        maxCapacity = 2
      }

      gatewayIPConfigurations = [{
        name = "gwip"
        properties = {
          subnet = { id = azurerm_subnet.appgw.id }
        }
      }]

      frontendIPConfigurations = [{
        name = "public"
        properties = {
          publicIPAddress = { id = azurerm_public_ip.appgw.id }
        }
      }]

      frontendPorts = [
        { name = "port443", properties = { port = 443 } },
        { name = "port80", properties = { port = 80 } },
      ]

      sslCertificates = [{
        name = "sentinel-tls"
        properties = {
          keyVaultSecretId = local.cert_secret_unversioned
        }
      }]

      backendAddressPools = [
        {
          name = "sentinel-app-pool"
          properties = {
            backendAddresses = [{ fqdn = azurerm_container_app.main.ingress[0].fqdn }]
          }
        },
        {
          name = "sentinel-gate-pool"
          properties = {
            backendAddresses = [{ fqdn = azurerm_container_app.gate.ingress[0].fqdn }]
          }
        },
      ]

      probes = [
        {
          name = "sentinel-app-probe"
          properties = {
            protocol                            = "Https"
            path                                = "/healthz"
            interval                            = 30
            timeout                             = 30
            unhealthyThreshold                  = 3
            pickHostNameFromBackendHttpSettings = true
            match                               = { statusCodes = ["200-399"] }
          }
        },
        {
          name = "sentinel-gate-probe"
          properties = {
            protocol                            = "Https"
            path                                = "/healthz"
            interval                            = 30
            timeout                             = 30
            unhealthyThreshold                  = 3
            pickHostNameFromBackendHttpSettings = true
            match                               = { statusCodes = ["200-399"] }
          }
        },
      ]

      backendHttpSettingsCollection = [
        {
          name = "sentinel-app-https"
          properties = {
            port                = 443
            protocol            = "Https"
            cookieBasedAffinity = "Disabled"
            requestTimeout      = 120
            # Keep the SentinelApp ACA FQDN as backend Host and TLS/SNI name.
            pickHostNameFromBackendAddress = true
            probe                          = { id = "${local.sub.probe}/sentinel-app-probe" }
          }
        },
        {
          name = "sentinel-gate-https"
          properties = {
            port                = 443
            protocol            = "Https"
            cookieBasedAffinity = "Disabled"
            requestTimeout      = 120
            # Keep the SentinelGate ACA FQDN as backend Host and TLS/SNI name.
            # x-original-host, when inspected by SentinelGate, is routing context only.
            pickHostNameFromBackendAddress = true
            probe                          = { id = "${local.sub.probe}/sentinel-gate-probe" }
          }
        },
      ]

      httpListeners = [
        {
          name = "ui-https"
          properties = {
            frontendIPConfiguration     = { id = "${local.sub.feip}/public" }
            frontendPort                = { id = "${local.sub.feport}/port443" }
            protocol                    = "Https"
            hostName                    = local.ui_hostname
            sslCertificate              = { id = "${local.sub.ssl}/sentinel-tls" }
            requireServerNameIndication = true
          }
        },
        {
          name = "api-https"
          properties = {
            frontendIPConfiguration     = { id = "${local.sub.feip}/public" }
            frontendPort                = { id = "${local.sub.feport}/port443" }
            protocol                    = "Https"
            hostName                    = local.api_hostname
            sslCertificate              = { id = "${local.sub.ssl}/sentinel-tls" }
            requireServerNameIndication = true
          }
        },
        {
          name = "http-80"
          properties = {
            frontendIPConfiguration = { id = "${local.sub.feip}/public" }
            frontendPort            = { id = "${local.sub.feport}/port80" }
            protocol                = "Http"
            hostName                = local.ui_hostname
          }
        },
      ]

      redirectConfigurations = [{
        name = "to-https"
        properties = {
          redirectType       = "Permanent"
          targetListener     = { id = "${local.sub.listener}/ui-https" }
          includePath        = true
          includeQueryString = true
        }
      }]

      # ---- The preview feature ----
      entraJWTValidationConfigs = [{
        name = "jwt-deny"
        properties = {
          tenantId = data.azurerm_client_config.current.tenant_id
          clientId = azuread_application.api.client_id
          audiences = [
            "api://${azuread_application.api.client_id}",
            azuread_application.api.client_id,
          ]
          unAuthorizedRequestAction = "Deny"
        }
      }]

      requestRoutingRules = [
        {
          name = "ui-rule"
          properties = {
            ruleType            = "Basic"
            priority            = 100
            httpListener        = { id = "${local.sub.listener}/ui-https" }
            backendAddressPool  = { id = "${local.sub.pool}/sentinel-app-pool" }
            backendHttpSettings = { id = "${local.sub.settings}/sentinel-app-https" }
          }
        },
        {
          name = "api-rule"
          properties = {
            ruleType            = "Basic"
            priority            = 110
            httpListener        = { id = "${local.sub.listener}/api-https" }
            backendAddressPool  = { id = "${local.sub.pool}/sentinel-gate-pool" }
            backendHttpSettings = { id = "${local.sub.settings}/sentinel-gate-https" }
            entraJWTValidationConfig = {
              id = "${local.sub.jwt}/jwt-deny"
            }
          }
        },
        {
          name = "redirect-rule"
          properties = {
            ruleType              = "Basic"
            priority              = 120
            httpListener          = { id = "${local.sub.listener}/http-80" }
            redirectConfiguration = { id = "${local.sub.redirect}/to-https" }
          }
        },
      ]
    }
  }

  depends_on = [
    azurerm_nat_gateway_public_ip_association.appgw,
    azurerm_subnet_nat_gateway_association.appgw,
    azurerm_role_assignment.kv_secrets_appgw,
    azurerm_key_vault_certificate.bootstrap,
  ]
}

# Access logs -> Log Analytics; the agent's "why was I blocked" tool queries
# AGWAccessLogs / AzureDiagnostics for 4xx with token failure details.
resource "azurerm_monitor_diagnostic_setting" "appgw" {
  name                           = "diag-${var.prefix}"
  target_resource_id             = azapi_resource.appgw.id
  log_analytics_workspace_id     = azurerm_log_analytics_workspace.main.id
  log_analytics_destination_type = "Dedicated"

  enabled_log {
    category = "ApplicationGatewayAccessLog"
  }
  enabled_log {
    category = "ApplicationGatewayFirewallLog"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}
