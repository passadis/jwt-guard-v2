using System.Net;
using Microsoft.Extensions.Logging;
using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class GateForwarderTests
{
    private const string CallerToken = "test-caller-token-never-log-this";

    [Fact]
    public async Task ForwardsCallerTokenOnlyToConfiguredProtectedEndpoint()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("sentinel-api.example.test", request.RequestUri!.Host);
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri.Scheme);
            Assert.True(request.RequestUri.IsDefaultPort);
            Assert.Equal("/enter", request.RequestUri.AbsolutePath);
            Assert.Empty(request.RequestUri.Query);
            Assert.Empty(request.RequestUri.UserInfo);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(CallerToken, request.Headers.Authorization?.Parameter);
            return GateResponse(HttpStatusCode.OK);
        });
        var logger = new CollectingLogger<GateForwarder>();
        var result = await Create(handler, logger).EnterAsync(CallerToken, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("allowed", result.Classification);
        Assert.True(result.GatewayValidated);
        Assert.True(result.RoutingContextConsistent);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(CallerToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreservesGateway401AsObservedDenial()
    {
        var result = await Create(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)))
            .EnterAsync(CallerToken, CancellationToken.None);

        Assert.Equal(401, result.HttpStatus);
        Assert.Equal("gateway_denied", result.Classification);
        Assert.Contains("telemetry", result.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "dns_failure")]
    [InlineData(HttpRequestError.SecureConnectionError, "tls_failure")]
    [InlineData(HttpRequestError.ConnectionError, "connection_failure")]
    public async Task DistinguishesTransportFailures(HttpRequestError error, string expected)
    {
        var handler = new StubHandler(_ =>
            throw new HttpRequestException(error, "safe test failure"));
        var result = await Create(handler).EnterAsync(CallerToken, CancellationToken.None);
        Assert.Equal(expected, result.Classification);
        Assert.DoesNotContain(CallerToken, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinguishesTimeout()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("test timeout"));
        var result = await Create(handler).EnterAsync(CallerToken, CancellationToken.None);
        Assert.Equal("timeout", result.Classification);
        Assert.Equal(504, result.HttpStatus);
    }

    [Fact]
    public async Task RejectsSuccessThatIsNotFromSentinelGate()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"allowed\":true}")
        });
        var result = await Create(handler).EnterAsync(CallerToken, CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal("invalid_backend_response", result.Classification);
    }

    [Theory]
    [InlineData("http://sentinel-api.example.test")]
    [InlineData("https://sentinel-api.example.test:444")]
    [InlineData("https://sentinel-api.example.test/browser-path")]
    [InlineData("https://sentinel-api.example.test/?target=attacker.invalid")]
    [InlineData("https://user@sentinel-api.example.test")]
    [InlineData("https://127.0.0.1")]
    public void RejectsAnyConfiguredTargetThatIsNotTheProtectedHttpsDnsOrigin(string target)
    {
        var options = TestOptions() with { GateApiBase = target };
        Assert.Throws<InvalidOperationException>(() => new GateForwarder(
            options,
            new StubHttpClientFactory(new StubHandler(_ => GateResponse(HttpStatusCode.OK))),
            new CollectingLogger<GateForwarder>()));
    }

    [Theory]
    [MemberData(nameof(InvalidSuccessfulPayloads))]
    public async Task RejectsSuccessfulResponseWithoutExactTrustedSchema(string body)
    {
        var result = await Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        })).EnterAsync(CallerToken, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.False(result.GatewayValidated);
        Assert.False(result.RoutingContextConsistent);
        Assert.Equal("invalid_backend_response", result.Classification);
    }

    public static IEnumerable<object[]> InvalidSuccessfulPayloads()
    {
        const string template = """
            {"service":"SentinelGate","allowed":true,"gatewayValidated":GATEWAY,"routingContextConsistent":CONTEXT,"tenantId":"TENANT","objectId":"OBJECT"}
            """;
        yield return [template.Replace("GATEWAY", "false").Replace("CONTEXT", "true").Replace("TENANT", "11111111-1111-1111-1111-111111111111").Replace("OBJECT", "22222222-2222-2222-2222-222222222222")];
        yield return [template.Replace("GATEWAY", "true").Replace("CONTEXT", "false").Replace("TENANT", "11111111-1111-1111-1111-111111111111").Replace("OBJECT", "22222222-2222-2222-2222-222222222222")];
        yield return [template.Replace("GATEWAY", "true").Replace("CONTEXT", "true").Replace("TENANT", "33333333-3333-3333-3333-333333333333").Replace("OBJECT", "22222222-2222-2222-2222-222222222222")];
        yield return [template.Replace("GATEWAY", "true").Replace("CONTEXT", "true").Replace("TENANT", "{11111111-1111-1111-1111-111111111111}").Replace("OBJECT", "22222222-2222-2222-2222-222222222222")];
        yield return [template.Replace("GATEWAY", "true").Replace("CONTEXT", "true").Replace("TENANT", "11111111-1111-1111-1111-111111111111").Replace("OBJECT", "00000000-0000-0000-0000-000000000000")];
        yield return [template.Replace("GATEWAY", "true").Replace("CONTEXT", "true").Replace("TENANT", "11111111-1111-1111-1111-111111111111").Replace("OBJECT", "{22222222-2222-2222-2222-222222222222}")];
    }

    private static GateForwarder Create(
        HttpMessageHandler handler,
        ILogger<GateForwarder>? logger = null) =>
        new(TestOptions(), new StubHttpClientFactory(handler), logger ?? new CollectingLogger<GateForwarder>());

    private static HttpResponseMessage GateResponse(HttpStatusCode status) => new(status)
    {
        Content = new StringContent(
            """
            {
              "service": "SentinelGate",
              "allowed": true,
              "message": "You are in",
              "gatewayValidated": true,
              "routingContextConsistent": true,
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "objectId": "22222222-2222-2222-2222-222222222222"
            }
            """),
    };

    internal static SentinelOptions TestOptions() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ApiClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        SpaClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        DaemonClientId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
        DaemonClientSecret = "test-only",
        OpenAIEndpoint = "https://example.openai.azure.com/",
        ModelDeployment = "test-model",
        GatewayResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.Network/applicationGateways/agw-test",
        LawWorkspaceGuid = "dddddddd-dddd-dddd-dddd-dddddddddddd",
        GateApiBase = "https://sentinel-api.example.test",
    };
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly HttpClient _client = new(handler, disposeHandler: false);
    public HttpClient CreateClient(string name) => _client;
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(response(request));
}

internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
