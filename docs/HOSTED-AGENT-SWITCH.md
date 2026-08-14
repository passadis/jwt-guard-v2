# Switching SentinelApp from the Embedded Agent to the Hosted Agent

**Status:** Gates 1 through 5 are complete. The first Gate 5 attempt on immutable version 6 exposed missing terminal output on tool-intent requests and rolled back. Immutable version 7 and the corrected SentinelApp client then passed tool-focused HostedShadow and final Hosted validation. SentinelApp revision `ca-edgegrd--0000020` now runs `Hosted` with no shadow allowlist. The reviewed Embedded rollback plan remains available and requires a new operator decision unless automatic rollback is responding to a validation failure.

## Purpose

This guide describes the recommended reversible path from SentinelApp's in-process Gate Explainer to the independently deployed Foundry Hosted Agent. It turns Phase 4 of the accepted migration design into an implementation and promotion sequence.

The switch changes only SentinelApp code, identity/configuration, and revisions, plus a narrow broker contract used by the Hosted Agent. It must not modify or restart Application Gateway, change its JWT policy, update SentinelGate, alter networking or DNS, or merge the two Terraform states.

The current position is:

- SentinelApp source contains the server-side router, embedded adapter, managed Responses client, session mapping, sanitized-evidence flow, and app-only broker routes;
- the running SentinelApp revision uses the managed Responses client with `AGENT_MODE=Hosted` explicitly configured;
- Hosted Agent version 7, Foundry IQ, monitoring, and evaluations are live in the isolated agent environment;
- the API exposes the dedicated `agent.scenario.execute` application role only to the exact Hosted Agent runtime principal;
- SentinelApp has Foundry Agent Consumer plus the single required user-identity-impersonation data action, both assigned at the exact Hosted Agent scope;
- `BROKER_BASE_URI` is fixed to the configured UI origin in Hosted Agent version 7, and the exact hosted endpoint/version are pinned in SentinelApp;
- the deployed client sends one server-derived pseudonymous `x-ms-user-identity` on session creation, conversation creation, and response invocation and uses `agent_session_id`;
- the embedded agent remains installed and tested as the reviewed rollback path.

## Recommended routing model

Keep the browser API unchanged and place a server-side router behind it:

```text
Browser
  -> authenticated /api/agent/chat and /api/agent/reset
  -> SentinelApp AgentRouter
       -> Embedded      -> current in-process AgentService
       -> HostedShadow  -> embedded response to browser
                        -> hosted comparison with safe/read-only evidence only
       -> Hosted        -> Foundry managed Responses endpoint

Hosted Agent
  -> direct read-only ARM and Log Analytics tools
  -> Foundry IQ toolbox
  -> managed-identity call to fixed SentinelApp broker routes
```

The browser must never receive the Hosted Agent endpoint, hosted session identifier, Foundry credential, or mode selector. It continues to call the same authenticated SentinelApp endpoints and receives the same SSE response shape.

## Modes

| Mode | User-visible response | Hosted execution | Intended use |
|---|---|---|---|
| `Embedded` | Embedded | None | Default, initial deployment, and rollback |
| `HostedShadow` | Embedded | Read-only comparison only | Controlled parity observation |
| `Hosted` | Hosted | Full approved hosted tool set | Promotion after explicit parity acceptance |

`Embedded` must remain the default when the setting is absent. An invalid value must fail startup instead of selecting a mode implicitly. The browser cannot override the mode per request.

Do not implement silent per-request fallback from `Hosted` to `Embedded`. That would hide a hosted outage, change the evidence path without telling the user, and make traces misleading. A hosted failure should produce a bounded, explicit unavailable response. The operator can then roll the whole SentinelApp revision back to `Embedded`.

## Configuration contract

Add environment-backed SentinelApp settings with strict startup validation:

