using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The elite and Boss Orks act on their target as the regular Orks do (the owner, 2026-11-09: "boss and elite orks are having issues that we fixed with regular orks. / not taking visibly doing
/// things when the player or other enemy targets ... / boss ork shootas just run away instead of backpedaling and shooting / boss ork boys dont take melee swings, they just kind of run around in
/// melee combat"). One Ork of each kind against one target on open ground in a war's pool (the crowd rules on), with the fight on: the melee kinds swing within a few seconds and face their target;
/// the shooters keep facing a target that walks in on them, back off while they fire and never turn their back; the same against a Guard squad (a target with a radius) as against a player.
/// </summary>
public class MusouEliteOrkTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;

    static float Degrees(float a) { while (a > MathF.PI) a -= MathF.Tau; while (a < -MathF.PI) a += MathF.Tau; return MathF.Abs(a) * 180f / MathF.PI; }
    static float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y);

    sealed record Melee(int WindUps, int Blows, float FirstBlow, float FacingOffMean, float MeanDistance);

    /// <summary>A melee Ork 10 m from a standing target, 12 s with the fight on: wind-ups, blows that landed, the first blow's time, the facing off the target within 6 m, the mean distance.</summary>
    static Melee RunMelee(MusouEnemyType type, bool squad, bool old = false)
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var prey = new Vector3(0f, 0f, 0f);
        ushort id = sim.Spawn(type, new Vector3(10f, 0f, 0f), MathF.PI / 2f, 1e6f);
        var targets = new[] { squad ? new MusouTarget(-3, prey, true, Radius: 2f) : new MusouTarget(1, prey, true) };
        var attacks = new List<MusouAttack>();
        int windUps = 0, blows = 0, near = 0; float first = float.NaN, off = 0f, dist = 0f; var last = MusouEnemyState.Spawning;
        for (int t = 0; t < 12 * 30; t++)
        {
            sim.BeginTick((uint)(t + 1));
            attacks.Clear();
            sim.Step(targets, null, true, attacks);
            sim.TryGet(id, out var e);
            if (e.State == MusouEnemyState.WindUp && last != MusouEnemyState.WindUp) windUps++;
            last = e.State;
            foreach (var a in attacks) if (a.Melee && a.Enemy == id) { blows++; if (float.IsNaN(first)) first = t * Dt; }
            float d = Vector2.Distance(new(e.Position.X, e.Position.Y), new(prey.X, prey.Y));
            dist += d;
            if (d < 6f && e.State != MusouEnemyState.Spawning) { near++; off += Degrees(YawTo(e.Position, prey) - e.Yaw); }
        }
        return new Melee(windUps, blows, first, near == 0 ? 0f : off / near, dist / (12 * 30));
    }

    sealed record Ranged(int Rounds, float FacingOffMean, float FacingOffP95, int BackTurned, int Backing, float StartGap, float EndGap);

    /// <summary>A shooter 22 m from a target that walks straight at him at 3 m/s for 10 s, then 4 s standing: rounds, facing off the target, ticks moving with his back to it, ticks backing off facing it.</summary>
    static Ranged RunRanged(MusouEnemyType type, bool squad)
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var prey = new Vector3(0f, 0f, 0f);
        ushort id = sim.Spawn(type, new Vector3(0f, 22f, 0f), MathF.PI, 1e6f);
        var attacks = new List<MusouAttack>();
        int rounds = 0, backTurned = 0, backing = 0;
        var offs = new List<float>();
        sim.TryGet(id, out var s0);
        float startGap = Vector3.Distance(s0.Position, prey);
        for (int t = 0; t < 14 * 30; t++)
        {
            if (t < 10 * 30) prey += new Vector3(0f, 3f * Dt, 0f);
            var targets = new[] { squad ? new MusouTarget(-3, prey, true, Radius: 2f) : new MusouTarget(1, prey, true) };
            sim.BeginTick((uint)(t + 1));
            attacks.Clear();
            sim.Step(targets, null, true, attacks);
            sim.TryGet(id, out var e);
            foreach (var a in attacks) if (!a.Melee && a.Enemy == id) rounds++;
            if (e.State == MusouEnemyState.Spawning) continue;
            float off = Degrees(YawTo(e.Position, prey) - e.Yaw);
            offs.Add(off);
            if (e.Velocity.Length() > 0.8f)
            {
                var away = Vector2.Normalize(new Vector2(e.Position.X - prey.X, e.Position.Y - prey.Y));
                bool retreating = Vector2.Dot(Vector2.Normalize(e.Velocity), away) > 0.5f;
                if (retreating && off > 100f) backTurned++;
                if (retreating && off < 45f) backing++;
            }
        }
        sim.TryGet(id, out var end);
        offs.Sort();
        return new Ranged(rounds, offs.Average(), offs[(int)(offs.Count * 0.95)], backTurned, backing, startGap, Vector3.Distance(end.Position, prey));
    }

    public static readonly TheoryData<MusouEnemyType> MeleeKinds = new() { MusouEnemyType.Boy, MusouEnemyType.ArdBoy, MusouEnemyType.Nob, MusouEnemyType.BossBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob };
    public static readonly TheoryData<MusouEnemyType> RangedKinds = new() { MusouEnemyType.ShootaBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BigShootaNob };

    [Theory]
    [MemberData(nameof(MeleeKinds))]
    public void A_melee_ork_swings_at_its_target_and_faces_it(MusouEnemyType type)
    {
        foreach (bool squad in new[] { false, true })
        {
            var m = RunMelee(type, squad);
            output.WriteLine($"ELITE melee {type} vs {(squad ? "squad" : "player")}: wind-ups {m.WindUps}, blows {m.Blows}, first blow {m.FirstBlow:F1} s, facing off within 6 m {m.FacingOffMean:F1} deg, mean distance {m.MeanDistance:F1} m");
            Assert.True(m.Blows >= 2, $"{type} vs {(squad ? "squad" : "player")}: {m.Blows} blows in 12 s");
            Assert.True(m.FirstBlow < 6f, $"{type}: first blow at {m.FirstBlow:F1} s");
            Assert.True(m.FacingOffMean < 30f, $"{type}: facing off its target {m.FacingOffMean:F1} deg");
        }
    }

    [Theory]
    [MemberData(nameof(RangedKinds))]
    public void A_shooter_backs_off_facing_its_target_and_fires(MusouEnemyType type)
    {
        foreach (bool squad in new[] { false, true })
        {
            var r = RunRanged(type, squad);
            output.WriteLine($"ELITE ranged {type} vs {(squad ? "squad" : "player")}: rounds {r.Rounds}, facing off mean {r.FacingOffMean:F1} deg p95 {r.FacingOffP95:F1}, ticks retreating back turned {r.BackTurned}, backing off facing {r.Backing}, gap {r.StartGap:F0} -> {r.EndGap:F1} m");
            Assert.True(r.Rounds >= 3, $"{type}: {r.Rounds} rounds");
            Assert.True(r.BackTurned <= 3, $"{type}: {r.BackTurned} ticks running away with his back to the target");
            Assert.True(r.FacingOffMean < 30f, $"{type}: facing off its target {r.FacingOffMean:F1} deg");
        }
    }

    /// <summary>
    /// A melee Ork of <paramref name="type"/> 10 m from a standing target with 12 Boyz already round it (the crowd holds the target's melee tokens), 12 s with the fight on: its wind-ups, blows and
    /// first blow, and its facing off the target within 6 m.
    /// </summary>
    static Melee RunMeleeInCrowd(MusouEnemyType type, bool squad)
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var prey = Vector3.Zero;
        for (int k = 0; k < 12; k++) { float a = k * MathF.Tau / 12f; sim.Spawn(MusouEnemyType.Boy, new Vector3(MathF.Cos(a) * 2.5f, MathF.Sin(a) * 2.5f, 0f), a + MathF.PI / 2f, 1e6f); }
        ushort id = sim.Spawn(type, new Vector3(10f, 0f, 0f), MathF.PI / 2f, 1e6f);
        var targets = new[] { squad ? new MusouTarget(-3, prey, true, Radius: 2f) : new MusouTarget(1, prey, true) };
        var attacks = new List<MusouAttack>();
        int windUps = 0, blows = 0, near = 0; float first = float.NaN, off = 0f, dist = 0f; var last = MusouEnemyState.Spawning;
        for (int t = 0; t < 12 * 30; t++)
        {
            sim.BeginTick((uint)(t + 1));
            attacks.Clear();
            sim.Step(targets, null, true, attacks);
            sim.TryGet(id, out var e);
            if (e.State == MusouEnemyState.WindUp && last != MusouEnemyState.WindUp) windUps++;
            last = e.State;
            foreach (var a in attacks) if (a.Melee && a.Enemy == id) { blows++; if (float.IsNaN(first)) first = t * Dt; }
            float d = Vector2.Distance(new(e.Position.X, e.Position.Y), new(prey.X, prey.Y));
            dist += d;
            if (d < 6f && e.State != MusouEnemyState.Spawning) { near++; off += Degrees(YawTo(e.Position, prey) - e.Yaw); }
        }
        return new Melee(windUps, blows, first, near == 0 ? 0f : off / near, dist / (12 * 30));
    }

    public static readonly TheoryData<MusouEnemyType> CrowdKinds = new() { MusouEnemyType.Warboss, MusouEnemyType.Nob, MusouEnemyType.BossBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob };

    [Theory]
    [MemberData(nameof(CrowdKinds))]
    public void An_elite_or_boss_swings_through_a_crowd_that_holds_the_targets_tokens(MusouEnemyType type)
    {
        foreach (bool squad in new[] { false, true })
        {
            var m = RunMeleeInCrowd(type, squad);
            output.WriteLine($"ELITE crowd {type} vs {(squad ? "squad" : "player")}: wind-ups {m.WindUps}, blows {m.Blows}, first blow {m.FirstBlow:F1} s, facing off within 6 m {m.FacingOffMean:F1} deg, mean distance {m.MeanDistance:F1} m");
            Assert.True(m.Blows >= 2, $"{type} vs {(squad ? "squad" : "player")} in a crowd: {m.Blows} blows in 12 s");
            Assert.True(m.FirstBlow < 6f, $"{type}: first blow at {m.FirstBlow:F1} s");
            Assert.True(m.FacingOffMean < 30f, $"{type}: facing off its target {m.FacingOffMean:F1} deg");
        }
    }
}
