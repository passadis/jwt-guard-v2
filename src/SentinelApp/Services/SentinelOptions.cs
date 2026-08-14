namespace SentinelApp.Services;

public sealed record SentinelOptions
{
    public required string TenantId { get; init; }
    public required string ApiClientId { get; init; }
    public required string SpaClientId { get; init; }
    public required string DaemonClientId { get; init; }
    public required string DaemonClientSecret { get; init; }
    public required string OpenAIEndpoint { get; init; }
    public required string ModelDeployment { get; init; }
    public required string GatewayResourceId { get; init; }
    public required string LawWorkspaceGuid { get; init; }
    public required string GateApiBase { get; init; }
    public AgentMode AgentMode { get; init; } = AgentMode.Embedded;
    public Uri? HostedAgentResponsesEndpoint { get; init; }
    public int? HostedAgentVersion { get; init; }
    public TimeSpan HostedAgentTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public Guid? HostedAgentPrincipalId { get; init; }
    public IReadOnlySet<Guid> HostedShadowTesterObjectIds { get; init; } = new HashSet<Guid>();

    public string ApiAudienceUri => $"api://{ApiClientId}";
    public Uri GateApiBaseUri => new(GateApiBase, UriKind.Absolute);
    public Guid TenantGuid => Guid.ParseExact(TenantId, "D");

    public static SentinelOptions FromConfiguration(IConfiguration configuration)
    {
        var tenantId = Required(configuration, "TENANT_ID");
        if (!Guid.TryParseExact(tenantId, "D", out var tenantGuid) || tenantGuid == Guid.Empty)
        {
            throw new InvalidOperationException("TENANT_ID must be a non-empty canonical GUID.");
        }

        var gateApiBase = Required(configuration, "GATE_API_BASE").TrimEnd('/');
        if (!Uri.TryCreate(gateApiBase, UriKind.Absolute, out var gateUri) ||
            gateUri.Scheme != Uri.UriSchemeHttps ||
            Uri.CheckHostName(gateUri.Host) != UriHostNameType.Dns ||
            !gateUri.IsDefaultPort ||
            gateUri.UserInfo.Length > 0 ||
            gateUri.AbsolutePath != "/" ||
            gateUri.Query.Length > 0 ||
            gateUri.Fragment.Length > 0)
        {
            throw new InvalidOperationException("GATE_API_BASE must be a standard-port HTTPS DNS origin without credentials, a path, query, or fragment.");
        }

        var agentMode = ParseAgentMode(configuration["AGENT_MODE"]);
        var hostedEndpoint = ParseHostedEndpoint(configuration["HOSTED_AGENT_RESPONSES_ENDPOINT"]);
        var hostedVersion = ParsePositiveInt(configuration["HOSTED_AGENT_VERSION"], "HOSTED_AGENT_VERSION");
        var hostedTimeout = ParseTimeout(configuration["HOSTED_AGENT_TIMEOUT_SECONDS"]);
        var hostedShadowTesters = ParseGuidSet(
            configuration["HOSTED_SHADOW_TESTER_OBJECT_IDS"],
            "HOSTED_SHADOW_TESTER_OBJECT_IDS");
        if (agentMode is not AgentMode.Embedded && (hostedEndpoint is null || hostedVersion is null))
        {
            throw new InvalidOperationException(
                "HOSTED_AGENT_RESPONSES_ENDPOINT and HOSTED_AGENT_VERSION are required when AGENT_MODE is Hosted or HostedShadow.");
        }
        if (agentMode == AgentMode.HostedShadow && hostedShadowTesters.Count == 0)
        {
            throw new InvalidOperationException(
                "HOSTED_SHADOW_TESTER_OBJECT_IDS must contain at least one canonical object ID when AGENT_MODE is HostedShadow.");
        }

        var hostedPrincipal = ParseOptionalGuid(
            configuration["HOSTED_AGENT_PRINCIPAL_ID"],
            "HOSTED_AGENT_PRINCIPAL_ID");

        return new SentinelOptions
        {
            TenantId = tenantId,
            ApiClientId = Required(configuration, "API_CLIENT_ID"),
            SpaClientId = Required(configuration, "SPA_CLIENT_ID"),
            DaemonClientId = Required(configuration, "DAEMON_CLIENT_ID"),
            DaemonClientSecret = Required(configuration, "DAEMON_CLIENT_SECRET"),
            OpenAIEndpoint = Required(configuration, "AZURE_OPENAI_ENDPOINT"),
            ModelDeployment = Required(configuration, "MODEL_DEPLOYMENT"),
            GatewayResourceId = Required(configuration, "GATEWAY_RESOURCE_ID"),
            LawWorkspaceGuid = Required(configuration, "LAW_WORKSPACE_GUID"),
            GateApiBase = gateApiBase,
            AgentMode = agentMode,
            HostedAgentResponsesEndpoint = hostedEndpoint,
            HostedAgentVersion = hostedVersion,
            HostedAgentTimeout = hostedTimeout,
            HostedAgentPrincipalId = hostedPrincipal,
            HostedShadowTesterObjectIds = hostedShadowTesters,
        };
    }

