# JWT Sentinel — Clean Deployment Runbook

**Runbook version:** 1.0  
**Date:** 2026-08-04  
**Applies to:** Clean JWT Sentinel repository and a new, isolated Azure environment  
**Primary references:** `README.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/FIELD-NOTES.md`
**Shell examples:** PowerShell 7 unless marked Bash  
**Deployment model:** Terraform infrastructure followed by ACR build/Container App update and trusted-certificate issuance

---

## 1. Purpose

This runbook provides the end-to-end operational procedure for deploying JWT Sentinel into a **new Azure environment without modifying the existing running deployment**.

It covers:

1. Repository and Git isolation.
2. Terraform state isolation.
3. Azure subscription, tenant, DNS, quota, and permission preflight.
4. Static validation and reviewed planning.
5. Infrastructure deployment.
6. Application image build and Container App revision activation.
7. DNS and TLS validation.
8. Trusted certificate issuance and rollover.
9. Application Gateway JWT configuration verification.
10. Browser, API, agent, telemetry, and security testing.
11. Post-restart recovery.
12. Troubleshooting, evidence capture, and teardown.

This runbook does not grant authorization to execute destructive or environment-changing actions. The pause gates defined below still require explicit approval.

### Current v2 topology

JWT Sentinel v2 deploys two Container Apps behind one Application Gateway:

- UI hostname → SentinelApp only.
- Protected hostname with JWT `Deny` → SentinelGate only.
- SentinelApp owns the SPA, authenticated APIs, Agent, tools, logs, configuration inspection, and BFF `/api/gate/enter` flow.
- SentinelGate exposes only `/healthz` and `/enter`, strictly parses the canonical injected identity, applies an additional original-host routing-context check, and has ACR pull permission only.
- Both backend settings keep `pickHostNameFromBackendAddress = true`; Application Gateway therefore sends each ACA FQDN as the backend `Host` and TLS/SNI name.
- SentinelGate treats `x-original-host` only as client-originated routing context. Its match with the protected public hostname is a consistency check, not authentication or proof of JWT validation.

Any older singular “Container App” wording in historical evidence refers to the original deployment and must not be used to collapse the v2 backends.

---

## 2. Non-negotiable safety rules

1. **Do not deploy from the original repository folder.**
2. **Do not copy or reuse the original Terraform state.**
3. **Do not reuse the original remote-state key or Terraform workspace.**
4. **Do not import the running JWT Sentinel resources into the new state.**
5. **Do not reuse the original Entra applications.**
6. **Do not reuse the original resource names, hostnames, or certificate name.**
7. **Do not use `az network application-gateway update`.**
8. **Do not remove the NAT Gateway from the Application Gateway subnet.**
9. **Do not expose Terraform state, the daemon secret, bearer tokens, or certificate files.**
10. **Do not declare success while the Container App still runs the bootstrap image.**
11. **Do not trust `x-msft-entra-identity` on a path that can bypass Application Gateway.**
12. **Do not treat the working UI listener as proof that JWT Validation is healthy.**

When any command output indicates an unexpected update, replacement, import, or destroy, stop before applying.

---

## 3. Required approval gates

| Gate | Action requiring approval | Minimum information presented before approval |
|---|---|---|
| A | `terraform apply` | Active tenant/subscription, resource group, hostnames, state path/key, plan summary, create/update/replace/destroy counts |
| B | Let's Encrypt production certificate issuance | Domain, two hostnames, Key Vault, certificate name, DNS-zone subscription |
| C | Application Gateway stop/start or intentional recreation | Reason, expected impact, recovery/config-push method, test plan |
| D | `terraform destroy` or manual deletion | Target environment, destroy plan, DNS impact, Entra apps affected, state backup/retention plan |
| E | Git commit, push, or PR creation | New repository remote, intended branch, file/secret scan result |

Local formatting, validation, builds, and Terraform planning are permitted when requested. The gated actions above require explicit current-session authorization.

---

## 4. Roles

| Role | Responsibilities |
|---|---|
| Deployment operator | Executes commands, captures evidence, stops at approval gates |
| Azure subscription owner or delegated platform engineer | Confirms target subscription, role-assignment rights, quota, policy, and DNS access |
| Entra administrator or application administrator | Confirms application-registration and consent permissions |
| Reviewer | Reviews Terraform plan, security boundaries, hostnames, and acceptance evidence |
| Demo owner | Confirms application behavior, storyline, and cleanup timing |

One person may hold several roles in a lab environment, but plan review should still be deliberate.

---

## 5. Inputs worksheet

Complete this table before running Azure or Terraform commands.

| Input | Value |
|---|---|
| New local repository path | `<NEW_REPOSITORY_PATH>` |
| New Git repository URL | `<NEW_GIT_REMOTE_OR_NOT_YET_CREATED>` |
| Azure tenant ID | `<TENANT_ID>` |
| Azure subscription name | `<SUBSCRIPTION_NAME>` |
| Azure subscription ID | `<SUBSCRIPTION_ID>` |
| Azure location | `<LOCATION>` |
| Resource prefix | `<PREFIX>` |
| Expected resource group | `rg-<PREFIX>` |
| Base domain | `<DOMAIN>` |
| UI hostname | `<UI_SUBDOMAIN>.<DOMAIN>` |
| Protected API hostname | `<API_SUBDOMAIN>.<DOMAIN>` |
| Azure DNS zone name | `<DNS_ZONE_NAME>` |
| Azure DNS zone resource group | `<DNS_ZONE_RESOURCE_GROUP>` |
| DNS-zone subscription ID | `<DNS_ZONE_SUBSCRIPTION_ID>` |
| Terraform state model | `local` or `remote azurerm` |
| Remote backend key, if used | `jwt-sentinel-v2/<ENVIRONMENT>.tfstate` |
| Model deployment name | `<MODEL_DEPLOYMENT_NAME>` |
| Model name/version | `<MODEL_NAME>` / `<MODEL_VERSION>` |
| Model capacity | `<MODEL_CAPACITY>` |
| Environment owner | `<OWNER>` |
| Intended expiry/cleanup date | `<DATE>` |

