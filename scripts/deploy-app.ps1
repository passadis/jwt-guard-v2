<#
.SYNOPSIS
Builds and deploys SentinelApp and SentinelGate, then verifies images,
revisions, and health through their correct Application Gateway listeners.
#>
param(
  [string] $ResourceGroup,
  [string] $AcrName,
  [string] $AppName,
  [string] $GateAppName,
  [string] $UiHost,
  [string] $ApiHost,
  [string] $ApiClientId,
  [string] $Tag = (Get-Date -Format "yyyyMMdd-HHmmss"),
  [int] $RevisionTimeoutSeconds = 300,
  [switch] $AllowBootstrapCertificate,
  [switch] $FunctionsOnly
)

$ErrorActionPreference = "Stop"

function Assert-RevisionReady {
  param(
    [Parameter(Mandatory)] [psobject] $Revision,
    [Parameter(Mandatory)] [string] $ExpectedImage
  )
  $actualImage = [string]$Revision.properties.template.containers[0].image
  if ($actualImage -ne $ExpectedImage) { throw "Active revision image '$actualImage' does not match '$ExpectedImage'." }
  if ($Revision.properties.active -ne $true) { throw "The expected revision is not active." }
  if ([string]$Revision.properties.healthState -ne "Healthy") { throw "The expected revision is not healthy." }
  if ([string]$Revision.properties.provisioningState -notin @("Provisioned", "Succeeded")) {
    throw "The expected revision provisioning state is '$($Revision.properties.provisioningState)'."
  }
}

function Wait-ContainerAppRevision {
  param(
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $ExpectedImage
  )
  $deadline = (Get-Date).AddSeconds($RevisionTimeoutSeconds)
  do {
    $json = az containerapp revision list --resource-group $ResourceGroup --name $Name -o json
    if ($LASTEXITCODE -ne 0) { throw "Could not list revisions for $Name." }
    $revision = @($json | ConvertFrom-Json) |
      Where-Object { $_.properties.active -eq $true -and $_.properties.template.containers[0].image -eq $ExpectedImage } |
      Select-Object -First 1
    if ($revision -and
        $revision.properties.healthState -eq "Healthy" -and
        $revision.properties.provisioningState -in @("Provisioned", "Succeeded")) {
      Assert-RevisionReady -Revision $revision -ExpectedImage $ExpectedImage
      return
    }
    Start-Sleep -Seconds 5
  } while ((Get-Date) -lt $deadline)
  throw "Timed out waiting for healthy revision '$ExpectedImage' on $Name."
}

function Invoke-HealthRequest {
  param(
    [Parameter(Mandatory)] [string] $Uri,
    [hashtable] $Headers = @{}
  )
  $invoke = @{ Uri = $Uri; Headers = $Headers; TimeoutSec = 30; SkipHttpErrorCheck = $true }
  if ($AllowBootstrapCertificate) { $invoke.SkipCertificateCheck = $true }
  $response = Invoke-WebRequest @invoke
  if ([int]$response.StatusCode -ne 200) { throw "$Uri returned HTTP $($response.StatusCode)." }
  $response.Content
}

function Invoke-DualAppDeployment {
  foreach ($name in "ResourceGroup", "AcrName", "AppName", "GateAppName", "UiHost", "ApiHost", "ApiClientId") {
    if (-not (Get-Variable -Name $name -ValueOnly)) { throw "-$name is required." }
  }

  $appImage = "$AcrName.azurecr.io/sentinel-app:$Tag"
  $gateImage = "$AcrName.azurecr.io/sentinel-gate:$Tag"
  $appSource = Join-Path $PSScriptRoot "..\src\SentinelApp"
  $gateSource = Join-Path $PSScriptRoot "..\src\SentinelGate"

  az acr build --registry $AcrName --image "sentinel-app:$Tag" $appSource
  if ($LASTEXITCODE -ne 0) { throw "SentinelApp ACR build failed." }
  az acr build --registry $AcrName --image "sentinel-gate:$Tag" $gateSource
  if ($LASTEXITCODE -ne 0) { throw "SentinelGate ACR build failed." }

  az containerapp update --name $AppName --resource-group $ResourceGroup --image $appImage --output none
  if ($LASTEXITCODE -ne 0) { throw "SentinelApp image update failed." }
  az containerapp update --name $GateAppName --resource-group $ResourceGroup --image $gateImage --output none
  if ($LASTEXITCODE -ne 0) { throw "SentinelGate image update failed." }

  Wait-ContainerAppRevision -Name $AppName -ExpectedImage $appImage
  Wait-ContainerAppRevision -Name $GateAppName -ExpectedImage $gateImage

  $uiHealth = Invoke-HealthRequest -Uri "https://$UiHost/healthz"
  if (($uiHealth | ConvertFrom-Json).status -ne "healthy") { throw "SentinelApp health payload is invalid." }

  $callerToken = az account get-access-token --scope "api://$ApiClientId/.default" --query accessToken -o tsv
  if ($LASTEXITCODE -ne 0 -or -not $callerToken) { throw "Could not acquire the protected health-check token." }
  try {
    $gateHealth = Invoke-HealthRequest -Uri "https://$ApiHost/healthz" -Headers @{ Authorization = "Bearer $callerToken" }
    if (($gateHealth | ConvertFrom-Json).service -ne "SentinelGate") { throw "SentinelGate health payload is invalid." }
  }
  finally {
    $callerToken = $null
  }

  Write-Host "Both images, revisions, and routed health endpoints are verified." -ForegroundColor Green
}

if (-not $FunctionsOnly) {
  Invoke-DualAppDeployment
}
