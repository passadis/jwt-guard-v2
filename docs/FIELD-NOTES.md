# JWT Sentinel — Deployment Field Notes

**Status:** Verified operational knowledge from the original JWT Sentinel deployment  
**Original deployment date:** 2026-07-28  
**Document date:** 2026-08-04  
**Applies to:** Azure Application Gateway JWT Validation preview, Terraform/AzAPI, Azure Container Apps, Entra ID, Key Vault, and the JWT Sentinel application

## 1. Purpose

This document records the deployment failures, misleading symptoms, verified causes, fixes, and prevention rules discovered while building and deploying JWT Sentinel.

These notes are not a generic Azure troubleshooting guide. Several behaviors are specific to:

- Azure Application Gateway JWT Validation preview.
- The API versions used by the original solution.
- A public Application Gateway calling an externally reachable Azure Container App FQDN.
- Terraform ownership split across `azurerm`, `azuread`, and `azapi`.
- The service behavior observed on 2026-07-28.

Treat each note according to its status:

- **Verified:** reproduced or confirmed by end-to-end testing.
- **Preventive:** derived directly from a verified failure.
- **Historical:** useful context, but not the final diagnosis.
- **Revalidate:** likely to change as preview services, providers, and APIs evolve.

Do not copy resource IDs, tenant IDs, application IDs, secrets, domains, or certificate material from the original deployment. The clean repository must generate its own values.

### v2 topology decision

The original deployment used one shared Container App. JWT Sentinel v2 deliberately replaces that topology with two structurally isolated backends:

- the UI hostname routes only to SentinelApp;
- the protected hostname routes only to SentinelGate with JWT `Deny`;
- SentinelGate exposes `/healthz` and `/enter`, requires canonical gateway-injected `tenantId:objectId`, and checks the configured tenant;
- backend settings keep `pickHostNameFromBackendAddress = true`, so the actual backend `Host` and TLS/SNI name are the SentinelGate ACA FQDN;
- client-originated `x-original-host` is inspected only as additional routing context; a match does not authenticate the caller or prove JWT validation;
- SentinelApp uses an authenticated BFF endpoint to forward the caller token without exposing it to the Agent or browser cross-origin flow.

The original NAT, API-version, identifier-URI, certificate, and post-boot findings remain applicable to both Container App backends where relevant.

## 2. Rapid triage table

| Symptom | Most likely cause | First action |
|---|---|---|
| Protected listener waits about 60 seconds and returns 500 | Application Gateway subnet lacks outbound access, or JWT engine needs a post-boot configuration push | Verify NAT attachment and egress, then push the full gateway configuration |
| UI listener works but protected listener fails | JWT validation path is unhealthy; UI path does not exercise it | Run the protected token matrix |
| Backend health is healthy but all JWT requests fail | Gateway JWT engine, audience, tenant, or token acquisition issue | Inspect live gateway config and test correct/bare audiences |
| No-token request unexpectedly returns 200 | JWT properties or routing-rule reference were removed | Read the gateway through ARM API `2025-05-01`; never use the legacy CLI update command |
| Gateway cannot reach Container Apps after NAT was added | ACA ingress allows the gateway frontend IP but not the NAT egress IP | Add the NAT public IP to the allow-list |
| `AADSTS500011` for `api://<clientId>/.default` | API identifier URI missing or being removed by Terraform | Inspect `identifierUris` and preserve `ignore_changes = [identifier_uris]` |
| Bare `<clientId>/.default` works but `api://.../.default` fails | Identifier URI ownership or propagation issue | Check Terraform state/config before assuming propagation |
| Container App stays on the quickstart/Hello World image | Infrastructure deployment only created the bootstrap revision | Run `scripts/deploy-app.ps1` and verify the active revision image |
| Browser shows `msal is not defined` | External MSAL script did not load | Serve the pinned MSAL browser library locally |
| Agent cannot see a request in logs immediately | Log Analytics ingestion delay | Wait and retry; do not invent an explanation |
| Terraform state cannot be read because it is locked | Another Terraform process still owns the local state file | Stop the competing process; do not delete state |
| Certificate changed but gateway still presents the old version | Key Vault certificate pickup or gateway refresh delay | Verify the unversioned secret URI; restart only when necessary, then perform a config push |

## 3. FN-001 — JWT listener hangs and returns 500 after approximately 60 seconds

**Status:** Verified and reproduced  
**Severity:** Critical  
**Area:** Application Gateway networking and JWT validation

### Symptom

The JWT-protected listener:

- accepted the TCP/TLS connection;
- waited approximately 60 seconds;
- returned HTTP 500;
- did not contact the backend;
- produced access-log entries with `ERRORINFO_NO_ERROR`.

At the same time:

- the non-JWT UI listener worked;
- the Container App was reachable;
- backend health could appear normal;
- the gateway configuration looked structurally correct.

### Why this was misleading

The failure looked like:

- a bad backend pool;
- a broken health probe;
- an invalid token;
- a malformed AzAPI payload;
- a stale gateway instance;
- or a preview-service defect unrelated to networking.

The fact that the ordinary listener worked made the VNet and gateway appear healthy.

### Verified cause