The values must describe the **new** environment. Any value matching the running environment must be reviewed as a potential collision.

---

## 6. Tooling prerequisites

Required locally:

- Git.
- Terraform compatible with the repository constraints.
- Azure CLI.
- PowerShell 7.
- .NET 10 SDK.
- Access to the new repository folder.
- Azure CLI authentication to the intended tenant.
- Permissions to create Azure resources, role assignments, and Entra applications.
- DNS control for the selected domain.
- A deployable Foundry/Azure AI model configuration in the selected region.

Recommended verification:

```powershell
git --version
terraform version
az version
$PSVersionTable.PSVersion
dotnet --info
```

Stop when the installed .NET SDK cannot build the target framework used by `src/SentinelApp/SentinelApp.csproj`.

---

# Part I — Repository and state isolation

## 7. Create the clean repository folder

### 7.1 Recommended: new Git history

Copy or extract the source into a new folder, excluding `.git`, `.terraform`, state, plans, and populated environment files.

Example:

```powershell
$Source = "<SOURCE_FOLDER>"
$Target = "<NEW_REPOSITORY_PATH>"

New-Item -ItemType Directory -Path $Target -Force | Out-Null

robocopy $Source $Target /E `
  /XD ".git" ".terraform" `
  /XF "terraform.tfstate" "terraform.tfstate.backup" "*.tfplan" "terraform.tfvars"

Set-Location $Target
git init
```

Review the `robocopy` result. A non-zero `robocopy` exit code does not always mean failure; inspect its summary.

### 7.2 Alternative: retain history but disconnect the original remote

Only use this option deliberately:

```powershell
Set-Location "<NEW_REPOSITORY_PATH>"
git remote -v
git remote remove origin
```

Do not add the new remote yet if repository content still contains secrets or deployment-specific artifacts.

---

## 8. Verify local isolation

Run:

```powershell
Get-Location
git rev-parse --show-toplevel
git remote -v
```

Expected:

- The path is the new folder.
- Git root is the new folder.
- No original GitHub remote is listed.

Inspect forbidden artifacts:

```powershell
Get-ChildItem -Force -Recurse `
  | Where-Object {
      $_.Name -eq ".terraform" -or
      $_.Name -like "terraform.tfstate*" -or
      $_.Name -like "*.tfplan" -or
      $_.Name -eq "terraform.tfvars"
    } `
  | Select-Object FullName
```

Before deleting anything, confirm every returned path belongs to the new folder.

Remove copied deployment artifacts only from the new folder:

```powershell
Remove-Item -Recurse -Force "infra/.terraform" -ErrorAction SilentlyContinue
Remove-Item -Force "infra/terraform.tfstate" -ErrorAction SilentlyContinue
Remove-Item -Force "infra/terraform.tfstate.backup" -ErrorAction SilentlyContinue
Remove-Item -Force "infra/*.tfplan" -ErrorAction SilentlyContinue
Remove-Item -Force "infra/terraform.tfvars" -ErrorAction SilentlyContinue
```

Re-run the inspection command. It should return nothing.

---

## 9. Confirm required repository files

Verify:

```powershell
$RequiredFiles = @(
  "README.md",
  "docs/ARCHITECTURE.md",
  "docs/DECISIONS.md",
  "docs/FIELD-NOTES.md",
  "infra/providers.tf",
  "infra/variables.tf",
  "infra/main.tf",
  "infra/entra.tf",
  "infra/ai.tf",
  "infra/app.tf",
  "infra/appgw.tf",
  "infra/outputs.tf",
  "infra/terraform.tfvars.example",
  "src/SentinelApp/SentinelApp.csproj",
  "src/SentinelApp/Program.cs",
  "src/SentinelApp/Dockerfile",
  "src/SentinelApp/wwwroot/lib/msal-browser.min.js",
  "src/SentinelGate/SentinelGate.csproj",
  "src/SentinelGate/Program.cs",
  "src/SentinelGate/Dockerfile",
  "tests/SentinelApp.Tests/SentinelApp.Tests.csproj",
  "tests/SentinelGate.Tests/SentinelGate.Tests.csproj",
  "scripts/test-static.ps1",
  "scripts/deploy-app.ps1",
  "scripts/issue-cert.ps1",
  "scripts/demo.ps1"
)

$Missing = $RequiredFiles | Where-Object { -not (Test-Path $_) }
$Missing
```

Expected: no output.

When files are missing, restore them from the source repository before proceeding. Do not improvise a replacement for the locally vendored MSAL library.

---

## 10. Scan for old-environment identifiers

Search for:

- old resource group;
- old domain and hostnames;
- old subscription and tenant IDs;
- original client IDs;
- original ACR, Key Vault, and Container App names;
- plaintext secrets;
- original Git remote.

Example:

```powershell
$Patterns = @(
  "<OLD_RESOURCE_GROUP>",
  "<OLD_DOMAIN>",
  "<OLD_SUBSCRIPTION_ID>",
  "<OLD_TENANT_ID>",
  "<OLD_API_CLIENT_ID>",
  "<OLD_SPA_CLIENT_ID>",
  "<OLD_DAEMON_CLIENT_ID>"
)

foreach ($Pattern in $Patterns) {
  if ($Pattern -and -not $Pattern.StartsWith("<")) {
    Write-Host "`nSearching for $Pattern"
    git grep -n -- $Pattern
  }
}
```

Every match must be classified as:

- intentional historical documentation;
- neutral example;
- or an environment value that must be removed.

Do not keep real IDs in example files.

---

# Part II — Azure and environment preflight

## 11. Authenticate and select the target

Authenticate:

```powershell
az login --tenant "<TENANT_ID>"
```

Select the subscription:

```powershell
az account set --subscription "<SUBSCRIPTION_ID>"
```

Record the active context:

```powershell
az account show `
  --query "{subscription:name,subscriptionId:id,tenantId:tenantId,user:user.name}" `
  -o table
