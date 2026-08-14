using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace SentinelApp.Services;

public sealed class HostedAgentRequirement : IAuthorizationRequirement;

public sealed class HostedAgentAuthorizationHandler(SentinelOptions options)
    : AuthorizationHandler<HostedAgentRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HostedAgentRequirement requirement)
    {
        if (options.HostedAgentPrincipalId is not { } expectedPrincipal ||
            context.User.Identity?.IsAuthenticated != true ||
            SentinelAuthorization.HasAnyDelegatedScope(context.User))
        {
            return Task.CompletedTask;
        }

        var tenant = FindFirst(context.User,
            "tid",
            "http://schemas.microsoft.com/identity/claims/tenantid");
        var objectId = FindFirst(context.User,
            "oid",
            "http://schemas.microsoft.com/identity/claims/objectidentifier");
        var hasRole = context.User.FindAll("roles")
            .Concat(context.User.FindAll(ClaimTypes.Role))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(SentinelAuthorization.AgentScenarioExecuteRole, StringComparer.Ordinal);

        if (Guid.TryParseExact(tenant, "D", out var tenantId) &&
            tenantId == options.TenantGuid &&
            Guid.TryParseExact(objectId, "D", out var principalId) &&
            principalId == expectedPrincipal &&
            hasRole)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }

    private static string? FindFirst(ClaimsPrincipal user, params string[] types) =>
        types.Select(user.FindFirst).FirstOrDefault(claim => claim is not null)?.Value;
}
