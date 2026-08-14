using System.Collections.Concurrent;
using System.Text.Json;

namespace SentinelApp.Services;

public sealed class BrokerEvidenceStore
{
    private const int MaximumEntries = 250;
    private const int MaximumEvidenceCharacters = 32 * 1024;
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, EvidenceEntry> _entries = new();
    private readonly ConcurrentDictionary<SessionKey, Guid> _pending = new();

    public EvidenceReceipt Store(string owner, string sessionId, object sanitizedEvidence)
    {
        ValidateOwnerSession(owner, sessionId);
        RemoveExpired();
        if (_entries.Count >= MaximumEntries)
        {
            throw new InvalidOperationException("The in-memory sanitized-evidence limit has been reached.");
        }

        var serialized = JsonSerializer.Serialize(sanitizedEvidence);
        if (serialized.Length > MaximumEvidenceCharacters)
        {
            throw new InvalidOperationException("The sanitized evidence exceeds the broker size limit.");
        }
        using var document = JsonDocument.Parse(serialized);
        var evidence = document.RootElement.Clone();
        var handle = NewHandle();
        var expiresAt = DateTimeOffset.UtcNow.Add(EvidenceLifetime);
        var key = new SessionKey(owner, sessionId);
        if (_pending.TryRemove(key, out var previous))
        {
            _entries.TryRemove(previous, out _);
        }
        _entries[handle] = new EvidenceEntry(owner, sessionId, evidence, expiresAt);
        _pending[key] = handle;
        return new EvidenceReceipt(evidence, expiresAt);
    }

    public bool TryGetPendingHandle(string owner, string sessionId, out Guid handle)
    {
        RemoveExpired();
        return _pending.TryGetValue(new SessionKey(owner, sessionId), out handle);
    }

    public bool TryConsume(Guid handle, out JsonElement evidence)
    {
        evidence = default;
        if (handle == Guid.Empty)
        {
            return false;
        }
        RemoveExpired();
        if (!_entries.TryRemove(handle, out var entry))
        {
            return false;
        }
        _pending.TryRemove(new SessionKey(entry.Owner, entry.SessionId), out _);
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }
        evidence = entry.Evidence;
        return true;
    }

    public void RemoveSession(string owner, string sessionId)
    {
        var key = new SessionKey(owner, sessionId);
        if (_pending.TryRemove(key, out var handle))
        {
            _entries.TryRemove(handle, out _);
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now && _entries.TryRemove(pair.Key, out var removed))
            {
                _pending.TryRemove(new SessionKey(removed.Owner, removed.SessionId), out _);
            }
        }
    }

    private static Guid NewHandle()
    {
        Guid handle;
        do
        {
            handle = Guid.NewGuid();
        }
        while (handle == Guid.Empty);
        return handle;
    }

    private static void ValidateOwnerSession(string owner, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(owner) ||
            !Guid.TryParseExact(sessionId, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException("A valid owner and canonical session ID are required.");
        }
    }

    private sealed record SessionKey(string Owner, string SessionId);
    private sealed record EvidenceEntry(
        string Owner,
        string SessionId,
        JsonElement Evidence,
        DateTimeOffset ExpiresAt);
}

public sealed record EvidenceReceipt(JsonElement Evidence, DateTimeOffset ExpiresAt);