```

Compare it with the inputs worksheet.

### Stop condition

Stop when:

- tenant is incorrect;
- subscription is incorrect;
- the target is the original environment unintentionally;
- or the signed-in identity is not the expected deployment identity.

---

## 12. Check naming and resource-group collision

Expected resource group:

```powershell
$Prefix = "<PREFIX>"
$ResourceGroup = "rg-$Prefix"
```

Check whether it already exists:

```powershell
az group exists --name $ResourceGroup
```

Expected for a clean deployment: `false`.

When it returns `true`, do not assume it is harmless. Inspect:

```powershell
az group show --name $ResourceGroup -o table
az resource list --resource-group $ResourceGroup -o table
```

Choose a new prefix unless the resource group is explicitly confirmed as an empty, intended target.

---

## 13. Verify DNS-zone control

Set variables:

```powershell
$Domain = "<DOMAIN>"
$DnsZoneName = "<DNS_ZONE_NAME>"
$DnsZoneResourceGroup = "<DNS_ZONE_RESOURCE_GROUP>"
$DnsZoneSubscriptionId = "<DNS_ZONE_SUBSCRIPTION_ID>"
$UiHost = "<UI_SUBDOMAIN>.$Domain"
$ApiHost = "<API_SUBDOMAIN>.$Domain"
```

Check the zone:

```powershell
az network dns zone show `
  --subscription $DnsZoneSubscriptionId `
  --resource-group $DnsZoneResourceGroup `
  --name $DnsZoneName `
  -o table
```

Check for collisions:

```powershell
az network dns record-set a show `
  --subscription $DnsZoneSubscriptionId `
  --resource-group $DnsZoneResourceGroup `
  --zone-name $DnsZoneName `
  --name "<UI_SUBDOMAIN>" `
  -o table

az network dns record-set a show `
  --subscription $DnsZoneSubscriptionId `
  --resource-group $DnsZoneResourceGroup `
  --zone-name $DnsZoneName `
  --name "<API_SUBDOMAIN>" `
  -o table
```

A `not found` response is expected for new records. Existing records require deliberate review.

Do not overwrite records belonging to the running environment.

---

## 14. Verify permissions and policy

The deployment identity must be able to:

- create the Azure resources described in `docs/ARCHITECTURE.md` and represented by the reviewed Terraform plan;
- create role assignments;
- create Entra applications and service principals;
- grant the required delegated/application permissions;
- create or update the intended DNS records;
- deploy the selected model.

Check subscription role assignments as appropriate:

```powershell
$SignedInObjectId = az ad signed-in-user show --query id -o tsv

az role assignment list `
  --assignee $SignedInObjectId `
  --subscription "<SUBSCRIPTION_ID>" `
  --all `
  -o table
```

This is an inventory, not proof that every required permission is present.

Confirm tenant-level application-registration and consent rights with the Entra administrator before apply.

### Policy check

List relevant policy assignments when subscription policy may block public IPs, Key Vault configuration, Container Apps ingress, or AI Services:

```powershell
az policy assignment list --scope "/subscriptions/<SUBSCRIPTION_ID>" -o table
```

Do not weaken policy automatically. Adapt the design only through an explicit reviewed change.

---

## 15. Confirm model availability and quota

Before apply, verify that the selected:

- Azure region;
- model name;
- model version;
- deployment type;
- and capacity

are supported for the target subscription.

The source repository keeps these values configurable because the working combination may differ between regions and dates.

When quota or availability is uncertain, change `terraform.tfvars` before planning rather than discovering the issue midway through apply.

---

# Part III — Configuration and local validation

## 16. Create `terraform.tfvars`

From the repository root:

```powershell
Copy-Item "infra/terraform.tfvars.example" "infra/terraform.tfvars"
```

Edit only the new file.

Example structure:

```hcl
prefix   = "<PREFIX>"
location = "<LOCATION>"

domain        = "<DOMAIN>"
ui_subdomain  = "<UI_SUBDOMAIN>"
api_subdomain = "<API_SUBDOMAIN>"

dns_zone_name           = "<DNS_ZONE_NAME>"
dns_zone_resource_group = "<DNS_ZONE_RESOURCE_GROUP>"

model_deployment_name = "<MODEL_DEPLOYMENT_NAME>"
model_name            = "<MODEL_NAME>"
model_version         = "<MODEL_VERSION>"
model_capacity        = <MODEL_CAPACITY>
```

Do not place secrets in `terraform.tfvars`.

Verify the file is ignored:

```powershell
git check-ignore -v "infra/terraform.tfvars"
```

Expected: a matching ignore rule. Add one before proceeding when the file is not ignored.

---

## 17. Configure Terraform state

### 17.1 Local state

Local state is acceptable for the first clean rebuild when:

- only one operator uses it;
- the state is protected;
- it is never committed;
- and the operator understands that it contains sensitive values.

A clean folder creates a new local state automatically.

### 17.2 Remote `azurerm` state

When remote state is used, the backend resources must already exist or be bootstrapped separately.

Use a unique backend key:

```text
jwt-sentinel-v2/<ENVIRONMENT>.tfstate
```

Never use the original key, workspace, or state path.

Example backend configuration file, stored outside Git when it contains environment-specific values:

```hcl
resource_group_name  = "<TFSTATE_RESOURCE_GROUP>"
storage_account_name = "<TFSTATE_STORAGE_ACCOUNT>"
container_name       = "<TFSTATE_CONTAINER>"
key                  = "jwt-sentinel-v2/<ENVIRONMENT>.tfstate"
```

Initialize with:

```powershell
terraform -chdir=infra init `
  -reconfigure `
  -backend-config="<BACKEND_CONFIG_FILE>"
