[CmdletBinding()]
param(
    [string]$SearchEndpoint,
    [string]$ManifestPath = "src/SentinelHostedAgent/knowledge/corpus.json",
    [string]$IndexName = "jwt-sentinel-docs-v1",
    [string]$KnowledgeSourceName = "jwt-sentinel-docs",
    [string]$KnowledgeBaseName = "jwt-sentinel-kb",
    [string]$ApiVersion = "2026-05-01-preview",
    [string]$ExpectedSubscriptionId,
    [string]$ExpectedTenantId,
    [string]$EvidencePath = "src/SentinelHostedAgent/.foundry/results/knowledge-publication.json",
    [switch]$Apply,
    [switch]$FunctionsOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$script:SemanticConfigurationName = "jwt-sentinel-semantic"

function Assert-KnowledgeName {
    param([Parameter(Mandatory)] [string]$Name, [Parameter(Mandatory)] [string]$Label)
    if ($Name -notmatch '^[a-z0-9][a-z0-9-]{1,126}[a-z0-9]$') {
        throw "$Label must be 3-128 lowercase letters, numbers, or hyphens and cannot begin or end with a hyphen."
    }
}

function Assert-SearchEndpoint {
    param([Parameter(Mandatory)] [string]$Endpoint)
    $uri = [Uri]$Endpoint
    if ($uri.Scheme -ne "https" -or $uri.Port -ne 443 -or
        $uri.AbsolutePath -ne "/" -or $uri.Query -or $uri.Fragment -or
        $uri.IdnHost -notmatch '^[a-z0-9-]+\.search\.windows\.net$') {
        throw "SearchEndpoint must be a bare standard-port Azure AI Search HTTPS endpoint."
    }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Get-RepositoryPath {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Label)
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Path))
    $prefix = $script:RepositoryRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must stay within the repository."
    }
    return $fullPath
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)] [string]$Value)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($hash).ToLowerInvariant()
}

function Split-KnowledgeContent {
    param(
        [Parameter(Mandatory)] [string]$Content,
        [string]$DefaultHeading = "Document",
        [int]$MaximumCharacters = 6000
    )

    $sections = [System.Collections.Generic.List[object]]::new()
    $heading = $DefaultHeading
    $buffer = [System.Text.StringBuilder]::new()
    foreach ($line in ($Content -split "`r?`n")) {
        if ($line -match '^#{1,4}\s+(.+?)\s*$' -and $buffer.Length -gt 0) {
            $sections.Add([pscustomobject]@{ Heading = $heading; Content = $buffer.ToString().Trim() })
            $null = $buffer.Clear()
            $heading = $Matches[1].Trim()
        }
        elseif ($line -match '^#{1,4}\s+(.+?)\s*$') {
            $heading = $Matches[1].Trim()
        }
        $null = $buffer.AppendLine($line)
    }
    if ($buffer.Length -gt 0) {
        $sections.Add([pscustomobject]@{ Heading = $heading; Content = $buffer.ToString().Trim() })
    }

    $chunks = [System.Collections.Generic.List[object]]::new()
    foreach ($section in $sections) {
        if ([string]::IsNullOrWhiteSpace($section.Content)) { continue }
        for ($offset = 0; $offset -lt $section.Content.Length; $offset += $MaximumCharacters) {
            $length = [Math]::Min($MaximumCharacters, $section.Content.Length - $offset)
            $chunks.Add([pscustomobject]@{
                Heading = $section.Heading
                Content = $section.Content.Substring($offset, $length).Trim()
            })
        }
    }
    return @($chunks)
}

function Get-CorpusManifest {
    param([Parameter(Mandatory)] [string]$Path)
    $manifestFullPath = Get-RepositoryPath -Path $Path -Label "ManifestPath"
    if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
        throw "Corpus manifest does not exist: $Path"
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestFullPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.corpus -ne "jwt-sentinel-docs-v1") {
        throw "Unsupported or unexpected corpus manifest."
    }
    if ($manifest.classification -ne "public-operational-documentation") {
        throw "The publisher accepts only the approved public operational corpus."
    }
    return $manifest
}