| Setting | Purpose | Validation |
|---|---|---|
| `AGENT_MODE` | `Embedded`, `HostedShadow`, or `Hosted` | Case-insensitive enum; missing defaults to `Embedded`; unknown value fails startup |
| `HOSTED_SHADOW_TESTER_OBJECT_IDS` | Comma-separated Entra user object IDs eligible for shadow execution | Required and non-empty in `HostedShadow`; every value must be a unique lowercase canonical non-empty GUID; the authenticated tenant must also match `TENANT_ID` |
| `HOSTED_AGENT_RESPONSES_ENDPOINT` | Exact managed Responses endpoint | Required outside `Embedded`; absolute standard-port HTTPS URI; no user information or fragment; host must end in `.services.ai.azure.com`; path must be exactly `/api/projects/{project}/agents/{agent}/endpoint/protocols/openai/responses`; the only query is exactly `api-version=v1` |
| `HOSTED_AGENT_VERSION` | Expected immutable version for telemetry and session pinning | Canonical positive integer; must match the reviewed active version |
| `HOSTED_AGENT_TIMEOUT_SECONDS` | End-to-end hosted timeout | Bounded operator value; suggested initial range 15–90 seconds |
| `HOSTED_AGENT_PRINCIPAL_ID` | Expected Hosted Agent runtime service-principal object ID for broker authorization | Optional while the broker is disabled; when present it must be a non-empty canonical GUID and must exactly match the app-only caller's `oid` |

The endpoint and version are non-secret configuration. Authentication uses SentinelApp's existing user-assigned managed identity selected by `AZURE_CLIENT_ID`. The implementation must use the supported Foundry client/authentication contract and a fixed token audience; neither the browser nor a chat message can supply an endpoint, audience, host, path, scheme, header, or credential.

Do not place `APPLICATIONINSIGHTS_CONNECTION_STRING`, Search keys, model keys, or any hosted access token in SentinelApp configuration.

Any Hosted Agent environment change, including changing `BROKER_BASE_URI`, creates a new immutable Hosted Agent version. Version 7 is the reviewed remediation candidate. Any later environment or source change must create another immutable version and repeat the relevant smoke, tool, citation, session, trace-redaction, latency, and evaluation gates before use.

## SentinelApp code shape

### One host-neutral interface

Introduce a small interface matching the existing browser contract:

```csharp
public interface IGateExplainer
{
    IAsyncEnumerable<string> StreamAsync(
        string owner,
        string sessionId,
        string message,
        CancellationToken cancellationToken = default);

    ValueTask ResetSessionAsync(
        string owner,
        string sessionId,
        CancellationToken cancellationToken = default);
}
```

Use three implementations:

- `EmbeddedGateExplainer` wraps the current `AgentService` behavior without changing its tools or instructions;
- `HostedGateExplainer` invokes only the configured managed Responses endpoint through managed identity;
- `AgentRouter` selects the configured mode and implements shadow behavior.

The existing `/api/agent/chat` route should depend on `IGateExplainer`, retain its authentication, owner extraction, canonical session-ID validation, message-size limit, SSE framing, and `[DONE]` terminator. The frontend should require no endpoint or authentication change.

### Hosted invocation boundary

`HostedGateExplainer` must:

1. use an `HttpClient` or supported SDK client whose base endpoint is constructed once from validated startup configuration;
2. disable redirects and never combine endpoint components with caller-controlled URI data;
3. acquire a managed-identity access token only for the documented Foundry audience;
4. send only the bounded user message plus server-produced safe context;
5. normalize the Hosted Responses stream into the existing text-chunk SSE contract;
6. reject unexpected content types, oversized events, malformed responses, and version mismatches;
7. never automatically retry a Responses request because the model may already have initiated a scenario side effect; a future retry may be added only for a separately documented operation proven to be side-effect-free;
8. record redacted mode, version, correlation, duration, retry class, and outcome telemetry.

The browser's delegated bearer token must never be placed in the Hosted Agent request. Before any hosted call, SentinelApp should detect bearer/JWT-like material in ordinary chat input and refuse it with guidance to use the local token-analysis flow. Token-derived evidence is transferred only through the sanitized handle process below.

## Session ownership

SentinelApp remains authoritative for browser sessions. Key each entry by the authenticated canonical `tenantId:objectId` owner plus the canonical browser session GUID.

For a hosted session, store only:

- the opaque Foundry session identifier;
- the opaque Responses conversation identifier;
- the pinned Hosted Agent version;
- mode and creation/last-access timestamps;
- a per-session lock.

Neither hosted identifier is returned to or accepted from the browser. Reset and expiry remove the complete mapping so the next turn receives both a new hosted session and a new conversation. A mode or agent-version change invalidates existing mappings; embedded and hosted histories are never joined implicitly.

The current in-memory limits—30-minute expiry, 250 entries, serialized access, and one SentinelApp replica—should be preserved for the first integration. Scaling SentinelApp requires an approved distributed, encrypted session-reference store and is not part of this switch.

