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
        _frames.Clear(); _elapsed = 0; _cpuSum = _gpuSum = _drawSum = _objSum = _primSum = _physSum = 0; _samples = 0;
    }

    /// <summary>The summary of every frame after the first 8 s (printed when the view leaves the tree).</summary>
    public void PrintSummary()
    {
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
