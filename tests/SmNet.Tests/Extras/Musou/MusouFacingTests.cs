using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Orks face the one they engage (MusouSim.Facing.cs; owner 2026-11-08): a gunner fires only within <see cref="MusouFacingRules.FireConeDegrees"/> of its target, turning first; an engaged Ork moves facing its target.</summary>
public class MusouFacingTests
{
    static float Off(MusouSim sim, ushort id, Vector3 target)
    {
        Assert.True(sim.TryGet(id, out var e));
        var want = MathF.Atan2(-(target.X - e.Position.X), target.Y - e.Position.Y);
        return MathF.Abs(MathF.IEEERemainder(want - sim.YawOf(id), MathF.Tau));
    }

    static List<(uint Tick, float Off)> Shots(MusouSim sim, ushort id, Vector3 target, int ticks)
    {
        var shots = new List<(uint, float)>();
        var attacks = new List<MusouAttack>();
        MusouTarget[] targets = [new(1, target, true)];
        for (uint t = 1; t <= ticks; t++)
        {
            sim.BeginTick(t);
            attacks.Clear();
            sim.Step(targets, null, true, attacks);
            foreach (var ev in sim.Events.Where(e => e.Enemy == id && (e.Kind == MusouEventKind.Tracer || (e.Kind == MusouEventKind.Telegraph && e.Value == MusouRokkitRules.FireCue)))) shots.Add((t, Off(sim, id, target)));
        }
        return shots;
    }

    [Theory]
    [InlineData(MusouEnemyType.ShootaBoy)]
    [InlineData(MusouEnemyType.RokkitBoy)]
    public void A_gunner_facing_away_turns_first_and_every_round_leaves_within_the_cone(MusouEnemyType type)
    {
        var sim = new MusouSim(null);
        var target = new Vector3(0f, 0f, 0f);
        ushort id = sim.Spawn(type, new Vector3(0f, type == MusouEnemyType.RokkitBoy ? 35f : 15f, 0f), 0f, 100f);   // 15 m (35 for the rocket) north, yaw 0 faces +y: straight away from the target
        Assert.True(MathF.Abs(Off(sim, id, target) - MathF.PI) < 0.01f);
        var shots = Shots(sim, id, target, 30 * 8);
        Assert.NotEmpty(shots);
        Assert.All(shots, s => Assert.True(s.Off <= MusouFacingRules.FireConeRadians + 0.01f, $"fired {s.Off} rad off its target"));
        // A half turn at 720 degrees a second takes about a quarter second: it did not fire on the first tick.
        Assert.True(shots[0].Tick > 3, $"first round on tick {shots[0].Tick}");
    }

    [Fact]
    public void A_gunner_too_close_backs_off_still_facing_its_target()
    {
        var sim = new MusouSim(null);
        var target = Vector3.Zero;
        ushort id = sim.Spawn(MusouEnemyType.ShootaBoy, new Vector3(0f, 4f, 0f), 0f, 100f);   // inside its minimum range, facing away
        var attacks = new List<MusouAttack>();
        MusouTarget[] targets = [new(1, target, true)];
        for (uint t = 1; t <= 60; t++) { sim.BeginTick(t); sim.Step(targets, null, true, attacks); }
        Assert.True(sim.TryGet(id, out var e));
        Assert.True(e.Position.Y > 4.2f, "backed off");
        Assert.True(Off(sim, id, target) < 0.3f, $"{Off(sim, id, target)} rad off while backing off");
    }

    [Fact]
    public void An_engaged_boy_closes_in_facing_its_target_even_when_the_way_is_sideways()
    {
        var sim = new MusouSim(null);
        ushort id = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 8f, 0f), MathF.PI / 2f, 100f);
        var attacks = new List<MusouAttack>();
        MusouTarget[] targets = [new(1, Vector3.Zero, true)];
        for (uint t = 1; t <= 60; t++) { sim.BeginTick(t); sim.Step(targets, null, false, attacks); }
        Assert.True(Off(sim, id, Vector3.Zero) < 0.3f);
    }
}