The Application Gateway subnet did not have reliable default outbound internet access. JWT validation required outbound HTTPS connectivity to Microsoft Entra endpoints.

A newly created subnet could not silently rely on platform-provided default outbound access. The JWT engine waited for its external dependency and eventually returned a 500 without a useful `ErrorInfo` value.

### Verified fix

Attach a NAT Gateway with a public IP to the Application Gateway subnet.

The infrastructure must preserve:

```text
Application Gateway subnet
    -> NAT Gateway
        -> NAT public IP
            -> outbound HTTPS to Entra endpoints
```

### Validation

Do not validate the fix only through the UI hostname.

Run at least:

1. Missing token → expected 401.
2. Wrong-audience token → expected 401.
3. Correct-audience token → expected 200.
4. Tampered token → expected 401.

Responses should return promptly rather than after approximately 60 seconds.

### Prevention

- Keep the NAT Gateway in Terraform.
- Do not remove it as an apparent cost optimization.
- Treat outbound Entra connectivity as a prerequisite of the protected listener.
- Add explicit monitoring for protected-listener latency and 500 responses.

## 4. FN-002 — A configuration push is required after every gateway instance boot

**Status:** Verified and reproducible  
**Severity:** Critical  
**Area:** Application Gateway JWT Validation preview lifecycle

### Symptom

Even after outbound connectivity was corrected, the protected listener could return the same approximately 60-second 500 behavior after:

- initial provisioning;
- a full Application Gateway recreation;
- stop/start;
- or another gateway instance boot.

The behavior persisted until another gateway configuration update was sent.

### Verified behavior

A full configuration push caused JWT validation to begin responding normally. After the push:

- missing and invalid tokens were denied quickly;
- valid tokens were forwarded;
- the gateway remained healthy across ordinary configuration changes.

The issue was reproduced after multiple forms of instance restart.

### Verified fix

After a gateway boot or restart, push the complete known-good Application Gateway configuration through:

- Terraform using the AzAPI resource; or
- a verified ARM REST request using an API version that supports JWT validation.

A normal Terraform apply that sends the full resource body is acceptable.

### Important nuance

Do not spend time repeatedly recreating the gateway before trying the configuration push. Recreating the gateway itself reproduces the boot condition and can recreate the same failure.

### Validation

After the push:

- run the complete protected-listener token matrix;
- read the live gateway configuration;
- confirm the protected routing rule still references the JWT validation config;
- confirm a no-token request does not reach the backend.

### Prevention

Any runbook action that restarts or recreates the gateway must include:

```text
Restart/recreate
    -> full safe configuration push
        -> protected-listener test matrix
            -> live configuration verification
```

## 5. FN-003 — Never use `az network application-gateway update`

**Status:** Verified security failure  
**Severity:** Critical  
**Area:** API-version safety

### Symptom

After an Application Gateway update through the Azure CLI, a request with no bearer token unexpectedly reached the backend and returned 200.

### Verified cause

The CLI command performed a read-modify-write operation through an API version that did not understand the preview JWT properties.

The update silently removed:

- top-level `entraJWTValidationConfigs`; and/or
- routing-rule `entraJWTValidationConfig` references.

The gateway remained operational, but the protected route was no longer protected.

### Forbidden command

```bash
az network application-gateway update
```

Do not use it for harmless-looking changes, certificate operations, tags, listeners, backend settings, or recovery actions.

### Allowed update methods

Use one of:

```text
Terraform -> azapi_resource -> Microsoft.Network/applicationGateways@2025-05-01
```

or:

```text
az rest -> verified JWT-capable API version -> complete reviewed payload
```

### Validation after every gateway change

Read the gateway through a JWT-capable ARM API and verify:

- `entraJWTValidationConfigs` exists;
- the expected tenant, client ID, audiences, and `Deny` action exist;
- the protected request-routing rule references the intended JWT config;
- missing token returns 401;
- valid token returns 200.

### Prevention

- Keep the forbidden command documented in the deployment runbook and contributor safety policy.
- Add a CI check that flags it in scripts and documentation.
- Treat a successful gateway update as incomplete until the no-token test fails closed.

## 6. FN-004 — NAT changes the source IP seen by the Container App

**Status:** Verified  
**Severity:** High  
**Area:** Backend ingress restrictions

### Symptom

After attaching NAT to the Application Gateway subnet, the gateway could no longer reach the external Container Apps FQDN even though:

- DNS resolved;
- the Container App revision was healthy;
- the Application Gateway frontend public IP was allowed in ACA ingress rules.

### Verified cause

Application Gateway reached the internet-facing Container App backend using the NAT Gateway public IP, not only the Application Gateway frontend IP.

### Verified fix

Allow the actual NAT public IP in Azure Container Apps ingress restrictions.

For the proven public-FQDN topology, retain both relevant addresses when required:

- Application Gateway frontend public IP.
- NAT Gateway public IP.

### Security significance

The backend restriction is not merely a connectivity setting. JWT Sentinel trusts the gateway-injected identity header on the protected path. If arbitrary clients can reach the backend directly, they may attempt to spoof that header.

### Validation

- Protected requests through Application Gateway succeed.
- Direct requests from an unapproved client IP are blocked.
- The backend cannot be used to bypass gateway validation.

