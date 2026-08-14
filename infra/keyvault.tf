# Key Vault holds the TLS certificate for the Application Gateway listeners.
# Terraform bootstraps a Key Vault-generated self-signed cert covering both
# hostnames so the stack deploys with zero manual steps. Run
# scripts/issue-cert.ps1 afterwards to replace it (same cert name, new
# version) with a trusted Let's Encrypt certificate — the gateway follows the
# unversioned secret URI automatically.

resource "random_string" "kv" {
  length  = 5
  lower   = true
  upper   = false
  special = false
  numeric = true
}

resource "azurerm_key_vault" "main" {
  name                       = "kv-${var.prefix}-${random_string.kv.result}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = false
  soft_delete_retention_days = 7
}

resource "azurerm_role_assignment" "kv_admin_current" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Administrator"
  principal_id         = data.azurerm_client_config.current.object_id
}

# User-assigned identity the gateway uses to read the certificate secret.
resource "azurerm_user_assigned_identity" "appgw" {
  name                = "id-${var.prefix}-appgw"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
}

resource "azurerm_role_assignment" "kv_secrets_appgw" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.appgw.principal_id
}

resource "azurerm_key_vault_certificate" "bootstrap" {
  name         = local.cert_name
  key_vault_id = azurerm_key_vault.main.id

  certificate_policy {
    issuer_parameters {
      name = "Self"
    }
    key_properties {
      exportable = true
      key_size   = 2048
      key_type   = "RSA"
      reuse_key  = false
    }
    secret_properties {
      content_type = "application/x-pkcs12"
    }
    x509_certificate_properties {
      subject            = "CN=${local.ui_hostname}"
      validity_in_months = 12
      key_usage          = ["digitalSignature", "keyEncipherment"]

      subject_alternative_names {
        dns_names = [local.ui_hostname, local.api_hostname]
      }
    }
  }

  # issue-cert.ps1 imports newer versions of this certificate out of band;
  # never let Terraform roll them back to the self-signed bootstrap.
  lifecycle {
    ignore_changes = [certificate_policy]
  }

  depends_on = [azurerm_role_assignment.kv_admin_current]
}

locals {
  cert_secret_unversioned = "https://${azurerm_key_vault.main.name}.vault.azure.net/secrets/${local.cert_name}"
}
