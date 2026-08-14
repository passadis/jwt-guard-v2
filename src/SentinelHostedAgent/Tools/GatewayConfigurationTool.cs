using System.ComponentModel;
using System.Text.Json.Nodes;
using Azure.Core;
using SentinelHostedAgent.Configuration;

namespace SentinelHostedAgent.Tools;

public sealed class GatewayConfigurationTool(
    HostedAgentOptions options,
    HttpClient httpClient,
    TokenCredential credential)
{
    [Description("Reads the live Application Gateway JWT policies and routing rules with ARM API 2025-05-01 and verifies the protected-listener rule attachment.")]
    public async Task<GatewayConfigurationEvidence> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var armToken = await credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://management.azure.com{options.GatewayResourceId}?api-version=2025-05-01");
            request.Headers.Authorization = new("Bearer", armToken.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GatewayConfigurationEvidence.Unavailable($"ARM returned HTTP {(int)response.StatusCode}.");
            }

            var root = JsonNode.Parse(await ReadBoundedAsync(response.Content, cancellationToken));
            var properties = root?["properties"];
            if (properties is null)
            {
                return GatewayConfigurationEvidence.Unavailable("ARM returned no gateway properties.");
            }

            var policies = ReadPolicies(properties);
            var listeners = ReadListeners(properties);
            var rules = ReadRules(properties, listeners);
            var protectedRule = rules.FirstOrDefault(rule =>
                    string.Equals(rule.ListenerHost, options.ProtectedHost, StringComparison.OrdinalIgnoreCase))
                ?? rules.FirstOrDefault(rule => rule.JwtPolicy is not null);
            var protectedPolicy = protectedRule?.JwtPolicy is null
                ? null
                : policies.FirstOrDefault(policy =>
                    string.Equals(policy.Name, protectedRule.JwtPolicy, StringComparison.Ordinal));

            return new GatewayConfigurationEvidence(
                true,
                null,
                root?["name"]?.GetValue<string>(),
                properties["provisioningState"]?.GetValue<string>(),
                options.ProtectedHost,
                protectedRule?.Name,
                protectedRule?.Listener,
                protectedRule?.JwtPolicy is not null && protectedPolicy is not null,
                protectedPolicy,
                policies,
                rules,
                "Live ARM evidence. Listener host selection identifies the protected rule; it is not an authentication proof.");
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

    private static IReadOnlyList<GatewayJwtPolicy> ReadPolicies(JsonNode properties) =>
        properties["entraJWTValidationConfigs"]?.AsArray()
            .Select(node => new GatewayJwtPolicy(
                node?["name"]?.GetValue<string>() ?? "(unnamed)",
                node?["properties"]?["tenantId"]?.GetValue<string>() ?? string.Empty,
                node?["properties"]?["clientId"]?.GetValue<string>() ?? string.Empty,
                node?["properties"]?["audiences"]?.AsArray()
                    .Select(audience => audience?.GetValue<string>() ?? string.Empty)
                    .Where(audience => audience.Length > 0)
                    .ToArray() ?? [],
                node?["properties"]?["unAuthorizedRequestAction"]?.GetValue<string>() ?? string.Empty))
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
                var policy = LastSegment(node?["properties"]?["entraJWTValidationConfig"]?["id"]?.GetValue<string>());
                listeners.TryGetValue(listener ?? string.Empty, out var host);
                return new GatewayRoutingRule(
                    node?["name"]?.GetValue<string>() ?? "(unnamed)",
                    listener,
                    host,
                    policy);
            })
            .ToArray() ?? [];

    private static string? LastSegment(string? resourceId) =>
        resourceId?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidOperationException("ARM response exceeded the accepted size.");
        }
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (target.Length + read > maximumBytes)
            {
                throw new InvalidOperationException("ARM response exceeded the accepted size.");
            }
            target.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(target.ToArray());
    }
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
    IReadOnlyList<GatewayRoutingRule> RoutingRules,
    string EvidenceBoundary)
{
    public static GatewayConfigurationEvidence Unavailable(string error) =>
        new(false, error, null, null, null, null, null, false, null, [], [],
            "No authentication conclusion can be drawn while live configuration is unavailable.");
}