### Prevention

When changing outbound architecture, always recalculate the source IP that the internet-facing backend will observe.

## 7. FN-005 — `AADSTS500011` was not only propagation; Terraform was deleting the identifier URI

**Status:** Verified final diagnosis  
**Severity:** High  
**Area:** Entra ID and Terraform ownership

### Symptom

Client-credential token requests using:

```text
scope=api://<api-client-id>/.default
```

failed with:

```text
AADSTS500011
The resource principal named api://<clientId> was not found
```

At times, this looked like ordinary service-principal propagation because:

```text
scope=<api-client-id>/.default
```

worked and the gateway accepted the bare GUID audience.

### Historical diagnosis

The first working theory was that the `api://` identifier URI needed time to propagate.

That was plausible but incomplete.

### Verified final cause

Terraform used both:

- `azuread_application`; and
- a separate `azuread_application_identifier_uri`.

Without a lifecycle safeguard, updates to `azuread_application` reset `identifierUris` to an empty array. The separate identifier-URI resource then had to restore it, and subsequent applies could remove it again.

### Verified fix

Preserve the following on the API application resource:

```hcl
lifecycle {
  ignore_changes = [identifier_uris]
}
```

The separate `azuread_application_identifier_uri` resource remains the owner of the `api://<clientId>` URI.

### Resilience measure

The gateway accepts both:

```text
api://<api-client-id>
<api-client-id>
```

The bare GUID audience provides a useful diagnostic path, but it is not a substitute for correcting Terraform ownership.

### Validation

1. Query the Entra application and confirm `identifierUris` contains the expected URI.
2. Run Terraform apply again.
3. Confirm the URI remains.
4. Acquire a token for `api://<clientId>/.default`.
5. Send it through the protected listener.
6. Expect 200 and the injected identity header.

### Prevention

When `AADSTS500011` appears:

- do not immediately wait for propagation;
- inspect the actual application `identifierUris`;
- inspect the Terraform plan;
- verify which resource owns the property.

## 8. FN-006 — Do not use a working bare-GUID token to hide an identifier-URI defect

**Status:** Preventive  
**Severity:** Medium  
**Area:** Token testing

### Observation

The following scope worked promptly:

```text
<api-client-id>/.default
```

The gateway accepted the resulting bare-GUID `aud` value because it was included in the configured audience list.

### Risk

This can make the overall demonstration look healthy while:

- the intended `api://` identifier URI is missing;
- the SPA/API permission model is inconsistent;
- future deployments or clients fail.

### Rule

Use the bare GUID as:

- a recovery test;
- a way to isolate gateway behavior from Entra identifier-URI behavior;
- and an accepted audience for resilience.

Still require the `api://` scope to pass before declaring the Entra configuration complete.

## 9. FN-007 — The UI listener working does not prove JWT Validation works

**Status:** Verified diagnostic principle  
**Severity:** High  
**Area:** Test strategy

### Observation

The UI listener intentionally has no gateway JWT validation on browser navigation. It can work while the protected listener is:

- unable to reach Entra;
- missing its JWT config;
- detached from its JWT config;
- wedged after restart;
- or accepting traffic without validation.

### Rule

Always test the two planes separately.

#### UI-plane proof

- SPA loads.
- MSAL sign-in succeeds.
- `/api/whoami` validates the token in ASP.NET Core.

#### Protected-plane proof

- no token is denied;
- wrong audience is denied;
- tampered token is denied;
- correct audience is allowed;
- injected identity is present.

Do not use backend health or UI availability as a proxy for protected-listener health.

## 10. FN-008 — The initial Container App image is only a bootstrap image

**Status:** Verified deployment workflow  
**Severity:** Medium  
**Area:** Container Apps and application deployment

### Symptom

Terraform completed, but the Container App:

- showed the default quickstart/Hello World content;
- appeared to be provisioning a revision for a long time;
- did not contain JWT Sentinel.

### Cause

The infrastructure deployment intentionally used a public bootstrap image so Terraform could create the Container App before the private application image existed in ACR.

The real application is deployed in a second phase.

### Verified deployment path

```powershell
./scripts/deploy-app.ps1 `
  -ResourceGroup <new-resource-group> `
  -AcrName <new-acr-name> `
  -AppName <new-container-app-name>
```

The script:

1. builds `src/SentinelApp` in ACR;
2. tags the image;
3. updates the Container App;
4. creates a new active revision.

### Terraform ownership rule

Terraform ignores subsequent image drift because the deployment script owns the real image after bootstrap.

Do not remove that lifecycle rule without redesigning the delivery model.

### Validation

- Inspect the active revision image.
- Confirm it points to the new ACR and `sentinel-app` repository.
- Call `/healthz`.
- Load the UI and confirm it is JWT Sentinel, not quickstart content.
- Check revision events and logs when provisioning does not complete.

## 11. FN-009 — `msal is not defined` was caused by the external browser-library dependency

**Status:** Verified application issue  
**Severity:** Medium  
**Area:** Frontend assets

### Symptom

The browser failed during startup with:

```text
Uncaught ReferenceError: msal is not defined
```

The failure occurred before sign-in could begin.

### Cause

The page relied on an externally hosted MSAL browser script. The script did not load successfully, so `app.js` executed without the global `msal` object.

### Verified fix

Vendor a pinned `msal-browser.min.js` under the application static assets and reference the local copy.

Expected layout:

```text
src/SentinelApp/wwwroot/
    lib/
        msal-browser.min.js
