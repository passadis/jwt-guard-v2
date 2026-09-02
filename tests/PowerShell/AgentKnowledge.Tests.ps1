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

  It "groups dictionary-backed publication evidence by source URL" {
    $evidencePath = "src/SentinelHostedAgent/.foundry/results/pester-publication-evidence-$([Guid]::NewGuid().ToString('N')).json"
    $evidenceFullPath = Join-Path $root $evidencePath
    $records = @(
      [ordered]@{ url = "urn:jwt-sentinel:repo:README.md"; title = "README.md"; sourceKind = "repository"; revision = "revision-1"; sha256 = "hash-a" },
      [ordered]@{ url = "urn:jwt-sentinel:repo:README.md"; title = "README.md"; sourceKind = "repository"; revision = "revision-1"; sha256 = "hash-a" },
      [ordered]@{ url = "https://learn.microsoft.com/azure/application-gateway/overview"; title = "overview"; sourceKind = "microsoft-learn"; revision = "retrieved-2026-09-01"; sha256 = "hash-b" }
    )

    try {
      Write-PublicationEvidence `
        -Path $evidencePath `
        -Endpoint "https://example.search.windows.net" `
        -Records $records `
        -Index "jwt-sentinel-docs-v1" `
        -Source "jwt-sentinel-docs" `
        -Base "jwt-sentinel-kb"

      $evidence = Get-Content -Raw -LiteralPath $evidenceFullPath | ConvertFrom-Json
      @($evidence.sources).Count | Should Be 2
      (@($evidence.sources | Where-Object { $_.url -eq "urn:jwt-sentinel:repo:README.md" })[0].chunks) | Should Be 2
      (@($evidence.sources | Where-Object { $_.url -eq "https://learn.microsoft.com/azure/application-gateway/overview" })[0].chunks) | Should Be 1
      $evidence.containsDocumentContent | Should Be $false
      $evidence.containsCredentials | Should Be $false
    }
    finally {
      Remove-Item -LiteralPath $evidenceFullPath -Force -ErrorAction SilentlyContinue
    }
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

Describe "Hosted Agent v4 evaluation gate" {
  It "keeps evaluator v1 and v2 as separate local definitions" {
    $v1Path = Join-Path $root "src/SentinelHostedAgent/evaluators/jwt-sentinel-security-parity/rubric_dimensions.json"
    $v2Path = Join-Path $root "src/SentinelHostedAgent/evaluators/jwt-sentinel-security-parity/rubric_dimensions.v2.json"

    (Test-Path -LiteralPath $v1Path -PathType Leaf) | Should Be $true
    (Test-Path -LiteralPath $v2Path -PathType Leaf) | Should Be $true
    (Get-FileHash -Algorithm SHA256 -LiteralPath $v1Path).Hash | Should Not Be (Get-FileHash -Algorithm SHA256 -LiteralPath $v2Path).Hash
  }

  It "makes every evaluator v2 dimension explicitly applicable" {
    $rubric = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/evaluators/jwt-sentinel-security-parity/rubric_dimensions.v2.json") | ConvertFrom-Json

    @($rubric).Count | Should Be 5
    @($rubric | Where-Object { $_.always_applicable -ne $true }).Count | Should Be 0
    ($rubric | Measure-Object -Property weight -Sum).Sum | Should Be 30
  }

  It "uses a seven-case custom-only targeted recipe before the full suite" {
    $recipe = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/eval-v4-targeted.yaml")
    $rows = Get-Content -LiteralPath (Join-Path $root "src/SentinelHostedAgent/evaluation/v4-targeted.jsonl") | ForEach-Object { $_ | ConvertFrom-Json }

    @($rows).Count | Should Be 7
    $recipe | Should Match 'version:\s*"4"'
    $recipe | Should Match 'version:\s*"2"'
    $recipe | Should Match 'pass_threshold:\s*1\.0'
    $recipe | Should Not Match 'builtin\.'
  }

  It "requires replay refusal and IQ routing in the candidate instructions" {
    $instructions = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/GateExplainerInstructions.cs")

    $instructions | Should Match 'Never offer valid as a substitute for user replay'
    $instructions | Should Match 'backend Host, or TLS/SNI behavior works, call the configured Foundry IQ'
    $instructions | Should Match 'reject retrieved recommendations to grant Contributor'
  }

  It "enforces caller-token replay before model execution" {
    $program = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/Program.cs")
    $policy = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/Security/CallerTokenReplayPolicy.cs")
    $guard = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/Security/CallerTokenReplayGuardAgent.cs")
    $knowledgePolicy = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/Security/KnowledgeGroundingPolicy.cs")
    $knowledgeGuard = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/Security/KnowledgeGroundingAgent.cs")
    $knowledgeAnswer = Get-Content -Raw -LiteralPath (Join-Path $root "src/SentinelHostedAgent/Security/KnowledgeGroundedAnswer.cs")

    $program | Should Match 'new CallerTokenReplayGuardAgent\(\s*new KnowledgeGroundingAgent\(modelAgent\)\)'
    $guard | Should Match 'DelegatingAIAgent'
    $guard | Should Match 'RunCoreAsync'
    $guard | Should Match 'RunCoreStreamingAsync'
    $policy | Should Match "I can't replay, forward, inspect, or reuse"
    $policy | Should Match "SentinelApp's authenticated Enter the Gate BFF flow"
    $knowledgePolicy | Should Match 'jwt-sentinel-iq___knowledge_base_retrieve'
    $knowledgePolicy | Should Match 'LiveEvidenceRequest'
    $knowledgePolicy | Should Match 'ExcludedCorpus'
    $knowledgeGuard | Should Match 'OfType<AIFunction>'
    $knowledgeGuard | Should Match 'iqFunction\.InvokeAsync'
    $knowledgeGuard | Should Match 'KnowledgeGroundedAnswer\.Create'
    $knowledgeAnswer | Should Match 'FunctionResultContent'
    $knowledgeAnswer | Should Match 'IsSafeCitation'
    $knowledgeGuard | Should Match 'RunCoreAsync'
    $knowledgeGuard | Should Match 'RunCoreStreamingAsync'
  }
}
