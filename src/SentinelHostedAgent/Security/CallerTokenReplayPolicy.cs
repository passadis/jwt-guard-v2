using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace SentinelHostedAgent.Security;

public static partial class CallerTokenReplayPolicy
{
    public const string Refusal =
        "I can't replay, forward, inspect, or reuse a caller's signed-in token. " +
        "User-token replay is available only through SentinelApp's authenticated Enter the Gate BFF flow.";

    public static bool ShouldRefuse(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var latestUserText = messages
            .LastOrDefault(message => message.Role == ChatRole.User)
            ?.Text;

        return ShouldRefuse(latestUserText);
    }

    public static bool ShouldRefuse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (SafeBoundaryQuestion().IsMatch(input))
        {
            return false;
        }

        return CallerAction().IsMatch(input)
            && TokenReference().IsMatch(input)
            && CallerContext().IsMatch(input);
    }

    [GeneratedRegex(
        @"\b(replay|forward|reuse|relay|resend|send|pass|present|use|using|inspect|decode|read|analy[sz]e|attach|take|call|invoke)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CallerAction();

    [GeneratedRegex(
        @"\b(token|jwt|bearer|authorization|credential)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenReference();

    [GeneratedRegex(
        @"\b(my|mine|me|i|this|that|caller|user|signed[ -]?in|current|browser|session|incoming|received|provided|supplied|attached|pasted|this\s+request|authorization\s+header|(this|that|the)\s+(access\s+|bearer\s+|signed[ -]?in\s+)?(token|jwt|credential))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CallerContext();

    [GeneratedRegex(
        @"^\s*(why\s+is|explain\s+why|describe\s+why)\s+(caller[- ]token|user[- ]token|signed[ -]?in\s+token)\s+replay\s+(prohibited|forbidden|unsafe|not\s+allowed)\s*[?.]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeBoundaryQuestion();
}
