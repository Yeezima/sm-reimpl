using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou": how smoothly a far Ork moves as drawn (the owner, 2026-11-06: "the orks movement at distance looks so choppy"). The server's real interest
/// (<see cref="MusouInterest"/>, its bands and its 55 KB/s budget) sends a crowd running round the player on circles at 10, 30, 60 and 100 m (plus a filler crowd
/// to load the budget); the sections reach a <see cref="MusouClientState"/> 50 to 66 ms later; the client samples it every frame at 60 fps, 3 ticks behind.
/// Per distance: the share of frames a running Ork does not move (a stall), the frame-to-frame speed change (p99, as a share of his speed), the largest step
/// (as a share of a frame's run); and the bytes a second.
/// </summary>
public class MusouFarMotionTests(ITestOutputHelper output)
{
    const int TickRate = 30;
    const float Speed = 5.5f;   // m/s, about a Boy's run
    static readonly float[] Rings = [10f, 30f, 60f, 100f];
    static readonly MusouBox Box = new(new Vector3(-200f, -200f, -10f), new Vector3(200f, 200f, 30f));
    static readonly byte[] Director = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));

    public sealed record Band(float Metres, double StallShare, double SpeedStepP99, double MaxStep, double LagMetres);
    public sealed record Result(IReadOnlyList<Band> Bands, double BytesPerSecond, double RecordsPerSecond, string Gaps, double SampleMs);

    /// <summary>Runs <paramref name="seconds"/> of a crowd of <paramref name="orks"/> (8 on each ring, the rest filler between 15 and 120 m; <paramref name="clustered"/>: 90 % of it within 15 to 40 m, as the stress test's ring). <paramref name="old"/>: both switches of the old behaviour (the client's motion and the server's send order).</summary>
    public static Result Measure(int orks, bool old, bool zigzag = false, bool clustered = false, double seconds = 12.0, double fps = 60.0, float[]? probeRings = null, int extraFar = 0)
    {
        // 2026-11-09 (the 300 m levels): extraFar more Orks between 120 and 300 m (the army Orks made real out there now), probes on probeRings, in a box that holds them.
        var Rings = probeRings ?? MusouFarMotionTests.Rings;
        var Box = extraFar > 0 || Rings.Max() > 150f ? new MusouBox(new Vector3(-400f, -400f, -10f), new Vector3(400f, 400f, 30f)) : MusouFarMotionTests.Box;
        var Director = Box == MusouFarMotionTests.Box ? MusouFarMotionTests.Director : MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));
        var rng = new Random(7);
        const int perRing = 8;
        int n = Math.Max(orks, perRing * Rings.Length) + extraFar;
        var radius = new float[n];
        var phase = new float[n];
        var dir = new float[n];
        for (int i = 0; i < n; i++)
        {
            radius[i] = i < perRing * Rings.Length ? Rings[i / perRing] : i >= n - extraFar ? 120f + (float)rng.NextDouble() * 180f : clustered && rng.NextDouble() < 0.9 ? 15f + (float)rng.NextDouble() * 25f : 15f + (float)rng.NextDouble() * 105f;
            phase[i] = (float)(rng.NextDouble() * Math.PI * 2);
            dir[i] = rng.Next(2) == 0 ? 1f : -1f;
        }
        const double Leg = 1.5;   // s: a zigzag turns 120 degrees this often
        Vector3 Truth(int i, double t)
        {
            if (zigzag)
            {
                var normal = new Vector3(MathF.Cos(phase[i]), MathF.Sin(phase[i]), 0f);
                var tangent = new Vector3(-normal.Y, normal.X, 0f) * dir[i];
                int k = (int)Math.Floor(t / Leg);
                float leg = (float)(Speed * Leg), into = (float)(Speed * (t - k * Leg));
                float side = k % 2 == 0 ? 1f : -1f;
                var way = tangent * 0.5f + normal * (0.866f * side);
                return normal * radius[i] + tangent * (k * leg * 0.5f) + (k % 2 == 1 ? normal * (leg * 0.866f) : Vector3.Zero) + way * into;
            }
            double a = phase[i] + dir[i] * Speed / radius[i] * t;
            return new Vector3(radius[i] * (float)Math.Cos(a), radius[i] * (float)Math.Sin(a), 0f);
        }

        var interest = new MusouInterest { OldFarSend = old };
        var client = new MusouClientState { OldFarMotion = old };
        var inFlight = new List<(double At, ExtrasSection Section)>();
        var enemies = new MusouEnemy[n];
        long bytes = 0;
        int ticks = (int)(seconds * TickRate);
        for (uint tick = 1; tick <= ticks; tick++)
        {
            double t = tick / (double)TickRate;
            for (int i = 0; i < n; i++)
            {
                var p = Truth(i, t);
                var q = Truth(i, t + 0.01);
                enemies[i] = new MusouEnemy((ushort)(i + 1), MusouEnemyType.Boy, MusouEnemyState.Seek, p, default, MathF.Atan2(q.Y - p.Y, q.X - p.X) - MathF.PI / 2f, 85f, -1, 0, 0, 0);
            }
            foreach (var m in interest.Plan(tick, TickRate, enemies, Vector3.Zero, Box, Director, []))
            {
                bytes += Protocol.EnvelopeBytes(new ExtrasUpdate(tick, m));
                double arrive = t + 0.050 + rng.NextDouble() * 0.016;
                foreach (var s in m) inFlight.Add((arrive, new ExtrasSection(tick, s.Tag, s.Payload)));
            }
        }
        inFlight.Sort((a, b) => a.At.CompareTo(b.At));
        var gaps = Rings.Select(_ => new List<uint>()).ToArray();
        var lastSent = new Dictionary<int, uint>();
        foreach (var (_, s) in inFlight.OrderBy(x => x.Section.ServerTick))
            if (s.Tag == MusouWire.EntitiesTag)
                foreach (var e in MusouWire.ReadEntities(s.Payload, Box)!)
                {
                    int i = e.Id - 1;
                    if (i >= perRing * Rings.Length) continue;
                    if (lastSent.TryGetValue(i, out var l)) gaps[i / perRing].Add(s.ServerTick - l);
                    lastSent[i] = s.ServerTick;
                }
        string gapNote = string.Join(", ", Rings.Select((r, b) => $"{r:F0} m every {gaps[b].DefaultIfEmpty(0u).Average(g => (double)g):F1} ticks (max {gaps[b].DefaultIfEmpty(0u).Max()})"));

        // The client: a frame every 1/fps s from 1 s on (past the first sends), the render tick 3 behind the newest server time (an ideal clock).
        var bands = new List<Band>();
        var prev = new Dictionary<int, Vector3>();
        var prevSpeed = new Dictionary<int, double>();
        var stalls = new int[Rings.Length];
        var frames = new int[Rings.Length];
        var steps = Rings.Select(_ => new List<double>()).ToArray();
        var maxStep = new double[Rings.Length];
        var lag = new double[Rings.Length];
        int next = 0;
        double dt = 1.0 / fps;
        var watch = new System.Diagnostics.Stopwatch();
        int sampled = 0;
        for (double now = 1.0; now < seconds - 0.2; now += dt)
        {
            while (next < inFlight.Count && inFlight[next].At <= now) client.Apply(inFlight[next++].Section, now);
            double renderTick = now * TickRate - 3.0;
            watch.Start();
            var shown = client.Sample(renderTick, now);
            watch.Stop(); sampled++;
            foreach (var e in shown)
            {
                int i = e.Id - 1;
                if (i >= perRing * Rings.Length) continue;
                int band = i / perRing;
                if (prev.TryGetValue(i, out var before))
                {
                    double step = Vector3.Distance(before, e.Position);
                    double speed = step / dt;
                    frames[band]++;
                    if (speed < 0.2 * Speed) stalls[band]++;
                    maxStep[band] = Math.Max(maxStep[band], step / (Speed * dt));
                    if (prevSpeed.TryGetValue(i, out var s0)) steps[band].Add(Math.Abs(speed - s0) / Speed);
                    prevSpeed[i] = speed;
                    lag[band] = Math.Max(lag[band], Vector3.Distance(e.Position, Truth(i, renderTick / TickRate)));
                }
                prev[i] = e.Position;
            }
        }
        for (int b = 0; b < Rings.Length; b++)
        {
            var sorted = steps[b].OrderBy(x => x).ToArray();
            double p99 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(0.99 * sorted.Length))];
            bands.Add(new Band(Rings[b], frames[b] == 0 ? 1 : stalls[b] / (double)frames[b], p99, maxStep[b], lag[b]));
        }
        return new Result(bands, bytes / seconds, interest.RecordsSent / seconds, gapNote, watch.Elapsed.TotalMilliseconds / Math.Max(1, sampled));
    }

    void Print(string name, Result r)
    {
        output.WriteLine($"{name}: {r.BytesPerSecond / 1000:F1} KB/s, {r.RecordsPerSecond:F0} records/s; Sample {r.SampleMs:F3} ms a frame (CPU noisy); records {r.Gaps}");
        foreach (var b in r.Bands)
            output.WriteLine($"  {b.Metres,4:F0} m: stalls {b.StallShare:P1}, speed step p99 {b.SpeedStepP99:P0}, largest step {b.MaxStep:F1}x a frame's run, off the true place at most {b.LagMetres:F2} m");
    }

    [Theory]
    [InlineData(300, false, false)]
    [InlineData(1800, false, false)]
    [InlineData(1800, true, false)]
    [InlineData(1800, false, true)]
    public void Far_orks_move_smoothly_at_every_distance(int orks, bool zigzag, bool clustered)
    {
        var old = Measure(orks, old: true, zigzag, clustered);
        var now = Measure(orks, old: false, zigzag, clustered);
        string name = $"{orks} orks{(zigzag ? " (zigzag)" : "")}{(clustered ? " (clustered)" : "")}";
        Print($"{name}, old", old);
        Print($"{name}, new", now);
        foreach (var b in now.Bands)
        {
            Assert.True(b.StallShare < 0.01, $"{b.Metres} m: {b.StallShare:P1} of frames stalled");
            // Runners on circles: smooth. Zigzags that turn 120 degrees every 1.5 s (a hard case; the old motion stepped 24 times a frame's run): a carried-on
            // runner overshoots a turn by up to his gap's run and glides back, so his speed changes, but he never stalls or jumps.
            Assert.True(b.SpeedStepP99 < (zigzag ? 2.0 : 0.25), $"{b.Metres} m: speed step p99 {b.SpeedStepP99:P0}");
            Assert.True(b.MaxStep < (zigzag ? 4.0 : 2.0), $"{b.Metres} m: a step of {b.MaxStep:F1}x a frame's run");
        }
        Assert.True(now.BytesPerSecond <= old.BytesPerSecond * 1.02 + 500, "the bandwidth stays where it was");
    }

    /// <summary>
    /// 2026-11-09 (the armies real within 300 m): 1,800 Orks clustered as the stress ring plus the 480 army Orks the levels may now make real between 120 and 300 m, through the unchanged 55 KB/s budget.
    /// Prints the bytes and the far rings' record gaps and stalls; holds that the near rings keep their records and the budget holds.
    /// </summary>
    [Fact]
    public void The_army_orks_made_real_out_to_300_m_fit_the_budget()
    {
        float[] rings = [10f, 30f, 60f, 100f, 200f, 290f];
        var before = Measure(1800, false, clustered: true, probeRings: rings);
        var after = Measure(1800, false, clustered: true, probeRings: rings, extraFar: 480);
        Print("FARMOTION 1800 clustered", before);
        Print("FARMOTION 1800 clustered + 480 at 120-300 m", after);
        Assert.True(after.BytesPerSecond < 62_000, $"{after.BytesPerSecond / 1000:F1} KB/s");
        foreach (var b in after.Bands.Where(b => b.Metres <= 30f)) Assert.True(b.StallShare < 0.05, $"{b.Metres} m stalls {b.StallShare:P1}");
    }

}