```

For local state:

```powershell
terraform -chdir=infra init -reconfigure
```

### State verification

```powershell
terraform -chdir=infra state list
```

Expected before first apply: no managed resources.

When existing resources appear, stop. The wrong state has been initialized.

---

## 18. Terraform formatting and validation

Run:

```powershell
terraform -chdir=infra fmt -check -recursive
terraform -chdir=infra validate
```

When formatting fails:

```powershell
terraform -chdir=infra fmt -recursive
terraform -chdir=infra fmt -check -recursive
```

Do not continue with validation errors.

---

## 19. Build the .NET application locally

```powershell
dotnet restore "src/SentinelApp/SentinelApp.csproj"
dotnet build "src/SentinelApp/SentinelApp.csproj" -c Release --no-restore
```

Expected:

- successful restore;
- successful .NET 10 Release build;
- no missing Agent Framework APIs;
- no missing static assets required by the project.

Do not downgrade the target framework or replace the known Agent Framework 1.15 patterns to bypass a local SDK mismatch.

---

## 20. Create and review the Terraform plan

Create the plan:

```powershell
terraform -chdir=infra plan -out=tfplan
```

Render it:

```powershell
terraform -chdir=infra show -no-color tfplan `
  | Set-Content -Path "infra/tfplan.txt" -Encoding utf8
```

Review:

```powershell
Get-Content "infra/tfplan.txt"
```

### Mandatory plan review

Confirm:

- one new resource group;
- one new VNet and Application Gateway subnet;
- NAT Gateway and NAT public IP;
- Application Gateway public IP;
- Key Vault, bootstrap certificate, and gateway identity;
- Log Analytics;
- ACR, Container Apps environment, SentinelApp, SentinelGate, and their separate identities;
- Foundry/Azure AI Services and model deployment;
- three new Entra applications and service principals;
- new role assignments;
- new DNS records when configured;
- Application Gateway through AzAPI `2025-05-01`;
- `entraJWTValidationConfigs`;
- protected routing-rule JWT reference;
- no original resource IDs;
- no imports;
- no deletions;
- no unexpected replacements.

Inspect plan counts at the end.

### Secret handling

`tfplan`, `tfplan.txt`, and Terraform state may contain sensitive values. Do not commit or share them broadly.

---

## 21. Approval Gate A — Infrastructure apply

Before requesting approval, report:

```text
Tenant:
Subscription:
Resource group:
Location:
UI hostname:
Protected API hostname:
State model:
State path/key:
Plan summary:
Creates:
Updates:
Replacements:
Destroys:
Known preview workarounds present:
  - NAT on AppGW subnet
  - AzAPI 2025-05-01
  - JWT rule reference
  - identifier URI lifecycle safeguard
  - both ACA NAT egress allow-lists
  - isolated UI → SentinelApp and API → SentinelGate pools
  - SentinelGate ACR-pull-only identity
```

Do not run apply until Gate A is approved.

---

# Part IV — Infrastructure deployment

## 22. Apply the reviewed plan

After approval:

```powershell
terraform -chdir=infra apply "tfplan"
```

Do not use `-auto-approve` in the documented operator workflow.

Capture the final result without exposing sensitive values.

Delete the rendered plan text after it is no longer needed:

```powershell
Remove-Item "infra/tfplan.txt" -ErrorAction SilentlyContinue
Remove-Item "infra/tfplan" -ErrorAction SilentlyContinue
```

---

## 23. Capture safe Terraform outputs

List outputs:

```powershell
terraform -chdir=infra output
```

Capture non-sensitive values:

```powershell
$UiUrl = terraform -chdir=infra output -raw ui_url
$ProtectedApiUrl = terraform -chdir=infra output -raw protected_api_url
$AppGwPublicIp = terraform -chdir=infra output -raw appgw_public_ip
$TenantId = terraform -chdir=infra output -raw tenant_id
$ApiClientId = terraform -chdir=infra output -raw api_client_id
$SpaClientId = terraform -chdir=infra output -raw spa_client_id
$DaemonClientId = terraform -chdir=infra output -raw daemon_client_id
$AcrName = terraform -chdir=infra output -raw acr_name
$SentinelAppName = terraform -chdir=infra output -raw sentinel_app_name
$SentinelGateName = terraform -chdir=infra output -raw sentinel_gate_name
$ResourceGroup = terraform -chdir=infra output -raw resource_group
$KeyVaultName = terraform -chdir=infra output -raw key_vault_name
$CertName = terraform -chdir=infra output -raw cert_name
```

Do not print or capture a daemon secret in an evidence file.

Record only the safe values in the deployment record.

---

## 24. Verify created resources

```powershell
az resource list --resource-group $ResourceGroup -o table
```

Confirm the expected resource categories.

Verify Entra applications exist using the new client IDs:

```powershell
az ad app show --id $ApiClientId --query "{displayName:displayName,appId:appId,identifierUris:identifierUris}" -o json
az ad app show --id $SpaClientId --query "{displayName:displayName,appId:appId}" -o json
az ad app show --id $DaemonClientId --query "{displayName:displayName,appId:appId}" -o json
```

The API app must contain:

```text
api://<API_CLIENT_ID>
```

If it is absent, stop and follow `FIELD-NOTES.md` FN-005 before continuing.

---

## 25. Verify network and ingress configuration

Identify public IPs:

```powershell
az network public-ip list `
  --resource-group $ResourceGroup `
  --query "[].{name:name,ip:ipAddress}" `
  -o table
```

Verify NAT attachment:

```powershell
az network vnet subnet show `
  --resource-group $ResourceGroup `
  --vnet-name "vnet-<PREFIX>" `
  --name "snet-appgw" `
  --query "{subnet:id,natGateway:natGateway.id}" `
  -o json
```

Use the actual names from the Terraform code/output when they differ.

Inspect both Container App ingress restrictions:

```powershell
az containerapp show `
  --resource-group $ResourceGroup `
  --name $SentinelAppName `
  --query "properties.configuration.ingress.ipSecurityRestrictions" `
  -o json
```

Confirm the list includes the source addresses required by the active gateway-to-ACA path, including the NAT public IP.

Repeat the same command with `$SentinelGateName`. Both apps must allow only the reviewed gateway frontend and NAT egress addresses.

---

## 26. Verify Application Gateway JWT configuration

Get the gateway ID without changing the resource:

```powershell
$AppGwName = "agw-<PREFIX>"

$AppGwId = az resource show `
  --resource-group $ResourceGroup `
  --resource-type "Microsoft.Network/applicationGateways" `
  --name $AppGwName `
  --query id `
  -o tsv
