using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Musou powerups (owner, 2026-11-10): drops hold powerups, not weapons, from elites (about one in ten) and bosses (always); the use key takes one and starts its buff for its full time (again: the
/// full time again, no stacking); Double Damage doubles the player's damage, Supercharged Melee its melee damage; Fear sends the Orks near the player running (never a boss); Rapid Fire sets
/// <see cref="PlayerState.FireBoostLeft"/>, which only Musou turns into a fire boost; a death ends the buffs; wire tag 6 carries them after the drops.
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
    public void A_boss_kill_drops_a_powerup_for_every_living_player_and_the_use_key_starts_its_buff()
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
        w.Step(id => id == 1 ? Press : default);
        Assert.Equal(MusouDropState.Picked, DropsOf(w, 1)[0].State);
        Assert.True(w.MusouBuffActive(1, kind));
        Assert.False(w.MusouBuffActive(2, kind));
        Assert.Equal(1, w.MusouPowerupsTaken);
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
