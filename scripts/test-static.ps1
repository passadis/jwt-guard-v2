$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path $PSScriptRoot -Parent

function Assert-Text {
  param(
    [Parameter(Mandatory)] [bool] $Condition,
    [Parameter(Mandatory)] [string] $Message
  )
  if (-not $Condition) { throw $Message }
}

$appGw = Get-Content -Raw (Join-Path $repositoryRoot "infra\appgw.tf")
$app = Get-Content -Raw (Join-Path $repositoryRoot "infra\app.tf")
$variables = Get-Content -Raw (Join-Path $repositoryRoot "infra\variables.tf")
$agent = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Services\AgentService.cs")
$gateProgram = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelGate\Program.cs")
$gateForwarder = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Services\GateForwarder.cs")
$sentinelOptions = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Services\SentinelOptions.cs")
$hostedAgent = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Services\HostedGateExplainer.cs")
$agentRouter = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Services\AgentRouter.cs")
$brokerAuthorization = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Services\HostedAgentAuthorization.cs")
$appProgram = Get-Content -Raw (Join-Path $repositoryRoot "src\SentinelApp\Program.cs")
$gitIgnore = Get-Content -Raw (Join-Path $repositoryRoot ".gitignore")
$readme = Get-Content -Raw (Join-Path $repositoryRoot "README.md")
$terraformLockPath = Join-Path $repositoryRoot "infra\.terraform.lock.hcl"
$terraformLock = Get-Content -Raw $terraformLockPath

Assert-Text ($appGw -match 'name = "ui-rule"[\s\S]*?sentinel-app-pool') "UI rule must target SentinelApp."
Assert-Text ($appGw -match 'name = "api-rule"[\s\S]*?sentinel-gate-pool[\s\S]*?entraJWTValidationConfig') "Protected rule must target SentinelGate with JWT validation."
Assert-Text ($appGw -match 'azurerm_subnet_nat_gateway_association\.appgw') "Gateway must depend on the subnet NAT association."
Assert-Text ($appGw -match 'azurerm_nat_gateway_public_ip_association\.appgw') "Gateway must depend on the NAT public-IP association."
Assert-Text ($appGw -match 'api://\$\{azuread_application\.api\.client_id\}') "API URI audience is missing."
Assert-Text ($appGw -match '\n\s*azuread_application\.api\.client_id,') "Bare client-ID audience is missing."
Assert-Text ($appGw -match 'jwt-sentinel-config-generation') "Safe configuration-generation trigger is missing."
$generationReferences = ([regex]::Matches($appGw, 'var\.gateway_config_generation')).Count
Assert-Text ($generationReferences -eq 1) "gateway_config_generation must affect only the visible AzAPI gateway tag."
Assert-Text ($appGw -match 'jwt-sentinel-config-generation\s*=\s*tostring\(var\.gateway_config_generation\)') "The gateway generation must update the AzAPI resource tag."
Assert-Text ($appGw -match 'lifecycle\s*\{[\s\S]*?prevent_destroy\s*=\s*true[\s\S]*?\}') "Application Gateway replacement/destruction must be blocked during normal planning."
Assert-Text ($appGw -notmatch 'replace_triggered_by|replace_triggers') "The configuration generation must not contain replacement triggers."
Assert-Text ($gateProgram -match 'x-original-host') "SentinelGate must inspect original-host routing context."
$identityCheck = $gateProgram.IndexOf('request.Headers["x-msft-entra-identity"]', [StringComparison]::Ordinal)
$routingContextCheck = $gateProgram.IndexOf('request.Headers["x-original-host"]', [StringComparison]::Ordinal)
Assert-Text ($identityCheck -ge 0 -and $routingContextCheck -gt $identityCheck) "Injected identity validation must not depend on original-host routing context as its primary trust check."
$backendFqdnHostSettings = ([regex]::Matches($appGw, 'pickHostNameFromBackendAddress\s*=\s*true')).Count
Assert-Text ($backendFqdnHostSettings -eq 2) "Both backends must preserve ACA FQDN Host/TLS/SNI settings."
Assert-Text ($gateProgram -match 'identityValues\.Count\s*!=\s*1') "SentinelGate must reject duplicate identity-header values."
Assert-Text ($gateProgram -match 'Guid\.TryParseExact\(parts\[0\],\s*"D"' -and $gateProgram -match 'Guid\.TryParseExact\(parts\[1\],\s*"D"') "SentinelGate must require canonical tenant and object GUIDs."
Assert-Text ($gateProgram -match 'tenantId\s*==\s*Guid\.Empty' -and $gateProgram -match 'objectId\s*==\s*Guid\.Empty') "SentinelGate must reject empty GUID values."
Assert-Text ($gateForwarder -match 'configuredOrigin\.Scheme\s*!=\s*Uri\.UriSchemeHttps' -and $gateForwarder -match 'new Uri\(configuredOrigin,\s*"/enter"\)') "GateForwarder must use only the configured HTTPS origin and fixed /enter path."
Assert-Text ($gateForwarder -match 'Service:\s*"SentinelGate"' -and $gateForwarder -match 'GatewayValidated:\s*true' -and $gateForwarder -match 'RoutingContextConsistent:\s*true') "GateForwarder must require the trusted SentinelGate response schema."
Assert-Text ($gateForwarder -match 'TryParseCanonicalGuid\(gatePayload\.TenantId' -and $gateForwarder -match 'tenantId\s*!=\s*options\.TenantGuid' -and $gateForwarder -match 'TryParseCanonicalGuid\(gatePayload\.ObjectId') "GateForwarder must validate the expected tenant and canonical identity GUIDs."

