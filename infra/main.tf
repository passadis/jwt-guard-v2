data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "main" {
  name     = local.rg_name
  location = var.location
}

resource "azurerm_virtual_network" "main" {
  name                = "vnet-${var.prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  address_space       = ["10.60.0.0/16"]
}

resource "azurerm_subnet" "appgw" {
  name                 = "snet-appgw"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.60.1.0/24"]
}

# JWT validation needs outbound 443 to login.microsoftonline.com from the
# gateway subnet. New subnets have no default outbound internet access
# (retired Sept 2025), so without this NAT gateway the JWT-validating
# listener hangs while trying to fetch Entra's signing keys.
resource "azurerm_public_ip" "nat" {
  name                = "pip-${var.prefix}-nat"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  allocation_method   = "Static"
  sku                 = "Standard"
}

resource "azurerm_nat_gateway" "appgw" {
  name                = "nat-${var.prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku_name            = "Standard"
}

resource "azurerm_nat_gateway_public_ip_association" "appgw" {
  nat_gateway_id       = azurerm_nat_gateway.appgw.id
  public_ip_address_id = azurerm_public_ip.nat.id
}

resource "azurerm_subnet_nat_gateway_association" "appgw" {
  subnet_id      = azurerm_subnet.appgw.id
  nat_gateway_id = azurerm_nat_gateway.appgw.id
}

resource "azurerm_public_ip" "appgw" {
  name                = "pip-${var.prefix}-appgw"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  allocation_method   = "Static"
  sku                 = "Standard"
  domain_name_label   = "${var.prefix}-${substr(md5(azurerm_resource_group.main.id), 0, 6)}"
}

resource "azurerm_log_analytics_workspace" "main" {
  name                = "law-${var.prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

# Optional convenience: A records when the zone is hosted in Azure DNS.
resource "azurerm_dns_a_record" "ui" {
  provider            = azurerm.dns
  count               = var.dns_zone_name == "" ? 0 : 1
  name                = var.ui_subdomain
  zone_name           = var.dns_zone_name
  resource_group_name = var.dns_zone_resource_group
  ttl                 = 300
  records             = [azurerm_public_ip.appgw.ip_address]
}

resource "azurerm_dns_a_record" "api" {
  provider            = azurerm.dns
  count               = var.dns_zone_name == "" ? 0 : 1
  name                = var.api_subdomain
  zone_name           = var.dns_zone_name
  resource_group_name = var.dns_zone_resource_group
  ttl                 = 300
  records             = [azurerm_public_ip.appgw.ip_address]
}
