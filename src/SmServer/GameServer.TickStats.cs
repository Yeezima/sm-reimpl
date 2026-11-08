using System.Diagnostics;
using System.Globalization;
using System.Net;
using SmNet;

namespace SmServer;

/// <summary>
/// Test aid (TF9; any match): <c>SM_TICK_STATS=&lt;s&gt;</c> in the server's environment. Every that many seconds one <c>TICKSTATS</c> line: the wall time of a tick (the step and everything sent with it)
/// as mean, median, 99th percentile and worst, and the bytes the server sent per client per second. Nothing is measured and nothing is wrapped without the variable.
/// </summary>
public sealed partial class GameServer
{
    static readonly double TickStatsSeconds = double.TryParse(Environment.GetEnvironmentVariable("SM_TICK_STATS"), CultureInfo.InvariantCulture, out double s) && s > 0.0 ? s : 0.0;
    readonly List<double> _tickMs = new();
    readonly Dictionary<int, long> _sentTo = new();
    long _sentBytes, _sentLargest;
    readonly SortedDictionary<string, long> _sentByKind = new(StringComparer.Ordinal);   // musou/perf-pass: bytes by message kind, the envelope by section tag
    TimeSpan _tickStatsSince;

    IServerTransport Counted(IServerTransport transport) => TickStatsSeconds > 0.0 ? new CountingTransport(transport, this) : transport;

    // hotfix/musou-server-lag: the wall time between the starts of two ticks and between two snapshot sends (the pacing a client sees), and the ticks run in one update.
    readonly List<double> _tickGapMs = new(), _snapshotGapMs = new();
    long _lastTickStart, _lastSnapshotSent;
    int _updatesWithTicks, _updatesWithSeveral;
    readonly int[] _gcAt = new int[3];
    TimeSpan _gcPausedAt;
    long _allocatedAt;

    long TickStatsBegin()
    {
        if (TickStatsSeconds <= 0.0) return 0L;
        SmSim.SimProfile.On = true;   // musou/perf-pass: the tick's named sections (SmSim.SimProfile)
        long now = Stopwatch.GetTimestamp();
        if (_lastTickStart != 0L) _tickGapMs.Add(Stopwatch.GetElapsedTime(_lastTickStart, now).TotalMilliseconds);
        _lastTickStart = now;
        return now;
    }

    /// <summary>hotfix/musou-server-lag: a snapshot round was sent (its gap from the last one, for TICKPACE).</summary>
    void TickStatsSnapshot()
    {
        if (TickStatsSeconds <= 0.0) return;
        long now = Stopwatch.GetTimestamp();
        if (_lastSnapshotSent != 0L) _snapshotGapMs.Add(Stopwatch.GetElapsedTime(_lastSnapshotSent, now).TotalMilliseconds);
        _lastSnapshotSent = now;
    }

    /// <summary>hotfix/musou-server-lag: how many ticks one update ran (TICKPACE).</summary>
    void TickStatsUpdate(int ran)
    {
        if (TickStatsSeconds <= 0.0 || ran == 0) return;
        _updatesWithTicks++;
        if (ran > 1) _updatesWithSeveral++;
    }