```

Load order remains:

```html
<script src="/lib/msal-browser.min.js"></script>
<script src="/config.js"></script>
<script src="/app.js"></script>
```

### Validation

- Browser network panel returns 200 for the local MSAL file.
- No `msal is not defined` error appears.
- Sign-in popup or redirect starts.
- The SPA acquires the intended delegated API token.

### Prevention

Do not replace the local library with an unverified CDN URL merely to reduce repository size.

## 12. FN-010 — Key Vault certificate rollover must preserve the certificate name

**Status:** Verified design and operational rule  
**Severity:** Medium  
**Area:** TLS and Key Vault

### Deployment pattern

Terraform creates a self-signed bootstrap certificate so Application Gateway can be provisioned immediately.

The trusted certificate process later:

1. performs DNS-01 validation;
2. issues a Let's Encrypt certificate;
3. imports it into Key Vault;
4. uses the same Key Vault certificate name.

Application Gateway references the unversioned Key Vault secret URI.

### Why the same name matters

Using a new certificate name would require changing the gateway configuration. Keeping the name stable creates a new certificate version while preserving the gateway reference.

### Operational caution

Certificate pickup may not be immediate. A gateway restart can accelerate refresh, but a restart also triggers the JWT validation boot issue described in FN-002.

### Safe sequence

```text
Issue/import same-name certificate
    -> verify new Key Vault version
        -> allow normal pickup when possible
            -> if restart is necessary
                -> restart
                    -> push full gateway configuration
                        -> run protected token matrix
                            -> verify trusted TLS
```

### Validation

Test both hostnames without disabling certificate validation.

The scripted demo's certificate-bypass option is useful during bootstrap but is not evidence that final TLS is correct.

## 13. FN-011 — Log Analytics is not real-time

**Status:** Verified expected behavior  
**Severity:** Low  
**Area:** Observability and agent answers

### Symptom

A simulation request completed, but the live gate feed or `query_gate_logs` tool did not immediately show it.

### Cause

Application Gateway diagnostic-log ingestion into Log Analytics lags behind live request processing. During the original deployment, the UI warned that ingestion could take several minutes.

### Rule for the agent

When a request is not yet visible:

- say that ingestion may be delayed;
- do not invent a log record;
- do not claim the request bypassed logging;
- offer to retry the query.

### Validation query principle

Filter by:

- recent time window;
- protected hostname or request URI;
- HTTP status;
- listener/rule when populated;
- transaction identifiers when available.

The `ErrorInfo` field may not contain a useful JWT-specific cause. `ERRORINFO_NO_ERROR` does not mean the request succeeded.

### Verified v2 host-field mapping

The v2 deployment confirmed that dedicated-table `AGWAccessLogs` records use
different fields for the incoming public hostname and the routed backend host:

- `OriginalHost` contains the incoming public hostname, such as the configured
  protected hostname.
- `Host` contains the ACA backend FQDN when the request is routed because
  `pickHostNameFromBackendAddress = true`.
- `Host` can be empty when Application Gateway denies a request before backend
  routing.

Log queries that select protected-listener traffic must therefore filter
`OriginalHost` and the protected path rather than requiring the public hostname
in `Host`. This use of `OriginalHost` is telemetry selection only. It is not
authentication evidence and does not prove that JWT validation occurred.

## 14. FN-012 — `ERRORINFO_NO_ERROR` does not mean there was no failure

**Status:** Verified  
**Severity:** Medium  
**Area:** Application Gateway diagnostics

### Observation

The 60-second JWT failure produced HTTP 500 while `ErrorInfo` contained:

```text
ERRORINFO_NO_ERROR
```

### Rule

Interpret access logs using the complete request context:

- `HttpStatus`;
- `TimeTaken`;
- whether a backend server was routed;
- backend response fields;
- listener and hostname;
- transaction ID;
- timing pattern;
- protected versus unprotected path.

Do not treat `ERRORINFO_NO_ERROR` as a success indicator.

## 15. FN-013 — Local Terraform state can be locked by another process

**Status:** Verified  
**Severity:** Medium  
**Area:** Terraform operations

### Symptom

Commands such as `terraform state pull` failed because `terraform.tfstate` was locked by another process.

Follow-on scripts then received null data and produced misleading secondary errors, such as missing secrets or `invalid_client`.

### Cause

A previous or concurrent Terraform operation still held the state file.

### Safe response

1. Identify the Terraform process.
2. Allow it to complete or stop it safely.
3. Confirm no apply is still writing state.
4. Retry the read.
5. Re-run any command that consumed null output.

### Forbidden response

Do not:

- delete the state file;
- copy over it;
- initialize a new state to bypass the lock;
- run concurrent applies;
- extract secrets from partial/null state output.

### Prevention

Use one Terraform process per state. A remote backend with state locking is a future improvement, but it still requires unique state keys for the clean rebuild.

## 16. FN-014 — Secondary errors can hide the first failure

**Status:** Preventive  
**Severity:** Medium  
**Area:** Troubleshooting discipline

### Example

A locked Terraform state prevented the daemon secret from being read. The token request then failed with `invalid_client`.

The identity error was real for that request, but it was not evidence that the Entra application credential itself was wrong.

### Rule

When a compound command fails:

1. inspect the first failing operation;
2. stop evaluating downstream output derived from null or empty variables;
3. rerun each stage independently.

Recommended diagnostic stages:

```text
Read state/config
    -> obtain required secret/reference
        -> acquire token
            -> inspect token audience
                -> call protected listener
                    -> query logs
