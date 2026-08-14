# JWT Sentinel Decisions

This log records accepted decisions that govern the current implementation and separately identified future designs. Earlier alternatives in `DESIGN.md` are historical when they conflict with these ADRs or `AGENTS.md`.

## ADR-001 — Isolate the UI and protected planes in two Container Apps

**Status:** Accepted

**Decision:** Use SentinelApp for the UI/control/AI plane and SentinelGate for the protected demonstration plane. Each has a dedicated Application Gateway backend pool, probe, backend settings, Container App, image, and managed identity. The UI listener cannot route to SentinelGate, and the protected listener cannot route to SentinelApp.

**Reason:** A shared runtime made a path-level trust boundary responsible for protecting the gateway-injected identity. Structural routing isolation makes bypass substantially harder and keeps SentinelGate minimal.

**Consequences:** Two images and revisions must be deployed and monitored. SentinelGate receives ACR pull only and contains no Agent, Foundry, logging-query, daemon, or SPA capability.

## ADR-002 — Forward the caller token through an authenticated BFF

**Status:** Accepted

**Decision:** The SPA calls SentinelApp `POST /api/gate/enter`. SentinelApp reuses the bearer token from that authenticated request and forwards it server-side to the configured protected HTTPS origin at `/enter`.

**Reason:** Browser navigation does not attach bearer tokens, and a direct cross-origin call can be blocked when the JWT-protected listener rejects an unauthenticated CORS preflight.

**Consequences:** The browser cannot choose the forwarding host, path, or scheme. The token is not logged, persisted, returned, placed in Agent prompts, or stored in Agent sessions. Transport failures and gateway/backend responses remain distinguishable.

## ADR-003 — Keep the Agent in SentinelApp for Stage 1

**Status:** Accepted for Stage 1

**Decision:** Retain the in-process Microsoft Agent Framework 1.15 GateExplainer in SentinelApp with the four existing tools.

**Reason:** This is the verified implementation. Authentication, authorization, session ownership, tool permissions, output validation, redaction, and error handling are already enforced by the application.

**Consequences:** SentinelApp remains at one replica while sessions are in memory. The Agent continues to use the configured Foundry/Azure AI model through managed identity.

## ADR-004 — Defer Foundry Hosted Agent and Foundry IQ migration

**Status:** Superseded for design by ADR-013; Stage 1 implementation remains unchanged

**Decision:** Do not begin a Foundry Hosted Agent or Foundry IQ migration in Stage 1.

**Reason:** A hosted migration changes identity, networking, data access, session ownership, tool execution, telemetry, and failure modes. Foundry IQ also requires an explicit knowledge-source and data-governance design.

**Revisit when:** The exact supported SDK/API path, agent identity, networking, RBAC, knowledge indexing, token handling, observability, cost, migration, and rollback plan have been reviewed and tested independently.

**Update (August 2026):** The isolated Hosted Agent foundation, corpus, IQ toolbox, and immutable version 7 runtime were deployed under separately approved gates. After bounded shadow validation, an explicitly approved Gate 5 plan promoted only SentinelApp to `Hosted`. The embedded Agent Framework implementation remains the reviewed rollback path. Application Gateway is unchanged.

**Gate 1 update (7 August 2026):** The reversible integration boundary is implemented and tested in source while remaining operationally disabled. Missing `AGENT_MODE` selects `Embedded`; no hosted endpoint, app role, principal assignment, broker base URI, or application revision has been deployed. Later gates retain separate approval.

**Gate 2 update (7 August 2026):** The stable `agent.scenario.execute` application role is granted only to the exact Hosted Agent runtime principal. SentinelApp receives Foundry Agent Consumer plus a custom role containing only the required `UserIdentityImpersonation/action`, both assigned at the exact Agent scope. The SentinelApp-only candidate is deployed with explicit `AGENT_MODE=Embedded`; no broker base URI, hosted endpoint/version, gateway change, or Hosted Agent version was deployed.

