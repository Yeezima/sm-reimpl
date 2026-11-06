using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// The calling thread's own CPU time (Windows: its cycle count, as ms at the rate measured once at the start), for the death-spike test: on a shared PC another process's load
/// stretches the wall time of any frame, the thread's own cycles not. Elsewhere the wall clock.
/// </summary>
static class ThreadClock
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();

    static double _cyclesPerMs;

    /// <summary>Only the test aid measures (the rate's measuring spins 200 ms once); off, <see cref="Mark"/> and <see cref="Ms"/> return 0.</summary>
    public static bool Enabled { get; set; }

    public static ulong Mark() => !Enabled ? 0 : OperatingSystem.IsWindows() && QueryThreadCycleTime(GetCurrentThread(), out ulong c) ? c : (ulong)Stopwatch.GetTimestamp();

    /// <summary>Milliseconds of this thread's CPU since <paramref name="mark"/>.</summary>
    public static double Ms(ulong mark) => !Enabled ? 0 : (Mark() - mark) / CyclesPerMs;

    /// <summary>Cycles a millisecond: 40 ms of spinning, the most of five tries (the try least switched out is the truest rate).</summary>
    static double CyclesPerMs
    {
        get
        {
            if (_cyclesPerMs > 0) return _cyclesPerMs;
            if (!OperatingSystem.IsWindows()) return _cyclesPerMs = Stopwatch.Frequency / 1000.0;
            double best = 0;
            for (int k = 0; k < 5; k++)
            {
                ulong c0 = Mark(); long t0 = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 40) { }
                best = Math.Max(best, (Mark() - c0) / Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            return _cyclesPerMs = Math.Max(1.0, best);
        }
    }
}


/// <summary>
/// Test aid (night batch 2026-11-06, the owner: "when a grenade explodes many orks die instantaneously ... remove the moment of extreme frame time"): <c>--musou-death-spike-test [N]</c>
/// runs the scripted gore feed (<see cref="MusouGoreDemoFeed"/>) with its <c>burst</c> case: every 4 s all N Orks (default 60), standing 8 m in front of the camera, die by a blast in one
/// tick, as a grenade in a packed crowd. The crowd is drawn even headless (the death path builds and moves its nodes, steps its ragdolls and its gore; the GPU, the shaders and the
/// screen are what a headless run cannot show). A frame's cost is the main thread's own CPU from the end of one Musou frame to the end of the next (<see cref="ThreadClock"/>: the
/// view's processing, the engine's physics step and its other nodes). One <c>MUSOUSPIKE</c> line per burst: the worst frame of the second after it and how far it stands above the
/// quiet frames' median (the frames 1.5 s and more after a burst), the Musou frame's and the rest's worst, and the Musou parts' worst (the deaths handed out, the crowd, the ragdolls,
/// the gore, the HUD and sounds); after <see cref="Bursts"/> bursts a summary and the client quits. <c>--musou-death-spike-ab</c>: the bursts after the first take turns, old hand-out
/// (<c>--musou-old-deaths</c>, everything in the frame it arrives) and new (<see cref="MusouDeathRules"/>), so both meet the same load on a shared PC.
/// </summary>
public partial class NetworkView
{
    static readonly bool MusouDeathSpikeTest = ThreadClock.Enabled = CmdFlag("musou-death-spike-test");
    static readonly bool MusouDeathSpikeAb = CmdFlag("musou-death-spike-ab");
    /// <summary>Bursts measured before the client quits (the first is the cold one: pools and looks not yet used).</summary>
    const int Bursts = 15;
    const double WindowSeconds = 1.0, QuietAfter = 1.5;

    /// <summary>The parts timed this frame (ms of CPU): the deaths handed out, the crowd's frame, the ragdolls' frame, the gore, the HUD and sounds.</summary>
    double _spikeDeathsMs, _spikeCrowdMs, _spikeDollsMs, _spikeGoreMs, _spikeSoundsMs;
    float _spikeOutsideMs;
    ulong _spikeLastCpu;
    long _spikeLastWall, _spikeBurstWall;
    int _spikeBurst, _spikeDeaths, _spikeWorstAt, _spikeSpread, _spikeFrame = -1;
    bool _spikeOld;
    int _spikeGc0, _spikeGc1, _spikeGc2;
    long _spikeAlloc;
    double _spikeWorst, _spikeInside, _spikeOutside, _spikeWallWorst, _spikeParts, _spikeDeathsWorst, _spikeCrowdWorst, _spikeDollsWorst, _spikeGoreWorst, _spikeSoundsWorst;
    readonly List<float> _spikeQuiet = new();
    readonly List<(bool Old, double Worst, double Above, double Inside, double Outside)> _spikeResults = new();
    string _spikeCold = "";

