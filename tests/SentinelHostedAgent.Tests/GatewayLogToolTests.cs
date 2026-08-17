using System.Text.Json;
using SentinelHostedAgent.Configuration;
using SentinelHostedAgent.Tools;

namespace SentinelHostedAgent.Tests;

public sealed class GatewayLogToolTests
{
    [Fact]
    public void QueryUsesOriginalHostOnlyAsBoundedTelemetryContext()
    {
        var query = GatewayLogTool.BuildQuery("sentinel-api.example.com", 60, 401, "tx-1");

        Assert.Contains("OriginalHost =~ 'sentinel-api.example.com'", query, StringComparison.Ordinal);
        Assert.Contains("RequestUri startswith '/enter'", query, StringComparison.Ordinal);
        Assert.Contains("HttpStatus == 401", query, StringComparison.Ordinal);
        Assert.Contains("TransactionId == 'tx-1'", query, StringComparison.Ordinal);
        Assert.Contains("| take 40", query, StringComparison.Ordinal);
        Assert.DoesNotContain("| where Host", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyResultExplainsIngestionDelayAndEvidenceBoundary()
    {
        var tool = new GatewayLogTool(CreateOptions(), new EmptyLogClient());

        var result = await tool.QueryAsync();
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("ingestion may lag", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not authentication proof", json, StringComparison.OrdinalIgnoreCase);
    }

    internal static HostedAgentOptions CreateOptions(Uri? broker = null) => HostedAgentOptions.FromValues(
        "https://aif-example.services.ai.azure.com/api/projects/proj-example",
        "gpt-4o",
        "/subscriptions/11111111-1111-4111-8111-111111111111/resourceGroups/rg-example/providers/Microsoft.Network/applicationGateways/agw-example",
        "22222222-2222-4222-8222-222222222222",
        "sentinel-api.example.com",
        "33333333-3333-4333-8333-333333333333",
        "44444444-4444-4444-8444-444444444444",
        broker?.AbsoluteUri,
        null);

    private sealed class EmptyLogClient : IHostedLogClient
    {
        public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
            string workspaceId,
            string query,
            TimeSpan timeRange,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, string?>>>([]);
    }
}
