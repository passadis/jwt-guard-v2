using System.Text.Json;
using SentinelHostedAgent.Configuration;
using SentinelHostedAgent.Tools;

namespace SentinelHostedAgent.Tests;

public sealed class GatewayLogToolTests
{
    [Fact]
    public void QueryUsesOriginalHostOnlyAsBoundedTelemetryContext()
    {
        var query = GatewayLogTool.BuildQuery("apiguard.mvps.gr", 60, 401, "tx-1");

        Assert.Contains("OriginalHost =~ 'apiguard.mvps.gr'", query, StringComparison.Ordinal);
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
        "/subscriptions/9d47bf93-091d-480e-a512-1e918864fee7/resourceGroups/rg-edgegrd/providers/Microsoft.Network/applicationGateways/agw-edgegrd",
        "11111111-1111-1111-1111-111111111111",
        "apiguard.mvps.gr",
        "35de4c50-7dcd-4871-8685-61789c017da2",
        "8ad40006-a261-46d2-bc79-362cd6a42256",
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
