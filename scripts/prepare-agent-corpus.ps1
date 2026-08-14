[CmdletBinding()]
param(
    [string]$ManifestPath = "src/SentinelHostedAgent/knowledge/corpus.json",
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$manifestFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ManifestPath))
if (-not $manifestFullPath.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ManifestPath must stay within the repository."
}

$manifest = Get-Content -Raw -LiteralPath $manifestFullPath | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.corpus -ne "jwt-sentinel-docs-v1") {
    throw "Unsupported or unexpected corpus manifest."
}

$records = [System.Collections.Generic.List[object]]::new()
foreach ($source in $manifest.localSources) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $source.path))
    if (-not $candidate.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Corpus source escaped the repository: $($source.path)"
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Corpus source does not exist: $($source.path)"
    }
    $item = Get-Item -LiteralPath $candidate -Force
    if ($item.LinkType) {
        throw "Symlinked corpus sources are not accepted: $($source.path)"
    }
    if ($source.citationUri -notmatch '^urn:jwt-sentinel:repo:[A-Za-z0-9._/-]+$') {
        throw "Corpus source has no approved repository citation URI: $($source.path)"
    }

    $content = Get-Content -Raw -LiteralPath $candidate
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($content))
    $hash = [System.Convert]::ToHexString($hashBytes).ToLowerInvariant()
    $records.Add([ordered]@{
        id = $hash
        title = $source.path
        path = $source.path
        url = $source.citationUri
        revision = $manifest.repositoryRevision
        classification = $manifest.classification
        sha256 = $hash
        content = $content
    })
}

foreach ($url in $manifest.learnSources) {
    $uri = [Uri]$url
    if ($uri.Scheme -ne "https" -or $uri.IdnHost -ne "learn.microsoft.com" -or $uri.Query -or $uri.Fragment) {
        throw "Learn source is not an approved canonical HTTPS URL: $url"
    }
}

if ($OutputPath) {
    $outputFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
    if (-not $outputFullPath.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputPath must stay within the repository."
    }
    $parent = Split-Path -Parent $outputFullPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $records | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 8 } | Set-Content -LiteralPath $outputFullPath -Encoding utf8NoBOM
}

[pscustomobject]@{
    Corpus = $manifest.corpus
    LocalDocuments = $records.Count
    LearnSources = $manifest.learnSources.Count
    OutputWritten = [bool]$OutputPath
    Boundary = "No Microsoft Learn fetch, Search write, knowledge-base creation, toolbox creation, or Azure mutation was performed."
}
