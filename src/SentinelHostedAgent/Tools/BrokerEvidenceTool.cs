using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using SentinelHostedAgent.Configuration;

namespace SentinelHostedAgent.Tools;

public sealed class BrokerEvidenceTool(
    HostedAgentOptions options,
    HttpClient httpClient,
    TokenCredential credential)
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly HashSet<string> AllowedScenarios =
        ["missing", "valid", "wrong_audience", "tampered"];

    [Description("Resolves a short-lived opaque evidence handle through the fixed SentinelApp broker and returns sanitized token findings. Raw JWTs are never accepted.")]
    public Task<object> DecodeAsync(
        [Description("Canonical GUID evidence handle issued by SentinelApp for the authenticated owner and session.")] string evidenceHandle,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(evidenceHandle, "D", out var handle) ||
            handle == Guid.Empty ||
            !string.Equals(handle.ToString("D"), evidenceHandle, StringComparison.Ordinal))
        {
            return Task.FromResult<object>(new
            {
                evidenceType = "unknown",
                error = "A non-empty canonical GUID evidence handle is required. Raw tokens are not accepted.",
            });
        }

        return SendAsync(HttpMethod.Get, $"api/agent/broker/decode/{handle:D}", null, cancellationToken);
    }

    [Description("Runs one allowlisted JWT Sentinel scenario through the fixed SentinelApp broker. User replay, raw tokens, arbitrary URLs, paths, schemes, and headers are forbidden.")]
    public Task<object> SimulateAsync(
        [Description("Exactly one of missing, valid, wrong_audience, or tampered.")] string scenario,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedScenarios.Contains(scenario ?? string.Empty))
        {
            return Task.FromResult<object>(new
            {
                evidenceType = "unknown",
                error = "Scenario must be exactly one of: missing, valid, wrong_audience, tampered. Caller replay remains in SentinelApp.",
            });
        }

        return SendAsync(
            HttpMethod.Post,
            "api/agent/broker/simulate",
            JsonContent.Create(new { scenario }),
            cancellationToken);
    }

    private async Task<object> SendAsync(
        HttpMethod method,
        string fixedRelativePath,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (options.BrokerOrigin is null)
        {
            content?.Dispose();
            return new
            {
                evidenceType = "unavailable_broker",
                error = "The SentinelApp evidence broker is not configured during independent hosted-agent validation.",
            };
        }

        var target = new Uri(options.BrokerOrigin, fixedRelativePath);
        if (!string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(target.IdnHost, options.BrokerOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            target.Port != options.BrokerOrigin.Port)
        {
            content?.Dispose();
            return new { evidenceType = "unknown", error = "The fixed broker target failed origin validation." };
        }

        try
        {
            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext([$"api://{options.ApiClientId:D}/.default"]),
                cancellationToken);
            using var request = new HttpRequestMessage(method, target) { Content = content };
            request.Headers.Authorization = new("Bearer", accessToken.Token);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await ReadBoundedAsync(response.Content, cancellationToken);
            JsonNode? evidence = null;
            try
            {
                evidence = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                // The bounded result below records a malformed broker response.
            }

            return new
            {
                evidenceType = "sentinelapp_broker_response",
                httpStatus = (int)response.StatusCode,
                schema = evidence is null ? "invalid_json" : "json",
                evidence,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new
            {
                evidenceType = "unknown",
                error = "The fixed SentinelApp evidence broker request failed.",
                detail = ex.GetType().Name,
            };
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidOperationException("Broker response exceeded the accepted size.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (target.Length + read > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Broker response exceeded the accepted size.");
            }
            target.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(target.ToArray());
    }
}
