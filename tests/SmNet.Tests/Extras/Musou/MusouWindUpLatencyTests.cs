using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (night batch 2026-11-06, the open item of musou/far-ork-smooth): whether a near Ork's attack wind-up reaches the client late when the crowd fills
/// the send budget. The server's real interest (<see cref="MusouInterest"/>, its bands, its 55 KB/s budget and the 15-tick most wait for far Orks) and its real event window
/// (<see cref="MusouEventWindow"/>) plan each tick; the test reads the planned sections and counts, for every wind-up a near Ork (3 to 12 m) starts, the ticks from the
/// simulation's state change to the first record that carries it, and for its <see cref="MusouEventKind.Telegraph"/> event the ticks to its first copy. A light crowd
/// (300 Orks) against the stress ring's (1800, 90 % within 15 to 40 m), with and without a grenade's 60 deaths in one tick. The network's own delay is the same in each
/// case and left out.
/// </summary>
public class MusouWindUpLatencyTests(ITestOutputHelper output)
{
    const int TickRate = 30, Near = 16;
    static readonly MusouBox Box = new(new Vector3(-200f, -200f, -10f), new Vector3(200f, 200f, 30f));
    static readonly byte[] Director = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));

    public sealed record Result(double RecordMean, uint RecordMax, double TelegraphMean, uint TelegraphMax, int WindUps, double FarGapMean, uint FarGapMax);

    /// <summary>
    /// <paramref name="orks"/> Orks for <paramref name="seconds"/>: the first <see cref="Near"/> at 3 to 12 m cycle Seek (20 to 40 ticks), WindUp (15), Strike (3), Recover (10);
    /// the rest run as filler (<paramref name="clustered"/>: 90 % within 15 to 40 m). <paramref name="blastEvery"/> &gt; 0: every that many ticks 60 deaths in one tick (a grenade
    /// in the crowd) join the events. <paramref name="old"/>: the send order before this change (<see cref="MusouInterest.OldNearSend"/>, <see cref="MusouEventWindow.OldOrder"/>).
    /// </summary>
    public static Result Measure(int orks, bool clustered, bool old, int blastEvery = 0, double seconds = 20.0)
    {
        var rng = new Random(5);
        int n = Math.Max(orks, Near + 1);
        var radius = new float[n];
        var phase = new float[n];
        for (int i = 0; i < n; i++)
        {
            radius[i] = i < Near ? 3f + 9f * i / (Near - 1) : clustered && rng.NextDouble() < 0.9 ? 15f + (float)rng.NextDouble() * 25f : 15f + (float)rng.NextDouble() * 105f;
            phase[i] = (float)(rng.NextDouble() * Math.PI * 2);
        }
        // The near Orks' attack cycles: the tick each state starts.
        var state = new MusouEnemyState[n];
        var stateUntil = new int[n];
        var windUpAt = new uint[n];
        for (int i = 0; i < Near; i++) { state[i] = MusouEnemyState.Seek; stateUntil[i] = 1 + rng.Next(20, 41); }

        var interest = new MusouInterest { OldFarSend = false, OldNearSend = old };
        var window = new MusouEventWindow { OldOrder = old };
        var enemies = new MusouEnemy[n];
        var events = new List<MusouEvent>();
        var recordDelays = new List<uint>();
        var telegraphDelays = new List<uint>();
        var pendingRecord = new Dictionary<int, uint>();
        var pendingTelegraph = new Dictionary<int, uint>();
        var lastFar = new Dictionary<int, uint>();
        var farGaps = new List<uint>();
        ushort deadId = 4000;
        int ticks = (int)(seconds * TickRate);
        for (uint tick = 1; tick <= ticks; tick++)
        {
            events.Clear();
            double t = tick / (double)TickRate;
            for (int i = 0; i < n; i++)
            {
                if (i < Near && tick >= stateUntil[i])
                {
                    (state[i], stateUntil[i]) = state[i] switch
                    {
                        MusouEnemyState.Seek => (MusouEnemyState.WindUp, (int)tick + 15),
                        MusouEnemyState.WindUp => (MusouEnemyState.Strike, (int)tick + 3),
                        MusouEnemyState.Strike => (MusouEnemyState.Recover, (int)tick + 10),
                        _ => (MusouEnemyState.Seek, (int)tick + rng.Next(20, 41)),
                    };
                    if (state[i] == MusouEnemyState.WindUp)
                    {
                        windUpAt[i] = tick;
                        if (tick > 30) { pendingRecord[i] = tick; pendingTelegraph[i] = tick; }   // past the first sends
                        events.Add(new MusouEvent(MusouEventKind.Telegraph, tick, (ushort)(i + 1), 1, 0, Vector3.Zero));
                    }
                }
                double a = phase[i] + 5.5 / radius[i] * t;
                var p = new Vector3(radius[i] * (float)Math.Cos(a), radius[i] * (float)Math.Sin(a), 0f);
                enemies[i] = new MusouEnemy((ushort)(i + 1), MusouEnemyType.Boy, i < Near ? state[i] : MusouEnemyState.Seek, p, default, (float)a, 85f, -1, 0, 0, 0);
            }
            if (blastEvery > 0 && tick % blastEvery == 0)
                for (int k = 0; k < 60; k++) events.Add(new MusouEvent(MusouEventKind.Death, tick, deadId++, 1, (byte)MusouDeathKind.Blast, Vector3.UnitZ));
            if (deadId > 4900) deadId = 4000;

            var chosen = window.Step(tick, events, MusouSendRules.EventsPerTick(TickRate));
            foreach (var m in interest.Plan(tick, TickRate, enemies, Vector3.Zero, Box, Director, chosen))
                foreach (var (tag, payload) in m)
                {
                    if (tag == MusouWire.EntitiesTag)
                        foreach (var e in MusouWire.ReadEntities(payload, Box)!)
                        {
                            int i = e.Id - 1;
                            if (i < Near && e.State == MusouEnemyState.WindUp && pendingRecord.Remove(i, out uint at)) recordDelays.Add(tick - at);
                            if (i >= Near && tick > 30)
                            {
                                if (lastFar.TryGetValue(i, out uint l) && radius[i] > 40f) farGaps.Add(tick - l);
                                lastFar[i] = tick;
                            }
                        }
                    else if (tag == MusouWire.EventsTag)
                        foreach (var e in MusouWire.ReadEvents(payload, Box)!)
                            if (e.Kind == MusouEventKind.Telegraph && pendingTelegraph.TryGetValue(e.Enemy - 1, out uint at) && at == e.Tick) { pendingTelegraph.Remove(e.Enemy - 1); telegraphDelays.Add(tick - at); }
                }
        }
        // A wind-up whose record never came before its strike counts as its whole length late (none did in any case measured).
        foreach (var _ in pendingRecord.Where(p => p.Value + 15 < ticks)) recordDelays.Add(15);
        return new Result(recordDelays.DefaultIfEmpty(0u).Average(d => (double)d), recordDelays.DefaultIfEmpty(0u).Max(),
            telegraphDelays.DefaultIfEmpty(0u).Average(d => (double)d), telegraphDelays.DefaultIfEmpty(0u).Max(), recordDelays.Count,
            farGaps.DefaultIfEmpty(0u).Average(d => (double)d), farGaps.DefaultIfEmpty(0u).Max());
    }

    void Print(string name, Result r) =>
        output.WriteLine($"{name}: {r.WindUps} near wind-ups; record after {r.RecordMean:F2} ticks mean, {r.RecordMax} most; telegraph after {r.TelegraphMean:F2} mean, {r.TelegraphMax} most; far Orks (beyond 40 m) every {r.FarGapMean:F1} ticks, {r.FarGapMax} most");

    [Theory]
    [InlineData(300, false, 0)]
    [InlineData(1800, true, 0)]
    [InlineData(1800, true, 45)]
    public void Near_wind_ups_are_sent_at_once_under_a_full_budget(int orks, bool clustered, int blastEvery)
    {
        var old = Measure(orks, clustered, old: true, blastEvery);
        var now = Measure(orks, clustered, old: false, blastEvery);
        string name = $"{orks} orks{(clustered ? " (clustered)" : "")}{(blastEvery > 0 ? $", 60 deaths every {blastEvery} ticks" : "")}";
        Print($"{name}, old", old);
        Print($"{name}, new", now);
        Assert.True(now.WindUps > 50, "enough wind-ups measured");
        Assert.True(now.RecordMax <= 1, $"a near wind-up's record came {now.RecordMax} ticks late");
        Assert.True(now.TelegraphMax <= 1, $"a near wind-up's telegraph came {now.TelegraphMax} ticks late");
        Assert.True(now.FarGapMax <= old.FarGapMax + 2, $"far Orks wait {now.FarGapMax} ticks at most (before: {old.FarGapMax})");
    }
}
