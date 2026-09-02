using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SentinelHostedAgent.Security;

public static class KnowledgeGroundedAnswer
{
    public static string? Create(
        string prompt,
        IEnumerable<ChatMessage> evidenceMessages)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(evidenceMessages);

        var sources = ExtractSources(evidenceMessages);
        if (sources.Count == 0)
        {
            return null;
        }

        var evidence = string.Join('\n', sources.Select(source => source.Content));
        var answer = SelectAnswer(prompt, evidence);
        if (answer is null)
        {
            return null;
        }

        var citations = sources
            .Select(source => source.Title)
            .Where(IsSafeCitation)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();

        return citations.Length == 0
            ? null
            : $"{answer}\n\nSources: {string.Join("; ", citations)}";
    }

    private static string? SelectAnswer(string prompt, string evidence)
    {
        if (ContainsAny(prompt, "host header", "backend host", "pickhostnamefrombackendaddress", "tls", "sni"))
        {
            return ContainsAll(evidence, "pickHostNameFromBackendAddress", "backend Host", "TLS/SNI")
                ? "Application Gateway preserves the SentinelGate Container App FQDN as the backend Host header and TLS/SNI name because `pickHostNameFromBackendAddress = true`. The protected public hostname remains routing context; `x-original-host` can support a consistency check but is not authentication or proof that JWT validation occurred."
                : null;
        }

        if (ContainsAny(prompt, "x-original-host"))
        {
            return ContainsAll(evidence, "x-original-host", "routing context", "not authentication")
                || ContainsAll(evidence, "x-original-host", "routing context", "neither authentication")
                ? "`x-original-host` is supplementary, client-originated routing context. SentinelGate may reject a missing or mismatched value as an unexpected route, but a match is never authentication and never proves that Application Gateway validated a JWT."
                : null;
        }

        if (ContainsAny(prompt, "x-msft-entra-identity", "entra identity"))
        {
            return ContainsAll(evidence, "x-msft-entra-identity", "canonical", "tenant")
                ? "SentinelGate accepts `x-msft-entra-identity` only inside the protected-listener boundary and requires exactly two canonical, non-empty GUIDs in `tenantId:objectId` form with the configured tenant. The header is not trusted on any path that can bypass Application Gateway."
                : null;
        }

        if (ContainsAny(prompt, "nat gateway", "nat egress", "container app ingress"))
        {
            return ContainsAll(evidence, "NAT", "ingress", "Application Gateway")
                ? "The Application Gateway subnet uses NAT for dependable outbound HTTPS connectivity, and each Container App ingress allowlist includes only the gateway frontend and NAT egress public IPs. This preserves backend reachability while preventing arbitrary direct callers from entering the trusted backend path."
                : null;
        }

        return ContainsAll(evidence, "SentinelApp", "SentinelGate", "Deny")
            ? "JWT Sentinel separates the browser-facing UI plane from the protected demonstration plane. The UI listener routes only to SentinelApp, while the dedicated protected listener attaches JWT `Deny` validation and routes only to SentinelGate. Restricted Container Apps ingress and SentinelGate's strict injected-identity parsing complete the boundary; no single client-originated header is authentication proof."
            : null;
    }

    private static List<IqSource> ExtractSources(IEnumerable<ChatMessage> messages)
    {
        var contents = messages.SelectMany(message => message.Contents).ToList();
        if (!contents.OfType<FunctionCallContent>().Any(call =>
            string.Equals(call.Name, KnowledgeGroundingPolicy.RequiredToolName, StringComparison.Ordinal)))
        {
            return [];
        }

        var sources = new List<IqSource>();
        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            if (result.Exception is not null || result.Result is null)
            {
                continue;
            }

            try
            {
                Visit(JsonSerializer.SerializeToElement(result.Result), sources, depth: 0);
            }
            catch (JsonException)
            {
                // Malformed or unexpected tool output is not adequate evidence.
            }
        }

        return sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Title)
                && !string.IsNullOrWhiteSpace(source.Content))
            .DistinctBy(source => (source.Title, source.Content))
            .ToList();
    }

    private static void Visit(JsonElement element, List<IqSource> sources, int depth)
    {
        if (depth > 12)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetString(element, "title", out var title)
                    && TryGetString(element, "content", out var content))
                {
                    sources.Add(new IqSource(title, content));
                }

                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value, sources, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, sources, depth + 1);
                }
                break;

            case JsonValueKind.String:
                var text = element.GetString();
                if (text is not null && text.Length <= 1_000_000)
                {
                    var trimmed = text.TrimStart();
                    if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(text);
                            Visit(document.RootElement, sources, depth + 1);
                        }
                        catch (JsonException)
                        {
                            // Ordinary retrieved text is not nested JSON.
                        }
                    }
                }
                break;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } text
            && (value = text).Length > 0;
    }

    private static bool ContainsAll(string value, params string[] terms) =>
        terms.All(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeCitation(string value) =>
        value.Length is > 0 and <= 256
        && !value.Any(char.IsControl)
        && (value.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://learn.microsoft.com/", StringComparison.OrdinalIgnoreCase));

    private sealed record IqSource(string Title, string Content);
}
