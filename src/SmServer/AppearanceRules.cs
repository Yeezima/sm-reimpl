using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// Which unlocks the server allows (docs/customizer-architecture.md section 2.8): <see cref="All"/>, the only working mode while there is no
/// progression; every level gate stays in the catalogue for later.
/// </summary>
public enum UnlockMode : byte { All = 0 }

/// <summary>A token bucket (ours, for the abuse limits): <see cref="Rate"/> tokens per second up to <see cref="Burst"/>.</summary>
public sealed class TokenBucket(double rate, double burst)
{
    double _tokens = burst;
    TimeSpan _last = TimeSpan.MinValue;
    public double Rate { get; } = rate;
    public double Burst { get; } = burst;

    /// <summary>Takes <paramref name="count"/> tokens when that many are there.</summary>
    public bool TryTake(TimeSpan now, double count = 1)
    {
        if (_last != TimeSpan.MinValue) _tokens = Math.Min(Burst, _tokens + Math.Max(0, (now - _last).TotalSeconds) * Rate);
        _last = now;
        if (_tokens < count) return false;
        _tokens -= count;
        return true;
    }
}

/// <summary>
/// The server's emblem cache (docs/customizer-architecture.md section 4): verified blobs by hash, at most <see cref="Capacity"/>; when full,
/// the least recently used blob no connected player references goes. Blobs in use are pinned and never evicted (a full cache of pinned
/// blobs refuses a new one).
/// </summary>
public sealed class EmblemCache(int capacity)
{
    readonly Dictionary<EmblemHash, (byte[] Data, long Used)> _blobs = new();
    long _clock;

    public int Capacity { get; } = capacity;
    public int Count => _blobs.Count;
    public bool Contains(EmblemHash hash) => _blobs.ContainsKey(hash);

    /// <summary>The blob, marking it used; null when not cached.</summary>
    public byte[]? Get(EmblemHash hash)
    {
        if (!_blobs.TryGetValue(hash, out var e)) return null;
        _blobs[hash] = (e.Data, ++_clock);
        return e.Data;
    }

    /// <summary>Adds a verified blob (the caller verified it); evicts unpinned blobs as needed. False when every cached blob is pinned.</summary>
    public bool Add(EmblemHash hash, byte[] data, Func<EmblemHash, bool> pinned)
    {
        if (_blobs.ContainsKey(hash)) { _blobs[hash] = (data, ++_clock); return true; }
        while (_blobs.Count >= Capacity)
        {
            var victim = _blobs.Where(kv => !pinned(kv.Key)).OrderBy(kv => kv.Value.Used).Select(kv => (EmblemHash?)kv.Key).FirstOrDefault();
            if (victim is not { } v) return false;
            _blobs.Remove(v);
        }
        _blobs[hash] = (data, ++_clock);
        return true;
    }
}
