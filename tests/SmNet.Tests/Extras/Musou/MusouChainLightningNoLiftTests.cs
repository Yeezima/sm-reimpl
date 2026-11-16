using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Librarian's chain lightning (Musou Mode only; owner order 2026-11-16): it never lifts or knocks back an Ork, the first target's or a chained one's; it still hurts (a Hit event, the flinch's trigger) and
/// reaches 52.5 m to the first target (+15 m), the 16 m jump unchanged; the Shockwave keeps its throw. Classic PvP has no Librarian and runs none of this.
/// </summary>
public class MusouChainLightningNoLiftTests
{
    const int Rate = 30;
    const float Dt = 1f / Rate;
    static readonly LibrarianPress Hold = new(true, false), ShockKey = new(false, true);

    static (MusouSim Sim, LibrarianPsyker Psyker) Arena(out ushort first, out ushort second, float health)
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        first = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 20f, 0f), 0f, health);
        second = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 34f, 0f), 0f, health);   // 14 m on: inside the 16 m jump
        return (sim, new LibrarianPsyker());
    }

    static void Cast(MusouSim sim, LibrarianPsyker p, LibrarianPress press, int ticks, ref uint tick)
    {
        var eye = new Vector3(0f, 0f, 1f);
        for (int k = 0; k < ticks; k++) { sim.BeginTick(++tick); p.Step(sim, new LibrarianCaster(1, Vector3.Zero, eye, 0f, 0f), press, Dt); }
    }

    [Fact]
    public void A_living_first_and_chained_Ork_is_hurt_but_not_lifted_pushed_or_driven()
    {
        var (sim, p) = Arena(out var a, out var b, 1e6f);
        uint tick = 0;
        sim.TryGet(a, out var a0); sim.TryGet(b, out var b0);
        Cast(sim, p, Hold, Rate, ref tick);
        Assert.Equal(2, p.LastStruck);
        foreach (var (id, before) in new[] { (a, a0), (b, b0) })
        {
            sim.TryGet(id, out var e);
            Assert.True(e.Health < 1e6f);
            Assert.Equal(before.Position, e.Position);   // not lifted, not knocked back
            Assert.Equal(before.State, e.State);          // not Airborne or Knocked by the sim; the stagger is the client's flinch on the Hit event
            Assert.Equal(Vector2.Zero, e.Velocity);
        }
        Assert.Contains(sim.Events, e => e.Kind == MusouEventKind.Hit && e.Enemy == a && e.Value == 1);   // the hit still reads as a hit (the flinch's trigger)
    }

    [Fact]
    public void A_kill_by_the_chain_is_never_a_thrown_blast()
    {
        var (sim, p) = Arena(out var a, out var b, 30f);   // dies within a second
        uint tick = 0;
        Cast(sim, p, Hold, Rate, ref tick);
        var kills = new List<MusouKill>();
        sim.TakeKills(kills);
        Assert.Contains(kills, k => k.Enemy == a);
        Assert.Contains(kills, k => k.Enemy == b);
        foreach (var k in kills) Assert.Equal(MusouDeathKind.BoltBlast, k.Cause);   // torn apart where he stands; never Blast (a thrown body)
        foreach (var e in sim.Events.Where(e => e.Kind == MusouEventKind.Death)) Assert.Equal(0f, e.Vector.Z);
    }

    [Fact]
    public void The_shockwave_keeps_its_throw()
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        var boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 10f, 0f), 0f, 1e6f);
        var p = new LibrarianPsyker();
        uint tick = 0;
        Cast(sim, p, ShockKey, 1, ref tick);
        for (int k = 0; k < 8; k++) { sim.BeginTick(++tick); sim.Step(new[] { new MusouTarget(1, Vector3.Zero, true) }, null, false, new List<MusouAttack>()); }
        sim.TryGet(boy, out var e);
        Assert.True(e.Position.Y > 12f, $"thrown away: {e.Position.Y}");
    }

    [Fact]
    public void The_range_is_52_and_a_half_to_the_first_target_and_the_jump_stays_16()
    {
        Assert.Equal(52.5f, LibrarianRules.LightningRange);
        Assert.Equal(16f, LibrarianRules.JumpRadius);
    }
}
