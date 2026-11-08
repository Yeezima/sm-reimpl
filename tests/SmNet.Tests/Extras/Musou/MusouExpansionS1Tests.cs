using System.Numerics;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Expansion stage S1 (docs/musou-expansion.md): the type byte on the wire, target preference by kind, the neutral Ork blast.</summary>
public class MusouExpansionS1Tests
{
    static readonly MusouBox Box = new(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));

    // ---------------------------------------------------------------- wire

    [Fact]
    public void The_new_kinds_follow_the_old_ones_in_the_documented_order_and_have_rows()
    {
        Assert.Equal(12, (int)MusouEnemyType.BombSquig);
        Assert.Equal([MusouEnemyType.BombSquig, MusouEnemyType.RokkitBoy, MusouEnemyType.Stormboy, MusouEnemyType.BigShootaNob, MusouEnemyType.ScrapMek],
            Enum.GetValues<MusouEnemyType>().Skip(12).ToArray());
        var rules = MusouRules.Default;
        Assert.Equal(30f, rules.Of(MusouEnemyType.BombSquig).Health);
        Assert.Equal(85f, rules.Of(MusouEnemyType.RokkitBoy).Health);
        Assert.Equal(120f, rules.Of(MusouEnemyType.Stormboy).Health);
        Assert.Equal(600f, rules.Of(MusouEnemyType.BigShootaNob).Health);
        Assert.Equal(250f, rules.Of(MusouEnemyType.ScrapMek).Health);
        foreach (var t in Enum.GetValues<MusouEnemyType>()) Assert.Equal(t, rules.Of(t).Type);
        Assert.Equal(64u, Protocol.Version);   // main's protocol (headshots raised it to 64 at integration); the Musou expansion does not raise it
    }

    [Fact]
    public void An_entity_with_a_type_of_sixteen_or_more_round_trips()
    {
        var list = Enum.GetValues<MusouEnemyType>().Select((t, k) => new MusouEntity((ushort)(k + 1), new Vector3(k, -k, 2f), 1f, t, MusouEnemyState.Knocked)).ToList();
        Assert.Contains(list, e => (byte)e.Type >= 16);
        var back = MusouWire.ReadEntities(MusouWire.WriteEntities(list, Box), Box);
        Assert.NotNull(back);
        Assert.Equal(list.Select(e => (e.Id, e.Type, e.State)), back.Select(e => (e.Id, e.Type, e.State)));
        var bytes = MusouWire.WriteEntities([new MusouEntity(1, Vector3.Zero, 0f, MusouEnemyType.ScrapMek, MusouEnemyState.Vanishing)], Box);
        bytes[^1] = 200;   // an unknown type byte is a malformed record
        Assert.Null(MusouWire.ReadEntities(bytes, Box));
    }

    // ---------------------------------------------------------------- target preference

    const int Human = 1;
    static readonly int Marine = TfwContracts.PuppetIdFirst;
    static readonly int Squad = TfwContracts.GuardTargetId(0);

    /// <summary>One Ork of <paramref name="type"/> at the origin in a war (or not) against <paramref name="targets"/>; where it stands after a second and a half (no graph: it walks straight).</summary>
    static Vector3 Walk(MusouEnemyType type, bool war, params MusouTarget[] targets)
    {
        var sim = new MusouSim(null) { War = war ? TfwCombatRules.Default : null };
        ushort id = sim.Spawn(type, Vector3.Zero, 0f, sim.Rules.Of(type).Health);
        Assert.NotEqual(0, id);
        var attacks = new List<MusouAttack>();
        for (uint t = 1; t <= 45; t++) { sim.BeginTick(t); attacks.Clear(); sim.Step(targets, null, true, attacks); }
        Assert.True(sim.TryGet(id, out var e));
        return e.Position;
    }

    [Fact]
    public void An_ordinary_Ork_picks_a_Guard_squad_over_a_player_nearer_than_two_and_a_half_times()
    {
        var player = new MusouTarget(Human, new Vector3(20f, 0f, 0f), true);   // 20 m east
        var squad = new MusouTarget(Squad, new Vector3(0f, 30f, 0f), true, 3f);   // 30 m north: 30 < 2.5 x 20
        var at = Walk(MusouEnemyType.Boy, war: true, player, squad);
        Assert.True(at.Y > 2f && MathF.Abs(at.X) < 0.5f, $"went north to the squad: {at}");
        var nearest = Walk(MusouEnemyType.Boy, war: false, player, squad);   // the skirmish keeps the nearest
        Assert.True(nearest.X > 2f && MathF.Abs(nearest.Y) < 0.5f, $"went east to the nearer player: {nearest}");
    }

    [Fact]
    public void An_ordinary_Ork_picks_the_player_within_twelve_metres_over_a_nearer_Marine()
    {
        var player = new MusouTarget(Human, new Vector3(10f, 0f, 0f), true);
        var marine = new MusouTarget(Marine, new Vector3(0f, 5f, 0f), true);
        var at = Walk(MusouEnemyType.Boy, war: true, player, marine);
        Assert.True(at.X > 2f && MathF.Abs(at.Y) < 0.5f, $"went for the player: {at}");
        var far = new MusouTarget(Human, new Vector3(20f, 0f, 0f), true);   // beyond 12 m the Marine at half the range wins
        var other = Walk(MusouEnemyType.Boy, war: true, far, marine);
        Assert.True(other.Y > 2f && MathF.Abs(other.X) < 0.5f, $"went for the Marine: {other}");
    }

    [Theory]
    [InlineData(MusouEnemyType.Nob)]
    [InlineData(MusouEnemyType.ArdBoy)]
    [InlineData(MusouEnemyType.BigShootaNob)]
    [InlineData(MusouEnemyType.Warboss)]
    [InlineData(MusouEnemyType.BossNob)]
    public void An_elite_or_boss_prefers_a_player_over_a_Marine_at_equal_range(MusouEnemyType type)
    {
        var player = new MusouTarget(Human, new Vector3(-35f, 0f, 0f), true);   // far enough that a gunner (the Big Shoota's band ends at 30 m) closes in
        var marine = new MusouTarget(Marine, new Vector3(35f, 0f, 0f), true);
        var at = Walk(type, war: true, marine, player);
        Assert.True(at.X < -1f, $"{type} went for the player: {at}");
        var guard = new MusouTarget(Squad, new Vector3(0f, 18f, 0f), true, 3f);   // a squad at 18 m weighs 54 against the Marine's 35
        var second = Walk(type, war: true, guard, marine);
        Assert.True(second.X > 1f, $"{type} went for the Marine: {second}");
    }

    [Fact]
    public void Rules_classify_the_kinds_and_weigh_the_targets()
    {
        Assert.Equal(MusouTargetClass.Ordinary, MusouTargetRules.ClassOf(MusouEnemyType.Boy));
        Assert.Equal(MusouTargetClass.Ordinary, MusouTargetRules.ClassOf(MusouEnemyType.Weirdboy));
        Assert.Equal(MusouTargetClass.Elite, MusouTargetRules.ClassOf(MusouEnemyType.Nob));
        Assert.Equal(MusouTargetClass.Elite, MusouTargetRules.ClassOf(MusouEnemyType.BigShootaNob));
        Assert.Equal(MusouTargetClass.Boss, MusouTargetRules.ClassOf(MusouEnemyType.Warboss));
        Assert.Equal(MusouTargetClass.Boss, MusouTargetRules.ClassOf(MusouEnemyType.BossShootaBoy));
        var p = new MusouTarget(Human, Vector3.Zero, true);
        Assert.Equal(2.5f, MusouTargetRules.Weight(MusouTargetClass.Ordinary, p));
        Assert.Equal(0.8f, MusouTargetRules.Weight(MusouTargetClass.Elite, p));
        Assert.Equal(1f, MusouTargetRules.Weight(MusouTargetClass.Ordinary, new MusouTarget(Marine, Vector3.Zero, true)));
        Assert.Equal(3f, MusouTargetRules.Weight(MusouTargetClass.Boss, new MusouTarget(Squad, Vector3.Zero, true)));
    }

    // ---------------------------------------------------------------- Ork blast

    static World War(out int retainer)
    {
        var config = MusouSimTests.Base with { RespawnTime = 60f };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        retainer = w.TfwSpawnRetainer(Human, new Vector3(30f, -60f, 0f)) ?? 0;
        Assert.NotEqual(0, retainer);
        Tick(w);
        return w;
    }

    static void Tick(World w) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));

    static float Total(PlayerState p) => p.Health + p.Shield;

    [Fact]
    public void An_Ork_blast_hurts_players_Marines_Guards_and_Orks_incl_elites_with_fall_off_and_credit()
    {
        var w = War(out int marine);
        var musou = w.Musou!;
        var guards = w.TfwGuards!;
        var centre = new Vector3(0f, -60f, 0f);
        // The Orks: a Boy at the centre and one near the edge; a Nob and the Warboss close by (sturdy, to see the damage).
        ushort boy = musou.Spawn(MusouEnemyType.Boy, centre + new Vector3(0f, 2f, 0f), 0f, 85f);
        ushort edge = musou.Spawn(MusouEnemyType.Boy, centre + new Vector3(0f, 7.5f, 0f), 0f, 85f);
        ushort nob = musou.Spawn(MusouEnemyType.Nob, centre + new Vector3(2f, 0f, 0f), 0f, 600f);
        ushort boss = musou.Spawn(MusouEnemyType.Warboss, centre + new Vector3(-3f, 0f, 0f), 0f, 15000f);
        Assert.True(boy != 0 && edge != 0 && nob != 0 && boss != 0);
        int squad = guards.SpawnSquad(centre + new Vector3(0f, 4f, 0f));
        Assert.True(squad >= 0);
        // The Marine stands beside the blast.
        w.TfwRespawnAt(marine, centre + new Vector3(0f, -3f, 0f), 0f);
        float playerBefore = Total(w.Players[Human]), marineBefore = Total(w.Players[marine]);
        int menBefore = guards.Alive;
        var spec = new OrkBlastSpec(8f, 200f);
        var r = w.MusouOrkBlast(centre, spec, credit: Human);
        Assert.True(r.Players >= 2, $"players struck: {r.Players}");
        Assert.True(Total(w.Players[Human]) < playerBefore, "the human is hurt");
        Assert.True(Total(w.Players[marine]) < marineBefore, "the AI Marine is hurt");
        Assert.True(r.GuardMen > 0, $"the Guards: {r.GuardMen} hurt");
        Assert.True(musou.TryGet(nob, out var n) && n.Health < 600f, "the elite is hurt");
        Assert.True(musou.TryGet(boss, out var b) && b.Health < 15000f, "the boss is hurt by default");
        Assert.True(r.Orks >= 4, $"Orks struck: {r.Orks}");
        // Fall-off: the Boy near the centre took more than the one near the edge (at 2 m about 91 %, at 7.5 m about 30 % of 200).
        Assert.False(musou.TryGet(boy, out var nearBoy) && nearBoy.Alive, "the Boy at the centre is dead");
        Assert.True(musou.TryGet(edge, out var farBoy) && farBoy.Alive && farBoy.Health < 85f, $"the Boy at the edge took the least and lives: {farBoy.Health}");
        Assert.Equal(1f, spec.IntensityAt(0f));
        Assert.Equal(0.25f, spec.IntensityAt(8f), 4);
        Assert.Equal(0f, spec.IntensityAt(8.1f));
        // Credit: the Boy died to the Ork blast, credited to the human.
        var kills = new List<MusouKill>();
        musou.TakeKills(kills);
        Assert.Contains(kills, k => k.Enemy == boy && k.Cause == MusouDeathKind.OrkBlast && k.Player == Human);
        // A heavy blast kills the men outright.
        Assert.True(w.MusouOrkBlast(centre, new OrkBlastSpec(8f, 100000f)).GuardKills > 0 && guards.Alive < menBefore);
    }

    [Fact]
    public void An_Ork_blast_without_a_credit_credits_no_one_and_can_spare_the_bosses()
    {
        var w = War(out _);
        var musou = w.Musou!;
        var centre = new Vector3(0f, -60f, 0f);
        ushort boy = musou.Spawn(MusouEnemyType.Boy, centre + new Vector3(0f, 2f, 0f), 0f, 10f);
        ushort boss = musou.Spawn(MusouEnemyType.Warboss, centre + new Vector3(-2f, 0f, 0f), 0f, 15000f);
        var r = w.MusouOrkBlast(centre, new OrkBlastSpec(6f, 100f) { HurtBosses = false });
        Assert.Equal(1, r.Orks);
        Assert.True(musou.TryGet(boss, out var b) && b.Health == 15000f);
        var kills = new List<MusouKill>();
        musou.TakeKills(kills);
        Assert.Contains(kills, k => k.Enemy == boy && k.Player == 0 && k.Cause == MusouDeathKind.OrkBlast);
    }

    [Fact]
    public void An_Ork_blast_queues_the_Bomb_Squigs_in_its_radius_once_each()
    {
        var w = War(out _);
        var musou = w.Musou!;
        var centre = new Vector3(0f, -60f, 0f);
        ushort near = musou.Spawn(MusouEnemyType.BombSquig, centre + new Vector3(0f, 6f, 0f), 0f, 1000f);
        ushort far = musou.Spawn(MusouEnemyType.BombSquig, centre + new Vector3(0f, 30f, 0f), 0f, 1000f);
        Assert.True(near != 0 && far != 0);
        var spec = new OrkBlastSpec(10f, 1f);
        Assert.Equal(1, w.MusouOrkBlast(centre, spec, credit: Human).SquigsQueued);
        Assert.Equal(0, w.MusouOrkBlast(centre, spec, credit: Human).SquigsQueued);   // already queued
        var queued = new List<(ushort Id, int Credit)>();
        musou.TakeDetonations(queued);
        Assert.Equal([(near, Human)], queued);
    }

    [Fact]
    public void The_friendly_fire_table_is_documented()
    {
        Assert.Contains("Orks no", World.OrkFriendlyFireTable);
        Assert.Contains("Ork explosions: players yes", World.OrkFriendlyFireTable);
    }
}
