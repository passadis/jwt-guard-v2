# Hosted Agent and Foundry IQ deployment path

## Purpose

This is the shortest supported path from a working JWT Sentinel Stage 1 deployment to the intended operating model:

- Microsoft Foundry Hosted Agent serves Agent responses;
- Foundry IQ retrieves cited evidence from the approved Azure AI Search corpus;
- SentinelApp remains the authenticated broker for deterministic scenarios and sanitized token evidence;
- the embedded Agent Framework implementation remains available only as an operator-controlled rollback.

`HostedShadow` and the full evaluator suite are useful assurance tools, but they are not prerequisites for a normal demonstration deployment. They can be run later when production-style parity evidence is required.

This guide deliberately keeps `infra/` and `agent-infra/` in different resource groups, Terraform workspaces, and state files. Never merge their states and never run `azd provision` over Terraform-owned Foundry resources.

## Why the deployment has two agent passes

The ordering is a real platform dependency, not an arbitrary gate:

1. Terraform creates the Foundry project, Search service, publisher identity, monitoring, and model.
2. The first Hosted Agent deployment creates the runtime principal and managed Responses endpoint.
3. Terraform can then grant that exact runtime principal read-only gateway, Log Analytics, and Search access, and grant SentinelApp permission to invoke the agent.
4. The publisher creates the Search index, knowledge source, and knowledge base.
5. The toolbox can then bind the knowledge-base MCP endpoint to the deployed runtime identity.
6. A new immutable agent version is deployed with `TOOLBOX_NAME` set.
7. SentinelApp is switched to `Hosted` only after an actual IQ tool invocation and citation flow have been observed.

Trying to collapse these steps creates a circular dependency: the runtime identity does not exist before the first agent deployment, while the final toolbox should not be activated before that identity has Search read permission.

The longer migration history also included optional high-assurance work: shadow observation, multiple immutable corrective versions, custom evaluator registration, quota-bounded evaluation, and rollback rehearsals. Those checks found real client and replay-boundary defects, but they are not all platform prerequisites for a normal Hosted/IQ rebuild.

| Source of friction | What it means for a rebuild |
| --- | --- |
| Runtime identity appears only after first deployment | An initial deployment followed by scoped RBAC is required |
| Toolbox depends on the published knowledge base and Search read role | Publish and authorize before the toolbox-enabled deployment |
| Foundry and Search APIs are evolving preview surfaces | Pin versions and verify the live object shape instead of relying only on portal appearance |
| Model tool choice is probabilistic | Prove the IQ tool event; use deterministic retrieval routing when grounding is mandatory |
| RBAC, DNS, certificate, and telemetry propagation are asynchronous | Use bounded waits and read-only verification rather than repeated mutations |
| Shadow and full evaluation consume time and model quota | Treat them as optional production-assurance stages, not basic deployment requirements |

## Inputs

Collect these values without putting secrets in source or command history:

```text
<environment>                         unique Terraform and azd environment name
<subscription-id>                     intended subscription
<tenant-id>                           intended tenant
<agent-resource-group>                new resource group owned by agent-infra
<agent-prefix>                        new, unique agent prefix
<foundry-project-name>                new project name
<stage1-resource-group>               existing JWT Sentinel Stage 1 resource group
<stage1-prefix>                       existing Stage 1 prefix
<gateway-resource-id>                 existing Application Gateway resource ID
<log-analytics-resource-id>           existing Stage 1 workspace resource ID
<sentinel-app-principal-id>           SentinelApp managed identity principal ID
<protected-host>                      configured protected public hostname
<api-client-id>                       API application client ID
<publisher-principal-id>              operator or automation principal used for publication
```

The examples below use isolated local Terraform workspaces. A remote backend is acceptable only with a separately approved unique key.

## 1. Validate locally

```powershell
terraform -chdir=agent-infra fmt -check -recursive
terraform -chdir=agent-infra init
terraform -chdir=agent-infra validate

dotnet restore src/SentinelHostedAgent/SentinelHostedAgent.csproj
dotnet build src/SentinelHostedAgent/SentinelHostedAgent.csproj -c Release --no-restore
dotnet test tests/SentinelHostedAgent.Tests/SentinelHostedAgent.Tests.csproj -c Release

./scripts/prepare-agent-corpus.ps1
./scripts/test-agent-static.ps1
Invoke-Pester ./tests/PowerShell/AgentKnowledge.Tests.ps1
```

## 2. Create the isolated agent foundation

Copy `agent-infra/terraform.tfvars.example` to the ignored `agent-infra/terraform.tfvars` and replace every placeholder. Keep this value null for the foundation plan:

```hcl
hosted_agent_principal_id = null
```

Select a new workspace and confirm it is empty before planning:

