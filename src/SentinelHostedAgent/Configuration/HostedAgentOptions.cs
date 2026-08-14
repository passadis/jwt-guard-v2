namespace SentinelHostedAgent.Configuration;

public sealed record HostedAgentOptions(
    Uri ProjectEndpoint,
    string ModelDeployment,
    string GatewayResourceId,
    string LawWorkspaceGuid,
    string ProtectedHost,
    Guid TenantId,
    Guid ApiClientId,
    Uri? BrokerOrigin,
    string? ToolboxName)
{
    public static HostedAgentOptions FromEnvironment() => FromValues(
        Required("FOUNDRY_PROJECT_ENDPOINT"),
        Required("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
        Required("GATEWAY_RESOURCE_ID"),
        Required("LAW_WORKSPACE_GUID"),
        Required("PROTECTED_HOST"),
        Required("TENANT_ID"),
        Required("API_CLIENT_ID"),
        Environment.GetEnvironmentVariable("BROKER_BASE_URI"),
        Environment.GetEnvironmentVariable("TOOLBOX_NAME"));

    public static HostedAgentOptions FromValues(
        string projectEndpoint,
        string modelDeployment,
        string gatewayResourceId,
        string lawWorkspaceGuid,
        string protectedHost,
        string tenantId,
        string apiClientId,
        string? brokerBaseUri,
        string? toolboxName)
    {
        var projectUri = RequireHttpsUri(projectEndpoint, "FOUNDRY_PROJECT_ENDPOINT", allowPath: true);
        if (!projectUri.AbsolutePath.Contains("/api/projects/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT must identify a Foundry project endpoint.");
        }

        modelDeployment = RequireBoundedValue(modelDeployment, "AZURE_AI_MODEL_DEPLOYMENT_NAME", 64);
        gatewayResourceId = RequireBoundedValue(gatewayResourceId, "GATEWAY_RESOURCE_ID", 1024);
        if (!gatewayResourceId.StartsWith("/subscriptions/", StringComparison.Ordinal) ||
            !gatewayResourceId.Contains("/providers/Microsoft.Network/applicationGateways/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GATEWAY_RESOURCE_ID must be an Application Gateway ARM resource ID.");
        }

        if (!Guid.TryParseExact(lawWorkspaceGuid, "D", out var workspaceId) || workspaceId == Guid.Empty)
        {
            throw new InvalidOperationException("LAW_WORKSPACE_GUID must be a non-empty canonical GUID.");
        }

        protectedHost = RequireBoundedValue(protectedHost, "PROTECTED_HOST", 253);
        if (protectedHost.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')) ||
            protectedHost.StartsWith('.') || protectedHost.EndsWith('.') || !protectedHost.Contains('.'))
        {
            throw new InvalidOperationException("PROTECTED_HOST must be a DNS hostname without a scheme, port, path, query, or fragment.");
        }

        if (!Guid.TryParseExact(tenantId, "D", out var tenantGuid) || tenantGuid == Guid.Empty)
        {
            throw new InvalidOperationException("TENANT_ID must be a non-empty canonical GUID.");
        }
        if (!Guid.TryParseExact(apiClientId, "D", out var apiClientGuid) || apiClientGuid == Guid.Empty)
        {
            throw new InvalidOperationException("API_CLIENT_ID must be a non-empty canonical GUID.");
        }

        Uri? brokerOrigin = null;
        if (!string.IsNullOrWhiteSpace(brokerBaseUri))
        {
            brokerOrigin = RequireHttpsUri(brokerBaseUri.Trim(), "BROKER_BASE_URI", allowPath: false);
        }

        toolboxName = string.IsNullOrWhiteSpace(toolboxName)
            ? null
            : RequireBoundedValue(toolboxName, "TOOLBOX_NAME", 64);

        return new HostedAgentOptions(
            projectUri,
            modelDeployment,
            gatewayResourceId,
            workspaceId.ToString("D"),
            protectedHost.ToLowerInvariant(),
            tenantGuid,
            apiClientGuid,
            brokerOrigin,
            toolboxName);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{name} is required.");

    private static string RequireBoundedValue(string value, string name, int maximumLength)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{name} is empty, too long, or contains control characters.");
        }
        return value;
    }

    private static Uri RequireHttpsUri(string value, string name, bool allowPath)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            uri.IsDefaultPort is false ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!allowPath && uri.AbsolutePath != "/"))
        {
            throw new InvalidOperationException($"{name} must be a standard HTTPS URI with no credentials, port, query, or fragment.");
        }
        return allowPath ? uri : new Uri($"https://{uri.IdnHost}/", UriKind.Absolute);
    }
}
