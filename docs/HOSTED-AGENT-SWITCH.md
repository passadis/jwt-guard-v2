# Switching SentinelApp between Embedded and Hosted Agents

## Purpose

This guide describes the reversible, operator-controlled switch between SentinelApp's in-process GateExplainer and the independently deployed Foundry Hosted Agent. It contains no environment identifiers and does not authorize a Terraform apply, agent deployment, RBAC change, or Azure operation.

The active implementation uses `Hosted`. The embedded Microsoft Agent Framework implementation remains installed and tested as the rollback path. Switching modes changes only SentinelApp configuration and its Container App revision; it must not modify or restart Application Gateway, alter SentinelGate, change DNS or certificates, or merge the Stage 1 and agent Terraform states.

## How the solution reached Hosted mode

The project deliberately started in `Embedded`. That established a known-good Agent Framework implementation, authenticated browser contract, deterministic scenario tools, evidence rules, redaction, and session ownership before introducing another runtime boundary.

The Hosted Agent, Foundry IQ knowledge plane, monitoring, identities, and cost controls were then deployed and tested independently. SentinelApp gained a fixed managed-endpoint client and a narrow app-only evidence broker. `HostedShadow` compared read-only Hosted behavior for explicitly allowlisted testers while continuing to return only Embedded responses to the browser.

Early trials found two useful failure modes: delegated identity must be consistent across hosted session, conversation, and response operations, and a successful model response is not enough when a required tool was not called or the stream lacked a valid terminal event. Both cases rolled back globally to `Embedded`. After correcting the client and tool-routing instructions, the Hosted path passed tool parity, Foundry IQ citations, continuity/reset, failure handling, telemetry redaction, latency, and the complete JWT matrix. Only then was `Hosted` selected globally.

## Routing model

```text
Browser
  -> authenticated /api/agent/chat and /api/agent/reset
  -> SentinelApp AgentRouter
       -> Embedded      -> in-process AgentService
       -> HostedShadow  -> Embedded response to browser
                        -> read-only Hosted comparison for allowlisted testers
       -> Hosted        -> Foundry managed Responses endpoint

Hosted Agent
  -> resource-scoped ARM and Log Analytics readers
  -> Foundry IQ toolbox and knowledge-base MCP endpoint
  -> managed-identity call to fixed SentinelApp evidence-broker routes
```

The browser always calls SentinelApp. It never receives or selects the Hosted endpoint, immutable version, execution mode, token audience, hosted session identifier, conversation identifier, broker origin, or tester allowlist.

## Modes

| Mode | Browser response | Hosted execution | Intended use |
| --- | --- | --- | --- |
| `Embedded` | Embedded | None | Initial deployment and rollback |
| `HostedShadow` | Embedded | Read-only comparison for allowlisted testers | Bounded parity observation |
| `Hosted` | Hosted | Full approved tool set | Promotion after parity acceptance |

An absent mode defaults safely to `Embedded`; an unknown value fails startup. There is no silent per-request Hosted-to-Embedded fallback. A Hosted failure produces an explicit bounded error so telemetry and the actual evidence path remain truthful.

## Terraform configuration contract

The current variables are:

| Variable | Purpose | Required behavior |
| --- | --- | --- |
| `agent_mode` | Selects `Embedded`, `HostedShadow`, or `Hosted` | Operator controlled; never browser controlled |
| `hosted_agent_responses_endpoint` | Exact managed Responses endpoint | Required with a version outside Embedded-only setup; standard-port HTTPS, approved Foundry host and exact path/query |
| `hosted_agent_version` | Immutable Hosted Agent version | Positive integer paired with the endpoint |
| `hosted_shadow_tester_object_ids` | Entra object IDs allowed to create shadow comparisons | Required only for `HostedShadow`; canonical lowercase non-empty GUIDs |

Use environment-specific values only in ignored `terraform.tfvars`, another approved ignored variable file, or a protected CI variable store. Do not write real tenant, subscription, principal, endpoint, hostname, or tester identifiers into public documentation.

Example shape:

```hcl
agent_mode                       = "Embedded"
hosted_agent_responses_endpoint = "https://<foundry-account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/openai/responses?api-version=v1"
hosted_agent_version             = <positive-version>
hosted_shadow_tester_object_ids  = []
```

Authentication uses SentinelApp's managed identity. Never add Hosted access tokens, model/Search keys, connection strings, browser tokens, or daemon secrets to these variables.

## Security boundaries that do not change with mode

- `/api/agent/chat` and `/api/agent/reset` remain behind SentinelApp's delegated authorization policy.
- Browser tokens and raw pasted JWTs never enter Hosted prompts, sessions, traces, evaluations, or IQ content.
- Hosted endpoint construction uses only validated startup configuration; redirects are disabled.
- Hosted sessions are keyed by authenticated owner plus browser session and retain only opaque remote identifiers.
- Reset, expiry, mode changes, and version changes remove or invalidate the complete local mapping.
- The broker rejects delegated callers and requires the expected tenant, app-only role, and Hosted runtime principal.
- Decode evidence is sanitized, short-lived, owner-bound, and referenced through a server-held opaque handle.
- Simulations accept only the fixed allowlist and cannot accept a host, path, scheme, headers, token, or credential.
- `x-original-host` remains routing context only and is never authentication proof.