function Get-LocalKnowledgeRecords {
    param([Parameter(Mandatory)] $Manifest)
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($source in $Manifest.localSources) {
        $fullPath = Get-RepositoryPath -Path $source.path -Label "Corpus source"
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Corpus source does not exist: $($source.path)"
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if ($item.LinkType) { throw "Symlinked corpus sources are not accepted: $($source.path)" }
        if ($source.citationUri -notmatch '^urn:jwt-sentinel:repo:[A-Za-z0-9._/-]+$') {
            throw "Corpus source has no approved repository citation URI: $($source.path)"
        }

        $content = Get-Content -Raw -LiteralPath $fullPath
        $sourceHash = Get-Sha256Hex -Value $content
        $chunks = Split-KnowledgeContent -Content $content -DefaultHeading $source.path
        for ($ordinal = 0; $ordinal -lt $chunks.Count; $ordinal++) {
            $chunk = $chunks[$ordinal]
            $id = Get-Sha256Hex -Value "$($source.citationUri)|$ordinal"
            $records.Add([ordered]@{
                id = $id
                content = $chunk.Content
                title = $source.path
                heading = $chunk.Heading
                path = $source.path
                url = $source.citationUri
                sourceKind = "repository"
                revision = $Manifest.repositoryRevision
                classification = $Manifest.classification
                sha256 = $sourceHash
                chunkOrdinal = $ordinal
            })
        }
    }
    return @($records)
}

function Assert-LearnAllowlist {
    param([Parameter(Mandatory)] $Manifest)
    foreach ($value in $Manifest.learnSources) {
        $uri = [Uri]$value
        if ($uri.Scheme -ne "https" -or $uri.Port -ne 443 -or
            $uri.IdnHost -ne "learn.microsoft.com" -or $uri.Query -or $uri.Fragment) {
            throw "Learn source is not an approved canonical HTTPS URL: $value"
        }
    }
}

function ConvertFrom-LearnHtml {
    param([Parameter(Mandatory)] [string]$Html)
    $withoutActiveContent = [regex]::Replace($Html, '(?is)<(script|style|svg|nav|footer)[^>]*>.*?</\1>', ' ')
    $withoutTags = [regex]::Replace($withoutActiveContent, '(?s)<[^>]+>', "`n")
    $decoded = [System.Net.WebUtility]::HtmlDecode($withoutTags)
    $lines = $decoded -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    return ($lines -join "`n")
}

function Get-LearnKnowledgeRecords {
    param([Parameter(Mandatory)] $Manifest)
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($value in $Manifest.learnSources) {
        $response = Invoke-WebRequest -Uri $value -Method Get -MaximumRedirection 5 -TimeoutSec 30 -Headers @{ Accept = "text/markdown, text/html;q=0.9" }
        $finalUri = [Uri]$response.BaseResponse.RequestMessage.RequestUri
        if ($finalUri.Scheme -ne "https" -or $finalUri.Port -ne 443 -or $finalUri.IdnHost -ne "learn.microsoft.com") {
            throw "Learn fetch redirected outside the approved host: $value"
        }
        $contentType = [string]$response.Headers.'Content-Type'
        $content = if ($contentType -match 'text/markdown') { [string]$response.Content } else { ConvertFrom-LearnHtml -Html ([string]$response.Content) }
        if ([string]::IsNullOrWhiteSpace($content) -or $content.Length -lt 200) {
            throw "Learn source returned no usable documentation: $value"
        }
        $sourceHash = Get-Sha256Hex -Value $content
        $title = $finalUri.AbsolutePath.Trim('/').Split('/')[-1]
        $chunks = Split-KnowledgeContent -Content $content -DefaultHeading $title
        for ($ordinal = 0; $ordinal -lt $chunks.Count; $ordinal++) {
            $chunk = $chunks[$ordinal]
            $id = Get-Sha256Hex -Value "$value|$ordinal"
            $records.Add([ordered]@{
                id = $id
                content = $chunk.Content
                title = $title
                heading = $chunk.Heading
                path = $finalUri.AbsolutePath
                url = $value
                sourceKind = "microsoft-learn"
                revision = "retrieved-$([DateTimeOffset]::UtcNow.ToString('yyyy-MM-dd'))"
                classification = $Manifest.classification
                sha256 = $sourceHash
                chunkOrdinal = $ordinal
            })
        }
    }
    return @($records)
}

