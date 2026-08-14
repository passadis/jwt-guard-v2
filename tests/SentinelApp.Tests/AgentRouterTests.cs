using Microsoft.Extensions.Logging.Abstractions;
using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class AgentRouterTests
{
    private const string Owner = "11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222";
    private const string Session = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task EmbeddedModeNeverCallsHosted()
    {
        var embedded = new FakeEmbedded("embedded");
        var hosted = new FakeHosted("hosted");
        var output = await Collect(Create(AgentMode.Embedded, embedded, hosted)
            .StreamAsync(Owner, Session, "Explain routing."));
        Assert.Equal("embedded", output);
        Assert.Equal(1, embedded.Calls);
        Assert.Equal(0, hosted.Calls);
    }

    [Fact]
    public async Task HostedModeDoesNotSilentlyFallback()
    {
        var embedded = new FakeEmbedded("embedded");
        var hosted = new FakeHosted("hosted");
        var output = await Collect(Create(AgentMode.Hosted, embedded, hosted)
            .StreamAsync(Owner, Session, "Explain routing."));
        Assert.Equal("hosted", output);
        Assert.Equal(0, embedded.Calls);
        Assert.Equal(1, hosted.Calls);
    }

    [Fact]
    public async Task ShadowReturnsOnlyEmbeddedContent()
    {
        var embedded = new FakeEmbedded("embedded");
        var hosted = new FakeHosted("hosted");
        var output = await Collect(Create(AgentMode.HostedShadow, embedded, hosted)
            .StreamAsync(Owner, Session, "Why is the listener isolated?"));
        await hosted.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("embedded", output);
        Assert.Equal(1, hosted.Calls);
    }

    [Fact]
    public async Task ShadowSkipsScenarioAndTokenInputs()
    {
        var embedded = new FakeEmbedded("embedded");
        var hosted = new FakeHosted("hosted");
        var router = Create(AgentMode.HostedShadow, embedded, hosted);
        _ = await Collect(router.StreamAsync(Owner, Session, "Run the tampered scenario."));
        _ = await Collect(router.StreamAsync(Owner, Session, "Inspect aaaaaaaa.bbbbbbbb.cccccccc"));
        await Task.Delay(50);
        Assert.Equal(0, hosted.Calls);
    }

    [Fact]
    public async Task ShadowSkipsNonAllowlistedOwner()
    {
        var embedded = new FakeEmbedded("embedded");
        var hosted = new FakeHosted("hosted");
        var output = await Collect(Create(AgentMode.HostedShadow, embedded, hosted)
            .StreamAsync(
                "11111111-1111-1111-1111-111111111111:44444444-4444-4444-4444-444444444444",
                Session,
                "Explain the protected listener."));
        await Task.Delay(50);
        Assert.Equal("embedded", output);
        Assert.Equal(0, hosted.Calls);
    }

    private static AgentRouter Create(
        AgentMode mode,
        IEmbeddedGateExplainer embedded,
        IHostedGateExplainer hosted) =>
        new(
            GateForwarderTests.TestOptions() with
            {
                AgentMode = mode,
                HostedShadowTesterObjectIds = new HashSet<Guid>
                {
                    Guid.ParseExact("22222222-2222-2222-2222-222222222222", "D"),
                },
            },
            embedded,
            hosted,
            NullLogger<AgentRouter>.Instance);

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }
        return string.Concat(chunks);
    }

    private abstract class FakeExplainer(string output) : IGateExplainer
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<string> StreamAsync(
            string owner,
            string sessionId,
            string message,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Invoked.TrySetResult();
            await Task.Yield();
            yield return output;
        }

        public ValueTask ResetSessionAsync(
            string owner,
            string sessionId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeEmbedded(string output) : FakeExplainer(output), IEmbeddedGateExplainer;
    private sealed class FakeHosted(string output) : FakeExplainer(output), IHostedGateExplainer;
}