## Evidence broker

The hosted runtime already contains fixed clients for:

```text
GET  /api/agent/broker/decode/{handle:D}
POST /api/agent/broker/simulate
```

Add those routes to SentinelApp under a separate app-only authentication policy. They are not browser APIs and must not use the SPA's delegated `access_as_user` policy.

The broker policy must validate:

- signature, issuer, lifetime, and the existing API audience;
- the dedicated `agent.scenario.execute` application role;
- the expected tenant;
- the exact hosted runtime service principal/application identity recorded during deployment.

The hosted runtime obtains its app-only token through managed identity. The role is an Entra application permission, not Azure RBAC.

### Decode evidence

SentinelApp performs token parsing locally and creates only sanitized findings. It then stores those findings under a non-empty canonical GUID handle that is:

- generated server-side;
- bound to the authenticated owner and browser session;
- short-lived and single-purpose;
- consumed or expired after a bounded period;
- never backed by a persisted raw JWT.

Only SentinelApp inserts the handle into hosted context. A handle typed by the browser is untrusted input and must not grant access to another user's evidence. The broker returns a bounded schema containing sanitized claims, comparison results, evidence type, and limitations—never the original token.

### Deterministic scenarios

The broker accepts exactly `missing`, `valid`, `wrong_audience`, or `tampered`. It calls the existing fixed-target `GateTools` implementation. It cannot accept a host, path, scheme, headers, token, credential, or `user_replay`.

Caller-token replay stays inside the authenticated Enter the Gate BFF flow and is represented to the Hosted Agent only as already-sanitized observed evidence.

`HostedShadow` must not run deterministic scenarios a second time. Shadow comparisons use the already-produced sanitized result, or read-only ARM, Log Analytics, and IQ operations only.

## Identity and state ownership

Keep the Terraform states permanently separate:

| Change | Owner |
|---|---|
| SentinelApp mode, endpoint/version settings, broker routes, API app role, and hosted-principal app-role grant | Existing `infra/` state and SentinelApp source |
| SentinelApp managed identity permission to invoke the hosted agent | `agent-infra/` state: Foundry Agent Consumer and the single `UserIdentityImpersonation/action` data action, both assigned at the exact agent scope |
| Hosted runtime's existing gateway, workspace, and Search readers | `agent-infra/` state |
| Hosted `BROKER_BASE_URI` and immutable hosted version | Hosted Agent `azure.yaml` and `azd deploy` workflow |

Pass principal/resource identifiers as reviewed explicit variables. Do not add `terraform_remote_state`, copy state, import either stack into the other, or give either identity Contributor, Owner, Key Vault access, Search administration, or subscription-wide Reader.

Every integration plan must be reviewed independently. An `infra/` plan may contain only the expected Entra app-role, app-role assignment, SentinelApp configuration/RBAC, and Container App revision changes. An `agent-infra/` plan may contain only the expected agent-scoped invocation role for the SentinelApp identity. Neither plan may modify, replace, or destroy Application Gateway, SentinelGate, networking, DNS, certificates, Search data, or unrelated resources. Do not change `gateway_config_generation` as part of agent integration. Its clean-environment default is `0`; this deployment remains at `1` after the separately approved recovery configuration push.

## Implementation sequence

### Gate 1 — Code in rollback mode

**Completed locally on 7 August 2026:** the interface, embedded adapter, Hosted Responses client, three-mode router, version-backed session/conversation mapping, sanitized evidence staging, app-only broker, frontend evidence flow, and focused tests are present. The implementation uses the documented `https://ai.azure.com/.default` audience, creates a session with `version_indicator.type = version_ref`, creates a separate Responses conversation, rejects JWT-like hosted chat input, disables redirects, and never silently falls back from `Hosted`.

At Gate 1 completion, `AGENT_MODE` was absent and therefore defaulted to `Embedded`; `BROKER_BASE_URI` was unset. The browser could not select a mode or receive the endpoint, hosted session ID, conversation ID, or evidence handle. Broker authorization failed closed because `HOSTED_AGENT_PRINCIPAL_ID`, the application role, and its assignment had not yet been introduced. No Azure resource, running Container App, Application Gateway, DNS record, certificate, Terraform state, or Hosted Agent version was changed by Gate 1.

