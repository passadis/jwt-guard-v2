using System.ComponentModel;
using Azure.Monitor.Query;
using SentinelHostedAgent.Configuration;

namespace SentinelHostedAgent.Tools;

public sealed class GatewayLogTool(HostedAgentOptions options, IHostedLogClient logClient)
{
    [Description("Queries recent protected-host /enter Application Gateway access logs. OriginalHost is routing context for telemetry selection, never authentication evidence.")]
    public async Task<object> QueryAsync(
        [Description("Minutes of history, from 1 to 1440.")] int minutes = 60,
        [Description("Optional HTTP status from 100 to 599.")] int? statusCode = null,
        [Description("Optional exact transaction identifier, up to 128 safe characters.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        minutes = Math.Clamp(minutes, 1, 1440);
        if (statusCode is not null and (< 100 or > 599))
        {
            return new { evidenceType = "unknown", error = "statusCode must be between 100 and 599." };
        }
        if (!string.IsNullOrWhiteSpace(correlationId) &&
            (correlationId.Length > 128 || correlationId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
        {
            return new { evidenceType = "unknown", error = "correlationId has an invalid format." };
        }

        var query = BuildQuery(options.ProtectedHost, minutes, statusCode, correlationId);
        try
        {
            var rows = await logClient.QueryAsync(
                options.LawWorkspaceGuid,
                query,
                TimeSpan.FromMinutes(minutes),
                cancellationToken);
            return new
            {
                evidenceType = "live_log_analytics_query",
                protectedHost = options.ProtectedHost,
                path = "/enter",
                minutes,
                statusCode,
                correlationId,
                count = rows.Count,
                routingContextBoundary = "OriginalHost selects the configured public route in telemetry; it is not authentication proof.",
                note = rows.Count == 0
                    ? "No matching records are available yet. Log Analytics ingestion may lag several minutes."
                    : "Matching protected-route records, newest first. Attribute a failure only when returned fields support it.",
                rows,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new
            {
                evidenceType = "unknown",
                error = "The protected-gate log query failed.",
                detail = ex.GetType().Name,
            };
        }
    }

    public static string BuildQuery(string protectedHost, int minutes, int? statusCode, string? correlationId)
    {
        var filters = new List<string>
        {
            $"| where TimeGenerated > ago({Math.Clamp(minutes, 1, 1440)}m)",
            $"| where OriginalHost =~ '{EscapeKql(protectedHost)}'",
            "| where RequestUri startswith '/enter'",
        };
        if (statusCode is >= 100 and <= 599)
        {
            filters.Add($"| where HttpStatus == {statusCode.Value}");
        }
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            filters.Add($"| where TransactionId == '{EscapeKql(correlationId)}'");
        }
        filters.Add("| project TimeGenerated, TransactionId, ClientIp, RequestUri, OriginalHost, HttpStatus, ServerStatus, TimeTaken");
        filters.Add("| order by TimeGenerated desc");
        filters.Add("| take 40");
        return "AGWAccessLogs\n" + string.Join("\n", filters);
    }

    private static string EscapeKql(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

public interface IHostedLogClient
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string workspaceId,
        string query,
        TimeSpan timeRange,
        CancellationToken cancellationToken);
}

public sealed class AzureHostedLogClient(Azure.Core.TokenCredential credential) : IHostedLogClient
{
    private readonly LogsQueryClient _client = new(credential);

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string workspaceId,
        string query,
        TimeSpan timeRange,
        CancellationToken cancellationToken)
    {
        var response = await _client.QueryWorkspaceAsync(
            workspaceId,
            query,
            new QueryTimeRange(timeRange),
            cancellationToken: cancellationToken);
        var table = response.Value.Table;
        return table.Rows.Select(row =>
                (IReadOnlyDictionary<string, string?>)table.Columns
                    .Select((column, index) => (column.Name, Value: row[index]?.ToString()))
                    .Where(item => !string.IsNullOrEmpty(item.Value))
                    .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }
}
