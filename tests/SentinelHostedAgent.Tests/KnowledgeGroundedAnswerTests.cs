using Microsoft.Extensions.AI;
using SentinelHostedAgent.Security;

namespace SentinelHostedAgent.Tests;

public sealed class KnowledgeGroundedAnswerTests
{
    [Fact]
    public void Create_ReturnsBoundedHostAnswerWithExactReturnedCitation()
    {
        var answer = KnowledgeGroundedAnswer.Create(
            "What Host header reaches SentinelGate?",
            CreateEvidence("docs/ARCHITECTURE.md",
                "pickHostNameFromBackendAddress keeps the ACA FQDN as the backend Host and TLS/SNI name."));

        Assert.NotNull(answer);
        Assert.Contains("Container App FQDN", answer, StringComparison.Ordinal);
        Assert.EndsWith("Sources: docs/ARCHITECTURE.md", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsEvidenceWithoutRequiredFacts()
    {
        var answer = KnowledgeGroundedAnswer.Create(
            "What Host header reaches SentinelGate?",
            CreateEvidence("docs/ARCHITECTURE.md", "Unrelated content."));

        Assert.Null(answer);
    }

    [Fact]
    public void Create_RejectsInventedOrUnsafeCitationShape()
    {
        var answer = KnowledgeGroundedAnswer.Create(
            "What Host header reaches SentinelGate?",
            CreateEvidence("https://evil.example/source",
                "pickHostNameFromBackendAddress keeps the ACA FQDN as the backend Host and TLS/SNI name."));

        Assert.Null(answer);
    }

    private static ChatMessage[] CreateEvidence(string title, string content)
    {
        const string callId = "iq-call";
        var result = $$"""
            {"content":[{"type":"text","text":"[{\"title\":\"{{title}}\",\"content\":\"{{content}}\"}]"}]}
            """;

        return [
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent(
                    callId,
                    KnowledgeGroundingPolicy.RequiredToolName,
                    new Dictionary<string, object?> { ["queries"] = new[] { "question" } })
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]),
        ];
    }
}