function New-KnowledgeIndexDefinition {
    param([Parameter(Mandatory)] [string]$Name)
    $fields = @(
        @{ name = "id"; type = "Edm.String"; key = $true; searchable = $false; filterable = $true; retrievable = $true },
        @{ name = "content"; type = "Edm.String"; searchable = $true; filterable = $false; retrievable = $true },
        @{ name = "title"; type = "Edm.String"; searchable = $true; filterable = $true; retrievable = $true },
        @{ name = "heading"; type = "Edm.String"; searchable = $true; filterable = $true; retrievable = $true },
        @{ name = "path"; type = "Edm.String"; searchable = $true; filterable = $true; retrievable = $true },
        @{ name = "url"; type = "Edm.String"; searchable = $false; filterable = $true; retrievable = $true },
        @{ name = "sourceKind"; type = "Edm.String"; searchable = $false; filterable = $true; retrievable = $true },
        @{ name = "revision"; type = "Edm.String"; searchable = $false; filterable = $true; retrievable = $true },
        @{ name = "classification"; type = "Edm.String"; searchable = $false; filterable = $true; retrievable = $true },
        @{ name = "sha256"; type = "Edm.String"; searchable = $false; filterable = $true; retrievable = $true },
        @{ name = "chunkOrdinal"; type = "Edm.Int32"; searchable = $false; filterable = $true; sortable = $true; retrievable = $true }
    )
    return [ordered]@{
        name = $Name
        description = "Allowlisted JWT Sentinel repository and Microsoft Learn operational documentation."
        fields = $fields
        semantic = [ordered]@{
            defaultConfiguration = $script:SemanticConfigurationName
            configurations = @([ordered]@{
                name = $script:SemanticConfigurationName
                prioritizedFields = [ordered]@{
                    titleField = @{ fieldName = "title" }
                    prioritizedContentFields = @(@{ fieldName = "content" })
                    prioritizedKeywordsFields = @(@{ fieldName = "heading" }, @{ fieldName = "path" })
                }
            })
        }
    }
}

function New-KnowledgeSourceDefinition {
    param([string]$Name = "jwt-sentinel-docs", [string]$SearchIndexName = "jwt-sentinel-docs-v1")
    return [ordered]@{
        name = $Name
        description = "Curated JWT Sentinel documentation index with explicit citation fields."
        kind = "searchIndex"
        searchIndexParameters = [ordered]@{
            searchIndexName = $SearchIndexName
            semanticConfigurationName = $script:SemanticConfigurationName
            searchFields = @(@{ name = "content" }, @{ name = "title" }, @{ name = "heading" })
            sourceDataFields = @(
                @{ name = "id" }, @{ name = "title" }, @{ name = "heading" },
                @{ name = "path" }, @{ name = "url" }, @{ name = "sourceKind" },
                @{ name = "revision" }, @{ name = "classification" }, @{ name = "sha256" }
            )
        }
    }
}

function New-KnowledgeBaseDefinition {
    param([string]$Name = "jwt-sentinel-kb", [string]$SourceName = "jwt-sentinel-docs")
    return [ordered]@{
        name = $Name
        description = "Extractive, citation-first knowledge base for the JWT Sentinel Hosted Agent."
        outputMode = "extractiveData"
        knowledgeSources = @(@{ name = $SourceName })
        models = @()
        retrievalReasoningEffort = @{ kind = "minimal" }
    }
}

function Get-PublicationPlan {
    param(
        [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] $Manifest,
        [Parameter(Mandatory)] [object[]]$LocalRecords,
        [string]$Index = "jwt-sentinel-docs-v1",
        [string]$Source = "jwt-sentinel-docs",
        [string]$Base = "jwt-sentinel-kb"
    )
    [pscustomobject]@{
        Mode = "DryRun"
        SearchEndpoint = $Endpoint
        IndexName = $Index
        KnowledgeSourceName = $Source
        KnowledgeBaseName = $Base
        LocalDocuments = @($Manifest.localSources).Count
        LocalChunks = $LocalRecords.Count
        LearnDocuments = @($Manifest.learnSources).Count
        ApiVersion = $ApiVersion
        Authentication = "Microsoft Entra ID for https://search.azure.com/.default; no Search keys"
        Mutations = @("create-or-validate index", "merge-or-upload allowlisted chunks", "create-or-validate knowledge source", "create-or-update owned knowledge base")
    }
}

