using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Azure.Core;
using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class GateToolsTests
{
    [Fact]
    public async Task DecoderUsesLivePolicyAndIncludesSignatureDisclaimer()
    {
        var tools = CreateTools(AttachedGatewayJson(), out _);
        var token = Token(
            expires: DateTime.UtcNow.AddMinutes(30),
            notBefore: DateTime.UtcNow.AddMinutes(-1));

        var result = JsonSerializer.Serialize(await tools.DecodeTokenAsync(token));

        Assert.Contains("verified_decode_plus_live_policy", result);
        Assert.Contains("Predicted allow", result);
        Assert.Contains("Not performed", result);
        Assert.Contains("api://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result);
    }

    [Fact]
    public async Task DecoderDetectsRemovedRuleAttachment()
    {
        var tools = CreateTools(DetachedGatewayJson(), out _);
        var result = JsonSerializer.Serialize(await tools.DecodeTokenAsync(Token()));
        Assert.Contains("Unknown because the live protected rule", result);
        Assert.Contains("\"ProtectedRuleAttached\":false", result);
    }

    [Fact]
    public async Task DecoderReportsLivePolicyDriftInsteadOfPredictingAllow()
    {
        var drifted = AttachedGatewayJson().Replace(
            "\"clientId\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"",
            "\"clientId\": \"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\"",
            StringComparison.Ordinal);
        var tools = CreateTools(drifted, out _);
        var result = JsonSerializer.Serialize(await tools.DecodeTokenAsync(Token()));

        Assert.Contains("live API client ID", result);
        Assert.Contains("Unknown because the live JWT policy differs", result);
        Assert.DoesNotContain("Predicted allow", result);
    }

    [Fact]
    public async Task DecoderEvaluatesExpirationAndNotBefore()
    {
        var tools = CreateTools(AttachedGatewayJson(), out _);
        var expired = JsonSerializer.Serialize(await tools.DecodeTokenAsync(
            Token(
                expires: DateTime.UtcNow.AddHours(-1),
                notBefore: DateTime.UtcNow.AddHours(-2))));
        var notYetValid = JsonSerializer.Serialize(await tools.DecodeTokenAsync(
            Token(
                expires: DateTime.UtcNow.AddHours(2),
                notBefore: DateTime.UtcNow.AddHours(1))));

        Assert.Contains("Predicted deny", expired);
        Assert.Contains("expiration (exp)", expired);
        Assert.Contains("Predicted deny", notYetValid);
        Assert.Contains("not before (nbf)", notYetValid);
    }

    [Fact]
    public async Task LogQueryIsRestrictedToProtectedHostAndEnterPath()
    {
        var tools = CreateTools(AttachedGatewayJson(), out var logs);
        var result = JsonSerializer.Serialize(await tools.QueryGateLogsAsync(30, 401, "tx-123"));

        Assert.Contains("OriginalHost =~ 'sentinel-api.example.test'", logs.LastKql);
        Assert.DoesNotContain("| where Host =~", logs.LastKql);
        Assert.Contains("RequestUri startswith '/enter'", logs.LastKql);
        Assert.Contains("HttpStatus == 401", logs.LastKql);
        Assert.Contains("TransactionId == 'tx-123'", logs.LastKql);
        Assert.DoesNotContain("sentinel.example.test", logs.LastKql);
        Assert.Contains("ingestion", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audience mismatch", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend was not reached", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SimulationDoesNotReturnUnvalidatedBackendFields()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "management.azure.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AttachedGatewayJson()),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"service\":\"NotSentinelGate\",\"allowed\":true,\"rawToken\":\"must-not-escape\"}"),
            };
        });
        var tools = new GateTools(
            GateForwarderTests.TestOptions(),
            new StubHttpClientFactory(handler),
            new FakeTokenCredential(),
            new RecordingLogClient());

        var result = JsonSerializer.Serialize(await tools.SimulateAsync("missing"));

        Assert.Contains("not a valid SentinelGate identity payload", result);
        Assert.DoesNotContain("must-not-escape", result);
        Assert.Contains("\"schemaValid\":false", result);
    }

    private static GateTools CreateTools(string armJson, out RecordingLogClient logs)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(armJson),
        });
        logs = new RecordingLogClient();
        return new GateTools(
            GateForwarderTests.TestOptions(),
            new StubHttpClientFactory(handler),
            new FakeTokenCredential(),
            logs);
    }

    private static string Token(DateTime? expires = null, DateTime? notBefore = null)
    {
        var jwt = new JwtSecurityToken(
            issuer: "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            audience: "api://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            claims:
            [
                new Claim("tid", "11111111-1111-1111-1111-111111111111"),
                new Claim("oid", "22222222-2222-2222-2222-222222222222"),
            ],
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddMinutes(30));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static string AttachedGatewayJson() => GatewayJson(includeAttachment: true);
    private static string DetachedGatewayJson() => GatewayJson(includeAttachment: false);

    private static string GatewayJson(bool includeAttachment) => $$"""
        {
          "name": "agw-test",
          "properties": {
            "provisioningState": "Succeeded",
            "httpListeners": [
              { "name": "ui-https", "properties": { "hostName": "sentinel.example.test" } },
              { "name": "api-https", "properties": { "hostName": "sentinel-api.example.test" } }
            ],
            "entraJWTValidationConfigs": [
              {
                "name": "jwt-deny",
                "properties": {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "clientId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "audiences": [
                    "api://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
                  ],
                  "unAuthorizedRequestAction": "Deny"
                }
              }
            ],
            "requestRoutingRules": [
              {
                "name": "ui-rule",
                "properties": {
                  "httpListener": { "id": "/httpListeners/ui-https" }
                }
              },
              {
                "name": "api-rule",
                "properties": {
                  "httpListener": { "id": "/httpListeners/api-https" }{{(includeAttachment ? ",\n                  \"entraJWTValidationConfig\": { \"id\": \"/entraJWTValidationConfigs/jwt-deny\" }" : "")}}
                }
              }
            ]
          }
        }
        """;
}

internal sealed class FakeTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-arm-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class RecordingLogClient : IGateLogClient
{
    public string LastKql { get; private set; } = string.Empty;

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string workspaceId,
        string kql,
        TimeSpan timeRange,
        CancellationToken cancellationToken)
    {
        LastKql = kql;
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = [];
        return Task.FromResult(rows);
    }
}