The focused local Gate 1 tests cover configuration fail-closed behavior, exact endpoint construction, managed-identity token isolation, version pinning, malformed/failed hosted responses, session isolation, raw-token refusal, single-use evidence, exact broker tenant/role/principal authorization, scenario allowlisting, and shadow output suppression. The full repository check result belongs in the associated implementation handoff; it is rerun before Gate 2 planning.

### Gate 2 — Identity and broker foundation

**Completed on 7 August 2026:** the two independently reviewed saved plans were applied to their existing isolated local states. The existing-stack plan added the stable `agent.scenario.execute` API application role, granted it only to the Hosted Agent runtime principal, and configured SentinelApp with explicit `AGENT_MODE=Embedded` and the expected hosted principal. The agent-stack plan added Foundry Agent Consumer and a custom role containing only `Microsoft.CognitiveServices/accounts/AIServices/agents/endpoints/UserIdentityImpersonation/action`; both assignments are scoped to `jwt-sentinel-gate-explainer`, not the project or account.

SentinelApp image `sentinel-app:gate2-20260807-030628` was built and deployed alone. Its new revision became ready with 100% traffic and returned a trusted-TLS health response. SentinelGate was not rebuilt or revised. Application Gateway, its retained recovery generation value `1`, networking, DNS, certificates, Search data, and Hosted Agent version were unchanged.

The protected-listener matrix passed `401, 401, 200, 401` for missing, wrong-audience, valid, and tampered tokens. The successful response was strict SentinelGate evidence with the expected tenant, canonical GUIDs, `gatewayValidated=true`, and `routingContextConsistent=true`. An authenticated Embedded chat completed its SSE stream and reset successfully. Live broker checks returned 401 without authentication and 403 for a delegated user. The 66-test SentinelApp suite verified the additional missing-role and wrong-principal denial cases. Post-apply plans for both states returned no changes.

At Gate 2 completion, hosted execution was not active: `BROKER_BASE_URI`, `HOSTED_AGENT_RESPONSES_ENDPOINT`, and `HOSTED_AGENT_VERSION` were absent from the running Container App, and the Hosted Agent had not yet been republished with broker configuration.

### Gate 3 — Broker-enabled Hosted Agent version

**Completed on 7 August 2026:** `BROKER_BASE_URI` was set to the exact `https://guard.mvps.gr` origin and direct-source `azd deploy` created immutable Hosted Agent version 6. `azd provision` was not run. The runtime identity, toolbox, and resource-scoped RBAC did not change.

The first live broker attempt exposed a framework-claim mapping mismatch: ASP.NET Core JwtBearer represented the Entra application role as `ClaimTypes.Role`, while the handler inspected only raw `roles`. The correction accepts both representations but still requires the exact tenant, exact Hosted Agent principal, exact `agent.scenario.execute` value, and absence of delegated scopes. SentinelApp alone was rebuilt and deployed; SentinelGate and Application Gateway were untouched.

Version 6 then passed the four deterministic broker scenarios (`401, 200, 401, 401`), invalid-handle refusal, exact IQ repository citations, same-session continuity, fresh-conversation isolation, and the existing evaluation workflow. The primary run passed all 15 samples, task adherence `15/15`, and groundedness `4/4` applicable samples; transient security-judge errors were closed by bounded retries so every case received a passing security-rubric score. In the last-24-hour trace sample, 185 agent invocations, 238 model calls, and 59 tool spans had zero failed spans. The aggregate redaction scan across 2,926 scoped rows found no JWT-like, bearer, client-secret, or private-key indicators. Observed P95 was approximately 6.94 seconds for `invoke_agent` and 1.41 seconds for `execute_tool`.

The reviewed `infra/` plan contained only the in-place SentinelApp addition of `HOSTED_AGENT_RESPONSES_ENDPOINT` and `HOSTED_AGENT_VERSION=6`: `0 added, 1 changed, 0 destroyed`. Its apply completed and the post-apply plan converged with no changes. The current revision remains `AGENT_MODE=Embedded`, serves 100% traffic, and passed trusted health plus the protected-listener matrix. Application Gateway was later recovered through a separately approved full AzAPI configuration push and now remains at generation `2`; it was not restarted.

Gate 3 pins readiness configuration; it does not send browser chat traffic to the Hosted Agent. Gate 4 still requires a separately saved and reviewed SentinelApp-only mode plan.

### Gate 4 — Hosted shadow

