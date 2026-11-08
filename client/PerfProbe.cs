using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// perf/pass-1 test aid (<c>--perf-probe</c>): every 5 s one line with the engine's own counters next to the frame time, so a run says where a frame goes
/// beyond the client's phases (<c>--frame-profile</c>): the renderer's CPU and GPU time for the main viewport, draw calls, objects and primitives drawn,
/// video, texture and buffer memory, the physics step and its active bodies, the node count, the managed heap, the bytes the .NET runtime allocated
/// per frame and its collections, the process's working set, and a count of the scene's lights (with shadows), decals, particle and mesh nodes. At the
/// end a summary of all windows after the first 8 s. Reads only; nothing in the game changes (the viewport's render-time measurement is switched on,
/// as <c>--frame-stats</c> does).
/// </summary>
sealed class PerfProbe
{
    public static readonly bool On = OS.GetCmdlineUserArgs().Contains("--perf-probe");
    /// <summary>
    /// musou/perf-pass test aid: <c>--perf-ab cells|grime|fsr|fsr-native</c> (with <c>--perf-probe</c>) switches one change on and off every window (5 s), so both meet the same scene and load:
    /// <c>cells</c> the crowd's far batches per cell (off: one batch per look), <c>grime</c> Ashfall's overlay grime pass on the kit models, <c>fsr</c> FSR 2 Quality (off: the starting scaling and
    /// anti-aliasing), <c>fsr-native</c> FSR 2 Native AA. The summary gives each state's frames, renderer CPU and GPU, draw calls and primitives.
    /// </summary>
    static readonly string? Ab = Arg("perf-ab");
    /// <summary><c>--perf-ab-shots &lt;dir&gt;</c>: a PNG just before each switch and 0.6 s after it (the same view either side, for a visual comparison).</summary>
    static readonly string? AbShots = Arg("perf-ab-shots");
    double _abShotAt = -1;
    int _abSwitches;
    bool _abOn;
    readonly Dictionary<bool, (List<float> Frames, double Cpu, double Gpu, double Draw, double Prim, int N)> _abStats = new();
    readonly SmClient.Customizer.Model.UpscaleChoice _abStartUpscale = RenderQuality.Upscale;

    static string? Arg(string name)
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, "--" + name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    void AbShot(string when)
    {
        if (AbShots is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(AbShots);
            var path = System.IO.Path.Combine(AbShots, $"{Ab}-{_abSwitches:00}-{when}-{(_abOn ? "on" : "off")}.png");
            _viewport.GetTexture().GetImage().SavePng(path);
        }
        catch (Exception e) { GD.PrintErr($"perf ab shot: {e.Message}"); }
    }

    /// <summary>The A/B's change on or off now.</summary>
    void AbSet(bool on)
    {
        _abOn = on;
        switch (Ab)
        {
            case "cells": CrowdView.OneBatch = !on; break;
            case "fxcull": FxPlayer.CullOffScreen = on; break;
            case "grime": AshfallGrime.SetOverlay(on); break;
            case "grimefold": AshfallGrime.SetFolded(on); break;   // the grime folded into the kit's materials against the overlay
            case "fsr" or "fsr-native":
                RenderQuality.ForceUpscale(on ? (Ab == "fsr" ? SmClient.Customizer.Model.UpscaleChoice.Fsr2Quality : SmClient.Customizer.Model.UpscaleChoice.Fsr2Native) : _abStartUpscale);
                GD.Print(RenderQuality.Apply(_viewport));
                break;
        }
        GD.Print($"perf ab: {Ab} {(on ? "on" : "off")} at {_total:F0} s");
    }
    const double WindowSeconds = 5.0, WarmupSeconds = 8.0;

    readonly Viewport _viewport;
    readonly List<float> _frames = new();
    readonly List<string> _lines = new();
    double _elapsed, _total;
    long _allocStart = GC.GetTotalAllocatedBytes(precise: false);
    int _gc0 = GC.CollectionCount(0), _gc2 = GC.CollectionCount(2);
    double _cpuSum, _gpuSum, _drawSum, _objSum, _primSum, _physSum;
    int _samples;
    // After the warm-up, for the summary.
    readonly List<float> _after = new();
    double _aCpu, _aGpu, _aDraw, _aObj, _aPhys; long _aAlloc; int _aSamples, _aGc0, _aGc2;

    public PerfProbe(Viewport viewport)
    {
        _viewport = viewport;
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
    }

    static double M(Performance.Monitor m) => Performance.GetMonitor(m);

