using System.Collections.Concurrent;
using Azure.Core;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace SentinelApp.Services;

/// <summary>
/// The "Gate Explainer" — a Microsoft Agent Framework agent backed by a
/// Foundry model deployment, armed with the four gateway tools.
/// </summary>
public sealed class AgentService
{
    private const string Instructions = """
        You are the Gate Explainer, the assistant inside JWT Sentinel — a demo app
        protected by Azure Application Gateway's JWT validation (preview) feature.

        Architecture:
        - The UI hostname routes only to SentinelApp. SentinelApp owns SPA sign-in,
          user authorization, this Agent, live config/log tools, and simulations.
        - The protected hostname routes only to SentinelGate. Its Application
          Gateway rule has Entra JWT validation with action Deny.
        - SentinelGate accepts /enter only on the protected hostname and only with
          a canonical x-msft-entra-identity tenantId:objectId header for the
          configured tenant.
        - Application Gateway keeps the SentinelGate ACA FQDN as the backend Host
          and TLS/SNI name. x-original-host is client-originated routing context;
          a match is not authentication or proof of JWT validation.
        - SentinelApp can forward the authenticated caller token through the
          protected hostname using the Enter the Gate BFF flow. You never receive
          or request that caller token.

        Your job: help users understand and explore this. Use your tools:
        - decode_token reads live policy and compares decoded claims. Decoding is
          not signature validation and produces a prediction, not a verified allow.
        - get_gateway_config reads the actual JWT policy and protected-rule attachment.
        - query_gate_logs reads only protected-host /enter traffic. Mention ingestion
          delay when a request has not appeared.
        - simulate_gate_request runs missing, valid, wrong_audience, and tampered
          scenarios. Caller replay belongs to the authenticated Enter the Gate flow.

        Every diagnostic response must distinguish:
        1. Verified evidence: live config, decoded values, observed HTTP response,
           SentinelGate payload, log record, or injected identity.
        2. Inference: what the evidence predicts or suggests.
        3. Unknowns: evidence that is unavailable, delayed, or not present.

        Prefer this concise structure when useful: Decision; Observed result; Token
        findings; Live policy; Backend result; Evidence used; Limitations; Conclusion.

        Never invent an exact gateway failure reason, a missing log record, backend
        non-reachability, or signature validation. An HTTP 401/403 alone does not
        prove that SentinelGate was untouched; matching telemetry is required. Never
        repeat complete access tokens, secrets, credentials, or tool arguments that
        contain them. If evidence is insufficient, say so plainly and offer the next
        safe observation.
        """;

    private readonly AIAgent _agent;
    private readonly ConcurrentDictionary<SessionKey, SessionEntry> _sessions = new();
    private readonly SemaphoreSlim _sessionCreationLock = new(1, 1);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private const int MaximumSessions = 250;

    public AgentService(SentinelOptions opts, GateTools tools, TokenCredential credential)
    {
        var client = new AzureOpenAIClient(new Uri(opts.OpenAIEndpoint), credential);

        _agent = client.GetChatClient(opts.ModelDeployment).AsAIAgent(
            instructions: Instructions,
            name: "GateExplainer",
            tools:
            [
                AIFunctionFactory.Create(tools.DecodeTokenAsync, "decode_token"),
                AIFunctionFactory.Create(tools.GetGatewayConfigAsync, "get_gateway_config"),
                AIFunctionFactory.Create(tools.QueryGateLogsAsync, "query_gate_logs"),
                AIFunctionFactory.Create(tools.SimulateForAgentAsync, "simulate_gate_request"),
            ]);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string owner,
        string sessionId,
        string message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) ||
            !Guid.TryParseExact(sessionId, "D", out _) ||
            string.IsNullOrWhiteSpace(message) ||
            message.Length > 16_000)
        {
            throw new ArgumentException("A valid owner, session ID, and bounded message are required.");
        }

        var key = new SessionKey(owner, sessionId);
        var entry = await GetOrCreateSessionAsync(key, ct);
        entry.LastAccessUtc = DateTimeOffset.UtcNow;
        await entry.Lock.WaitAsync(ct);
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(
                message,
                entry.Session,
                cancellationToken: ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return update.Text;
                }
            }
        }
        finally
        {
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            entry.Lock.Release();
        }
    }

    public void ResetSession(string owner, string sessionId) =>
        _sessions.TryRemove(new SessionKey(owner, sessionId), out _);

    private async Task<SessionEntry> GetOrCreateSessionAsync(SessionKey key, CancellationToken ct)
    {
        RemoveExpiredSessions();
        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        await _sessionCreationLock.WaitAsync(ct);
        try
        {
            RemoveExpiredSessions();
            if (_sessions.TryGetValue(key, out existing))
            {
                return existing;
            }
            if (_sessions.Count >= MaximumSessions)
            {
                throw new InvalidOperationException("The in-memory Agent session limit has been reached.");
            }

            var session = await _agent.CreateSessionAsync(ct);
            return _sessions.GetOrAdd(key, _ => new SessionEntry(session));
        }
        finally
        {
            _sessionCreationLock.Release();
        }
    }

    private void RemoveExpiredSessions()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(SessionLifetime);
        foreach (var pair in _sessions)
        {
            if (pair.Value.LastAccessUtc < cutoff)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record SessionKey(string Owner, string SessionId);

    private sealed class SessionEntry(AgentSession session)
    {
        public AgentSession Session { get; } = session;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