```

Read through the JWT-capable API:

```powershell
$AppGwJson = az rest `
  --method get `
  --url "https://management.azure.com$AppGwId?api-version=2025-05-01" `
  | ConvertFrom-Json
```

Inspect:

```powershell
$AppGwJson.properties.entraJWTValidationConfigs | ConvertTo-Json -Depth 10

$AppGwJson.properties.requestRoutingRules `
  | Select-Object name, @{
      Name = "JwtConfigId"
      Expression = { $_.properties.entraJWTValidationConfig.id }
    } `
  | Format-Table
```

Confirm:

- JWT config exists.
- Tenant ID is the new tenant.
- Client ID is the new API client ID.
- Audiences include `api://<clientId>` and the bare GUID.
- Unauthorized action is `Deny`.
- The protected rule references the JWT config.
- The UI rule does not.

Never use `az network application-gateway update` during verification or recovery.

---

## 27. Check backend health

Read-only CLI use is acceptable:

```powershell
az network application-gateway show-backend-health `
  --resource-group $ResourceGroup `
  --name $AppGwName `
  -o json
```

At this stage, backend health may reflect the bootstrap image. It must still become healthy.

A healthy backend does not prove JWT Validation works.

---

# Part V — Application image deployment

## 28. Confirm bootstrap state

Inspect both active revisions and images using the `sentinel_app_name` and `sentinel_gate_name` outputs.

```powershell
az containerapp revision list `
  --resource-group $ResourceGroup `
  --name $SentinelAppName `
  --query "[].{name:name,active:properties.active,health:properties.healthState,provisioning:properties.provisioningState,image:properties.template.containers[0].image}" `
  -o table
```

Repeat for `$SentinelGateName`. It is expected that both first revisions may use Terraform bootstrap images.

Do not mark the application ready at this point.

---

## 29. Build and deploy JWT Sentinel

Run the repository script:

```powershell
./scripts/deploy-app.ps1 `
  -ResourceGroup $ResourceGroup `
  -AcrName $AcrName `
  -AppName $SentinelAppName `
  -GateAppName $SentinelGateName `
  -UiHost $UiHost `
  -ApiHost $ApiHost `
  -ApiClientId $ApiClientId
```

The script builds both image repositories, updates both Container Apps, verifies active image tags, waits for healthy provisioned revisions, checks SentinelApp `/healthz` through the UI listener, and checks SentinelGate `/healthz` through the protected listener with an Azure CLI user token. Add `-AllowBootstrapCertificate` only during the explicitly understood bootstrap-certificate phase.

### Monitor the revision

```powershell
az containerapp revision list `
  --resource-group $ResourceGroup `
  --name $SentinelAppName `
  --query "[].{name:name,active:properties.active,health:properties.healthState,provisioning:properties.provisioningState,image:properties.template.containers[0].image}" `
  -o table
```

Expected:

- newest revision active;
- provisioning successful;
- healthy;
- image from `<ACR>.azurecr.io/sentinel-app:<TAG>`.

Repeat for SentinelGate and require `<ACR>.azurecr.io/sentinel-gate:<TAG>`.

### Application logs

When the revision fails:

```powershell
az containerapp logs show `
  --resource-group $ResourceGroup `
  --name $SentinelAppName `
  --type system `
  --tail 100

az containerapp logs show `
  --resource-group $ResourceGroup `
  --name $SentinelAppName `
  --type console `
  --tail 100
```

Resolve the first failure before retrying.

---

## 30. Test application health

Through the UI hostname:

```powershell
curl.exe -i "$UiUrl/healthz"
```

Expected: HTTP 200.

Check the page:

```powershell
curl.exe -I $UiUrl
```

During the bootstrap certificate phase, curl may reject the self-signed chain. A temporary certificate bypass can be used only to prove routing:

```powershell
curl.exe -k -I $UiUrl
```

A `-k` success is not final TLS acceptance.

---

# Part VI — DNS and trusted TLS

## 31. Verify DNS resolution

```powershell
Resolve-DnsName $UiHost
Resolve-DnsName $ApiHost
```

Expected: both resolve to `$AppGwPublicIp`.

When DNS is external to Azure DNS, create the records through the authorized DNS process before continuing.

Check from a public resolver when local caching or split DNS may interfere.

---

## 32. Verify bootstrap certificate behavior

Inspect both hostnames:

```powershell
curl.exe -k -I "https://$UiHost"
curl.exe -k -X POST -I "https://$ApiHost/enter"
```

Expected during bootstrap:

- TLS connection succeeds with bypass;
- UI route responds;
- protected no-token request is denied.

Do not issue a trusted certificate until DNS resolves correctly to the new gateway.

---

## 33. Approval Gate B — Production certificate issuance

Report:

```text
Domain:
UI hostname:
API hostname:
DNS zone:
DNS-zone subscription:
Key Vault:
Certificate name:
Current DNS target:
```

Obtain approval before invoking Let's Encrypt production issuance.

---

## 34. Issue the trusted certificate

After approval:

```powershell
./scripts/issue-cert.ps1 `
  -Domain $Domain `
  -UiHost $UiHost `
  -ApiHost $ApiHost `
  -KeyVaultName $KeyVaultName `
  -CertName $CertName `
  -DnsZoneSubscriptionId $DnsZoneSubscriptionId `
  -AcmeEnvironment Staging `
  -PoshAcmeVersion <REVIEWED_PINNED_VERSION>
```

The script must import the new certificate under the same Key Vault certificate name used by the bootstrap certificate.

Verify versions:

```powershell
az keyvault certificate list-versions `
  --vault-name $KeyVaultName `
  --name $CertName `
  --query "[].{id:id,enabled:attributes.enabled,created:attributes.created,expires:attributes.expires}" `
  -o table
```

---

## 35. Verify trusted TLS

Without bypass:

```powershell
curl.exe -I "https://$UiHost"
curl.exe -i -X POST "https://$ApiHost/enter"
```

Expected:

