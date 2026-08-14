using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;

namespace SentinelApp.Services;

public sealed class HostedGateExplainer(
    SentinelOptions options,
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    BrokerEvidenceStore evidenceStore,
    ILogger<HostedGateExplainer> logger) : IHostedGateExplainer
{
    private const string FoundryScope = "https://ai.azure.com/.default";
    private const int MaximumSessions = 250;
    private const int MaximumSseLineCharacters = 1024 * 1024;
    private const int MaximumErrorCharacters = 64 * 1024;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<SessionKey, SessionEntry> _sessions = new();
    private readonly SemaphoreSlim _sessionCreationLock = new(1, 1);

    public async IAsyncEnumerable<string> StreamAsync(
        string owner,
        string sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(owner, sessionId, message);
        if (options.HostedAgentResponsesEndpoint is null || options.HostedAgentVersion is null)
        {
            yield return "The Hosted Agent is not configured. The operator can switch the application back to Embedded mode.";
            yield break;
        }
        if (HostedMessageSafety.ContainsJwtLikeMaterial(message))
        {
            yield return "Do not paste access tokens into hosted chat. Use JWT Sentinel's local token-analysis flow so only sanitized evidence is shared.";
            yield break;
        }

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _ = ProduceAsync(owner, sessionId, message, channel.Writer, cancellationToken);
        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }

    private async Task ProduceAsync(
        string owner,
        string sessionId,
        string message,
        ChannelWriter<string> writer,
        CancellationToken callerCancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        timeout.CancelAfter(options.HostedAgentTimeout);
        var ct = timeout.Token;
        var key = new SessionKey(owner, sessionId, options.HostedAgentVersion!.Value);
        var hasPendingEvidence = evidenceStore.TryGetPendingHandle(owner, sessionId, out var handle);
        var safeMessage = hasPendingEvidence
            ? $"{message}\n\nServer context: sanitized token evidence is available under handle {handle:D}. Use decode_token with exactly this handle; never request or repeat a raw token."
            : message;
        var mayRetryWithFreshSession = !hasPendingEvidence &&
            HostedMessageSafety.IsReadOnlyShadowMessage(message);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await InvokeAttemptAsync(key, safeMessage, writer, callerCancellationToken, ct);
            if (result.Succeeded)
            {
                logger.LogInformation(
                    "Hosted Agent invocation completed. Mode={AgentMode} Version={AgentVersion} CorrelationId={CorrelationId} DurationMs={DurationMs} RetryClass={RetryClass} Outcome=success",
                    options.AgentMode,
                    options.HostedAgentVersion,
                    result.Entry?.LastCorrelationId,
                    stopwatch.ElapsedMilliseconds,
                    attempt == 0 ? "none" : "fresh-session");
                writer.TryComplete();
                return;
            }

            var exception = result.Exception!;
            if (exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested)
            {
                writer.TryComplete(new OperationCanceledException(callerCancellationToken));
                return;
            }

            EvictFailedSession(key, result.Entry);
            if (attempt == 0 &&
                mayRetryWithFreshSession &&
                !result.EmittedText &&
                exception is HostedProtocolException)
            {
                logger.LogWarning(
                    exception,
                    "Hosted Agent invocation will retry with a fresh session. Mode={AgentMode} Version={AgentVersion} CorrelationId={CorrelationId} DurationMs={DurationMs} RetryClass=fresh-session Outcome={Outcome}",
                    options.AgentMode,
                    options.HostedAgentVersion,
                    result.Entry?.LastCorrelationId,
                    stopwatch.ElapsedMilliseconds,
                    exception.GetType().Name);
                continue;
            }

            logger.LogWarning(
                exception,
                "Hosted Agent invocation failed. Mode={AgentMode} Version={AgentVersion} CorrelationId={CorrelationId} DurationMs={DurationMs} RetryClass={RetryClass} Outcome={Outcome}",
                options.AgentMode,
                options.HostedAgentVersion,
                result.Entry?.LastCorrelationId,
                stopwatch.ElapsedMilliseconds,
                attempt == 0 ? "none" : "fresh-session-exhausted",
                exception.GetType().Name);
            await writer.WriteAsync(
                "The Hosted Agent request failed or returned an invalid response. No embedded fallback was selected automatically.",
                callerCancellationToken);
            writer.TryComplete();
            return;
        }
    }

    private async Task<HostedAttemptResult> InvokeAttemptAsync(
        SessionKey key,
        string message,
        ChannelWriter<string> writer,
        CancellationToken callerCancellationToken,
        CancellationToken ct)
    {
        SessionEntry? entry = null;
        var lockHeld = false;
        var emittedText = false;
        try
        {
            entry = await GetOrCreateSessionAsync(key, ct);
            await entry.Lock.WaitAsync(ct);
            lockHeld = true;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            await foreach (var chunk in InvokeResponsesAsync(entry, message, ct))
            {
                emittedText = true;
                await writer.WriteAsync(chunk, callerCancellationToken);
            }
            return HostedAttemptResult.Success(entry, emittedText);
        }
        catch (Exception ex)
        {
            return HostedAttemptResult.Failure(entry, emittedText, ex);
        }
        finally
        {
            if (entry is not null && lockHeld)
            {
                entry.LastAccessUtc = DateTimeOffset.UtcNow;
                entry.Lock.Release();
            }
        }
    }

    private void EvictFailedSession(SessionKey key, SessionEntry? failedEntry)
    {
        if (failedEntry is not null &&
            _sessions.TryGetValue(key, out var current) &&
            ReferenceEquals(current, failedEntry))
        {
            _sessions.TryRemove(key, out _);
        }
        evidenceStore.RemoveSession(key.Owner, key.BrowserSessionId);
    }

    public ValueTask ResetSessionAsync(
        string owner,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.HostedAgentVersion is not null)
        {
            _sessions.TryRemove(
                new SessionKey(owner, sessionId, options.HostedAgentVersion.Value),
                out _);
        }
        evidenceStore.RemoveSession(owner, sessionId);
        return ValueTask.CompletedTask;
    }

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
                throw new InvalidOperationException("The in-memory Hosted Agent session limit has been reached.");
            }

            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext([FoundryScope]),
                ct);
            var userIdentity = PseudonymousOwner(key.Owner);
            var session = await CreateVersionedSessionAsync(accessToken.Token, key.Version, userIdentity, ct);
            var conversation = await CreateConversationAsync(accessToken.Token, userIdentity, ct);
            return _sessions.GetOrAdd(key, _ => new SessionEntry(session, conversation, userIdentity));
        }
        finally
        {
            _sessionCreationLock.Release();
        }
    }

    private async Task<string> CreateVersionedSessionAsync(
        string bearerToken,
        int version,
        string userIdentity,
        CancellationToken ct)
    {
        var endpoints = HostedEndpoints.FromResponsesEndpoint(options.HostedAgentResponsesEndpoint!);
        using var request = NewRequest(
            HttpMethod.Post,
            endpoints.Sessions,
            bearerToken,
            JsonSerializer.Serialize(new
            {
                version_indicator = new
                {
                    type = "version_ref",
                    agent_version = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            }));
        AddUserIdentity(request, userIdentity);
        using var response = await SendAsync(request, ct);
        var body = await ReadBoundedAsync(response, MaximumErrorCharacters, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HostedProtocolException($"Session creation returned HTTP {(int)response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var sessionId = RequiredString(root, "agent_session_id");
        var indicator = root.GetProperty("version_indicator");
        var actualVersion = RequiredString(indicator, "agent_version");
        var indicatorType = RequiredString(indicator, "type");
        if (indicatorType != "version_ref" ||
            actualVersion != version.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new HostedProtocolException("Foundry created a session for an unexpected Agent version.");
        }
        return BoundedOpaqueId(sessionId, "session");
    }

    private async Task<string> CreateConversationAsync(
        string bearerToken,
        string userIdentity,
        CancellationToken ct)
    {
        var endpoint = HostedEndpoints.FromResponsesEndpoint(options.HostedAgentResponsesEndpoint!).Conversations;
        using var request = NewRequest(HttpMethod.Post, endpoint, bearerToken, "{}");
        AddUserIdentity(request, userIdentity);
        using var response = await SendAsync(request, ct);
        var body = await ReadBoundedAsync(response, MaximumErrorCharacters, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HostedProtocolException($"Conversation creation returned HTTP {(int)response.StatusCode}.");
        }
        using var json = JsonDocument.Parse(body);
        return BoundedOpaqueId(RequiredString(json.RootElement, "id"), "conversation");
    }

    private async IAsyncEnumerable<string> InvokeResponsesAsync(
        SessionEntry entry,
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext([FoundryScope]),
            ct);
        using var request = NewRequest(
            HttpMethod.Post,
            options.HostedAgentResponsesEndpoint!,
            accessToken.Token,
            JsonSerializer.Serialize(new
            {
                input = message,
                stream = true,
                agent_session_id = entry.SessionId,
                conversation = new { id = entry.ConversationId },
            }));
        AddUserIdentity(request, entry.UserIdentity);

        using var response = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        entry.LastCorrelationId = FirstHeaderValue(response, "x-request-id") ??
            FirstHeaderValue(response, "apim-request-id");
        if (!response.IsSuccessStatusCode)
        {
            _ = await ReadBoundedAsync(response, MaximumErrorCharacters, ct);
            throw new HostedProtocolException($"Responses endpoint returned HTTP {(int)response.StatusCode}.");
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            throw new HostedProtocolException("Responses endpoint returned an unexpected content type.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        string? eventName = null;
        var completed = false;
        var emittedText = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > MaximumSseLineCharacters)
            {
                throw new HostedProtocolException("Responses stream exceeded the event-size limit.");
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line[7..];
                continue;
            }
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[6..];
            using var json = JsonDocument.Parse(data);
            var dataEventName = json.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
            if (eventName is not null && dataEventName is not null && eventName != dataEventName)
            {
                throw new HostedProtocolException("Responses stream event header and data type did not match.");
            }
            var effectiveEventName = dataEventName ?? eventName;
            eventName = null;

            if (effectiveEventName == "response.output_text.delta")
            {
                if (json.RootElement.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.String &&
                    delta.GetString() is { Length: > 0 } text)
                {
                    emittedText = true;
                    yield return text;
                }
            }
            else if (effectiveEventName == "response.completed")
            {
                if (json.RootElement.TryGetProperty("response", out var completedResponse) &&
                    completedResponse.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.String &&
                    status.GetString() != "completed")
                {
                    throw new HostedProtocolException("Hosted Agent reported a non-completed response.");
                }
                if (!emittedText &&
                    json.RootElement.TryGetProperty("response", out completedResponse))
                {
                    foreach (var text in ExtractCompletedText(completedResponse))
                    {
                        emittedText = true;
                        yield return text;
                    }
                }
                completed = true;
            }
            else if (effectiveEventName is "response.failed" or "response.incomplete" or "error")
            {
                throw new HostedProtocolException($"Hosted Agent returned terminal event {effectiveEventName}.");
            }
        }

        if (!completed)
        {
            throw new HostedProtocolException("Responses stream ended without a completion event.");
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, Uri endpoint, string bearerToken, string json)
    {
        var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", bearerToken);
        return request;
    }

    private static void AddUserIdentity(HttpRequestMessage request, string userIdentity)
    {
        if (!request.Headers.TryAddWithoutValidation("x-ms-user-identity", userIdentity))
        {
            throw new HostedProtocolException("Hosted request could not apply its delegated-user scope.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        => await httpClientFactory.CreateClient("hosted-agent")
            .SendAsync(request, completion, ct);

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumCharacters,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximumCharacters)
        {
            throw new HostedProtocolException("Hosted Agent response exceeded the size limit.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096];
        var body = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                return body.ToString();
            }
            if (body.Length + read > maximumCharacters)
            {
                throw new HostedProtocolException("Hosted Agent response exceeded the size limit.");
            }
            body.Append(buffer, 0, read);
        }
    }

    private static IEnumerable<string> ExtractCompletedText(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String &&
                    text.GetString() is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
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
                evidenceStore.RemoveSession(pair.Key.Owner, pair.Key.BrowserSessionId);
            }
        }
    }

    private static void ValidateRequest(string owner, string sessionId, string message)
    {
        if (string.IsNullOrWhiteSpace(owner) ||
            !Guid.TryParseExact(sessionId, "D", out var browserSession) ||
            browserSession == Guid.Empty ||
            string.IsNullOrWhiteSpace(message) ||
            message.Length > 16_000)
        {
            throw new ArgumentException("A valid owner, session ID, and bounded message are required.");
        }
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : throw new HostedProtocolException($"Hosted response omitted {property}.");

    private static string BoundedOpaqueId(string value, string kind) =>
        value.Length is > 0 and <= 512 && !value.Any(char.IsControl)
            ? value
            : throw new HostedProtocolException($"Hosted {kind} identifier was invalid.");

    private static string PseudonymousOwner(string owner) =>
        $"usr_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))).ToLowerInvariant()}";

    private static string? FirstHeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.SelectMany(value => value.Split(','))
                .Select(value => value.Trim())
                .FirstOrDefault(value => value.Length is > 0 and <= 256)
            : null;

    private sealed record SessionKey(string Owner, string BrowserSessionId, int Version);

    private sealed class SessionEntry(string sessionId, string conversationId, string userIdentity)
    {
        public string SessionId { get; } = sessionId;
        public string ConversationId { get; } = conversationId;
        public string UserIdentity { get; } = userIdentity;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
        public string? LastCorrelationId { get; set; }
    }

    private sealed record HostedAttemptResult(
        bool Succeeded,
        bool EmittedText,
        SessionEntry? Entry,
        Exception? Exception)
    {
        public static HostedAttemptResult Success(SessionEntry entry, bool emittedText) =>
            new(true, emittedText, entry, null);

        public static HostedAttemptResult Failure(SessionEntry? entry, bool emittedText, Exception exception) =>
            new(false, emittedText, entry, exception);
    }

    private sealed class HostedProtocolException(string message) : Exception(message);

    private sealed record HostedEndpoints(Uri Sessions, Uri Conversations)
    {
        public static HostedEndpoints FromResponsesEndpoint(Uri endpoint)
        {
            var segments = endpoint.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var project = segments[2];
            var agent = segments[4];
            var origin = endpoint.GetLeftPart(UriPartial.Authority);
            var projectBase = $"{origin}/api/projects/{project}";
            return new(
                new Uri($"{projectBase}/agents/{agent}/endpoint/sessions?api-version=v1", UriKind.Absolute),
                new Uri($"{projectBase}/agents/{agent}/endpoint/protocols/openai/conversations?api-version=v1", UriKind.Absolute));
        }
    }
}

internal static class HostedMessageSafety
{
    private static readonly string[] ScenarioTerms =
    [
        "simulate", "run scenario", "missing token", "wrong audience",
        "wrong_audience", "tampered", "user replay", "user_replay",
    ];

    public static bool ContainsJwtLikeMaterial(string message) =>
        message.Split([' ', '\r', '\n', '\t', '"', '\'', '<', '>', '(', ')', '[', ']', '{', '}', ','],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(IsJwtLike);

    public static bool IsReadOnlyShadowMessage(string message) =>
        !ContainsJwtLikeMaterial(message) &&
        !ScenarioTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsJwtLike(string value)
    {
        var trimmed = value.TrimEnd('.', ';', ':');
        var parts = trimmed.Split('.');
        return parts.Length == 3 && parts.All(part =>
            part.Length >= 8 && part.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }
}
