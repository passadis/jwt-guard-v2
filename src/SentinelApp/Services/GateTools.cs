using System.ComponentModel;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Monitor.Query;

namespace SentinelApp.Services;

/// <summary>
/// Evidence-producing capabilities exposed through authenticated REST APIs and
/// the Gate Explainer. Pasted and acquired tokens are never returned or stored.
/// </summary>
public sealed class GateTools(
    SentinelOptions options,
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    IGateLogClient logClient)
{
    private static readonly JwtSecurityTokenHandler JwtHandler = new();
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaximumTokenCharacters = 128 * 1024;

    [Description("Decodes a JWT without validating its signature, reads the live protected-listener policy, and predicts the gateway decision from observable claims. The result always distinguishes prediction from verified validation.")]
    public async Task<object> DecodeTokenAsync(
        [Description("The raw JWT to inspect. It is not persisted or returned.")] string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenCharacters)
        {
            return new
            {
                evidenceType = "unknown",
                validFormat = false,
                error = "The token is empty or exceeds the accepted inspection size.",
                signatureVerification = "Not performed.",
            };
        }

        JwtSecurityToken jwt;
        try
        {
            jwt = JwtHandler.ReadJwtToken(token.Trim());
        }
        catch (Exception)
        {
            return new
            {
                evidenceType = "verified_local_decode",
                validFormat = false,
                error = "The value is not a parseable JWT.",
                signatureVerification = "Not performed.",
                prediction = "Predicted deny because the token cannot be decoded.",
            };
        }

        var live = await GetGatewayConfigAsync(cancellationToken);
        if (!live.Available || !live.ProtectedRuleAttached || live.ProtectedPolicy is null)
        {
            return new
            {
                evidenceType = "verified_local_decode_with_unavailable_live_policy",
                validFormat = true,
                signatureVerification = "Not performed. Decoding never proves cryptographic validity.",
                prediction = "Unknown because the live protected rule and JWT policy could not be confirmed.",
                livePolicy = live,
            };
        }

        string? Claim(string type) => jwt.Claims.FirstOrDefault(claim => claim.Type == type)?.Value;
        var policy = live.ProtectedPolicy;
        var now = DateTimeOffset.UtcNow;
        var audiences = jwt.Audiences.ToArray();
        var tenant = Claim("tid");
        var issuer = Claim("iss");
        var objectId = Claim("oid");

        var expectedIssuers = new[]
        {
            $"https://login.microsoftonline.com/{policy.TenantId}/v2.0",
            $"https://sts.windows.net/{policy.TenantId}/",
        };

        var expectedAudiences = new[] { options.ApiAudienceUri, options.ApiClientId };
        var liveTenantMatches = string.Equals(
            policy.TenantId,
            options.TenantId,
            StringComparison.OrdinalIgnoreCase);
        var liveClientMatches = string.Equals(
            policy.ClientId,
            options.ApiClientId,
            StringComparison.OrdinalIgnoreCase);
        var liveAudiencesMatch = expectedAudiences.All(expected =>
            policy.Audiences.Contains(expected, StringComparer.Ordinal));
        var liveDenyEnabled = string.Equals(
            policy.UnauthorizedRequestAction,
            "Deny",
            StringComparison.Ordinal);
        var livePolicyMatchesEnvironment = liveTenantMatches && liveClientMatches &&
            liveAudiencesMatch && liveDenyEnabled;

        var tenantPass = string.Equals(tenant, policy.TenantId, StringComparison.OrdinalIgnoreCase);
        var audiencePass = audiences.Any(actual =>
            policy.Audiences.Contains(actual, StringComparer.Ordinal));
        var issuerPass = issuer is not null &&
            expectedIssuers.Contains(issuer, StringComparer.OrdinalIgnoreCase);
        var objectIdPass = Guid.TryParseExact(objectId, "D", out var parsedObjectId) &&
            parsedObjectId != Guid.Empty;

        var exp = ParseUnixTime(Claim("exp"));
        var nbf = ParseUnixTime(Claim("nbf"));
        var expirationPass = exp is not null && exp.Value >= now.Subtract(ClockSkew);
        var notBeforePass = nbf is null || nbf.Value <= now.Add(ClockSkew);

        var findings = new object[]
        {
            Finding("tenant (tid)", tenant, policy.TenantId, tenantPass, "exact tenant comparison"),
            Finding("audience (aud)", string.Join(", ", audiences), string.Join(" or ", policy.Audiences), audiencePass, "exact audience comparison"),
            Finding("issuer (iss)", issuer, string.Join(" or ", expectedIssuers), issuerPass, "exact known Entra issuer comparison"),
            Finding("expiration (exp)", FormatInstant(exp), $"not expired with {ClockSkew.TotalMinutes:0}-minute clock skew", expirationPass, "decoded claim only"),
            Finding("not before (nbf)", FormatInstant(nbf), $"not in the future beyond {ClockSkew.TotalMinutes:0}-minute clock skew", notBeforePass, "decoded claim only"),
            Finding("object id (oid)", objectId, "non-empty canonical GUID", objectIdPass, "required for injected identity"),
        };

        var claimsPredictPass = tenantPass && audiencePass && issuerPass &&
            expirationPass && notBeforePass && objectIdPass;

        return new
        {
            evidenceType = "verified_decode_plus_live_policy",
            validFormat = true,
            findings,
            livePolicy = new
            {
                live.ProtectedRule,
                live.ProtectedListener,
                policy.TenantId,
                policy.ClientId,
                policy.Audiences,
                policy.UnauthorizedRequestAction,
                environmentComparisons = new object[]
                {
                    Finding("live tenant", policy.TenantId, options.TenantId, liveTenantMatches, "configured environment comparison"),
                    Finding("live API client ID", policy.ClientId, options.ApiClientId, liveClientMatches, "configured environment comparison"),
                    Finding("live accepted audiences", string.Join(" or ", policy.Audiences), string.Join(" or ", expectedAudiences), liveAudiencesMatch, "both required audiences must be present"),
                    Finding("live unauthorized action", policy.UnauthorizedRequestAction, "Deny", liveDenyEnabled, "protected-listener invariant"),
                },
            },
            signatureVerification = "Not performed. Application Gateway verifies the signature against Entra signing keys only during a real request.",
            prediction = !livePolicyMatchesEnvironment
                ? "Unknown because the live JWT policy differs from the configured JWT Sentinel environment."
                : claimsPredictPass
                    ? "Predicted allow if the signature and all gateway-only checks succeed."
                    : "Predicted deny from one or more decoded-claim mismatches.",
            nextStep = "Execute the protected request to obtain an observed gateway and SentinelGate result.",
        };
    }

    [Description("Reads the live Application Gateway JWT policies and routing rules through ARM API 2025-05-01, then confirms whether the protected hostname's rule references its JWT policy.")]
    public async Task<GatewayConfigurationEvidence> GetGatewayConfigAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var armToken = await credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                cancellationToken);
            var http = httpClientFactory.CreateClient("arm");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://management.azure.com{options.GatewayResourceId}?api-version=2025-05-01");
            request.Headers.Authorization = new("Bearer", armToken.Token);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GatewayConfigurationEvidence.Unavailable(
                    $"ARM returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);
            var properties = root?["properties"];
            if (properties is null)
            {
                return GatewayConfigurationEvidence.Unavailable(
                    "ARM returned a response without gateway properties.");
            }

            var policies = ReadPolicies(properties);
            var listeners = ReadListeners(properties);
            var rules = ReadRules(properties, listeners);
            var protectedRule = rules.FirstOrDefault(rule =>
                    string.Equals(rule.ListenerHost, options.GateApiBaseUri.Host, StringComparison.OrdinalIgnoreCase))
                ?? rules.FirstOrDefault(rule => rule.JwtPolicy is not null);
            var protectedPolicy = protectedRule?.JwtPolicy is null
                ? null
                : policies.FirstOrDefault(policy =>
                    string.Equals(policy.Name, protectedRule.JwtPolicy, StringComparison.Ordinal));

            return new GatewayConfigurationEvidence(
                Available: true,
                Error: null,
                Gateway: root?["name"]?.GetValue<string>(),
                ProvisioningState: properties["provisioningState"]?.GetValue<string>(),
                ProtectedHostname: options.GateApiBaseUri.Host,
                ProtectedRule: protectedRule?.Name,
                ProtectedListener: protectedRule?.Listener,
                ProtectedRuleAttached: protectedRule?.JwtPolicy is not null && protectedPolicy is not null,
                ProtectedPolicy: protectedPolicy,
                JwtPolicies: policies,
                RoutingRules: rules);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GatewayConfigurationEvidence.Unavailable(
                $"Live gateway configuration could not be read ({ex.GetType().Name}).");
        }
    }

    [Description("Queries protected-host SentinelGate access logs. Results are restricted to the configured protected hostname and /enter path; ingestion may lag several minutes.")]
    public async Task<object> QueryGateLogsAsync(
        [Description("Minutes of history, from 1 to 1440.")] int minutes = 60,
        [Description("Optional HTTP status code filter.")] int? statusCode = null,
        [Description("Optional exact transaction/correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        minutes = Math.Clamp(minutes, 1, 24 * 60);
        var host = EscapeKql(options.GateApiBaseUri.Host);
        var correlation = string.IsNullOrWhiteSpace(correlationId)
            ? null
            : EscapeKql(correlationId.Trim());
        var filters = new List<string>
        {
            $"| where TimeGenerated > ago({minutes}m)",
            // With pickHostNameFromBackendAddress enabled, Host is the ACA
            // backend FQDN (or empty when the gateway denies before routing).
            // OriginalHost retains the incoming public hostname for telemetry
            // selection only; it is not authentication evidence.
            $"| where OriginalHost =~ '{host}'",
            "| where RequestUri startswith '/enter'",
        };
        if (statusCode is >= 100 and <= 599)
        {
            filters.Add($"| where HttpStatus == {statusCode.Value}");
        }
        if (correlation is not null)
        {
            filters.Add($"| where TransactionId == '{correlation}'");
        }
        filters.Add("| order by TimeGenerated desc");
        filters.Add("| take 40");

        var kql = "AGWAccessLogs\n" + string.Join("\n", filters);
        try
        {
            var rows = await logClient.QueryAsync(
                options.LawWorkspaceGuid,
                kql,
                TimeSpan.FromMinutes(minutes),
                cancellationToken);
            return new
            {
                evidenceType = "live_log_analytics_query",
                protectedHost = options.GateApiBaseUri.Host,
                path = "/enter",
                minutes,
                statusCode,
                correlationId,
                count = rows.Count,
                note = rows.Count == 0
                    ? "No matching protected-gate records are available yet. Log Analytics ingestion may lag several minutes."
                    : "Matching protected-gate records, newest first. Attribute an exact failure only when the returned fields support it.",
                rows,
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
                error = "The protected-gate log query failed.",
                detail = ex.GetType().Name,
            };
        }
    }

    [Description("Sends a real request to SentinelGate through the JWT-protected listener. Scenarios: missing, valid, wrong_audience, tampered, and user_replay when the application layer securely supplies the caller token.")]
    public async Task<object> SimulateAsync(
        [Description("One of: missing, valid, wrong_audience, tampered, user_replay.")] string scenario,
        [Description("Caller token supplied only by the authorized application layer for user_replay.")] string? userToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return new { error = "A simulation scenario is required." };
        }

        scenario = scenario.Trim().ToLowerInvariant().Replace('-', '_');
        string? token;
        string tokenStory;

        try
        {
            switch (scenario)
            {
                case "missing":
                    token = null;
                    tokenStory = "No Authorization header was sent.";
                    break;
                case "valid":
                    token = await AcquireDaemonTokenAsync($"{options.ApiAudienceUri}/.default", cancellationToken);
                    tokenStory = "A daemon token was acquired for the configured API audience.";
                    break;
                case "wrong_audience":
                    token = await AcquireDaemonTokenAsync("https://graph.microsoft.com/.default", cancellationToken);
                    tokenStory = "A genuine Entra token was acquired for Microsoft Graph, not JWT Sentinel.";
                    break;
                case "tampered":
                    token = Tamper(await AcquireDaemonTokenAsync(
                        $"{options.ApiAudienceUri}/.default",
                        cancellationToken));
                    tokenStory = "One payload character was changed after token issuance, invalidating the signature.";
                    break;
                case "user_replay":
                    if (string.IsNullOrWhiteSpace(userToken))
                    {
                        return new
                        {
                            error = "Caller replay requires a token attached by the authenticated application layer. The Agent cannot provide one.",
                        };
                    }
                    token = userToken;
                    tokenStory = "The authenticated caller token was attached by the application layer for this request only.";
                    break;
                default:
                    return new { error = $"Unknown simulation scenario '{scenario}'." };
            }
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
                scenario,
                error = "Token acquisition for the simulation failed.",
                detail = ex.GetType().Name,
            };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(options.GateApiBaseUri, "/enter"));
        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        try
        {
            var client = httpClientFactory.CreateClient("protected-gate");
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            object? backend = null;
            var validSentinelGateResult = false;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<GateSimulationPayload>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                    var tenantId = Guid.Empty;
                    var objectId = Guid.Empty;
                    validSentinelGateResult = parsed is
                    {
                        Service: "SentinelGate",
                        Allowed: true,
                        GatewayValidated: true,
                        RoutingContextConsistent: true,
                    } &&
                    TryParseCanonicalGuid(parsed.TenantId, out tenantId) &&
                    tenantId == options.TenantGuid &&
                    TryParseCanonicalGuid(parsed.ObjectId, out objectId);
                    if (parsed is not null)
                    {
                        backend = new
                        {
                            parsed.Service,
                            parsed.Allowed,
                            parsed.GatewayValidated,
                            parsed.RoutingContextConsistent,
                            tenantId = validSentinelGateResult ? tenantId : (Guid?)null,
                            objectId = validSentinelGateResult ? objectId : (Guid?)null,
                            schemaValid = validSentinelGateResult,
                        };
                    }
                }
                catch (JsonException)
                {
                    // The structured result below records the invalid body.
                }
            }

            return new
            {
                evidenceType = "observed_protected_http_response",
                scenario,
                targetHost = options.GateApiBaseUri.Host,
                targetPath = "/enter",
                tokenStory,
                tokenClaims = token is null ? null : SummarizeToken(token),
                httpStatus = (int)response.StatusCode,
                observedResult = response.IsSuccessStatusCode
                    ? validSentinelGateResult
                        ? "The protected route returned HTTP success with a validated SentinelGate identity payload."
                        : "The protected route returned HTTP success, but the response was not a valid SentinelGate identity payload."
                    : "The protected route returned a denial or error response.",
                backendResult = backend,
                limitation = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "HTTP denial is observed. Backend non-reachability requires matching telemetry."
                    : null,
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new { evidenceType = "transport_failure", scenario, error = "timeout" };
        }
        catch (HttpRequestException ex)
        {
            return new
            {
                evidenceType = "transport_failure",
                scenario,
                error = ex.HttpRequestError.ToString(),
            };
        }
    }

    public Task<object> SimulateForAgentAsync(string scenario, CancellationToken cancellationToken = default) =>
        string.Equals(scenario?.Trim(), "user_replay", StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult<object>(new
            {
                error = "Caller replay must be initiated by the authenticated Enter the Gate flow; the Agent is never given the caller token.",
            })
            : SimulateAsync(scenario ?? string.Empty, null, cancellationToken);

    private async Task<string> AcquireDaemonTokenAsync(string scope, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("entra");
        using var response = await http.PostAsync(
            $"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.DaemonClientId,
                ["client_secret"] = options.DaemonClientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = scope,
            }),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonNode.Parse(body);
        var token = json?["access_token"]?.GetValue<string>();
        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        var errorCode = json?["error"]?.GetValue<string>() ?? "unknown_error";
        throw new InvalidOperationException(
            $"Token acquisition failed with HTTP {(int)response.StatusCode} ({errorCode}).");
    }

    private static IReadOnlyList<GatewayJwtPolicy> ReadPolicies(JsonNode properties) =>
        properties["entraJWTValidationConfigs"]?.AsArray()
            .Select(node => new GatewayJwtPolicy(
                Name: node?["name"]?.GetValue<string>() ?? "(unnamed)",
                TenantId: node?["properties"]?["tenantId"]?.GetValue<string>() ?? string.Empty,
                ClientId: node?["properties"]?["clientId"]?.GetValue<string>() ?? string.Empty,
                Audiences: node?["properties"]?["audiences"]?.AsArray()
                    .Select(audience => audience?.GetValue<string>() ?? string.Empty)
                    .Where(audience => audience.Length > 0)
                    .ToArray() ?? [],
                UnauthorizedRequestAction: node?["properties"]?["unAuthorizedRequestAction"]?.GetValue<string>() ?? string.Empty))
            .ToArray() ?? [];

    private static IReadOnlyDictionary<string, string?> ReadListeners(JsonNode properties) =>
        properties["httpListeners"]?.AsArray()
            .Where(node => node?["name"] is not null)
            .ToDictionary(
                node => node!["name"]!.GetValue<string>(),
                node => node!["properties"]?["hostName"]?.GetValue<string>(),
                StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<GatewayRoutingRule> ReadRules(
        JsonNode properties,
        IReadOnlyDictionary<string, string?> listeners) =>
        properties["requestRoutingRules"]?.AsArray()
            .Select(node =>
            {
                var listener = LastSegment(node?["properties"]?["httpListener"]?["id"]?.GetValue<string>());
                var jwtPolicy = LastSegment(node?["properties"]?["entraJWTValidationConfig"]?["id"]?.GetValue<string>());
                listeners.TryGetValue(listener ?? string.Empty, out var host);
                return new GatewayRoutingRule(
                    Name: node?["name"]?.GetValue<string>() ?? "(unnamed)",
                    Listener: listener,
                    ListenerHost: host,
                    JwtPolicy: jwtPolicy);
            })
            .ToArray() ?? [];

    private static string? LastSegment(string? resourceId) =>
        resourceId?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static object Finding(
        string check,
        string? value,
        string expected,
        bool pass,
        string basis) => new
        {
            check,
            value = value ?? "(absent)",
            expected,
            pass,
            basis,
        };

    private static DateTimeOffset? ParseUnixTime(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string FormatInstant(DateTimeOffset? value) =>
        value?.ToString("u", CultureInfo.InvariantCulture) ?? "(absent)";

    private static string EscapeKql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool TryParseCanonicalGuid(string? value, out Guid result) =>
        Guid.TryParseExact(value, "D", out result) && result != Guid.Empty;

    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var payload = parts[1].ToCharArray();
        var index = payload.Length / 2;
        payload[index] = payload[index] == 'A' ? 'B' : 'A';
        parts[1] = new string(payload);
        return string.Join('.', parts);
    }

    private static object SummarizeToken(string token)
    {
        try
        {
            var jwt = JwtHandler.ReadJwtToken(token);
            string? Claim(string type) => jwt.Claims.FirstOrDefault(claim => claim.Type == type)?.Value;
            return new
            {
                aud = jwt.Audiences.ToArray(),
                tid = Claim("tid"),
                oid = Claim("oid"),
                appid = Claim("appid") ?? Claim("azp"),
                exp = Claim("exp"),
                nbf = Claim("nbf"),
            };
        }
        catch
        {
            return new { note = "The token is not parseable after tampering." };
        }
    }

    private sealed record GateSimulationPayload(
        string? Service,
        bool Allowed,
        bool GatewayValidated,
        bool RoutingContextConsistent,
        string? TenantId,
        string? ObjectId);
}

