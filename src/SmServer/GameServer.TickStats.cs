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

    static long TickStatsBegin()
    {
        if (TickStatsSeconds <= 0.0) return 0L;
        SmSim.SimProfile.On = true;   // musou/perf-pass: the tick's named sections (SmSim.SimProfile)
        return Stopwatch.GetTimestamp();
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
