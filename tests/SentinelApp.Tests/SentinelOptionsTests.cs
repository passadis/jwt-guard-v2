using Microsoft.Extensions.Configuration;
using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class SentinelOptionsTests
{
    [Fact]
    public void MissingAgentModeDefaultsToEmbedded()
    {
        var options = SentinelOptions.FromConfiguration(Configuration());
        Assert.Equal(AgentMode.Embedded, options.AgentMode);
        Assert.Null(options.HostedAgentResponsesEndpoint);
        Assert.Null(options.HostedAgentVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("automatic")]
    [InlineData("hosted-or-embedded")]
    [InlineData("1")]
    public void UnknownAgentModeFailsStartup(string value)
    {
        var configuration = Configuration(new() { ["AGENT_MODE"] = value });
        if (value.Length == 0)
        {
            Assert.Equal(AgentMode.Embedded, SentinelOptions.FromConfiguration(configuration).AgentMode);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => SentinelOptions.FromConfiguration(configuration));
        }
    }

    [Theory]
    [InlineData("http://acct.services.ai.azure.com/api/projects/p/agents/a/endpoint/protocols/openai/responses?api-version=v1")]
    [InlineData("https://acct.services.ai.azure.com:444/api/projects/p/agents/a/endpoint/protocols/openai/responses?api-version=v1")]
    [InlineData("https://user@acct.services.ai.azure.com/api/projects/p/agents/a/endpoint/protocols/openai/responses?api-version=v1")]
    [InlineData("https://acct.example.test/api/projects/p/agents/a/endpoint/protocols/openai/responses?api-version=v1")]
    [InlineData("https://acct.services.ai.azure.com/api/projects/p/agents/a/endpoint/protocols/openai/responses/other?api-version=v1")]
    [InlineData("https://acct.services.ai.azure.com/api/projects/p/agents/a/endpoint/protocols/openai/responses?api-version=v2")]
    [InlineData("https://acct.services.ai.azure.com/api/projects/p/agents/a/endpoint/protocols/openai/responses?api-version=v1#fragment")]
    public void HostedEndpointRejectsUnreviewedOriginsOrPaths(string endpoint)
    {
        var configuration = Configuration(new()
        {
            ["AGENT_MODE"] = "Hosted",
            ["HOSTED_AGENT_VERSION"] = "5",
            ["HOSTED_AGENT_RESPONSES_ENDPOINT"] = endpoint,
        });
        Assert.Throws<InvalidOperationException>(() => SentinelOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void HostedModeAcceptsExactVersionedResponsesEndpoint()
    {
        var options = SentinelOptions.FromConfiguration(Configuration(new()
        {
            ["AGENT_MODE"] = "hosted",
            ["HOSTED_AGENT_VERSION"] = "5",
            ["HOSTED_AGENT_TIMEOUT_SECONDS"] = "60",
            ["HOSTED_AGENT_RESPONSES_ENDPOINT"] =
                "https://acct.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/openai/responses?api-version=v1",
        }));
        Assert.Equal(AgentMode.Hosted, options.AgentMode);
        Assert.Equal(5, options.HostedAgentVersion);
        Assert.Equal(TimeSpan.FromSeconds(60), options.HostedAgentTimeout);
    }

    [Fact]
    public void HostedVersionRejectsLeadingZeros()
    {
        var configuration = Configuration(new()
        {
            ["AGENT_MODE"] = "Hosted",
            ["HOSTED_AGENT_VERSION"] = "05",
            ["HOSTED_AGENT_RESPONSES_ENDPOINT"] =
                "https://acct.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/openai/responses?api-version=v1",
        });
        Assert.Throws<InvalidOperationException>(() => SentinelOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void HostedShadowRequiresTesterAllowlist()
    {
        var configuration = Configuration(new()
        {
            ["AGENT_MODE"] = "HostedShadow",
            ["HOSTED_AGENT_VERSION"] = "6",
            ["HOSTED_AGENT_RESPONSES_ENDPOINT"] =
                "https://acct.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/openai/responses?api-version=v1",
        });
        Assert.Throws<InvalidOperationException>(() => SentinelOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void HostedShadowAcceptsCanonicalTesterAllowlist()
    {
        var options = SentinelOptions.FromConfiguration(Configuration(new()
        {
            ["AGENT_MODE"] = "HostedShadow",
            ["HOSTED_AGENT_VERSION"] = "6",
            ["HOSTED_AGENT_RESPONSES_ENDPOINT"] =
                "https://acct.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/openai/responses?api-version=v1",
            ["HOSTED_SHADOW_TESTER_OBJECT_IDS"] =
                "22222222-2222-2222-2222-222222222222,33333333-3333-3333-3333-333333333333",
        }));
        Assert.Equal(AgentMode.HostedShadow, options.AgentMode);
        Assert.Equal(2, options.HostedShadowTesterObjectIds.Count);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("22222222-2222-2222-2222-222222222222,22222222-2222-2222-2222-222222222222")]
    [InlineData("22222222-2222-2222-2222-22222222222A")]
    [InlineData("not-a-guid")]
    public void HostedShadowRejectsInvalidTesterAllowlist(string testers)
    {
        var configuration = Configuration(new()
        {
            ["AGENT_MODE"] = "HostedShadow",
            ["HOSTED_AGENT_VERSION"] = "6",
            ["HOSTED_AGENT_RESPONSES_ENDPOINT"] =
                "https://acct.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/openai/responses?api-version=v1",
            ["HOSTED_SHADOW_TESTER_OBJECT_IDS"] = testers,
        });
        Assert.Throws<InvalidOperationException>(() => SentinelOptions.FromConfiguration(configuration));
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
            ["API_CLIENT_ID"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ["SPA_CLIENT_ID"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            ["DAEMON_CLIENT_ID"] = "cccccccc-cccc-cccc-cccc-cccccccccccc",
            ["DAEMON_CLIENT_SECRET"] = "test-only",
            ["AZURE_OPENAI_ENDPOINT"] = "https://example.openai.azure.com/",
            ["MODEL_DEPLOYMENT"] = "test-model",
            ["GATEWAY_RESOURCE_ID"] = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.Network/applicationGateways/agw-test",
            ["LAW_WORKSPACE_GUID"] = "dddddddd-dddd-dddd-dddd-dddddddddddd",
            ["GATE_API_BASE"] = "https://sentinel-api.example.test",
        };
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