## Ownership boundaries

| Change | Owner |
| --- | --- |
| SentinelApp mode, endpoint/version variables, API app role, broker routes, and Container App revision | Existing `infra/` state and SentinelApp deployment workflow |
| Foundry account/project, Search, monitoring, runtime/publisher identities, and resource-scoped readers | Independent `agent-infra/` state |
| Immutable Hosted Agent source/configuration version | Hosted Agent direct-source deployment workflow |
| Application Gateway, SentinelGate, networking, DNS, and certificates | Unchanged by an Agent mode switch |

Never introduce `terraform_remote_state` coupling, copy either state, import one stack into the other, or use `azd provision` over Terraform-owned Foundry resources.

## Promotion workflow

### Gate 1: establish Embedded rollback

1. Build and test the router, Embedded adapter, Hosted client, session mapping, and broker locally.
2. Keep `agent_mode = "Embedded"`.
3. Verify Embedded streaming, reset, all tools, trusted TLS, both backends, and the complete protected-listener matrix.

### Gate 2: validate Hosted independently

1. Deploy an immutable Hosted Agent version into the isolated agent environment.
2. Assign only the reviewed resource-scoped gateway, Log Analytics, Search, and invocation permissions.
3. Validate direct tools, broker authorization, IQ citations, session continuity, evaluations, trace redaction, latency, and cost without changing SentinelApp mode.

### Gate 3: run a bounded shadow observation

1. Select explicit tester object IDs and an observation duration; never infer testers from the current CLI identity.
2. Set `agent_mode = "HostedShadow"` and populate `hosted_shadow_tester_object_ids` in protected environment input.
3. Produce and inspect a saved `infra/` plan. It must change only SentinelApp configuration/revision data: no Application Gateway generation, SentinelGate, network, DNS, certificate, agent foundation, replacement, or destroy.
4. Apply only the reviewed plan.
5. Use read-only comparison prompts. Do not run deterministic simulations twice or send token-like material through shadow traffic.
6. Compare evidence fidelity, tool calls, citations, continuity, isolation, terminal events, latency, token use, cost, and redaction telemetry.
7. Return to `Embedded` with a pre-reviewed SentinelApp-only rollback unless a separate promotion decision is approved.

### Gate 4: promote Hosted

1. Set `agent_mode = "Hosted"` and clear the shadow allowlist.
2. Confirm the exact reviewed endpoint and immutable version.
3. Produce a saved plan and require zero additions, zero destroys, and only the expected in-place SentinelApp configuration change.
4. Apply only that plan.
5. Run trusted UI health, `/api/whoami`, BFF entry, both backend pools, the protected-listener matrix, deterministic scenarios, live gateway/log tools, sanitized decode, IQ citations, continuity/reset, and security-refusal checks.
6. Inspect correlated telemetry and count-only secret signatures before accepting promotion.

## Rollback to Embedded

Rollback is deliberate and global:

1. set `agent_mode = "Embedded"` in protected environment configuration;
2. clear any shadow allowlist;
3. produce a saved plan showing only the SentinelApp configuration/revision change;
4. apply or activate the reviewed Embedded revision;
5. verify Embedded chat, reset/continuity, tools, Enter the Gate, both backends, trusted TLS, and the complete protected-listener matrix;
6. retain Hosted traces for diagnosis without exposing sensitive content.

Rollback must not restart or update Application Gateway. Do not destroy the agent resource group during incident recovery; hosted-infrastructure cleanup requires its own `agent-infra` destroy review.

## Minimum acceptance criteria

- Both modes satisfy the same authenticated browser and SSE contract.
- Hosted uses the expected immutable version and owner-bound session/conversation.
- `get_gateway_config` produces a correlated ARM read using API `2025-05-01`.
- `query_gate_logs` produces a correlated Log Analytics query and reports ingestion delay.
- IQ retrieval calls the configured toolbox/knowledge base and returns exact citations.
- Decode and simulation requests cross only the app-only broker with bounded schemas.
- Cross-owner evidence and session access are rejected.
- Failed, incomplete, malformed, throttled, timed-out, or dependency-failed Hosted responses are bounded and observable.
- Traces and broker logs contain no tokens, secrets, connection strings, raw JWTs, or complete sensitive payloads.
- A deliberate rollback succeeds without an Application Gateway operation.

## Related documents

- [Architecture](ARCHITECTURE.md)
- [Accepted decisions](DECISIONS.md)
- [Hosted Agent and IQ migration design](AGENT-MIGRATION.md)
- [Operator guide](OPERATOR-GUIDE.md)
- [Test matrix](TEST-MATRIX.md)
