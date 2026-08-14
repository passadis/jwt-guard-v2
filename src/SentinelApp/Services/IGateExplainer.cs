namespace SentinelApp.Services;

public interface IGateExplainer
{
    IAsyncEnumerable<string> StreamAsync(
        string owner,
        string sessionId,
        string message,
        CancellationToken cancellationToken = default);

    ValueTask ResetSessionAsync(
        string owner,
        string sessionId,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddedGateExplainer : IGateExplainer;

public interface IHostedGateExplainer : IGateExplainer;

public sealed class EmbeddedGateExplainer(AgentService agent) : IEmbeddedGateExplainer
{
    public IAsyncEnumerable<string> StreamAsync(
        string owner,
        string sessionId,
        string message,
        CancellationToken cancellationToken = default) =>
        agent.StreamAsync(owner, sessionId, message, cancellationToken);

    public ValueTask ResetSessionAsync(
        string owner,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        agent.ResetSession(owner, sessionId);
        return ValueTask.CompletedTask;
    }
}