**Gate 3 update (7 August 2026):** Hosted Agent version 6 was deployed through direct-source `azd deploy` with `BROKER_BASE_URI` fixed to `https://guard.mvps.gr`; `azd provision` was not run. SentinelApp's broker authorization was corrected to recognize both the raw Entra `roles` claim and ASP.NET Core's mapped `ClaimTypes.Role`, without changing the exact tenant, principal, role, or app-only requirements. Broker, IQ citation, session, evaluation, trace-redaction, latency, and Stage 1 regression checks passed. Terraform then pinned only the exact managed Responses endpoint and version 6 in SentinelApp; `AGENT_MODE=Embedded` remains authoritative and Gate 4 requires separate approval.

## ADR-005 — Keep Application Gateway under AzAPI ownership

**Status:** Accepted

**Decision:** Manage the complete Application Gateway as `Microsoft.Network/applicationGateways@2025-05-01` through one `azapi_resource`.

**Reason:** The required `entraJWTValidationConfigs` policy and routing-rule reference are preview properties not represented by the selected AzureRM gateway resource.

**Consequences:** Gateway changes use the same full AzAPI body. The older gateway CLI update command is prohibited because it can silently omit preview fields.

`gateway_config_generation` affects only a visible tag on the same resource. A reviewed increment triggers a full in-place resubmission after an approved restart. `prevent_destroy = true` blocks replacement or destruction; a plan that cannot update in place must fail.

## ADR-006 — Require NAT on the Application Gateway subnet

**Status:** Accepted

**Decision:** Attach a NAT Gateway and public IP to the Application Gateway subnet and make the AzAPI gateway depend explicitly on those associations.

**Reason:** JWT validation requires outbound HTTPS access to Entra endpoints, and Application Gateway reaches the public Container App FQDNs through subnet egress. Dependable default outbound access must not be assumed.

**Consequences:** Container Apps ingress allows both the Application Gateway frontend public IP and NAT public IP. Removing either address can break routing or weaken assumptions about the backend boundary.

## ADR-007 — Preserve ACA FQDN backend Host and TLS/SNI

**Status:** Accepted

**Decision:** Keep `pickHostNameFromBackendAddress = true` in both backend HTTP settings.

**Reason:** Application Gateway must use each Container App FQDN as the backend `Host` header and TLS/SNI name.

**Consequences:** SentinelGate does not expect the public protected hostname in the backend `Host` header. Client-originated `x-original-host` may be checked as supplementary routing context, but its match is neither authentication nor proof of JWT validation.

## ADR-008 — Trust the injected identity only inside the combined boundary

**Status:** Accepted

**Decision:** Trust `x-msft-entra-identity` only when the request path is constrained by the dedicated protected listener and attached JWT `Deny` policy, the SentinelGate-only pool, restricted Container Apps ingress, and strict canonical identity parsing.

**Reason:** Header presence alone is forgeable and is not sufficient proof.

**Consequences:** SentinelGate rejects missing, duplicate, empty, malformed, noncanonical, empty-GUID, or unexpected-tenant identity values. `x-original-host` remains an additional consistency check only.

## ADR-009 — Use three new Entra applications per environment

**Status:** Accepted

**Decision:** Create separate API, SPA, and daemon applications for every clean environment.

**Reason:** Delegated browser access and controlled app-only simulations have different OAuth flows and permissions.

**Consequences:** The API exposes `access_as_user` and the daemon role. The SPA uses authorization code with PKCE. The daemon uses client credentials. The API identifier URI remains owned by `azuread_application_identifier_uri`, with `identifier_uris` ignored on the application resource to prevent accidental reset.

## ADR-010 — Use managed identity and least privilege for Azure access

**Status:** Accepted

**Decision:** SentinelApp receives ACR pull, Foundry/OpenAI user, resource-group Reader, and Log Analytics Reader. SentinelGate receives ACR pull only. Application Gateway receives only the Key Vault access needed for its certificate.

**Reason:** Azure service keys and broad Owner/Contributor grants are unnecessary.

**Consequences:** Any new tool or hosted-agent migration must justify additional permissions independently.

## ADR-011 — Roll certificates under one unversioned Key Vault secret URI

**Status:** Accepted

**Decision:** Terraform creates a bootstrap certificate and Application Gateway references the unversioned secret URI. `issue-cert.ps1` creates later versions under the same certificate name.

**Reason:** Version rollover does not require rebuilding the listener or changing Terraform configuration.

