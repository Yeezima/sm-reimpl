using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The autocannon pass 2 (the owner, 2026-11-11: "There should be no damage falloff for any ranged weapon in Musou mode"): in Musou Mode's war every ranged hit on an Ork deals its full damage at any
/// distance it reaches (<see cref="TfwCombatRules.RangeFallOff"/> false, <c>World.MusouRangeFallOff</c>): a bolter, a stalker bolter, a lascannon and a plasma gun at 100 m (past every fall-off's end)
/// hit as at 8 m, and the melta's cone at 15 m as at 5 m; the same world with the fall-off switched back on, the 5-wave run and Classic PvP keep the weapons' fall-off. Synthetic weapons (names shaped
/// like the install's, values typed in) on the synthetic arena; no original data.
/// </summary>
public class MusouRangeFallOffTests
{
    // Hit-scan weapons with a hard fall-off (x0.5 from 45 to 70 m), head and body x1, so a hit is exactly Damage x fall-off x the war's 1.2.
    static WeaponSpec Gun(string name, float damage) => new(name, WeaponSlot.Ranged, TriggerKind.FullAuto, damage, 0.2f, 100, 100, 1f, 250f, 45f, 70f, 0.5f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Bolter = Gun("sm_mp_ranged_bolter", 20f);
    static readonly WeaponSpec Stalker = Gun("sm_mp_ranged_sniper_rifle", 50f);
    static readonly WeaponSpec Lascannon = Gun("sm_mp_exotic_las_cannon", 120f);
    static readonly WeaponSpec Plasma = Gun("sm_mp_ranged_plasma_gun", 40f);
    static readonly WeaponSpec Melta = MeltaTests.Melta with { Name = "sm_mp_ranged_melta" };   // the cone: 200, distance fall-off 6.5 m x1, 10 m x0.5, 20 m x0.15
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
    static readonly WeaponSpec[] Weapons = [Bolter, Stalker, Lascannon, Plasma, Melta];

    const int Human = 1;
    const float Tough = 100000f;

    static SimConfig Config()
    {
        var tactical = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0, 1, 2, 3, 4], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        return MusouLoadout.Prepare(MusouSimTests.Base with
        {
            Weapons = Weapons, Thrown = [], Classes = [tactical, tactical with { Name = "csm - tactical", Side = Team.Chaos }], MusouRules = Still, RespawnTime = 60f,
        });
    }

