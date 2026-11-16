using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SmSim;

/// <summary>
/// Test aid (musou/perf-pass, 2026-11-08): named sections of the server's tick, timed when <see cref="On"/> (the server sets it with <c>SM_TICK_STATS</c>; off everywhere else, where
/// <see cref="Begin"/> returns 0 and <see cref="End"/> returns at once). The server's TICKSTATS line adds each section's mean and worst ms over its window, and a slow tick prints its own
/// sections (<see cref="TickReport"/>), so a spike says where it went. Reads the clock only; nothing of the simulation changes. One server's main thread uses it (not thread-safe).
/// </summary>
public static class SimProfile
{
    public static bool On { get; set; }

    sealed class Acc { public double Sum, Max, Tick; public int Count; }
    static readonly Dictionary<string, Acc> Sections = new(StringComparer.Ordinal);
    static int _ticks;

    public static long Begin() => On ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Charges the time since <paramref name="started"/> (from <see cref="Begin"/>) to <paramref name="name"/>.</summary>
    public static void End(string name, long started)
    {
        if (!On || started == 0L) return;
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!Sections.TryGetValue(name, out var a)) Sections[name] = a = new Acc();
        a.Sum += ms; a.Tick += ms; a.Count++;
        if (a.Tick > a.Max) a.Max = a.Tick;
    }

    /// <summary>A tick ends: per-tick sums start again (the window's worst is a whole tick's share).</summary>
    public static void EndTick()
    {
        if (!On) return;
        _ticks++;
        foreach (var a in Sections.Values) { if (a.Tick > a.Max) a.Max = a.Tick; a.Tick = 0; }
    }

    /// <summary>This tick's sections over 1 ms, largest first (for a slow tick's line); call before <see cref="EndTick"/>.</summary>
    public static string TickReport()
    {
        var sb = new StringBuilder();
        foreach (var (k, a) in Sections.Where(s => s.Value.Tick >= 1.0).OrderByDescending(s => s.Value.Tick))
            sb.Append(CultureInfo.InvariantCulture, $"{(sb.Length > 0 ? ", " : "")}{k} {a.Tick:F1}");
        return sb.ToString();
    }

    /// <summary>The window's sections (mean ms a tick, worst ms in one tick), largest mean first; then they start again.</summary>
    public static string WindowReport()
    {
        if (_ticks == 0) return "";
        var sb = new StringBuilder();
        foreach (var (k, a) in Sections.OrderByDescending(s => s.Value.Sum))
            sb.Append(CultureInfo.InvariantCulture, $"{(sb.Length > 0 ? "; " : "")}{k} {a.Sum / _ticks:F2}/{a.Max:F1}");
        Sections.Clear();
        _ticks = 0;
        return sb.ToString();
    }
}