- trusted certificate chain;
- hostname match for both hosts;
- UI response on the UI host;
- 401 for no token on the protected host.

When the old certificate remains:

1. Verify the new Key Vault version exists.
2. Verify the gateway references the unversioned secret URI.
3. Allow normal certificate pickup.
4. Restart only when necessary and approved.

---

# Part VII — Application Gateway restart recovery

## 36. Approval Gate C — Restart

Do not stop/start Application Gateway merely to speed routine operations.

When restart is necessary, present:

- reason;
- target gateway;
- expected outage;
- trusted-certificate status;
- full configuration-push method;
- post-restart test matrix.

Obtain approval before restarting.

---

## 37. Restart and recover

After approval, use the approved stop/start method.

Immediately after the gateway returns:

1. Test the protected no-token request.
2. If requests hang and return 500 after about 60 seconds, apply the known recovery.
3. Increment `gateway_config_generation` in the isolated environment variables and review the resulting in-place tag change on the AzAPI gateway.
4. Apply that reviewed plan so the existing AzAPI resource resubmits its complete `2025-05-01` body.
5. Verify the live JWT policy, protected-rule attachment, and isolated SentinelGate backend.
6. Run the full matrix.
7. Never use `az network application-gateway update`.

First attempt:

```powershell
terraform -chdir=infra plan -out=tfplan-recovery
terraform -chdir=infra show -no-color tfplan-recovery
terraform -chdir=infra apply "tfplan-recovery"
```

The plan must show the reviewed `gateway_config_generation` tag update in place and must not replace the gateway. A no-change plan means the generation was not incremented; do not improvise a duplicate gateway payload in a script.

This recovery must be performed by an engineer who understands the full gateway payload. Do not improvise with an older CLI API.

Delete recovery plan files afterward.

---

## 38. Post-restart verification

Run the complete matrix in Part VIII.

Also re-read the gateway through API `2025-05-01` and confirm the JWT config and protected-rule reference remain present.

---

# Part VIII — End-to-end validation

## 39. Obtain the daemon secret securely

The demo script needs the daemon secret. Retrieve it into memory without displaying it:

```powershell
$DaemonSecret = az containerapp secret list `
  --resource-group $ResourceGroup `
  --name $SentinelAppName `
  --show-values `
  --query "[?name=='daemon-secret'].value | [0]" `
  -o tsv
```

Do not echo `$DaemonSecret`, save it to a file, or place it in shell history.

After testing:

```powershell
$DaemonSecret = $null
[System.GC]::Collect()
```

PowerShell memory clearing is best effort; it is not a substitute for secret rotation after exposure.

---

## 40. Run the scripted gateway scenarios

```powershell
./scripts/demo.ps1 `
  -ApiHost $ApiHost `
  -TenantId $TenantId `
  -ApiClientId $ApiClientId `
  -DaemonClientId $DaemonClientId `
  -DaemonSecret $DaemonSecret
```

Expected:

| Scenario | Expected |
|---|---:|
| No token | 401 |
| Valid Graph/wrong-audience token | 401 |
| Correct API audience | 200 |
| Tampered token | 401 |

The valid response must contain the gateway-injected identity.

---

## 41. Manual missing-token test

```powershell
curl.exe -i -X POST "https://$ApiHost/enter"
```

Expected:

- HTTP 401;
- prompt response, not a 60-second 500;
- backend not contacted.

If it returns 200, treat this as a security incident in the lab:

1. stop further demo traffic;
2. inspect the live gateway JWT configuration;
3. inspect the protected-rule reference;
4. identify whether an unsafe gateway update removed the properties.

---

## 42. Correct-audience token test

Acquire the token through the repository's demo tooling or a secure client-credential call.

The intended scope is:

```text
api://<API_CLIENT_ID>/.default
```

The bare GUID scope is a diagnostic fallback:

```text
<API_CLIENT_ID>/.default
```

A bare-GUID success does not eliminate the requirement to verify the `api://` identifier URI.

Expected protected response:

- HTTP 200;
- `service: SentinelGate`;
- `allowed: true`;
- `gatewayValidated: true`;
- `routingContextConsistent: true`, which is supplementary routing evidence and not JWT proof by itself;
- tenant ID;
- object ID.

---

## 43. Verify API identifier URI persistence

```powershell
az ad app show `
  --id $ApiClientId `
  --query "{appId:appId,identifierUris:identifierUris}" `
  -o json
```

Run a normal Terraform plan again:

```powershell
terraform -chdir=infra plan
```

Confirm it does not attempt to clear the identifier URI.

The API application Terraform resource must preserve:

```hcl
lifecycle {
  ignore_changes = [identifier_uris]
}
```

---

## 44. Browser and UI-plane test

Open:

```text
https://<UI_HOST>
```

Verify:

1. Page loads with trusted TLS.
2. Browser developer console has no `msal is not defined`.
3. `/lib/msal-browser.min.js` returns 200.
4. Entra sign-in begins.
5. Sign-in completes.
6. SPA obtains the delegated API token.
7. `/api/whoami` returns the signed-in user.
8. The UI identifies the application/JwtBearer validation path.
9. Sign-out clears the local session.

Do not protect the initial SPA navigation with the gateway JWT `Deny` rule.

---

## 45. Tool endpoint tests

While signed in:

### Token decode

- Paste a non-sensitive sample token.
- Confirm claims are decoded.
- Confirm the UI states that decoding is not cryptographic validation.
- Confirm the token is not retained.

### Live gateway config

- Call the configuration tool.
- Confirm it matches the ARM API verification.

### Simulations

Run:

- user replay;
- valid daemon token;
- wrong audience;
- tampered;
- missing.

Confirm actual status codes are shown.

### Agent chat

Ask:

```text
Show me the active gateway JWT configuration.
```

Then:

```text
Why was the wrong-audience request denied?
```

Expected:

- server-sent event streaming;
- tool use;
- evidence-based response;
- no invented log entries;
- acknowledgment of ingestion delay when logs are not yet present.

---

## 46. Verify direct-backend restriction

Get the SentinelGate Container App FQDN:

```powershell
$SentinelGateFqdn = az containerapp show `
  --resource-group $ResourceGroup `
  --name $SentinelGateName `
  --query "properties.configuration.ingress.fqdn" `
  -o tsv
