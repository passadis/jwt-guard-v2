using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class HostedGateExplainerTests
{
    private const string Owner = "11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222";
    private const string Session = "33333333-3333-3333-3333-333333333333";
    private const string Endpoint = "https://acct.services.ai.azure.com/api/projects/project/agents/gate-agent/endpoint/protocols/openai/responses?api-version=v1";

    [Fact]
    public async Task UsesConsistentDelegatedIdentityAndCurrentVersionedContracts()
    {
        var requests = new List<CapturedRequest>();
        var count = 0;
        var handler = new StubHandler(request =>
        {
            requests.Add(CapturedRequest.From(request));
            count++;
            return count switch
            {
                1 => Json(HttpStatusCode.Created,
                    """{"agent_session_id":"session-opaque","version_indicator":{"type":"version_ref","agent_version":"5"}}"""),
                2 => Json(HttpStatusCode.Created, """{"id":"conversation-opaque"}"""),
                3 => Sse("""
                    event: response.output_text.delta
                    data: {"delta":"Hosted answer"}

                    event: response.completed
                    data: {"response":{"status":"completed"}}

                    """),
                _ => throw new InvalidOperationException("Unexpected request."),
            };
        });
        var explainer = Create(handler);

        var output = await Collect(explainer.StreamAsync(Owner, Session, "Explain the protected listener."));

        Assert.Equal("Hosted answer", output);
        Assert.Equal(3, requests.Count);
        Assert.Equal(
            "https://acct.services.ai.azure.com/api/projects/project/agents/gate-agent/endpoint/sessions?api-version=v1",
            requests[0].Uri.AbsoluteUri);
        Assert.Contains("\"agent_version\":\"5\"", requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(
            "https://acct.services.ai.azure.com/api/projects/project/agents/gate-agent/endpoint/protocols/openai/conversations?api-version=v1",
            requests[1].Uri.AbsoluteUri);
        Assert.Equal(Endpoint, requests[2].Uri.AbsoluteUri);
        Assert.Contains("\"agent_session_id\":\"session-opaque\"", requests[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session_id\":", requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"conversation-opaque\"", requests[2].Body, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.Equal("Bearer test-foundry-token", request.Authorization));
        Assert.DoesNotContain(requests, request => request.Body.Contains("caller-token", StringComparison.Ordinal));
        var userIdentity = Assert.IsType<string>(requests[0].UserIdentity);
        Assert.StartsWith("usr_", userIdentity, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.Equal(userIdentity, request.UserIdentity));
        Assert.DoesNotContain("11111111", userIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(requests, request => request.Body.Contains(Owner, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DelegatedIdentityIsStablePerOwnerAndDifferentAcrossOwners()
    {
        var requests = new List<CapturedRequest>();
        var sessions = 0;
        var conversations = 0;
        var handler = new StubHandler(request =>
        {
            requests.Add(CapturedRequest.From(request));
            if (request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                sessions++;
                return Json(HttpStatusCode.Created,
                    $"{{\"agent_session_id\":\"session-{sessions}\",\"version_indicator\":{{\"type\":\"version_ref\",\"agent_version\":\"5\"}}}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/conversations", StringComparison.Ordinal))
            {
                conversations++;
                return Json(HttpStatusCode.Created, $"{{\"id\":\"conversation-{conversations}\"}}");
            }
            return Sse("""
                event: response.completed
                data: {"response":{"status":"completed","output":[{"content":[{"text":"ok"}]}]}}

                """);
        });
        var explainer = Create(handler);
        const string otherOwner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        _ = await Collect(explainer.StreamAsync(Owner, Session, "First owner."));
        _ = await Collect(explainer.StreamAsync(otherOwner, Session, "Second owner."));

        Assert.Equal(6, requests.Count);
        var firstIdentity = Assert.IsType<string>(requests[0].UserIdentity);
        var secondIdentity = Assert.IsType<string>(requests[3].UserIdentity);
        Assert.All(requests.Take(3), request => Assert.Equal(firstIdentity, request.UserIdentity));
        Assert.All(requests.Skip(3), request => Assert.Equal(secondIdentity, request.UserIdentity));
        Assert.NotEqual(firstIdentity, secondIdentity);
    }

    [Fact]
    public async Task JwtLikeChatInputNeverReachesHostedTransport()
    {
        var calls = 0;
        var explainer = Create(new StubHandler(_ =>
        {
            calls++;
            throw new InvalidOperationException();
        }));

        var output = await Collect(explainer.StreamAsync(
            Owner,
            Session,
            "Please inspect aaaaaaaa.bbbbbbbb.cccccccc"));

        Assert.Equal(0, calls);
        Assert.Contains("Do not paste access tokens", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionMismatchFailsClosedWithoutEmbeddedFallback()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created,
            """{"agent_session_id":"session-opaque","version_indicator":{"type":"version_ref","agent_version":"6"}}"""));
        var output = await Collect(Create(handler).StreamAsync(Owner, Session, "Explain routing."));
        Assert.Contains("No embedded fallback", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("throttled")]
    [InlineData("wrong-content-type")]
    [InlineData("incomplete-stream")]
    public async Task DependencyAndMalformedResponsesFailClosed(string failure)
    {
        var count = 0;
        var handler = new StubHandler(_ =>
        {
            count++;
            return count switch
            {
                1 => Json(HttpStatusCode.Created,
                    """{"agent_session_id":"session-opaque","version_indicator":{"type":"version_ref","agent_version":"5"}}"""),
                2 => Json(HttpStatusCode.Created, """{"id":"conversation-opaque"}"""),
                3 when failure == "throttled" => Json(HttpStatusCode.TooManyRequests, """{"error":"redacted"}"""),
                3 when failure == "wrong-content-type" => Json(HttpStatusCode.OK, """{"output":"unexpected"}"""),
                3 => Sse("""
                    event: response.output_text.delta
                    data: {"delta":"partial"}

                    """),
                _ => throw new InvalidOperationException(),
            };
        });
        var output = await Collect(Create(handler).StreamAsync(Owner, Session, "Run the missing token scenario."));
        Assert.Contains("No embedded fallback", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyIncompleteResponseRetriesOnceWithFreshMapping()
    {
        var requests = new List<CapturedRequest>();
        var sessions = 0;
        var conversations = 0;
        var responses = 0;
        var handler = new StubHandler(request =>
        {
            requests.Add(CapturedRequest.From(request));
            if (request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                sessions++;
                return Json(HttpStatusCode.Created,
                    $"{{\"agent_session_id\":\"session-{sessions}\",\"version_indicator\":{{\"type\":\"version_ref\",\"agent_version\":\"5\"}}}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/conversations", StringComparison.Ordinal))
            {
                conversations++;
                return Json(HttpStatusCode.Created, $"{{\"id\":\"conversation-{conversations}\"}}");
            }
            responses++;
            return responses == 1
                ? Sse("""
                    event: response.incomplete
                    data: {"type":"response.incomplete","response":{"status":"incomplete"}}

                    """)
                : Sse("""
                    event: response.completed
                    data: {"type":"response.completed","response":{"status":"completed","output":[{"content":[{"text":"recovered"}]}]}}

                    """);
        });

        var output = await Collect(Create(handler).StreamAsync(Owner, Session, "Inspect the live gateway configuration."));

        Assert.Equal("recovered", output);
        Assert.Equal(2, sessions);
        Assert.Equal(2, conversations);
        Assert.Equal(2, responses);
        Assert.Contains("\"agent_session_id\":\"session-1\"", requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"agent_session_id\":\"session-2\"", requests[5].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataTypeWithoutEventHeaderCanCompleteResponse()
    {
        var count = 0;
        var handler = new StubHandler(_ => ++count switch
        {
            1 => Json(HttpStatusCode.Created,
                """{"agent_session_id":"session-opaque","version_indicator":{"type":"version_ref","agent_version":"5"}}"""),
            2 => Json(HttpStatusCode.Created, """{"id":"conversation-opaque"}"""),
            3 => Sse("""
                data: {"type":"response.output_text.delta","delta":"data-only"}

                data: {"type":"response.completed","response":{"status":"completed"}}

                """),
            _ => throw new InvalidOperationException("Unexpected request."),
        });

        var output = await Collect(Create(handler).StreamAsync(Owner, Session, "Explain routing."));

        Assert.Equal("data-only", output);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ScenarioFailureIsNotRetriedAndNextCallCreatesFreshMapping()
    {
        var sessions = 0;
        var conversations = 0;
        var responses = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                sessions++;
                return Json(HttpStatusCode.Created,
                    $"{{\"agent_session_id\":\"session-{sessions}\",\"version_indicator\":{{\"type\":\"version_ref\",\"agent_version\":\"5\"}}}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/conversations", StringComparison.Ordinal))
            {
                conversations++;
                return Json(HttpStatusCode.Created, $"{{\"id\":\"conversation-{conversations}\"}}");
            }
            responses++;
            return responses == 1
                ? Sse("""
                    event: response.incomplete
                    data: {"type":"response.incomplete","response":{"status":"incomplete"}}

                    """)
                : Sse("""
                    event: response.completed
                    data: {"type":"response.completed","response":{"status":"completed","output":[{"content":[{"text":"ok"}]}]}}

                    """);
        });
        var explainer = Create(handler);

        var failed = await Collect(explainer.StreamAsync(Owner, Session, "Run the missing token scenario."));
        var recovered = await Collect(explainer.StreamAsync(Owner, Session, "Explain routing."));

        Assert.Contains("No embedded fallback", failed, StringComparison.Ordinal);
        Assert.Equal("ok", recovered);
        Assert.Equal(2, sessions);
        Assert.Equal(2, conversations);
        Assert.Equal(2, responses);
    }

    [Fact]
    public async Task ReusesOwnerBoundSessionAndResetCreatesANewMapping()
    {
        var sessions = 0;
        var conversations = 0;
        var responses = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                sessions++;
                return Json(HttpStatusCode.Created,
                    $"{{\"agent_session_id\":\"session-{sessions}\",\"version_indicator\":{{\"type\":\"version_ref\",\"agent_version\":\"5\"}}}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/conversations", StringComparison.Ordinal))
            {
                conversations++;
                return Json(HttpStatusCode.Created, $"{{\"id\":\"conversation-{conversations}\"}}");
            }
            responses++;
            return Sse("""
                event: response.completed
                data: {"response":{"status":"completed","output":[{"content":[{"text":"ok"}]}]}}

                """);
        });
        var explainer = Create(handler);
        _ = await Collect(explainer.StreamAsync(Owner, Session, "First."));
        _ = await Collect(explainer.StreamAsync(Owner, Session, "Second."));
        Assert.Equal(1, sessions);
        Assert.Equal(1, conversations);
        Assert.Equal(2, responses);

        await explainer.ResetSessionAsync(Owner, Session);
        _ = await Collect(explainer.StreamAsync(Owner, Session, "After reset."));
        Assert.Equal(2, sessions);
        Assert.Equal(2, conversations);
        Assert.Equal(3, responses);
    }

    private static HostedGateExplainer Create(HttpMessageHandler handler)
    {
        var options = GateForwarderTests.TestOptions() with
        {
            AgentMode = AgentMode.Hosted,
            HostedAgentResponsesEndpoint = new Uri(Endpoint),
            HostedAgentVersion = 5,
            HostedAgentTimeout = TimeSpan.FromSeconds(30),
        };
        return new HostedGateExplainer(
            options,
            new StubHttpClientFactory(handler),
            new StaticTokenCredential(),
            new BrokerEvidenceStore(),
            NullLogger<HostedGateExplainer>.Instance);
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var result = new StringBuilder();
        await foreach (var chunk in stream)
        {
            result.Append(chunk);
        }
        return result.ToString();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Sse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/event-stream"),
    };

    private sealed record CapturedRequest(Uri Uri, string Body, string? Authorization, string? UserIdentity)
    {
        public static CapturedRequest From(HttpRequestMessage request) => new(
            request.RequestUri!,
            request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("x-ms-user-identity", out var values) ? values.Single() : null);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-foundry-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
