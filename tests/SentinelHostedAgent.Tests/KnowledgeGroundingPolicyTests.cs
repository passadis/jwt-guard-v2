using Microsoft.Extensions.AI;
using SentinelHostedAgent.Security;

namespace SentinelHostedAgent.Tests;

public sealed class KnowledgeGroundingPolicyTests
{
    [Theory]
    [InlineData("Why are SentinelApp and SentinelGate separate Container Apps?")]
    [InlineData("Explain the protected listener trust boundary.")]
    [InlineData("How does pickHostNameFromBackendAddress affect backend TLS and SNI?")]
    [InlineData("Describe why x-original-host is not authentication proof.")]
    [InlineData("What Host header reaches SentinelGate?")]
    public void ShouldRequireIq_ReturnsTrueForArchitectureKnowledgeQuestions(string prompt)
    {
        Assert.True(KnowledgeGroundingPolicy.ShouldRequireIq(prompt));
    }

    [Theory]
    [InlineData("Inspect the live gateway JWT validation configuration.")]
    [InlineData("Show me the recent gateway logs.")]
    [InlineData("Run the wrong audience scenario.")]
    [InlineData("Decode the token claims.")]
    [InlineData("What is in docs/history?")]
    [InlineData("Read the archived session JSONL.")]
    public void ShouldRequireIq_ReturnsFalseForOtherEvidenceRoutes(string prompt)
    {
        Assert.False(KnowledgeGroundingPolicy.ShouldRequireIq(prompt));
    }

    [Fact]
    public void ShouldRequireIq_UsesOnlyLatestUserMessage()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "Why are there two Container Apps?"),
            new(ChatRole.Assistant, "Earlier response"),
            new(ChatRole.User, "Inspect the live gateway configuration."),
        };

        Assert.False(KnowledgeGroundingPolicy.ShouldRequireIq(messages));
    }
}