    static string Spread(List<double> ms)
    {
        if (ms.Count == 0) return "none";
        ms.Sort();
        double At(double share) => ms[Math.Clamp((int)Math.Ceiling(share * ms.Count) - 1, 0, ms.Count - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"p1 {At(0.01):F1} p50 {At(0.5):F1} p99 {At(0.99):F1} max {At(1.0):F1}");
    }

    /// <summary>A tick slower than this (ms) prints its own sections (a TICKSPIKE line; musou/perf-pass).</summary>
    const double SpikeMs = 25.0;

    void TickStatsEnd(long started)
    {
        if (TickStatsSeconds <= 0.0) return;
        double tickMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _tickMs.Add(tickMs);
        if (tickMs >= SpikeMs) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TICKSPIKE tick {World.Tick}: {tickMs:F1} ms: {SmSim.SimProfile.TickReport()}"));
        SmSim.SimProfile.EndTick();
        var seconds = (_clock.Now - _tickStatsSince).TotalSeconds;
        if (seconds < TickStatsSeconds) return;
        _tickMs.Sort();
        double At(double share) => _tickMs[Math.Clamp((int)Math.Ceiling(share * _tickMs.Count) - 1, 0, _tickMs.Count - 1)];
        int clients = Math.Max(1, _sentTo.Count);
        long busiest = _sentTo.Count == 0 ? 0L : _sentTo.Values.Max();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TICKSTATS tick {World.Tick}: {_tickMs.Count} ticks, ms mean {_tickMs.Average():F3} p50 {At(0.5):F3} p99 {At(0.99):F3} max {At(1.0):F3}; {_sentTo.Count} clients, KB/s per client mean {_sentBytes / 1000.0 / seconds / clients:F1} busiest {busiest / 1000.0 / seconds:F1}, largest packet {_sentLargest} B; skipped ticks {SkippedTicks}"));
        if (SmSim.SimProfile.WindowReport() is { Length: > 0 } sections) Console.WriteLine($"TICKSECTIONS tick {World.Tick} (mean/worst ms a tick): {sections}");
        _tickMs.Clear();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TICKPACE tick {World.Tick}: ms between tick starts {Spread(_tickGapMs)}; between snapshots {Spread(_snapshotGapMs)}; updates running ticks {_updatesWithTicks}, with more than one {_updatesWithSeveral}; " +
            $"GC gen0 {GC.CollectionCount(0) - _gcAt[0]} gen1 {GC.CollectionCount(1) - _gcAt[1]} gen2 {GC.CollectionCount(2) - _gcAt[2]}, paused {(GC.GetTotalPauseDuration() - _gcPausedAt).TotalMilliseconds:F1} ms, " +
            $"allocated {(GC.GetTotalAllocatedBytes() - _allocatedAt) / 1e6 / seconds:F1} MB/s ({(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} GC, {System.Runtime.GCSettings.LatencyMode})"));
        _tickGapMs.Clear(); _snapshotGapMs.Clear(); _updatesWithTicks = _updatesWithSeveral = 0;
        for (int g = 0; g < 3; g++) _gcAt[g] = GC.CollectionCount(g);
        _gcPausedAt = GC.GetTotalPauseDuration();
        _allocatedAt = GC.GetTotalAllocatedBytes();
        if (_sentByKind.Count > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TICKBYTES tick {World.Tick}: KB/s per client by kind: ") + string.Join(", ", _sentByKind.OrderByDescending(kv => kv.Value).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value / 1000.0 / seconds / clients:F1}"))));
        _sentByKind.Clear();
        _sentTo.Clear();
        _sentBytes = _sentLargest = 0L;
        _tickStatsSince = _clock.Now;
    }

    /// <summary>The bytes of one packet by kind: the first byte, the envelope (16) by its sections' tags (musou/perf-pass).</summary>
    void CountKinds(byte[] packet)
    {
        if (packet.Length == 0) return;
        if (packet[0] != 16 || packet.Length < Protocol.ExtrasHeaderBytes) { Add($"kind {packet[0]}", packet.Length); return; }
        Add("envelope headers", Protocol.ExtrasHeaderBytes);
        int n = packet[5], at = Protocol.ExtrasHeaderBytes;
        for (int k = 0; k < n && at + Protocol.ExtrasSectionHeaderBytes <= packet.Length; k++)
        {
            int length = packet[at + 1] | (packet[at + 2] << 8);
            Add($"tag {packet[at]}", Protocol.ExtrasSectionHeaderBytes + length);
            at += Protocol.ExtrasSectionHeaderBytes + length;
        }
        void Add(string key, int bytes) => _sentByKind[key] = _sentByKind.GetValueOrDefault(key) + bytes;
    }

    void Count(int connection, int bytes)
    {
        _sentTo[connection] = _sentTo.GetValueOrDefault(connection) + bytes;
        _sentBytes += bytes;
        _sentLargest = Math.Max(_sentLargest, bytes);
    }

    sealed class CountingTransport(IServerTransport inner, GameServer server) : IServerTransport
    {
        public void Send(int connection, byte[] packet) { server.Count(connection, packet.Length); server.CountKinds(packet); inner.Send(connection, packet); }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => inner.EndPointOf(connection);
    }
}