**Consequences:** Issuance defaults to staging. Production requires the explicit `-AcmeEnvironment Production` option and approval. The certificate script never restarts Application Gateway. If a restart is separately approved, it is followed by the AzAPI configuration-generation push and full acceptance matrix.

## ADR-012 — Keep provider selection reproducible and state isolated

**Status:** Accepted

**Decision:** Commit `infra/.terraform.lock.hcl`. Ignore `.terraform/`, state, populated tfvars, plans, backend configuration, certificates, secrets, tokens, and generated artifacts. Use fresh local state initially or an explicitly approved unique remote key.

**Reason:** The clean rebuild must not share state or provider drift with the running environment.

**Consequences:** `terraform init -backend=false` is valid for local static validation and does not initialize an Azure backend or remote state. Real planning requires a separately reviewed state location and environment-specific variables.

## ADR-013 — Permanently isolate the Hosted Agent migration and preserve embedded rollback

**Status:** Accepted; Hosted v7 promoted after Gate 5 parity validation, with Embedded rollback preserved

**Decision:** Build the Foundry Hosted Agent, Foundry IQ, Search, knowledge storage, observability, budgets, and agent identities in a separate resource group and Terraform state that will never be merged into the Stage 1 `infra/` state. Integrate the managed hosted endpoint only through explicit SentinelApp configuration and a reversible `Embedded`/`Hosted` server-side switch. Preserve the embedded Agent Framework implementation until hosted parity has been validated and accepted.

The hosted implementation reuses the existing .NET 10 Agent Framework 1.15 instructions and tool contracts. Its dedicated runtime identity receives only resource-scoped Application Gateway Reader, workspace-scoped Log Analytics Reader, query-only Search data permissions, and the minimum execution permission inside its own Foundry project. A separate ingestion identity owns Search publication permissions.

SentinelApp remains responsible for deterministic scenario execution and caller-token handling. A hosted agent may request an allowlisted scenario only through a managed-identity-authenticated broker with a narrow Entra application role; it cannot choose the destination, path, scheme, headers, or token. Foundry IQ contains only an allowlisted, versioned documentation corpus and must provide citations.

**Reason:** Permanent state and resource-group isolation makes cost, lifecycle, preview risk, and rollback independently controllable. Keeping deterministic and token-sensitive operations in the existing authenticated BFF prevents raw credentials from entering hosted sessions, traces, evaluations, or knowledge indexes. The feature switch allows rollback without changing or restarting Application Gateway.

**Implementation update (7 August 2026):** Gate 1 uses a server-side three-mode router (`Embedded`, `HostedShadow`, `Hosted`) with no browser selector and no silent hosted-to-embedded request fallback. Hosted calls use a validated fixed Azure AI Services Responses endpoint, the `https://ai.azure.com/.default` managed-identity audience, an explicitly version-backed session, and a separate opaque conversation. Broker calls are a separate app-only trust boundary; delegated tokens are rejected even if they carry a role. The raw-token analysis path stores only short-lived sanitized evidence and never returns its opaque handle to the browser. The running deployment remains `Embedded`, so this implementation does not constitute promotion.

**Gate 2 authorization update (7 August 2026):** The Hosted Agent runtime alone receives the API's `agent.scenario.execute` application role. SentinelApp's user-assigned identity receives the built-in Foundry Agent Consumer role and a custom role containing only `Microsoft.CognitiveServices/accounts/AIServices/agents/endpoints/UserIdentityImpersonation/action`; both assignments are at the exact `jwt-sentinel-gate-explainer` scope. The custom data action is required for the server-derived `x-ms-user-identity` isolation value and is not included in the built-in consumer role. This does not authorize project administration or broaden Stage 1 resource access.

**Gate 3 integration update (7 August 2026):** The Hosted Agent uses only the configured `https://guard.mvps.gr` broker origin and was redeployed as immutable version 6. SentinelApp pins the exact managed Responses endpoint and version as a required pair, while retaining `AGENT_MODE=Embedded`. The pin is readiness and rollback configuration, not traffic activation. No gateway, SentinelGate, network, DNS, certificate, Search, or agent-foundation Terraform resource changed.

