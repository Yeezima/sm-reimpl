using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Bod;
using SmFormats.Fx;
using SmFormats.Obpk;
using SmSim;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>
/// The weapon effect catalogue (M25a): play_fx and material-effect bindings in wargear and blueprints, the per-shot summary,
/// material effect containers with their fall-backs and Space Marine / Chaos duplicates, surfaces, the player's surface,
/// map fx objects and the effects library. Synthetic files shaped like the install's (names and values invented).
/// </summary>
public class EffectCatalogueTests
{
    static (string, object)[] PlayFx(string name, float scale, string marker = "", bool attached = false, string target = "", string direction = "negative_hit_direction") =>
    [
        ("$ref", "action_commands\\play_fx"), ("fx_package", "test_fx"), ("fx_name", name), ("scale", scale), ("length", scale), ("attach_to_marker", attached),
        ("trigger_point", "self"), ("marker", new (string, object)[] { ("marker", marker) }), ("trigger_point_target", target),
        ("marker_target", Array.Empty<(string, object)>()), ("rotation_source", "trigger_point"), ("trigger_direction", direction), ("scale_if_from_player", false),
        ("on_stop_policy", "stop_emitting"), ("lifetime", 0f), ("apply_only_if_on_player", false),
    ];

    static (string, object)[] Material(string container) =>
        [("$ref", "action_commands\\trigger_material_effect"), ("effect_container", container), ("material_source", "hit_material")];

    static (string, object)[] Upgrade(params string[] names) =>
        [("$ref", "_base_templates\\required_upgrade_name"), ("required_list", names.Select(n => ("upgrade_name", (object)n)).ToArray()), ("allowed_difficulty", Array.Empty<(string, object)>())];

    static (string, object)[] Filter(((string, object)[] Actions, (string, object)[][] Default, (string, object)[][] Upgraded) f) =>
    [
        ("$ref", "wargear\\fx_action_filter"), ("actions", f.Actions),
        ("actions_by_upgrade", f.Upgraded.Select(u => ("entry", (object)new (string, object)[] { ("required_upgrade_name", Upgrade("test_perk")), ("action", new (string, object)[] { ("commands", u) }) }))
            .Concat(f.Default.Select(d => ("entry", (object)new (string, object)[] { ("required_upgrade_name", Upgrade()), ("action", new (string, object)[] { ("commands", d) }) }))).ToArray()),
    ];

    const string SmContainer = "material_effect_container\\weapon_hits\\space_marines\\ranged_weapons\\test_rifle";
    const string ChaosContainer = "material_effect_container\\weapon_hits\\chaos\\ranged_weapons\\test_rifle";

