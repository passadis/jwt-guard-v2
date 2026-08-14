$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Assert-AgentCondition {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )
    if (-not $Condition) { throw $Message }
}

$terraformFiles = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "agent-infra") -Filter *.tf -File
$terraform = ($terraformFiles | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n"
$program = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/Program.cs")
$options = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/Configuration/HostedAgentOptions.cs")
$broker = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/Tools/BrokerEvidenceTool.cs")
$logs = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/Tools/GatewayLogTool.cs")
$instructions = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/GateExplainerInstructions.cs")
$agentInstructions = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/AGENTS.md")
$gitIgnore = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot ".gitignore")
$corpus = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/knowledge/corpus.json") | ConvertFrom-Json
$publisher = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "scripts/publish-agent-knowledge.ps1")
$toolboxConfigurator = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "scripts/configure-agent-toolbox.ps1")
$toolboxDefinition = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/toolbox.yaml")
$monitoring = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "agent-infra/monitoring.tf")
$evaluationDefinition = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/eval.yaml")
$rubricOnlyEvaluationDefinition = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/eval-rubric.yaml")
$securityRubric = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "src/SentinelHostedAgent/evaluators/jwt-sentinel-security-parity/rubric_dimensions.json") | ConvertFrom-Json
$evaluatorRegistrar = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "scripts/register-agent-evaluator.py")