    static ulong SpikeMark() => ThreadClock.Mark();
    static double SpikeMs(ulong mark) => ThreadClock.Ms(mark);

    /// <summary>The start of a Musou frame: the CPU since the last one ended is the rest of the frame.</summary>
    void MusouDeathSpikeStart() { if (MusouDeathSpikeTest) _spikeOutsideMs = _spikeLastCpu == 0 ? 0f : (float)SpikeMs(_spikeLastCpu); }

    /// <summary>Where the test's crowd stands: on the ground 8 m in front of the camera (game space), else the own place.</summary>
    System.Numerics.Vector3 MusouDeathSpikeCentre(System.Numerics.Vector3 fallback)
    {
        if (_camera is not { } cam || _client?.Collision is not { } world) return fallback;
        var c = cam.GlobalPosition;
        var f = -cam.GlobalBasis.Z;
        var eye = new System.Numerics.Vector3(c.X, -c.Z, c.Y);
        var ahead = new System.Numerics.Vector2(f.X, -f.Z);
        ahead = ahead.LengthSquared() > 1e-4f ? System.Numerics.Vector2.Normalize(ahead) : System.Numerics.Vector2.UnitY;
        var top = eye + new System.Numerics.Vector3(ahead * 8f, 1f);
        var at = world.Weapons.Raycast(top, new System.Numerics.Vector3(0f, 0f, -1f), 12f) is { } hit ? top - new System.Numerics.Vector3(0f, 0f, hit.Distance) : fallback;
        GD.Print($"MUSOUSPIKE camera at ({eye.X:F1} {eye.Y:F1} {eye.Z:F1}), crowd at ({at.X:F1} {at.Y:F1} {at.Z:F1}){(MusouDeathSpikeAb ? "; A/B: old and new hand-out take turns" : "")}");
        return at;
    }