    private static AgentMode ParseAgentMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AgentMode.Embedded;
        }
        var trimmed = value.Trim();
        if (Enum.GetNames<AgentMode>().Any(name =>
                string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse<AgentMode>(trimmed, ignoreCase: true, out var mode))
        {
            return mode;
        }
        throw new InvalidOperationException(
            "AGENT_MODE must be Embedded, HostedShadow, or Hosted.");
    }

    private static Uri? ParseHostedEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0 ||
            Uri.CheckHostName(uri.Host) != UriHostNameType.Dns ||
            !uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "HOSTED_AGENT_RESPONSES_ENDPOINT must be a standard-port Azure AI Services HTTPS endpoint.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var validPath = segments.Length == 9 &&
            segments[0] == "api" && segments[1] == "projects" &&
            IsSafePathSegment(segments[2]) && segments[3] == "agents" &&
            IsSafePathSegment(segments[4]) && segments[5] == "endpoint" &&
            segments[6] == "protocols" && segments[7] == "openai" &&
            segments[8] == "responses";
        if (!validPath || !string.Equals(uri.Query, "?api-version=v1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "HOSTED_AGENT_RESPONSES_ENDPOINT must use the exact reviewed /api/projects/{project}/agents/{agent}/endpoint/protocols/openai/responses?api-version=v1 path.");
        }

        return uri;
    }

    private static bool IsSafePathSegment(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static int? ParsePositiveInt(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0 ||
            value != parsed.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"{name} must be a canonical positive integer.");
        }
        return parsed;
    }

    private static TimeSpan ParseTimeout(string? value)
    {
        var seconds = ParsePositiveInt(value, "HOSTED_AGENT_TIMEOUT_SECONDS") ?? 75;
        if (seconds is < 15 or > 90)
        {
            throw new InvalidOperationException(
                "HOSTED_AGENT_TIMEOUT_SECONDS must be between 15 and 90.");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static Guid? ParseOptionalGuid(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new InvalidOperationException($"{name} must be a non-empty canonical GUID.");
        }
        return parsed;
    }

    private static IReadOnlySet<Guid> ParseGuidSet(string? value, string name)
    {
        var result = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (item.Length == 0 ||
                !Guid.TryParseExact(item, "D", out var parsed) ||
                parsed == Guid.Empty ||
                item != parsed.ToString("D") ||
                !result.Add(parsed))
            {
                throw new InvalidOperationException(
                    $"{name} must be a comma-separated set of unique lowercase canonical non-empty GUIDs.");
            }
        }
        return result;
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration[name]
        ?? throw new InvalidOperationException($"Missing environment variable {name}");
}

public enum AgentMode
{
    Embedded,
    HostedShadow,
    Hosted,
}