**Approved and locally prepared on 7 August 2026:** SentinelApp and Terraform now enforce a fail-closed `HOSTED_SHADOW_TESTER_OBJECT_IDS` allowlist. Shadow execution occurs only when the authenticated owner has the configured tenant and an allowlisted canonical Entra object ID. Non-allowlisted users continue to receive the embedded answer without a hosted comparison. An empty, duplicate, malformed, empty-GUID, uppercase, or otherwise noncanonical allowlist fails validation. The browser cannot supply or override the allowlist.

**Preflight pause on 10 August 2026:** immutable image `sentinel-app:gate4-shadowallow-20260810-225106` was deployed as healthy revision `ca-edgegrd--0000008` with `AGENT_MODE=Embedded`. The first protected-listener matrix reached the tampered-token case only after the preceding missing, wrong-audience, and valid cases passed, then received HTTP 500 after approximately 60 seconds. A targeted follow-up returned a 60.41-second HTTP 500 for a missing token and a prompt HTTP 401 for a tampered token. This mixed behavior is not an acceptable Gate 4 baseline. No HostedShadow plan was created or applied. A separately approved generation increment and full AzAPI gateway configuration push must restore a consistently passing matrix before this sequence resumes.

**Completed shadow and rollback on 13 August 2026:** the approved generation-2 full AzAPI recovery changed only the gateway in place and was followed by three passing `401, 401, 200 SentinelGate, 401` matrices. A saved SentinelApp-only plan then enabled `HostedShadow` for tester `7e35709d-f693-4896-9599-146e27046ef4` for exactly 60 minutes. Four read-only prompts returned HTTP 200 embedded SSE with `[DONE]`; hosted content remained discarded. App Insights correlated exactly four hosted invocations, each failing before inference when `POST /responses` returned 404 `Azure.AI.AgentServer.Responses.ResourceNotFoundException` after `storage/history/item_ids` returned 404. Sixty scoped trace rows contained no JWT-like, bearer, client-secret, or private-key indicators; model calls, tool calls, input tokens, and output tokens were all zero.

The failure is a SentinelApp client-contract defect. The deployed revision creates the conversation without the server-derived `x-ms-user-identity`, supplies that identity only on `/responses`, and sends `session_id`; the current Hosted Responses contract requires consistent delegated identity scope and uses `agent_session_id`. The scheduled saved-plan rollback completed with `0` additions, `1` in-place SentinelApp change, and `0` destroys. Revision `ca-edgegrd--0000010` is ready on the same immutable image, explicitly uses `Embedded`, has no tester allowlist, passed trusted UI health and the complete protected-listener matrix, and Terraform reports no changes.

The corrected implementation asserts the same pseudonymous identity on session, conversation, and response calls, sends `agent_session_id`, and retains the explicit version-pinned session plus conversation binding. Local reset removes the complete mapping; if a future implementation adds a remote history or delete request, that operation must carry the same identity.

**Corrected repeat completed on 14 August 2026:** the activation plan contained only the in-place SentinelApp mode and allowlist change. Four allowlisted read-only prompts returned embedded HTTP 200 SSE with `[DONE]`, while four Hosted comparisons completed with HTTP 200 history and Responses operations, successful model/agent calls, and one continuous session/conversation. Count-only inspection found no JWT-like, bearer, client-secret, or private-key indicators in 108 correlated telemetry rows. Recorded usage was 3,195 input and 817 output tokens; Hosted P95 was about 11.8 seconds. A toolbox GET method probe returned 405, but successful POST operations and all four completed invocations show it was non-fatal. The pre-reviewed rollback ran at the 60-minute deadline, restored `Embedded`, removed the allowlist, passed trusted health and the complete JWT matrix, and converged with no Terraform changes. Gate 4 is complete; Gate 5 remains a separate decision.

The live activation sequence remains:

1. Record the explicitly approved tester object IDs and bounded observation duration; do not infer a tester from the Azure CLI identity.
2. Build and deploy the locally validated SentinelApp candidate while keeping `AGENT_MODE=Embedded`, then verify revision health and the Stage 1 matrix.
3. Produce a saved `infra/` plan changing only SentinelApp mode/configuration to `HostedShadow` and setting the approved allowlist.
4. Apply it only after confirming `0` additions, `1` in-place SentinelApp change, and `0` destroys; no gateway generation change is involved.
5. Use only read-only comparison prompts. Scenario and token-like prompts are intentionally excluded so shadow mode cannot execute a scenario twice or place bearer material in hosted traffic.
6. Compare evidence fidelity, citations, session continuity, failure behavior, latency, token usage, cost, and redaction telemetry for the bounded window.
7. Confirm user-visible answers came only from the embedded path, then return globally to `Embedded` with another reviewed SentinelApp-only plan unless a separate decision explicitly extends the window.

