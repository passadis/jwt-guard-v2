using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SentinelHostedAgent.Security;

public sealed class CallerTokenReplayGuardAgent(AIAgent innerAgent) : DelegatingAIAgent(innerAgent)
{
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        if (CallerTokenReplayPolicy.ShouldRefuse(messages))
        {
            return Task.FromResult(CreateRefusalResponse());
        }

        return base.RunCoreAsync(messages, session, options, cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (CallerTokenReplayPolicy.ShouldRefuse(messages))
        {
            foreach (var update in CreateRefusalResponse().ToAgentResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    private static AgentResponse CreateRefusalResponse() =>
        new(new ChatMessage(ChatRole.Assistant, CallerTokenReplayPolicy.Refusal));
}
