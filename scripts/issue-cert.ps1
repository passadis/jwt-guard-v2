<#
.SYNOPSIS
Replaces the bootstrap self-signed certificate with a trusted Let's Encrypt
certificate covering both Sentinel hostnames, using Posh-ACME with the Azure
DNS plugin (DNS-01 challenge).

The certificate is imported into Key Vault under the SAME certificate name the
gateway listener references (unversioned secret URI), so Application Gateway
picks up the new version automatically — no Terraform change needed.

.PREREQS
- The DNS zone for -Domain hosted in Azure DNS, and your az login identity able
  to create TXT records in it (DNS Zone Contributor).
- Az PowerShell or az CLI login (Get-AzAccessToken is used by the plugin).

.EXAMPLE
./issue-cert.ps1 -Domain contoso.com -UiHost sentinel.contoso.com `
  -ApiHost sentinel-api.contoso.com -KeyVaultName kv-jwtsent-ab12c `
  -CertName jwtsent-tls -DnsZoneSubscriptionId <sub-guid>
#>
param(
  [Parameter(Mandatory)] [string] $Domain,
  [Parameter(Mandatory)] [string] $UiHost,
  [Parameter(Mandatory)] [string] $ApiHost,
  [Parameter(Mandatory)] [string] $KeyVaultName,
  [Parameter(Mandatory)] [string] $CertName,
  [Parameter(Mandatory)] [string] $DnsZoneSubscriptionId,
  [string] $Contact = "admin@$Domain",
  [ValidateSet("Staging", "Production")] [string] $AcmeEnvironment = "Staging",
  [Parameter(Mandatory)] [version] $PoshAcmeVersion,
  [switch] $InstallPinnedModule
)

$ErrorActionPreference = "Stop"

$module = Get-Module -ListAvailable Posh-ACME |
  Where-Object Version -eq $PoshAcmeVersion |
  Select-Object -First 1
if (-not $module -and $InstallPinnedModule) {
  Install-Module Posh-ACME -RequiredVersion $PoshAcmeVersion -Scope CurrentUser -Force
  $module = Get-Module -ListAvailable Posh-ACME |
    Where-Object Version -eq $PoshAcmeVersion |
    Select-Object -First 1
}
if (-not $module) {
  throw "Posh-ACME $PoshAcmeVersion is required. Install it deliberately or pass -InstallPinnedModule."
}
Import-Module $module.Path

if ($AcmeEnvironment -eq "Production") {
  Write-Warning "Production certificate issuance requires the runbook's explicit Approval Gate B."
}
Set-PAServer $(if ($AcmeEnvironment -eq "Staging") { "LE_STAGE" } else { "LE_PROD" })

# Azure DNS plugin authenticates with an access token from the current session
# (az CLI first, Az PowerShell as fallback).
$token = az account get-access-token --resource "https://management.core.windows.net/" --query accessToken -o tsv
if (-not $token) { $token = (Get-AzAccessToken -ResourceUrl "https://management.core.windows.net/").Token }
$pfxPass = [Guid]::NewGuid().ToString("N")

$cert = New-PACertificate $UiHost, $ApiHost `
  -AcceptTOS -Contact $Contact `
  -Plugin Azure -PluginArgs @{ AZSubscriptionId = $DnsZoneSubscriptionId; AZAccessToken = $token } `
  -PfxPass $pfxPass -Force

Write-Host "Certificate issued: $($cert.Subject) -> importing into Key Vault '$KeyVaultName' as '$CertName'"

az keyvault certificate import `
  --vault-name $KeyVaultName `
  --name $CertName `
  --file $cert.PfxFullChain `
  --password $pfxPass | Out-Null

Write-Host "Done. Allow normal unversioned-secret pickup whenever possible."
Write-Host "Do not restart Application Gateway casually. A restart requires Approval Gate C,"
Write-Host "an incremented and reviewed gateway_config_generation plan, the full AzAPI configuration"
Write-Host "push, live JWT-rule verification, and the complete protected-listener test matrix."