```powershell
terraform -chdir=agent-infra workspace new <environment>
# If it already exists:
# terraform -chdir=agent-infra workspace select <environment>

terraform -chdir=agent-infra state list
terraform -chdir=agent-infra plan -out=tfplan-agent-foundation
terraform -chdir=agent-infra show -no-color tfplan-agent-foundation
```

The plan must be create-only and must not touch `infra/`, Application Gateway, Container Apps, DNS, or the Stage 1 state. Apply only the reviewed saved plan, then capture the non-sensitive project, model, Search, and monitoring outputs.

## 3. Deploy the initial immutable Hosted Agent

Create a unique ignored azd environment and set the values consumed by the root `azure.yaml`. For this first deployment, leave `BROKER_BASE_URI` and `TOOLBOX_NAME` empty unless their dependencies already exist and have been reviewed.

Before deploying, inspect the root `azure.yaml`. Its `uses` entry and `azure.ai.project` endpoint must identify the Foundry project just created by the selected `agent-infra` state. A copied repository can retain the last environment's literal project binding; changing only the azd environment name does not retarget that binding. Stop and prepare a reviewed, environment-specific configuration update if either value names another project. This preflight prevents a successful deployment into the wrong Foundry project.

```powershell
azd env new <environment>
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME <model-deployment> --environment <environment>
azd env set GATEWAY_RESOURCE_ID <gateway-resource-id> --environment <environment>
azd env set LAW_WORKSPACE_GUID <workspace-guid> --environment <environment>
azd env set PROTECTED_HOST <protected-host> --environment <environment>
azd env set TENANT_ID <tenant-id> --environment <environment>
azd env set API_CLIENT_ID <api-client-id> --environment <environment>
azd env set BROKER_BASE_URI "" --environment <environment>
azd env set TOOLBOX_NAME "" --environment <environment>

azd deploy jwt-sentinel-gate-explainer --environment <environment> --no-prompt
```

Do not run `azd provision`. Record the immutable agent version, managed Responses endpoint, and runtime principal. The Responses endpoint is configuration, not a credential; invocation still uses managed identity.

## 4. Apply post-deployment least-privilege RBAC

Set the exact deployed runtime principal and SentinelApp principal in the ignored `agent-infra/terraform.tfvars`:

```hcl
hosted_agent_principal_id = "<runtime-principal-guid>"
sentinel_app_principal_id = "<sentinel-app-principal-guid>"
```

Generate a fresh plan against the populated agent state. It should add only the reviewed runtime Search/gateway/log readers and SentinelApp invocation roles. Stop on any replacement, destroy, Stage 1 resource action, or unrelated update.

After applying the saved RBAC plan, verify the exact scopes. Do not broaden the runtime identity to Contributor or assign Search write roles to it.

## 5. Publish the approved IQ corpus

Run the local preparation and publisher dry run first:

```powershell
./scripts/prepare-agent-corpus.ps1

./scripts/publish-agent-knowledge.ps1 `
  -SearchEndpoint <search-endpoint>
```

Review the index, knowledge source, knowledge base, document counts, API version, and allowlisted sources. The live publication command is intentionally explicit:

```powershell
./scripts/publish-agent-knowledge.ps1 `
  -SearchEndpoint <search-endpoint> `
  -ExpectedSubscriptionId <subscription-id> `
  -ExpectedTenantId <tenant-id> `
  -Apply
```

The publisher uses Microsoft Entra authentication, never a Search admin key. Its generated evidence file is ignored and contains hashes/metadata rather than document bodies or credentials.

## 6. Create the IQ connection and toolbox

First review the dry run:

```powershell
./scripts/configure-agent-toolbox.ps1 `
  -ProjectEndpoint <foundry-project-endpoint> `
  -SearchEndpoint <search-endpoint>
```

Then create the reviewed connection and toolbox:

```powershell
./scripts/configure-agent-toolbox.ps1 `
  -ProjectEndpoint <foundry-project-endpoint> `
  -SearchEndpoint <search-endpoint> `
  -SearchResourceId <search-resource-id> `
  -AgentPrincipalId <runtime-principal-guid> `
  -ExpectedSubscriptionId <subscription-id> `
  -ExpectedTenantId <tenant-id> `
  -Apply
```

The expected objects are:

- connection `jwt-sentinel-iq`, using agentic identity and audience `https://search.azure.com/`;
- toolbox `jwt-sentinel-tools`;
- one exposed MCP tool, `knowledge_base_retrieve`;
- target `https://<search-service>.search.windows.net/knowledgebases/jwt-sentinel-kb/mcp?api-version=2026-05-01-preview`.

## 7. Redeploy with the toolbox enabled

Set the toolbox name and the fixed SentinelApp broker origin in the same ignored azd environment, then deploy one new immutable version. The broker origin is the configured UI HTTPS origin only; it is never supplied by a browser request and contains no path, query, fragment, credentials, or alternate scheme:

