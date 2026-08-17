# JWT Sentinel Decisions

This log records the accepted decisions that govern the current implementation. It intentionally excludes tenant, subscription, principal, hostname, revision, and other deployment-specific identifiers.

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

**Migration outcome:** The migration was later completed under ADR-013. Hosted is now the active operator-selected mode, the embedded implementation remains the reviewed rollback path, and Application Gateway was not changed as part of the promotion.

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

**Migration outcome:** The integration uses a server-side three-mode router (`Embedded`, `HostedShadow`, `Hosted`) with no browser selector and no silent per-request fallback. Hosted calls use a validated fixed Responses endpoint, managed identity, a version-backed session, and an opaque conversation. Broker calls form a separate app-only trust boundary; delegated tokens are rejected even if they carry a role. Raw-token analysis stores only short-lived sanitized evidence, and the browser never receives its opaque handle.

The staged journey began in `Embedded`, added least-privilege identities and the evidence broker, deployed an immutable Hosted Agent and IQ toolbox independently, and then used allowlisted `HostedShadow` for read-only parity observation. Early shadow testing exposed inconsistent delegated identity on session/conversation operations; an early Hosted attempt exposed incomplete terminal-event handling and nondeterministic tool intent. Both attempts rolled back globally to `Embedded` without changing Application Gateway. The client and agent instructions were corrected, shadow validation was repeated, and `Hosted` was promoted only after tool execution, IQ citations, continuity/reset, security refusals, redaction, latency, and the complete JWT matrix passed. The embedded implementation remains installed as the rollback path.

**Consequences:**

- there is no `terraform_remote_state` coupling and no later infrastructure merge;
- existing gateway, listener, routing, networking, certificate, SentinelGate, and Stage 1 state ownership do not change;
- independent deployment must prove tool parity, citations, session continuity and isolation, security boundaries, latency, cost, and trace redaction before integration;
- hosted shadow testing cannot duplicate deterministic scenario side effects;
- agent costs and telemetry are owned by the agent resource group;
- Hosted Agent and any IQ preview APIs remain explicitly isolated preview dependencies;
- removing the embedded path requires a later ADR after a sustained, approved hosted observation period.

See [Foundry Hosted Agent and Foundry IQ Migration Design](AGENT-MIGRATION.md) and the [Hosted Agent switch guide](HOSTED-AGENT-SWITCH.md).

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

- agent tracing and evaluation telemetry stay in the dedicated agent resource group and do not change Stage 1 monitoring or Application Gateway;
- the account connection remains shared to its projects, while Azure normalizes the project-scoped connection itself to `isSharedToAll=false`; Terraform models that live value so authorization-only plans do not resend unrelated monitoring configuration;
- Application Insights local ingestion authentication remains enabled for the platform's connection-string exporter; Entra-only ingestion requires a separately reviewed exporter change;
- evaluator-set changes require a fresh evaluation group rather than reuse of an immutable group, and local `LAST_EVAL_ID` must be cleared before such a run;
- aggregate success is never treated as sufficient when a criterion has errors; rate-limited cases are retried with the rubric-only recipe and retained as supporting evidence;
- explicit version 7 gateway-tool execution, log queries, IQ continuation, session continuity/reset, bounded evaluation closure, tool-focused shadow testing, and the final Gate 5 Hosted promotion have passed;
- the Hosted Agent is the active mode and the embedded Agent remains the preserved rollback path.
