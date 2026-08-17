using SentinelHostedAgent.Configuration;

namespace SentinelHostedAgent.Tests;

public sealed class HostedAgentOptionsTests
{
    [Fact]
    public void AcceptsStrictApprovedValues()
    {
        var options = Create();

        Assert.Equal("sentinel-api.example.com", options.ProtectedHost);
        Assert.Equal(new Uri("https://sentinel.example.com/"), options.BrokerOrigin);
        Assert.Equal("agent-iq", options.ToolboxName);
    }

    [Theory]
    [InlineData("http://sentinel.example.com/")]
    [InlineData("https://sentinel.example.com/path")]
    [InlineData("https://sentinel.example.com/?next=https://evil.example")]
    [InlineData("https://user@sentinel.example.com/")]
    [InlineData("https://sentinel.example.com:8443/")]
    public void RejectsNoncanonicalBrokerOrigins(string brokerOrigin)
    {
        Assert.Throws<InvalidOperationException>(() => Create(brokerOrigin));
    }

    [Theory]
    [InlineData("https://sentinel-api.example.com/")]
    [InlineData("sentinel-api.example.com/path")]
    [InlineData("sentinel-api.example.com:443")]
    [InlineData("apiguard")]
    public void RejectsProtectedHostThatIsNotAPlainDnsName(string protectedHost)
    {
        Assert.Throws<InvalidOperationException>(() => Create(protectedHost: protectedHost));
    }

    private static HostedAgentOptions Create(
        string brokerOrigin = "https://sentinel.example.com/",
        string protectedHost = "sentinel-api.example.com") => HostedAgentOptions.FromValues(
            "https://aif-example.services.ai.azure.com/api/projects/proj-example",
            "gpt-4o",
            "/subscriptions/11111111-1111-4111-8111-111111111111/resourceGroups/rg-example/providers/Microsoft.Network/applicationGateways/agw-example",
            "22222222-2222-4222-8222-222222222222",
            protectedHost,
            "33333333-3333-4333-8333-333333333333",
            "44444444-4444-4444-8444-444444444444",
            brokerOrigin,
            "agent-iq");
}