### Gate 5 — Hosted promotion

Promote only after explicit hosted-parity acceptance:

1. save and review the configuration-only plan for `AGENT_MODE=Hosted`;
2. confirm the exact endpoint and immutable version;
3. apply the SentinelApp-only revision;
4. run the complete browser, BFF, protected-listener, trusted-TLS, logs, tools, citations, session-isolation, and Agent matrix;
5. verify App Insights shows the expected mode/version and contains no token, secret, connection string, or raw broker evidence.

**14 August 2026 outcome:** the activation plan contained exactly `0` additions, one in-place SentinelApp change, and `0` destroys. Revision `ca-edgegrd--0000014` ran in `Hosted` with the pinned version 6 endpoint, unchanged image, and no shadow allowlist. Trusted health, `/api/whoami`, strict BFF entry, IQ citations, and multi-turn continuity passed. Both attempts to call the live gateway-configuration tool returned the controlled Hosted failure response. SentinelApp reported `Responses stream ended without a completion event`; Foundry recorded successful HTTP 200 agent/model/Responses operations and response persistence but no broker-tool dependency. The saved rollback plan was therefore applied, producing healthy revision `ca-edgegrd--0000015` in `Embedded`. Embedded streaming, both gateway backends, and the complete `401, 401, 200 SentinelGate, 401` matrix passed. Do not promote the unchanged candidate again; first establish why tool-intent responses omit a terminal event or fail to invoke the broker, correct the contract/runtime behavior, and repeat Gate 4 or an equivalently bounded tool-focused observation.

**Version 7 remediation and shadow proof:** version 6 direct replay showed that the broker itself remained functional, but tool choice was not deterministic and the SentinelApp client recognized only a narrow terminal-event shape. Version 7 instructions now require live configuration and log questions to call the corresponding tools. The corrected client parses both SSE headers and JSON event types, rejects mismatches, treats failed/incomplete/error events as terminal, requires a completed status, and performs one fresh-session retry only for safe read-only zero-output protocol failures. It never retries scenarios, pending token evidence, timeouts, dependency failures, or partial output.

The corrected SentinelApp image was deployed while still `Embedded`, and Terraform pinned only `HOSTED_AGENT_VERSION=7` with a reviewed `0/1/0` plan. The strict protected-listener matrix and embedded streaming passed. A second reviewed `0/1/0` plan enabled `HostedShadow` only for tester `7e35709d-f693-4896-9599-146e27046ef4`; a rollback plan was saved before traffic. Four read-only prompts returned complete embedded SSE while all four hosted comparisons completed with `RetryClass=none`. Correlated v7 traces proved `execute_tool get_gateway_config` plus ARM HTTP 200, `execute_tool query_gate_logs` plus Log Analytics HTTP 200, IQ knowledge-base retrieval plus MCP HTTP 200, and a same-session follow-up with no new session or conversation. The saved rollback restored healthy revision `ca-edgegrd--0000019` to `Embedded`, removed the allowlist, and left Terraform converged.

The v7 15-case evaluation completed 13/15 because two managed responses had response/trace IDs but zero output and no finish reason. Both synthetic cases returned the required behavior on isolated direct replay and passed a bounded two-case evaluation retry with task adherence and security rubric `2/2`, zero failures, and zero errors. This closes the v6 defect evidence but does not itself authorize another global Hosted activation.

**Version 7 Gate 5 completed on 14 August 2026:** the explicitly approved saved plan changed only SentinelApp `AGENT_MODE` from `Embedded` to `Hosted`, with `0` additions, one in-place change, and `0` destroys. Revision `ca-edgegrd--0000020` retained image `sentinel-app:hosted-recovery-20260814-0155`, pinned version 7, and had no shadow allowlist. Trusted UI assets, delegated UI APIs, strict BFF entry, both healthy backend pools, the protected-listener `401, 401, 200 SentinelGate, 401` matrix, the deterministic `401, 200, 401, 401` scenario matrix, live gateway and log tools, sanitized decode, exact IQ citations, continuity/reset, and security refusals passed. Correlated telemetry recorded 12/12 successful version 7 invocations and 19/19 successful model calls; one safe read-only IQ request used the single permitted fresh-session retry. P50/P95 invocation latency was approximately 5.15/17.64 seconds, with 53,775 input and 2,977 output tokens. No dependency failed, and 537 correlated rows contained no JWT-like, bearer-value, client-secret, private-key, or storage-key patterns. Terraform reported no changes. The spent activation plan was removed; `infra/tfplan-gate5-v7-rollback` remains the reviewed global Embedded rollback artifact.

