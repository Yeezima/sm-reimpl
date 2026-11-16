using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou": how smoothly the crowd moves as drawn (the owner, 2026-11-08: "stormboyz look extremely choppy while jumping and flying ... do the same for all guardsmen, orks").
/// Engine-free: probes with a known true path (Boys running circles at 10 to 100 m, Stormboys running and hopping at 10 to 60 m, Boys that run and stop to fight near the player, Guardsmen
/// marching at 10 to 100 m) among a filler crowd that loads the budget go through the server's real interests (<see cref="MusouInterest"/> with its 55 KB/s, <see cref="TfwGuardInterest"/> with what
/// Musou left of the client's 58 KB/s) and the real event window; every section reaches the client 50 to 66 ms later; the client's real states (<see cref="MusouClientState"/>,
/// <see cref="TfwClientState"/>) are sampled every frame at 60 fps, 3 ticks behind (an ideal render clock).
/// Per probe group: the drawn place's frame-to-frame step change (jerk: |step(k) - step(k-1)|, cm; p99 and max) beside the true path's own, the share of frames a moving probe is drawn still,
/// the distance from the true place at the render tick (p99 and max, m) and the facing's largest turn in one frame (deg, p99 and max).
/// </summary>
public class MusouCrowdSmoothnessTests(ITestOutputHelper output)
{
    const int TickRate = 30;
    static readonly MusouBox Box = new(new Vector3(-200f, -200f, -10f), new Vector3(200f, 200f, 30f));
    static readonly byte[] Director = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));

    public sealed record Group(string Name, int Frames, double JerkP99Cm, double JerkMaxCm, double TruthJerkP99Cm, double TruthJerkMaxCm, double OffStepP99Cm, double OffStepMaxCm, double StallShare, double ErrorP99, double ErrorMax, double YawP99, double YawMax);
    public sealed record Result(IReadOnlyList<Group> Groups, double MusouKBs, double GuardKBs, double SampleMs);

    enum Kind { Runner, Stormboy, Fighter, Guard }

    sealed class Probe
    {
        public Kind Kind; public float Ring; public float Phase; public float Dir; public int Offset; public string Group = "";
    }

    // The probes' true paths, at a fractional server tick.
    const float RunPerTick = 5.5f / TickRate, StormRunPerTick = 4f / TickRate, GuardPerTick = 3f / TickRate;
    const int StormRunTicks = 36;
    const float StormHop = 22f;
    /// <summary>The flight ticks as <see cref="MusouSim.Hop"/> gives them.</summary>
    static readonly int StormHopTicks = Math.Max(1, (int)MathF.Round(MusouStormboy.FlightSeconds * Math.Clamp(StormHop / MusouStormboy.HopDistance, 0.5f, 1f) * TickRate));
    const int FightRun = 30, WindUp = 15, Strike = 6, Recover = 15;

    static Vector2 Rotate(Vector2 v, float a) => new(v.X * MathF.Cos(a) - v.Y * MathF.Sin(a), v.X * MathF.Sin(a) + v.Y * MathF.Cos(a));
    static float YawOf(Vector2 d) => MathF.Atan2(d.Y, d.X) - MathF.PI / 2f;

    /// <summary>A probe at tick <paramref name="tick"/>: place, facing, state (Musou's; a Guardsman's state is <see cref="GuardState.Moving"/>), and for a Stormboy the hop under way (start tick, landing).</summary>
    static (Vector3 P, float Yaw, MusouEnemyState State, (uint Start, Vector3 To)? Hop) Truth(Probe p, double tick)
    {
        switch (p.Kind)
        {
            case Kind.Runner:
            case Kind.Guard:
            {
                float speed = p.Kind == Kind.Runner ? RunPerTick : GuardPerTick;
                double a = p.Phase + p.Dir * speed / p.Ring * tick;
                var pos = new Vector3(p.Ring * (float)Math.Cos(a), p.Ring * (float)Math.Sin(a), 0f);
                var tangent = new Vector2(-(float)Math.Sin(a), (float)Math.Cos(a)) * p.Dir;
                return (pos, YawOf(tangent), MusouEnemyState.Seek, null);
            }
            case Kind.Stormboy:
            {
                double tau = tick + p.Offset;
                int cycle = StormRunTicks + StormHopTicks;
                int k = (int)Math.Floor(tau / cycle);
                double w = tau - k * cycle;
                var start = new Vector2(p.Ring * MathF.Cos(p.Phase), p.Ring * MathF.Sin(p.Phase));
                var baseDir = Vector2.Normalize(new Vector2(-start.Y, start.X)) * p.Dir;
                // Legs turn 90 degrees each, so four legs close a square and the probe stays near its ring.
                var at = start;
                for (int j = 0; j < (k % 4 + 4) % 4; j++) at += Rotate(baseDir, j * MathF.PI / 2f) * (StormRunTicks * StormRunPerTick + StormHop);
                var dir = Rotate(baseDir, ((k % 4 + 4) % 4) * MathF.PI / 2f);
                if (w < StormRunTicks) return (new Vector3(at + dir * (float)(StormRunPerTick * w), 0f), YawOf(dir), MusouEnemyState.Seek, null);
                var from = new Vector3(at + dir * (StormRunTicks * StormRunPerTick), 0f);
                var to = from + new Vector3(dir * StormHop, 0f);
                float u = (float)((w - StormRunTicks) / StormHopTicks);
                var pos = Vector3.Lerp(from, to, u) + new Vector3(0f, 0f, 4f * MusouStormboy.ApexHeight * u * (1f - u));
                uint hopStart = (uint)Math.Round(k * (double)cycle + StormRunTicks - p.Offset);
                return (pos, YawOf(dir), MusouEnemyState.Airborne, (hopStart, to));
            }
            default:
            {
                // A Boy near the player: runs 1 s, then stands to wind up, strike and recover, then runs on 120 degrees round.
                double tau = tick + p.Offset;
                int cycle = FightRun + WindUp + Strike + Recover;
                int k = (int)Math.Floor(tau / cycle);
                double w = tau - k * cycle;
                var at = new Vector2(p.Ring * MathF.Cos(p.Phase), p.Ring * MathF.Sin(p.Phase));
                var baseDir = Vector2.Normalize(new Vector2(-at.Y, at.X)) * p.Dir;
                for (int j = 0; j < (k % 3 + 3) % 3; j++) at += Rotate(baseDir, j * 2f * MathF.PI / 3f) * (FightRun * RunPerTick);
                var dir = Rotate(baseDir, ((k % 3 + 3) % 3) * 2f * MathF.PI / 3f);
                if (w < FightRun) return (new Vector3(at + dir * (float)(RunPerTick * w), 0f), YawOf(dir), MusouEnemyState.Seek, null);
                var stand = new Vector3(at + dir * (FightRun * RunPerTick), 0f);
                var state = w < FightRun + WindUp ? MusouEnemyState.WindUp : w < FightRun + WindUp + Strike ? MusouEnemyState.Strike : MusouEnemyState.Recover;
                return (stand, YawOf(dir), state, null);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="seconds"/>: the probes plus <paramref name="fillerOrks"/> Orks (90 % within 15 to 40 m, as the stress test's ring) and <paramref name="fillerGuards"/> Guardsmen (15 to 120 m), every
    /// filler running on a circle. <paramref name="old"/>: the client's crowd motion before 2026-11-08 (<see cref="MusouClientState.OldCrowdMotion"/>, <see cref="TfwClientState.OldCrowdMotion"/>).
    /// </summary>
    public static Result Measure(int fillerOrks, int fillerGuards, bool old, double seconds = 14.0, double fps = 60.0)
    {
        var rng = new Random(11);
        var probes = new List<Probe>();
        foreach (var ring in new[] { 10f, 30f, 60f, 100f })
            for (int k = 0; k < 8; k++) probes.Add(new Probe { Kind = Kind.Runner, Ring = ring, Phase = (float)(rng.NextDouble() * Math.PI * 2), Dir = rng.Next(2) == 0 ? 1f : -1f, Group = $"Boy running {ring:F0} m" });
        foreach (var ring in new[] { 10f, 30f, 60f })
            for (int k = 0; k < 6; k++) probes.Add(new Probe { Kind = Kind.Stormboy, Ring = ring, Phase = (float)(rng.NextDouble() * Math.PI * 2), Dir = rng.Next(2) == 0 ? 1f : -1f, Offset = rng.Next(70), Group = $"Stormboy {ring:F0} m" });
        for (int k = 0; k < 8; k++) probes.Add(new Probe { Kind = Kind.Fighter, Ring = 5f + k, Phase = (float)(rng.NextDouble() * Math.PI * 2), Dir = 1f, Offset = rng.Next(66), Group = "Boy fighting 5-12 m" });
        int orkProbes = probes.Count;
        foreach (var ring in new[] { 10f, 30f, 60f, 100f })
            for (int k = 0; k < 8; k++) probes.Add(new Probe { Kind = Kind.Guard, Ring = ring, Phase = (float)(rng.NextDouble() * Math.PI * 2), Dir = rng.Next(2) == 0 ? 1f : -1f, Group = $"Guardsman {ring:F0} m" });
        int guardProbes = probes.Count - orkProbes;

        var fillRadius = new float[fillerOrks]; var fillPhase = new float[fillerOrks];
        for (int i = 0; i < fillerOrks; i++) { fillRadius[i] = rng.NextDouble() < 0.9 ? 15f + (float)rng.NextDouble() * 25f : 15f + (float)rng.NextDouble() * 105f; fillPhase[i] = (float)(rng.NextDouble() * Math.PI * 2); }
        var guardRadius = new float[fillerGuards]; var guardPhase = new float[fillerGuards];
        for (int i = 0; i < fillerGuards; i++) { guardRadius[i] = 15f + (float)rng.NextDouble() * 105f; guardPhase[i] = (float)(rng.NextDouble() * Math.PI * 2); }

        var musou = new MusouInterest { OldNearRate = old };
        var guardsInterest = new TfwGuardInterest { OldShare = old };
        var window = new MusouEventWindow();
        var client = new MusouClientState { OldCrowdMotion = old };
        var tfw = new TfwClientState { OldCrowdMotion = old };
        var inFlight = new List<(double At, ExtrasSection Section, bool Guard)>();
        var enemies = new MusouEnemy[orkProbes + fillerOrks];
        var guards = new TfwGuard[guardProbes + fillerGuards];
        var newEvents = new List<MusouEvent>();
        var lastState = new MusouEnemyState[orkProbes];
        long musouBytes = 0, guardBytes = 0;
        int ticks = (int)(seconds * TickRate);
        for (uint tick = 1; tick <= ticks; tick++)
        {
            double t = tick / (double)TickRate;
            newEvents.Clear();
            for (int i = 0; i < orkProbes; i++)
            {
                var (p, yaw, state, hop) = Truth(probes[i], tick);
                var type = probes[i].Kind == Kind.Stormboy ? MusouEnemyType.Stormboy : MusouEnemyType.Boy;
                if (state == MusouEnemyState.Airborne && lastState[i] != MusouEnemyState.Airborne && hop is { } h)
                    newEvents.Add(new MusouEvent(MusouEventKind.Telegraph, tick, (ushort)(i + 1), MusouStormboy.HopEventPlayer(StormHopTicks), MusouStormboy.HopCue, h.To));
                if (state == MusouEnemyState.Seek && lastState[i] == MusouEnemyState.Airborne)
                    newEvents.Add(new MusouEvent(MusouEventKind.Telegraph, tick, (ushort)(i + 1), 0, MusouStormboy.LandCue, p));
                lastState[i] = state;
                enemies[i] = new MusouEnemy((ushort)(i + 1), type, state, p, default, yaw, 85f, -1, 0, 0, 0);
            }
            for (int i = 0; i < fillerOrks; i++)
            {
                double a = fillPhase[i] + RunPerTick / fillRadius[i] * tick;
                enemies[orkProbes + i] = new MusouEnemy((ushort)(orkProbes + i + 1), MusouEnemyType.Boy, MusouEnemyState.Seek,
                    new Vector3(fillRadius[i] * (float)Math.Cos(a), fillRadius[i] * (float)Math.Sin(a), 0f), default, (float)a, 85f, -1, 0, 0, 0);
            }
            for (int i = 0; i < guardProbes; i++)
            {
                var (p, yaw, _, _) = Truth(probes[orkProbes + i], tick);
                guards[i] = new TfwGuard((ushort)(i + 1), p, yaw, (byte)GuardState.Moving);
            }
            for (int i = 0; i < fillerGuards; i++)
            {
                double a = guardPhase[i] + GuardPerTick / guardRadius[i] * tick;
                guards[guardProbes + i] = new TfwGuard((ushort)(guardProbes + i + 1), new Vector3(guardRadius[i] * (float)Math.Cos(a), guardRadius[i] * (float)Math.Sin(a), 0f), (float)a, (byte)GuardState.Moving);
            }
            var events = window.Step(tick, newEvents, MusouSendRules.EventsPerTick(TickRate));
            double arrive = t + 0.050 + rng.NextDouble() * 0.016;
            foreach (var m in musou.Plan(tick, TickRate, enemies, Vector3.Zero, Box, Director, events))
            {
                musouBytes += Protocol.EnvelopeBytes(new ExtrasUpdate(tick, m));
                if (Trace is { } traced && traced < orkProbes)
                    foreach (var s in m)
                        if (s.Tag == MusouWire.EntitiesTag && MusouWire.ReadEntities(s.Payload, Box)!.Any(e => e.Id == traced + 1)) TraceLines.Add($"sent at tick {tick} ({enemies[traced].State}) tokens {musou.Tokens:F0}");
                foreach (var s in m) inFlight.Add((arrive, new ExtrasSection(tick, s.Tag, s.Payload), false));
            }
            float spent = guardsInterest.MusouBytes(musou.Tokens, TickRate);
            foreach (var m in guardsInterest.Plan(tick, TickRate, null, null, guards, Vector3.Zero, Box, [], spent))
            {
                guardBytes += Protocol.EnvelopeBytes(new ExtrasUpdate(tick, m));
                foreach (var s in m) inFlight.Add((arrive, new ExtrasSection(tick, s.Tag, s.Payload), true));
            }
            musou.CoreBytes += guardsInterest.TakeOverdraw();   // as GameServer.TfwSend.cs
        }
        inFlight.Sort((a, b) => a.At.CompareTo(b.At));

        var names = probes.Select(p => p.Group).Distinct().ToList();
        var jerks = names.ToDictionary(n => n, _ => new List<double>());
        var truthJerks = names.ToDictionary(n => n, _ => new List<double>());
        var offSteps = names.ToDictionary(n => n, _ => new List<double>());
        var errors = names.ToDictionary(n => n, _ => new List<double>());
        var yaws = names.ToDictionary(n => n, _ => new List<double>());
        var stalls = names.ToDictionary(n => n, _ => 0);
        var moving = names.ToDictionary(n => n, _ => 0);
        var frames = names.ToDictionary(n => n, _ => 0);
        var lastDrawn = new Dictionary<int, (Vector3 P, Vector3 Step, float Yaw, double Render)>();
        var lastTrue = new Dictionary<int, (Vector3 P, Vector3 Step)>();
        int next = 0;
        double dt = 1.0 / fps;
        var watch = new System.Diagnostics.Stopwatch();
        int sampled = 0;
        void Note(int probe, Vector3 drawn, float yaw, double renderTick)
        {
            var p = probes[probe];
            var truth = Truth(p, renderTick).P;
            if (Trace is { } trace && probe == trace && renderTick < 400 && lastDrawn.TryGetValue(probe, out var td) && lastTrue.TryGetValue(probe, out var tt))
                TraceLines.Add($"r {renderTick:F1} drawn {drawn.X:F2},{drawn.Y:F2},{drawn.Z:F2} true {truth.X:F2},{truth.Y:F2},{truth.Z:F2} {Truth(p, renderTick).State} offstep {Vector3.Distance(drawn - td.P, truth - tt.P) * 100:F1} cm");
            string g = p.Group;
            if (lastDrawn.TryGetValue(probe, out var d) && d.Render < renderTick && renderTick - d.Render < 1.0)
            {
                var step = drawn - d.P;
                var trueStep = truth - lastTrue[probe].P;
                frames[g]++;
                if (trueStep.Length() > 0.02f) { moving[g]++; if (step.Length() < 0.2f * trueStep.Length()) stalls[g]++; }
                if (d.Step != Vector3.Zero || lastTrue[probe].Step != Vector3.Zero)
                {
                    jerks[g].Add(Vector3.Distance(step, d.Step) * 100.0);
                    truthJerks[g].Add(Vector3.Distance(trueStep, lastTrue[probe].Step) * 100.0);
                }
                offSteps[g].Add(Vector3.Distance(step, trueStep) * 100.0);
                yaws[g].Add(Math.Abs(MathF.IEEERemainder(yaw - d.Yaw, 2f * MathF.PI)) * 180.0 / Math.PI);
                errors[g].Add(Vector3.Distance(drawn, truth));
                lastDrawn[probe] = (drawn, step, yaw, renderTick);
                lastTrue[probe] = (truth, trueStep);
            }
            else
            {
                lastDrawn[probe] = (drawn, Vector3.Zero, yaw, renderTick);
                lastTrue[probe] = (truth, Vector3.Zero);
            }
        }
        for (double now = 1.5; now < seconds - 0.2; now += dt)
        {
            while (next < inFlight.Count && inFlight[next].At <= now)
            {
                var (_, s, guard) = inFlight[next++];
                if (guard) tfw.Apply(s, Box, now); else client.Apply(s, now);
            }
            double renderTick = now * TickRate - 3.0;
            watch.Start();
            var shown = client.Sample(renderTick, now);
            var shownGuards = tfw.Sample(renderTick, now);
            watch.Stop(); sampled++;
            foreach (var e in shown) if (e.Id - 1 < orkProbes) Note(e.Id - 1, e.Position, e.Yaw, renderTick);
            foreach (var g in shownGuards) if (g.Id - 1 < guardProbes) Note(orkProbes + g.Id - 1, g.Position, g.Yaw, renderTick);
            client.TakeNew(); tfw.TakeNew();
        }
        static double P(List<double> v, double q) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToArray(); return s[Math.Min(s.Length - 1, (int)(q * s.Length))]; }
        var groups = names.Select(n => new Group(n, frames[n], P(jerks[n], 0.99), jerks[n].DefaultIfEmpty(0).Max(), P(truthJerks[n], 0.99), truthJerks[n].DefaultIfEmpty(0).Max(), P(offSteps[n], 0.99), offSteps[n].DefaultIfEmpty(0).Max(),
            moving[n] == 0 ? 0 : stalls[n] / (double)moving[n], P(errors[n], 0.99), errors[n].DefaultIfEmpty(0).Max(), P(yaws[n], 0.99), yaws[n].DefaultIfEmpty(0).Max())).ToList();
        return new Result(groups, musouBytes / seconds / 1000.0, guardBytes / seconds / 1000.0, watch.Elapsed.TotalMilliseconds / Math.Max(1, sampled));
    }

    /// <summary>Test aid: the probe whose drawn and true places <see cref="Measure"/> writes to <see cref="TraceLines"/> (null: none).</summary>
    public static int? Trace;
    public static readonly List<string> TraceLines = new();

    [Fact(Skip = "trace aid: SM_SMOOTH_PROBE, SM_SMOOTH_ORKS, SM_SMOOTH_OLD")]
    public void Trace_a_Stormboy()
    {
        Trace = int.TryParse(Environment.GetEnvironmentVariable("SM_SMOOTH_PROBE"), out int probe) ? probe : 32; TraceLines.Clear();
        int orks = int.TryParse(Environment.GetEnvironmentVariable("SM_SMOOTH_ORKS"), out int o) ? o : 300;
        Measure(orks, orks / 6, old: Environment.GetEnvironmentVariable("SM_SMOOTH_OLD") == "1", seconds: 8);
        Trace = null;
        foreach (var l in TraceLines) output.WriteLine(l);
    }

    void Print(string name, Result r)
    {
        output.WriteLine($"{name}: Musou {r.MusouKBs:F1} KB/s, Guards {r.GuardKBs:F1} KB/s, Sample {r.SampleMs:F3} ms a frame (CPU noisy)");
        output.WriteLine("  group                  | jerk p99 / max cm (truth p99 / max) | step off true p99 / max cm | stalled | off true p99 / max m | turn p99 / max deg");
        foreach (var g in r.Groups)
            output.WriteLine($"  {g.Name,-22} | {g.JerkP99Cm,6:F2} / {g.JerkMaxCm,6:F1} ({g.TruthJerkP99Cm,5:F2} / {g.TruthJerkMaxCm,5:F1}) | {g.OffStepP99Cm,6:F2} / {g.OffStepMaxCm,6:F1} | {g.StallShare,6:P1} | {g.ErrorP99,5:F2} / {g.ErrorMax,5:F2} | {g.YawP99,5:F1} / {g.YawMax,5:F1}");
    }

    [Theory]
    [InlineData(300, 100)]
    [InlineData(1800, 300)]
    public void The_crowd_is_drawn_smoothly(int orks, int guards)
    {
        var old = Measure(orks, guards, old: true);
        var now = Measure(orks, guards, old: false);
        Print($"{orks} Orks, {guards} Guardsmen, old", old);
        Print($"{orks} Orks, {guards} Guardsmen, new", now);
        foreach (var g in now.Groups)
        {
            // A Stormboy stands on his landing until his next record from the ground (the landing's own record says nothing of his run): up to 15 ticks far under a full budget.
            Assert.True(g.StallShare < (g.Name.StartsWith("Stormboy") ? 0.16 : 0.03), $"{g.Name}: {g.StallShare:P1} of moving frames drawn still");
            // The drawn path's step changes stay near the true path's own (a Stormboy's take-off and landing, a fighter's stop are real changes; a carried-on far runner's correction is glided):
            // a few cm a frame against steps of 5 to 33 cm.
            Assert.True(g.JerkP99Cm <= 1.2 * g.TruthJerkP99Cm + 3.0, $"{g.Name}: jerk p99 {g.JerkP99Cm:F2} cm (truth {g.TruthJerkP99Cm:F2})");
            // A Stormboy's flight is 33 cm a frame; his landing stand and the glide off it under a full budget are the worst of his frames. A fighter's stop and start (9 cm a frame) are rounded.
            Assert.True(g.OffStepP99Cm <= (g.Name.StartsWith("Stormboy") ? 25.0 : 10.0), $"{g.Name}: a drawn step off the true one by {g.OffStepP99Cm:F2} cm (p99)");
            Assert.True(g.YawMax < 30.0, $"{g.Name}: a turn of {g.YawMax:F1} degrees in one frame");
        }
        Assert.True(now.MusouKBs + now.GuardKBs <= (old.MusouKBs + old.GuardKBs) * 1.02 + 0.5, "the client's Musou and war bandwidth stays where it was");
    }
}