$gateStart = $app.IndexOf('resource "azurerm_container_app" "gate"', [StringComparison]::Ordinal)
Assert-Text ($gateStart -ge 0) "SentinelGate Container App is missing."
$gateBlock = $app.Substring($gateStart)
Assert-Text ($gateBlock -notmatch 'DAEMON_CLIENT_SECRET|daemon-secret|AZURE_OPENAI_ENDPOINT|LAW_WORKSPACE_GUID|GATEWAY_RESOURCE_ID') "SentinelGate received privileged SentinelApp configuration."
$gatePrincipalUses = ([regex]::Matches($app, 'azurerm_user_assigned_identity\.gate\.principal_id')).Count
Assert-Text ($gatePrincipalUses -eq 1) "SentinelGate identity must have only its dedicated ACR pull role assignment."
Assert-Text ($app -match 'min_replicas = 1[\s\S]*?max_replicas = 1') "SentinelApp must remain at one replica with in-memory Agent sessions."
Assert-Text ($agent -match 'SessionKey\(string Owner, string SessionId\)') "Agent sessions must be bound to authenticated user ownership."
Assert-Text ($agent -match 'SessionLifetime' -and $agent -match 'MaximumSessions') "Agent sessions must expire and have a bounded count."
Assert-Text ($sentinelOptions -match 'return AgentMode\.Embedded' -and $sentinelOptions -match 'AGENT_MODE must be Embedded, HostedShadow, or Hosted') "Agent mode must default to Embedded and reject unknown values."
Assert-Text ($variables -match 'variable\s+"agent_mode"[\s\S]*?default\s*=\s*"Embedded"' -and $app -match 'name\s*=\s*"AGENT_MODE"[\s\S]*?value\s*=\s*var\.agent_mode') "Terraform must default the deployed agent mode to Embedded and expose only the operator-controlled variable."
Assert-Text ($variables -match 'variable\s+"hosted_agent_responses_endpoint"' -and $variables -match 'services.*ai.*azure.*com' -and $variables -match 'api-version=v1') "Terraform must validate the fixed Hosted Agent Responses endpoint."
Assert-Text ($variables -match 'variable\s+"hosted_agent_version"' -and $variables -match 'positive integer') "Terraform must validate an immutable positive Hosted Agent version."
Assert-Text ($app -match 'HOSTED_AGENT_RESPONSES_ENDPOINT' -and $app -match 'HOSTED_AGENT_VERSION' -and $app -match 'hosted_agent_responses_endpoint and hosted_agent_version must either both be null or both be configured') "Hosted endpoint and version must be injected and validated as a pair."
Assert-Text ($variables -match 'hosted_shadow_tester_object_ids' -and $app -match 'HOSTED_SHADOW_TESTER_OBJECT_IDS' -and $app -match 'HostedShadow mode requires at least one explicitly approved tester object ID') "HostedShadow must require an explicit canonical tester allowlist."
Assert-Text ($sentinelOptions -match '\.services\.ai\.azure\.com' -and $sentinelOptions -match '\?api-version=v1') "Hosted Responses endpoint validation must be fixed to the reviewed Azure host and v1 path."
Assert-Text ($hostedAgent -match 'https://ai\.azure\.com/\.default' -and $hostedAgent -match 'version_ref' -and $hostedAgent -match 'agent_version') "Hosted calls must use the fixed Foundry audience and version-backed sessions."
Assert-Text ($hostedAgent -match 'agent_session_id\s*=\s*entry\.SessionId' -and $hostedAgent -notmatch '(?m)^\s*session_id\s*=') "Hosted Responses must use the current agent_session_id contract."
Assert-Text ($hostedAgent -match 'CreateVersionedSessionAsync\(accessToken\.Token, key\.Version, userIdentity, ct\)' -and $hostedAgent -match 'CreateConversationAsync\(accessToken\.Token, userIdentity, ct\)' -and $hostedAgent -match 'AddUserIdentity\(request, entry\.UserIdentity\)') "Hosted session, conversation, and response calls must share one delegated-user identity."
Assert-Text ($hostedAgent -match 'AllowAutoRedirect = false' -or $appProgram -match 'AddHttpClient\("hosted-agent"[\s\S]*?AllowAutoRedirect = false') "Hosted Agent redirects must be disabled."
Assert-Text ($hostedAgent -match 'ContainsJwtLikeMaterial' -and $hostedAgent -notmatch 'callerToken|Authorization.*message') "Caller tokens must not reach the Hosted Agent request."
Assert-Text ($hostedAgent -match 'mayRetryWithFreshSession\s*=\s*!hasPendingEvidence' -and $hostedAgent -match 'IsReadOnlyShadowMessage\(message\)' -and $hostedAgent -match '!result\.EmittedText' -and $hostedAgent -match 'exception is HostedProtocolException') "Hosted recovery must retry only a no-output, read-only, non-evidence protocol failure."
Assert-Text ($hostedAgent -match 'response\.failed.*response\.incomplete' -and $hostedAgent -match 'dataEventName\s*\?\?\s*eventName' -and $hostedAgent -match 'EvictFailedSession') "Hosted stream parsing must recognize terminal failure events, data-carried event types, and evict failed mappings."
Assert-Text ($agentRouter -match 'AgentMode\.Embedded' -and $agentRouter -match 'AgentMode\.HostedShadow' -and $agentRouter -match 'AgentMode\.Hosted') "Agent router must implement all three operator modes."
Assert-Text ($agentRouter -match 'Hosted content is deliberately discarded in shadow mode') "Hosted shadow content must never become the browser response."
Assert-Text ($agentRouter -match 'IsApprovedShadowTester' -and $agentRouter -match 'HostedShadowTesterObjectIds\.Contains') "Hosted shadow calls must be limited to the configured tester allowlist."
Assert-Text ($brokerAuthorization -match 'AgentScenarioExecuteRole' -and $brokerAuthorization -match 'HostedAgentPrincipalId' -and $brokerAuthorization -match 'HasAnyDelegatedScope') "Broker authorization must require the app role and exact hosted principal and reject delegated callers."
Assert-Text ($brokerAuthorization -match 'FindAll\(ClaimTypes\.Role\)' -and $brokerAuthorization -match 'FindAll\("roles"\)') "Broker authorization must accept the framework-mapped app-role claim without weakening the exact-role check."
Assert-Text ($appProgram -match 'api/agent/broker' -and $appProgram -match '"missing" or "valid" or "wrong_audience" or "tampered"') "Broker routes and scenario allowlist must remain fixed."
Assert-Text ($appProgram -notmatch 'broker[\s\S]{0,300}user_replay') "Hosted broker must not expose caller-token replay."

