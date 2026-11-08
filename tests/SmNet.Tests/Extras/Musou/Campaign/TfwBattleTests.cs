using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>A scripted war on the real generated map, without a nav graph (the enemies walk the flat floor): the script teleports the player and kills the Orks; the rest is the simulation's.</summary>
internal static class TfwRig
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    public static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou, RespawnTime = 2f,
    };

    public const int Human = 1;

    public static World War(NavGraph? nav = null, SimConfig? config = null)
    {
        var map = TfwTestArena.Build();
        var w = new World(config ?? Config, map.Collision, TfwTestArena.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = TfwTestArena.Data.StartAreas };
        Assert.True(w.StartTfw(nav, TfwTestArena.Field));
        w.Tfw!.AllyUp = _ => false;   // the tests of the extraction; those of the revive set their own
        w.Tfw.Guardsmen = true;   // these tests are of the battle with its squads; TfwOrkTests of the switch
        w.Tfw.Patrols = false;    // and of the crowds as they count; TfwPatrolTests of the columns
        w.TfwMeksOn = false;      // the Scrap Meks too (MusouExpansionS4Tests turns them on)
        w.Spawn(Human, w.AssignClass(0));
        Steps(w, 30);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    public static void Steps(World w, int ticks)
    {
        for (int k = 0; k < ticks; k++) w.Step(_ => default);
    }

    public static void Seconds(World w, float seconds) => Steps(w, (int)MathF.Round(seconds * 30f));

    public static void Command(World w, TfwCommand command, TfwLane lane = TfwLane.A) => Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(1, command, (byte)lane)));

    /// <summary>Deploys and lands.</summary>
    public static TfwBattle Deploy(World w, TfwLane lane = TfwLane.A)
    {
        Command(w, TfwCommand.Deploy, lane);
        Seconds(w, TfwBattleRules.TravelSeconds + 0.2f);
        return Assert.IsType<TfwBattle>(w.Tfw!.Battle);
    }

    /// <summary>Every enemy alive but <paramref name="spare"/> dies by the player.</summary>
    public static void KillAll(World w, ushort spare = 0)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive && all[k].Id != spare) w.Musou.Damage(all[k].Id, Human, 1e6f, Vector3.Zero, out _);
    }

    public static void Teleport(World w, Vector3 to) => Assert.True(w.TfwRespawnAt(Human, to, 0f));

    /// <summary>Stands the player at position <paramref name="k"/>, keeps the Orks dead and lets a squad secure it.</summary>
    public static void Secure(World w, TfwBattle battle, int k)
    {
        Teleport(w, battle.Layout.Positions[k]);
        for (int t = 0; t < 120 * 30 && battle.Objectives[k].State != TfwPositionState.Secured; t++)
        {
            KillAll(w);
            w.Step(_ => default);
        }
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[k].State);
    }

    /// <summary>The three positions, the commander's death and the free pickup: one won battle.</summary>
    public static void WinBattle(World w, TfwLane lane)
    {
        var battle = Deploy(w, lane);
        for (int k = 0; k < 3; k++) Secure(w, battle, k);
        KillAll(w);
        Steps(w, 2);
        Assert.True(battle.Won);
        Command(w, TfwCommand.RequestPickup);
        Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw!.Phase);
    }

    public static bool InStormbird(Vector3 p) => TfwTestArena.Hub.Contains(p);
}

/// <summary>A battle (TF4, design section D): the layout, the chain of positions, the defenders, the arrivals, the securing, the commander and the failsafe.</summary>
public class TfwBattleTests
{
    [Theory]
    [InlineData(10.0, 4)]
    [InlineData(100.0, 7)]
    [InlineData(200.0, 12)]
    public void Squads_follow_the_imperial_strength(double imperial, int squads) => Assert.Equal(squads, TfwBattleRules.SquadsAt(imperial));

