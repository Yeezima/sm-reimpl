using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Musou Ork movement (owner, 2026-11-09: "storm boys do a lot of walking in place while on the ground. reduce their movement floatiness - the same is true for all orks. make them fan out more and move
/// more intentionally and forcefully with full continuity and flow of movement. increase the distance for active ork behavior and rendering - 300m."). Engine-free measures on the pool with the real
/// rules: a released pack crossing a field to a player beyond a gap in a wall, and a Stormboy squad hunting a player. Each prints its numbers (MOVEMENT lines) and holds the rules' result.
/// </summary>
public class MusouOrkMovementTests(ITestOutputHelper output)
{
    static int Ticks(float seconds) => (int)MathF.Round(seconds * 30f);

    /// <summary>The numbers of one run: per Ork tick in Seek on the ground.</summary>
    sealed class Measure
    {
        public int Seek, Standing, Moving;
        public readonly List<float> Slide = new(), Turn = new();
        public float StandShare => Seek == 0 ? 0f : Standing / (float)Seek;
        public float SlideMean => Slide.Count == 0 ? 0f : Slide.Average();
        public static float P(List<float> v, float q) { if (v.Count == 0) return 0f; var s = v.OrderBy(x => x).ToList(); return s[Math.Clamp((int)(s.Count * q), 0, s.Count - 1)]; }
        public override string ToString() =>
            $"seek ticks {Seek}, standing {StandShare:P1}, facing off the move mean {SlideMean:F1} deg p90 {P(Slide, 0.9f):F1} deg, heading change per tick p95 {P(Turn, 0.95f):F1} deg p99 {P(Turn, 0.99f):F1} deg";
    }

    static float Degrees(float a) { while (a > MathF.PI) a -= MathF.Tau; while (a < -MathF.PI) a += MathF.Tau; return MathF.Abs(a) * 180f / MathF.PI; }

    /// <summary>One tick's samples of <paramref name="ids"/> into <paramref name="m"/> (those farther than <paramref name="from"/> m from <paramref name="target"/> count as standing when slower than 0.3 m/s).</summary>
    static void Sample(MusouSim sim, IEnumerable<ushort> ids, Measure m, Dictionary<ushort, float> heading, Vector3 target, float from)
    {
        foreach (ushort id in ids)
        {
            if (!sim.TryGet(id, out var e) || !e.Alive || e.State != MusouEnemyState.Seek) { heading.Remove(id); continue; }
            m.Seek++;
            float speed = e.Velocity.Length();
            float far = Vector2.Distance(new(e.Position.X, e.Position.Y), new(target.X, target.Y));
            if (speed < 0.3f && far > from) m.Standing++;
            if (speed < 1f || far < 4f) { heading.Remove(id); continue; }   // at the player's feet a token holder waits (fight is off here)
            m.Moving++;
            float move = MathF.Atan2(-e.Velocity.X, e.Velocity.Y);
            m.Slide.Add(Degrees(move - e.Yaw));
            if (heading.TryGetValue(id, out float was)) { m.Turn.Add(Degrees(move - was)); }
            heading[id] = move;
        }
    }

    /// <summary>
    /// The width of a group across its way to the target (m, the spread perpendicular to the line from its middle to the target) and along it.
    /// </summary>
    static (float Across, float Along) Spread(MusouSim sim, IEnumerable<ushort> ids, Vector3 target)
    {
        var at = ids.Select(id => sim.TryGet(id, out var e) && e.Alive ? new Vector2(e.Position.X, e.Position.Y) : (Vector2?)null).OfType<Vector2>().ToArray();
        if (at.Length < 2) return (0f, 0f);
        var mid = at.Aggregate(Vector2.Zero, (s, p) => s + p) / at.Length;
        var way = Vector2.Normalize(new Vector2(target.X, target.Y) - mid);
        var side = new Vector2(-way.Y, way.X);
        float across = MathF.Sqrt(at.Average(p => MathF.Pow(Vector2.Dot(p - mid, side), 2f)));
        float along = MathF.Sqrt(at.Average(p => MathF.Pow(Vector2.Dot(p - mid, way), 2f)));
        return (across, along);
    }

    /// <summary>How evenly the Orks round the target are spread round it: 0 all on one side, 1 evenly round (one less the mean resultant length of their bearings from it).</summary>
    static float Ring(MusouSim sim, IEnumerable<ushort> ids, Vector3 target, float within)
    {
        float sx = 0f, sy = 0f; int n = 0;
        foreach (ushort id in ids)
        {
            if (!sim.TryGet(id, out var e) || !e.Alive) continue;
            var d = new Vector2(e.Position.X - target.X, e.Position.Y - target.Y);
            float l = d.Length();
            if (l > within || l < 1e-3f) continue;
            sx += d.X / l; sy += d.Y / l; n++;
        }
        return n == 0 ? 0f : 1f - MathF.Sqrt(sx * sx + sy * sy) / n;
    }

    /// <summary>The pack's run: 120 Boyz released from a 9 m disc, a player 170 m off beyond a 24 m gap in a wall; 40 s.</summary>
    (Measure Move, float Across, float Along, float Ring, int Near) Pack()
    {
        var level = new NavigationTests.Level().Floor(0f, 0f, 240f, 160f).Wall(110f, 0f, 110f, 68f).Wall(110f, 92f, 110f, 160f);
        var sim = new MusouSim(NavGraph.Build(level.Mesh(), null, NavSettings.From(new SimConfig()))) { Swarm = MusouSwarmRules.Default };
        var ids = new ushort[120];
        for (int k = 0; k < ids.Length; k++)
        {
            float a = k * 2.399963f, r = 9f * MathF.Sqrt((k + 0.5f) / ids.Length);
            ids[k] = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(30f + MathF.Cos(a) * r, 80f + MathF.Sin(a) * r, 0f), 0f, 85f, 7, 5f);
        }
        sim.ReleaseGroup(7);
        var target = new Vector3(200f, 120f, 0f);
        var targets = new[] { new MusouTarget(1, target, true) };
        var attacks = new List<MusouAttack>();
        var m = new Measure();
        var heading = new Dictionary<ushort, float>();
        float across = 0f, along = 0f;
        bool spreadTaken = false;
        uint tick = 0;
        for (int k = 0; k < Ticks(40f); k++)
        {
            sim.BeginTick(++tick);
            sim.Step(targets, null, false, attacks);
            if (tick > Ticks(MusouRules.Default.SpawnSeconds) + 2) Sample(sim, ids, m, heading, target, 14f);
            // The pack's shape on the open ground past the wall, once its middle is 45 m from the player.
            if (!spreadTaken)
            {
                var mid = ids.Select(id => sim.TryGet(id, out var e) ? e.Position : default).Aggregate(Vector3.Zero, (s, p) => s + p) / ids.Length;
                if (mid.X > 115f && Vector2.Distance(new(mid.X, mid.Y), new(target.X, target.Y)) < 45f) { (across, along) = Spread(sim, ids, target); spreadTaken = true; }
            }
        }
        int near = ids.Count(id => sim.TryGet(id, out var e) && Vector3.Distance(e.Position, target) < 12f);
        return (m, across, along, Ring(sim, ids, target, 12f), near);
    }

    [Fact]
    public void A_pack_runs_with_purpose_faces_its_way_and_fans_out()
    {
        var (m, across, along, ring, near) = Pack();
        output.WriteLine($"MOVEMENT pack: {m}; across {across:F1} m, along {along:F1} m (ratio {along / MathF.Max(0.1f, across):F2}); ring evenness {ring:F2} with {near} within 12 m");
        Assert.True(m.Seek > 10_000, $"{m.Seek} seek ticks");
        Assert.True(m.StandShare < 0.05f, $"standing {m.StandShare:P1} of the seek ticks far from the player");
        Assert.True(m.SlideMean < MusouMoveRules.MaxSlideDegrees, $"facing off the move by {m.SlideMean:F1} deg on average");
        Assert.True(Measure.P(m.Turn, 0.95f) < MusouMoveRules.MaxTurnDegreesPerTick, $"heading change p95 {Measure.P(m.Turn, 0.95f):F1} deg a tick");
        Assert.True(across > MusouMoveRules.LeastAcrossMetres, $"the pack {across:F1} m across its way");
        Assert.True(ring > 0.25f, $"ring evenness {ring:F2}");
    }

    // ---------------------------------------------------------------- Stormboys

    static readonly TfwLayout Places = new(
        new Vector3(20f, 80f, 0f),
        [new(60f, 30f, 0f), new(170f, 30f, 0f), new(190f, 130f, 0f)],
        new Vector3(110f, 140f, 0f),
        [new(66f, 36f, 0f), new(176f, 36f, 0f), new(184f, 124f, 0f), new(116f, 134f, 0f)]);

    /// <summary>The squads' hunt on the S3 tests' floor: their share of ground ticks standing still while the player is far (over 25 m), and their speed on the ground.</summary>
    (Measure Move, float GroundSpeed, int Landings, float OldInPlace, float NewInPlace, float Slide) Storm()
    {
        var nav = NavGraph.Build(new NavigationTests.Level().Floor(0f, 0f, 240f, 160f).Wall(120f, 0f, 120f, 68f).Wall(120f, 92f, 120f, 160f).Mesh(), null, NavSettings.From(new SimConfig()));
        var sim = new MusouSim(nav) { Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var battle = new TfwBattle(sim, new GuardSim(sim), Places, 100.0, 1, false, default, TfwBattleRules.EliteBase, false)
        { Trickle = false, BossOrksOn = false, WeirdboyzOn = false, Patrols = true, BigShootasOn = false };
        battle.Start();
        var prey = new Vector3(30f, 150f, 0f);
        var feet = new[] { prey };
        var targets = new[] { new MusouTarget(1, prey, true) };
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        void Run(int n) { for (int k = 0; k < n; k++) { sim.BeginTick(++tick); battle.Step(feet); sim.Step(targets, null, false, attacks); } }
        Run(Ticks(TfwStormboyRules.FirstSeconds) + 5);
        battle.SecureForTests(1);
        Run(1);
        foreach (var s in battle.StormSquads()) foreach (ushort id in s.Ids.Take(4)) sim.Damage(id, 1, 1000f, Vector3.Zero, out _);   // they hunt the player
        Run(1);
        var m = new Measure();
        var heading = new Dictionary<ushort, float>();
        float speedSum = 0f; int speedN = 0;
        int landed = sim.Landings;
        var tracks = new Dictionary<ushort, MusouGaitTrack>();
        int ground = 0, oldInPlace = 0, newInPlace = 0, runFrames = 0;
        float slide = 0f;
        for (int k = 0; k < Ticks(30f); k++)
        {
            Run(1);
            var ids = battle.StormSquads().SelectMany(s => s.Ids).ToArray();
            Sample(sim, ids, m, heading, prey, 25f);
            foreach (ushort id in ids)
            {
                if (!sim.TryGet(id, out var e) || !e.Alive) { tracks.Remove(id); continue; }
                if (e.State != MusouEnemyState.Seek) continue;   // as the client: only a seeking Ork's track is stepped (in the air he plays the idle)
                tracks.TryGetValue(id, out var track);
                bool running = track.Step(new Vector2(e.Position.X, e.Position.Y), 1f / 30f);
                tracks[id] = track;
                float v = e.Velocity.Length();
                if (Vector3.Distance(e.Position, prey) > 25f) { speedSum += v; speedN++; }
                // As drawn: before, a seeking Ork always played the run, so slower than 0.3 m/s he ran on the spot; now the run plays only while his drawn speed says he runs.
                ground++;
                if (v < 0.3f) { oldInPlace++; if (running) newInPlace++; }
                if (running) { runFrames++; slide += MathF.Abs(v - MusouGaitRules.Rate(track.Speed, 6.47f) * 6.47f); }   // the feet's speed as drawn against his true speed
            }
        }
        return (m, speedN == 0 ? 0f : speedSum / speedN, sim.Landings - landed, oldInPlace / (float)Math.Max(1, ground), newInPlace / (float)Math.Max(1, ground), slide / Math.Max(1, runFrames));
    }

    [Fact]
    public void Stormboys_on_the_ground_run_on_between_hops_instead_of_standing()
    {
        var (m, speed, landings, oldInPlace, newInPlace, slide) = Storm();
        output.WriteLine($"MOVEMENT stormboys: {m}; mean ground speed {speed:F2} m/s; {landings} landings; run on the spot as drawn: old clip rule {oldInPlace:P1}, gait rule {newInPlace:P1}; feet slide while running {slide:F2} m/s");
        Assert.True(landings >= 12, $"{landings} landings");
        Assert.True(m.StandShare < 0.15f, $"standing {m.StandShare:P1} of their ground ticks with the player far");
        Assert.True(m.SlideMean < MusouMoveRules.MaxSlideDegrees, $"facing off the move by {m.SlideMean:F1} deg on average");
        Assert.True(newInPlace < 0.03f, $"the run plays on the spot in {newInPlace:P1} of the ground frames");   // only the eased speed's stop (about a quarter second)
        Assert.True(oldInPlace > 0.3f, $"the old clip rule ran on the spot in only {oldInPlace:P1}");
        Assert.True(slide < 0.4f, $"feet slide {slide:F2} m/s while running");
    }

    // ---------------------------------------------------------------- the drawn gait

    [Fact]
    public void The_drawn_run_follows_the_speed_with_no_run_on_the_spot_and_the_feet_matched()
    {
        const float clip = 6.47f, dt = 1f / 60f;
        var track = new MusouGaitTrack();
        var at = new Vector2(10f, 10f);
        // Standing (pushed about a little, 0.2 m/s): the idle.
        for (int f = 0; f < 60; f++) { at += new Vector2(0.2f * dt * MathF.Sin(f * 0.7f), 0f); Assert.False(track.Step(at, dt), $"frame {f}: runs at {track.Speed:F2} m/s"); }
        // Off at a Boy's 5.2 m/s: the run within 0.1 s, its feet at his speed.
        int started = -1;
        for (int f = 0; f < 60; f++) { at += new Vector2(5.2f * dt, 0f); if (track.Step(at, dt) && started < 0) started = f; }
        output.WriteLine($"GAIT run from frame {started}, eased speed {track.Speed:F2} m/s");
        Assert.InRange(started, 0, 6);
        Assert.InRange(MusouGaitRules.Rate(track.Speed, clip) * clip, 5.0f, 5.4f);
        Assert.True(MusouGaitRules.Slide(track.Speed, clip, true) < 0.2f);
        // A slow circle at 2.3 m/s, a Gretchin's 6.2 and 8 m/s: still matched.
        foreach (float v in new[] { 2.3f, 6.2f, 8f }) Assert.True(MusouGaitRules.Slide(v, clip, true) < 0.05f, $"{v} m/s slides");
        // A stop: the idle within a quarter second, no flicker on the way.
        int stopped = -1, flips = 0;
        bool was = true;
        for (int f = 0; f < 60; f++) { bool r = track.Step(at, dt); if (r != was) flips++; was = r; if (!r && stopped < 0) stopped = f; }
        Assert.InRange(stopped, 0, 20);
        Assert.Equal(1, flips);
        // A snap of the drawn place is no speed.
        track.Step(at + new Vector2(10f, 0f), dt);
        Assert.False(track.Running);
        // An unmeasured clip falls back to 6 m/s.
        Assert.Equal(5.2f / MusouGaitRules.FallbackClipSpeed, MusouGaitRules.Rate(5.2f, 0f), 3);
    }

    // ---------------------------------------------------------------- 300 m

    static TfwBattle FrozenBattle(out MusouSim sim)
    {
        sim = new MusouSim(null) { Frozen = true };
        var battle = new TfwBattle(sim, new GuardSim(sim), SmNet.Tests.Extras.Musou.Campaign.TfwTestArena.Layout(), 100.0, 1, false, default, TfwBattleRules.EliteBase, guardsmen: true)
        { Trickle = false, Patrols = false, BossOrksOn = false, StormboysOn = false, BigShootasOn = false, WeirdboyzOn = false, RokkitsOn = false, OrkAggregates = true };   // the 300 m levels of the Ork aggregates (before 2026-11-09; OrkArmyAggregateTests of none)
        battle.Start();
        return battle;
    }

    [Fact]
    public void An_army_within_300_m_is_real_and_one_past_330_m_folds_back()
    {
        Assert.Equal(300f, TfwArmyRules.NearMetres);
        Assert.Equal(330f, TfwArmyRules.FarMetres);
        var battle = FrozenBattle(out var sim);
        var anchor = new Vector3(80f, -60f, 0f);
        var orks = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 0, 120, anchor, 0.3f)!;
        Vector3[] at280 = [anchor + new Vector3(0f, 280f + orks.Radius, 0f)], at320 = [anchor + new Vector3(0f, 320f + orks.Radius, 0f)], at400 = [anchor + new Vector3(0f, 400f + orks.Radius, 0f)];
        battle.Step(at400);
        Assert.Equal(0, orks.Real);
        for (int t = 0; t < 12; t++) battle.Step(at280);
        Assert.Equal(120, orks.Real);   // 280 m off: every Ork real (the brief's 90 m kept it an impostor)
        for (int t = 0; t < 12; t++) battle.Step(at320);
        Assert.Equal(120, orks.Real);   // inside the hysteresis band
        for (int t = 0; t < 12; t++) battle.Step(at400);
        Assert.Equal(0, orks.Real);
        Assert.Equal(120, orks.Count);
        output.WriteLine($"RANGE real at 280 and 320 m, folded at 400 m; pool alive {sim.Alive}");
    }

    [Fact]
    public void With_the_cap_full_the_army_nearest_the_players_is_the_real_one()
    {
        var battle = FrozenBattle(out _);
        var farAnchor = new Vector3(80f, -60f, 0f);
        var nearAnchor = farAnchor + new Vector3(0f, 250f, 0f);
        var far = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 0, TfwArmyRules.NearOrksMost, farAnchor, 0.3f)!;
        Vector3[] byFar = [farAnchor + new Vector3(0f, 20f + far.Radius, 0f)];
        for (int t = 0; t < 60; t++) battle.Step(byFar);
        Assert.Equal(TfwArmyRules.NearOrksMost, far.Real);   // the first army takes the whole cap
        var near = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 1, 300, nearAnchor, 0.3f)!;
        Vector3[] byNear = [nearAnchor + new Vector3(0f, 30f + near.Radius, 0f)];   // the human goes on to the second: the first is now about 250 m behind him
        int ticks = 0;
        while (near.Real < 300 && ticks < 200) { battle.Step(byNear); ticks++; }
        output.WriteLine($"RANGE the nearer army real after {ticks} ticks; the farther keeps {far.Real} real");
        Assert.Equal(300, near.Real);
        for (int t = 0; t < 10; t++) battle.Step(byNear);
        Assert.Equal(300, near.Real);
        Assert.Equal(TfwArmyRules.NearOrksMost - 300, far.Real);   // it gave up what the nearer needed and stays partly real (it is within 300 m)
        Assert.True(battle.ArmyOrksAlive <= TfwArmyRules.NearOrksMost);
    }
}