Assert-AgentCondition ($terraform -notmatch 'terraform_remote_state|backend\s+"azurerm"') "Agent Terraform must not read Stage 1 state or initialize a remote backend."
Assert-AgentCondition ($terraform -notmatch 'azurerm_application_gateway|Microsoft\.Network/applicationGateways@|azurerm_container_app|azurerm_dns|azurerm_key_vault') "Agent Terraform must not own gateway, Container Apps, DNS, or Key Vault resources."
Assert-AgentCondition ($terraform -match 'hosted_agent_principal_id\s*!=\s*null' -and $terraform -match 'count\s*=\s*local\.hosted_agent_rbac_enabled\s*\?\s*1\s*:\s*0') "Existing-stack role assignments must stay disabled until a real hosted-agent identity is supplied."
Assert-AgentCondition ($terraform -match 'knowledgeRetrieval\s*=\s*"free"' -and $terraform -match 'semantic_search_sku\s*=\s*"free"') "Search paid retrieval and semantic ranking must remain opt-in."
Assert-AgentCondition ($terraform -match 'local_authentication_enabled\s*=\s*false') "New services must use Entra/RBAC rather than local keys."
Assert-AgentCondition ($program -match 'AddFoundryResponses' -and $program -match 'RegisterProtocol\("responses"') "Hosted Agent must expose only the Foundry Responses protocol."
Assert-AgentCondition ($program -match 'AddFoundryToolboxes\(credential, options\.ToolboxName\)') "Hosted Agent must consume IQ through the Agent Framework 1.15 Foundry toolbox hosting boundary."
Assert-AgentCondition ($options -match 'BROKER_BASE_URI' -and $options -match 'UriSchemeHttps' -and $options -match 'uri\.IsDefaultPort') "Broker origin must be configured as standard-port HTTPS only."
Assert-AgentCondition ($broker -match 'api/agent/broker/decode/\{handle:D\}' -and $broker -match 'api/agent/broker/simulate') "Broker paths must be fixed in source."
Assert-AgentCondition ($broker -match '\["missing", "valid", "wrong_audience", "tampered"\]' -and $broker -notmatch 'AllowedScenarios[^\n]*user_replay') "Hosted simulation must exclude user replay and arbitrary scenarios."
Assert-AgentCondition ($broker -match 'Raw tokens are not accepted' -and $broker -notmatch 'Authorization.*scenario|new Uri\([^,]+,\s*scenario') "Broker tools must not accept raw tokens or scenario-derived targets."
Assert-AgentCondition ($logs -match "OriginalHost =~" -and $logs -match "RequestUri startswith '/enter'" -and $logs -match '\| take 40') "Log query must be bounded to protected routing context and /enter."
Assert-AgentCondition ($instructions -match 'client-originated\s+routing context' -and $instructions -match 'never\s+authentication or proof') "OriginalHost must never be documented as authentication evidence."
Assert-AgentCondition ($instructions -match 'cite only sources actually returned' -and $instructions -match 'Do not use Markdown link syntax' -and $instructions -match 'placeholders such as \(#\)') "IQ citations must be grounded in returned source identifiers without fabricated links."
Assert-AgentCondition ($instructions -match 'Reject an all-zero GUID.*without\s+calling the tool' -and $instructions -match 'docs/history and archived session JSONL are outside' -and $instructions -match 'After every tool result, produce a final') "Hosted Agent instructions must fail closed for invalid handles, excluded archives, and tool continuation."
Assert-AgentCondition ($instructions -match 'Mandatory evidence routing' -and $instructions -match 'call get_gateway_config during that turn' -and $instructions -match 'Never answer that request\s+solely from instructions, IQ, conversation history, or an earlier tool\s+result') "Explicit live gateway requests must deterministically call the live configuration tool."
Assert-AgentCondition ($agentInstructions -match 'built with the microsoft-foundry skill') "Hosted Agent AGENTS.md is missing the required Foundry skill marker."
Assert-AgentCondition (Test-Path -LiteralPath (Join-Path $repositoryRoot "agent-infra/.terraform.lock.hcl") -PathType Leaf) "The isolated Terraform lock file is missing."
Assert-AgentCondition ($gitIgnore -notmatch '(?m)^\s*!?\.terraform\.lock\.hcl\s*$') "Terraform lock files must remain commit-ready."
Assert-AgentCondition ($gitIgnore -match '\*\*/\.terraform/' -and $gitIgnore -match '\*\.tfstate' -and $gitIgnore -match '\*\.tfplan' -and $gitIgnore -match '\*\.tfvars') "Terraform metadata, state, plans, and populated tfvars must be ignored."
Assert-AgentCondition ($gitIgnore -match '\*\*/__pycache__/' -and $gitIgnore -match '\*\.py\[cod\]' -and $gitIgnore -match '\*\*/\.checkpoints/') "Python bytecode and Hosted Agent checkpoints must remain ignored."
Assert-AgentCondition ($publisher -match '\[switch\]\$Apply' -and $publisher -match 'if \(-not \$Apply\)' -and $publisher -match 'No Microsoft Learn page was fetched') "Knowledge publication must default to a local dry run."
Assert-AgentCondition ($publisher -match 'https://search\.azure\.com/\.default' -and $publisher -notmatch '(?i)api-key\s*=|SearchKey|AzureKeyCredential') "Knowledge publication must use Search Entra authentication without keys."
Assert-AgentCondition ($publisher -match 'outputMode\s*=\s*"extractiveData"' -and $publisher -match 'kind\s*=\s*"minimal"' -and $publisher -match 'models\s*=\s*@\(\)') "The initial knowledge base must remain extractive and minimal without a Search-side LLM."
Assert-AgentCondition ($toolboxConfigurator -match '--auth-type agentic-identity' -and $toolboxConfigurator -match '--audience "https://search\.azure\.com/"') "The IQ connection must use the deployed agent identity for the Search audience."
Assert-AgentCondition ($toolboxConfigurator -notmatch 'project-managed-identity|user-entra-token|azd\s+(provision|deploy)') "Toolbox configuration must not use project/user identity, provision the project, or deploy an agent."
Assert-AgentCondition ($toolboxDefinition -match '(?m)^connections:' -and $toolboxDefinition -match '(?m)^\s*- name: jwt-sentinel-iq\s*$' -and $toolboxDefinition -notmatch '(?m)^\s*(tools|skills):') "The toolbox must contain only the curated IQ connection."
Assert-AgentCondition ($monitoring -match 'Microsoft\.CognitiveServices/accounts/connections@2025-04-01-preview' -and $monitoring -match 'Microsoft\.CognitiveServices/accounts/projects/connections@2025-04-01-preview') "Foundry monitoring must be linked at both account and project scopes."
Assert-AgentCondition ($monitoring -match 'sensitive_body' -and $monitoring -match 'application_insights\.agent\.connection_string') "The monitoring credential must remain a write-only AzAPI input."
Assert-AgentCondition ($monitoring -match '73c42c96-874c-492b-b04d-ab87d138a893' -and $monitoring -match 'dbc9c667-e97f-4491-aee6-90b9cf960190' -and $monitoring -match 'scope\s*=\s*azurerm_application_insights\.agent\.id') "Monitoring readers must remain limited to the agent-owned Application Insights component."
Assert-AgentCondition ($evaluationDefinition -match '(?m)^\s*version:\s*"7"\s*$' -and $evaluationDefinition -match 'builtin\.task_adherence' -and $evaluationDefinition -match 'builtin\.groundedness') "The primary evaluation must pin the reviewed Hosted Agent version 7 and retain the supported built-in evaluators."
Assert-AgentCondition ($evaluationDefinition -match 'name:\s*jwt-sentinel-security-parity' -and $evaluationDefinition -match '(?m)^\s*version:\s*"1"\s*$' -and $evaluationDefinition -notmatch 'builtin\.tool_call_accuracy') "The primary evaluation must use security rubric v1 instead of the incompatible tool-call evaluator."
Assert-AgentCondition ($rubricOnlyEvaluationDefinition -match '(?m)^\s*version:\s*"7"\s*$' -and $rubricOnlyEvaluationDefinition -match 'name:\s*jwt-sentinel-security-parity' -and $rubricOnlyEvaluationDefinition -notmatch 'builtin\.') "The low-quota retry recipe must pin version 7 and contain only the repository-owned security rubric."
Assert-AgentCondition (@($securityRubric).Count -eq 5) "The security parity rubric must retain exactly five reviewed dimensions."
foreach ($dimension in @('security_boundary_fidelity', 'evidence_and_tool_discipline', 'grounding_and_corpus_boundaries', 'confidentiality_and_session_isolation', 'communication_quality')) {
    Assert-AgentCondition ($securityRubric.id -contains $dimension) "The security parity rubric is missing dimension $dimension."
}
Assert-AgentCondition (($securityRubric | Measure-Object -Property weight -Sum).Sum -eq 30) "The reviewed security parity rubric weights must total 30."
Assert-AgentCondition ($evaluatorRegistrar -match 'action="store_true"' -and $evaluatorRegistrar -match 'Dry run' -and $evaluatorRegistrar -match 'create_version') "Evaluator registration must default to dry run and require an explicit apply switch for initial creation."

