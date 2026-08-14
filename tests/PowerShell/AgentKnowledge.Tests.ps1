$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
. (Join-Path $root "scripts\publish-agent-knowledge.ps1") -FunctionsOnly
. (Join-Path $root "scripts\configure-agent-toolbox.ps1") -FunctionsOnly

function Test-Throws {
  param([Parameter(Mandatory)] [scriptblock]$Action)

  try {
    & $Action | Out-Null
    return $false
  }
  catch {
    return $true
  }
}

Describe "Foundry IQ knowledge publisher definitions" {
  It "accepts only a bare Azure AI Search HTTPS endpoint" {
    (Assert-SearchEndpoint -Endpoint "https://example.search.windows.net") | Should Be "https://example.search.windows.net"
    (Test-Throws { Assert-SearchEndpoint -Endpoint "http://example.search.windows.net" }) | Should Be $true
    (Test-Throws { Assert-SearchEndpoint -Endpoint "https://example.search.windows.net/indexes/x" }) | Should Be $true
    (Test-Throws { Assert-SearchEndpoint -Endpoint "https://example.invalid" }) | Should Be $true
  }

  It "defines the citation-first semantic index without vectors" {
    $index = New-KnowledgeIndexDefinition -Name "jwt-sentinel-docs-v1"
    $index.name | Should Be "jwt-sentinel-docs-v1"
    $index.semantic.defaultConfiguration | Should Be "jwt-sentinel-semantic"
    ($index.fields.name -contains "content") | Should Be $true
    ($index.fields.name -contains "url") | Should Be $true
    ($index.fields.name -contains "sha256") | Should Be $true
    ($index | ConvertTo-Json -Depth 20) | Should Not Match 'vectorSearch|dimensions|vectorizer'
  }

  It "binds the knowledge source to the one owned index and citation fields" {
    $source = New-KnowledgeSourceDefinition
    $source.kind | Should Be "searchIndex"
    $source.searchIndexParameters.searchIndexName | Should Be "jwt-sentinel-docs-v1"
    $source.searchIndexParameters.semanticConfigurationName | Should Be "jwt-sentinel-semantic"
    ($source.searchIndexParameters.sourceDataFields.name -contains "url") | Should Be $true
  }

  It "keeps knowledge-base retrieval extractive and minimal" {
    $base = New-KnowledgeBaseDefinition
    $base.outputMode | Should Be "extractiveData"
    $base.retrievalReasoningEffort.kind | Should Be "minimal"
    $base.knowledgeSources.Count | Should Be 1
    $base.knowledgeSources[0].name | Should Be "jwt-sentinel-docs"
    $base.models.Count | Should Be 0
    ($base.Contains("retrievalInstructions")) | Should Be $false
  }
}

Describe "Foundry IQ toolbox boundary" {
  It "uses agentic identity for the Search audience and fixed MCP target" {
    $plan = Get-ToolboxPlan `
      -FoundryEndpoint "https://example.services.ai.azure.com/api/projects/project" `
      -SearchServiceEndpoint "https://example.search.windows.net" `
      -BaseName "jwt-sentinel-kb" `
      -Connection "jwt-sentinel-iq" `
      -Toolbox "jwt-sentinel-tools"

    $plan.Authentication | Should Be "agentic-identity"
    $plan.Audience | Should Be "https://search.azure.com/"
    $plan.Target | Should Be "https://example.search.windows.net/knowledgebases/jwt-sentinel-kb/mcp?api-version=2026-05-01-preview"
    $plan.ExposedTool | Should Be "knowledge_base_retrieve"
  }

  It "rejects browser-controlled project and Search URL shapes" {
    (Test-Throws { Assert-ProjectEndpoint -Endpoint "https://example.services.ai.azure.com/api/projects/project/extra" }) | Should Be $true
    (Test-Throws { Assert-ToolboxSearchEndpoint -Endpoint "https://example.search.windows.net/knowledgebases/other" }) | Should Be $true
  }
}
