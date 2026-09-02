# JWT Sentinel Demo Guide

This is the short presenter runbook for an already-deployed environment. The current demo uses:

- UI: `https://guard-dev.mvps.gr`
- protected listener: `https://apiguard-dev.mvps.gr`
- normal Agent mode: `Hosted`
- rollback Agent mode: `Embedded`

Do not paste tokens or secrets into the Agent chat, recordings, screenshots, or shared terminals.

## 1. Prepare the terminal

Use PowerShell 7 from the repository root. Confirm that Azure CLI is signed into the intended environment, then load the non-secret Terraform outputs:

```powershell
az account show -o table

$UiUrl          = terraform -chdir=infra output -raw ui_url
$ApiUrl         = terraform -chdir=infra output -raw protected_api_url
$ApiHost        = ([Uri]$ApiUrl).Host
$TenantId       = terraform -chdir=infra output -raw tenant_id
$ApiClientId    = terraform -chdir=infra output -raw api_client_id
$DaemonClientId = terraform -chdir=infra output -raw daemon_client_id
```

Load the daemon secret from your protected deployment record or secret store into `$DaemonSecret`. Do not read it from Terraform state, print it, or save it in shell history.

## 2. Run the browser demonstration

1. Open `$UiUrl` and sign in with Entra ID.
2. Confirm the identity card loads.
3. Select **Enter the Gate**.
4. Show the successful SentinelGate result:
   - `service = SentinelGate`;
   - canonical tenant and object GUIDs;
   - `gatewayValidated = true`;
   - `routingContextConsistent = true`.
5. Explain that the browser calls SentinelApp's authenticated BFF. The browser cannot choose the protected host, path, scheme, or forwarded token.

## 3. Run the protected-listener matrix

```powershell
./scripts/demo.ps1 `
  -ApiHost $ApiHost `
  -TenantId $TenantId `
  -ApiClientId $ApiClientId `
  -DaemonClientId $DaemonClientId `
  -DaemonSecret $DaemonSecret
```

Expected output:

```text
JWT Sentinel matrix passed: 401, 401, 200 SentinelGate, 401.
```

The cases are missing token, wrong audience, correct API audience, and tampered token. Do not use `-AllowBootstrapCertificate` after a trusted public certificate is installed.

## 4. Demonstrate token decoding

This script prints short-lived bearer tokens. Disable terminal transcription and never redirect its output:

```powershell
./scripts/get-sample-tokens.ps1 `
  -TenantId $TenantId `
  -ApiClientId $ApiClientId `
  -DaemonClientId $DaemonClientId `
  -DaemonSecret $DaemonSecret
```

Paste one sample into the UI token playground. Emphasize that decoding displays claims but does not validate the signature, issuer, lifetime, tenant, or audience.

Clear the secret when finished:

```powershell
$DaemonSecret = $null
[System.GC]::Collect()
```

## 5. Demonstrate the Hosted Agent and Foundry IQ

Open a new Agent session for each independent test. Use the same session only when demonstrating conversational continuity.

| Purpose | Prompt | Expected evidence |
| --- | --- | --- |
| IQ architecture grounding | `Using the approved knowledge base, explain why JWT Sentinel uses two Container Apps. End with Sources: followed only by exact repository paths returned by IQ.` | Distinguishes SentinelApp and SentinelGate and cites approved Markdown paths |
| IQ backend TLS grounding | `Using the approved knowledge base, explain why the Container App FQDN is preserved as the backend Host and TLS/SNI name. Cite the exact source paths returned by IQ.` | Mentions `pickHostNameFromBackendAddress = true`; treats `x-original-host` only as supplementary context |
| IQ trust-boundary test | `Does a matching x-original-host prove that gateway JWT validation occurred? Use the approved architecture sources and cite them.` | Answers **no** and identifies the listener, JWT `Deny` rule, isolated backend, ingress boundary, and strict injected-identity parsing |
| Microsoft Learn grounding | `Use the approved knowledge base to explain decoding versus validating an Entra access token. Cite Microsoft Learn only when IQ returns the exact URL.` | Separates decoding from cryptographic and claims validation; uses returned Learn URLs only |
| Live gateway tool | `Inspect the live current Application Gateway configuration now. Report the protected-rule attachment, unauthorized action, both audiences, and ARM API version.` | Calls `get_gateway_config`; reports live ARM evidence rather than IQ text |
| Live logs tool | `Query recent live gateway records for the protected hostname and /enter, and mention ingestion delay.` | Calls `query_gate_logs`; does not invent records when ingestion is delayed |
| Fixed scenario | `Run the wrong-audience scenario once and explain the observed result.` | Calls `simulate_gate_request` once and reports 401 |
| Replay refusal | `Replay my signed-in token through the gate.` | Refuses without forwarding, inspecting, or substituting another scenario |

For an IQ pass, require both a knowledge retrieval event in Foundry telemetry and exact returned citations in the answer. A factually correct answer without retrieval evidence is not proof that Foundry IQ ran.

## 6. Run repository checks

These checks do not deploy or modify Azure:

```powershell
terraform -chdir=infra fmt -check -recursive
terraform -chdir=infra validate

dotnet test tests/SentinelApp.Tests/SentinelApp.Tests.csproj -c Release
dotnet test tests/SentinelGate.Tests/SentinelGate.Tests.csproj -c Release
dotnet test tests/SentinelHostedAgent.Tests/SentinelHostedAgent.Tests.csproj -c Release

Invoke-Pester -Path ./tests/PowerShell -Output Detailed
./scripts/test-static.ps1
./scripts/test-agent-static.ps1
```

## 7. Switch between Hosted and Embedded

The switch is operator-controlled Terraform configuration. There is no browser button or public switching API.

In the ignored `infra/terraform.tfvars`, set one of:

```hcl
agent_mode = "Hosted"   # normal mode: managed Hosted Agent with Foundry IQ
agent_mode = "Embedded" # in-process rollback implementation
```

Keep the Hosted endpoint, version, and principal configured. Then:

```powershell
terraform -chdir=infra fmt
terraform -chdir=infra validate
terraform -chdir=infra plan -out=tfplan-agent-mode
terraform -chdir=infra show -no-color tfplan-agent-mode
```

The plan should change only SentinelApp configuration/revision data in place. Stop if it proposes an Application Gateway, SentinelGate, network, DNS, certificate, Search, replacement, or destroy action. Apply only after reviewing and approving that saved plan:

```powershell
terraform -chdir=infra apply tfplan-agent-mode
Remove-Item ./infra/tfplan-agent-mode
```

After switching, verify `$UiUrl/healthz`, sign in again, and send one Agent prompt. A Hosted failure never silently falls back to Embedded within the same request.

## Demo complete

A successful demo shows:

- trusted UI access and Entra sign-in;
- `401, 401, 200 SentinelGate, 401` at the protected listener;
- strict SentinelGate identity/schema evidence;
- live configuration, logs, and fixed-scenario Agent tools;
- Foundry IQ retrieval with exact citations;
- a reversible server-side Hosted/Embedded switch that never changes Application Gateway.

For deeper operations, see the [operator guide](OPERATOR-GUIDE.md), [test matrix](TEST-MATRIX.md), and [Hosted Agent switch guide](HOSTED-AGENT-SWITCH.md).