function Assert-AzureContext {
    param([Parameter(Mandatory)] [string]$SubscriptionId, [Parameter(Mandatory)] [string]$TenantId)
    if ($SubscriptionId -notmatch '^[0-9a-fA-F-]{36}$' -or $TenantId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw "ExpectedSubscriptionId and ExpectedTenantId must be canonical GUIDs."
    }
    $accountJson = & az account show --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "Unable to read the active Azure context." }
    $account = $accountJson | ConvertFrom-Json
    if ($account.id -ne $SubscriptionId -or $account.tenantId -ne $TenantId) {
        throw "Active Azure subscription or tenant does not match the explicitly approved context."
    }
}

function Get-SearchAccessToken {
    $token = (& az account get-access-token --scope "https://search.azure.com/.default" --query accessToken -o tsv --only-show-errors)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw "Unable to acquire an Azure AI Search access token."
    }
    return $token.Trim()
}

function Invoke-SearchJson {
    param(
        [Parameter(Mandatory)] [ValidateSet("GET", "POST", "PUT")] [string]$Method,
        [Parameter(Mandatory)] [string]$Uri,
        [Parameter(Mandatory)] [string]$AccessToken,
        $Body
    )
    $invoke = @{ Method = $Method; Uri = $Uri; Headers = @{ Authorization = "Bearer $AccessToken"; Accept = "application/json" }; TimeoutSec = 60 }
    if ($null -ne $Body) {
        $invoke.ContentType = "application/json"
        $invoke.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    return Invoke-RestMethod @invoke
}

function Assert-CompatibleIndex {
    param([Parameter(Mandatory)] $Existing)
    $required = @("id", "content", "title", "heading", "path", "url", "sourceKind", "revision", "classification", "sha256", "chunkOrdinal")
    foreach ($field in $required) {
        if ($Existing.fields.name -notcontains $field) { throw "Existing index is not owned-compatible: missing field $field." }
    }
    if ($Existing.semantic.defaultConfiguration -ne $script:SemanticConfigurationName) {
        throw "Existing index has an unexpected semantic configuration."
    }
}

function Publish-KnowledgeArtifacts {
    param(
        [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] [object[]]$Records,
        [Parameter(Mandatory)] [string]$AccessToken,
        [Parameter(Mandatory)] [string]$Index,
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Base
    )
    $escapedIndex = [Uri]::EscapeDataString($Index)
    $indexUri = "$Endpoint/indexes/$escapedIndex`?api-version=$ApiVersion"
    try {
        $existingIndex = Invoke-SearchJson -Method GET -Uri $indexUri -AccessToken $AccessToken
        Assert-CompatibleIndex -Existing $existingIndex
    }
    catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
        $null = Invoke-SearchJson -Method PUT -Uri $indexUri -AccessToken $AccessToken -Body (New-KnowledgeIndexDefinition -Name $Index)
    }

    for ($offset = 0; $offset -lt $Records.Count; $offset += 100) {
        $last = [Math]::Min($offset + 99, $Records.Count - 1)
        $batch = @($Records[$offset..$last] | ForEach-Object {
            $copy = [ordered]@{ "@search.action" = "mergeOrUpload" }
            foreach ($property in $_.GetEnumerator()) { $copy[$property.Key] = $property.Value }
            $copy
        })
        $result = Invoke-SearchJson -Method POST -Uri "$Endpoint/indexes/$escapedIndex/docs/index?api-version=$ApiVersion" -AccessToken $AccessToken -Body @{ value = $batch }
        if (@($result.value | Where-Object { -not $_.status }).Count -gt 0) { throw "One or more Search documents failed to upload." }
    }

    $sourceList = Invoke-SearchJson -Method GET -Uri "$Endpoint/knowledgesources?api-version=$ApiVersion" -AccessToken $AccessToken
    $existingSource = @($sourceList.value | Where-Object name -eq $Source)
    if ($existingSource.Count -eq 0) {
        $null = Invoke-SearchJson -Method POST -Uri "$Endpoint/knowledgesources?api-version=$ApiVersion" -AccessToken $AccessToken -Body (New-KnowledgeSourceDefinition -Name $Source -SearchIndexName $Index)
    }
    elseif ($existingSource.Count -ne 1 -or $existingSource[0].kind -ne "searchIndex" -or $existingSource[0].searchIndexParameters.searchIndexName -ne $Index) {
        throw "Existing knowledge source is not owned-compatible."
    }

    $baseUri = "$Endpoint/knowledgebases/$([Uri]::EscapeDataString($Base))?api-version=$ApiVersion"
    try {
        $existingBase = Invoke-SearchJson -Method GET -Uri $baseUri -AccessToken $AccessToken
        if (@($existingBase.knowledgeSources).Count -ne 1 -or $existingBase.knowledgeSources[0].name -ne $Source) {
            throw "Existing knowledge base is not owned-compatible."
        }
    }
    catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
    }
    $null = Invoke-SearchJson -Method PUT -Uri $baseUri -AccessToken $AccessToken -Body (New-KnowledgeBaseDefinition -Name $Base -SourceName $Source)
}