```

From an unapproved external source:

```powershell
curl.exe -i -X POST "https://$SentinelGateFqdn/enter"
```

Expected: blocked by ingress restrictions, with no normal application response.

A successful direct response means the gateway identity header trust boundary is not adequately protected.

---

## 47. Verify backend health after real image deployment

```powershell
az network application-gateway show-backend-health `
  --resource-group $ResourceGroup `
  --name $AppGwName `
  -o json
```

Expected: healthy backend associated with the Container App.

Pair this check with the token matrix; backend health alone is insufficient.

---

## 48. Verify logs

Find the workspace customer ID:

```powershell
$WorkspaceId = az monitor log-analytics workspace list `
  --resource-group $ResourceGroup `
  --query "[0].customerId" `
  -o tsv
```

Example KQL:

```kusto
AGWAccessLogs
| where TimeGenerated > ago(30m)
| where Host in ("<UI_HOST>", "<API_HOST>")
| project TimeGenerated, Host, HttpMethod, RequestUri, HttpStatus,
          TimeTaken, ServerRouted, ServerStatus, ErrorInfo, TransactionId
| order by TimeGenerated desc
```

Run:

```powershell
$Query = @"
AGWAccessLogs
| where TimeGenerated > ago(30m)
| where Host in ('$UiHost', '$ApiHost')
| project TimeGenerated, Host, HttpMethod, RequestUri, HttpStatus,
          TimeTaken, ServerRouted, ServerStatus, ErrorInfo, TransactionId
| order by TimeGenerated desc
"@

az monitor log-analytics query `
  --workspace $WorkspaceId `
  --analytics-query $Query `
  -o table
```

Allow for ingestion delay.

`ERRORINFO_NO_ERROR` is not proof of success; interpret it with HTTP status, timing, and backend routing fields.

---

## 49. Mandatory acceptance matrix

Run after initial deployment and after every material gateway, identity, audience, NAT, ingress, certificate-restart, provider, or API-version change.

| Test | Expected | Pass |
|---|---|---|
| Terraform format | Pass | ☐ |
| Terraform validate | Pass | ☐ |
| .NET Release build | Pass | ☐ |
| Plan targets only new environment | Yes | ☐ |
| ACR application image active | Yes | ☐ |
| Container App revision healthy | Yes | ☐ |
| DNS: UI host → new AppGW IP | Yes | ☐ |
| DNS: API host → new AppGW IP | Yes | ☐ |
| Trusted TLS on UI host | Yes | ☐ |
| Trusted TLS on API host | Yes | ☐ |
| AppGW backend health | Healthy | ☐ |
| Missing token to `/enter` | 401; backend reachability confirmed separately by telemetry | ☐ |
| Wrong-audience token | 401, backend not contacted | ☐ |
| Tampered token | 401, backend not contacted | ☐ |
| Correct API token | 200 | ☐ |
| Injected identity present | Yes | ☐ |
| UI SPA loads | Yes | ☐ |
| Local MSAL asset loads | Yes | ☐ |
| Entra sign-in | Pass | ☐ |
| `/api/whoami` | 200 via JwtBearer | ☐ |
| Direct ACA backend from unapproved IP | Blocked | ☐ |
| Live gateway config tool | Accurate | ☐ |
| Log-query tool | Accurate after ingestion | ☐ |
| Simulation tool | All scenarios correct | ☐ |
| Agent response | Grounded in tools | ☐ |
| Original running environment | Unchanged | ☐ |

No deployment is complete until every applicable item passes.

---

# Part IX — Evidence and handover

## 50. Deployment evidence record

Capture:

```text
Deployment date/time:
Operator:
Reviewer:
Git commit:
Tenant:
Subscription:
Resource group:
Location:
State model/key:
UI URL:
Protected API URL:
AppGW public IP:
NAT public IP:
API client ID:
SPA client ID:
Daemon client ID:
Key Vault:
Certificate name/version:
Container image/tag:
Terraform plan counts:
Acceptance matrix result:
Known exceptions:
Cleanup date:
```

Do not include:

- daemon secret;
- bearer tokens;
- Terraform state;
- PFX/PEM content;
- sensitive plan values.

---

## 51. Documentation updates

After successful validation:

1. Update `README.md` with environment-neutral commands.
2. Update `docs/FIELD-NOTES.md` only with reproduced observations.
3. Mark hypotheses clearly.
4. Update `docs/ARCHITECTURE.md` and `docs/DECISIONS.md` when architecture or accepted decisions change.
5. Keep the public runbook and contributor safety policy aligned with hard safety rules.
6. Remove plan files and temporary outputs.
7. Run a repository secret scan before Git publication.

Example basic check:

```powershell
git status --short
git diff --check
git grep -n -I -E "(client_secret|DAEMON_CLIENT_SECRET|BEGIN[ ]PRIVATE KEY|BEGIN[ ]CERTIFICATE)"
```

Review matches; configuration variable names are not automatically secret leaks, but values may be.

---

## 52. Approval Gate E — Git publication

Before adding the new remote:

```powershell
git remote -v
git status
```

Report:

- intended new remote;
- branch;
- files to commit;
- secret-scan result;
- confirmation that `terraform.tfvars`, state, plans, certificates, and secrets are excluded.

After approval:

```powershell
git remote add origin "<NEW_REPOSITORY_URL>"
```

Commit/push only under the approved workflow.

---

# Part X — Troubleshooting

## 53. Troubleshooting sequence

When the protected endpoint fails:

1. Confirm subscription, tenant, resource group, gateway, and hostname.
2. Confirm DNS and TLS reach the intended new gateway.
3. Read the live gateway with API `2025-05-01`.
4. Confirm `entraJWTValidationConfigs`.
5. Confirm the protected rule references the JWT config.
6. Confirm NAT is attached to the gateway subnet.
7. Confirm ACA ingress includes the NAT egress IP.
8. Confirm token acquisition independently.
9. Inspect `aud`, `iss`, `tid`, `oid`, `exp`, and `nbf`.
10. Run the no-token test.
11. Run the correct-token test.
12. For approximately 60-second 500 after boot, push the full safe gateway configuration.
13. Allow for log-ingestion delay.
14. Check Container App revision/image.
15. Check system and application logs.
16. Change one layer at a time.

Use `docs/FIELD-NOTES.md` for detailed symptom/root-cause mappings.

---

## 54. Critical symptom responses

### Protected request takes about 60 seconds and returns 500

Check:

- NAT attachment;
- outbound connectivity;
- post-boot configuration push.

Do not rebuild repeatedly before trying the safe config push.

### UI works but protected API fails

The UI path does not exercise gateway JWT validation. Run the protected matrix.

### No-token request returns 200

Treat as a fail-open condition.

Check whether JWT config or rule reference was removed. Review recent gateway-management commands. Never use the legacy CLI update path.

### Gateway cannot reach ACA after NAT

Check that ACA allows the NAT public IP, not only the gateway frontend IP.

### `AADSTS500011`

Inspect the actual Entra API application's `identifierUris` and the Terraform plan. Do not assume propagation before checking property ownership.

### Container App remains on Hello World

Run `scripts/deploy-app.ps1`, inspect the active revision image, and verify ACR pull permissions.

### `msal is not defined`

Verify the local `/lib/msal-browser.min.js` file exists and returns 200.

### Logs missing

Wait for Log Analytics ingestion and retry. Do not manufacture a diagnosis.

### Terraform state locked

Stop or allow the competing Terraform process to finish. Do not delete state.

---

# Part XI — Teardown

## 55. Teardown prerequisites

Teardown requires Approval Gate D.

Before requesting approval:

1. Confirm the exact state and target environment.
2. Verify the original environment is not represented in the state.
3. Capture required non-secret evidence.
4. Confirm whether DNS records are managed by Terraform.
5. Confirm whether the DNS zone itself is shared.
6. Confirm the three new Entra applications to be deleted.
7. Confirm no other deployment depends on the resource group.
8. Confirm that removing the Application Gateway `prevent_destroy` lifecycle guard is explicitly included in the teardown approval request.
9. Create and review a destroy plan only after that guard change is approved.

---

## 56. Create the destroy plan

The committed Application Gateway resource has `prevent_destroy = true`. This is intentional: normal plans and `gateway_config_generation` updates cannot replace or destroy the gateway. A full destroy plan will fail until an explicitly approved teardown-only change removes that guard. Do not remove it merely to make planning convenient, and restore it if teardown is cancelled.

```powershell
terraform -chdir=infra plan -destroy -out=tfdestroy
terraform -chdir=infra show -no-color tfdestroy `
  | Set-Content "infra/tfdestroy.txt"
```

Review every resource.

The destroy plan must not include:

- the original JWT Sentinel environment;
- shared DNS zones not owned by this deployment;
- shared storage services not owned by this deployment;
- unrelated Entra applications.

---

## 57. Approval Gate D — Destroy

Report:

```text
Environment:
Resource group:
State path/key:
DNS records:
Entra applications:
Resources to destroy:
Shared resources excluded:
Evidence retained:
```

Do not destroy without approval.

---

## 58. Execute teardown

After approval:

```powershell
terraform -chdir=infra apply "tfdestroy"
```

or, only when the reviewed workflow explicitly authorizes it:

```powershell
terraform -chdir=infra destroy
```

Do not manually delete resources first; doing so can break Terraform's ability to clean up Entra and role-assignment resources.

---

## 59. Verify teardown

```powershell
az group exists --name $ResourceGroup
```

Expected: `false`, unless the resource group was intentionally retained.

Verify the new Entra applications no longer exist:

```powershell
az ad app show --id $ApiClientId
az ad app show --id $SpaClientId
az ad app show --id $DaemonClientId
```

Expected: not found.

Verify DNS records are removed only when they were owned by this deployment.

Delete local plan files:

```powershell
Remove-Item "infra/tfdestroy" -ErrorAction SilentlyContinue
Remove-Item "infra/tfdestroy.txt" -ErrorAction SilentlyContinue
```

Handle state according to the approved retention policy. State remains sensitive even after destroy.

---

## 60. Final operator checklist

### Before apply

- [ ] New folder and repository confirmed.
- [ ] Original remote removed.
- [ ] No copied state or `.terraform`.
- [ ] New `terraform.tfvars`.
- [ ] Correct tenant and subscription.
- [ ] New prefix and resource group.
- [ ] New DNS hostnames with no collision.
- [ ] Unique state path/key.
- [ ] Model availability checked.
- [ ] Terraform validation passed.
- [ ] .NET build passed.
- [ ] Plan reviewed.
- [ ] Gate A approved.

### Before declaring ready

- [ ] Real ACR image active.
- [ ] Container App healthy.
- [ ] NAT attached.
- [ ] NAT IP allowed by ACA.
- [ ] JWT config and rule reference present.
- [ ] Trusted TLS on both hosts.
- [ ] Negative token tests fail closed.
- [ ] Correct token returns 200 with injected identity.
- [ ] SPA sign-in and `/api/whoami` pass.
- [ ] Direct backend access blocked.
- [ ] Logs and agent tools work.
- [ ] Original environment unchanged.
- [ ] Evidence captured.
- [ ] Cleanup date assigned.

### Before destroy

- [ ] Correct state selected.
- [ ] Destroy plan reviewed.
- [ ] Shared resources excluded.
- [ ] Gate D approved.
- [ ] Post-destroy Azure, Entra, and DNS verification completed.

---

## 61. Operational principle

The deployment is successful only when it proves both sides of the security contract:

> Valid requests are forwarded with the expected identity, and invalid requests are denied at the gateway before reaching the backend.

A green Terraform apply, healthy backend, working UI, or successful 200 response is not sufficient by itself. The deployment must pass the complete positive and negative matrix while remaining isolated from the original JWT Sentinel environment.