**Gate 4 preparation update (7 August 2026):** Shadow execution now requires both `AGENT_MODE=HostedShadow` and a non-empty operator-configured `HOSTED_SHADOW_TESTER_OBJECT_IDS` set. Every tester value must be a unique lowercase canonical non-empty GUID. SentinelApp also requires the authenticated owner tenant to equal the configured tenant before comparing the owner object ID with the allowlist. A non-allowlisted user receives only the embedded response and produces no hosted call. The browser cannot select the mode, tester, endpoint, or version. At that time, live activation remained pending the explicit tester IDs, bounded observation duration, immutable image deployment in `Embedded`, and review of a SentinelApp-only Terraform plan; the 13 August outcome below supersedes that preparatory status.

**Gate 4 outcome (13 August 2026):** After a generation-2 full AzAPI gateway recovery and three passing protected-listener matrices, a reviewed SentinelApp-only plan enabled `HostedShadow` for one approved tester for 60 minutes. Four safe read-only prompts returned only embedded SSE responses. Foundry telemetry recorded four hosted invocations and four `POST /responses` 404 failures with `Azure.AI.AgentServer.Responses.ResourceNotFoundException`; no model/tool calls or token usage occurred, and the count-only redaction scan found no JWT-like, bearer, client-secret, or private-key indicators. The client created the conversation without its server-derived `x-ms-user-identity`, added that header only to `/responses`, and used the obsolete `session_id` field. Conversation/history resources are identity-scoped, so the hosted runtime could not resolve the history. The scheduled reviewed rollback restored `Embedded`, removed the tester allowlist, retained the immutable image, passed the JWT matrix, and converged with no Terraform changes. Another shadow window requires a corrected client and tests that enforce consistent delegated identity across session, conversation, and response operations and the current `agent_session_id`/conversation binding contract.

**Local client correction (13 August 2026):** The SentinelApp source candidate now derives the pseudonymous delegated-user value once per owner-bound session mapping, stores it with the opaque session/conversation IDs, and applies the identical `x-ms-user-identity` value to version-pinned session creation, conversation creation, and response invocation. The Responses payload now uses `agent_session_id` and retains the opaque conversation binding. Regression and static checks enforce the current field and same-identity invariant. This is a source/test correction only: it did not deploy an image, activate `HostedShadow`, change either Terraform state, or produce new live parity evidence. Another Gate 4 window remains a separate approval, and Gate 5 remains blocked.

**Embedded deployment update (13 August 2026):** ACR quick build `dta` produced immutable image `sentinel-app:hosted-contract-20260813-191559`, which was deployed only to SentinelApp as revision `ca-edgegrd--0000011`. The revision retained `AGENT_MODE=Embedded`, had no shadow tester allowlist, and received 100% traffic. Trusted UI health, the vendored MSAL asset, both Application Gateway backend pools, and the complete `401, 401, 200 SentinelGate, 401` matrix passed. SentinelGate remained on revision `ca-edgegrd-gate--0000003`; Application Gateway and both Terraform states were unchanged. This proves safe Embedded deployment and Stage 1 compatibility, not the corrected Hosted contract. Another Gate 4 window remains separately gated, and Gate 5 remains blocked.

**Corrected Gate 4 repeat (13–14 August 2026):** A saved plan containing `0` additions, one in-place SentinelApp change, and `0` destroys enabled `HostedShadow` only for tester `7e35709d-f693-4896-9599-146e27046ef4`. A separately saved rollback plan was created before test traffic. Four read-only prompts returned embedded HTTP 200 SSE with `[DONE]` in 4.63–11.83 seconds. SentinelApp logged four completed HostedShadow invocations and no failures. Foundry telemetry recorded four `storage/history/item_ids` HTTP 200 calls, four `/responses` HTTP 200 calls, four successful model calls, four successful agent invocations, and successful IQ toolbox POSTs. All four calls used one session and one conversation; 108 correlated rows contained zero JWT-like, bearer, client-secret, or private-key indicators. Recorded usage was 3,195 input and 817 output tokens, with Hosted P95 about 11.8 seconds. One toolbox GET returned 405 in 63 ms, but no invocation failed and toolbox POSTs returned 200/204. At the 60-minute deadline, the reviewed rollback restored `Embedded`, removed the allowlist, produced healthy revision `ca-edgegrd--0000013`, passed the complete JWT matrix, and converged with no Terraform changes. This clears the prior client-contract blocker but does not itself authorize Gate 5.