    /// <summary>The end of a Musou frame: <paramref name="arrived"/> deaths came this frame.</summary>
    void MusouDeathSpikeFrame(int arrived)
    {
        if (!MusouDeathSpikeTest) return;
        float ms = _spikeLastCpu == 0 ? 0f : (float)SpikeMs(_spikeLastCpu);   // this thread's CPU since the last frame's end: one whole frame
        _spikeLastCpu = SpikeMark();
        long wall = Stopwatch.GetTimestamp();
        float wallMs = _spikeLastWall == 0 ? 0f : (float)((wall - _spikeLastWall) * 1000.0 / Stopwatch.Frequency);
        _spikeLastWall = wall;
        double parts = _spikeDeathsMs + _spikeCrowdMs + _spikeDollsMs + _spikeGoreMs + _spikeSoundsMs;
        if (arrived >= 10 && _spikeFrame < 0)
        {
            _spikeFrame = 0; _spikeBurstWall = wall; _spikeDeaths = arrived; _spikeSpread = 0; _spikeOld = _musouOldDeaths;
            (_spikeGc0, _spikeGc1, _spikeGc2, _spikeAlloc) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetAllocatedBytesForCurrentThread());
            _spikeWorst = _spikeInside = _spikeOutside = _spikeWallWorst = _spikeParts = _spikeDeathsWorst = _spikeCrowdWorst = _spikeDollsWorst = _spikeGoreWorst = _spikeSoundsWorst = 0;
            _musouCrowd?.Timing();
        }
        double since = _spikeBurstWall == 0 ? double.MaxValue : (wall - _spikeBurstWall) / (double)Stopwatch.Frequency;
        if (_spikeFrame >= 0)
        {
            if (ms > _spikeWorst) { _spikeWorst = ms; _spikeWorstAt = _spikeFrame; }
            _spikeInside = Math.Max(_spikeInside, ms - _spikeOutsideMs);
            _spikeOutside = Math.Max(_spikeOutside, _spikeOutsideMs);
            _spikeWallWorst = Math.Max(_spikeWallWorst, wallMs);
            _spikeParts = Math.Max(_spikeParts, parts);
            _spikeDeathsWorst = Math.Max(_spikeDeathsWorst, _spikeDeathsMs); _spikeCrowdWorst = Math.Max(_spikeCrowdWorst, _spikeCrowdMs);
            _spikeDollsWorst = Math.Max(_spikeDollsWorst, _spikeDollsMs); _spikeGoreWorst = Math.Max(_spikeGoreWorst, _spikeGoreMs); _spikeSoundsWorst = Math.Max(_spikeSoundsWorst, _spikeSoundsMs);
            if (_musouDeathQueue.Count > 0) _spikeSpread = _spikeFrame + 1;
            _spikeFrame++;
            if (since >= WindowSeconds) SpikeReport();
        }
        else if (since >= QuietAfter && _spikeBurst > 0) _spikeQuiet.Add(ms);
        _spikeDeathsMs = _spikeCrowdMs = _spikeDollsMs = _spikeGoreMs = _spikeSoundsMs = 0;
    }

    void SpikeReport()
    {
        _spikeBurst++;
        _spikeFrame = -1;
        var quiet = _spikeQuiet.OrderBy(f => f).ToArray();
        _spikeQuiet.Clear();
        double p50 = quiet.Length == 0 ? double.NaN : quiet[quiet.Length / 2];
        string line = $"MUSOUSPIKE burst {_spikeBurst} ({(_spikeOld ? "old" : "new")}): {_spikeDeaths} deaths in one frame{(_spikeOld ? ", all handed out at once" : $", handed out over {_spikeSpread + 1} frames")}; main-thread CPU: " +
                      $"worst frame {_spikeWorst:F1} ms (frame +{_spikeWorstAt}), the quiet frames before it p50 {p50:F1} ms; the Musou frame worst {_spikeInside:F1} ms, the rest of a frame worst {_spikeOutside:F1} ms; wall worst {_spikeWallWorst:F1} ms; " +
                      $"Musou parts worst {_spikeParts:F1} ms (deaths {_spikeDeathsWorst:F1}, crowd {_spikeCrowdWorst:F1} [{_musouCrowd?.Timing() ?? "-"}], ragdolls {_spikeDollsWorst:F1}, gore {_spikeGoreWorst:F1}, HUD and sounds {_spikeSoundsWorst:F1}); " +
                      $"bodies {_musouCrowd?.Bodies ?? 0}, ragdolls {_musouRagdolls?.Active ?? 0} of {_musouRagdolls?.Built ?? 0}; in the second: GCs {GC.CollectionCount(0) - _spikeGc0}/{GC.CollectionCount(1) - _spikeGc1}/{GC.CollectionCount(2) - _spikeGc2}, main thread allocated {(GC.GetAllocatedBytesForCurrentThread() - _spikeAlloc) / 1024} KB";
        GD.Print(line);
        if (_spikeBurst == 1) _spikeCold = $"{_spikeWorst:F1} ms ({(_spikeOld ? "old" : "new")})";
        else if (!double.IsNaN(p50)) _spikeResults.Add((_spikeOld, _spikeWorst, _spikeWorst - p50, _spikeInside, _spikeOutside));
        if (MusouDeathSpikeAb) _musouOldDeaths = _spikeBurst % 2 == 1;   // bursts 2, 4, ... old; 3, 5, ... new
        if (_spikeBurst < Bursts) return;
        static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a[a.Length / 2]; }
        foreach (bool old in new[] { true, false })
        {
            var r = _spikeResults.Where(x => x.Old == old).ToList();
            if (r.Count == 0) continue;
            GD.Print($"MUSOUSPIKE summary ({(old ? "old" : "new")}): {r.Count} warm bursts, main-thread CPU medians: worst frame {Median(r.Select(x => x.Worst)):F1} ms (most {r.Max(x => x.Worst):F1}), " +
                     $"above the quiet frames {Median(r.Select(x => x.Above)):F1} ms; the Musou frame {Median(r.Select(x => x.Inside)):F1} ms, the rest {Median(r.Select(x => x.Outside)):F1} ms; the cold burst {_spikeCold}");
        }
        GetTree().Quit(0);
    }
}
