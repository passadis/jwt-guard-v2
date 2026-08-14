using System.Security.Claims;

namespace SentinelApp.Services;

public static class SentinelAuthorization
{
    public const string SpaUserPolicy = "spa-user-access";
    public const string HostedAgentPolicy = "hosted-agent-broker";
    public const string AccessAsUserScope = "access_as_user";
    public const string AgentScenarioExecuteRole = "agent.scenario.execute";

    private static readonly string[] ScopeClaimTypes =
    [
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope",
    ];

    public static bool HasDelegatedScope(ClaimsPrincipal user, string requiredScope) =>
        user.Identity?.IsAuthenticated == true &&
        ScopeClaimTypes
            .SelectMany(type => user.FindAll(type))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);

    public static bool HasAnyDelegatedScope(ClaimsPrincipal user) =>
        ScopeClaimTypes
            .SelectMany(type => user.FindAll(type))
            .Any(claim => !string.IsNullOrWhiteSpace(claim.Value));

    public static string? GetSessionOwner(ClaimsPrincipal user)
    {
        var tenant = FindFirst(user,
            "tid",
            "http://schemas.microsoft.com/identity/claims/tenantid");
        var objectId = FindFirst(user,
            "oid",
            "http://schemas.microsoft.com/identity/claims/objectidentifier");

        return Guid.TryParseExact(tenant, "D", out var tenantId) &&
               Guid.TryParseExact(objectId, "D", out var oid)
            ? $"{tenantId:D}:{oid:D}"
            : null;
    }

    private static string? FindFirst(ClaimsPrincipal user, params string[] types) =>
        types.Select(user.FindFirst).FirstOrDefault(claim => claim is not null)?.Value;
}
