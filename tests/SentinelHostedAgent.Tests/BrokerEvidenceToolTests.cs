using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using SentinelHostedAgent.Tools;

namespace SentinelHostedAgent.Tests;

public sealed class BrokerEvidenceToolTests
{
    [Theory]
    [InlineData("user_replay")]
    [InlineData("valid ")]
    [InlineData("VALID")]
    [InlineData("https://evil.example")]
    public async Task SimulationRejectsAnythingOutsideCanonicalAllowlist(string scenario)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);

        var result = await tool.SimulateAsync(scenario);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("exactly one of", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("8AD40006-A261-46D2-BC79-362CD6A42256")]
    [InlineData("eyJhbGciOiJub25lIn0.e30.")]
    public async Task DecodeRejectsEmptyMalformedAndNoncanonicalHandles(string handle)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);

        var result = await tool.DecodeAsync(handle);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("Raw tokens are not accepted", json, StringComparison.Ordinal);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task ValidScenarioUsesOnlyConfiguredOriginAndFixedPath()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);

        await tool.SimulateAsync("valid");

        Assert.Equal(new Uri("https://guard.mvps.gr/api/agent/broker/simulate"), handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("managed-identity-token", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task DecodeUsesOnlyConfiguredOriginAndCanonicalHandlePath()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);

        await tool.DecodeAsync("22222222-2222-2222-2222-222222222222");

        Assert.Equal(
            new Uri("https://guard.mvps.gr/api/agent/broker/decode/22222222-2222-2222-2222-222222222222"),
            handler.RequestUri);
    }

    private static BrokerEvidenceTool CreateTool(RecordingHandler handler) => new(
        GatewayLogToolTests.CreateOptions(new Uri("https://guard.mvps.gr/")),
        new HttpClient(handler),
        new FakeCredential());

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("managed-identity-token", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"evidenceType\":\"fixture\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
