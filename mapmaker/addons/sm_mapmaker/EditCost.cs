using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SmMapmakerEditor;

/// <summary>
/// What one edit costs (the edit-hang fix, 2026-11-27): counters of the work the view does per edit (always kept; a few integer adds) and, while
/// <see cref="Enabled"/> (the perf test aid <c>--mm-perf</c>), the time spent in each named phase. The perf test resets them around one edit and checks
/// that the work follows the edited objects, not the size of the map.
/// </summary>
public static class EditCost
{
    public static bool Enabled { get; set; }

    /// <summary>Object nodes whose transform, name, parent or visibility the view wrote (a sync that finds a node unchanged writes nothing).</summary>
    public static int NodesWritten;
    /// <summary>Objects whose drawn content was rebuilt.</summary>
    public static int ContentRebuilt;
    /// <summary>Objects a view sync looked at (the incremental sync visits only the changed ones).</summary>
    public static int NodesVisited;
    /// <summary>Full view syncs (every object visited): opening a map, a library or SM1 change, a step that touched many objects.</summary>
    public static int FullSyncs;
    /// <summary>Rows the dock's Scene lists created.</summary>
    public static int SceneRows;
    /// <summary>Times the quick map check ran.</summary>
    public static int Checks;
    /// <summary>Collision worlds built on the main thread (the placement aim; a map of plain pieces builds its own at once, a decorated map never should).</summary>
    public static int MainWorldBuilds;
    /// <summary>Map compiles on the main thread.</summary>
    public static int MainCompiles;

    static readonly Dictionary<string, (double Ms, int Count)> Phases = new();

    public static void Reset()
    {
        NodesWritten = ContentRebuilt = NodesVisited = FullSyncs = SceneRows = Checks = MainWorldBuilds = MainCompiles = 0;
        lock (Phases) Phases.Clear();
    }

    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(string phase, long start)
    {
        if (!Enabled || start == 0) return;
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        lock (Phases) Phases[phase] = Phases.TryGetValue(phase, out var p) ? (p.Ms + ms, p.Count + 1) : (ms, 1);   // the background check reports from its own thread
    }

    public static string Counters() => $"visited {NodesVisited}, written {NodesWritten}, rebuilt {ContentRebuilt}, full syncs {FullSyncs}, scene rows {SceneRows}, checks {Checks}, main-thread world builds {MainWorldBuilds}, main-thread compiles {MainCompiles}";

    /// <summary>The phases that took the longest, "name ms (count)", most expensive first.</summary>
    public static string Top(int n = 8) { lock (Phases) return TopCore(n); }
    static string TopCore(int n) =>
        string.Join("; ", Phases.OrderByDescending(p => p.Value.Ms).Take(n).Select(p => $"{p.Key} {p.Value.Ms:0.0} ms ({p.Value.Count})"));
}
