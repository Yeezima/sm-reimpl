using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Musou Dreadnought D1 (docs/musou-dreadnought-design.md; owner's decisions of 2026-11-14): the class is appended after the Librarian under Musou only, with a body of its own (the data's speeds, the owner's health
/// pool of 2000 with the data's regeneration share, no shield, a 2 m by 3 m capsule), no gun, pistol, equipment, perks, jump pack or evade; its entrance multiplies everything it receives by 0 for the data's 5 s and
/// starts again on each spawn; the Orks keep their ring and reach at the edge of its wider capsule. Classic configs and every other class are untouched.
/// </summary>
public class DreadnoughtClassTests
{
    const float Dt = 1f / 30f;
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly WeaponSpec Bolter = new("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec BoltPistol = new("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);

    static readonly SimConfig Raw = new()
    {
        Weapons = [Bolter, BoltPistol],
        Perks = [new PerkSpec("mp_perk_a", "a", PerkKind.Weapon, 1)],
        Classes =
        [
            new("sm - tactical", 0, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], PerkChoices = [0], DefaultPerk = 0, Side = Team.SpaceMarines, Role = "tactical" },
            new("csm - tactical", 0, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = Team.Chaos, Role = "tactical" },
        ],
        Extras = ContentExtras.Musou,
    };
    static readonly SimConfig Prepared = MusouLoadout.Prepare(Raw);
    static int DreadnoughtIndex(SimConfig c) => c.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);

    [Fact]
    public void The_class_is_appended_after_the_librarian_and_every_earlier_index_is_unchanged()
    {
        var names = Prepared.Classes.Select(c => c.Name).ToArray();
        Assert.Equal(["sm - tactical", "csm - tactical", LibrarianRules.ClassName, DreadnoughtRules.ClassName], names);
        Assert.Equal(3, DreadnoughtIndex(Prepared));
        Assert.True(DreadnoughtRules.IsDreadnought(Prepared, 3));
        Assert.False(DreadnoughtRules.IsDreadnought(Prepared, 2));
        Assert.False(DreadnoughtRules.IsDreadnought(Prepared, 0));
        Assert.False(DreadnoughtRules.IsDreadnought(Raw, 3));   // the unprepared (Classic-shaped) config has no such class
        Assert.False(DreadnoughtRules.IsDreadnought(Prepared with { Extras = ContentExtras.None }, 3));
    }

    [Fact]
    public void Without_musou_nothing_is_added_and_preparing_twice_adds_it_once()
    {
        var classic = Raw with { Extras = ContentExtras.None };
        Assert.Same(classic, MusouLoadout.Prepare(classic));
        Assert.Empty(MusouLoadout.Prepare(classic).Bodies);
        var again = MusouLoadout.Prepare(Prepared);
        Assert.Single(again.Classes, c => c.Name == DreadnoughtRules.ClassName);
        Assert.Single(again.Bodies);
    }

    [Fact]
    public void Its_role_is_new_with_the_space_marine_side_the_fixed_kit_and_no_choices()
    {
        var c = Prepared.Classes[DreadnoughtIndex(Prepared)];
        Assert.Equal((Team.SpaceMarines, "dreadnought", SmSim.Race.None), (c.Side, c.Role, c.Race));
        Assert.Equal((-1, -1), (c.Primary, c.Pistol));
        Assert.Empty(c.PrimaryChoices);
        Assert.Empty(c.PistolChoices);
        Assert.Empty(c.EquipmentChoices);
        Assert.Empty(c.PerkChoices);
        Assert.Equal((-1, -1, -1), (c.DefaultPerk, c.JumpPack, c.Evade));
        Assert.Equal(0, c.Body);
        Assert.DoesNotContain(Prepared.Classes.Where(k => k.Name != DreadnoughtRules.ClassName), k => k.Role == "dreadnought");
        // The other classes keep no body of their own.
        Assert.All(Prepared.Classes.Where(k => k.Name != DreadnoughtRules.ClassName), k => Assert.Equal(-1, k.Body));
    }

    [Fact]
    public void His_body_is_the_datas_speeds_the_owners_health_pool_with_the_datas_regeneration_share_and_a_big_capsule()
    {
        var body = Assert.Single(Prepared.Bodies);
        Assert.True(body.IsValid);
        Assert.Equal((6.5f * 1.3f, 6.5f * 1.3f, 4.5f * 1.3f, 11f * 1.3f, 3f * 1.3f), (body.RunSpeed, body.StrafeSpeed, body.BackSpeed, body.SprintSpeed, body.WalkSpeed));
        Assert.Equal((5f * 1.3f, 5f * 1.3f, 4f * 1.3f), (body.AimRunSpeed, body.AimStrafeSpeed, body.AimBackSpeed));
        Assert.Equal((2000f, 0f, 5f), (body.HealthMax, body.ShieldMax, body.HealthRegenDelay));
        // 250 a second of 8000 is 3.125 % of his health a second; the same share of his 2000.
        Assert.Equal(DreadnoughtRules.DataRegenRate / DreadnoughtRules.DataHealth, body.HealthRegenRate / body.HealthMax, 6);
        Assert.Equal(62.5f, body.HealthRegenRate, 4);
        Assert.Equal((1.0f, 3.0f), (body.CapsuleRadius!.Value, body.CapsuleHeight!.Value));
        Assert.Null(body.HitVolumes);   // the marine's volumes stay (no player-versus-player damage in Musou)
        Assert.Equal(DreadnoughtRules.Body(), body);
    }

    [Fact]
    public void The_dreadnoughts_config_view_carries_the_capsule_and_every_other_class_keeps_the_marine_capsule()
    {
        int d = DreadnoughtIndex(Prepared);
        var view = Prepared.For(d);
        Assert.Equal((1.0f, 3.0f), (view.CapsuleRadius, view.CapsuleHeight));
        Assert.Equal((2000f, 62.5f, 5f, 0f), (view.HealthMax, view.HealthRegenRate, view.HealthRegenDelay, view.ShieldMax));
        Assert.Equal((Raw.CapsuleRadius, Raw.CapsuleHeight), (Prepared.For(0).CapsuleRadius, Prepared.For(0).CapsuleHeight));
        Assert.Same(Prepared, Prepared.For(0));
        Assert.Equal((Raw.CapsuleRadius, Raw.CapsuleHeight), (Prepared.CapsuleRadius, Prepared.CapsuleHeight));
    }

    [Fact]
    public void A_body_without_a_capsule_writes_exactly_the_bytes_it_always_did_and_one_with_it_adds_a_marked_tail()
    {
        var plain = new BodySpec("x", 6f, 6f, 4f, 9f, 3f, 3f, 3f, 2f, 100f, 5f, 5f, 100f, 7f, 33f);
        byte[] Written(BodySpec body)
        {
            var m = new MemoryStream();
            using (var w = new BinaryWriter(m)) typeof(BodySpec).GetMethod("Write", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(body, [w]);
            return m.ToArray();
        }
        // The stream before this change, written out independently: the name, the fourteen floats, the volumes (none: the config's).
        var expected = new MemoryStream();
        using (var w = new BinaryWriter(expected))
        {
            w.Write("x");
            foreach (float f in new[] { 6f, 6f, 4f, 9f, 3f, 3f, 3f, 2f, 100f, 5f, 5f, 100f, 7f, 33f }) w.Write(f);
        }
        var before = expected.ToArray();
        var now = Written(plain);
        Assert.Equal(before, now[..before.Length]);
        Assert.True(now.Length - before.Length is > 0 and < 8);   // only the volumes' absent marker follows, as before
        var withCapsule = Written(plain with { CapsuleRadius = 1f });
        Assert.Equal(now, withCapsule[..now.Length]);
        Assert.True(withCapsule.Length > now.Length);
        Assert.False((plain with { CapsuleRadius = 0f }).IsValid);
        Assert.False((plain with { CapsuleHeight = float.NaN }).IsValid);
        Assert.True((plain with { CapsuleRadius = 1f, CapsuleHeight = 3f }).IsValid);
    }

    [Fact]
    public void The_class_changes_the_musou_hash_and_a_config_with_only_a_plain_body_hashes_as_it_did_before_the_capsule_field()
    {
        Assert.NotEqual(Raw.Hash(), Prepared.Hash());
        // The golden pins of the Classic and extended (Ork body) profiles are ConfigHashPinTests (install-gated; they passed with this change).
        Assert.False(Raw.HasBodyCapsules);
        Assert.True(Prepared.HasBodyCapsules);
        Assert.False((Raw with { Bodies = [new BodySpec("x", 6f, 6f, 4f, 9f, 3f, 3f, 3f, 2f, 100f, 5f, 5f, 100f, 7f, 33f)] }).HasBodyCapsules);
    }

    // ---- in a match ----

    static World NewWorld()
    {
        var w = new World(Prepared with { MusouRules = MusouRules.Default with { PlayerDamageScale = 1f } }) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Musou!.Frozen = true;
        return w;
    }

    static void Steps(World w, int n) { for (int k = 0; k < n; k++) w.Step(_ => default); }

    [Fact]
    public void A_dreadnought_spawns_with_his_own_health_and_no_shield()
    {
        var w = NewWorld();
        w.Spawn(1, DreadnoughtIndex(w.Config));
        w.Spawn(2, 0);
        Steps(w, 2);
        Assert.Equal((2000f, 0f), (w.Players[1].Health, w.Players[1].Shield));
        Assert.Equal(100f, w.Players[2].Health);
        Assert.True(w.Players[2].Shield > 0f);
    }

    [Fact]
    public void His_entrance_takes_no_damage_for_five_seconds_then_everything_as_before_and_a_marine_never_has_one()
    {
        var w = NewWorld();
        w.Spawn(1, DreadnoughtIndex(w.Config));
        w.Spawn(2, 0);
        Steps(w, 3);
        Assert.Equal(0f, w.DamageTakenScale(1));
        Assert.Equal(1f, w.DamageTakenScale(2));
        Assert.True(w.DreadnoughtEntryLeft(1) > 4.8f);
        Steps(w, 30 * 4);   // about 4.1 s in
        Assert.Equal(0f, w.DamageTakenScale(1));
        Steps(w, 30 * 2);   // past 5 s
        Assert.Equal(1f, w.DamageTakenScale(1));
        Assert.Equal(0f, w.DreadnoughtEntryLeft(1));
        Assert.Equal(1f, w.DamageTakenScale(2));
    }

    [Fact]
    public void A_fresh_join_of_the_same_id_starts_the_entrance_again()
    {
        var w = NewWorld();
        w.Spawn(1, DreadnoughtIndex(w.Config));
        Steps(w, 30 * 6);
        Assert.Equal(1f, w.DamageTakenScale(1));
        w.Spawn(1, DreadnoughtIndex(w.Config));   // the id joins again (a respawn is the same: the dead are forgotten at the next step)
        w.MusouJoined(1);
        Assert.Equal(0f, w.DamageTakenScale(1));
        Steps(w, 3);
        Assert.Equal(0f, w.DamageTakenScale(1));
        Assert.True(w.DreadnoughtEntryLeft(1) > 4.8f);
    }

    [Fact]
    public void A_death_and_the_cores_respawn_start_the_entrance_again()
    {
        var w = NewWorld();
        w.Spawn(1, DreadnoughtIndex(w.Config));
        Steps(w, 30 * 6);
        Assert.Equal(1f, w.DamageTakenScale(1));
        var field = typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Dictionary<int, PlayerState>)field.GetValue(w)!)[1] = Combat.Killed(w.Players[1], w.Config) with { RespawnLeft = 0.1f };
        Steps(w, 30);
        Assert.True(w.Players[1].Alive);
        Assert.True(w.DreadnoughtEntryLeft(1) > 3.5f);
        Assert.Equal(0f, w.DamageTakenScale(1));
    }

    [Fact]
    public void Changing_class_away_ends_the_entrance_and_choosing_him_again_begins_one()
    {
        var w = NewWorld();
        w.Spawn(1, DreadnoughtIndex(w.Config));
        Steps(w, 3);
        Assert.True(w.DreadnoughtEntryLeft(1) > 0f);
        w.Spawn(1, 0);
        Steps(w, 2);
        Assert.Equal((0f, 1f), (w.DreadnoughtEntryLeft(1), w.DamageTakenScale(1)));
        w.Spawn(1, DreadnoughtIndex(w.Config));
        Steps(w, 2);
        Assert.Equal(0f, w.DamageTakenScale(1));
    }

    // ---- the Orks and the big body ----

    [Fact]
    public void An_ork_keeps_his_ring_at_the_edge_of_a_wider_capsule_and_still_lands_his_swing()
    {
        float Closest(float body, out bool swung)
        {
            var sim = new MusouSim(null, MusouRules.Default);
            ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 12f, 0f), 0f, 1000f);
            var targets = new[] { new MusouTarget(1, Vector3.Zero, true, Body: body) };
            var attacks = new List<MusouAttack>();
            float closest = float.MaxValue;
            bool hit = false;
            for (uint t = 1; t <= 30 * 20; t++)
            {
                sim.BeginTick(t);
                attacks.Clear();
                sim.Step(targets, null, true, attacks);
                if (attacks.Any(a => a.Enemy == boy && a.Melee)) hit = true;
                Assert.True(sim.TryGet(boy, out var e));
                closest = MathF.Min(closest, new Vector2(e.Position.X, e.Position.Y).Length());
            }
            swung = hit;
            return closest;
        }
        float marine = Closest(0f, out bool marineSwung), big = Closest(0.55f, out bool bigSwung);
        Assert.True(marineSwung, "the Boy never struck the marine");
        Assert.True(bigSwung, "the Boy never struck the big body: his reach must count the capsule");
        Assert.True(big >= marine + 0.35f, $"closest {big:0.00} m to the big body against {marine:0.00} m to the marine: the ring did not widen");
    }

    [Fact]
    public void Only_the_dreadnought_has_a_body_ring_and_it_is_the_capsule_beyond_a_marines()
    {
        var w = NewWorld();
        w.Spawn(1, DreadnoughtIndex(w.Config));
        w.Spawn(2, 0);
        Steps(w, 1);
        var ring = typeof(World).GetMethod("DreadnoughtBodyRing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        float Ring(int id) => (float)ring.Invoke(w, [w.Players[id]])!;
        Assert.Equal(DreadnoughtRules.CapsuleRadius - w.Config.CapsuleRadius, Ring(1), 4);
        Assert.Equal(0f, Ring(2));
    }

    [Fact]
    public void The_hold_before_the_swing_is_the_same_distance_from_the_edge_of_the_body_for_a_marine_and_the_big_body()
    {
        // MeleeHold compared with the reach that already leaves the capsule out: he must not hold off a second time (review 2026-11-14).
        float HoldDistance(float body)
        {
            var sim = new MusouSim(null, MusouRules.Default);
            ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 12f, 0f), 0f, 1000f);
            var targets = new[] { new MusouTarget(1, Vector3.Zero, true, Body: body) };
            var attacks = new List<MusouAttack>();
            for (uint t = 1; t <= 30 * 8; t++) { sim.BeginTick(t); attacks.Clear(); sim.Step(targets, null, true, attacks); }
            Assert.True(sim.TryGet(boy, out var e));
            return new Vector2(e.Position.X, e.Position.Y).Length() - body;
        }
        Assert.Equal(HoldDistance(0f), HoldDistance(0.55f), 0.35f);
    }
}
