namespace SentinelApp.Services;

public sealed class AgentRouter(
    SentinelOptions options,
    IEmbeddedGateExplainer embedded,
    IHostedGateExplainer hosted,
    ILogger<AgentRouter> logger) : IGateExplainer
{
    private readonly SemaphoreSlim _shadowConcurrency = new(2, 2);

    public IAsyncEnumerable<string> StreamAsync(
        string owner,
        string sessionId,
        string message,
        CancellationToken cancellationToken = default) =>
        options.AgentMode switch
        {
            AgentMode.Embedded => embedded.StreamAsync(owner, sessionId, message, cancellationToken),
            AgentMode.Hosted => hosted.StreamAsync(owner, sessionId, message, cancellationToken),
            AgentMode.HostedShadow => StreamShadowAsync(owner, sessionId, message, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported Agent mode."),
        };

    public async ValueTask ResetSessionAsync(
        string owner,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await embedded.ResetSessionAsync(owner, sessionId, cancellationToken);
        await hosted.ResetSessionAsync(owner, sessionId, cancellationToken);
    }

    private async IAsyncEnumerable<string> StreamShadowAsync(
        string owner,
        string sessionId,
        string message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in embedded.StreamAsync(owner, sessionId, message, cancellationToken))
        {
            yield return chunk;
        }

        if (!IsApprovedShadowTester(owner))
        {
            logger.LogInformation("Hosted shadow invocation skipped because the authenticated owner is not in the tester allowlist.");
            yield break;
        }

        if (HostedMessageSafety.IsReadOnlyShadowMessage(message))
        {
            _ = RunShadowAsync(owner, sessionId, message);
        }
        else
        {
            logger.LogInformation("Hosted shadow invocation skipped by the token/side-effect safety policy.");
        }
    }

    private async Task RunShadowAsync(string owner, string sessionId, string message)
    {
        if (!await _shadowConcurrency.WaitAsync(0))
        {
            logger.LogInformation("Hosted shadow comparison skipped because the bounded worker limit is active.");
            return;
        }
        try
        {
            await foreach (var _ in hosted.StreamAsync(
                owner,
                sessionId,
                $"Shadow comparison (read-only): {message}",
                CancellationToken.None))
            {
                // Hosted content is deliberately discarded in shadow mode.
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hosted shadow comparison failed.");
        }
        finally
        {
            _shadowConcurrency.Release();
        }
    }

    private bool IsApprovedShadowTester(string owner)
    {
        var parts = owner.Split(':', StringSplitOptions.None);
        return parts.Length == 2 &&
            Guid.TryParseExact(parts[0], "D", out var tenantId) &&
            tenantId == options.TenantGuid &&
            Guid.TryParseExact(parts[1], "D", out var objectId) &&
            options.HostedShadowTesterObjectIds.Contains(objectId);
    }
}