    [Fact]
    public void The_crowds_hold_until_a_target_comes_near_and_a_hurt_one_wakes_its_crowd()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwTestArena.StartAlive + battle.Pooled(0) + battle.Pooled(1) + battle.Pooled(2), w.Musou!.Alive);   // the garrisons, and the first of each crowd (one every 4 ticks)
        var at = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou.CopyTo(at);
        Assert.All(at.Take(n), e => Assert.True(w.Musou.Leashed(e.Id)));
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[1].State);       // all three live
        w.Musou.Damage(at[0].Id, TfwRig.Human, 1f, Vector3.Zero, out _);
        int free = at.Take(n).Count(e => !w.Musou.Leashed(e.Id));
        Assert.Equal(TfwBattleRules.Garrison, free);
    }

    [Fact]
    public void A_position_is_taken_by_a_player_under_fire_and_never_by_the_guardsmen()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Seconds(w, 1f);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);   // nobody near
        TfwRig.Teleport(w, battle.Layout.Positions[0]);
        w.Musou!.Frozen = true;                                            // the garrison stands on it
        TfwRig.Seconds(w, 2f);
        Assert.Equal(TfwPositionState.Clearing, battle.Objectives[0].State);
        Assert.True(battle.Objectives[0].Progress > 0f);                   // the owner, 2026-11-06: progress with Orks still on it
        w.Musou.Frozen = false;
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 3);
        Assert.Equal(TfwPositionState.Securing, battle.Objectives[0].State);
        float made = battle.Objectives[0].Progress;
        TfwRig.Teleport(w, battle.Layout.LandingZone);
        var guards = w.TfwGuards!;
        Assert.True(guards.SpawnSquad(battle.Layout.Positions[0]) >= 0);   // a squad put on it by hand (their posts would never send one)
        for (int t = 0; t < 30; t++) { TfwRig.KillAll(w); w.Step(_ => default); }
        Assert.True(guards.CountInside(battle.Layout.Positions[0], TfwTestArena.CaptureRadius) > 0);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);   // Guardsmen never capture
        Assert.True(battle.Objectives[0].Progress < made);
    }

    [Fact]
    public void A_wiped_squad_lets_the_position_decay_and_a_reserve_goes_after_15_s()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Teleport(w, battle.Layout.Positions[0]);
        TfwRig.Steps(w, 2);
        for (int t = 0; t < 90 * 30 && battle.Objectives[0].Progress < 0.3f; t++) { TfwRig.KillAll(w); w.Step(_ => default); }
        TfwRig.Teleport(w, battle.Layout.LandingZone);
        float progress = battle.Objectives[0].Progress;
        Assert.True(progress >= 0.3f);
        var guards = w.TfwGuards!;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
            while (guards.AliveIn(s) > 0) guards.Hit(s, 1e6f, Vector3.Zero);
        TfwRig.Seconds(w, 10f);
        Assert.InRange(battle.Objectives[0].Progress, progress - 0.21f, progress - 0.19f);   // 2 % a second
        int reserves = battle.Reserves;
        Assert.Equal(1, reserves);                                         // 7 squads at Imperial 100, 6 in the field
        TfwRig.Seconds(w, 5.5f);
        Assert.Equal(reserves - 1, battle.Reserves);
        Assert.Equal(1, guards.SquadsAlive);
    }

    [Fact]
    public void With_no_guardsman_on_it_a_player_or_marine_secures_at_the_full_rate_with_an_ork_on_it_too()
    {
        var sim = new MusouSim(null);
        var guards = new GuardSim(sim);
        var layout = TfwTestArena.Layout(TfwLane.A, 1);
        var battle = new TfwBattle(sim, guards, layout, 10.0, 1) { Trickle = false, Patrols = false };     // 4 squads, all in the field
        battle.Start();
        Assert.Equal(0, battle.Reserves);
        Vector3[] at = [layout.Positions[0]], away = [layout.LandingZone];
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all);
        for (int k = 0; k < n; k++) sim.Damage(all[k].Id, 1, 1e6f, Vector3.Zero, out _);
        for (int s = 0; s < GuardSim.MaxSquads; s++)
            while (guards.AliveIn(s) > 0) guards.Hit(s, 1e6f, Vector3.Zero);
        Assert.Equal(0, guards.Alive);

        for (int k = 0; k < 30; k++) battle.Step(away);                      // nobody on it
        Assert.Equal(0f, battle.Objectives[0].Progress);
        ushort ork = sim.Spawn(MusouEnemyType.Boy, layout.Positions[0], 0f, 85f);
        for (int k = 0; k < 30; k++) battle.Step(at);
        Assert.InRange(battle.Objectives[0].Progress, 1f / 25f - 0.001f, 1f / 25f + 0.001f);   // an Ork no longer pauses it (owner, 2026-11-06)
        sim.Damage(ork, 1, 1e6f, Vector3.Zero, out _);
        for (int k = 0; k < 11 * 30; k++) { ClearAll(sim); battle.Step(at); }
        Assert.InRange(battle.Objectives[0].Progress, 0.479f, 0.481f);       // the full rate (no half rate since 2026-11-06)
        for (int k = 0; k < 13 * 30 + 2; k++) { ClearAll(sim); battle.Step(at); }
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);
    }

    static void ClearAll(MusouSim sim)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all);
        for (int k = 0; k < n; k++) if (all[k].Alive) sim.Damage(all[k].Id, 1, 1e6f, Vector3.Zero, out _);
    }

    [Fact]
    public void The_commander_comes_after_the_third_position_with_a_boss_bar()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        Assert.NotEqual((ushort)0, battle.BossId);
        Assert.Equal(w.Musou!.Rules.BossHealth, battle.BossMaxHealth);   // the commander is the Warboss (owner, 2026-11-06)
        Assert.True(w.Musou!.TryGet(battle.BossId, out var boss));
        Assert.Equal(MusouEnemyType.Warboss, boss.Type);
        TfwRig.Steps(w, 1);
        Assert.Equal(MusouPhase.Campaign, w.Musou.Director.Phase);
        Assert.Equal(battle.BossId, w.Musou.Director.BossId);
        Assert.Equal(w.Musou.Rules.BossHealth, w.Musou.Director.BossMaxHealth);
        Assert.Equal(7, w.Musou.Alive);                                    // him, a Nob and five 'Ard Boyz
    }

    [Fact]
    public void The_boss_goes_for_the_squads_and_strikes_them()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        var guards = w.TfwGuards!;
        int squad = guards.SpawnSquad(battle.Layout.Hq + new Vector3(0f, -3f, 0f));
        Assert.True(squad >= 0);
        int before = guards.Lost;
        bool slam = false, hurt = false;
        for (int t = 0; t < 20 * 30 && !(slam && hurt); t++)
        {
            w.Step(_ => default);
            slam |= w.Musou!.Events.Any(e => e.Kind == MusouEventKind.Strike && e.Enemy == battle.BossId && e.Value == (byte)MusouBossAbility.GroundSlam);
            hurt |= guards.Events.Any(e => e.Kind is GuardEventKind.Hurt or GuardEventKind.Death);
        }
        Assert.True(slam, $"the slam met a squad (men {guards.AliveIn(squad)}, lost {guards.Lost - before}; it lands at about 12 s)");
        Assert.True(hurt, "and hurt a man");
    }

    [Fact]
    public void A_waaagh_brings_its_boyz_from_the_sources()
    {
        var sim = new MusouSim(null);
        var battle = new TfwBattle(sim, new GuardSim(sim), TfwTestArena.Layout(TfwLane.A, 1), 100.0, 1) { Trickle = false, Patrols = false };
        battle.Start();
        int before = sim.Alive;
        battle.NoteWaaaghs(1);
        for (int k = 0; k < 5; k++) battle.Step([]);
        Assert.Equal(before + sim.Rules.Boss.WaaaghBoyz + battle.Pooled(0) + battle.Pooled(1) + battle.Pooled(2), sim.Alive);   // and the crowds' first (one every 4 ticks)
    }

    [Fact]
    public void A_woken_defender_with_no_reachable_target_is_moved_then_removed()
    {
        // Two floors with a gap: a defender on the far one wakes, cannot walk to the player and holds in Seek.
        var mesh = new NavigationTests.Level().Floor(0f, 0f, 20f, 20f).Floor(30f, 0f, 50f, 20f).Mesh();
        var nav = NavGraph.Build(mesh, null, NavSettings.From(new SimConfig()));
        var sim = new MusouSim(nav);
        var guards = new GuardSim(sim);
        var near = new Vector3(35f, 2f, 0f);
        var layout = new TfwLayout(near, [new(10f, 10f, 0f), new(10f, 12f, 0f), new(10f, 14f, 0f)], new Vector3(10f, 16f, 0f), [new(8f, 8f, 0f), new(8f, 8f, 0f), new(8f, 8f, 0f), new(8f, 8f, 0f)]);
        var battle = new TfwBattle(sim, guards, layout, 100.0, 1) { Trickle = false, Patrols = false };
        ushort id = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(10f, 2f, 0f), 0f, 85f, 9, TfwBattleRules.DefenderAggro);
        MusouTarget[] targets = [new(1, near, true)];
        var attacks = new List<MusouAttack>();
        int moved = 0;
        for (uint t = 1; t < 40 * 30 && sim.TryGet(id, out var e) && e.Alive; t++)
        {
            sim.BeginTick(t);
            battle.Step([]);
            sim.Step(targets, null, true, attacks);
            moved = Math.Max(moved, sim.Relocations(id));
        }
        Assert.Equal(1, moved);
        var kills = new List<MusouKill>();
        sim.TakeKills(kills);
        foreach (var k in kills) battle.NoteKill(k);
        var kill = Assert.Single(kills, k => k.Enemy == id);               // moved once, stranded again, removed
        Assert.Equal(MusouDeathKind.World, kill.Cause);
        Assert.Equal(0, battle.OrksKilled);                                // nobody's KO
    }

    [Fact]
    public void The_full_cap_holds_back_the_arrivals()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var battle = new TfwBattle(sim, new GuardSim(sim), TfwTestArena.Layout(TfwLane.A, 1), 100.0, 1) { Trickle = false, Patrols = false };
        battle.Start();
        for (int k = 0; k < 400 * 30; k++) battle.Step([]);
        Assert.Equal(TfwBattleRules.AliveCapSolo + 3 * TfwBattleRules.Garrison, sim.Alive);   // the crowds and swarms are the cap; the garrisons stand beside it
    }
}
