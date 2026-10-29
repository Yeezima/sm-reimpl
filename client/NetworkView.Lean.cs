using System;
using System.Globalization;
using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// perf/lean-bots: a headless test bot loads only what its simulation, prediction, navigation and aiming read (<see cref="LeanBot"/>, ours;
/// spec "Test bots"). Nobody sees or hears a headless bot, so the map's look, lighting, effects, looks, weapon models, ragdolls, decals,
/// pickup models, the Ork presentation (an Ork is the placeholder marine), the loyalist barks' takes, the pose data (hip fire, locomotion, turns, falls, upper body,
/// brace and vent, sprint edges), the HUD art, animation prefetch and the client's JIT
/// prewarm are skipped, and the heap is compacted after the join; <c>--bot-full-load</c>, or any presentation test aid (<see cref="LeanBot.PresentationAids"/>),
/// loads everything as before. Kept: the content tuning (the config hash), the map's collision, the navigation graph, the camera rig and the placeholder
/// marine (characters still exist, so every per-frame path runs as before; clips decode when first shown).
/// The memory report (<c>--mem-report [s]</c>; lean bots: always once, 30 s after joining) prints the process and heap figures the change was measured with.
/// </summary>
public partial class NetworkView
{
    /// <summary>True for a lean headless bot (<see cref="LeanBot.Decide"/>): presentation-only loads are skipped.</summary>
    internal bool Lean => _lean ??= LeanBot.Decide(_bot, DisplayServer.GetName(), OS.GetCmdlineUserArgs());
    bool? _lean;

    /// <summary>
    /// The lean end of <see cref="LoadMap"/>: what the simulation, the prediction, the bot's navigation and line of sight and the flight pose's ground probe read
    /// (the collision world, built before this) and nothing else: no map meshes, textures, lighting, grey collision, blocker planes, props, ragdoll world,
    /// look or weapon warm-ups. The returned map (collision and hash) is the one the full load returns, so the handshake's map check and the prediction are the same.
    /// </summary>
    LoadedMap LeanMapLoaded(string name, SmContent.MapContent map, SmSim.CollisionWorld world, long collisionMs, System.Diagnostics.Stopwatch watch)
    {
        _jumpGround = world;   // M45: the flight pose's height above the ground (the same probe as the full load)
        _mapNote = $"map {name}: collision only (lean bot: nothing is drawn; --{LeanBot.FullLoadFlag} loads the visuals)";
        GD.Print($"{_mapNote}; collision {collisionMs} ms, total {watch.ElapsedMilliseconds} ms");
        return new LoadedMap(world, map.Hash);
    }

    /// <summary>
    /// Every frame of a lean bot: the queues the skipped presentation would have drained (looks: appearance changes and emblem arrivals) are emptied, so
    /// they do not grow over a long match.
    /// </summary>
    void LeanFrame()
    {
        if (!Lean || _client is null) return;
        if (_looks is null) { _client.TakeAppearanceChanges(); _client.TakeEmblemArrivals(); }
        // The start's reads (content, collision, navigation graph) leave the GC heap committed well above what stays live; one aggressive, compacting
        // collection after the join's work gives it back (a blocking pause of tens of ms, which a bot does not feel; ours).
        if (_leanCompactions < LeanCompactAt.Length && _connectedFor >= LeanCompactAt[_leanCompactions])
        {
            _leanCompactions++;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GD.Print($"t {_connectedFor:F1} s: lean bot: heap compacted in {watch.Elapsed.TotalMilliseconds:F0} ms (managed {GC.GetTotalMemory(false) / 1048576.0:F1} MB, committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0:F1} MB)");
        }
    }

    /// <summary>Seconds after joining at which a lean bot compacts its heap (after the join's loads, and once more when the match is under way).</summary>
    static readonly double[] LeanCompactAt = [5.0, 25.0];
    int _leanCompactions;

    /// <summary><c>--mem-steps</c> (test aid): the live managed heap after a full collection at each named step of the start (where the memory goes).</summary>
    static readonly bool MemSteps = CmdFlag("mem-steps");
    long _memStepLast;

    void MemStep(string step)
    {
        if (!MemSteps) return;
        GC.Collect();
        long now = GC.GetTotalMemory(false);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        GD.Print($"mem step {step}: managed {now / 1048576.0:F1} MB ({(now - _memStepLast) / 1048576.0:+0.0;-0.0} MB), Godot static {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F1} MB, " +
                 $"working set {process.WorkingSet64 / 1048576.0:F1} MB, private {process.PrivateMemorySize64 / 1048576.0:F1} MB");
        _memStepLast = now;
    }

    double _memReportAt = -1;
    bool _memReported;

    /// <summary>Once, <c>--mem-report</c> seconds (default 30) after joining; lean bots always report once at 30 s.</summary>
    void MemoryReportFrame()
    {
        if (_memReported) return;
        if (_memReportAt < 0)
        {
            bool asked = CmdFlag("mem-report");
            if (!asked && !Lean) { _memReported = true; return; }   // a full-load or probe bot is not paused by a collection it did not ask for
            _memReportAt = CmdArg("mem-report") is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var at) && at >= 0 ? at : 30.0;
        }
        if (_connectedFor < _memReportAt) return;
        _memReported = true;
        GD.Print($"t {_connectedFor:F1} s: memory ({(Lean ? "lean bot" : "full load")}): {MemoryText()}");
    }

    static string MemoryText()
    {
        GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        static string Mb(double bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
        return $"working set {Mb(process.WorkingSet64)} MB (peak {Mb(process.PeakWorkingSet64)} MB), private {Mb(process.PrivateMemorySize64)} MB; " +
               $"managed heap {Mb(GC.GetTotalMemory(false))} MB (GC heap {Mb(gc.HeapSizeBytes)} MB, committed {Mb(gc.TotalCommittedBytes)} MB); " +
               $"Godot static {Mb(Performance.GetMonitor(Performance.Monitor.MemoryStatic))} MB, " +
               $"textures {Mb(Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed))} MB, buffers {Mb(Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed))} MB; " +
               $"objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}, resources {Performance.GetMonitor(Performance.Monitor.ObjectResourceCount):F0}, nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0}";
    }
}
