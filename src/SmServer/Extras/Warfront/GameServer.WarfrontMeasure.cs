using System.Diagnostics;
using System.Globalization;
using SmContent.Extras.Warfront;

namespace SmServer;

/// <summary>
/// Test aid <c>SM_WF_PREPARE_TEST=&lt;map&gt;</c> (slice W5a; off by default, logged at start): ten seconds after the stage is up the server prepares that map's caches in this process exactly as a deployment's
/// preparation does (off the tick thread), and measures it: the seconds it took, the peak working set of the process (two maps in memory), and the interval between ticks while it ran (p50, p99, max). No deployment follows.
/// </summary>
public sealed partial class GameServer
{
    static readonly string? WarfrontPrepareTestMap = Environment.GetEnvironmentVariable("SM_WF_PREPARE_TEST") is { Length: > 0 } m ? m : null;

    sealed class WarfrontMeasure
    {
        public required string Map;
        public required Stopwatch Clock;
        public required long WorkingSetBefore;
        public readonly List<double> Intervals = new();
        public volatile bool Done;
        public string? Note, Error;
        public long LastTicks;
        public double PeakSampledMb;
    }

    WarfrontMeasure? _wfMeasure;
    bool _wfMeasureStarted;

    void WarfrontMeasureStep(WarfrontServerState s)
    {
        if (WarfrontPrepareTestMap is not { } map) return;
        if (_wfMeasure is null)
        {
            if (_wfMeasureStarted || World.Tick < (uint)(10 * WarfrontRate)) return;
            _wfMeasureStarted = true;
            if (!WarfrontMaps.IsWarfrontMap(map)) { WfLog($"SM_WF_PREPARE_TEST: {map} is not a Warfront map"); return; }
            var m = new WarfrontMeasure { Map = map, Clock = Stopwatch.StartNew(), WorkingSetBefore = Process.GetCurrentProcess().WorkingSet64, LastTicks = Stopwatch.GetTimestamp() };
            _wfMeasure = m;
            var config = World.Config;
            WfLog($"SM_WF_PREPARE_TEST: preparing {map} in this process; working set before {m.WorkingSetBefore / 1048576} MB");
            Task.Run(() =>
            {
                try { m.Note = WarfrontPrebuild(map, config, null); }
                catch (Exception e) when (e is not OutOfMemoryException) { m.Error = e.Message; }
                m.Done = true;
            });
            return;
        }
        var me = _wfMeasure;
        long now = Stopwatch.GetTimestamp();
        if (!me.Done)
        {
            me.Intervals.Add((now - me.LastTicks) * 1000.0 / Stopwatch.Frequency);
            me.LastTicks = now;
            if (me.Intervals.Count % 30 == 0) me.PeakSampledMb = Math.Max(me.PeakSampledMb, Process.GetCurrentProcess().WorkingSet64 / 1048576.0);
            return;
        }
        if (WarfrontMeasureReported) return;
        WarfrontMeasureReported = true;
        var p = Process.GetCurrentProcess();
        var sorted = me.Intervals.OrderBy(x => x).ToArray();
        double Pct(double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        WfLog(string.Create(CultureInfo.InvariantCulture,
            $"SM_WF_PREPARE_TEST result: {me.Map} {(me.Error is null ? "prepared" : "FAILED: " + me.Error)} in {me.Clock.Elapsed.TotalSeconds:0.0} s; peak working set {p.PeakWorkingSet64 / 1048576} MB (before {me.WorkingSetBefore / 1048576} MB); " +
            $"tick interval over {sorted.Length} ticks: p50 {Pct(0.5):0.0} ms, p99 {Pct(0.99):0.0} ms, max {(sorted.Length == 0 ? 0 : sorted[^1]):0.0} ms (a tick is {1000.0 / WarfrontRate:0.0} ms); {me.Note}"));
    }

    bool WarfrontMeasureReported;
}