```powershell
azd env set TOOLBOX_NAME jwt-sentinel-tools --environment <environment>
azd env set BROKER_BASE_URI https://<ui-host> --environment <environment>
azd deploy jwt-sentinel-gate-explainer --environment <environment> --no-prompt
```

Do not leave `BROKER_BASE_URI` empty on the final toolbox-enabled deployment. An empty value is permitted only for the initial identity-bootstrap version and leaves `decode_token` and `simulate_gate_request` deliberately unavailable.

The current .NET hosting contract intentionally uses `TOOLBOX_NAME`. `AddFoundryToolboxes` resolves that name through `FOUNDRY_PROJECT_ENDPOINT`, connects to the Foundry toolbox proxy, and eagerly enumerates tools at startup. A Search index appearing separately in the portal is therefore normal: the agent is connected through **toolbox -> RemoteTool connection -> knowledge-base MCP endpoint -> knowledge source -> index**, not by a direct index attachment.

After deployment, confirm that the immutable version increased and that the managed endpoint, runtime principal, and toolbox name are unchanged. If the runtime principal changes, stop and review RBAC before invoking the agent.

## 8. Prove IQ end to end

Use a fresh session and keep three different claims separate:

1. **Configured:** the toolbox exists and points to the expected knowledge-base MCP endpoint.
2. **Available:** the Hosted Agent runtime enumerates `knowledge_base_retrieve` successfully.
3. **Used:** the response stream or correlated trace contains the IQ tool call and the final answer cites only sources returned by it.

Ask the runtime to list its available tools, then use a grounding-required prompt such as:

> Using the approved knowledge base, explain why JWT Sentinel preserves the Container App FQDN as the backend Host and TLS/SNI name. Cite the exact source paths returned by IQ.

A correct answer without a knowledge retrieval event is **not** an IQ pass; the model may have answered from its instructions or conversation context. Record the discovered runtime tool name, the successful tool event, returned source titles/URLs, and final citations. Never record retrieved document bodies, tokens, or secrets.

If the tool enumerates but a grounding-required prompt does not call it, the infrastructure and Search connection are not the first suspect. This is an orchestration/routing failure. The implemented Hosted Agent wraps the model with `KnowledgeGroundingAgent`: qualifying architecture, trust-boundary, backend Host, and TLS/SNI questions directly invoke the exact enumerated `jwt-sentinel-iq___knowledge_base_retrieve` function with the bounded question. `KnowledgeGroundedAnswer` then verifies that the returned evidence contains the required facts and renders a bounded answer with exact returned citations. Missing tool availability or malformed evidence fails closed. This route deliberately bypasses the model tool loop: the hosting layer otherwise performs a post-tool model call, while the initial Search result can consume most of a small model deployment's TPM window. Live gateway, log, token, and simulation questions remain routed to their dedicated model tools, and archived-history questions remain excluded. Treat a missing IQ tool event or missing final text as a failed validation; do not weaken the citation requirement or present toolbox attachment as proof of use.

## 9. Make Hosted the normal SentinelApp mode

Once the Hosted endpoint, security refusals, live tools, IQ retrieval, citations, and session continuity pass, set the ignored Stage 1 inputs:

```hcl
agent_mode                       = "Hosted"
hosted_agent_responses_endpoint = "<exact-managed-responses-endpoint>"
hosted_agent_version            = <immutable-version>
hosted_agent_principal_id       = "<runtime-principal-guid>"
hosted_shadow_tester_object_ids = []
```

Generate a saved `infra/` plan. It should contain only the expected in-place SentinelApp configuration/revision change. It must not modify Application Gateway, SentinelGate, networking, DNS, certificates, Entra applications, or `agent-infra`.

The normal mode is `Hosted`. Rollback is a reviewed configuration change to:

```hcl
agent_mode = "Embedded"
```

There is deliberately no browser button or public API for changing this security and cost boundary. `HostedShadow` remains an optional advanced validation mode, not a required deployment step.

## Completion checklist

- [ ] Agent foundation is isolated from Stage 1 by resource group, workspace, and state.
- [ ] Initial Hosted deployment returned a runtime principal and managed endpoint.
- [ ] Runtime and SentinelApp roles exist only at the intended scopes.
- [ ] Corpus publication evidence matches the approved manifest.
- [ ] Toolbox points to the expected knowledge-base MCP endpoint.
- [ ] Hosted runtime enumerates the IQ retrieval tool.
- [ ] A fresh grounded prompt produces an IQ tool event and exact citations.
- [ ] Live gateway/log/broker tools preserve their original security boundaries.
- [ ] SentinelApp plan changes only its agent configuration and selects `Hosted`.
- [ ] `Embedded` remains documented and tested as the operator rollback.

For deeper security, evaluation, telemetry, and rollback checks, continue with the [operator guide](OPERATOR-GUIDE.md) and [Hosted Agent switch guide](HOSTED-AGENT-SWITCH.md).
