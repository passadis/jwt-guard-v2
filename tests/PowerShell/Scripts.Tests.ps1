$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
. (Join-Path $root "scripts\demo.ps1") -FunctionsOnly
. (Join-Path $root "scripts\deploy-app.ps1") -FunctionsOnly

function Test-ScriptThrows {
  param([Parameter(Mandatory)] [scriptblock] $Action)
  try {
    & $Action
    return $false
  }
  catch {
    return $true
  }
}

Describe "JWT Sentinel asserting smoke harness" {
  It "accepts only the expected 401 denial" {
    $result = [pscustomobject]@{ TransportSucceeded = $true; StatusCode = 401; Body = ""; FailureKind = $null }
    Test-ScriptThrows { Assert-GateScenarioResult -Scenario "missing" -ExpectedStatus 401 -Result $result } | Should Be $false
  }

  foreach ($failureKind in "dns_failure", "tls_failure", "timeout", "connection_failure") {
    It "fails $failureKind instead of counting it as a denial" {
      $result = [pscustomobject]@{ TransportSucceeded = $false; StatusCode = $null; Body = $null; FailureKind = $failureKind }
      Test-ScriptThrows { Assert-GateScenarioResult -Scenario "missing" -ExpectedStatus 401 -Result $result } | Should Be $true
    }
  }

  It "fails a wrong HTTP status" {
    $result = [pscustomobject]@{ TransportSucceeded = $true; StatusCode = 500; Body = ""; FailureKind = $null }
    Test-ScriptThrows { Assert-GateScenarioResult -Scenario "missing" -ExpectedStatus 401 -Result $result } | Should Be $true
  }

  It "requires a valid SentinelGate identity for HTTP 200" {
    $invalid = [pscustomobject]@{ TransportSucceeded = $true; StatusCode = 200; Body = '{"allowed":true}'; FailureKind = $null }
    Test-ScriptThrows { Assert-GateScenarioResult -Scenario "valid" -ExpectedStatus 200 -Result $invalid -RequireSentinelGate } | Should Be $true

    $validBody = '{"service":"SentinelGate","allowed":true,"gatewayValidated":true,"routingContextConsistent":true,"tenantId":"11111111-1111-1111-1111-111111111111","objectId":"22222222-2222-2222-2222-222222222222"}'
    $valid = [pscustomobject]@{ TransportSucceeded = $true; StatusCode = 200; Body = $validBody; FailureKind = $null }
    Test-ScriptThrows { Assert-GateScenarioResult -Scenario "valid" -ExpectedStatus 200 -Result $valid -RequireSentinelGate } | Should Be $false
  }
}

Describe "Dual application deployment checks" {
  It "fails an unhealthy revision" {
    $revision = [pscustomobject]@{
      properties = [pscustomobject]@{
        active = $true
        healthState = "Unhealthy"
        provisioningState = "Provisioned"
        template = [pscustomobject]@{
          containers = @([pscustomobject]@{ image = "acr.example/sentinel-app:test" })
        }
      }
    }
    Test-ScriptThrows { Assert-RevisionReady -Revision $revision -ExpectedImage "acr.example/sentinel-app:test" } | Should Be $true
  }
}

Describe "Certificate issuance safety" {
  $issueCertSource = Get-Content -Raw (Join-Path $PSScriptRoot "..\..\scripts\issue-cert.ps1")

  It "defaults explicitly to staging and requires Production by name" {
    $issueCertSource | Should Match '\[ValidateSet\("Staging", "Production"\)\]\s*\[string\]\s*\$AcmeEnvironment\s*=\s*"Staging"'
    $issueCertSource | Should Match '\$AcmeEnvironment\s+-eq\s+"Production"'
  }

  It "does not start, stop, or restart Application Gateway" {
    $issueCertSource | Should Not Match '(?im)^\s*az\s+network\s+application-gateway\s+(start|stop|restart)\b'
  }
}
