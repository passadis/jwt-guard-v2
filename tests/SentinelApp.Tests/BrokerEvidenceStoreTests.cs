using SentinelApp.Services;
using Xunit;

namespace SentinelApp.Tests;

public sealed class BrokerEvidenceStoreTests
{
    private const string OwnerA = "11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222";
    private const string OwnerB = "11111111-1111-1111-1111-111111111111:33333333-3333-3333-3333-333333333333";
    private const string Session = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public void EvidenceIsServerHandledAndSingleUse()
    {
        var store = new BrokerEvidenceStore();
        var receipt = store.Store(OwnerA, Session, new { validFormat = true, aud = "api://safe" });
        Assert.True(receipt.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(store.TryGetPendingHandle(OwnerA, Session, out var handle));
        Assert.NotEqual(Guid.Empty, handle);
        Assert.True(store.TryConsume(handle, out var evidence));
        Assert.True(evidence.GetProperty("validFormat").GetBoolean());
        Assert.False(store.TryConsume(handle, out _));
        Assert.False(store.TryGetPendingHandle(OwnerA, Session, out _));
    }

    [Fact]
    public void PendingHandleIsBoundToOwnerAndBrowserSession()
    {
        var store = new BrokerEvidenceStore();
        store.Store(OwnerA, Session, new { safe = true });
        Assert.False(store.TryGetPendingHandle(OwnerB, Session, out _));
        Assert.False(store.TryGetPendingHandle(OwnerA, "55555555-5555-5555-5555-555555555555", out _));
    }

    [Fact]
    public void EmptyAndUnknownHandlesAreRejected()
    {
        var store = new BrokerEvidenceStore();
        Assert.False(store.TryConsume(Guid.Empty, out _));
        Assert.False(store.TryConsume(Guid.NewGuid(), out _));
    }
}