    public void Frame(double delta, Node root)
    {
        _total += delta; _elapsed += delta;
        float ms = (float)(delta * 1000.0);
        _frames.Add(ms);
        var rid = _viewport.GetViewportRid();
        double cpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid) + RenderingServer.GetFrameSetupTimeCpu();
        double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        double draws = M(Performance.Monitor.RenderTotalDrawCallsInFrame), objects = M(Performance.Monitor.RenderTotalObjectsInFrame);
        double prims = M(Performance.Monitor.RenderTotalPrimitivesInFrame), phys = M(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        _cpuSum += cpu; _gpuSum += gpu; _drawSum += draws; _objSum += objects; _primSum += prims; _physSum += phys; _samples++;
        if (_total > WarmupSeconds) { _after.Add(ms); _aCpu += cpu; _aGpu += gpu; _aDraw += draws; _aObj += objects; _aPhys += phys; _aSamples++; }
        if (Ab is not null && _total > WarmupSeconds && _elapsed > 0.5)   // the first half second of a window settles after the switch
        {
            var st = _abStats.TryGetValue(_abOn, out var v) ? v : (new List<float>(), 0, 0, 0, 0, 0);
            st.Frames.Add(ms);
            _abStats[_abOn] = (st.Frames, st.Cpu + cpu, st.Gpu + gpu, st.Draw + draws, st.Prim + prims, st.N + 1);
        }
        if (_abShotAt > 0 && _total >= _abShotAt) { _abShotAt = -1; AbShot("after"); }
        if (_elapsed < WindowSeconds) return;

        long alloc = GC.GetTotalAllocatedBytes(precise: false);
        int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
        var sorted = _frames.OrderBy(f => f).ToList();
        double mean = _frames.Average(), p99 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.99))];
        int n = _samples;
        var (lights, shadowed, decals, particles, meshes) = CountScene(root);
        var size = _viewport.GetVisibleRect().Size;
        string line = $"perf: {_total:F0} s: {size.X:F0}x{size.Y:F0}, vsync {DisplayServer.WindowGetVsyncMode()}, max fps {Engine.MaxFps}: {n} frames, mean {mean:F1} ms ({1000.0 / mean:F0} fps), p99 {p99:F1}, worst {sorted[^1]:F1}; render cpu {_cpuSum / n:F2} ms gpu {_gpuSum / n:F2} ms; "
                      + $"draw calls {_drawSum / n:F0}, objects {_objSum / n:F0}, primitives {_primSum / n / 1000.0:F0}k; physics step {_physSum / n:F2} ms, active bodies {M(Performance.Monitor.Physics3DActiveObjects):F0}; "
                      + $"vram {M(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F0} MB (textures {M(Performance.Monitor.RenderTextureMemUsed) / 1048576.0:F0}, buffers {M(Performance.Monitor.RenderBufferMemUsed) / 1048576.0:F0}); "
                      + $"nodes {M(Performance.Monitor.ObjectNodeCount):F0}; lights {lights} ({shadowed} with shadows), decals {decals}, particle nodes {particles}, mesh instances {meshes}; "
                      + $"managed heap {GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0:F0} MB, allocated {(alloc - _allocStart) / (double)n / 1024.0:F1} KB/frame, gc gen0 +{gc0 - _gc0} gen2 +{gc2 - _gc2}; "
                      + $"working set {System.Environment.WorkingSet / 1048576.0:F0} MB";
        GD.Print(line);
        if (_total > WarmupSeconds) { _aAlloc += alloc - _allocStart; _aGc0 += gc0 - _gc0; _aGc2 += gc2 - _gc2; }
        _allocStart = alloc; _gc0 = gc0; _gc2 = gc2;
        if (Ab is not null && _total > WarmupSeconds)
        {
            AbShot("before");
            AbSet(!_abOn);
            _abSwitches++;
            _abShotAt = _total + 0.6;
        }
        _frames.Clear(); _elapsed = 0; _cpuSum = _gpuSum = _drawSum = _objSum = _primSum = _physSum = 0; _samples = 0;
    }

    /// <summary>The summary of every frame after the first 8 s (printed when the view leaves the tree).</summary>
    public void PrintSummary()
    {
        foreach (var (on, st) in _abStats.OrderBy(kv => kv.Key))
        {
            var f = st.Frames.OrderBy(x => x).ToList();
            if (f.Count == 0) continue;
            GD.Print($"perf ab SUMMARY {Ab} {(on ? "on " : "off")}: {f.Count} frames, mean {f.Average():F2} ms, p50 {f[f.Count / 2]:F1} p99 {f[(int)Math.Min(f.Count - 1, f.Count * 0.99)]:F1} worst {f[^1]:F1}; render cpu {st.Cpu / st.N:F2} ms gpu {st.Gpu / st.N:F2} ms; draw calls {st.Draw / st.N:F0}, primitives {st.Prim / st.N / 1000.0:F0}k");
        }
        if (_after.Count == 0 || _aSamples == 0) return;
        var sorted = _after.OrderBy(f => f).ToList();
        double P(double p) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * p))];
        double mean = _after.Average();
        GD.Print($"perf: SUMMARY after {WarmupSeconds:F0} s: {sorted.Count} frames, mean {mean:F2} ms ({1000.0 / mean:F0} fps), p50 {P(0.5):F1} p99 {P(0.99):F1} p99.9 {P(0.999):F1} worst {sorted[^1]:F1} ms, "
                 + $"{sorted.Count(f => f > 33.0)} over 33 ms, {sorted.Count(f => f > 16.7)} over 16.7 ms; render cpu {_aCpu / _aSamples:F2} ms gpu {_aGpu / _aSamples:F2} ms; draw calls {_aDraw / _aSamples:F0}, "
                 + $"objects {_aObj / _aSamples:F0}; physics step {_aPhys / _aSamples:F2} ms; allocated {_aAlloc / (double)_aSamples / 1024.0:F1} KB/frame, gc gen0 +{_aGc0} gen2 +{_aGc2}");
    }

    static (int Lights, int Shadowed, int Decals, int Particles, int Meshes) CountScene(Node root)
    {
        int lights = 0, shadowed = 0, decals = 0, particles = 0, meshes = 0;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case Light3D l when l.Visible: lights++; if (l.ShadowEnabled) shadowed++; break;
                case Decal d when d.Visible: decals++; break;
                case GpuParticles3D or CpuParticles3D: particles++; break;
                case MultiMeshInstance3D or MeshInstance3D: meshes++; break;
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return (lights, shadowed, decals, particles, meshes);
    }
}