    /// <summary>
    /// A bolter-shaped weapon: fire_fx with a shell in its actions and the muzzle flash in a default upgrade entry (an upgraded
    /// one first); hit_fx with the tracer and the hit container; stop_fire_fx smoke; a charged secondary whose stage fires a
    /// projectile and has its own tracer; a heat warning light.
    /// </summary>
    static byte[] Rifle(string container = SmContainer) => SyntheticBafWriter.Build(
        ("common_data", new (string, object)[] { ("slot", "ranged"), ("heat", new (string, object)[] { ("high_heat", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_led", 0.5f, "fx_warning")) }) }) }) }),
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[]
            {
                ("$ref", "wargear\\ability_components\\delivery\\ranged_delivery"),
                ("fire_fx", Filter(([("play_fx", PlayFx("shell_casing", 1.3f, "fx_shells_eject", attached: true))],
                    [[("play_fx", PlayFx("test_muzzle", 0.8f, "fx_muzzle"))]], [[("play_fx", PlayFx("test_perk_muzzle", 0.4f, "fx_muzzle"))]]))),
                ("hit_fx", Filter(([], [[("play_fx", PlayFx("test_tracer", 1.5f, "fx_muzzle", attached: true, target: "hitpoint")), ("trigger_material_effect", Material(container))]],
                    [[("play_fx", PlayFx("test_perk_trail", 1f, "fx_muzzle", attached: true, target: "hitpoint")), ("trigger_material_effect", Material(container + "_perk"))]]))),
                ("stop_fire_fx", Filter(([("play_fx", PlayFx("test_smoke", 1f, "fx_muzzle", attached: true))], [], []))),
                ("max_range", 250f), ("muzzle_marker", "fx_muzzle"),
            }) }),
            ("ability_ranged_secondary", new (string, object)[] { ("charge_states", new (string, object)[]
            {
                ("1.charge_light", new (string, object)[] { ("charging_action", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_charging", 1f, "fx_muzzle", attached: true)) }) }) }),
                ("4.charge_heavy", new (string, object)[] { ("delivery", new (string, object)[]
                {
                    ("fire_fx", Filter(([("play_fx", PlayFx("test_heavy_muzzle", 2f, "fx_muzzle"))], [], []))),
                    ("ranged_delivery_type", new (string, object)[] { ("entry", new (string, object)[] { ("required_upgrade_name", Upgrade()), ("type", new (string, object)[] { ("ebp", "ebps\\projectiles\\test_bolt") }) }) }),
                }) }),
            }) }),
        }) }));

    static byte[] Bolt() => SyntheticBafWriter.Build(("extensions", new (string, object)[]
    {
        ("entity_actions_ext", new (string, object)[] { ("lifetime_action", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_trail", 2f, "fx_trail", attached: true)) }) }) }),
        ("projectile_ext", new (string, object)[] { ("detonate_action", new (string, object)[] { ("commands", new (string, object)[]
        {
            ("play_fx", PlayFx("test_blast", 0.6f, direction: "up")), ("trigger_material_effect", Material("material_effect_container\\weapon_hits\\space_marines\\ranged_weapons\\test_blast")),
        }) }) }),
    }));

    /// <summary>A container: default/default stone hit, metal/default, broken_ceramite head only (body falls back), an empty stone class.</summary>
    static byte[] Container(string prefix = "") => SyntheticBafWriter.Build(
        ("sfx", new (string, object)[] { ("$ref", "action_commands\\post_sound_event"), ("start_event_name", "play_test_hits"), ("trigger_point", "hitpoint") }),
        ("fx", new (string, object)[]
        {
            ("default", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx(prefix + "test_dmg_stone", 0.3f, direction: "hitpoint_normal")) }) }),
            ("stone", new (string, object)[] { ("default", Array.Empty<(string, object)>()) }),
            ("metal", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx(prefix + "test_dmg_metal", 0.3f)), ("play_fx", PlayFx(prefix + "test_sparks", 0.5f)) }) }),
            ("broken_ceramite", new (string, object)[]
            {
                ("default_head", new (string, object)[] { ("play_fx", PlayFx(prefix + "test_dmg_head", 0.4f)) }),
                ("default", new (string, object)[] { ("play_fx", PlayFx(prefix + "test_dmg_body", 0.3f)) }),
            }),
        }));

    static byte[] Global(params string[] surfaces) => SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("fx_environment_manager_ext", new (string, object)[] { ("collision_fx",
        new (string, object)[] { ("collision_small", new (string, object)[] { ("material_table", new (string, object)[] { ("fx", new (string, object)[] { ("default",
            surfaces.Select(s => (s, (object)Array.Empty<(string, object)>())).ToArray()) }) }) }) }) }) }));

    static byte[] Player() => SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("entity_ext", new (string, object)[]
    {
        ("material_audio_visual", "broken_ceramite"), ("material_simulation", "default_body"),
        ("material_overrides", new (string, object)[]
        {
            ("entry", new (string, object)[] { ("hit_zone", "body"), ("material_simulation", "default_body"), ("material_audio_visual", "broken_ceramite") }),
            ("entry", new (string, object)[] { ("hit_zone", "head"), ("material_simulation", "default_head"), ("material_audio_visual", "broken_ceramite") }),
        }),
    }) }));

    static EffectCatalogue Catalogue(List<string>? notes = null) => EffectCatalogue.Build(
        new Dictionary<string, BafDocument> { ["test_rifle"] = BafDocument.Read(Rifle()), ["csm_test_rifle"] = BafDocument.Read(Rifle(ChaosContainer)) },
        path => path.EndsWith("test_bolt") ? BafDocument.Read(Bolt()) : null,
        new Dictionary<string, IReadOnlyList<BafDocument>>
        {
            ["test_rifle"] = [BafDocument.Read(Container()), BafDocument.Read(Container("chaos_"))],
            ["test_blast"] = [BafDocument.Read(Container())],
        },
        BafDocument.Read(Global([.. Surfaces.Default])), BafDocument.Read(Player()), notes);

    [Fact]
    public void Bindings_carry_their_path_event_ability_stage_and_upgrades()
    {
        var bindings = FxBinding.Collect(BafDocument.Read(Rifle()).Root);
        Assert.Equal(["test_led", "shell_casing", "test_perk_muzzle", "test_muzzle", "test_perk_trail", "", "test_tracer", "", "test_smoke", "test_charging", "test_heavy_muzzle"],
            bindings.Select(b => b.Play?.Name ?? ""));
        var led = bindings[0];
        Assert.Equal(("high_heat", "", true, false), (led.Event, led.Ability, led.IsDefault, led.InDelivery));
        var perk = bindings[2];
        Assert.Equal(("fire_fx", "ability_ranged_primary", true, false), (perk.Event, perk.Ability, perk.InDelivery, perk.IsDefault));
        Assert.Equal(["test_perk"], perk.RequiredUpgrades);
        var tracer = bindings[6];
        Assert.True(tracer.IsDefault);
        Assert.Equal(("test_tracer", 1.5f, true, "fx_muzzle", "hitpoint", "negative_hit_direction", "test_fx"),
            (tracer.Play!.Name, tracer.Play.Scale, tracer.Play.Attached, tracer.Play.Marker, tracer.Play.TargetPoint, tracer.Play.Direction, tracer.Play.Package));
        Assert.Equal(("hit_fx", SmContainer, "test_rifle", "hit_material"), (bindings[7].Event, bindings[7].Material!.Container, bindings[7].Material!.ContainerName, bindings[7].Material!.MaterialSource));
        Assert.Equal(("charging_action", "1.charge_light", false), (bindings[9].Event, bindings[9].ChargeStage, bindings[9].InDelivery));
        Assert.Equal(("fire_fx", "ability_ranged_secondary", "4.charge_heavy"), (bindings[10].Event, bindings[10].Ability, bindings[10].ChargeStage));
        Assert.Equal("ability_data/ability_definitions/ability_ranged_primary/delivery/hit_fx/actions_by_upgrade/entry/action/commands/play_fx", tracer.Path);
    }

    [Fact]
    public void A_weapon_summary_names_muzzle_shell_tracer_smoke_hits_and_projectiles()
    {
        var notes = new List<string>();
        var c = Catalogue(notes);
        var w = c.Weapon("TEST_RIFLE")!;   // names match ignoring case
        Assert.Equal(("ability_ranged_primary", "fx_muzzle", 250f), (w.BasicAbility, w.MuzzleMarker, w.MaxRange));
        Assert.Equal(("test_muzzle", 0.8f), (w.Muzzle!.Name, w.Muzzle.Scale));                // the default entry, not the perk's
        Assert.Equal(("shell_casing", "fx_shells_eject"), (w.ShellEject!.Name, w.ShellEject.Marker));
        Assert.Equal("test_tracer", w.Tracer!.Name);
        Assert.Equal(["test_smoke"], w.StopFire.Select(p => p.Name));
        Assert.Equal(SmContainer, w.HitMaterial!.Container);
        Assert.Null(w.Projectile);                                                             // the basic shot is hit-scan
        var charged = w.Shot("ability_ranged_secondary", "4.charge_heavy");
        Assert.Equal("test_heavy_muzzle", charged.Muzzle!.Name);
        Assert.Null(charged.Tracer);
        var bolt = Assert.Single(w.Projectiles);
        Assert.Equal("ebps\\projectiles\\test_bolt", bolt.Blueprint);
        Assert.Equal(["test_trail"], bolt.Trail.Select(p => p.Name));
        Assert.Equal(["test_blast"], bolt.Detonation.Select(p => p.Name));
        Assert.Equal("test_blast", bolt.DetonationMaterial!.ContainerName);

        // Impacts by surface (world hits: damage class "default") and by zone for players, with fall-backs.
        Assert.Equal(["test_dmg_stone"], c.Impact("test_rifle", ShotHit.World, 0).Select(p => p.Name));     // default
        Assert.Equal(["test_dmg_stone"], c.Impact("test_rifle", ShotHit.World, 3).Select(p => p.Name));     // stone/default is empty: default/default
        Assert.Equal(["test_dmg_metal", "test_sparks"], c.Impact("test_rifle", ShotHit.World, 4).Select(p => p.Name));
        Assert.Equal(["test_dmg_stone"], c.Impact("test_rifle", ShotHit.World, -1).Select(p => p.Name));    // unknown id: default
        Assert.Equal(["test_dmg_body"], c.Impact("test_rifle", ShotHit.Body, -1).Select(p => p.Name));      // default_body: broken_ceramite/default
        Assert.Equal(["test_dmg_head"], c.Impact("test_rifle", ShotHit.Head, -1).Select(p => p.Name));
        Assert.Empty(c.Impact("test_rifle", ShotHit.None, 0));
        Assert.Empty(c.Impact("no_such_weapon", ShotHit.World, 0));
        Assert.Equal("play_test_hits", c.Container(w.HitMaterial)!.SoundEvent);
        Assert.Equal(["test_dmg_stone"], c.Resolve(bolt.DetonationMaterial, ShotHit.World, 2).Select(p => p.Name));

        // The Chaos weapon's container path picks the Chaos-named duplicate.
        Assert.Equal(["chaos_test_dmg_metal", "chaos_test_sparks"], c.Impact("csm_test_rifle", ShotHit.World, 4).Select(p => p.Name));
        Assert.Contains(notes, n => n.Contains("2 files named test_rifle"));
        Assert.Contains(notes, n => n.Contains("test_rifle_perk") && n.Contains("not found"));
        Assert.Equal(Surfaces.Default, c.SurfaceOrder);
        Assert.Equal(("metal", "air", "default", "default"), (c.SurfaceName(4), c.SurfaceName(7), c.SurfaceName(12), c.SurfaceName(-1)));
    }

    [Fact]
    public void Containers_fall_back_and_duplicates_are_chosen_by_their_folder()
    {
        var t = MaterialEffectTable.Read("x", BafDocument.Read(Container()));
        Assert.Equal(["default", "stone", "metal", "broken_ceramite"], t.Effects.Keys);
        Assert.Equal(["test_dmg_head"], t.Resolve("broken_ceramite", "default_head").Select(p => p.Name));
        Assert.Equal(["test_dmg_body"], t.Resolve("broken_ceramite", "tough_body").Select(p => p.Name));   // (surface, default)
        Assert.Equal(["test_dmg_stone"], t.Resolve("sand", "default_head").Select(p => p.Name));           // (default, default)
        var empty = MaterialEffectTable.Read("e", BafDocument.Read(SyntheticBafWriter.Build(("sfx", Array.Empty<(string, object)>()))));
        Assert.Empty(empty.Resolve("default", "default"));
        Assert.Equal("", empty.SoundEvent);

        MaterialEffectTable[] pair = [t, MaterialEffectTable.Read("x", BafDocument.Read(Container("csm_")))];
        Assert.Equal(1, EffectCatalogue.ChooseContainer(ChaosContainer, pair));
        Assert.Equal(0, EffectCatalogue.ChooseContainer(SmContainer, pair));
        Assert.Equal(1, EffectCatalogue.ChooseContainer(SmContainer, [pair[1], pair[0]]));
        Assert.Equal(0, EffectCatalogue.ChooseContainer(ChaosContainer, [t, t]));   // a tie: the first
        Assert.Equal(0, EffectCatalogue.ChooseContainer(ChaosContainer, [t]));
    }

    [Fact]
    public void Surfaces_and_the_player_surface_come_from_the_data()
    {
        Assert.Equal(["default", "dirt", "stone"], Surfaces.ReadOrder(BafDocument.Read(Global("default", "dirt", "stone"))));
        Assert.Null(Surfaces.ReadOrder(BafDocument.Read(SyntheticBafWriter.Build(("extensions", Array.Empty<(string, object)>())))));
        Assert.Equal("stone", Surfaces.NameOf(2, ["default", "dirt", "stone"]));
        Assert.Equal(("default", "metal"), (Surfaces.NameOf(0), Surfaces.NameOf(4)));
        var notes = new List<string>();
        var c = EffectCatalogue.Build(new Dictionary<string, BafDocument>(), null, new Dictionary<string, IReadOnlyList<BafDocument>>(),
            BafDocument.Read(Global("default", "metal")), null, notes);
        Assert.Equal(["default", "metal"], c.SurfaceOrder);
        Assert.Equal("metal", c.SurfaceName(1));
        Assert.Contains(notes, n => n.Contains("differs"));
        Assert.Equal(CharacterSurface.Unknown, c.Player);

        var player = CharacterSurface.Read(BafDocument.Read(Player()))!;
        Assert.Equal(("broken_ceramite", "default_head"), player.For(HitZone.Head));
        Assert.Equal(("broken_ceramite", "default_body"), player.For(HitZone.Body));
        Assert.Null(CharacterSurface.Read(BafDocument.Read(SyntheticBafWriter.Build(("extensions", Array.Empty<(string, object)>())))));
    }

    [Fact]
    public void An_armoured_hit_reads_the_armour_surface_and_the_blueprint_names_the_victim_effects()
    {
        var blueprint = SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("entity_ext", new (string, object)[] { ("material_audio_visual", "broken_ceramite"), ("material_simulation", "default_body") }),
            ("health_ext", new (string, object)[] { ("armourshield_info", new (string, object)[] { ("armourshield_info", new (string, object)[]
            {
                ("armourshield_material", "ceramite_armour"),
                ("armourshield_hit", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_armour_hit", 1f)) }) }),
                ("armourshield_inactive_hit", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_armour_down_hit", 0.5f)) }) }),
            }) }) }),
        }));
        var player = CharacterSurface.Read(BafDocument.Read(blueprint))!;
        Assert.Equal("ceramite_armour", player.ArmourSurface);
        Assert.Equal(["test_armour_hit"], player.ArmourHit.Select(p => p.Name));
        Assert.Equal(["test_armour_down_hit"], player.ArmourInactiveHit.Select(p => p.Name));
        // Armour up: the armour surface; down or unknown: the entity's own (the choice before M38).
        Assert.Equal(("ceramite_armour", "default_body"), player.For(HitZone.Body, armourUp: true));
        Assert.Equal(("broken_ceramite", "default_body"), player.For(HitZone.Body, armourUp: false));
        Assert.Equal(("broken_ceramite", "default_body"), player.For(HitZone.Body));
        // A blueprint without the block keeps the documented defaults.
        var plain = CharacterSurface.Read(BafDocument.Read(Player()))!;
        Assert.Equal("ceramite_armour", plain.ArmourSurface);
        Assert.Empty(plain.ArmourHit);
    }

    [Fact]
    public void Resolve_picks_the_armour_or_the_flesh_entry_of_a_player_hit_and_unknown_is_the_old_choice()
    {
        // Container(): its "broken_ceramite" entry is test_dmg_body etc. (see Container); add the armour entry by building a container with both surfaces.
        var surfaces = Surfaces.Default;
        var fx = new (string, object)[]
        {
            ("default", new (string, object)[] { ("default", Array.Empty<(string, object)>()) }),
            ("ceramite_armour", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx("test_armour", 1f)) }) }),
            ("broken_ceramite", new (string, object)[] { ("default", new (string, object)[] { ("play_fx", PlayFx("test_flesh", 1f)) }) }),
        };
        var table = MaterialEffectTable.Read("c", BafDocument.Read(SyntheticBafWriter.Build(("fx", fx))));
        var c = EffectCatalogue.Build(new Dictionary<string, BafDocument>(), null, new Dictionary<string, IReadOnlyList<BafDocument>>(),
            BafDocument.Read(Global([.. surfaces])), BafDocument.Read(Player()));
        var player = c.Player;
        Assert.Equal(["test_armour"], table.Resolve(player.For(HitZone.Body, armourUp: true).Surface, player.For(HitZone.Body, armourUp: true).DamageClass).Select(p => p.Name));
        Assert.Equal(["test_flesh"], table.Resolve(player.For(HitZone.Body, armourUp: false).Surface, player.For(HitZone.Body, armourUp: false).DamageClass).Select(p => p.Name));
        Assert.Equal(["test_flesh"], table.Resolve(player.For(HitZone.Head).Surface, player.For(HitZone.Head).DamageClass).Select(p => p.Name));   // unknown: before M38
    }

    [Fact]
    public void The_flash_follows_falls_of_a_living_players_shield_and_health_only()
    {
        Assert.Equal((11f, 0f), HitFeedbackRules.FlashDamage(true, 100f, 100f, true, 89f, 100f));      // armour
        Assert.Equal((0f, 35f), HitFeedbackRules.FlashDamage(true, 0f, 100f, true, 0f, 65f));          // health
        Assert.Equal((24f, 5f), HitFeedbackRules.FlashDamage(true, 24f, 100f, true, 0f, 95f));         // both
        Assert.Equal((0f, 0f), HitFeedbackRules.FlashDamage(true, 50f, 100f, true, 80f, 100f));        // regeneration
        Assert.Equal((0f, 0f), HitFeedbackRules.FlashDamage(false, 0f, 0f, true, 100f, 100f));         // respawn
        Assert.Equal((0f, 0f), HitFeedbackRules.FlashDamage(true, 100f, 40f, false, 100f, 0f));        // death: the vignette instead
        Assert.Equal((0f, 0f), HitFeedbackRules.FlashDamage(true, 100f, 100f, true, 99.995f, 100f));   // quantisation noise
    }

    [Fact]
    public void Armour_up_is_known_only_for_the_own_player_the_test_aid_and_the_protocol_bit()
    {
        const ShotHit body = ShotHit.Body;
        Assert.Null(HitFeedbackRules.ArmourUp(ShotHit.World, null, false, 100f, true));          // not a player hit
        Assert.Null(HitFeedbackRules.ArmourUp(ShotHit.None, true, true, 100f, true));
        Assert.Null(HitFeedbackRules.ArmourUp(body, null, false, 100f, null));                   // someone else, no bit: unknown (no guess)
        Assert.True(HitFeedbackRules.ArmourUp(body, null, false, 0f, true));                     // the protocol bit
        Assert.False(HitFeedbackRules.ArmourUp(ShotHit.Head, null, false, 100f, false));
        Assert.True(HitFeedbackRules.ArmourUp(body, null, true, 40f, null));                     // own shield up
        Assert.False(HitFeedbackRules.ArmourUp(body, null, true, 0f, true));                     // own shield down beats a bit
        Assert.False(HitFeedbackRules.ArmourUp(body, false, true, 100f, true));                  // the test aid beats everything
        Assert.True(HitFeedbackRules.ArmourUp(body, true, false, 0f, null));
    }

    [Fact]
    public void Map_fx_objects_are_read_with_their_transforms()
    {
        static Struct Fx(string name, float[] at, float[] rotation, bool disabled = false, params Node[] children) => new("base/fxobject",
            ("FXName", new S(name)), ("FXPackage", new S("test_fx")), ("FXLength", new F(2f)), ("FXScale", new F(3f)), ("FXTargetMarker", new S("")),
            ("FXAudioEventName", new S("")), ("FXAutoTrigger", new B(true)), ("FXPreComputeTime", new F(0.5f)),
            ("Position", new Vec(at)), ("Rotation", new Vec(rotation)), ("Scale", new Vec(1f, 1f, 1f)), ("Name", new S("n_" + name)), ("Disabled", new B(disabled)),
            ("ChildObjects", new List(children)));
        var group = new Struct("WorldGroup", ("Name", new S("g")), ("Position", new Vec(100f, 0f, 0f)), ("Rotation", new Vec(0f, 0f, 90f)), ("Scale", new Vec(1f, 1f, 1f)),
            ("ChildObjects", new List(Fx("test_ray", [1f, 0f, 0f], [30f, 0f, -45f]))));
        var layer = new Struct("RegionLayer", ("Root", new Ref(new Struct("WorldGroup", ("Objects", new List(
            Fx("test_steam", [5f, 6f, 7f], [90f, 0f, 0f]), group, Fx("test_off", [0f, 0f, 0f], [0f, 0f, 0f], disabled: true, Fx("test_hidden", [1f, 1f, 1f], [0f, 0f, 0f]))))))));
        var objects = MapEffects.Read(BodDocument.Read(Document(layer)), "l_lights");
        Assert.Equal(["test_steam", "test_ray"], objects.Select(o => o.Fx));   // the disabled object hides its child too
        var steam = objects[0];
        Assert.Equal(("n_test_steam", "l_lights", "test_fx", 2f, 3f, true, 0.5f), (steam.Name, steam.Layer, steam.Package, steam.Length, steam.Scale, steam.AutoTrigger, steam.PreComputeTime));
        Assert.Equal(new Vector3(5f, 6f, 7f), steam.Position);
        // A child of a group turned 90 degrees about Z at x = 100: local (1, 0, 0) lands at (100, 1, 0).
        var ray = objects[1];
        Assert.True(Vector3.Distance(new Vector3(100f, 1f, 0f), ray.Position) < 1e-4f, ray.Position.ToString());
        Assert.Equal(new Vector3(30f, 0f, -45f), ray.RotationDegrees);
        Assert.Equal(ray.Position, ray.Transform.Translation);
    }

    [Fact]
    public void The_effects_library_reads_systems_on_demand_and_names_the_atlas()
    {
        string dir = Directory.CreateTempSubdirectory("sm-fx-").FullName;
        try
        {
            string path = Path.Combine(dir, "test_fx.oppc");
            var page = SyntheticFxWriter.TexturePage((Hash("test_streak_00"), 0f, 0f, 0.5f, 0.5f, 1), (Hash("test_smoke"), 0.5f, 0f, 0.5f, 0.5f, 0));
            File.WriteAllBytes(path, SyntheticObpkWriter.Build([
                new("PSystem", "test_tracer", SyntheticFxWriter.TracerSystem()), new("tpage", "test_fx", page), new("dds", "test_fx", [1, 2, 3]), new("dds", "test_decal_dif", [4, 5])]));
            using var fx = FxLibrary.Open(path);
            Assert.Equal(["test_tracer"], fx.SystemNames);
            Assert.Equal(("test_fx", 2), (fx.Page.AtlasTexture, fx.Page.Rects.Count));
            Assert.Equal(["test_streak_00", "test_smoke"], fx.Page.Rects.Select(r => r.Name));   // named from the systems' strings
            var tracer = fx.System("test_tracer")!;
            Assert.Same(tracer, fx.System("test_tracer"));
            Assert.All(tracer.Materials, m => Assert.NotNull(fx.Page.Find(m)));
            Assert.Null(fx.System("missing"));
            Assert.Equal([1, 2, 3], fx.Texture(fx.Page.AtlasTexture));
            Assert.Equal([4, 5], fx.Texture("TEST_DECAL_DIF"));
            Assert.Null(fx.Texture("missing"));
            File.WriteAllBytes(Path.Combine(dir, "bare.oppc"), SyntheticObpkWriter.Build([new("PSystem", "test_tracer", SyntheticFxWriter.TracerSystem())]));
            Assert.Throws<InvalidDataException>(() => FxLibrary.Open(Path.Combine(dir, "bare.oppc")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_effects_library_reads_several_entries_in_one_pass_and_is_safe_to_share_between_threads()
    {
        string dir = Directory.CreateTempSubdirectory("sm-fx-").FullName;
        try
        {
            string path = Path.Combine(dir, "test_fx.oppc");
            var page = SyntheticFxWriter.TexturePage((Hash("test_streak_00"), 0f, 0f, 0.5f, 0.5f, 1), (Hash("test_smoke"), 0.5f, 0f, 0.5f, 0.5f, 0));
            File.WriteAllBytes(path, SyntheticObpkWriter.Build([
                new("PSystem", "test_tracer", SyntheticFxWriter.TracerSystem()), new("PSystem", "test_tracer_b", SyntheticFxWriter.TracerSystem()),
                new("tpage", "test_fx", page), new("dds", "test_fx", [1, 2, 3]), new("dds", "test_decal_dif", [4, 5]), new("dds", "test_decal_two", [6])]));
            using var fx = FxLibrary.Open(path);
            var failures = new List<string>();
            var systems = fx.Systems(["test_tracer_b", "test_tracer", "missing", "test_tracer"], failures);
            Assert.Equal(["test_tracer", "test_tracer_b"], systems.Keys.Order());
            Assert.Empty(failures);
            Assert.Same(systems["test_tracer"], fx.System("test_tracer"));   // parsed once, shared with the single read
            var textures = fx.Textures(["TEST_DECAL_TWO", "test_decal_dif", "missing", "test_decal_dif"]);
            Assert.Equal([4, 5], textures["test_decal_dif"]);
            Assert.Equal([6], textures["test_decal_two"]);
            Assert.Equal(2, textures.Count);
            Assert.Empty(fx.Systems([]));
            // Reads share one stream position: concurrent users (a background load and a main thread) must not corrupt each other.
            var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 64, i =>
            {
                if (i % 3 == 0) { if (fx.Texture("test_decal_dif") is not [4, 5]) bad.Add("texture " + i); }
                else if (i % 3 == 1) { if (fx.Textures(["test_fx", "test_decal_two"]).Count != 2) bad.Add("textures " + i); }
                else if (fx.Systems(["test_tracer", "test_tracer_b"]).Count != 2) bad.Add("systems " + i);
            });
            Assert.Empty(bad);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
