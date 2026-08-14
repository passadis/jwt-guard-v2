<p align="center">
  <a href="https://skillicons.dev">
    <img src="https://skillicons.dev/icons?i=azure,terraform,vscode,dotnet,docker,github" />
  </a>
</p>

<h1 align="center">JWT Sentinel</h1>

<h3 align="center">Zero-Code Backend Auth with JWT Validation at the Edge and Microsoft Agents Framework</h3>





**Learn how to offload Entra ID JWT validation to Azure Application Gateway at the edge and use AI agents to explain access logs and gateway rules**

An AI-enhanced web app behind an Application Gateway that enforces **Entra JWT
validation at the edge**. Inside, a Microsoft Agent Framework agent (the *Gate
Explainer*, powered by a Foundry model deployment) reads the live gateway
config and access logs, decodes tokens, and fires real allow/deny requests
through the gate — so the preview feature becomes something you can poke at.

## Architecture

![Knowledge answer flow](images/hl-azgw-jwt.png)



## Why two hostnames?

Browsers do not attach `Authorization` headers to page navigations, so a SPA
behind a `Deny` rule could never load its own login page. Hence:

| Hostname | Gateway JWT validation | Purpose |
| --- | --- | --- |
| `sentinel.<domain>` | none | Serves the SPA; its API validates tokens **in-app** (classic JwtBearer) |
| `sentinel-api.<domain>` | **Deny** | The demo plane — only gateway-validated traffic reaches `/gw/echo` |

Same app, both listeners — a live comparison of edge vs. in-app validation.

## Repo layout

```text
infra/            Terraform (azurerm + azuread + azapi)
src/SentinelApp/  .NET 10 minimal API + Agent Framework agent + SPA (wwwroot)
scripts/          cert issuance, ACR build/deploy, curl demo storyline
DESIGN.md         full design notes
```

## Deploy

### 1. Terraform

```bash
cd infra
cp terraform.tfvars.example terraform.tfvars   # set domain (+ Azure DNS zone if you have one)
terraform init
terraform apply
```

This creates: VNet + public IP, Key Vault (with a **self-signed bootstrap
cert** covering both hostnames), Log Analytics, three Entra app registrations
(api / spa / daemon, with admin-consented permissions), Foundry AI Services +
`gpt-4o` deployment, ACR + Container App (placeholder image), and the
Application Gateway **via AzAPI @ 2025-05-01** with `entraJWTValidationConfigs`
and diagnostic logs wired to Log Analytics.

> The JWT validation portal blade needs the feature flag:
> `https://portal.azure.com/?feature.applicationgatewayjwtvalidation=true`

If your DNS zone is **not** in Azure DNS, point A records for both hostnames at
the `appgw_public_ip` output.

### 2. App image

```powershell
./scripts/deploy-app.ps1 -ResourceGroup rg-jwtsent -AcrName <acr_name output> -AppName ca-jwtsent
```

### 3. Trusted certificate (optional but recommended)

The stack works immediately with the self-signed bootstrap cert (browser
warning). For a clean demo, issue a Let's Encrypt cert — same Key Vault cert
name, so the gateway rolls over automatically:

```powershell
./scripts/issue-cert.ps1 -Domain contoso.com -UiHost sentinel.contoso.com `
  -ApiHost sentinel-api.contoso.com -KeyVaultName <key_vault_name output> `
  -CertName <cert_name output> -DnsZoneSubscriptionId <sub-id>
```

### 4. The storyline

```powershell
./scripts/demo.ps1 -ApiHost sentinel-api.contoso.com -TenantId <tenant_id> `
  -ApiClientId <api_client_id> -DaemonClientId <daemon_client_id> -DaemonSecret <secret>
```

Four requests: no token → 401 · valid **Graph** token (wrong audience) → 401 ·
correct audience → 200 with injected `x-msft-entra-identity` · tampered
payload → 401. Then open `https://sentinel.<domain>`, sign in, and ask the
Gate Explainer *"why were three of my last four requests denied?"* — it reads
the actual access logs and gateway config to answer.

### Flow

![Sequence flow](images/flow-azgw-jwt.png)

## The agent's tools

| Tool | Backing call |
| --- | --- |
| `decode_token` | local decode + diff against gateway config |
| `get_gateway_config` | ARM GET `applicationGateways@2025-05-01` (managed identity, Reader) |
| `query_gate_logs` | KQL on `AGWAccessLogs` (Log Analytics Reader) |
| `simulate_gate_request` | mints tokens via client credentials and calls through the gate |

## Field notes — gotchas we hit deploying this (July 2026)

1. **Default outbound access is retired.** The docs list "outbound connectivity
   from the AppGW subnet to `login.microsoftonline.com:443`" as a prerequisite.
   Since Sept 2025, new subnets get **no default outbound internet access**, so
   a fresh VNet fails this silently: the JWT-validating listener hangs and then
   returns **500 after ~60s** (access logs show `ERRORINFO_NO_ERROR`, backend
   never contacted) while listeners without JWT validation work fine. Fix: a
   **NAT Gateway on the AppGW subnet** (included in this Terraform).
2. **Every instance (re)boot wedges the validation engine; a config push
   revives it.** Reproducible: after initial provisioning, after a full
   recreate, and after every stop/start, JWT-validating listeners return
   60s/500 — until any configuration update is pushed to the gateway, after
   which they answer in ~200ms and stay healthy across further config
   changes. If you restart the gateway (e.g. to roll a Key Vault
   certificate), follow it with a config push (a `terraform apply` of the
   same config does the job via a full PUT).
3. **Never touch the gateway with `az network application-gateway update`.**
   The CLI does GET→modify→PUT at an API version that predates the preview,
   silently deleting `entraJWTValidationConfigs` and the rule references —
   requests then flow to the backend with **no validation at all** (we
   watched a no-token request return 200). Use `az rest` with
   `api-version=2025-05-01` or Terraform/AzAPI for any change.
4. **NAT changes the gateway's egress IP.** With internet-reachable backends
   (here: an external Container Apps FQDN locked down by ingress IP
   restrictions), remember the gateway now reaches the backend from the **NAT
   public IP**, not its frontend IP. Allow both.
5. **`AADSTS500011` for `api://<clientId>` scopes — check Terraform, not
   propagation.** With `azuread_application` plus a separate
   `azuread_application_identifier_uri`, every apply of the application
   resource resets `identifierUris` to `[]`, so client-credentials requests
   for `api://<clientId>/.default` fail while the bare `<clientId>/.default`
   scope keeps working (the gateway accepts both audiences). Fix (included
   here): `lifecycle { ignore_changes = [identifier_uris] }` on the
   application resource.

## Preview caveats

- No SLA; not for production (preview supplemental terms apply).
- Entra ID-issued tokens only — no third-party OIDC.
- Answers *who are you* (authN), not *what may you do* — scopes/roles noted as
  future work in the docs; app-level authZ still belongs to you.
- HTTPS listeners only; Standard_v2/WAF_v2; ARM API ≥ 2025-03-01.
- Log ingestion into Log Analytics lags a few minutes.

## Cost / cleanup

Standard_v2 gateway + Container Apps + AI Services bill while running:

```bash
terraform destroy
```
**Made with ❤️ by [Konstantinos Passadis](https://github.com/passadis)**
