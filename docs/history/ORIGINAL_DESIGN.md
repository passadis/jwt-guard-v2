# JWT Sentinel — AppGW JWT Validation, explained by the app it protects

An AI-enhanced web app that sits **behind** Azure Application Gateway's new JWT
Validation (preview) feature and whose job is to **explain and demonstrate that
very feature**. The demo is self-referential: to reach the app you must pass
gateway-level JWT validation, and once inside, an agent shows you exactly what
happened at the edge and lets you break it on purpose.

## The feature (facts that anchor the post)

| Fact | Detail |
|---|---|
| Status | Public preview — no SLA, not for production |
| What's validated | JWT signature, issuer, tenant, audience, lifetime — **Entra ID tokens only** |
| Identity propagation | Gateway injects `x-msft-entra-identity: <tenantId>:<oid>` header to backend |
| Actions | `Deny` (401) or `Allow` (forward without identity header) for invalid/missing tokens |
| SKU | Standard_v2 or WAF_v2 only (no Basic) |
| Listener | HTTPS only, TLS cert required |
| ARM | API version `2025-03-01` or later; portal needs feature flag `?feature.applicationgatewayjwtvalidation=true` |
| Network | AppGW subnet needs outbound 443 to `login.microsoftonline.com` |
| Tenant values | GUID, `common`, `organizations`, or `consumers`; up to 5 extra audiences |
| Failure modes | Invalid/missing token → 401; missing `oid` claim → 403 |

## Architecture

```
                        ┌────────────────────────────────────────────┐
 Client (browser /      │  Azure Application Gateway (Standard_v2)   │
 curl + Bearer token) ──►  HTTPS listener + JWT validation config    │
                        │  (tenant, clientId, audiences, Deny)       │
                        └───────────────┬────────────────────────────┘
                                        │ injects x-msft-entra-identity
                                        ▼
                        ┌────────────────────────────────────────────┐
                        │  Azure Container Apps (internal ingress)   │
                        │  "JWT Sentinel" web app                    │
                        │   • UI: shows the injected identity header │
                        │   • Agent (Microsoft Agent Framework 1.0)  │
                        │     model via Microsoft Foundry deployment │
                        └──────┬──────────────┬──────────────┬───────┘
                               │              │              │
                     Managed identity   Log Analytics    Entra ID
                     (Reader on AppGW)  (AGW access      (client-credentials
                     reads jwtConfigs    logs, KQL)       token minting)
```

### Components

1. **Application Gateway Standard_v2** — HTTPS listener (Key Vault cert or
   self-signed for demo), JWT validation configuration (`Deny`) linked to the
   routing rule; backend pool → Container Apps.
2. **Entra ID app registrations** (two):
   - `sentinel-api` — the protected resource; App ID URI `api://<clientId>`,
     used as the audience in the gateway config.
   - `sentinel-client` — demo client used to mint tokens (client-credentials
     for curl demos, auth-code/PKCE for the browser).
3. **Web app** (Container Apps): any stack — suggested .NET 9 minimal API +
   Blazor, or FastAPI + React. Reads `x-msft-entra-identity`, splits
   `tenantId:oid`, optionally resolves display name via Microsoft Graph.
4. **AI layer**: Microsoft Agent Framework 1.0 (GA) running in-process, model
   deployment in Microsoft Foundry. (Alternative: host the agent in Foundry
   Agent Service and keep the web app thin.)

### The agent: "Gate Explainer" — four tools

| Tool | What it does | Why it sells the feature |
|---|---|---|
| `decode_token` | Base64-decodes a pasted JWT (no validation), annotates `iss`/`tid`/`aud`/`oid`/`exp`/`nbf`, diffs them against the gateway's config | Teaches claim-by-claim what the gateway checks |
| `get_gateway_config` | ARM GET on the gateway's `jwtConfigurations` via managed identity (Reader) | Shows the live policy, catches audience/tenant mismatches |
| `query_access_logs` | KQL against Log Analytics AppGW access logs, surfaces recent 401/403s with failure reasons | "Why was I blocked?" answered from real telemetry |
| `simulate_request` | Mints tokens (valid / wrong audience / expired) and calls through the gateway, reports the status code | Live allow/deny demo without leaving the chat |

## Demo storyline (blog flow)

1. `curl https://<appgw>/` with no token → **401 at the edge**, backend never
   touched (prove with logs).
2. Get a token (`az account get-access-token` won't work — wrong audience;
   that's a teaching moment. Use client-credentials against `sentinel-api`).
3. Call with the token → app loads, UI banner shows the injected
   `x-msft-entra-identity` — no auth middleware in the backend code at all.
4. Ask the agent: *"Why did my first request fail?"* → it queries logs +
   config and explains in plain language.
5. Ask: *"Show me what happens with a wrong-audience token"* →
   `simulate_request` demonstrates the 401 live.

**Key message:** authN moved to the edge — zero auth code in the backend —
and AI turns the gateway's opaque 401s into explainable, queryable behavior.

## Repo layout (proposed)

```
appgw-jwt-ai/
├── infra/                 # Bicep: vnet, appgw (2025-03-01 API), aca env,
│                          # log analytics, key vault, foundry project, RBAC
├── src/
│   ├── api/               # backend + Agent Framework agent + tools
│   └── web/               # UI (identity banner + chat)
├── scripts/               # token minting helpers, demo curl scenarios
├── DESIGN.md
└── README.md
```

## Open decisions

- **Stack**: .NET 9 (Agent Framework C#) vs Python (FastAPI + agent-framework
  pip package). Both GA.
- **Agent hosting**: in-app Agent Framework (simpler, one deployable) vs
  Foundry Agent Service hosted agent (extra Foundry showcase, more moving parts).
- **Frontend auth**: pure curl/scripted demo vs MSAL.js in the browser
  (auth-code + PKCE) so the UI itself acquires the token.

## Preview caveats to state in the post

- Preview supplemental terms apply; don't run production traffic through it.
- Entra ID issuers only — no generic OIDC / third-party IdPs yet.
- No claim-based authorization yet (scopes noted as "future" in docs) — the
  gateway answers *who are you*, not *what may you do*; the app still owns authZ.
- Portal blade only visible with the feature-flag URL.
