using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Musou powerups (owner, 2026-11-10): drops hold powerups, not weapons, from elites (about one in ten) and bosses (always); holding the use key half a second takes one (the server checks the hold)
/// and starts its buff for its full time (again: the full time again, no stacking); Double Damage doubles the player's damage, Supercharged Melee its melee damage and widens its cleave, and no
/// multiplier stacks past x2 (the owner's caps: Supercharged x Double Damage x a stim on melee, Rapid Fire x a stim on the fire rate); Fear sends the Orks near the player running (never a boss);
/// Rapid Fire sets <see cref="PlayerState.FireBoostLeft"/>, which only Musou turns into a fire boost; a death or a new round ends the buffs; wire tag 6 carries them after the drops.
/// </summary>
public class MusouPowerupsTests
{
    static readonly WeaponSpec Bolter = new("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec BoltPistol = new("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly SimConfig Raw = new()
    {
        Weapons = [Bolter, BoltPistol],
        Classes = [new("sm - tactical", 0, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = Team.SpaceMarines, Role = "tactical" }],
        Extras = ContentExtras.Musou,
    };
    static readonly SimConfig Prepared = MusouLoadout.Prepare(Raw with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f } });

    static World NewWorld()
    {
        var w = new World(Prepared) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Musou!.Frozen = true;
        return w;
    }

    static PlayerInput Press => new(0, 0f, 0f, 0f, InputButtons.Interact);

    static List<MusouDrop> DropsOf(World w, int id)
    {
        var list = new List<MusouDrop>();
        w.MusouDropsOf(id, list);
        return list;
    }

    /// <summary>Changes player <paramref name="id"/>'s state in place (as <see cref="MusouCombatRulesTests"/> does).</summary>
    static void Set(World w, int id, Func<PlayerState, PlayerState> change)
    {
        var players = (Dictionary<int, PlayerState>)typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        players[id] = change(players[id]);
    }

    /// <summary>Player <paramref name="id"/> holds the use key for <paramref name="ticks"/> ticks (the first a press), the others nothing.</summary>
    static void Hold(World w, int id, int ticks)
    {
        for (int k = 0; k < ticks; k++) w.Step(p => p == id ? Press : default);
    }

    static int HoldTicks(World w) => MusouPowerupRules.HoldTicks(w.Config.TickRate);

    // ---------------------------------------------------------------- the rules

    [Fact]
    public void Rules_follow_the_owners_numbers()
    {
        Assert.Equal(30f, MusouPowerupRules.Seconds(MusouPowerup.DoubleDamage));
        Assert.Equal(15f, MusouPowerupRules.Seconds(MusouPowerup.Fear));
        Assert.Equal(20f, MusouPowerupRules.Seconds(MusouPowerup.Supercharged));
        Assert.Equal(20f, MusouPowerupRules.Seconds(MusouPowerup.RapidFire));
        foreach (var kind in MusouPowerupRules.All)
        {
            ushort item = MusouPowerupRules.Item(kind);
            Assert.True(MusouPowerupRules.IsPowerup(item));
            Assert.Equal(kind, MusouPowerupRules.KindOf(item));
            Assert.Null(MusouDropItem.Wargear(Raw, 0, item));   // never a weapon
        }
        Assert.False(MusouPowerupRules.IsPowerup(0));
        Assert.False(MusouPowerupRules.IsPowerup(MusouDropItem.Melee(1)));
    }

    [Fact]
    public void Bosses_always_drop_elites_about_one_in_ten_the_rest_never()
    {
        foreach (var boss in new[] { MusouEnemyType.Warboss, MusouEnemyType.BossNob, MusouEnemyType.BossBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossShootaBoy })
            Assert.True(MusouPowerupRules.Drops(boss, 123, 45));
        int drops = 0;
        for (uint t = 0; t < 4000; t++) if (MusouPowerupRules.Drops(MusouEnemyType.Nob, t * 7, (ushort)(t % 900 + 1))) drops++;
        Assert.InRange(drops / 4000.0, 0.08, 0.12);
        for (uint t = 0; t < 500; t++)
        {
            Assert.False(MusouPowerupRules.Drops(MusouEnemyType.Boy, t, 7));
            Assert.False(MusouPowerupRules.Drops(MusouEnemyType.ShootaBoy, t, 7));
            Assert.False(MusouPowerupRules.Drops(MusouEnemyType.Gretchin, t, 7));
        }
        var seen = new HashSet<MusouPowerup>();
        for (int p = 1; p < 60; p++) seen.Add(MusouPowerupRules.Roll(10, 3, p));
        Assert.Equal(4, seen.Count);   // every kind comes up
    }

    // ---------------------------------------------------------------- the world

    [Fact]
    public void A_boss_kill_drops_a_powerup_for_every_living_player_and_holding_the_use_key_starts_its_buff()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(0));
        w.Step(_ => default);   // warm-up into play (a new round clears drops and buffs)
        var at = w.Players[1].Position + new Vector3(0.5f, 0f, 0f);
        ushort boss = w.Musou!.Spawn(MusouEnemyType.BossNob, at, 0f, 10f);
        Assert.True(w.Musou.Damage(boss, 1, 1e6f, Vector3.Zero, out bool killed) && killed);
        w.Step(_ => default);
        var mine = DropsOf(w, 1);
        Assert.Single(mine);
        Assert.Single(DropsOf(w, 2));
        Assert.True(MusouPowerupRules.IsPowerup(mine[0].Item));
        var kind = MusouPowerupRules.KindOf(mine[0].Item);
        Hold(w, 1, HoldTicks(w));
        Assert.Equal(MusouDropState.Picked, DropsOf(w, 1)[0].State);
        Assert.True(w.MusouBuffActive(1, kind));
        Assert.False(w.MusouBuffActive(2, kind));
        Assert.Equal(1, w.MusouPowerupsTaken);
    }

    [Fact]
    public void A_powerup_takes_a_half_second_hold_that_a_release_or_leaving_its_reach_ends()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        int need = HoldTicks(w);
        Assert.Equal(0.5f, MusouPowerupRules.HoldSeconds);
        Assert.Equal(15, MusouPowerupRules.HoldTicks(30));
        Assert.Equal(1, MusouPowerupRules.HoldTicks(1));
        var drop = w.MusouSpawnDrop(1, w.Players[1].Position + new Vector3(0.6f, 0f, 0f), MusouPowerupRules.Item(MusouPowerup.Fear))!.Value;

        // A tap, and a hold one tick short: nothing taken, the hold runs while the key is down and ends with the release.
        Hold(w, 1, 1);
        Assert.Equal(drop.Id, w.MusouHoldOf(1)!.Value.Drop);
        w.Step(_ => default);
        Assert.Null(w.MusouHoldOf(1));
        Hold(w, 1, need - 1);
        Assert.NotNull(w.MusouHoldOf(1));
        Assert.Equal(MusouDropState.Live, DropsOf(w, 1)[0].State);
        Assert.False(w.MusouBuffActive(1, MusouPowerup.Fear));
        w.Step(_ => default);   // released one tick short
        Assert.Null(w.MusouHoldOf(1));
        Assert.Equal(MusouDropState.Live, DropsOf(w, 1)[0].State);
        Assert.Equal(0, w.MusouPowerupsTaken);

        // Leaving the drop's reach mid-hold ends it too; the key still down starts nothing (a new press does).
        Hold(w, 1, need / 2);
        var home = w.Players[1].Position;
        Set(w, 1, p => p with { Position = home + new Vector3(10f, 0f, 0f) });
        w.Step(p => p == 1 ? Press : default);
        Assert.Null(w.MusouHoldOf(1));
        Set(w, 1, p => p with { Position = home });
        Hold(w, 1, need);
        Assert.Null(w.MusouHoldOf(1));
        Assert.Equal(MusouDropState.Live, DropsOf(w, 1)[0].State);   // held through, never pressed again

        // A full hold from a fresh press: taken on its last tick.
        w.Step(_ => default);
        Hold(w, 1, need);
        Assert.Equal(MusouDropState.Picked, DropsOf(w, 1)[0].State);
        Assert.True(w.MusouBuffActive(1, MusouPowerup.Fear));
        Assert.Null(w.MusouHoldOf(1));
        Assert.Equal(1, w.MusouPowerupsTaken);
    }

    [Fact]
    public void A_weapon_drop_still_goes_at_the_press()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        w.MusouSpawnDrop(1, w.Players[1].Position + new Vector3(0.6f, 0f, 0f), 0);   // the bolter (index 0)
        w.Step(id => id == 1 ? Press : default);
        Assert.Equal(MusouDropState.Picked, DropsOf(w, 1)[0].State);
        Assert.Null(w.MusouHoldOf(1));
    }

    [Fact]
    public void A_buff_lasts_its_time_and_a_second_one_restarts_it()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        int rate = w.Config.TickRate;
        Assert.True(w.MusouGiveBuff(1, MusouPowerup.Fear));
        uint end = w.MusouBuffsOf(1).Fear;
        Assert.Equal(w.Tick + (uint)(15 * rate), end);
        for (int k = 0; k < 5 * rate; k++) w.Step(_ => default);
        Assert.True(w.MusouGiveBuff(1, MusouPowerup.Fear));
        Assert.Equal(w.Tick + (uint)(15 * rate), w.MusouBuffsOf(1).Fear);   // the full time again, not 25 s
        for (int k = 0; k < 15 * rate + 1; k++) w.Step(_ => default);
        Assert.False(w.MusouBuffActive(1, MusouPowerup.Fear));
    }

    [Fact]
    public void A_death_ends_the_buffs()
    {
        var config = Prepared with { RespawnTime = 1f, MusouRules = Prepared.MusouRules! with { PlayerDamageScale = 100f } };
        var w = new World(config) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        Assert.True(w.MusouGiveBuff(1, MusouPowerup.DoubleDamage));
        uint version = w.MusouBuffVersion(1);
        for (int t = 0; t < 30 * 20 && w.Players[1].Alive; t++)
        {
            if (w.Musou!.Alive == 0) w.Musou.Spawn(MusouEnemyType.Boy, w.Players[1].Position + new Vector3(0f, 1.2f, 0f), 0f, 85f);
            w.Step(_ => default);
        }
        Assert.False(w.Players[1].Alive);
        w.Step(_ => default);
        Assert.False(w.MusouBuffActive(1, MusouPowerup.DoubleDamage));
        Assert.NotEqual(version, w.MusouBuffVersion(1));
        Assert.False(w.MusouGiveBuff(1, MusouPowerup.RapidFire));   // not for the dead
    }

    [Fact]
    public void Double_damage_and_supercharged_melee_scale_the_players_damage()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(0));
        w.Step(_ => default);
        var far = new Vector3(500f, 500f, 0f);
        ushort a = w.Musou!.Spawn(MusouEnemyType.Boy, far, 0f, 1000f);
        ushort b = w.Musou.Spawn(MusouEnemyType.Boy, far + new Vector3(3f, 0f, 0f), 0f, 1000f);
        w.MusouGiveBuff(1, MusouPowerup.DoubleDamage);
        w.MusouGiveBuff(2, MusouPowerup.Supercharged);
        w.Step(_ => default);   // the scales reach the pool once a tick
        Health(w, a, out float a0); Health(w, b, out float b0);
        w.Musou.Damage(a, 1, 10f, Vector3.Zero, out _);                                     // Double Damage: a shot 2x
        w.Musou.Damage(a, 1, 10f, Vector3.Zero, out _, cause: MusouDeathKind.Melee);        // and melee 2x
        w.Musou.Damage(b, 2, 10f, Vector3.Zero, out _);                                     // Supercharged: a shot 1x
        w.Musou.Damage(b, 2, 10f, Vector3.Zero, out _, cause: MusouDeathKind.Melee);        // melee 2x
        Health(w, a, out float a1); Health(w, b, out float b1);
        Assert.Equal(40f, a0 - a1, 3);
        Assert.Equal(30f, b0 - b1, 3);
    }

    static void Health(World w, ushort id, out float health) { Assert.True(w.Musou!.TryGet(id, out var e)); health = e.Health; }

    // ---------------------------------------------------------------- the owner's x2 caps (2026-11-10) and the wider cleave

    static readonly StimSpec StimData = new(5f, 0.3f, 0.9f) { MeleeDamage = 1.35f };
    static readonly ThrownSpec StimItem = new("sm_mp_eq_stimpack", 1, -1, 6f, 0f, ThrownSpec.NoProjectile) { Stim = StimData };
    /// <summary>A slow automatic gun (0.3 s a shot): x1, x1.2, x2 and x2.4 fire 9, 8, 5 and 4 ticks apart, so the counts tell the caps apart.</summary>
    static readonly WeaponSpec SlowGun = new("slow_rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.3f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Swing = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);

    /// <summary>The Musou combat world of <see cref="MusouCombatRulesTests"/> (enemies that stand still and hurt nobody), with a stim as the class's equipment and the slow gun; <paramref name="extras"/> its profile.</summary>
    static SimConfig CombatConfig(ContentExtras extras = ContentExtras.Musou) => new()
    {
        Weapons = [SlowGun],
        Thrown = [StimItem],
        Classes = [new("sm - tactical", 0, -1, [Swing]) { PrimaryChoices = [0], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical" }],
        Extras = extras,
        MusouRules = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] },
    };

    static World CombatWorld()
    {
        var w = new World(CombatConfig()) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(1, w.AssignClass(0), equipmentChoice: 0);
        w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    /// <summary>The damage one light swing of player 1 (facing +Y) deals a sturdy Boy at <paramref name="offset"/> from it, with <paramref name="buffs"/> running and, if <paramref name="stim"/>, a stim.</summary>
    static float SwingDamage(Vector3 offset, bool stim, params MusouPowerup[] buffs)
    {
        var w = CombatWorld();
        if (stim) Set(w, 1, p => p with { StimLeft = 30f, Equipment = 0 });
        Assert.Equal(stim, Stims.Active(w.Config, w.Players[1]) is not null);
        foreach (var b in buffs) Assert.True(w.MusouGiveBuff(1, b));
        w.Step(_ => default);   // the scales reach the pool once a tick
        ushort boy = w.Musou!.Spawn(MusouEnemyType.Boy, w.Players[1].Position + offset, 0f, 1e5f);
        Health(w, boy, out float before);
        for (int t = 0; t < 20; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Melee : 0));
        Health(w, boy, out float after);
        return before - after;
    }

    [Fact]
    public void Melee_never_passes_x2_with_supercharged_double_damage_and_a_stim_together()
    {
        var ahead = new Vector3(0f, 1.5f, 0f);
        float plain = SwingDamage(ahead, false);
        Assert.True(plain > 0f);
        Assert.Equal(2f * plain, SwingDamage(ahead, false, MusouPowerup.Supercharged), 2);                              // Supercharged alone: x2
        Assert.Equal(2f * plain, SwingDamage(ahead, false, MusouPowerup.DoubleDamage), 2);                              // Double Damage alone: x2
        Assert.Equal(2f * plain, SwingDamage(ahead, false, MusouPowerup.Supercharged, MusouPowerup.DoubleDamage), 2);   // both: x2, not x4
        Assert.Equal(2f * plain, SwingDamage(ahead, true), 2);                                                          // Musou's stim alone: x2, as before
        Assert.Equal(2f * plain, SwingDamage(ahead, true, MusouPowerup.Supercharged, MusouPowerup.DoubleDamage), 2);    // all three: x2, not x8
        Assert.Equal(2f, MusouPowerupRules.Cap);

        var w = CombatWorld();
        Set(w, 1, p => p with { StimLeft = 30f, Equipment = 0 });
        w.MusouGiveBuff(1, MusouPowerup.Supercharged);
        w.MusouGiveBuff(1, MusouPowerup.DoubleDamage);
        w.Step(_ => default);
        Assert.Equal(2f, w.MusouMeleeMultiplier(1), 4);
        Assert.Equal(2f, w.Musou!.MeleeScaleOf(1), 4);
        // Shots keep Double Damage's x2 (the cap is about stacking; a stim adds nothing to a shot).
        ushort boy = w.Musou.Spawn(MusouEnemyType.Boy, new Vector3(300f, 300f, 0f), 0f, 1000f);
        Health(w, boy, out float h0);
        w.Musou.Damage(boy, 1, 10f, Vector3.Zero, out _);
        Health(w, boy, out float h1);
        Assert.Equal(20f, h0 - h1, 3);
    }

    [Fact]
    public void Supercharged_melee_widens_the_cleave()
    {
        // At 101 degrees off the facing, 2 m out: outside the 5-wave run's cleave (half-angle 60 x 1.5 = 90 degrees), inside Supercharged's 135.
        var beside = new Vector3(2f, -0.4f, 0f);
        Assert.Equal(0f, SwingDamage(beside, false));
        Assert.True(SwingDamage(beside, false, MusouPowerup.Supercharged) > 0f);
        Assert.Equal(0f, SwingDamage(beside, false, MusouPowerup.DoubleDamage));   // Double Damage leaves the cleave as it is
        Assert.Equal(1.5f, MusouPowerupRules.CleaveScale);
        // And longer: 1.25 times the cleave's reach straight ahead.
        var w = CombatWorld();
        float reach = w.Musou!.Rules.Combat.CleaveRange(w.Config.MeleeRange, Swing.TrackToHit, war: false);
        var far = new Vector3(0f, reach * 1.25f, 0f);
        Assert.Equal(0f, SwingDamage(far, false));
        Assert.True(SwingDamage(far, false, MusouPowerup.Supercharged) > 0f);
    }

    /// <summary>Shots and rounds spent in 90 ticks of held fire with <see cref="PlayerState.FireBoostLeft"/> and a stim's time set on player 1 (no Musou enemies: <see cref="Combat"/>'s own step).</summary>
    static (int Shots, int Spent) Fire(SimConfig config, float boost, float stim)
    {
        var w = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = MusouSimTests.Endless };
        w.Spawn(1, 0, equipmentChoice: 0);
        for (int t = 0; t < 30; t++) w.Step(_ => default);   // raised and ready
        Set(w, 1, p => p with { FireBoostLeft = boost, StimLeft = stim, Equipment = 0 });
        int clip = w.Players[1].Clip, shots = 0;
        for (int t = 0; t < 90; t++) { w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire)); shots += w.LastShots.Count; }
        return (shots, clip - w.Players[1].Clip);
    }

    [Fact]
    public void A_set_fire_boost_doubles_the_shots_and_keeps_the_clip_in_Musou_and_does_nothing_in_Classic()
    {
        var musou = CombatConfig();
        var plain = Fire(musou, 0f, 0f);
        var boosted = Fire(musou, 10f, 0f);
        Assert.True(plain.Shots >= 8 && plain.Spent == plain.Shots, $"plain {plain}");
        Assert.True(boosted.Shots >= plain.Shots * 17 / 10, $"boosted {boosted} vs plain {plain}");   // 5 ticks apart instead of 9: x1.8 at 30 ticks a second
        Assert.Equal(0, boosted.Spent);
        // The owner's "Cap at x2": Rapid Fire with a stim's x1.2 fires exactly as Rapid Fire alone (2.4 would be 4 ticks apart), and the stim alone keeps its x1.2.
        Assert.Equal(boosted, Fire(musou, 10f, 30f));
        var stimmed = Fire(musou, 0f, 30f);
        Assert.True(stimmed.Shots > plain.Shots && stimmed.Shots < boosted.Shots && stimmed.Spent == 0, $"stim {stimmed}");
        Assert.Equal(2f, musou.FireBoostCap);

        // Classic PvP: the field does nothing (FireBoost 1), the rounds are spent, the stim fires as it always did.
        var classic = CombatConfig(ContentExtras.None);
        Assert.Equal(1f, classic.FireBoost);
        Assert.Equal(float.PositiveInfinity, classic.FireBoostCap);
        var classicPlain = Fire(classic, 0f, 0f);
        Assert.Equal(classicPlain, Fire(classic, 10f, 0f));
        Assert.Equal(plain.Shots, classicPlain.Shots);
        Assert.Equal(classicPlain.Shots, classicPlain.Spent);
        Assert.Equal(Fire(classic, 0f, 30f), Fire(classic, 10f, 30f));
    }

    [Fact]
    public void A_new_round_clears_every_buff_and_hold()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        foreach (var kind in MusouPowerupRules.All) Assert.True(w.MusouGiveBuff(1, kind));
        w.MusouSpawnDrop(1, w.Players[1].Position + new Vector3(0.6f, 0f, 0f), MusouPowerupRules.Item(MusouPowerup.Fear));
        Hold(w, 1, 2);
        Assert.NotNull(w.MusouHoldOf(1));
        Assert.True(w.Players[1].FireBoostLeft > 0f);
        uint version = w.MusouBuffVersion(1);
        typeof(World).GetMethod("MusouNewRound", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
        foreach (var kind in MusouPowerupRules.All) Assert.False(w.MusouBuffActive(1, kind));
        Assert.False(w.MusouBuffsOf(1).Any(w.Tick));
        Assert.Equal(0f, w.Players[1].FireBoostLeft);
        Assert.Null(w.MusouHoldOf(1));
        Assert.NotEqual(version, w.MusouBuffVersion(1));   // the server sends the change
        Assert.Empty(w.MusouDrops);
        w.Step(_ => default);
        Assert.Equal(1f, w.Musou!.MeleeScaleOf(1));   // no scale left in the pool
    }

    [Fact]
    public void Fear_sends_the_orks_near_the_player_running_but_never_a_boss()
    {
        var sim = new MusouSim(null) { War = TfwCombatRules.Default };
        ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 10f, 0f), 0f, 100f);
        ushort boss = sim.Spawn(MusouEnemyType.BossNob, new Vector3(6f, 10f, 0f), 0f, 1000f);
        var player = new MusouTarget(1, Vector3.Zero, true);
        uint tick = 0;
        for (int k = 0; k < 20; k++) { sim.BeginTick(++tick); sim.Step([player], null, false, new List<MusouAttack>()); }   // spawned and walking in
        Assert.True(sim.TryGet(boy, out var before));
        sim.SetFear([1]);
        for (int k = 0; k < 30; k++) { sim.BeginTick(++tick); sim.Step([player], null, false, new List<MusouAttack>()); }
        Assert.True(sim.TryGet(boy, out var after));
        Assert.True(after.Position.Length() > before.Position.Length() + 1f, $"the Boy ran: {before.Position} -> {after.Position}");
        Assert.True(sim.Fleeing >= 1);
        Assert.False(MusouSim.Fears(MusouEnemyType.BossNob));
        Assert.False(MusouSim.Fears(MusouEnemyType.Warboss));
        Assert.True(sim.TryGet(boss, out var bossNow) && bossNow.Position.Length() < 12f, $"the Boss Nob stood his ground: {bossNow.Position}");
        sim.SetFear([]);
        for (int k = 0; k < 2; k++) { sim.BeginTick(++tick); sim.Step([player], null, false, new List<MusouAttack>()); }
        Assert.Equal(0, sim.Fleeing);
    }

    [Fact]
    public void Rapid_fire_sets_the_fire_boost_and_only_musou_fires_faster_and_free()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        Assert.True(w.MusouGiveBuff(1, MusouPowerup.RapidFire));
        Assert.Equal(20f, w.Players[1].FireBoostLeft, 2);
        for (int k = 0; k < w.Config.TickRate; k++) w.Step(_ => default);
        Assert.InRange(w.Players[1].FireBoostLeft, 18.8f, 19.2f);
        Assert.Equal(MusouPowerupRules.FireRate, w.Config.FireBoost);
        Assert.Equal(MusouPowerupRules.Cap, w.Config.FireBoostCap);
        Assert.Equal(1f, (Raw with { Extras = ContentExtras.None }).FireBoost);   // Classic PvP: no boost whatever the field holds
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void Tag_6_carries_the_buffs_after_the_drops_and_none_leaves_the_old_layout()
    {
        var box = new MusouBox(new Vector3(-100f), new Vector3(100f));
        var drops = new List<MusouDropRecord> { new(3, new Vector3(1f, 2f, 0f), MusouPowerupRules.Item(MusouPowerup.Fear), MusouDropState.Live) };
        var plain = MusouWire.WriteDrops(drops, box);
        Assert.Equal(plain, MusouWire.WriteDrops(drops, box, Array.Empty<MusouBuffRecord>()));
        Assert.Equal(1 + MusouWire.DropBytes, plain.Length);
        var buffs = new List<MusouBuffRecord> { new(MusouPowerup.DoubleDamage, 900), new(MusouPowerup.RapidFire, 12) };
        var bytes = MusouWire.WriteDrops(drops, box, buffs);
        Assert.Equal(1 + MusouWire.DropBytes + 1 + 2 * MusouWire.BuffBytes, bytes.Length);
        var read = MusouWire.ReadDrops(bytes, box, out var back);
        Assert.NotNull(read);
        Assert.Single(read!);
        Assert.Equal(MusouPowerupRules.Item(MusouPowerup.Fear), read![0].Item);
        Assert.Equal(buffs, back);
        Assert.NotNull(MusouWire.ReadDrops(bytes, box));
        var twice = MusouWire.WriteDrops(drops, box, [new(MusouPowerup.Fear, 1), new(MusouPowerup.Fear, 2)]);
        Assert.Null(MusouWire.ReadDrops(twice, box, out _));   // a kind twice is malformed
        bytes[^3] = 9;
        Assert.Null(MusouWire.ReadDrops(bytes, box, out _));   // an unknown kind too
    }
}