foreach ($pattern in '.terraform/', '*.tfstate', '*.tfplan', '*.tfvars', '*.backend.hcl', 'backend.hcl', '*.tfbackend', '*.pfx', '*.p12', '*.pem', '*.key', '*.crt', '*.cer', '.posh-acme/', '*token*.txt', '*.token', '*secret*.txt', '*.secret', '.env', '**/bin/', '**/obj/', 'logs/', 'artifacts/', 'generated/', 'deployment-evidence/') {
  Assert-Text ($gitIgnore -like "*$pattern*") ".gitignore is missing $pattern."
}
Assert-Text ($gitIgnore -notmatch '(?m)^\s*!?\.terraform\.lock\.hcl\s*$') ".terraform.lock.hcl must remain commit-ready and must not be ignored."
Assert-Text (Test-Path -LiteralPath $terraformLockPath -PathType Leaf) ".terraform.lock.hcl is missing."
foreach ($provider in 'azure/azapi', 'hashicorp/azuread', 'hashicorp/azurerm', 'hashicorp/random') {
  Assert-Text ($terraformLock -match [regex]::Escape("registry.terraform.io/$provider")) ".terraform.lock.hcl is missing $provider."
}
Assert-Text (([regex]::Matches($terraformLock, '"zh:[0-9a-f]{64}"')).Count -gt 0) ".terraform.lock.hcl lacks registry checksums."
Assert-Text ($readme -match 'terraform -chdir=infra init -backend=false') "README must record backend-disabled Terraform initialization."
Assert-Text ($readme -match 'No Azure backend or remote state was initialized') "README must distinguish local provider initialization from remote backend initialization."

