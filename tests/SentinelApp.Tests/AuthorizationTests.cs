using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class AuthorizationTests : IClassFixture<SentinelAppFactory>
{
    private readonly HttpClient _client;

    public AuthorizationTests(SentinelAppFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task UserApi_RejectsMissingAuthentication()
    {
        var response = await _client.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UserApi_RejectsMissingScope()
    {
        using var request = AuthenticatedRequest(scope: null);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UserApi_RejectsWrongScope()
    {
        using var request = AuthenticatedRequest("other_scope");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UserApi_AcceptsAccessAsUserScope()
    {
        using var request = AuthenticatedRequest("openid access_as_user profile");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UserApi_RejectsAppOnlyRoleToken()
    {
        using var request = AuthenticatedRequest(scope: null, role: "Gateway.Simulate");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Broker_RejectsMissingAuthentication()
    {
        var response = await _client.GetAsync(
            "/api/agent/broker/decode/33333333-3333-3333-3333-333333333333");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Broker_RejectsDelegatedUserEvenWithAppRole()
    {
        using var request = BrokerRequest("access_as_user", SentinelAuthorization.AgentScenarioExecuteRole);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Broker_RejectsAnyDelegatedScopeEvenWithAppRole()
    {
        using var request = BrokerRequest("other_scope", SentinelAuthorization.AgentScenarioExecuteRole);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Broker_RequiresExactAppRole()
    {
        using var request = BrokerRequest(scope: null, role: "other-role");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Broker_AcceptsConfiguredAppIdentityAndRole()
    {
        using var request = BrokerRequest(scope: null, SentinelAuthorization.AgentScenarioExecuteRole);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Broker_AcceptsFrameworkMappedAppRoleClaim()
    {
        using var request = BrokerRequest(scope: null, role: null);
        request.Headers.Add("X-Test-Mapped-Role", SentinelAuthorization.AgentScenarioExecuteRole);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Broker_RejectsWrongPrincipalWithCorrectRole()
    {
        using var request = BrokerRequest(scope: null, SentinelAuthorization.AgentScenarioExecuteRole);
        request.Headers.Add("X-Test-Oid", "99999999-9999-9999-9999-999999999999");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BrokerSimulationRejectsCallerControlledScenarioAndTarget()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/broker/simulate")
        {
            Content = new StringContent(
                """{"scenario":"https://evil.example/run","target":"https://evil.example"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Role", SentinelAuthorization.AgentScenarioExecuteRole);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalDecodeQueuesEvidenceWithoutReturningRawTokenOrHandle()
    {
        const string rawValue = "this-is-not-a-jwt-and-must-not-be-returned";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/evidence/decode")
        {
            Content = new StringContent(
                $$"""{"sessionId":"33333333-3333-3333-3333-333333333333","token":"{{rawValue}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Scope", SentinelAuthorization.AccessAsUserScope);
        var response = await _client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(rawValue, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("handle", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queuedForAgent", responseBody, StringComparison.Ordinal);
    }

    private static HttpRequestMessage AuthenticatedRequest(string? scope, string? role = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("X-Test-Authenticated", "true");
        if (scope is not null)
        {
            request.Headers.Add("X-Test-Scope", scope);
        }
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }
        return request;
    }

    private static HttpRequestMessage BrokerRequest(string? scope, string? role)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/agent/broker/decode/33333333-3333-3333-3333-333333333333");
        request.Headers.Add("X-Test-Authenticated", "true");
        if (scope is not null)
        {
            request.Headers.Add("X-Test-Scope", scope);
        }
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }
        return request;
    }
}

public sealed class SentinelAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseSetting("TENANT_ID", "11111111-1111-1111-1111-111111111111")
            .UseSetting("API_CLIENT_ID", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            .UseSetting("SPA_CLIENT_ID", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
            .UseSetting("DAEMON_CLIENT_ID", "cccccccc-cccc-cccc-cccc-cccccccccccc")
            .UseSetting("DAEMON_CLIENT_SECRET", "test-only-not-a-real-secret")
            .UseSetting("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/")
            .UseSetting("MODEL_DEPLOYMENT", "test-model")
            .UseSetting("GATEWAY_RESOURCE_ID", "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.Network/applicationGateways/agw-test")
            .UseSetting("LAW_WORKSPACE_GUID", "dddddddd-dddd-dddd-dddd-dddddddddddd")
            .UseSetting("GATE_API_BASE", "https://sentinel-api.example.test")
            .UseSetting("HOSTED_AGENT_PRINCIPAL_ID", "22222222-2222-2222-2222-222222222222")
            .ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            });
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-Authenticated"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("tid", "11111111-1111-1111-1111-111111111111"),
            new("oid", Request.Headers.TryGetValue("X-Test-Oid", out var oid)
                ? oid.ToString()
                : "22222222-2222-2222-2222-222222222222"),
            new("name", "Test User"),
        };
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope))
        {
            claims.Add(new Claim("scp", scope.ToString()));
        }
        if (Request.Headers.TryGetValue("X-Test-Role", out var role))
        {
            claims.Add(new Claim("roles", role.ToString()));
        }
        if (Request.Headers.TryGetValue("X-Test-Mapped-Role", out var mappedRole))
        {
            claims.Add(new Claim(ClaimTypes.Role, mappedRole.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
