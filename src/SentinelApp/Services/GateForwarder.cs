using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SentinelApp.Services;

public sealed class GateForwarder(
    SentinelOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<GateForwarder> logger)
{
    private const int MaximumResponseCharacters = 64 * 1024;
    private readonly Uri _protectedEnterUri = CreateProtectedEnterUri(options.GateApiBaseUri);

    public async Task<GateForwardResult> EnterAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return GateForwardResult.Failure(
                StatusCodes.Status401Unauthorized,
                "missing_caller_token",
                "The authenticated request did not contain a reusable bearer token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _protectedEnterUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            var client = httpClientFactory.CreateClient("protected-gate");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > MaximumResponseCharacters)
            {
                return GateForwardResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "invalid_backend_response",
                    "The protected backend response exceeded the accepted size.",
                    observedHttpStatus: (int)response.StatusCode);
            }

            var gatePayload = TryReadGatePayload(body);
            if (response.IsSuccessStatusCode)
            {
                if (gatePayload is not
                    {
                        Service: "SentinelGate",
                        Allowed: true,
                        GatewayValidated: true,
                        RoutingContextConsistent: true,
                    } ||
                    !TryParseCanonicalGuid(gatePayload.TenantId, out var tenantId) ||
                    tenantId != options.TenantGuid ||
                    !TryParseCanonicalGuid(gatePayload.ObjectId, out var objectId))
                {
                    return GateForwardResult.Failure(
                        StatusCodes.Status502BadGateway,
                        "invalid_backend_response",
                        "The protected backend did not return a valid SentinelGate identity result.",
                        observedHttpStatus: (int)response.StatusCode);
                }

                return new GateForwardResult(
                    StatusCodes.Status200OK,
                    "allowed",
                    true,
                    gatePayload.Message ?? "You are in",
                    true,
                    true,
                    tenantId,
                    objectId,
                    (int)response.StatusCode,
                    "Observed HTTP 200 and a validated SentinelGate response.",
                    null);
            }

            if (gatePayload?.Service == "SentinelGate")
            {
                return GateForwardResult.Failure(
                    (int)response.StatusCode,
                    "sentinel_gate_rejected",
                    gatePayload.Message ?? "SentinelGate rejected the forwarded identity.",
                    observedHttpStatus: (int)response.StatusCode,
                    evidence: "Observed a structured SentinelGate rejection response.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return GateForwardResult.Failure(
                    (int)response.StatusCode,
                    "gateway_denied",
                    "The protected listener denied the request.",
                    observedHttpStatus: (int)response.StatusCode,
                    evidence: $"Observed HTTP {(int)response.StatusCode} from the protected hostname.",
                    limitation: "Backend non-reachability requires matching gateway telemetry; HTTP status alone is not sufficient proof.");
            }

            if ((int)response.StatusCode >= 500)
            {
                return GateForwardResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "upstream_failure",
                    "The protected route returned an upstream failure.",
                    observedHttpStatus: (int)response.StatusCode,
                    evidence: $"Observed HTTP {(int)response.StatusCode} from the protected hostname.",
                    limitation: "Without matching telemetry this cannot be attributed precisely to Application Gateway or SentinelGate.");
            }

            return GateForwardResult.Failure(
                StatusCodes.Status502BadGateway,
                "unexpected_http_response",
                "The protected route returned an unexpected HTTP response.",
                observedHttpStatus: (int)response.StatusCode);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Protected gate entry failed with a timeout.");
            return GateForwardResult.Failure(
                StatusCodes.Status504GatewayTimeout,
                "timeout",
                "The protected route did not respond before the timeout.");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            logger.LogWarning("Protected gate entry failed during DNS resolution.");
            return GateForwardResult.Failure(
                StatusCodes.Status502BadGateway,
                "dns_failure",
                "The protected hostname could not be resolved.");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            logger.LogWarning("Protected gate entry failed during TLS negotiation.");
            return GateForwardResult.Failure(
                StatusCodes.Status502BadGateway,
                "tls_failure",
                "TLS validation for the protected hostname failed.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Protected gate entry failed during connection or HTTP processing.");
            return GateForwardResult.Failure(
                StatusCodes.Status502BadGateway,
                "connection_failure",
                "The protected hostname could not be reached.");
        }
    }

    private static GatePayload? TryReadGatePayload(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<GatePayload>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri CreateProtectedEnterUri(Uri configuredOrigin)
    {
        if (!configuredOrigin.IsAbsoluteUri ||
            configuredOrigin.Scheme != Uri.UriSchemeHttps ||
            Uri.CheckHostName(configuredOrigin.Host) != UriHostNameType.Dns ||
            !configuredOrigin.IsDefaultPort ||
            configuredOrigin.UserInfo.Length > 0 ||
            configuredOrigin.AbsolutePath != "/" ||
            configuredOrigin.Query.Length > 0 ||
            configuredOrigin.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                "The protected gate target must be a standard-port HTTPS DNS origin.");
        }

        return new Uri(configuredOrigin, "/enter");
    }

    private static bool TryParseCanonicalGuid(string? value, out Guid result) =>
        Guid.TryParseExact(value, "D", out result) && result != Guid.Empty;

    private sealed record GatePayload(
        string? Service,
        bool Allowed,
        string? Message,
        bool GatewayValidated,
        bool RoutingContextConsistent,
        string? TenantId,
        string? ObjectId,
        string? ErrorCode);
}

public sealed record GateForwardResult(
    int HttpStatus,
    string Classification,
    bool Allowed,
    string Message,
    bool GatewayValidated,
    bool RoutingContextConsistent,
    Guid? TenantId,
    Guid? ObjectId,
    int? ObservedHttpStatus,
    string? Evidence,
    string? Limitation)
{
    public static GateForwardResult Failure(
        int status,
        string classification,
        string message,
        int? observedHttpStatus = null,
        string? evidence = null,
        string? limitation = null) =>
        new(
            status,
            classification,
            false,
            message,
            false,
            false,
            null,
            null,
            observedHttpStatus,
            evidence,
            limitation);
}

public static class BearerTokenReader
{
    public static bool TryRead(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        token = header.Parameter;
        return true;
    }
}
