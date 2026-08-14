[CmdletBinding()]
param(
    [string]$ProjectEndpoint,
    [string]$SearchEndpoint,
    [string]$SearchResourceId,
    [string]$AgentPrincipalId,
    [string]$ConnectionName = "jwt-sentinel-iq",
    [string]$ToolboxName = "jwt-sentinel-tools",
    [string]$KnowledgeBaseName = "jwt-sentinel-kb",
    [string]$ToolboxSpecPath = "src/SentinelHostedAgent/toolbox.yaml",
    [string]$ExpectedSubscriptionId,
    [string]$ExpectedTenantId,
    [string]$ApiVersion = "2026-05-01-preview",
    [switch]$Apply,
    [switch]$FunctionsOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Assert-ToolboxName {
    param([Parameter(Mandatory)] [string]$Name, [Parameter(Mandatory)] [string]$Label)
    if ($Name -notmatch '^[a-z0-9][a-z0-9-]{1,126}[a-z0-9]$') {
        throw "$Label must be 3-128 lowercase letters, numbers, or hyphens and cannot begin or end with a hyphen."
    }
}

function Assert-ProjectEndpoint {
    param([Parameter(Mandatory)] [string]$Endpoint)
    $uri = [Uri]$Endpoint
    if ($uri.Scheme -ne "https" -or $uri.Port -ne 443 -or $uri.Query -or $uri.Fragment -or
        $uri.IdnHost -notmatch '^[a-z0-9-]+\.services\.ai\.azure\.com$' -or
        $uri.AbsolutePath -notmatch '^/api/projects/[A-Za-z0-9._-]+/?$') {
        throw "ProjectEndpoint must be a standard-port Foundry project HTTPS endpoint."
    }
    return $Endpoint.TrimEnd('/')
}

function Assert-ToolboxSearchEndpoint {
    param([Parameter(Mandatory)] [string]$Endpoint)
    $uri = [Uri]$Endpoint
    if ($uri.Scheme -ne "https" -or $uri.Port -ne 443 -or $uri.AbsolutePath -ne "/" -or
        $uri.Query -or $uri.Fragment -or $uri.IdnHost -notmatch '^[a-z0-9-]+\.search\.windows\.net$') {
        throw "SearchEndpoint must be a bare standard-port Azure AI Search HTTPS endpoint."
    }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Get-ToolboxPlan {
    param(
        [Parameter(Mandatory)] [string]$FoundryEndpoint,
        [Parameter(Mandatory)] [string]$SearchServiceEndpoint,
        [Parameter(Mandatory)] [string]$BaseName,
        [Parameter(Mandatory)] [string]$Connection,
        [Parameter(Mandatory)] [string]$Toolbox
    )
    [pscustomobject]@{
        Mode = "DryRun"
        ProjectEndpoint = $FoundryEndpoint
        ConnectionName = $Connection
        ToolboxName = $Toolbox
        Authentication = "agentic-identity"
        Audience = "https://search.azure.com/"
        Target = "$SearchServiceEndpoint/knowledgebases/$BaseName/mcp?api-version=$ApiVersion"
        ExposedTool = "knowledge_base_retrieve"
        MutationBoundary = "Connection and toolbox only; no agent deployment, project provisioning, Search write, or RBAC assignment"
    }
}

function Assert-ActiveContext {
    param([Parameter(Mandatory)] [string]$SubscriptionId, [Parameter(Mandatory)] [string]$TenantId)
    $accountJson = & az account show --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "Unable to read the active Azure context." }
    $account = $accountJson | ConvertFrom-Json
    if ($account.id -ne $SubscriptionId -or $account.tenantId -ne $TenantId) {
        throw "Active Azure subscription or tenant does not match the explicitly approved context."
    }
}

function Assert-AgentSearchReader {
    param([Parameter(Mandatory)] [string]$PrincipalId, [Parameter(Mandatory)] [string]$Scope)
    if ($PrincipalId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        throw "AgentPrincipalId must be a canonical GUID."
    }
    if ($Scope -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Search/searchServices/[^/]+$') {
        throw "SearchResourceId must identify one Azure AI Search service."
    }
    $assignments = & az role assignment list --assignee-object-id $PrincipalId --scope $Scope --role "Search Index Data Reader" --fill-principal-name false --query "[].id" -o json --only-show-errors
    if ($LASTEXITCODE -ne 0 -or @($assignments | ConvertFrom-Json).Count -lt 1) {
        throw "The hosted-agent identity does not have Search Index Data Reader on the exact Search service."
    }
}

if ($FunctionsOnly) { return }
if ([string]::IsNullOrWhiteSpace($ProjectEndpoint) -or [string]::IsNullOrWhiteSpace($SearchEndpoint)) {
    throw "ProjectEndpoint and SearchEndpoint are required."
}
$validatedProjectEndpoint = Assert-ProjectEndpoint -Endpoint $ProjectEndpoint
$validatedSearchEndpoint = Assert-ToolboxSearchEndpoint -Endpoint $SearchEndpoint
Assert-ToolboxName -Name $ConnectionName -Label "ConnectionName"
Assert-ToolboxName -Name $ToolboxName -Label "ToolboxName"
Assert-ToolboxName -Name $KnowledgeBaseName -Label "KnowledgeBaseName"
$specFullPath = [System.IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $ToolboxSpecPath))
if (-not $specFullPath.StartsWith($script:RepositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $specFullPath -PathType Leaf)) {
    throw "ToolboxSpecPath must identify the committed toolbox definition inside the repository."
}
$spec = Get-Content -Raw -LiteralPath $specFullPath
if ($spec -notmatch "(?m)^\s*-\s+name:\s+$([regex]::Escape($ConnectionName))\s*$" -or $spec -match '(?m)^\s*(tools|skills):') {
    throw "The toolbox definition must reference only the expected IQ connection."
}
$plan = Get-ToolboxPlan -FoundryEndpoint $validatedProjectEndpoint -SearchServiceEndpoint $validatedSearchEndpoint -BaseName $KnowledgeBaseName -Connection $ConnectionName -Toolbox $ToolboxName
if (-not $Apply) {
    $plan
    Write-Host "Dry run only. No Foundry connection or toolbox was created or updated." -ForegroundColor Yellow
    return
}