public sealed record GatewayJwtPolicy(
    string Name,
    string TenantId,
    string ClientId,
    IReadOnlyList<string> Audiences,
    string UnauthorizedRequestAction);

public sealed record GatewayRoutingRule(
    string Name,
    string? Listener,
    string? ListenerHost,
    string? JwtPolicy);

public sealed record GatewayConfigurationEvidence(
    bool Available,
    string? Error,
    string? Gateway,
    string? ProvisioningState,
    string? ProtectedHostname,
    string? ProtectedRule,
    string? ProtectedListener,
    bool ProtectedRuleAttached,
    GatewayJwtPolicy? ProtectedPolicy,
    IReadOnlyList<GatewayJwtPolicy> JwtPolicies,
    IReadOnlyList<GatewayRoutingRule> RoutingRules)
{
    public static GatewayConfigurationEvidence Unavailable(string error) =>
        new(false, error, null, null, null, null, null, false, null, [], []);
}

public interface IGateLogClient
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string workspaceId,
        string kql,
        TimeSpan timeRange,
        CancellationToken cancellationToken);
}

public sealed class AzureGateLogClient(TokenCredential credential) : IGateLogClient
{
    private readonly LogsQueryClient _client = new(credential);

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string workspaceId,
        string kql,
        TimeSpan timeRange,
        CancellationToken cancellationToken)
    {
        var result = await _client.QueryWorkspaceAsync(
            workspaceId,
            kql,
            new QueryTimeRange(timeRange),
            cancellationToken: cancellationToken);
        var table = result.Value.Table;
        return table.Rows.Select(row =>
                (IReadOnlyDictionary<string, string?>)table.Columns
                    .Select((column, index) => (column.Name, Value: row[index]?.ToString()))
                    .Where(item => !string.IsNullOrEmpty(item.Value))
                    .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }
}