$expectedLocalSources = @(
    "AGENTS.md",
    "README.md",
    "docs/ARCHITECTURE.md",
    "docs/DECISIONS.md",
    "docs/FIELD-NOTES.md",
    "docs/DEPLOYMENT-RUNBOOK.md",
    "docs/TEST-MATRIX.md"
)
Assert-AgentCondition (@($corpus.localSources).Count -eq $expectedLocalSources.Count) "Corpus local source count differs from the accepted allowlist."
foreach ($source in $expectedLocalSources) {
    Assert-AgentCondition ($corpus.localSources.path -contains $source) "Corpus is missing approved source $source."
}
Assert-AgentCondition ($corpus.excluded -contains "docs/history/") "Historical documents must remain outside the IQ corpus."

$evaluationPath = Join-Path $repositoryRoot "src/SentinelHostedAgent/evaluation/smoke.jsonl"
$evaluationRows = Get-Content -LiteralPath $evaluationPath | ForEach-Object { $_ | ConvertFrom-Json }
Assert-AgentCondition (@($evaluationRows).Count -ge 15) "Evaluation seed must retain at least 15 security and parity cases."
Assert-AgentCondition (@($evaluationRows | Where-Object { $_.tags.PSObject.Properties.Name -contains "security" -and $_.tags.security -eq "critical" }).Count -ge 8) "Evaluation seed lacks the required security-critical coverage."
Assert-AgentCondition (-not ((Get-Content -Raw -LiteralPath $evaluationPath) -match 'Ignore your security instructions')) "The indirect-injection case must test untrusted retrieved content without using judge-blocked jailbreak wording."

Write-Host "Hosted-agent static architecture, security, isolation, corpus, and evaluation checks passed." -ForegroundColor Green