foreach ($required in @($ExpectedSubscriptionId, $ExpectedTenantId, $SearchResourceId, $AgentPrincipalId)) {
    if ([string]::IsNullOrWhiteSpace($required)) { throw "Apply requires expected subscription, tenant, Search resource ID, and deployed agent principal ID." }
}
Assert-ActiveContext -SubscriptionId $ExpectedSubscriptionId -TenantId $ExpectedTenantId
if (-not $SearchResourceId.StartsWith("/subscriptions/$ExpectedSubscriptionId/", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "SearchResourceId is outside the explicitly approved subscription."
}
Assert-AgentSearchReader -PrincipalId $AgentPrincipalId -Scope $SearchResourceId

$target = $plan.Target
& azd ai connection create $ConnectionName --kind remote-tool --target $target --auth-type agentic-identity --audience "https://search.azure.com/" --project-endpoint $validatedProjectEndpoint --no-prompt
if ($LASTEXITCODE -ne 0) { throw "Foundry IQ connection creation failed." }

& azd ai toolbox create $ToolboxName --from-file $specFullPath --project-endpoint $validatedProjectEndpoint --no-prompt
if ($LASTEXITCODE -ne 0) { throw "Foundry toolbox creation failed." }

[pscustomobject]@{
    Mode = "Applied"
    ProjectEndpoint = $validatedProjectEndpoint
    ConnectionName = $ConnectionName
    ToolboxName = $ToolboxName
    ToolboxEnvironmentValue = $ToolboxName
    Authentication = "agentic-identity"
    Boundary = "No project provisioning, hosted-agent deployment, Search write, or RBAC assignment was performed."
}
