using System.Diagnostics;
using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using Xunit.Abstractions;
using ArsenalContent = SmContent.Extras.Musou.Arsenal.Arsenal;

namespace SmNet.Tests.Extras.Musou.Arsenal;

/// <summary>
/// W4's measurement (docs/new-weapons-design.md section 7, "W4 must measure, and report to the lead before anything is cut"): a charge going off in a Musou crowd of 1,000+ Orks on the synthetic
/// arena. Server tick time around the centre blast and the ring (against the quiet ticks before), the entities and events a tick produces (the Musou events: deaths, their kind), and the bytes the
/// Musou send plan puts on the wire for one client (<see cref="MusouInterest"/>, its budget in force). The numbers are printed (the measure; output lines "CHARGEMEASURE"); the assertions hold only
/// what the charge must not do on any machine. Nothing is cut here: the owner decides.
/// </summary>
public class ChargeMeasureTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
    static readonly MusouBox Box = new(new Vector3(-200f, -200f, -20f), new Vector3(200f, 200f, 60f));
    const int Human = 1;
    static readonly Vector3 At = new(0f, -60f, 0f);

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    internal readonly record struct Result(int Orks, int Killed, int Flown, double QuietMs, double CentreMs, double RingWorstMs, double AfterWorstMs, int EventsCentre, int EventsRingWorst,
        int EventsTotal, int BytesQuiet, int BytesCentre, int BytesRingWorst, int BytesTotal, int Ticks, int EventsDropped, int DeathEvents, int DeathsDeferred, int DeathsWaiting);

    internal static Result Measure(int orks)
    {
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = ArsenalContent.Append(MusouSimTests.Base with { Weapons = [Rifle], Thrown = [], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], MusouRules = Still, RespawnTime = 60f }, "", []);
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        for (int k = 0; k < 90 && w.State != MatchState.Playing; k++) Tick(w);
        w.TfwRespawnAt(Human, At, 0f);
        Tick(w);
        Tick(w, InputButtons.Grenade);
        for (int k = 0; k < 120 && (w.Projectiles.Count == 0 || w.Projectiles[0].Motion != ProjectileMotion.Stuck); k++) Tick(w);
        Assert.Equal(ProjectileMotion.Stuck, w.Projectiles[0].Motion);
        var at = w.Projectiles[0].Position;
        // The crowd: Orks on a disc 22 m wide round the charge (every Ork within the blast; the mix of a battle: 70 % Boyz, 15 % Shootas, 10 % 'Ard Boyz, 5 % Nobz).
        for (int k = 0; k < orks; k++)
        {
            float r = 22f * MathF.Sqrt((k + 0.5f) / orks), a = k * 2.39996f;
            var type = k % 20 == 0 ? MusouEnemyType.Nob : k % 10 == 0 ? MusouEnemyType.ArdBoy : k % 7 == 0 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy;
            Assert.NotEqual(0, w.Musou!.Spawn(type, at + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), a, type == MusouEnemyType.Nob ? 600f : type == MusouEnemyType.ArdBoy ? 170f : 85f));
        }
        int alive0 = w.Musou!.Alive;
        var interest = new MusouInterest();
        var enemies = new MusouEnemy[MusouSim.Capacity];
        var director = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));

        var rows = new List<(double Ms, int Events, int Bytes, int Centres, int Spots)>();
        int deathEvents = 0;
        void Step(InputButtons b = InputButtons.None)
        {
            long t0 = Stopwatch.GetTimestamp();
            Tick(w, b);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            int n = w.Musou.CopyTo(enemies);
            var events = w.Musou.Events.ToList();
            int bytes = interest.Plan(w.Tick, 30, enemies.AsSpan(0, n).ToArray(), at, Box, director, events).Sum(m => m.Sum(s => s.Payload.Length));
            deathEvents += events.Count(e => e.Kind == MusouEventKind.Death);
            rows.Add((ms, events.Count, bytes, w.ChargeCentres, w.ChargeSpots));
        }
        for (int k = 0; k < 20; k++) Step();                         // quiet
        int quietEnd = rows.Count;
        while (w.ChargeCentres == 0 && rows.Count < quietEnd + 400) Step();
        int centreAt = rows.Count - 1;
        for (int k = 0; k < 150; k++) Step();                        // the ring, a second after and the deferred deaths
        int ringEnd = centreAt + 1 + DemolitionChargeRules.SpotTick(15);
        var quiet = rows.Take(quietEnd).Skip(5).ToArray();
        var ring = rows.Skip(centreAt + 1).Take(ringEnd - centreAt).ToArray();
        var after = rows.Skip(ringEnd + 1).ToArray();
        return new Result(orks, alive0 - w.Musou.Alive, w.ChargeFlown, quiet.Select(r => r.Ms).Order().ElementAt(quiet.Length / 2), rows[centreAt].Ms,
            ring.Max(r => r.Ms), after.Max(r => r.Ms), rows[centreAt].Events, ring.Max(r => r.Events), rows.Skip(centreAt).Sum(r => r.Events),
            (int)quiet.Average(r => r.Bytes), rows[centreAt].Bytes, ring.Max(r => r.Bytes), rows.Skip(centreAt).Sum(r => r.Bytes), rows.Count - centreAt, w.Musou.EventsDropped, deathEvents, w.Musou.DeathsDeferred, w.Musou.DeathsWaiting);
    }

    [Theory]
    [Trait("Speed", "Slow")]   // a measurement: three runs of a 1,000 or 1,500 Ork crowd
    [InlineData(1000)]
    [InlineData(1500)]
    public void A_charge_in_a_crowd_of_a_thousand_or_more_costs_the_server_less_than_a_tick(int orks)
    {
        var runs = new List<Result>();
        for (int r = 0; r < 3; r++)
        {
            var m = Measure(orks);
            runs.Add(m);
            output.WriteLine($"CHARGEMEASURE orks {m.Orks} run {r}: killed {m.Killed}, flown {m.Flown}; tick ms: quiet median {m.QuietMs:F2}, centre {m.CentreMs:F2}, ring worst {m.RingWorstMs:F2}, after worst {m.AfterWorstMs:F2}; " +
                $"Musou events: centre tick {m.EventsCentre}, ring worst tick {m.EventsRingWorst}, whole charge {m.EventsTotal} (non-death events dropped over the {MusouSim.MaxEventsPerTick} a tick cap: {m.EventsDropped}; deaths deferred to later ticks: {m.DeathsDeferred}, still waiting {m.DeathsWaiting}; death events seen {m.DeathEvents} for {m.Killed} killed); plan bytes: quiet avg {m.BytesQuiet}, centre tick {m.BytesCentre}, ring worst tick {m.BytesRingWorst}, " +
                $"total over {m.Ticks} ticks {m.BytesTotal}");
        }
        Assert.All(runs, m => Assert.True(m.Killed > 100, $"{m.Killed} killed"));
        Assert.All(runs, m => Assert.True(m.Flown <= DemolitionChargeRules.ThrownMax));
        // The events dropped over the cap are hits and the like (never deaths: those wait and play out over the next ticks).
        Assert.All(runs, m => Assert.Equal(m.Killed, m.DeathEvents));
        double median = runs.Select(m => Math.Max(m.CentreMs, m.RingWorstMs)).Order().ElementAt(1);
        Assert.True(median < 250.0, $"{median:F1} ms: a charge tick took far longer than a 30 Hz tick (about 3 to 5 ms on an idle PC; the bound holds on a loaded one)");
    }
}
