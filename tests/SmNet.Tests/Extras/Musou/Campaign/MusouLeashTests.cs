using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The leashed defenders (TF3, design section D): they hold at home until a target is within 30 m of it or their group is hurt.</summary>
public class MusouLeashTests
{
    const float Aggro = 30f;
    static readonly Vector3 Home = new(0f, 0f, 0f);

    static uint _tick;

    static void Run(MusouSim sim, Vector3 player, int ticks, List<MusouAttack>? attacks = null, bool alive = true)
    {
        attacks ??= new List<MusouAttack>();
        MusouTarget[] targets = [new(1, player, alive)];
        for (int k = 0; k < ticks; k++)
        {
            sim.BeginTick(++_tick);
            sim.Step(targets, null, true, attacks);
        }
    }

    static Vector3 At(MusouSim sim, ushort id) { Assert.True(sim.TryGet(id, out var e)); return e.Position; }

    [Theory]
    [InlineData(MusouEnemyType.Boy)]
    [InlineData(MusouEnemyType.ShootaBoy)]
    [InlineData(MusouEnemyType.ArdBoy)]
    public void A_defender_holds_with_a_player_beyond_the_leash(MusouEnemyType type)
    {
        var sim = new MusouSim(null);
        var id = sim.SpawnLeashed(type, Home, 0f, 85f, group: 1, Aggro);
        Assert.NotEqual((ushort)0, id);
        var attacks = new List<MusouAttack>();
        Run(sim, new Vector3(0f, 31f, 0f), 900, attacks);
        Assert.Equal(Home, At(sim, id));
        Assert.Empty(attacks);
        Assert.Equal(0, sim.TargetOf(id));
        Assert.True(sim.Leashed(id));
        var stranded = new List<ushort>();
        Assert.Equal(0, sim.TakeStranded(stranded));   // 30 s standing is no reason to move it to a gate
    }

    [Fact]
    public void A_player_within_30_m_wakes_it_and_it_goes_home_when_he_leaves()
    {
        var sim = new MusouSim(null);
        var id = sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, Aggro);
        Run(sim, new Vector3(0f, 29f, 0f), 60);
        Assert.True(At(sim, id).Y > 5f, $"{At(sim, id)}");
        Assert.Equal(1, sim.TargetOf(id));
        Run(sim, new Vector3(0f, 80f, 0f), 240);
        Assert.True(Vector3.Distance(At(sim, id), Home) <= 0.6f, $"{At(sim, id)}");
        Assert.Equal(0, sim.TargetOf(id));
    }

    [Fact]
    public void A_dead_player_within_30_m_wakes_nobody()
    {
        var sim = new MusouSim(null);
        var id = sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, Aggro);
        Run(sim, new Vector3(0f, 5f, 0f), 90, alive: false);
        Assert.Equal(Home, At(sim, id));
    }

    [Fact]
    public void Hurting_one_wakes_its_whole_group_for_good_and_no_other()
    {
        var sim = new MusouSim(null);
        var a = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(0f, 0f, 0f), 0f, 85f, 1, Aggro);
        var b = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(3f, 0f, 0f), 0f, 85f, 1, Aggro);
        var other = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(6f, 0f, 0f), 0f, 85f, 2, Aggro);
        var player = new Vector3(0f, 100f, 0f);
        Run(sim, player, 30);
        Assert.Equal(new Vector3(3f, 0f, 0f), At(sim, b));
        Assert.True(sim.Damage(a, 1, 1f, Vector3.Zero, out _));
        Assert.False(sim.Leashed(a));
        Assert.False(sim.Leashed(b));
        Assert.True(sim.Leashed(other));
        Run(sim, player, 60);
        Assert.True(At(sim, a).Y > 5f && At(sim, b).Y > 5f, $"{At(sim, a)} {At(sim, b)}");
        Assert.Equal(new Vector3(6f, 0f, 0f), At(sim, other));
    }

    [Fact]
    public void Release_group_counts_its_members_and_the_bad_calls_are_refused()
    {
        var sim = new MusouSim(null);
        var id = sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 7, Aggro);
        sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 7, Aggro);
        Assert.Equal(0, sim.ReleaseGroup(0));
        Assert.Equal(0, sim.ReleaseGroup(8));
        Assert.Equal(2, sim.ReleaseGroup(7));
        Assert.Equal(0, sim.ReleaseGroup(7));
        Assert.False(sim.Leashed(id));
        Run(sim, new Vector3(0f, 100f, 0f), 60);
        Assert.True(At(sim, id).Y > 5f);
        Assert.Equal((ushort)0, sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 0, Aggro));
        Assert.Equal((ushort)0, sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, float.NaN));
        Assert.Equal((ushort)0, sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, -1f));
        Assert.False(sim.Leashed(0));
        Assert.False(sim.Leashed(MusouSim.Capacity + 1));
    }

    [Fact]
    public void A_plain_spawn_has_no_leash_and_a_reused_slot_forgets_it()
    {
        var sim = new MusouSim(null);
        var plain = sim.Spawn(MusouEnemyType.Boy, Home, 0f, 85f);
        Assert.False(sim.Leashed(plain));
        Run(sim, new Vector3(0f, 100f, 0f), 60);
        Assert.True(At(sim, plain).Y > 5f);

        var small = new MusouSim(null);
        for (int k = 0; k < MusouSim.Capacity; k++) Assert.NotEqual((ushort)0, small.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, Aggro));
        Assert.Equal((ushort)0, small.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, Aggro));   // full
        small.Remove(1);
        var again = small.Spawn(MusouEnemyType.Boy, Home, 0f, 85f);
        Assert.Equal((ushort)1, again);
        Assert.False(small.Leashed(again));
    }

    [Fact]
    public void A_shot_from_a_guard_squad_wakes_the_group_too()
    {
        var sim = new MusouSim(null);
        var id = sim.SpawnLeashed(MusouEnemyType.Boy, Home, 0f, 85f, 1, Aggro);
        var guards = new SmSim.Extras.Musou.Campaign.GuardSim(sim);
        guards.SpawnSquad(new Vector3(0f, 35f, 0f));
        for (uint k = 1; k <= 120 && sim.Leashed(id); k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); }
        Assert.False(sim.Leashed(id));
    }
}
