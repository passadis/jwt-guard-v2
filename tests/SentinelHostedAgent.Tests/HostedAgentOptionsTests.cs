using SentinelHostedAgent.Configuration;

namespace SentinelHostedAgent.Tests;

public sealed class HostedAgentOptionsTests
{
    [Fact]
    public void AcceptsStrictApprovedValues()
    {
        var options = Create();

        Assert.Equal("apiguard.mvps.gr", options.ProtectedHost);
        Assert.Equal(new Uri("https://guard.mvps.gr/"), options.BrokerOrigin);
        Assert.Equal("agent-iq", options.ToolboxName);
    }

    [Theory]
    [InlineData("http://guard.mvps.gr/")]
    [InlineData("https://guard.mvps.gr/path")]
    [InlineData("https://guard.mvps.gr/?next=https://evil.example")]
    [InlineData("https://user@guard.mvps.gr/")]
    [InlineData("https://guard.mvps.gr:8443/")]
    public void RejectsNoncanonicalBrokerOrigins(string brokerOrigin)
    {
        Assert.Throws<InvalidOperationException>(() => Create(brokerOrigin));
    }

    [Theory]
    [InlineData("https://apiguard.mvps.gr/")]
    [InlineData("apiguard.mvps.gr/path")]
    [InlineData("apiguard.mvps.gr:443")]
    [InlineData("apiguard")]
    public void RejectsProtectedHostThatIsNotAPlainDnsName(string protectedHost)
    {
        Assert.Throws<InvalidOperationException>(() => Create(protectedHost: protectedHost));
    }

    private static HostedAgentOptions Create(
        string brokerOrigin = "https://guard.mvps.gr/",
        string protectedHost = "apiguard.mvps.gr") => HostedAgentOptions.FromValues(
            "https://aif-example.services.ai.azure.com/api/projects/proj-example",
            "gpt-4o",
            "/subscriptions/9d47bf93-091d-480e-a512-1e918864fee7/resourceGroups/rg-edgegrd/providers/Microsoft.Network/applicationGateways/agw-edgegrd",
            "11111111-1111-1111-1111-111111111111",
            protectedHost,
            "35de4c50-7dcd-4871-8685-61789c017da2",
            "8ad40006-a261-46d2-bc79-362cd6a42256",
            brokerOrigin,
            "agent-iq");
}