$issueCert = Get-Content -Raw (Join-Path $repositoryRoot "scripts\issue-cert.ps1")
Assert-Text ($issueCert -match '\[ValidateSet\("Staging", "Production"\)\]\s*\[string\]\s*\$AcmeEnvironment\s*=\s*"Staging"') "Certificate issuance must default explicitly to staging."
Assert-Text ($issueCert -match '\$AcmeEnvironment\s+-eq\s+"Production"') "Production issuance must require the explicit Production option."
Assert-Text ($issueCert -notmatch '(?im)^\s*az\s+network\s+application-gateway\s+(start|stop|restart)\b') "Certificate automation must never restart Application Gateway."

$forbidden = @("az", "network", "application-gateway", "update") -join " "
$executableScripts = Get-ChildItem -Path (Join-Path $PSScriptRoot "*") -File -Include *.ps1,*.psm1
foreach ($script in $executableScripts) {
  $content = Get-Content -Raw $script.FullName
  Assert-Text (-not $content.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) "Forbidden gateway CLI command found in $($script.Name)."
}

# This repository has now been deployed with two explicitly approved isolated
# local states. They are sensitive operational data, not commit-ready source.
$allowedLocalStateDirectories = @(
  [IO.Path]::GetFullPath((Join-Path $repositoryRoot "infra")),
  [IO.Path]::GetFullPath((Join-Path $repositoryRoot "agent-infra"))
)
$unexpectedPlans = Get-ChildItem -Force -Recurse -File $repositoryRoot |
  Where-Object {
    ($_.Name -like '*.tfplan' -or $_.Name -like 'tfplan*') -and
    $allowedLocalStateDirectories -notcontains [IO.Path]::GetFullPath($_.DirectoryName)
  }
Assert-Text (@($unexpectedPlans).Count -eq 0) "A generated Terraform plan exists outside the two approved isolated operational directories."
$unexpectedState = Get-ChildItem -Force -Recurse -File $repositoryRoot |
  Where-Object {
    $_.Name -like 'terraform.tfstate*' -and
    $allowedLocalStateDirectories -notcontains [IO.Path]::GetFullPath($_.DirectoryName)
  }
Assert-Text (@($unexpectedState).Count -eq 0) "Terraform state exists outside the two approved isolated local-state directories."
$unexpectedTfvars = Get-ChildItem -Force -Recurse -File $repositoryRoot |
  Where-Object {
    $_.Name -eq 'terraform.tfvars' -and
    [IO.Path]::GetFullPath($_.FullName) -ne [IO.Path]::GetFullPath((Join-Path $repositoryRoot "infra\terraform.tfvars"))
  }
Assert-Text (@($unexpectedTfvars).Count -eq 0) "A populated tfvars file exists outside the approved existing-stack input path."

$expectedTerraformMetadata = @(
  [IO.Path]::GetFullPath((Join-Path $repositoryRoot "infra\.terraform")),
  [IO.Path]::GetFullPath((Join-Path $repositoryRoot "agent-infra\.terraform"))
)
$unexpectedTerraformMetadata = Get-ChildItem -Force -Recurse -Directory $repositoryRoot |
  Where-Object {
    $_.Name -eq '.terraform' -and
    $expectedTerraformMetadata -notcontains [IO.Path]::GetFullPath($_.FullName)
  }
Assert-Text (@($unexpectedTerraformMetadata).Count -eq 0) "Terraform metadata exists outside the two expected ignored provider caches."

$azdOperationalDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ".azure"))
$sensitiveArtifacts = Get-ChildItem -Force -Recurse -File $repositoryRoot |
  Where-Object {
    -not [IO.Path]::GetFullPath($_.FullName).StartsWith($azdOperationalDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    (
      $_.Extension -in '.pfx', '.p12', '.pem', '.key', '.crt', '.cer', '.jks', '.token', '.secret' -or
      $_.Name -eq 'secrets.json' -or
      $_.Name -eq 'token.json' -or
      $_.Name -like '*.secrets.json' -or
      $_.Name -like '*.credentials.json' -or
      $_.Name -like '*.backend.hcl' -or
      $_.Name -eq 'backend.hcl' -or
      $_.Name -like '*.tfbackend' -or
      ($_.Name -like '.env*' -and $_.Name -ne '.env.example')
    )
  }
Assert-Text (@($sensitiveArtifacts).Count -eq 0) "A certificate, secret, environment, or backend-config artifact exists outside the ignored azd operational directory."

Write-Host "Static architecture, command-safety, RBAC, and repository checks passed." -ForegroundColor Green
