# JWT Sentinel Stage 1 Architecture

## Scope

This document describes the implemented Stage 1 topology. It is the concise operational architecture reference; `DESIGN.md` retains the fuller design history and alternatives.

Stage 1 uses two Azure Container Apps behind one Azure Application Gateway. SentinelApp revision `ca-edgegrd--0000020` now routes Gate Explainer requests to immutable Foundry Hosted Agent version 7 through `AGENT_MODE=Hosted`; the in-process Microsoft Agent Framework implementation remains intact as the immediate rollback path. Foundry Agent and IQ infrastructure stays permanently isolated from Stage 1 infrastructure in its own resource group and Terraform state. Version 7 requires evidence-tool routing and the client fails closed on explicit failed/incomplete stream events, with one fresh-session retry limited to safe read-only zero-output protocol failures. The final Gate 5 promotion changed only SentinelApp configuration and did not change or restart Application Gateway.

## System context

```mermaid
flowchart LR
    U[Browser] -->|UI hostname| GW[Application Gateway Standard_v2]
    U -->|Entra sign-in| E[Microsoft Entra ID]
    GW -->|UI rule| APP[SentinelApp Container App]
    APP -->|Caller token via protected hostname| GW
    GW -->|API rule + JWT Deny| GATE[SentinelGate Container App]
    APP -->|Managed identity| ARM[Azure Resource Manager]
    APP -->|Managed identity| LAW[Log Analytics]
    APP -->|Managed identity| AI[Foundry / Azure AI model]
    GW -->|Certificate secret| KV[Key Vault]
```

The gateway has separate listeners, rules, pools, probes, and backend HTTP settings for SentinelApp and SentinelGate. There is no cross-routing between the two application planes.

## Runtime components

### SentinelApp

SentinelApp is the UI, control, and explanation plane. It provides:

- the static MSAL.js SPA;
- ASP.NET Core JwtBearer authentication;
- delegated `access_as_user` authorization for all `/api/*` endpoints;
- the `/api/gate/enter` BFF endpoint;
- Microsoft Agent Framework 1.15 GateExplainer sessions;
- token decoding against live gateway policy;
- Application Gateway configuration inspection through ARM;
- protected-traffic queries through Log Analytics;
- controlled missing, valid, wrong-audience, tampered, and caller-replay simulations.

SentinelApp uses a user-assigned managed identity for ACR pull, Foundry/OpenAI user access, resource-group Reader, and Log Analytics Reader. Its daemon credential is stored as a Container App secret for controlled simulations and is never passed to the browser or Agent.

Agent sessions are stored in process, bound to the authenticated tenant/object pair, serialized per session, limited to 250 active entries, and expired after 30 minutes. SentinelApp therefore has exactly one replica in Stage 1.

### SentinelGate

SentinelGate is a small stateless .NET 10 Minimal API exposing:

- `GET /healthz`;
- `POST /enter`.

It has no Agent Framework, Foundry, Log Analytics, ARM Reader, daemon credential, SPA, or simulation dependency. Its managed identity has ACR pull only.

For `/enter`, SentinelGate requires exactly one gateway-injected `x-msft-entra-identity` value in canonical `tenantId:objectId` form. Both values must be non-empty `D`-format GUIDs and the tenant must match configuration. Duplicate, empty, malformed, noncanonical, or unexpected-tenant values are rejected.

## Listener and backend isolation

| Listener | Hostname | Rule | Backend | Gateway JWT validation |
|---|---|---|---|---|
| UI HTTPS | `sentinel.<domain>` | `ui-rule` | SentinelApp pool | None on page navigation |
| Protected HTTPS | `sentinel-api.<domain>` | `api-rule` | SentinelGate pool | `Deny` |
| UI HTTP | `sentinel.<domain>` | redirect rule | UI HTTPS listener | Not applicable |

The JWT policy accepts both `api://<api-client-id>` and the bare API client ID. The protected routing rule explicitly references that policy. The UI rule has no SentinelGate route; the protected rule has no SentinelApp route.

Each backend has its own `/healthz` probe and HTTPS backend settings. `pickHostNameFromBackendAddress = true` is intentional: Application Gateway uses the corresponding Container App FQDN as the backend `Host` header and TLS/SNI name.

`x-original-host` originates with client routing context. SentinelGate may require it to match the configured protected public hostname as a fail-closed consistency check, but that match is not authentication and is not proof of JWT validation.

## Trust boundaries

The SentinelGate identity header is trusted only within the combined boundary formed by:

1. the dedicated protected Application Gateway listener;
2. the JWT `Deny` configuration attached to that listener's rule;
3. the dedicated SentinelGate backend pool with no UI-listener route;
4. Container Apps ingress restricted to Application Gateway egress addresses;
5. strict canonical parsing and tenant checking of `x-msft-entra-identity`.

The Container Apps are externally addressable because Application Gateway reaches their public FQDNs, but ingress allows only the gateway frontend public IP and NAT Gateway public IP. Direct requests from unapproved public sources must be blocked.

## Request flows

### UI API

1. The SPA signs in through Entra authorization code with PKCE.
2. It acquires the delegated API token.
3. SentinelApp validates issuer, audience, signature, lifetime, and `access_as_user`.
4. `/api/whoami`, Agent, tool, and BFF endpoints run only under that policy.

### Enter the Gate