    /// <summary>A war on the synthetic arena with the human at (0, -60) carrying primary <paramref name="primary"/>, the Orks frozen, the bolt blasts off (so a hit is the gun's alone).</summary>
    static World War(int primary, bool fallOff = false)
    {
        var map = TfwTestArena.Build();
        var w = new World(Config(), map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0, primaryChoice: primary);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        w.Musou.War = w.Musou.War! with { BoltBlasts = false, RangeFallOff = fallOff };
        Assert.Equal(Weapons[primary].Name, Loadouts.InHand(w.Config, w.Players[Human])!.Name);
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    /// <summary>Fires at a tough Boy <paramref name="ahead"/> metres north until he is hurt; the first tick's damage.</summary>
    static float FirstHit(World w, float ahead)
    {
        var me = w.Players[Human].Position;
        ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, ahead, 0f), 0f, Tough);
        Assert.NotEqual(0, id);
        for (int k = 0; k < 60; k++)
        {
            Tick(w, InputButtons.Fire);
            float left = w.Musou.TryGet(id, out var e) ? e.Health : 0f;
            if (left < Tough) return Tough - left;
        }
        Assert.Fail($"the Boy {ahead} m ahead was never hit");
        return 0f;
    }

    public static TheoryData<int> Guns => new(0, 1, 2, 3);

    [Theory]
    [MemberData(nameof(Guns))]
    public void A_gun_deals_its_full_damage_at_100_metres_in_the_war(int primary)
    {
        float ranged = TfwCombatRules.Default.PlayerRangedDamageScale;
        var gun = Weapons[primary];
        Assert.Equal(0.5f, SmSim.Combat.FallOff(100f, gun));   // the weapon's own fall-off at that range
        float near = FirstHit(War(primary), 8f), far = FirstHit(War(primary), 100f);
        Assert.Equal(gun.Damage * ranged, near, 0.01f);
        Assert.Equal(gun.Damage * ranged, far, 0.01f);   // no fall-off (owner 2026-11-11)
        // The same war with the fall-off switched back on (TfwCombatRules.RangeFallOff): the weapon's x0.5 at 100 m.
        Assert.Equal(gun.Damage * 0.5f * ranged, FirstHit(War(primary, fallOff: true), 100f), 0.01f);
    }

    [Fact]
    public void The_meltas_cone_deals_its_full_damage_to_its_end_in_the_war()
    {
        float ranged = TfwCombatRules.Default.PlayerRangedDamageScale;
        Assert.Equal(0.325f, SmSim.Combat.SplashIntensity(Melta.Cone!.FallOff, 15f), 3);   // the data's distance fall-off at 15 m
        float near = FirstHit(War(4), 5f), far = FirstHit(War(4), 15f);
        Assert.Equal(200f * ranged, near, 0.01f);
        Assert.Equal(200f * ranged, far, 0.01f);   // fully covered at both: the coverage multiplier is 1, the distance one gone
        Assert.Equal(200f * 0.325f * ranged, FirstHit(War(4, fallOff: true), 15f), 0.5f);   // with the fall-off on: about x0.325 (the distance runs eye to chest, a little over 15 m)
    }

    [Fact]
    public void The_rule_is_off_by_default_and_the_war_alone_reads_it()
    {
        Assert.False(TfwCombatRules.Default.RangeFallOff);
        // A blast's fall-off from its centre is another rule and stays (the bolt blast: full to 0.5 m, 30 % at its 1.25 m edge).
        Assert.Equal(0.3f, SmSim.Combat.SplashIntensity(TfwCombatRules.Default.BoltBlastFallOff, 1.25f), 3);
    }

    [Fact]
    public void The_five_wave_run_keeps_the_weapons_fall_off()
    {
        // The 5-wave test aid (--map mp_tdm_09, pinned replay): no war, so the weapon's own fall-off and no x1.2 (a class with the bolter in hand, as MusouCombatTests' flat ground).
        var tactical = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with { Weapons = [Bolter], Thrown = [], Classes = [tactical, tactical with { Name = "csm - tactical", Side = Team.Chaos }], MusouRules = Still };
        float Hit(float ahead)
        {
            var w = new World(config) { Rules = MusouSimTests.Endless };
            Assert.True(w.StartMusou(null));
            w.MusouDirector!.Hold = true;
            w.Spawn(Human, w.AssignClass(0));
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));   // in play, facing +y
            Assert.Equal(MatchState.Playing, w.State);
            Assert.Null(w.Musou!.War);
            Assert.Equal(Bolter.Name, Loadouts.InHand(w.Config, w.Players[Human])!.Name);
            return FirstHit(w, ahead);
        }
        Assert.Equal(Bolter.Damage, Hit(8f), 0.01f);
        Assert.Equal(Bolter.Damage * 0.5f, Hit(100f), 0.01f);
    }

    // ---- the damage answers (owner 2026-11-11): an AI Marine's guns keep their fall-off, a human's shot loses nothing per Ork, the melta's 200 to 20 m

    const int Marine = TfwContracts.PuppetIdFirst;

    /// <summary>A war with an AI Marine (a puppet) at (0, -60) carrying primary <paramref name="primary"/>, firing north whenever the returned flag is set; the human stands far off.</summary>
    static (World W, bool[] Fire) MarineWar(int primary)
    {
        var map = TfwTestArena.Build();
        var w = new World(Config(), map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        var fire = new bool[1];
        w.Tfw.PuppetBrain = (_, p) => new PlayerInput(w.Tick, 0f, 0f, 0f, fire[0] ? InputButtons.Fire : InputButtons.None);
        w.Spawn(Human, 0, primaryChoice: primary);
        w.Spawn(Marine, 0, primaryChoice: primary);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(100f, -100f, 0f), 0f);
        w.TfwRespawnAt(Marine, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        w.Musou.War = w.Musou.War! with { BoltBlasts = false };
        Assert.False(TfwCombatRules.ScalesPlayer(Marine));
        Assert.Equal(Weapons[primary].Name, Loadouts.InHand(w.Config, w.Players[Marine])!.Name);
        return (w, fire);
    }

    static float MarineHit(int primary, float ahead)
    {
        var (w, fire) = MarineWar(primary);
        ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, w.Players[Marine].Position + new Vector3(0f, ahead, 0f), 0f, Tough);
        Assert.NotEqual(0, id);
        fire[0] = true;
        for (int k = 0; k < 60; k++)
        {
            Tick(w);
            float left = w.Musou.TryGet(id, out var e) ? e.Health : 0f;
            if (left < Tough) return Tough - left;
        }
        Assert.Fail($"the Boy {ahead} m ahead was never hit by the Marine");
        return 0f;
    }

    [Theory]
    [MemberData(nameof(Guns))]
    public void An_AI_Marines_gun_keeps_its_range_fall_off_in_the_war_and_takes_no_x1_2(int primary)
    {
        var gun = Weapons[primary];
        Assert.Equal(gun.Damage, MarineHit(primary, 8f), 0.01f);   // inside the fall-off's start: full, x1 (only players get the x1.2)
        Assert.Equal(gun.Damage * 0.5f, MarineHit(primary, 100f), 0.01f);   // the weapon's own x0.5 at 100 m, as before pass 2
    }

    [Fact]
    public void An_AI_Marines_melta_keeps_its_cone_fall_off_and_a_players_stays_full_to_20_metres()
    {
        float ranged = TfwCombatRules.Default.PlayerRangedDamageScale;
        Assert.Equal(200f * 0.325f, MarineHit(4, 15f), 0.5f);   // the cone's data fall-off, as before pass 2
        Assert.Equal(200f * ranged, FirstHit(War(4), 20f), 0.01f);   // owner: "melta should keep its full 200 damage out to 20m" (x1.2 a player's)
        Assert.Equal(200f * ranged, FirstHit(War(4), 18f), 0.01f);
    }

    /// <summary>Three tough Orks in a line north of <paramref name="shooter"/>, 5, 6 and 7 m away; the damage each lost once any is hurt.</summary>
    static float[] LineHits(World w, int shooter, Action step)
    {
        var me = w.Players[shooter].Position;
        var ids = new ushort[3];
        for (int i = 0; i < 3; i++) ids[i] = w.Musou!.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 5f + i, 0f), 0f, Tough);
        Assert.DoesNotContain((ushort)0, ids);
        for (int k = 0; k < 60; k++)
        {
            step();
            if (ids.Any(id => w.Musou!.TryGet(id, out var e) && e.Health < Tough)) break;
        }
        return [.. ids.Select(id => Tough - (w.Musou!.TryGet(id, out var e) ? e.Health : 0f))];
    }

    [Fact]
    public void A_players_shot_loses_nothing_for_each_Ork_it_passes_through_and_an_AI_Marines_keeps_the_15_percent()
    {
        float ranged = TfwCombatRules.Default.PlayerRangedDamageScale;
        var lascannon = Weapons[2];
        Assert.True(TfwCombatRules.Pierced(lascannon) >= 2);
        var human = War(2);
        var h = LineHits(human, Human, () => Tick(human, InputButtons.Fire));
        Assert.Equal(lascannon.Damage * ranged, h[0], 0.05f);
        Assert.Equal(h[0], h[1], 0.05f);   // no loss for the second Ork
        Assert.Equal(h[0], h[2], 0.05f);
        var (marine, fire) = MarineWar(2);
        fire[0] = true;
        var m = LineHits(marine, Marine, () => Tick(marine));
        Assert.Equal(lascannon.Damage, m[0], 0.05f);
        Assert.Equal(m[0] * 0.85f, m[1], 0.05f);   // the AI Marine's shot keeps the 15 % penalty
        Assert.Equal(m[0] * 0.85f * 0.85f, m[2], 0.05f);
    }
}
