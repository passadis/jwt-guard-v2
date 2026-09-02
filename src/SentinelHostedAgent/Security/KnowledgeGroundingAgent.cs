using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SentinelHostedAgent.Security;

public sealed class KnowledgeGroundingAgent(AIAgent innerAgent) : DelegatingAIAgent(innerAgent)
{
    private const string GroundingFailure =
        "I couldn't obtain adequate evidence from the approved Foundry IQ corpus, so I can't provide a grounded answer.";

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (!KnowledgeGroundingPolicy.ShouldRequireIq(messageList))
        {
            return await base.RunCoreAsync(messageList, session, options, cancellationToken);
        }

        return await RetrieveAndRenderAsync(LatestUserText(messageList), options, cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (!KnowledgeGroundingPolicy.ShouldRequireIq(messageList))
        {
            await foreach (var update in base.RunCoreStreamingAsync(messageList, session, options, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        var response = await RetrieveAndRenderAsync(
            LatestUserText(messageList),
            options,
            cancellationToken);
        foreach (var update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }

    private static string LatestUserText(IEnumerable<ChatMessage> messages) =>
        messages.Last(message => message.Role == ChatRole.User).Text ?? string.Empty;

    private static async Task<AgentResponse> RetrieveAndRenderAsync(
        string prompt,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var iqFunction = (options as ChatClientAgentRunOptions)?.ChatOptions?.Tools?
            .OfType<AIFunction>()
            .SingleOrDefault(tool => string.Equals(
                tool.Name,
                KnowledgeGroundingPolicy.RequiredToolName,
                StringComparison.Ordinal));
        if (iqFunction is null)
        {
            return FailureResponse();
        }

        var query = prompt.Trim();
        if (query.Length > 1_000)
        {
            query = query[..1_000];
        }

        var arguments = new AIFunctionArguments
        {
            ["queries"] = new[] { query },
        };

        object? result;
        try
        {
            result = await iqFunction.InvokeAsync(arguments, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return FailureResponse();
        }

        var callId = $"iq_{Guid.NewGuid():N}";
        var evidenceMessages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [
                new FunctionCallContent(
                    callId,
                    KnowledgeGroundingPolicy.RequiredToolName,
                    arguments)
            ]),
            new(ChatRole.Tool, [new FunctionResultContent(callId, result)]),
        };

        var groundedAnswer = KnowledgeGroundedAnswer.Create(prompt, evidenceMessages);
        if (groundedAnswer is null)
        {
            return FailureResponse();
        }

        evidenceMessages.Add(new ChatMessage(ChatRole.Assistant, groundedAnswer));
        return new AgentResponse(evidenceMessages);
    }

    private static AgentResponse FailureResponse() =>
        new(new ChatMessage(ChatRole.Assistant, GroundingFailure));
}