1. The SPA calls authenticated `POST /api/gate/enter` on SentinelApp.
2. SentinelApp obtains the bearer token from the current authenticated request.
3. GateForwarder sends it only to the configured standard-port HTTPS protected DNS origin at `/enter`; the caller cannot supply a target host, path, or scheme.
4. Application Gateway validates the JWT under the protected rule.
5. Allowed traffic reaches SentinelGate with the injected Entra identity.
6. SentinelGate validates the canonical identity and supplementary routing context.
7. SentinelApp accepts only the expected SentinelGate schema, configured tenant, non-empty canonical GUIDs, and both boolean evidence flags.
8. The SPA presents the result and sends only a token-free allowlisted summary to the Agent for explanation.

### Agent evidence

The GateExplainer distinguishes:

- verified evidence: live configuration, decoded claims, observed HTTP response, SentinelGate payload, injected identity, or log row;
- inference: expected behavior derived from claims and live policy;
- unknowns: unavailable configuration, missing or delayed telemetry, or unverified signature state.

Token decoding never claims cryptographic validation. HTTP denial alone never proves backend non-reachability.

## Network and certificates

The Application Gateway subnet is attached to a NAT Gateway. Outbound HTTPS is required for Entra signing metadata and for the public Container App backends. Both NAT and frontend public IPs are retained in Container Apps ingress restrictions.

Application Gateway uses a user-assigned identity with Key Vault secret access. The listener references an unversioned Key Vault secret URI. Terraform creates the bootstrap certificate; `issue-cert.ps1` creates later versions under the same certificate name. Certificate issuance defaults to Let's Encrypt staging, and production must be selected explicitly.

## Terraform ownership and recovery

Application Gateway is a single `azapi_resource` using API `2025-05-01`; this preserves preview JWT properties not represented by the AzureRM gateway resource.

`gateway_config_generation` defaults to zero and affects only a visible tag on that same AzAPI resource. Incrementing it is an opt-in trigger that resubmits the complete resource body. `prevent_destroy = true` makes a proposed replacement or destruction fail rather than recreate the gateway. After an approved restart, operators must review the in-place update, apply it through the same AzAPI resource, verify the live JWT rule, and rerun the complete matrix.

Terraform owns initial Container App images but ignores later image drift because `deploy-app.ps1` owns deployed image revisions. Certificate versions after bootstrap are similarly owned by `issue-cert.ps1`.

The repository currently uses fresh local state by default. A remote backend may be introduced only with an explicitly approved, unique key. `.terraform.lock.hcl` is committed; `.terraform/`, state, tfvars, plans, backend configuration, and generated sensitive material are ignored.

## Stage boundary

Stage 1 deliberately retains the in-process Agent Framework implementation but does not own the Foundry Hosted Agent, Foundry IQ knowledge source, distributed session store, or hosted identity. Those components remain in the permanently separate `rg-edgegrd-agent` resource group and `agent-infra/terraform.tfstate`; they have no route through Application Gateway. The current SentinelApp revision explicitly sets `AGENT_MODE=Hosted` and pins the reviewed Hosted Agent Responses endpoint and immutable version 7. The Hosted Agent alone holds the exact `BROKER_BASE_URI=https://guard.mvps.gr` origin. The reviewed `infra/tfplan-gate5-v7-rollback` plan can restore `Embedded` without modifying the gateway.

The isolated candidate's Foundry account and project are connected to `appi-edgegrd-agent`, backed by `law-edgegrd-agent`. The project identity has only component-scoped Log Analytics Reader and Privileged Monitoring Data Reader access for hosted traces and evaluations. The connection credential is held as sensitive Terraform state and is not emitted as an output. The hosted runtime identity retains only the previously approved resource-scoped gateway, Stage 1 log-workspace, and Search reader roles.

The published IQ corpus, Search index, knowledge source, knowledge base, RemoteTool connection, and toolbox remain agent-stack artifacts. Final Gate 5 validation correlated successful `get_gateway_config` plus ARM HTTP 200, `query_gate_logs` plus Log Analytics HTTP 200, IQ retrieval, the fixed simulation broker, and sanitized decode broker calls. Continuity and explicit reset passed; security prompts caused no prohibited tool execution. All 12 v7 invocations and 19 model calls succeeded, and a count-only scan of 537 correlated rows found no token or secret-value patterns. One safe read-only IQ call used the single allowed fresh-session retry. The embedded agent remains available only as the preserved rollback implementation. The [agent migration design](AGENT-MIGRATION.md) defines permissions, data governance, and rollback.

The integration boundary keeps `/api/agent/chat` and `/api/agent/reset` stable behind the SPA's delegated policy. A server-only router supports `Embedded`, `HostedShadow`, and `Hosted`; the absent setting still fails safely to `Embedded`. `HostedShadow` additionally requires a non-empty operator-configured set of unique lowercase canonical Entra object IDs. The browser cannot select the mode, tester identity, endpoint, or version. Hosted sessions are keyed by canonical authenticated owner plus browser session GUID and retain only opaque Foundry session/conversation identifiers and the server-derived pseudonymous identity that scopes their Foundry operations. Local reset or expiry removes the whole mapping; any future remote history or deletion operation must use that same delegated-user identity. A separate broker policy rejects delegated callers and requires the exact tenant, `agent.scenario.execute` application role, and configured Hosted Agent principal. ASP.NET Core may expose that application role as either the raw `roles` claim or the framework-mapped `ClaimTypes.Role`; both feed the same exact-role comparison. The role is granted only to the deployed runtime principal. SentinelApp has Foundry Agent Consumer and the single user-identity-impersonation data action at the exact Hosted Agent scope. Raw tokens remain local: the browser's authenticated decode flow produces bounded sanitized evidence, while the server retains a short-lived opaque handle that is never returned to the browser. Version 7 and its fixed endpoint are pinned, and the operator-controlled mode is now `Hosted`; the shadow allowlist is empty.
