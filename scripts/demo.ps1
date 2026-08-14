<#
.SYNOPSIS
Runs an asserting JWT Sentinel protected-listener matrix against SentinelGate.

.DESCRIPTION
TLS validation is enabled by default. Use -AllowBootstrapCertificate only
during the explicitly understood self-signed bootstrap phase. Any wrong HTTP
status, DNS/TLS/timeout/connection failure, invalid SentinelGate response, or
malformed injected identity terminates the script with a non-zero exit code.
#>
param(
  [string] $ApiHost,
  [string] $TenantId,
  [string] $ApiClientId,
  [string] $DaemonClientId,
  [string] $DaemonSecret,
  [switch] $AllowBootstrapCertificate,
  [switch] $FunctionsOnly
)

$ErrorActionPreference = "Stop"

function Invoke-GateRequest {
  param(
    [Parameter(Mandatory)] [string] $Uri,
    [string] $Token,
    [switch] $SkipCertificateValidation,
    [int] $TimeoutSeconds = 90
  )

  $headers = @{}
  if ($Token) { $headers.Authorization = "Bearer $Token" }
  $invoke = @{
    Method             = "Post"
    Uri                = $Uri
    Headers            = $headers
    TimeoutSec         = $TimeoutSeconds
    SkipHttpErrorCheck = $true
  }
  if ($SkipCertificateValidation) { $invoke.SkipCertificateCheck = $true }

  try {
    $response = Invoke-WebRequest @invoke
    return [pscustomobject]@{
      TransportSucceeded = $true
      StatusCode          = [int]$response.StatusCode
      Body                = $response.Content
      FailureKind         = $null
    }
  }
  catch {
    $exception = $_.Exception
    $types = @()
    $messages = @()
    while ($exception) {
      $types += $exception.GetType().FullName
      $messages += $exception.Message
      $exception = $exception.InnerException
    }
    $joined = ($types + $messages) -join " "
    $kind = if ($joined -match "AuthenticationException|certificate|SSL|TLS") {
      "tls_failure"
    }
    elseif ($joined -match "HostNotFound|NameResolution|No such host|could not be resolved") {
      "dns_failure"
    }
    elseif ($joined -match "TaskCanceledException|timed out|timeout") {
      "timeout"
    }
    else {
      "connection_failure"
    }

    return [pscustomobject]@{
      TransportSucceeded = $false
      StatusCode          = $null
      Body                = $null
      FailureKind         = $kind
    }
  }
}

function Assert-GateScenarioResult {
  param(
    [Parameter(Mandatory)] [string] $Scenario,
    [Parameter(Mandatory)] [int] $ExpectedStatus,
    [Parameter(Mandatory)] [psobject] $Result,
    [switch] $RequireSentinelGate
  )

  if (-not $Result.TransportSucceeded) {
    throw "$Scenario failed before an HTTP response: $($Result.FailureKind)."
  }
  if ($Result.StatusCode -ne $ExpectedStatus) {
    throw "$Scenario returned HTTP $($Result.StatusCode); expected $ExpectedStatus."
  }

  if ($RequireSentinelGate) {
    try { $payload = $Result.Body | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "$Scenario returned HTTP 200 without a valid JSON SentinelGate response." }

    $tenant = [Guid]::Empty
    $object = [Guid]::Empty
    $tenantValid = [Guid]::TryParseExact([string]$payload.tenantId, "D", [ref]$tenant)
    $objectValid = [Guid]::TryParseExact([string]$payload.objectId, "D", [ref]$object)
    if ($payload.service -ne "SentinelGate" -or
        $payload.allowed -ne $true -or
        $payload.gatewayValidated -ne $true -or
        $payload.routingContextConsistent -ne $true -or
        -not $tenantValid -or $tenant -eq [Guid]::Empty -or
        -not $objectValid -or $object -eq [Guid]::Empty) {
      throw "$Scenario did not return a well-formed validated SentinelGate identity response."
    }
  }
}

function Get-DaemonToken {
  param([Parameter(Mandatory)] [string] $Scope)
  $body = @{
    client_id     = $DaemonClientId
    client_secret = $DaemonSecret
    grant_type    = "client_credentials"
    scope         = $Scope
  }
  $tokenRequest = @{
    Method = "Post"
    Uri    = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
    Body   = $body
  }
  $response = Invoke-RestMethod @tokenRequest
  if (-not $response.access_token) { throw "Token acquisition returned no access token." }
  $response.access_token
}

function Invoke-GateMatrix {
  foreach ($name in "ApiHost", "TenantId", "ApiClientId", "DaemonClientId", "DaemonSecret") {
    if (-not (Get-Variable -Name $name -ValueOnly)) { throw "-$name is required." }
  }

  $gate = "https://$ApiHost/enter"
  $invokeArgs = @{ Uri = $gate; SkipCertificateValidation = $AllowBootstrapCertificate }

  $missing = Invoke-GateRequest @invokeArgs
  Assert-GateScenarioResult -Scenario "Missing token" -ExpectedStatus 401 -Result $missing

  $graphToken = Get-DaemonToken "https://graph.microsoft.com/.default"
  $wrong = Invoke-GateRequest @invokeArgs -Token $graphToken
  Assert-GateScenarioResult -Scenario "Wrong audience" -ExpectedStatus 401 -Result $wrong

  $goodToken = Get-DaemonToken "api://$ApiClientId/.default"
  $valid = Invoke-GateRequest @invokeArgs -Token $goodToken
  Assert-GateScenarioResult -Scenario "Correct API token" -ExpectedStatus 200 -Result $valid -RequireSentinelGate

  $parts = $goodToken.Split(".")
  $characters = $parts[1].ToCharArray()
  $index = [int]($characters.Length / 2)
  $characters[$index] = if ($characters[$index] -eq 'A') { 'B' } else { 'A' }
  $parts[1] = -join $characters
  $tampered = Invoke-GateRequest @invokeArgs -Token ($parts -join ".")
  Assert-GateScenarioResult -Scenario "Tampered token" -ExpectedStatus 401 -Result $tampered

  Write-Host "JWT Sentinel matrix passed: 401, 401, 200 SentinelGate, 401." -ForegroundColor Green
  if ($AllowBootstrapCertificate) {
    Write-Warning "TLS trust was bypassed explicitly. This is not final certificate acceptance."
  }
}

if (-not $FunctionsOnly) {
  Invoke-GateMatrix
}