## Rollback

Rollback is deliberate and global:

1. set `AGENT_MODE=Embedded` in the existing infrastructure configuration;
2. produce a saved plan showing only the SentinelApp configuration/revision change;
3. apply or activate the reviewed embedded revision;
4. verify embedded chat reset/continuity, all four tools, Enter the Gate, and the complete protected-listener matrix;
5. record the hosted failure and preserve agent traces for diagnosis.

Rollback must not restart or update Application Gateway. Do not destroy the agent resource group during incident recovery. Hosted infrastructure cleanup is a separate `agent-infra` destroy review.

## Minimum test additions

### Unit and contract tests

- missing mode defaults to `Embedded`; unknown mode fails startup;
- endpoint validation rejects HTTP, non-default ports, credentials, query/fragment, unapproved hosts, and path changes;
- the browser cannot select mode, endpoint, version, token audience, session, or conversation identifiers;
- no caller authorization header or JWT-like chat input reaches the hosted client;
- owner/session binding, expiry, reset, concurrency, mode changes, and version changes isolate conversations;
- broker authentication requires the tenant, application role, and expected hosted identity;
- evidence handles reject empty, noncanonical, expired, reused, caller-injected, and cross-owner values;
- simulation accepts only the four fixed scenarios and cannot derive a target URI from input;
- hosted timeout, malformed stream, not-ready, throttling, and dependency failures are bounded and observable;
- shadow mode never returns hosted content or repeats a scenario side effect.

### Live acceptance

- `Embedded` repeats the currently passing matrix before any promotion;
- `HostedShadow` produces correlated comparisons without changing browser output;
- `Hosted` demonstrates tool parity, exact IQ citations, multi-turn continuity, reset, expiry, and cross-user isolation;
- hosted traces and broker logs contain no tokens, secrets, connection strings, raw JWTs, or complete sensitive payloads;
- a deliberate rollback to `Embedded` succeeds without an Application Gateway change or restart.

## Operational rules

- Never use `azd provision`; Terraform owns the Foundry project and foundation.
- A Hosted Agent source/configuration deployment uses `azd deploy` and creates an immutable version.
- A SentinelApp mode change is an existing-stack configuration/revision change, not an agent-infrastructure merge.
- Never pass the SPA token, daemon secret, raw pasted JWT, Terraform state, or provider credentials to Foundry.
- Never use `x-original-host` as authentication proof in either implementation.
- Never remove the embedded implementation until a later ADR approves that irreversible step after a sustained observation period.

## Decision checklist

Before implementation begins, explicitly approve:

- the exact Foundry Responses endpoint and token audience supported by the selected SDK/API version;
- the new broker-enabled Hosted Agent version;
- the `agent.scenario.execute` app-role ID and hosted principal assignment;
- the SentinelApp-to-hosted invocation scope;
- the shadow tester boundary and observation duration;
- latency, availability, citation, and monthly cost promotion thresholds;
- whether the initial in-memory hosted-session mapping is acceptable while SentinelApp remains at one replica.

Gates 1 through 5 now include the version 7 remediation, live tool traces, bounded evaluation closure, endpoint/version pin, successful tool-focused shadow observation, and final Hosted promotion. The first Gate 5 attempt remains a recorded v6 failure. Any rollback, new version, permission change, corpus/toolbox update, or further deployment requires a fresh review and explicit approval.

## Related documents

- [Foundry Hosted Agent and Foundry IQ Migration Design](AGENT-MIGRATION.md)
- [Foundry Agent Isolated Implementation Plan](AGENT-IMPLEMENTATION-PLAN.md)
- [JWT Sentinel Architecture](ARCHITECTURE.md)
- [Accepted Decisions](DECISIONS.md)
- [Test Matrix](TEST-MATRIX.md)
- [Operator Guide](OPERATOR-GUIDE.md)

This guide is not currently part of the seven-document Foundry IQ allowlist. Adding it to the knowledge corpus and republishing the index requires a separate corpus-version review.