**Gate 5 outcome (14 August 2026):** An explicitly approved saved plan with `0` additions, one in-place SentinelApp change, and `0` destroys promoted only `AGENT_MODE` to `Hosted`, creating revision `ca-edgegrd--0000014`. Trusted health, the authenticated UI API, strict BFF entry, repository citations, and same-session continuity passed. Two attempts to inspect the live gateway through the Hosted Agent returned SentinelApp's controlled failure response. SentinelApp logged `HostedProtocolException: Responses stream ended without a completion event` in 608–782 ms. Foundry telemetry showed successful HTTP 200 `/responses`, model, agent, history, and response-persistence operations but no broker-tool dependency. Because tool parity is mandatory, the pre-reviewed `0/1/0` rollback restored `Embedded` as healthy revision `ca-edgegrd--0000015`. Both gateway backends were healthy, embedded streaming passed, and the protected-listener matrix passed `401, 401, 200 SentinelGate, 401`. Application Gateway, SentinelGate, agent infrastructure, DNS, certificates, and both state ownership boundaries were unchanged.

**Version 7 remediation (14 August 2026):** Hosted Agent instructions now require fresh live gateway and log questions to invoke their evidence tools. SentinelApp recognizes both SSE `event` headers and JSON `type`, rejects mismatches, treats `response.failed`, `response.incomplete`, and `error` as terminal failures, and requires completed status. It evicts the owner-bound mapping after failure and permits exactly one fresh-session retry only when the request is safe/read-only, contains no pending evidence, emitted no text, and failed with a protocol exception; simulations, token evidence, timeouts, partial output, and dependency failures are never retried. Version 7 direct evidence proved `get_gateway_config` and the ARM read. A reviewed tester-only HostedShadow plan then completed gateway configuration, log query, IQ retrieval, and same-session follow-up without retry or failure, after which the pre-reviewed rollback produced revision `ca-edgegrd--0000019` in `Embedded`, removed the allowlist, passed the strict JWT matrix, and converged with no Terraform changes. The retained v7 evaluation run passed 13/15 because two managed responses contained no terminal output; both cases produced the required answers on direct replay and passed a bounded two-case retry with zero failures or errors. This clears the diagnosed v6 blocker but does not authorize global Hosted mode.

**Version 7 Gate 5 promotion (14 August 2026):** The explicitly approved `infra/tfplan-gate5-v7` contained only the SentinelApp `AGENT_MODE` change from `Embedded` to `Hosted`: `0` additions, one in-place change, and `0` destroys. Apply created healthy revision `ca-edgegrd--0000020` on the unchanged immutable image and version 7 endpoint, with no shadow allowlist. Trusted browser assets, delegated `/api/whoami`, strict BFF entry, both backend pools, the protected-listener `401, 401, 200 SentinelGate, 401` matrix, all four deterministic scenarios, live gateway and log tools, sanitized decode evidence, exact IQ citations, continuity, reset, and security refusals passed. Eleven user-facing prompts produced 12 successful version 7 invocations because one safe read-only IQ call used the single permitted fresh-session retry; all 19 model calls and all dependencies succeeded. The count-only scan found no JWT-like, bearer-value, client-secret, private-key, or storage-key patterns in 537 correlated rows. Terraform converged with no changes. Hosted is now authoritative; `infra/tfplan-gate5-v7-rollback` is retained to restore Embedded without an Application Gateway operation. Cross-user live testing still requires a second approved identity; owner isolation remains covered by contract tests and the single-user live boundary checks.

**Consequences:**

- there is no `terraform_remote_state` coupling and no later infrastructure merge;
- existing gateway, listener, routing, networking, certificate, SentinelGate, and Stage 1 state ownership do not change;
- independent deployment must prove tool parity, citations, session continuity and isolation, security boundaries, latency, cost, and trace redaction before integration;
- hosted shadow testing cannot duplicate deterministic scenario side effects;
- agent costs and telemetry are owned by the agent resource group;
- Hosted Agent and any IQ preview APIs remain explicitly isolated preview dependencies;
- removing the embedded path requires a later ADR after a sustained, approved hosted observation period.

