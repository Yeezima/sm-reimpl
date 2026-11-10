using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Librarian (Musou Mode only; owner's brief 2026-11-07, docs/musou-expansion.md S5; pass 3 2026-11-10): the class exists only under Musou, the Psychic Stress ratios (20 s of lightning = a Shockwave twice over),
/// the lock and its release, the melee relief, the cooldown, the chain lightning's cap and falloff, what the 60 m Shockwave does to an ordinary, an elite and a boss Ork, and that the Warp Pull is gone: Grenade is his
/// equipment's, every Space Marine item.
/// </summary>
public class MusouLibrarianTests
{
    const int Rate = 30;
    const float Dt = 1f / Rate;
    static readonly LibrarianPress None = default, Hold = new(true, false), ShockKey = new(false, true);

    /// <summary>A Librarian at the origin looking along +Y, a pool of enemies, and a clock.</summary>
    sealed class Rig
    {
        public readonly MusouSim Sim = new(null) { TickRate = Rate };
        public readonly LibrarianPsyker Psyker = new();
        public uint Tick;
        public Vector3 Eye = new(0f, 0f, 1f);

        public LibrarianCaster Caster => new(1, Vector3.Zero, Eye, 0f, 0f);

        /// <summary>One tick of the Librarian only (the pool does not move).</summary>
        public void Cast(LibrarianPress press, int ticks = 1)
        {
            for (int k = 0; k < ticks; k++) { Sim.BeginTick(++Tick); Psyker.Step(Sim, Caster, press, Dt); }
        }

        /// <summary>One tick of the Librarian and of the pool (enemies move; nothing attacks).</summary>
        public void Run(LibrarianPress press, int ticks)
        {
            var targets = new[] { new MusouTarget(1, Vector3.Zero, true) };
            var attacks = new List<MusouAttack>();
            for (int k = 0; k < ticks; k++)
            {
                Sim.BeginTick(++Tick);
                Psyker.Step(Sim, Caster, k == 0 ? press : None, Dt);
                attacks.Clear();
                Sim.Step(targets, null, false, attacks);
            }
        }

        public ushort Add(MusouEnemyType type, float x, float y, float? health = null) =>
            Sim.Spawn(type, new Vector3(x, y, 0f), 0f, health ?? MusouRules.Default.Of(type).Health);

        public MusouEnemy Get(ushort id) { Assert.True(Sim.TryGet(id, out var e)); return e; }
        public float Distance(ushort id) => new Vector2(Get(id).Position.X, Get(id).Position.Y).Length();
    }

    // ---------------------------------------------------------------- stress

    [Fact]
    public void Twenty_seconds_of_lightning_fill_the_scale_and_a_shockwave_is_ten_seconds_of_it()
    {
        var r = new Rig();
        r.Cast(Hold, 10 * Rate);
        Assert.Equal(50f, r.Psyker.Stress, 0.1f);
        Assert.False(r.Psyker.Locked);
        r.Cast(Hold, 10 * Rate);
        Assert.Equal(LibrarianRules.StressMax, r.Psyker.Stress);
        Assert.True(r.Psyker.Locked);

        var shock = new Rig();
        shock.Cast(ShockKey);
        Assert.Equal(50f, shock.Psyker.Stress);
        Assert.Equal(LibrarianRules.ShockwaveStress, LibrarianRules.LightningStressPerSecond * 10f);
    }

    [Fact]
    public void Ten_seconds_of_lightning_and_a_shockwave_lock_him_and_lightning_then_does_nothing()
    {
        var r = new Rig();
        r.Add(MusouEnemyType.Boy, 0f, 70f, 5000f);   // out of every reach: the lightning strikes nothing, the Shockwave spares it
        r.Cast(Hold, 10 * Rate);
        r.Cast(ShockKey);
        Assert.Equal(100f, r.Psyker.Stress);
        Assert.True(r.Psyker.Locked);
        r.Cast(Hold);
        Assert.False(r.Psyker.Casting);
        Assert.Empty(r.Psyker.Links);
        Assert.Equal(0, r.Psyker.LastStruck);
    }

    [Fact]
    public void At_the_top_everything_stays_locked_until_the_stress_is_back_at_zero_cooling_after_a_second()
    {
        var r = new Rig();
        r.Cast(Hold, 20 * Rate);
        Assert.True(r.Psyker.Locked);
        r.Cast(None, Rate / 2);   // half a second: still no cooling
        Assert.Equal(100f, r.Psyker.Stress);
        r.Cast(None, Rate / 2 + 1);   // past the second: it begins
        Assert.True(r.Psyker.Stress < 100f);
        r.Cast(ShockKey);   // locked: a press does nothing
        Assert.Equal(0, r.Psyker.ShockwaveCount);
        // 8 s of cooling from full at 12.5 a second; at 7 s it is not yet unlocked.
        r.Cast(None, 7 * Rate);
        Assert.True(r.Psyker.Stress > 0f && r.Psyker.Locked);
        r.Cast(None, 2 * Rate);
        Assert.Equal(0f, r.Psyker.Stress);
        Assert.False(r.Psyker.Locked);
        r.Cast(None);
        r.Cast(ShockKey);
        Assert.Equal(1, r.Psyker.ShockwaveCount);
    }

    [Fact]
    public void Casting_resets_the_cooling_delay()
    {
        var r = new Rig();
        r.Cast(Hold, 5 * Rate);
        float high = r.Psyker.Stress;
        r.Cast(None, Rate / 2);
        r.Cast(Hold, 1);   // a cast inside the delay: the delay starts over
        r.Cast(None, Rate / 2);
        Assert.True(r.Psyker.Stress >= high);
        r.Cast(None, 2 * Rate);
        Assert.True(r.Psyker.Stress < high);
    }

    [Fact]
    public void Each_melee_hit_takes_six_off_and_can_unlock_him_at_zero()
    {
        var r = new Rig();
        r.Cast(Hold, 10 * Rate);
        float before = r.Psyker.Stress;
        r.Psyker.MeleeHit();
        Assert.Equal(before - 6f, r.Psyker.Stress, 0.001f);
        for (int k = 0; k < 20; k++) r.Psyker.MeleeHit();
        Assert.Equal(0f, r.Psyker.Stress);

        var locked = new Rig();
        locked.Cast(Hold, 20 * Rate);
        Assert.True(locked.Psyker.Locked);
        for (int k = 0; k < 17; k++) locked.Psyker.MeleeHit();   // 17 x 6 = 102
        Assert.Equal(0f, locked.Psyker.Stress);
        Assert.False(locked.Psyker.Locked);
    }

    [Fact]
    public void The_shockwave_waits_twelve_seconds_and_a_held_key_casts_it_once()
    {
        var s = new Rig();
        s.Cast(ShockKey);
        s.Cast(None, 11 * Rate);
        s.Cast(ShockKey);
        Assert.Equal(1, s.Psyker.ShockwaveCount);
        s.Cast(None, 2 * Rate);
        s.Cast(ShockKey);
        Assert.Equal(2, s.Psyker.ShockwaveCount);
        Assert.Equal(LibrarianRules.ShockwaveCooldown, s.Psyker.ShockwaveCooldown, 0.001f);

        var held = new Rig();
        held.Cast(ShockKey, 3);
        Assert.Equal(1, held.Psyker.ShockwaveCount);
        held.Cast(None, 13 * Rate);   // the stress of 50 cools away; the cooldown ends
        held.Cast(ShockKey);
        Assert.Equal(2, held.Psyker.ShockwaveCount);
    }

    // ---------------------------------------------------------------- chain lightning

    [Fact]
    public void The_chain_strikes_forty_at_most_and_every_link_takes_the_same()
    {
        var r = new Rig();
        r.Eye = MusouSim.Chest(Vector3.Zero, r.Sim.Body(MusouEnemyType.Boy).Height);
        var ids = new List<ushort>();
        for (int k = 0; k < 50; k++) ids.Add(r.Add(MusouEnemyType.Boy, 0f, 6f + 2f * k, 10000f));   // a line, 2 m apart: every jump finds the next
        r.Cast(Hold);
        Assert.Equal(LibrarianRules.MaxTargets, r.Psyker.LastStruck);
        float primary = LibrarianRules.PrimaryDps * Dt;
        for (int k = 0; k < 50; k++)
        {
            float lost = 10000f - r.Get(ids[k]).Health;
            if (k < LibrarianRules.MaxTargets) Assert.Equal(primary, lost, 0.001f);   // no fall-off since the owner's 2026-11-11 "it always stays constant" (each jump x0.85 before)
            else Assert.Equal(0f, lost);
        }
        Assert.Equal(LibrarianRules.MaxDrawnLinks, r.Psyker.Links.Count);   // the drawing is capped, the damage is not
        Assert.Equal(112.5f, LibrarianRules.PrimaryDps);   // 90 until the owner's "+25% range and damage" (2026-11-11)
    }

    [Fact]
    public void A_gap_wider_than_the_jump_radius_ends_the_chain_and_a_target_out_of_range_is_not_picked()
    {
        var r = new Rig();
        r.Eye = MusouSim.Chest(Vector3.Zero, r.Sim.Body(MusouEnemyType.Boy).Height);
        var near = r.Add(MusouEnemyType.Boy, 0f, 10f, 1000f);
        var second = r.Add(MusouEnemyType.Boy, 0f, 25f, 1000f);   // 15 m on: in reach of the 16 m jump (owner 2026-11-11; 8 m before)
        var third = r.Add(MusouEnemyType.Boy, 0f, 42f, 1000f);    // 17 m on: past it
        var far = r.Add(MusouEnemyType.Boy, 0f, 80f, 1000f);      // beyond 37.5 m from him
        r.Cast(Hold);
        Assert.Equal(2, r.Psyker.LastStruck);
        Assert.True(r.Get(near).Health < 1000f && r.Get(second).Health < 1000f);
        Assert.Equal(1000f, r.Get(third).Health);
        Assert.Equal(1000f, r.Get(far).Health);
    }

    [Fact]
    public void Lightning_with_nothing_in_the_sights_draws_a_bolt_but_strikes_nobody_and_still_builds_stress()
    {
        var r = new Rig();
        var behind = r.Add(MusouEnemyType.Boy, 0f, -8f, 1000f);
        r.Cast(Hold, Rate);
        Assert.Equal(0, r.Psyker.LastStruck);
        Assert.Single(r.Psyker.Links);
        Assert.Equal(1000f, r.Get(behind).Health);
        Assert.Equal(5f, r.Psyker.Stress, 0.1f);
    }

    [Fact]
    public void Lightning_kills_credit_the_librarian_and_wears_a_weirdboys_shield_down()
    {
        var r = new Rig();
        var gretchin = r.Add(MusouEnemyType.Gretchin, 0f, 8f);
        var weirdboy = r.Add(MusouEnemyType.Weirdboy, 3f, 40f);   // out of the chain: shield checked below with its own rig
        r.Cast(Hold, 40);   // 30 hp at 90 a second: dead in 1/3 s
        Assert.False(r.Get(gretchin).Alive);
        var kills = new List<MusouKill>();
        r.Sim.TakeKills(kills);
        Assert.Contains(kills, k => k.Enemy == gretchin && k.Player == 1);
        Assert.True(r.Sim.ShieldOf(weirdboy) > 0f);

        var w = new Rig();
        w.Eye = MusouSim.Chest(Vector3.Zero, w.Sim.Body(MusouEnemyType.Weirdboy).Height);
        var wb = w.Add(MusouEnemyType.Weirdboy, 0f, 15f);
        float shield = w.Sim.ShieldOf(wb);
        w.Cast(Hold, 10);
        Assert.True(w.Sim.ShieldOf(wb) < shield);   // not stopped whole as a bullet is
    }

    // ---------------------------------------------------------------- psychic shockwave (pass 3: 60 m, 300 damage; owner 2026-11-10)

    [Fact]
    public void The_shockwave_does_300_within_60_metres_and_kills_every_ordinary_ork_and_chaos_marine()
    {
        var r = new Rig();
        var gretchin = r.Add(MusouEnemyType.Gretchin, 2f, 3f);
        var boy = r.Add(MusouEnemyType.Boy, -30f, 20f);
        var ard = r.Add(MusouEnemyType.ArdBoy, 0f, -45f);
        var chaos = r.Add(MusouEnemyType.ChaosMarine, 40f, 40f);   // 56.6 m: inside
        var edge = r.Add(MusouEnemyType.Boy, 0f, 59f);
        var outside = r.Add(MusouEnemyType.Boy, 0f, 62f);
        r.Cast(ShockKey);
        foreach (var id in new[] { gretchin, boy, ard, chaos, edge }) Assert.False(r.Get(id).Alive, $"enemy {id} lived");
        Assert.Equal(MusouRules.Default.Of(MusouEnemyType.Boy).Health, r.Get(outside).Health);
        Assert.Equal(1, r.Psyker.ShockwaveCount);
        var kills = new List<MusouKill>();
        r.Sim.TakeKills(kills);
        Assert.Contains(kills, k => k.Enemy == chaos && k.Player == 1);
        Assert.Equal(60f, LibrarianRules.ShockwaveRadius);
        Assert.Equal(300f, LibrarianRules.ShockwaveDamage);
    }

    [Fact]
    public void The_shockwave_throws_ordinary_orks_forty_to_fifty_metres_and_elites_fifteen_to_twenty()
    {
        var r = new Rig();
        var boy = r.Add(MusouEnemyType.Boy, -3f, 4f, 5000f);
        var shoota = r.Add(MusouEnemyType.ShootaBoy, 0f, -20f, 5000f);
        var nob = r.Add(MusouEnemyType.Nob, 6f, 6f, 5000f);
        var ard = r.Add(MusouEnemyType.ArdBoy, -10f, -10f, 5000f);
        var ids = new[] { boy, shoota, nob, ard };
        var start = ids.ToDictionary(i => i, i => r.Get(i).Position);
        var peak = ids.ToDictionary(i => i, _ => 0f);
        r.Cast(ShockKey);
        Assert.Equal(5000f - LibrarianRules.ShockwaveDamage, r.Get(boy).Health, 0.01f);
        for (int k = 0; k < (int)(LibrarianRules.ThrowSeconds * Rate) + 8; k++)
        {
            r.Run(None, 1);
            foreach (var id in ids) peak[id] = MathF.Max(peak[id], Vector3.Distance(r.Get(id).Position, start[id]));
        }
        Assert.InRange(peak[boy], LibrarianRules.ThrowMin - 3f, LibrarianRules.ThrowMax + 3f);
        Assert.InRange(peak[shoota], LibrarianRules.ThrowMin - 3f, LibrarianRules.ThrowMax + 3f);
        Assert.InRange(peak[nob], LibrarianRules.EliteThrowMin - 3f, LibrarianRules.EliteThrowMax + 3f);
        Assert.InRange(peak[ard], LibrarianRules.EliteThrowMin - 3f, LibrarianRules.EliteThrowMax + 3f);
        Assert.True(r.Get(boy).Position.X < start[boy].X + 0.01f && r.Get(shoota).Position.Y < start[shoota].Y + 0.01f);   // thrown away from him, not towards
    }

    [Fact]
    public void The_shockwave_hurts_bosses_but_does_not_move_them()
    {
        var r = new Rig();
        var warboss = r.Add(MusouEnemyType.Warboss, 0f, 7f);
        var bossOrk = r.Add(MusouEnemyType.BossNob, 0f, -30f);
        var warbossHealth = r.Get(warboss).Health; var bossOrkHealth = r.Get(bossOrk).Health;
        var at = new[] { warboss, bossOrk }.ToDictionary(i => i, i => r.Get(i).Position);
        r.Cast(ShockKey);
        Assert.Equal(warbossHealth - 300f, r.Get(warboss).Health, 0.01f);
        Assert.Equal(bossOrkHealth - 300f, r.Get(bossOrk).Health, 0.01f);
        r.Cast(None, Rate);
        foreach (var (id, position) in at) Assert.True(Vector3.Distance(r.Get(id).Position, position) < 0.3f, $"enemy {id} was moved");
    }

    [Fact]
    public void Every_enemy_type_has_a_tier()
    {
        foreach (MusouEnemyType type in Enum.GetValues<MusouEnemyType>())
        {
            var tier = LibrarianRules.TierOf(type);
            if (type is MusouEnemyType.Warboss or MusouEnemyType.Weirdboy || MusouBossOrks.Is(type)) Assert.Equal(LibrarianRules.Tier.Boss, tier);
            else if (type is MusouEnemyType.Nob or MusouEnemyType.ArdBoy) Assert.Equal(LibrarianRules.Tier.Elite, tier);
            else Assert.Equal(LibrarianRules.Tier.Ordinary, tier);
        }
    }

    // ---------------------------------------------------------------- the class

    static readonly WeaponSpec Bolter = new("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec BoltPistol = new("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly MeleeStep SwordHit = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 60f, 10f, 3.5f);
    static readonly ProjectileSpec FragBall = new("ebps\\musou\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null, null);
    static readonly ThrownSpec Frag = new("frag", 3, 3, 1f, 10f, FragBall);
    static readonly ThrownSpec Blind = new("blind", 3, 3, 1f, 10f, FragBall);
    static readonly ThrownSpec ChaosFrag = new("chaos frag", 3, 3, 1f, 10f, FragBall);
    static readonly MeleeCombo Chain = new("sm_mp_melee_chainsword", [Light]);
    static readonly MeleeCombo Sword = new(LibrarianRules.PowerSwordWargear, [SwordHit]);

    internal static SimConfig Raw(ContentExtras extras) => new()
    {
        Weapons = [Bolter, BoltPistol],
        Thrown = [Frag, Blind, ChaosFrag],
        Classes =
        [
            new("sm - tactical", 0, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], MeleeChoices = [Chain], Side = Team.SpaceMarines, Role = "tactical" },
            new("sm - assault", -1, 1, [SwordHit]) { PistolChoices = [1], EquipmentChoices = [1, 0], MeleeChoices = [Sword, Chain], Side = Team.SpaceMarines, Role = "assault" },
            new("csm - tactical", 0, 1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [2], Side = Team.Chaos, Role = "tactical" },
        ],
        Extras = extras,
    };

    [Fact]
    public void The_class_exists_only_under_musou_and_is_appended_after_the_others()
    {
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.All & ~ContentExtras.Musou })
        {
            var raw = Raw(extras);
            var prepared = MusouLoadout.Prepare(raw);
            Assert.Same(raw, prepared);
            Assert.DoesNotContain(prepared.Classes, c => c.Name == LibrarianRules.ClassName);
            Assert.False(LibrarianRules.IsLibrarian(prepared with { Classes = [.. prepared.Classes, prepared.Classes[0] with { Name = LibrarianRules.ClassName }] }, 3));
        }
        var musou = MusouLoadout.Prepare(Raw(ContentExtras.Musou));
        Assert.Equal(4, musou.Classes.Count);
        Assert.Equal(["sm - tactical", "sm - assault", "csm - tactical", LibrarianRules.ClassName], musou.Classes.Select(c => c.Name));
        Assert.True(LibrarianRules.IsLibrarian(musou, 3));
        Assert.False(LibrarianRules.IsLibrarian(musou, 0));
        Assert.False(LibrarianRules.IsLibrarian(musou, 9));
        Assert.True(MusouLoadout.Prepared(musou));
        Assert.Equal(musou.Classes[3].Name, MusouLoadout.Prepare(musou).Classes[3].Name);
        Assert.Equal(4, MusouLoadout.Prepare(musou).Classes.Count);   // a second Prepare adds none
    }

    [Fact]
    public void The_librarian_is_a_marine_with_no_gun_no_pistol_every_marine_equipment_item_and_the_power_sword_as_his_melee()
    {
        var c = MusouLoadout.Prepare(Raw(ContentExtras.Musou)).Classes[3];
        Assert.Equal(Team.SpaceMarines, c.Side);
        Assert.Equal((-1, -1), (c.Primary, c.Pistol));
        Assert.Empty(c.PrimaryChoices); Assert.Empty(c.PistolChoices);
        Assert.Equal([0, 1], c.EquipmentChoices);   // the Tactical's frag first, then the Assault's blind; never the Chaos item (2)
        Assert.Equal(LibrarianRules.PowerSwordWargear, c.MeleeChoices[0].Wargear);
        Assert.Equal(SwordHit, c.Melee[0]);
        Assert.Contains(c.MeleeChoices, m => m.Wargear == "sm_mp_melee_chainsword");
        // No class has the sword (a config without the Assault): the Tactical's melee stands.
        var bare = Raw(ContentExtras.Musou) with { Classes = [Raw(ContentExtras.Musou).Classes[0]] };
        var plain = MusouLoadout.Prepare(bare).Classes[1];
        Assert.Equal(Light, plain.Melee[0]);
        // No marine class at all: nothing appended.
        Assert.Single(MusouLoadout.Prepare(bare with { Classes = [Raw(ContentExtras.Musou).Classes[2]] }).Classes);
    }

    // ---------------------------------------------------------------- in the world

    internal static World NewWorld(out int librarianClass, LibrarianVariant variant = default)
    {
        var config = MusouLoadout.Prepare(Raw(ContentExtras.Musou) with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f }, LibrarianVariant = variant });
        librarianClass = config.Classes.Count - 1;
        var w = new World(config) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        return w;
    }

    [Fact]
    public void In_the_world_fire_casts_lightning_reload_shocks_grenade_is_his_equipment_and_he_shoots_nothing()
    {
        var w = NewWorld(out int cls);
        w.Spawn(1, cls);
        w.Step(_ => default);
        Assert.Contains(1, w.Librarians.Keys);
        var me = w.Players[1];
        Assert.Contains(me.Equipment, w.Config.Classes[cls].EquipmentChoices);   // he carries a marine's equipment (the Warp Pull is gone)
        Assert.Null(Loadouts.InHand(w.Config, me));
        float yaw = me.Yaw;
        var forward = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        var eye = new Vector3(0f, 0f, w.Config.EyeHeight);
        var boy = w.Musou!.Spawn(MusouEnemyType.Boy, me.Position + forward * 8f, 0f, 5000f);
        w.Musou.TryGet(boy, out var e0);
        var psyker = w.LibrarianOf(1)!;

        PlayerInput Input(InputButtons b) => new(0, 0f, 0f, yaw, b, Pitch: 0f);
        for (int t = 0; t < Rate; t++) w.Step(_ => Input(InputButtons.Fire));
        Assert.Equal(5f, psyker.Stress, 0.4f);
        w.Musou.TryGet(boy, out var e1);
        Assert.True(e1.Health < e0.Health);
        Assert.True(e0.Health - e1.Health >= LibrarianRules.PrimaryDps * 0.5f);

        float before = psyker.Stress;
        w.Step(_ => Input(InputButtons.Grenade));
        Assert.Equal(0, psyker.ShockwaveCount);   // Grenade casts no power
        Assert.True(psyker.Stress <= before + 0.01f);
        for (int t = 0; t < 3 * Rate; t++) w.Step(_ => default);
        w.Step(_ => Input(InputButtons.Reload));
        Assert.Equal(1, psyker.ShockwaveCount);
        Assert.True(psyker.Stress >= LibrarianRules.ShockwaveStress - 1f);
    }

    [Fact]
    public void A_dead_librarian_starts_his_next_life_at_rest_and_a_marine_has_no_powers()
    {
        var w = NewWorld(out int cls);
        w.Spawn(1, cls);
        w.Spawn(2, 0);
        w.Step(_ => default);
        Assert.DoesNotContain(2, w.Librarians.Keys);
        PlayerInput Input(InputButtons b) => new(0, 0f, 0f, 0f, b);
        for (int t = 0; t < 30; t++) w.Step(id => Input(id == 1 ? InputButtons.Fire : InputButtons.Fire | InputButtons.Grenade));
        Assert.True(w.LibrarianOf(1)!.Stress > 0f);
        Assert.DoesNotContain(2, w.Librarians.Keys);
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void Tag_16_round_trips_and_a_malformed_payload_is_refused()
    {
        var links = new List<LibrarianLink> { new(new Vector3(1.5f, -2.5f, 3f), new Vector3(10f, 20f, 1.1f)), new(new Vector3(10f, 20f, 1.1f), new Vector3(12f, 21f, 1f)) };
        var views = new List<LibrarianView>
        {
            new(7, 62.5f, false, true, 3.2f, 0f, 4, 9, links),
            new(9, 100f, true, false, 0f, 11.9f, 255, 0, []),
        };
        var bytes = LibrarianWire.Write(views);
        Assert.Equal(1 + 2 * LibrarianWire.RecordBytes + LibrarianWire.FirstLinkBytes + LibrarianWire.JumpBytes, bytes.Length);
        var read = LibrarianWire.Read(bytes)!;
        Assert.Equal(2, read.Count);
        Assert.Equal((ushort)7, read[0].Player);
        Assert.Equal(62.5f, read[0].Stress, 0.001f);
        Assert.True(read[0].Casting && !read[0].Locked);
        Assert.Equal(3.2f, read[0].PullCooldown, 0.001f);
        Assert.Equal((4, 9), (read[0].PullCount, read[0].ShockwaveCount));
        Assert.Equal(2, read[0].Links.Count);
        Assert.Equal(links[0].From.X, read[0].Links[0].From.X, 0.06f);
        Assert.Equal(links[1].To.Z, read[0].Links[1].To.Z, 0.11f);   // a jump's end: within half a 0.2 m step (MusouSim 42; half a decimetre before)
        Assert.True(read[1].Locked);
        Assert.Equal(11.9f, read[1].ShockwaveCooldown, 0.001f);
        Assert.Equal((byte)255, read[1].PullCount);

        Assert.Null(LibrarianWire.Read([]));
        Assert.Null(LibrarianWire.Read(bytes[..^1]));
        Assert.Null(LibrarianWire.Read([.. bytes, 0]));
        var tooMany = (byte[])bytes.Clone(); tooMany[0] = 99;
        Assert.Null(LibrarianWire.Read(tooMany));
        var stress = (byte[])bytes.Clone(); stress[3] = 0xFF; stress[4] = 0xFF;
        Assert.Null(LibrarianWire.Read(stress));
        Assert.Empty(LibrarianWire.Read([0])!);
        Assert.True(SmNet.Extras.Musou.Campaign.TfwWire.Owns(LibrarianWire.Tag));
    }

    [Fact]
    public void The_stress_ratios_hold_in_the_rules()
    {
        Assert.Equal(20f, LibrarianRules.StressMax / LibrarianRules.LightningStressPerSecond);
        Assert.Equal(8f, LibrarianRules.StressMax / LibrarianRules.CoolPerSecond);
        Assert.Equal(10f, LibrarianRules.ShockwaveStress / LibrarianRules.LightningStressPerSecond);
    }
}
