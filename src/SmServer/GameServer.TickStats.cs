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
    TimeSpan _tickStatsSince;

    IServerTransport Counted(IServerTransport transport) => TickStatsSeconds > 0.0 ? new CountingTransport(transport, this) : transport;

    static long TickStatsBegin() => TickStatsSeconds > 0.0 ? Stopwatch.GetTimestamp() : 0L;

    void TickStatsEnd(long started)
    {
        if (TickStatsSeconds <= 0.0) return;
        _tickMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var seconds = (_clock.Now - _tickStatsSince).TotalSeconds;
        if (seconds < TickStatsSeconds) return;
        _tickMs.Sort();
        double At(double share) => _tickMs[Math.Clamp((int)Math.Ceiling(share * _tickMs.Count) - 1, 0, _tickMs.Count - 1)];
        int clients = Math.Max(1, _sentTo.Count);
        long busiest = _sentTo.Count == 0 ? 0L : _sentTo.Values.Max();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TICKSTATS tick {World.Tick}: {_tickMs.Count} ticks, ms mean {_tickMs.Average():F3} p50 {At(0.5):F3} p99 {At(0.99):F3} max {At(1.0):F3}; {_sentTo.Count} clients, KB/s per client mean {_sentBytes / 1000.0 / seconds / clients:F1} busiest {busiest / 1000.0 / seconds:F1}, largest packet {_sentLargest} B; skipped ticks {SkippedTicks}"));
        _tickMs.Clear();
        _sentTo.Clear();
        _sentBytes = _sentLargest = 0L;
        _tickStatsSince = _clock.Now;
    }

    void Count(int connection, int bytes)
    {
        _sentTo[connection] = _sentTo.GetValueOrDefault(connection) + bytes;
        _sentBytes += bytes;
        _sentLargest = Math.Max(_sentLargest, bytes);
    }

    sealed class CountingTransport(IServerTransport inner, GameServer server) : IServerTransport
    {
        public void Send(int connection, byte[] packet) { server.Count(connection, packet.Length); inner.Send(connection, packet); }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => inner.EndPointOf(connection);
    }
}