function Write-PublicationEvidence {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] [object[]]$Records,
        [Parameter(Mandatory)] [string]$Index,
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Base
    )
    $fullPath = Get-RepositoryPath -Path $Path -Label "EvidencePath"
    $allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot "src/SentinelHostedAgent/.foundry/results"))
    if (-not $fullPath.StartsWith($allowedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "EvidencePath must stay under src/SentinelHostedAgent/.foundry/results/."
    }
    $sources = @($Records | Group-Object url | ForEach-Object {
        $first = $_.Group[0]
        [ordered]@{
            url = $first.url
            title = $first.title
            sourceKind = $first.sourceKind
            revision = $first.revision
            sha256 = $first.sha256
            chunks = $_.Count
        }
    })
    $evidence = [ordered]@{
        schemaVersion = 1
        publishedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        searchEndpoint = $Endpoint
        indexName = $Index
        knowledgeSourceName = $Source
        knowledgeBaseName = $Base
        chunkCount = $Records.Count
        sources = $sources
        containsDocumentContent = $false
        containsCredentials = $false
    }
    $parent = Split-Path -Parent $fullPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullPath -Encoding utf8NoBOM
}

if ($FunctionsOnly) { return }

if ([string]::IsNullOrWhiteSpace($SearchEndpoint)) { throw "SearchEndpoint is required." }
$validatedEndpoint = Assert-SearchEndpoint -Endpoint $SearchEndpoint
Assert-KnowledgeName -Name $IndexName -Label "IndexName"
Assert-KnowledgeName -Name $KnowledgeSourceName -Label "KnowledgeSourceName"
Assert-KnowledgeName -Name $KnowledgeBaseName -Label "KnowledgeBaseName"
$manifest = Get-CorpusManifest -Path $ManifestPath
Assert-LearnAllowlist -Manifest $manifest
$localRecords = Get-LocalKnowledgeRecords -Manifest $manifest
$plan = Get-PublicationPlan -Endpoint $validatedEndpoint -Manifest $manifest -LocalRecords $localRecords -Index $IndexName -Source $KnowledgeSourceName -Base $KnowledgeBaseName

if (-not $Apply) {
    $plan
    Write-Host "Dry run only. No Microsoft Learn page was fetched and no Azure AI Search object or document was changed." -ForegroundColor Yellow
    return
}

if ([string]::IsNullOrWhiteSpace($ExpectedSubscriptionId) -or [string]::IsNullOrWhiteSpace($ExpectedTenantId)) {
    throw "Apply requires explicit ExpectedSubscriptionId and ExpectedTenantId values."
}
Assert-AzureContext -SubscriptionId $ExpectedSubscriptionId -TenantId $ExpectedTenantId
$learnRecords = Get-LearnKnowledgeRecords -Manifest $manifest
$allRecords = @($localRecords) + @($learnRecords)
$accessToken = Get-SearchAccessToken
try {
    Publish-KnowledgeArtifacts -Endpoint $validatedEndpoint -Records $allRecords -AccessToken $accessToken -Index $IndexName -Source $KnowledgeSourceName -Base $KnowledgeBaseName
}
finally {
    $accessToken = $null
}
Write-PublicationEvidence -Path $EvidencePath -Endpoint $validatedEndpoint -Records $allRecords -Index $IndexName -Source $KnowledgeSourceName -Base $KnowledgeBaseName

[pscustomobject]@{
    Mode = "Applied"
    SearchEndpoint = $validatedEndpoint
    IndexName = $IndexName
    KnowledgeSourceName = $KnowledgeSourceName
    KnowledgeBaseName = $KnowledgeBaseName
    PublishedChunks = $allRecords.Count
    EvidencePath = $EvidencePath
    McpEndpoint = "$validatedEndpoint/knowledgebases/$KnowledgeBaseName/mcp?api-version=$ApiVersion"
    Boundary = "No Search key was used or stored. Existing documents were not pruned."
}
