using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SentinelHostedAgent.Security;

namespace SentinelHostedAgent.Tests;

public sealed class KnowledgeGroundingAgentTests
{
    [Fact]
    public async Task RunAsync_ArchitectureQuestionRequiresIqTool()
    {
        var inner = new CapturingAgent();
        var agent = new KnowledgeGroundingAgent(inner);

        var response = await agent.RunAsync(
            "Why are SentinelApp and SentinelGate separate Container Apps?",
            options: CreateIqOptions());

        Assert.Contains("separates the browser-facing UI plane", response.Text, StringComparison.Ordinal);
        Assert.Contains("Sources: docs/DECISIONS.md", response.Text, StringComparison.Ordinal);
        Assert.Empty(inner.Options);
    }

    [Fact]
    public async Task RunStreamingAsync_ArchitectureQuestionRequiresIqTool()
    {
        var inner = new CapturingAgent();
        var agent = new KnowledgeGroundingAgent(inner);

        await foreach (var _ in agent.RunStreamingAsync(
            "Explain the backend Host and TLS/SNI design.",
            options: CreateIqOptions()))
        {
        }

        Assert.Empty(inner.Options);
    }

    [Fact]
    public async Task RunAsync_LiveGatewayQuestionPreservesExistingOptions()
    {
        var inner = new CapturingAgent();
        var agent = new KnowledgeGroundingAgent(inner);
        var original = new ChatClientAgentRunOptions(new ChatOptions
        {
            ToolMode = ChatToolMode.Auto,
            Tools = [CreateIqFunction()],
        });

        await agent.RunAsync(
            "Inspect the live gateway JWT validation configuration.",
            options: original);

        Assert.Single(inner.Options);
        Assert.Same(original, inner.Options[0]);
        var routed = Assert.IsType<ChatClientAgentRunOptions>(inner.Options[0]);
        Assert.Same(ChatToolMode.Auto, routed.ChatOptions?.ToolMode);
    }

    [Fact]
    public async Task RunAsync_GroundingDoesNotMutateCallerOptions()
    {
        var inner = new CapturingAgent();
        var agent = new KnowledgeGroundingAgent(inner);
        var original = new ChatClientAgentRunOptions(new ChatOptions
        {
            ToolMode = ChatToolMode.Auto,
            Tools = [CreateIqFunction()],
        });

        await agent.RunAsync(
            "Explain the protected listener trust boundary.",
            options: original);

        Assert.Same(ChatToolMode.Auto, original.ChatOptions?.ToolMode);
        Assert.Empty(inner.Options);
    }

    [Fact]
    public async Task RunAsync_MissingIqEvidenceFailsClosedWithoutSynthesis()
    {
        var inner = new CapturingAgent();
        var agent = new KnowledgeGroundingAgent(inner);

        var response = await agent.RunAsync(
            "What Host header reaches SentinelGate?",
            options: CreateIqOptions("Unrelated evidence."));

        Assert.Contains("can't provide a grounded answer", response.Text, StringComparison.Ordinal);
        Assert.Empty(inner.Options);
    }

    [Fact]
    public async Task RunAsync_MissingIqToolFailsClosedWithoutCallingModel()
    {
        var inner = new CapturingAgent();
        var agent = new KnowledgeGroundingAgent(inner);

        var response = await agent.RunAsync("What Host header reaches SentinelGate?");

        Assert.Contains("can't provide a grounded answer", response.Text, StringComparison.Ordinal);
        Assert.Empty(inner.Options);
    }

    private static ChatClientAgentRunOptions CreateIqOptions(string? content = null) =>
        new(new ChatOptions { Tools = [CreateIqFunction(content)] });

    private static AIFunction CreateIqFunction(string? content = null) =>
        AIFunctionFactory.Create(
            (string[] queries) => $$"""
                {"content":[{"type":"text","text":"[{\"title\":\"docs/DECISIONS.md\",\"content\":\"{{content ?? "SentinelApp and SentinelGate are separate backends; the protected listener uses JWT Deny. Keep pickHostNameFromBackendAddress = true so the ACA FQDN is the backend Host and TLS/SNI name. x-original-host is routing context, not authentication."}}\"}]"}]}
                """,
            KnowledgeGroundingPolicy.RequiredToolName);

    private sealed class CapturingAgent : AIAgent
    {
        public List<AgentRunOptions?> Options { get; } = [];

        protected override string IdCore => "capturing-agent";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            Options.Add(options);
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "inner-response")));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Options.Add(options);
            var response = new AgentResponse(new ChatMessage(ChatRole.Assistant, "inner-response"));
            foreach (var update in response.ToAgentResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        private sealed class TestAgentSession : AgentSession;
    }
}
