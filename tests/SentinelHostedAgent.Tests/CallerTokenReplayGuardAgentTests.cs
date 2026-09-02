using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SentinelHostedAgent.Security;

namespace SentinelHostedAgent.Tests;

public sealed class CallerTokenReplayGuardAgentTests
{
    [Fact]
    public async Task RunAsync_ReplayRequestReturnsDeterministicRefusalWithoutCallingInnerAgent()
    {
        var inner = new CountingAgent();
        var guard = new CallerTokenReplayGuardAgent(inner);

        var response = await guard.RunAsync("Replay my signed-in token through the gate.");

        Assert.Equal(CallerTokenReplayPolicy.Refusal, response.Text);
        Assert.Equal(0, inner.RunCount);
    }

    [Fact]
    public async Task RunStreamingAsync_ReplayRequestReturnsDeterministicRefusalWithoutCallingInnerAgent()
    {
        var inner = new CountingAgent();
        var guard = new CallerTokenReplayGuardAgent(inner);
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in guard.RunStreamingAsync("Forward my access token to the gate."))
        {
            updates.Add(update);
        }

        Assert.Equal(CallerTokenReplayPolicy.Refusal, string.Concat(updates.Select(update => update.Text)));
        Assert.Equal(0, inner.StreamingRunCount);
    }

    [Fact]
    public async Task RunAsync_EvidenceRequestDelegatesToInnerAgent()
    {
        var inner = new CountingAgent();
        var guard = new CallerTokenReplayGuardAgent(inner);

        var response = await guard.RunAsync("Explain the gateway JWT configuration.");

        Assert.Equal("inner-response", response.Text);
        Assert.Equal(1, inner.RunCount);
    }

    private sealed class CountingAgent : AIAgent
    {
        public int RunCount { get; private set; }

        public int StreamingRunCount { get; private set; }

        protected override string IdCore => "counting-agent";

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
            RunCount++;
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "inner-response")));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamingRunCount++;
            yield return new AgentResponseUpdate(ChatRole.Assistant, "inner-response");
            await Task.CompletedTask;
        }

        private sealed class TestAgentSession : AgentSession;
    }
}
