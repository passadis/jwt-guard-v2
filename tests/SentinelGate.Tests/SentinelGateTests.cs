using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SentinelGate.Tests;

public sealed class SentinelGateTests : IClassFixture<SentinelGateFactory>
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly HttpClient _client;

    public SentinelGateTests(SentinelGateFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Healthz_IsPublicAndHealthy()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SentinelGate", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_RejectsUiHostnameAsUnexpectedRoutingContext()
    {
        using var request = EnterRequest("sentinel.example.test", $"{TenantId:D}:{ObjectId:D}");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("unexpected_routing_context", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_RejectsMissingOriginalHostAsUnexpectedRoutingContext()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enter");
        request.Headers.TryAddWithoutValidation(
            "x-msft-entra-identity",
            $"{TenantId:D}:{ObjectId:D}");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("unexpected_routing_context", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("sentinel-api.example.test:443")]
    [InlineData("sentinel-api.example.test.attacker.invalid")]
    public async Task Enter_RejectsNonExactProtectedHostAsUnexpectedRoutingContext(string host)
    {
        using var request = EnterRequest(host, $"{TenantId:D}:{ObjectId:D}");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("unexpected_routing_context", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_MatchingOriginalHostDoesNotReplaceInjectedIdentity()
    {
        using var request = EnterRequest("sentinel-api.example.test", null);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("missing_identity", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_RejectsEmptyIdentityHeader()
    {
        using var request = EnterRequest("sentinel-api.example.test", string.Empty);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("missing_identity", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_RejectsDuplicateIdentityHeaders()
    {
        using var request = EnterRequest("sentinel-api.example.test", null);
        request.Headers.TryAddWithoutValidation(
            "x-msft-entra-identity",
            [$"{TenantId:D}:{ObjectId:D}", $"{TenantId:D}:{ObjectId:D}"]);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("missing_identity", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not-a-guid:also-not-a-guid")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222:extra")]
    [InlineData("{11111111-1111-1111-1111-111111111111}:22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111111111111111111111111111:22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111:{22222222-2222-2222-2222-222222222222}")]
    [InlineData("00000000-0000-0000-0000-000000000000:22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111:00000000-0000-0000-0000-000000000000")]
    public async Task Enter_RejectsMalformedIdentity(string identity)
    {
        using var request = EnterRequest("sentinel-api.example.test", identity);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("malformed_identity", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_RejectsUnexpectedTenant()
    {
        using var request = EnterRequest(
            "sentinel-api.example.test",
            $"33333333-3333-3333-3333-333333333333:{ObjectId:D}");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("unexpected_tenant", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enter_AcceptsCanonicalExpectedIdentity()
    {
        using var request = EnterRequest("sentinel-api.example.test", $"{TenantId:D}:{ObjectId:D}");
        var response = await _client.SendAsync(request);
        var result = await response.Content.ReadFromJsonAsync<GateEntryResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal("SentinelGate", result.Service);
        Assert.True(result.Allowed);
        Assert.True(result.GatewayValidated);
        Assert.True(result.RoutingContextConsistent);
        Assert.Equal(TenantId, result.TenantId);
        Assert.Equal(ObjectId, result.ObjectId);
    }

    private static HttpRequestMessage EnterRequest(string host, string? identity)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enter");
        request.Headers.Host = "sentinel-gate.internal.example";
        request.Headers.TryAddWithoutValidation("x-original-host", host);
        if (identity is not null)
        {
            request.Headers.TryAddWithoutValidation("x-msft-entra-identity", identity);
        }
        return request;
    }
}

public sealed class SentinelGateFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("PROTECTED_HOST", "sentinel-api.example.test")
            .UseSetting("EXPECTED_TENANT_ID", "11111111-1111-1111-1111-111111111111");
}