```

## 17. FN-015 — The daemon secret and Terraform state are sensitive

**Status:** Preventive  
**Severity:** High  
**Area:** Secret handling

### Observation

The client-credential demo requires a daemon application secret. Terraform creates it and the Container App receives it as a secret reference.

### Implications

The secret may exist in:

- Terraform state;
- Container Apps secret storage;
- process memory during deployment/testing;
- local shell history if handled carelessly.

### Rules

- Never commit state.
- Never paste state content into issues, pull requests, or documentation.
- Never echo the secret.
- Do not store generated token responses.
- Redact bearer tokens from logs.
- Avoid broad `terraform state pull` operations when a safer output or Azure secret reference is available.
- Rotate the secret if it appears in a transcript or external log.

For future automation, prefer workload identity or certificates where compatible with the demonstration requirements.

## 18. FN-016 — Trusting `x-msft-entra-identity` depends on the backend boundary

**Status:** Preventive security rule  
**Severity:** Critical  
**Area:** Header trust

### Observation

Application Gateway injects:

```text
x-msft-entra-identity: <tenant-id>:<object-id>
```

after successful validation.

### Risk

HTTP headers can be supplied by clients. The header is trustworthy only when:

- the request path is exclusively reachable through the validating gateway;
- direct backend access is blocked;
- the gateway overwrites or controls the header;
- the application does not trust it on unrelated routes.

### Rules

- Restrict Container App ingress.
- Use the header only on the gateway demonstration path.
- Keep JwtBearer validation on the UI API path.
- Never convert the header into broad authorization without additional policy.
- Treat JWT Validation as authentication, not complete application authorization.

## 19. FN-017 — The two-hostname design is required for the browser experience

**Status:** Verified architecture constraint  
**Severity:** Medium  
**Area:** Browser authentication

### Problem

Normal browser page navigation does not include the API bearer token. If the SPA's initial page load is behind a JWT `Deny` rule, the user cannot load the page that starts the sign-in flow.

### Resolution

Use:

```text
sentinel.<domain>
```

for the SPA and application-authenticated `/api/*` routes, and:

```text
sentinel-api.<domain>
```

for the gateway-protected SentinelGate `/enter` demonstration.

### Rule

Do not collapse the hostnames as a cleanup unless the browser bootstrapping model is redesigned and tested.

## 20. FN-018 — A valid token response is the definitive end-to-end proof

**Status:** Verified  
**Severity:** High  
**Area:** Acceptance testing

The v2 deployment is proven only when a correct API-audience token returns 200 through the protected hostname and the SentinelGate response shows:

- `service: SentinelGate`;
- `allowed: true`;
- `gatewayValidated: true`;
- `routingContextConsistent: true` as supplementary routing evidence, not authentication;
- the parsed expected tenant ID;
- a parsed non-empty object ID.

This test proves, together:

- token issuance;
- audience configuration;
- gateway validation;
- routing-rule attachment;
- backend reachability;
- header injection;
- application parsing.

It must be paired with negative tests because a 200 alone cannot prove the gateway fails closed.

## 21. Mandatory post-deployment matrix

Run this matrix after:

- initial deployment;
- any gateway update;
- certificate-related restart;
- Entra application change;
- audience change;
- NAT/ingress change;
- provider or API-version upgrade.

| Scenario | Expected result | Backend contacted? | Key proof |
|---|---:|---:|---|
| Missing token | 401 | No | Gateway deny page/response |
| Valid token, wrong audience | 401 | No | Audience enforcement |
| Correct API token | 200 | Yes | Injected identity header |
| Tampered payload/signature | 401 | No | Signature enforcement |
| UI sign-in token to `/api/whoami` | 200 | Yes | ASP.NET Core JwtBearer path |
| Direct backend request from unapproved source | Blocked | No application response | Ingress boundary |
| Post-restart protected request before/after config push | Failure then recovery when issue reproduces | Varies | Preview lifecycle behavior |

## 22. Troubleshooting order

When the protected endpoint fails, follow this order:

1. **Confirm the target.** Verify subscription, resource group, gateway, DNS host, and new-environment IDs.
2. **Confirm TLS and DNS.** Ensure the request reaches the intended gateway.
3. **Confirm live JWT configuration.** Read it through ARM API `2025-05-01`.
4. **Confirm rule attachment.** The protected rule must reference the JWT config.
5. **Confirm outbound networking.** NAT must be attached and functional.
6. **Confirm backend source allow-list.** Include the NAT egress IP.
7. **Confirm token acquisition.** Separate Entra acquisition failure from gateway failure.
8. **Inspect `aud`, `iss`, `tid`, `oid`, `exp`, and `nbf`.**
9. **Run missing-token test.** It must fail closed.
10. **Run correct-token test.** It must return promptly.
11. **If 60-second 500 persists after boot, push full gateway config.**
12. **Query logs after allowing for ingestion delay.**
13. **Check Container App revision and image.**
14. **Check application logs and agent tool errors.**

Avoid changing multiple layers at once. Preserve enough evidence to distinguish token, gateway, network, backend, and application failures.

## 23. Revalidation checklist for future provider/service updates

Before removing any workaround, verify:

- Does AzureRM now expose every JWT validation property and rule reference?
- Does `az network application-gateway update` use a JWT-capable API and preserve unknown properties?
- Do new Application Gateway subnets have an explicit supported outbound model?
- Does the post-boot config-push issue still reproduce?
- Does Application Gateway use the NAT IP for this backend topology?
- Has the Entra/Terraform identifier-URI ownership behavior changed?
- Has the Agent Framework API changed from the 1.15 patterns?
- Is the Application Gateway feature still preview, or has its contract changed?
- Have log schemas or table names changed?
- Is the configured Foundry model/version still deployable in the selected region?

A documentation claim is not enough to remove a workaround that previously prevented a security bypass. Prove the new behavior through the full acceptance matrix.

## 24. Rules that must remain visible to automation and contributors

The following rules must remain visible in contributor guidance and must not be buried only here:

1. Never reuse the original Terraform state.
2. Never use `az network application-gateway update`.
3. Keep NAT on the Application Gateway subnet.
4. Allow the NAT egress IP to reach both Container App backends.
5. After an approved restart, increment and review `gateway_config_generation`, push the full configuration, and run the complete matrix.
6. Preserve `ignore_changes = [identifier_uris]`.
7. Keep the two-hostname architecture.
8. Keep MSAL browser assets local.
9. Keep the two backends structurally isolated and trust the injected header only within the protected-listener, JWT `Deny`, SentinelGate-only pool, and restricted-ingress boundary after canonical-GUID and tenant validation. Treat original-host matching only as additional routing consistency.
10. Run positive and negative token tests after every material gateway change.
11. Protect Terraform state and daemon credentials.
12. Do not declare success while the bootstrap image is active.

## 25. Hosted broker roles may be framework-mapped

During Gate 3, the Hosted Agent reached SentinelApp with the correct managed-identity access token, exact runtime principal, tenant, and `agent.scenario.execute` application role, but the broker returned 403. The app-role assignment in Microsoft Graph was correct. ASP.NET Core JwtBearer had mapped the JWT `roles` value to `ClaimTypes.Role`, while the custom authorization handler inspected only the raw `roles` claim.

The correct interoperability behavior is to inspect both representations and apply the same exact ordinal role comparison. This does not broaden authorization: the broker must still reject delegated scopes and require the configured tenant, exact Hosted Agent principal, and exact application role. Tests must include both raw and framework-mapped claim forms so local test identities cannot mask production claim mapping.

## 26. Gate 4 preflight exposed mixed JWT runtime health

During HostedShadow preflight, a SentinelApp candidate was deployed while retaining `AGENT_MODE=Embedded`. The image-only update did not change SentinelGate, Application Gateway, its configuration-generation value, networking, DNS, certificates, or agent infrastructure. Trusted SentinelApp health passed.

The required protected-listener preflight did not pass consistently. In the first full matrix, missing-token, wrong-audience, and valid-token checks passed before the tampered-token request returned HTTP 500 after approximately 60 seconds. A targeted follow-up then returned HTTP 500 for a missing token after 60.41 seconds and HTTP 401 for a tampered token in 0.32 seconds.

These alternating results are verified evidence that the protected listener was not uniformly healthy across requests. The most likely explanation is that one Application Gateway instance booted or recycled and entered the previously observed preview JWT-validation failure state; that instance-level explanation remains a hypothesis until Azure exposes direct per-instance correlation. The 60-second 500 signature matches the established recovery condition closely enough that HostedShadow activation must stop.

No shadow traffic was activated and the 60-minute observation window did not start. Recovery requires a separate approval to increment `gateway_config_generation`, review an in-place full AzAPI Application Gateway plan, apply that complete configuration push, and rerun the entire protected-listener matrix. Agent integration must not silently perform that gateway recovery.

## 27. FN-025 — Delegated Hosted Agent identity must scope every conversation operation

**Status:** Failure verified, corrected, and live Hosted revalidation passed on 14 August 2026  
**Severity:** High  
**Area:** Foundry Hosted Agent integration

After a generation-2 full AzAPI gateway recovery and three passing JWT matrices, Gate 4 enabled allowlisted `HostedShadow` for one approved tester for 60 minutes. Four read-only browser calls returned successful embedded SSE while all four hosted comparisons failed before inference. Agent-owned Application Insights recorded `POST /responses` HTTP 404 with `Azure.AI.AgentServer.Responses.ResourceNotFoundException`, preceded by 404 from `storage/history/item_ids`. SentinelApp logs recorded four hosted failures and no completed hosted invocation.

The client created the version-pinned session and conversation without the server-derived `x-ms-user-identity`, then added that identity only to `/responses`. Foundry scopes conversation/history resources to the delegated end user, so the response-side history lookup could not see a conversation created outside that scope. The request also used `session_id`; the current Hosted Responses contract uses `agent_session_id`, and a conversation ID can automatically bind the hosted session.

Shadow containment worked: user-visible content remained embedded, the redaction scan across 60 correlated rows found no JWT-like, bearer, client-secret, or private-key indicators, and no model/tool call or token usage occurred. The reviewed automatic rollback restored `Embedded`, removed the tester allowlist, retained the immutable image, passed the complete JWT matrix, and converged with no Terraform changes.

The local SentinelApp source candidate now derives the pseudonymous owner once per mapping, applies the identical `x-ms-user-identity` to session creation, conversation creation, and response invocation, and sends `agent_session_id` with the opaque conversation binding. Focused and static tests enforce these invariants. At the local-correction stage, no image was deployed, no Azure or Terraform state changed, and no new Gate 4 evidence was produced.

The corrected immutable image was first deployed only to SentinelApp in `Embedded`, with no shadow allowlist. Trusted UI health, the vendored MSAL asset, both gateway backend pools, and the protected-listener matrix passed. SentinelGate, both Terraform states, and Application Gateway configuration remained unchanged. Because Embedded mode makes no Hosted request, the corrected identity/session contract still required a bounded shadow observation.

The corrected Gate 4 repeat used a reviewed `0 add, 1 change, 0 destroy` activation plan and a separately saved rollback plan. Four read-only prompts returned embedded HTTP 200 SSE with `[DONE]` while all four Hosted comparisons completed. Foundry recorded four history HTTP 200 calls, four Responses HTTP 200 calls, four successful model calls, four successful agent invocations, and successful toolbox POST operations. The four prompts shared one Hosted session and conversation, recorded 3,195 input and 817 output tokens, and had Hosted P95 of approximately 11.8 seconds. A count-only scan across 108 correlated rows found no JWT-like, bearer, client-secret, or private-key indicators.

One `GET` probe to the IQ toolbox MCP endpoint returned HTTP 405 in 63 ms. It did not fail an invocation: toolbox POST operations returned 200/204 and SentinelApp recorded four Hosted completion events with no failures. Retain this as a non-fatal preview-protocol warning and recheck it after platform or toolbox-client upgrades.

At the observation deadline, the reviewed rollback restored `Embedded`, removed the tester allowlist, passed the complete protected-listener matrix, and converged with no Terraform changes. The client-contract blocker was cleared. Apply the same delegated identity to any future remote reset, deletion, or history operation; the current reset remains local and removes the complete mapping without a Foundry call.

Do not retry the unchanged Gate 4 candidate. The failure is deterministic at the conversation/history boundary and is unrelated to Application Gateway, SentinelGate, the Hosted Agent runtime identity, or the extension restart.

## 28. FN-026 — Gate 5 ordinary chat can pass while tool-intent streams fail

**Status:** Verified failure; automatic rollback completed on 14 August 2026  
**Severity:** High  
**Area:** Foundry Hosted Agent promotion

Gate 5 used an explicitly reviewed plan that changed only SentinelApp `AGENT_MODE` from `Embedded` to `Hosted`. The candidate retained the corrected immutable image and had no shadow allowlist. Trusted UI health, authenticated `/api/whoami`, strict BFF entry through SentinelGate, an IQ-grounded answer with repository citations, and same-session continuity all passed.

Mandatory tool parity did not pass. Two identical prompts asking the Hosted Agent to inspect the live gateway configuration returned SentinelApp's controlled failure message. The failures occurred in 608–782 ms with `HostedProtocolException: Responses stream ended without a completion event.` Agent-owned telemetry nevertheless recorded HTTP 200 for `/responses`, successful model and agent operations, successful history lookup, and HTTP 201 response persistence. No broker-tool dependency appeared for either request.

This evidence rules out a SentinelApp outage, token acquisition delay, Application Gateway failure, and ordinary endpoint authorization failure. It does not yet prove whether the cause is model tool selection, the hosted runtime's terminal-event behavior, or a preview Responses contract change. Do not describe the HTTP 200 managed operations as tool success: the broker was not invoked and the user-visible stream failed closed.

The pre-reviewed rollback plan changed only `AGENT_MODE` to `Embedded`. Both Application Gateway backend pools were healthy, embedded chat streamed successfully, and the protected listener passed missing-token 401, wrong-audience 401, valid-token 200 from SentinelGate with strict schema checks, and tampered-token 401. No Application Gateway, SentinelGate, agent-infrastructure, DNS, or certificate change was made.

Before another Gate 5 attempt, reproduce the tool-intent event sequence directly against immutable Hosted Agent version 6, confirm whether the broker is selected and invoked, capture the terminal SSE event contract, and add a deterministic pre-promotion probe that fails unless a tool result and final completion are both observed. FN-027 records the completed version 7 remediation and supersedes this action item; the version 6 failure remains historical evidence.

## 29. FN-027 — Tool intent needs deterministic routing and bounded terminal-event recovery

**Status:** Version 7 remediation and bounded shadow validation passed on 14 August 2026  
**Severity:** High  
**Area:** Foundry Hosted Agent promotion

Direct replay against version 6 proved that the broker, RBAC, and live Application Gateway read remained functional, so the Gate 5 failure was not a broken broker boundary. The failing promoted calls produced a model response without broker selection and without the terminal event SentinelApp expected. This made tool intent nondeterministic and left the client unable to distinguish explicit incomplete/failed responses from a clean completion.

Version 7 adds mandatory evidence routing: current/live gateway configuration questions must call `get_gateway_config`, recent/live log questions must call `query_gate_logs`, scenario requests call the fixed simulation tool once, and sanitized pending evidence calls `decode_token` once. The SentinelApp client now obtains event type from both the SSE `event` field and JSON `type`, rejects disagreement, recognizes `response.failed`, `response.incomplete`, and `error`, and accepts `response.completed` only with status `completed`. It removes the failed owner/session mapping and permits one fresh-session retry only for a safe read-only request with no pending evidence, no emitted text, and a protocol exception. It never retries scenarios, token evidence, partial output, timeouts, or dependency failures, and it never silently falls back to Embedded within a Hosted request.

Local application, Hosted Agent, and static validation passed. Only SentinelApp was updated, initially in `Embedded`. A reviewed Terraform plan pinned the new immutable Hosted Agent version with only the expected in-place SentinelApp change. Trusted health, Embedded SSE, and the strict protected-listener matrix passed.

A separately reviewed tester-only HostedShadow plan changed only SentinelApp mode and the tester allowlist. Read-only comparisons completed without retry or failure. Correlated traces proved `get_gateway_config` plus ARM HTTP 200, `query_gate_logs` plus Log Analytics HTTP 200, IQ knowledge retrieval plus MCP HTTP 200, and a same-session follow-up with no new session or conversation creation. The pre-reviewed rollback restored `Embedded`, removed the allowlist, passed the strict protected-listener matrix and Embedded chat, and Terraform reported no changes. Application Gateway, SentinelGate, DNS, networking, certificates, and agent Terraform state did not change.

The v7 15-case evaluation produced 13 passes and two zero-output managed responses with response/trace IDs but no finish reason or token metrics. Both affected synthetic cases returned the required behavior on direct isolated replay and then passed a bounded two-case evaluation retry with task adherence and security rubric `2/2`, zero failures, and zero errors. Treat zero-output terminal anomalies as a preview availability risk covered by the narrow client retry, not as proof that HTTP 200 means a usable answer.

## 30. FN-028 — Hosted version 7 passed final Gate 5 promotion

**Status:** Verified on 14 August 2026  
**Severity:** Informational  
**Area:** Foundry Hosted Agent promotion

The explicitly approved activation plan was rechecked before apply and contained only SentinelApp `AGENT_MODE` changing from `Embedded` to `Hosted`: zero additions, one in-place change, and zero destroys. It pinned the reviewed immutable Hosted Agent version and left the shadow tester allowlist empty. A separate saved plan containing only the reverse mode change was created before test traffic.

Trusted UI and vendored MSAL assets, delegated `/api/whoami`, strict BFF forwarding, both backend pools, and the final protected-listener matrix passed. Successful SentinelGate responses had the expected service/schema, tenant, canonical object ID, `gatewayValidated = true`, and `routingContextConsistent = true`. The deterministic scenarios observed the expected deny/allow matrix and retained the configured fixed protected `/enter` target.

Hosted validation exercised ordinary streaming, live gateway configuration, recent gateway logs, IQ grounding and exact repository citations, same-session continuity, explicit reset, fixed simulation, sanitized decode evidence, prompt-injection refusal, and all-zero handle refusal. Correlated spans proved `execute_tool get_gateway_config` plus ARM HTTP 200, `execute_tool query_gate_logs` plus Log Analytics HTTP 200, IQ retrieval, simulation broker HTTP 200, and decode broker HTTP 200. The invalid-handle and exfiltration prompts invoked no prohibited tools. Eleven user-facing checks created 12 successful version 7 agent invocations because one safe read-only IQ call encountered a zero-output protocol condition and used exactly the single allowed fresh-session retry. Simulations, token evidence, partial output, timeouts, and dependency errors were not retried.

All correlated model calls and dependencies succeeded. A count-only scan found zero JWT-like values, bearer values, client-secret values, private keys, or storage keys. Terraform then reported no changes. Application Gateway, SentinelGate, DNS, networking, certificates, agent infrastructure, and agent Terraform state were unchanged. A reviewed Embedded rollback plan remains available. Live cross-user isolation requires separately approved test identities; local contract tests cover owner/session separation and cross-owner evidence denial.

## 31. Final operational takeaway

The hardest failures were not ordinary application bugs. They occurred at the boundaries between:

- preview resource properties and older management APIs;
- Application Gateway runtime dependencies and subnet egress;
- NAT egress and backend ingress restrictions;
- Terraform resource ownership and Entra application properties;
- infrastructure provisioning and application image delivery;
- live request behavior and delayed telemetry.

The correct operating model is therefore:

> Make the deployment deterministic, keep management API versions explicit, verify security behavior with negative tests, and preserve every proven workaround until a full end-to-end test demonstrates it is no longer required.