See [Foundry Hosted Agent and Foundry IQ Migration Design](AGENT-MIGRATION.md) and the proposed [Isolated Implementation Plan](AGENT-IMPLEMENTATION-PLAN.md).

## ADR-014 — Separate Foundry IQ infrastructure, corpus, and toolbox ownership

**Status:** Accepted

**Decision:** Terraform owns the Azure AI Search and Foundry foundation, identities, cost controls, and RBAC. A dry-run-first, Entra-authenticated publisher separately owns the allowlisted versioned Search index, indexed records, `searchIndex` knowledge source, and extractive knowledge base. A second post-deployment workflow owns the Foundry RemoteTool connection and toolbox.

The first index is semantic text search without vectors. The first knowledge base uses `extractiveData`, `minimal` retrieval reasoning, no Search-side LLM, and explicit citation fields. The toolbox connection targets only the fixed knowledge-base MCP endpoint, requests the `https://search.azure.com/` audience, and authenticates as the deployed hosted-agent identity. It does not use Search keys, the project managed identity, or a proxied user token.

**Reason:** The repository corpus is small and curated, no embedding deployment was approved, and the Hosted Agent already owns answer synthesis. This keeps retrieval cost and preview exposure bounded while preserving citations. The identity split ensures the runtime receives Search read access only, while publication remains an operator-controlled write action.

**Consequences:**

- corpus publication and toolbox creation are not Terraform operations and require their own approval and evidence;
- Microsoft Learn fetches are restricted to the committed allowlist and redirects may not leave `learn.microsoft.com`;
- the publisher does not prune old chunks automatically; a deletion or index-version rollover requires explicit review;
- a later vector, indexed-source, or answer-synthesis design is a new version and decision, not an in-place silent upgrade;
- toolbox configuration waits for a deployed agent identity and verifies its exact Search Index Data Reader assignment first.

## ADR-015 — Terraform-own Hosted Agent monitoring and use a repository-owned security evaluator

**Status:** Accepted

**Decision:** Terraform owns shared account- and project-scoped Foundry connections to the agent-owned Application Insights component. The Application Insights connection string is supplied only through the AzAPI resource's write-only `sensitive_body`, remains protected in isolated Terraform state, and is never emitted as an output. The Foundry project identity receives Log Analytics Reader and Privileged Monitoring Data Reader only on that Application Insights component.

Hosted evaluation pins the candidate to version 7 and uses the built-in task-adherence and groundedness evaluators plus version 1 of the registered `jwt-sentinel-security-parity` rubric. The custom rubric evaluates trust-boundary fidelity, evidence and tool discipline, grounding and corpus boundaries, confidentiality and session isolation, and communication quality. A rubric-only recipe is retained for bounded retries when the shared model deployment reaches its token-per-minute limit.

**Reason:** Server-side Foundry tracing requires an explicit monitoring connection, while the connection credential must not become application configuration or a normal Terraform output. The built-in tool-call-accuracy evaluator cannot evaluate the current Hosted Agent result shape because tool definitions are absent, so continuing to report it would produce errors rather than meaningful security evidence. A versioned repository rubric makes the actual JWT Sentinel boundaries reviewable and reproducible.

**Consequences:**

- agent tracing and evaluation telemetry stay in `rg-edgegrd-agent` and do not change Stage 1 monitoring or Application Gateway;
- the account connection remains shared to its projects, while Azure normalizes the project-scoped connection itself to `isSharedToAll=false`; Terraform models that live value so authorization-only plans do not resend unrelated monitoring configuration;
- Application Insights local ingestion authentication remains enabled for the platform's connection-string exporter; Entra-only ingestion requires a separately reviewed exporter change;
- evaluator-set changes require a fresh evaluation group rather than reuse of an immutable group, and local `LAST_EVAL_ID` must be cleared before such a run;
- aggregate success is never treated as sufficient when a criterion has errors; rate-limited cases are retried with the rubric-only recipe and retained as supporting evidence;
- explicit version 7 gateway-tool execution, log queries, IQ continuation, session continuity/reset, bounded evaluation closure, tool-focused shadow testing, and the final Gate 5 Hosted promotion have passed;
- the embedded agent remains the rollback path and authoritative Stage 1 runtime.
