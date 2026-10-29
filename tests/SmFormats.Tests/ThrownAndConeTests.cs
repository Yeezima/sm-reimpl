using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M13 readers: the melta's shapecast cone and recharging clip, rigid-body grenades, several blasts (lingering ones),
/// equipment choices per class, the hidden-equipment rule, and the throw timing from the toss graph. Synthetic files only.
/// </summary>
public class ThrownAndConeTests
{
    static readonly (float, float)[] MeltaFallOff = [(20f, 0.15f), (6.5f, 1f), (10f, 0.5f)];   // unsorted on purpose
    static readonly (float, float)[] MeltaCoverage = [(1f, 1f), (0.36f, 1f), (0.35f, 0.75f), (0.15f, 0.2f)];

    static BafDocument Melta((float, float, float, float)? shape = null) => BafDocument.Read(SyntheticBafWriter.MeltaWeapon(200f, 25f, 0.33f, 1, 12, 0.5f, 0.5f,
        shape ?? (20f, 10f, 20f, -20f), MeltaFallOff, MeltaCoverage));

    [Fact]
    public void The_melta_reads_its_cone_damage_fall_off_coverage_and_recharging_clip()
    {
        var notes = new List<string>();
        var melta = WeaponTuning.Read("melta", Melta(), notes)!;
        Assert.Equal((DeliveryKind.Cone, TriggerKind.SemiAuto, WeaponSlot.Ranged), (melta.Delivery, melta.Trigger, melta.Slot));
        Assert.Equal((200f, 0.33f, 1, 12), (melta.Damage, melta.ShotCooldown, melta.ClipSize, melta.ReserveSize));   // default entries, not co-op or fury
        var cone = melta.Cone!;
        Assert.Equal((20f, 10f, 20f, -20f), (cone.Length, cone.Angle, cone.StartLength, cone.Offset));
        Assert.Equal(new DamageSpec(200f, 25f, 1f, 1f, 1f, 1f, false), cone.Damage);
        Assert.Equal([new(6.5f, 1f), new(10f, 0.5f), new DistancePoint(20f, 0.15f)], cone.FallOff);
        Assert.Equal([new(0.15f, 0.2f), new(0.35f, 0.75f), new(0.36f, 1f), new DistancePoint(1f, 1f)], cone.CoverageFallOff);
        Assert.Equal(new RechargeSpec(0.5f, 0.5f), melta.Recharge);
        Assert.Equal(2.5f, melta.ReloadTime, 5);   // one recharged round, not a reload fallback
        Assert.Null(melta.Projectile);
        Assert.DoesNotContain(notes, n => n.Contains("triggered_reload_time") || n.Contains("fall-off has"));
        Assert.Contains(notes, n => n.Contains("melta") && n.Contains("amount -1"));
        // Controls: a shapecast without a cone shape is refused with the old note; a raycast gun has neither cone nor recharge.
        Assert.Null(WeaponTuning.Read("blob", BafDocument.Read(SyntheticBafWriter.MeltaWeapon(200f, 25f, 0.33f, 1, 12, 0.5f, 0.5f, null, [], [])), notes));
        Assert.Contains(notes, n => n.Contains("blob") && n.Contains("neither a raycast nor a projectile"));
        var bolter = WeaponTuning.Read("bolter", BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.133f, 30, 270, 1.3f, 250f, [], 1f, 1f, 1f, 1f)), notes)!;
        Assert.Equal((DeliveryKind.Raycast, (ConeSpec?)null, (RechargeSpec?)null), (bolter.Delivery, bolter.Cone, bolter.Recharge));
    }

    [Fact]
    public void A_cone_offset_that_is_not_minus_the_start_length_is_noted()
    {
        var notes = new List<string>();
        Assert.NotNull(WeaponTuning.Read("odd", Melta((20f, 10f, 20f, -5f)), notes));
        Assert.Contains(notes, n => n.Contains("odd") && n.Contains("offset"));
        notes.Clear();
        Assert.Null(WeaponTuning.Read("flat", Melta((20f, 180f, 20f, -20f)), notes));   // not a cone
        Assert.Contains(notes, n => n.Contains("flat") && n.Contains("angle"));
    }

    static readonly RigidFixture Rigid = new(0.3f, 0.4f, 0.05f, 0.05f, [(0f, 1f), (90f, 0.2f)], 0.1f, 65f);

    static BafDocument Frag(string collision = "rigidbody") => BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(47.5f, 30f, 0f, 10f, 200f, 0f,
        [(0f, 1f), (2.5f, 1f), (2.6f, 0.5f), (5f, 0.5f), (5.1f, 0.3f), (7f, 0.3f), (10f, 0.05f)], [(5f, 0f), (10f, 1f)], detonateOnCollision: false,
        collisionType: collision, bounces: 1, groundLifetime: 1f, extraAimAngle: 1.5f, rigid: Rigid, stopAtMaxBounce: true));

    static Func<string, BafDocument?> Blueprints(params (string Path, BafDocument Doc)[] docs) => docs.ToDictionary(d => d.Path, d => d.Doc).GetValueOrDefault;

    [Fact]
    public void Rigid_body_grenades_read_their_contact_rules_and_only_rigid_bodies_do()
    {
        var notes = new List<string>();
        var frag = WeaponTuning.ReadThrown("frag", BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\frag", 2, 1, 1f, 10f)), notes, Blueprints(("ebps\\p\\frag", Frag())))!;
        var p = frag.Projectile;
        var r = p.Rigid!;
        Assert.Equal((65f, 0.3f, 0.4f, 0.05f, 0.05f, 0.1f), (r.GroundAngle, r.GroundHorizontalReduction, r.GroundVerticalReduction, r.EntityHorizontalReduction, r.EntityVerticalReduction, r.DelayBeforeCollision));
        Assert.Equal([new(0f, 1f), new DistancePoint(90f, 0.2f)], r.WallHorizontalReduction);   // written reversed, read sorted
        Assert.Equal([new(0f, 1f), new DistancePoint(90f, 0.2f)], r.WallVerticalReduction);
        Assert.True(p.StopAtMaxBounce);
        Assert.Equal(1, frag.SpawnCount);
        Assert.Empty(p.ExtraSplashes);
        Assert.False(p.Splash!.Lingers);
        // Control: the same file with a raycast collision type has no rigid rules (M12 projectiles keep their bounce rule).
        var raycast = WeaponTuning.ReadThrown("ray", BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\frag", 2, 1, 1f, 10f)), notes, Blueprints(("ebps\\p\\frag", Frag("raycast"))))!;
        Assert.Null(raycast.Projectile.Rigid);
        Assert.False(raycast.Projectile.StopAtMaxBounce);
        Assert.Empty(notes);
    }

    [Fact]
    public void Every_damaging_blast_is_read_with_its_lifetime_and_blind_only_blasts_are_noted()
    {
        var notes = new List<string>();
        var bomb = BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(30f, 30f, 0f, 8f, 8f, 20f, [], [(0f, 0.75f), (8f, 1.8f)], detonateOnCollision: false,
            collisionType: "rigidbody", bounces: 1, groundLifetime: 1f, rigid: Rigid, stopAtMaxBounce: true, splashLingers: (10f, 0.2f),
            secondBlast: (6f, 10f, 65f, [(4f, 1f), (5f, 0.5f), (6f, 0.05f)])));
        var melta = WeaponTuning.ReadThrown("bomb", BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\bomb", 1, -1, 0.5f, 10f, visible: false)), notes, Blueprints(("ebps\\p\\bomb", bomb)))!;
        var first = melta.Projectile.Splash!;
        Assert.Equal((8f, 8f, 20f, 10f, 0.2f, true), (first.Radius, first.Damage.Damage, first.Damage.ShieldDamage, first.Lifetime, first.Period, first.Lingers));
        var second = Assert.Single(melta.Projectile.ExtraSplashes);
        Assert.Equal((6f, 10f, 65f, false), (second.Radius, second.Damage.Damage, second.Damage.ShieldDamage, second.Lingers));
        Assert.Equal([new(4f, 1f), new(5f, 0.5f), new DistancePoint(6f, 0.05f)], second.FallOff);
        Assert.Equal(2, melta.Projectile.AllSplashes.Count());
        Assert.Equal(1, melta.SpawnCount);   // initial -1: full
        Assert.DoesNotContain(notes, n => n.Contains("damaging blasts"));

        var krak = BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(47.5f, 7f, 0f, 30f, 10f, 0f, [(8f, 1f), (8.05f, 0.5f), (30f, 0.05f)], [(5f, 0f), (10f, 1f)],
            detonateOnCollision: false, collisionType: "rigidbody", bounces: 1, groundLifetime: 1f, rigid: Rigid, stopAtMaxBounce: true, blindness: 3.5f));
        var k = WeaponTuning.ReadThrown("krak", BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\krak", 2, 1, 1f, 10f)), notes, Blueprints(("ebps\\p\\krak", krak)))!;
        Assert.Equal((30f, 10f), (k.Projectile.Splash!.Radius, k.Projectile.Splash.Damage.Damage));
        // M28: the blindness-only blast is kept after the damaging one, as a splash of no damage that blinds for 3.5 s.
        var blind = Assert.Single(k.Projectile.ExtraSplashes);
        Assert.Equal((0f, 3.5f), (blind.Damage.Damage, blind.Blindness));
        Assert.Contains(notes, n => n.Contains("krak") && n.Contains("blinds"));
    }

    static readonly BehaviorGraph TossGraph = new(MpClasses.TossGraph, [],
    [
        new BehaviorSequencer("SequencerGenerator00", ["Toss"],
        [
            new BehaviorTag("Branch", 0.4878049f, 1f, MpClasses.EndBranch, null),
            new BehaviorTag("Event", 0.1463415f, 0.1463415f, MpClasses.ReleaseEvent, null),
            new BehaviorTag("Branch", 0.2682927f, 1f, "BRANCH_Evade", null),
        ], [1f]),
    ]);

    [Fact]
    public void Throw_timing_comes_from_the_toss_graph()
    {
        var notes = new List<string>();
        var t = MpClasses.ReadThrowTiming([TossGraph], _ => 41f / 30f, notes)!;
        Assert.Equal(0.2f, t.Release, 4);
        Assert.Equal(20f / 30f, t.Duration, 4);
        Assert.Equal("Toss", t.Animation);
        Assert.Empty(notes);
        // Playback speed 2 halves both.
        var fast = MpClasses.ReadThrowTiming([TossGraph with { Sequencers = [TossGraph.Sequencers[0] with { PlaybackSpeeds = [2f] }] }], _ => 41f / 30f, notes)!;
        Assert.Equal(0.1f, fast.Release, 4);
        // Controls: no graph, no release event.
        Assert.Null(MpClasses.ReadThrowTiming([], _ => 1f, notes));
        Assert.Null(MpClasses.ReadThrowTiming([TossGraph with { Sequencers = [TossGraph.Sequencers[0] with { Tags = [] }] }], _ => 1f, notes));
        Assert.Equal(2, notes.Count(n => n.Contains("placeholder")));
    }

    [Fact]
    public void Classes_offer_visible_thrown_equipment_in_data_order_and_the_melta()
    {
        var classes = new[]
        {
            new MpClass("sm - tactical", "tactical", "space_marine", [],
                ["sm_mp_eq_frag_grenade", "sm_mp_eq_krak_grenade", "sm_mp_eq_stimpack", "sm_mp_eq_melta_bomb", "sm_mp_ranged_melta"], [], null),
            new MpClass("sm - devastator", "heavy", "space_marine", [], ["sm_mp_eq_krak_grenade", "sm_mp_eq_frag_grenade"], [], null),
        };
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_eq_frag_grenade"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\frag", 2, 1, 1f, 10f)),
            ["sm_mp_eq_krak_grenade"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\krak", 2, 1, 1f, 10f)),
            ["sm_mp_eq_stimpack"] = BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "equipment") }))),
            ["sm_mp_eq_melta_bomb"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\bomb", 1, -1, 0.5f, 10f, visible: false)),
            ["sm_mp_ranged_melta"] = Melta(),
        };
        var blueprints = Blueprints(("ebps\\p\\frag", Frag()), ("ebps\\p\\krak", Frag()), ("ebps\\p\\bomb", Frag()));
        var notes = new List<string>();
        var result = MpClasses.Build(classes, wargear, [TossGraph], _ => 41f / 30f, notes, blueprints);
        Assert.Equal(["sm_mp_eq_frag_grenade", "sm_mp_eq_krak_grenade"], result.Thrown.Select(t => t.Name));
        Assert.Equal([0, 1], result.Loadouts[0].EquipmentChoices);
        Assert.Equal([1, 0], result.Loadouts[1].EquipmentChoices);   // the class's own order; each item read once
        Assert.Contains(notes, n => n.Contains("stimpack") && n.Contains("not simulated"));
        Assert.Contains(notes, n => n.Contains("melta_bomb") && n.Contains("not offered"));
        Assert.Equal(["sm_mp_ranged_melta"], result.Loadouts[0].PrimaryChoices.Select(i => result.Weapons[i].Name));
        Assert.Equal(0.2f, result.Throw!.Release, 4);
        var config = GameplayTuning.WithClasses(new SimConfig(), result.Weapons, result.Loadouts, result.Thrown);
        Assert.NotNull(config);
        Assert.Equal(2, config!.Thrown.Count);

        var all = MpClasses.Build(classes, wargear, [], _ => 1f, [], blueprints, offerHiddenEquipment: true);
        Assert.Equal(["sm_mp_eq_frag_grenade", "sm_mp_eq_krak_grenade", "sm_mp_eq_melta_bomb"], all.Thrown.Select(t => t.Name));
        Assert.Null(all.Throw);
    }

    [Fact]
    public void Config_validation_covers_cones_recharge_rigid_bodies_and_thrown_items()
    {
        var notes = new List<string>();
        var melta = WeaponTuning.Read("melta", Melta(), notes)!;
        var frag = WeaponTuning.ReadThrown("frag", BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\p\\frag", 2, 1, 1f, 10f)), notes, Blueprints(("ebps\\p\\frag", Frag())))!;
        ClassLoadout[] classes = [new("c", 0, -1, []) { EquipmentChoices = [0] }];
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [melta], classes, [frag]));   // control
        WeaponSpec[] badWeapons =
        [
            melta with { Cone = null }, melta with { Cone = melta.Cone! with { Angle = 0f } }, melta with { Cone = melta.Cone! with { Length = float.NaN } },
            melta with { Cone = melta.Cone! with { FallOff = [new(10f, 1f), new(5f, 1f)] } }, melta with { Recharge = new RechargeSpec(0.5f, 0f) },
            melta with { ShotCooldown = 0f }, melta with { Projectile = frag.Projectile },
        ];
        foreach (var w in badWeapons) Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [w], classes, [frag]));
        var rigid = frag.Projectile.Rigid!;
        ThrownSpec[] badThrown =
        [
            frag with { Count = 0 }, frag with { Count = 256 }, frag with { Cooldown = float.NaN },
            frag with { Projectile = frag.Projectile with { Rigid = rigid with { GroundVerticalReduction = 1.5f } } },
            frag with { Projectile = frag.Projectile with { ExtraSplashes = [frag.Projectile.Splash! with { Period = -1f }] } },
        ];
        foreach (var t in badThrown) Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [melta], classes, [t]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [melta], [classes[0] with { EquipmentChoices = [1] }], [frag]));   // no item 1
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [melta], classes));                                              // no items at all
    }

    [Fact]
    public void A_shapecast_charge_stage_is_dropped_and_the_weapon_stays_usable()
    {
        var notes = new List<string>();
        var gun = WeaponTuning.Read("conecharge", BafDocument.Read(SyntheticBafWriter.RangedWeapon(30f, 0.267f, 125, 0, 1f, 250f, [], 1f, 1f, 1f, 1f,
            charge: new ChargeFixture(1.1f, 0f, 0f, 15, 250f, 0.1f, "_base_templates\\ranged_delivery_types\\shapecast", null, 200f))), notes)!;
        Assert.Equal(DeliveryKind.Raycast, gun.Delivery);
        Assert.Null(gun.Charge);   // its only firing stage was a cone: no charge
        Assert.Contains(notes, n => n.Contains("conecharge") && n.Contains("shapecast") && n.Contains("not supported"));
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [gun], [new ClassLoadout("c", 0, -1, [])]));   // the classes stay
    }

    static byte[] HashExtras(WeaponSpec w)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) w.WriteHashExtras(writer);
        return stream.ToArray();
    }

    [Fact]
    public void Hash_extras_cover_the_cone_and_the_recharge()
    {
        var melta = WeaponTuning.Read("melta", Melta(), [])!;
        Assert.Equal(HashExtras(melta), HashExtras(WeaponTuning.Read("melta", Melta(), [])!));   // control
        var c = melta.Cone!;
        foreach (var v in new[] { melta with { Cone = c with { Angle = 11f } }, melta with { Cone = c with { StartLength = 19f } },
                     melta with { Cone = c with { CoverageFallOff = [new(0.5f, 1f)] } }, melta with { Cone = c with { Damage = c.Damage with { ShieldDamage = 26f } } },
                     melta with { Recharge = null }, melta with { Recharge = new RechargeSpec(0.5f, 1f) } })
            Assert.NotEqual(HashExtras(melta), HashExtras(v));
    }
}
