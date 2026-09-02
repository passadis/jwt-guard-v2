using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace SentinelHostedAgent.Security;

public static partial class KnowledgeGroundingPolicy
{
    public const string RequiredToolName = "jwt-sentinel-iq___knowledge_base_retrieve";

    public static bool ShouldRequireIq(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var latestUserText = messages
            .LastOrDefault(message => message.Role == ChatRole.User)
            ?.Text;

        return ShouldRequireIq(latestUserText);
    }

    public static bool ShouldRequireIq(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || ExcludedCorpus().IsMatch(input)
            || LiveEvidenceRequest().IsMatch(input))
        {
            return false;
        }

        return ArchitectureTopic().IsMatch(input)
            && KnowledgeQuestion().IsMatch(input);
    }

    [GeneratedRegex(
        @"\b(docs[/\\]history|archived?\s+(session|jsonl|record)|session\s+jsonl)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedCorpus();

    [GeneratedRegex(
        @"\b(live|current|actual|currently\s+deployed|inspect|query|recent\s+logs?|show\s+me\s+the\s+logs?|run\s+(the\s+)?scenario|simulate|decode\s+(the\s+)?token|token\s+claims?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveEvidenceRequest();

    [GeneratedRegex(
        @"\b(architecture|trust\s+boundary|two[- ](?:container[- ]?)?apps?|sentinelapp|sentinelgate|backend\s+host|host\s+header|pickhostnamefrombackendaddress|tls|sni|x-original-host|x-msft-entra-identity|jwt\s+validation|protected\s+listener|ui\s+listener|container\s+apps?\s+ingress|nat\s+(gateway|egress))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureTopic();

    [GeneratedRegex(
        @"\b(what|which|why|how|explain|describe|document|design|purpose|reason|work|works|working|source|sources|cite|citation|knowledge|documentation|architecture)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnowledgeQuestion();
}
