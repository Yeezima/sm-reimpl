using SmContent;
using SmFormats.Baf;
using SmSim.Extras.Musou;

namespace SmFormats.Tests;

/// <summary>
/// gore-original (Musou Mode, added content): the Ork's own blood read from synthetic files shaped like the install's (no original bytes), and the rules that pick a kill's point.
/// </summary>
public class OrkBloodTests
{
    static (string, object) Zone(string name, float chance, string point) =>
        (name, new (string, object)[] { ("$ref", "dismemberment\\dismemberment_instance"), ("chance", chance), ("dismemberment", point.Length > 0 ? "dismemberment\\dismemberment_point\\" + point : "") });

    static (string, object) Row(string type, float head, string headPoint, float body, string bodyPoint, string left) => (type, new (string, object)[]
    {
        ("hit_zone", new (string, object)[]
        {
            Zone("head", head, headPoint), Zone("body", body, bodyPoint), Zone("left_arm", body, bodyPoint), Zone("right_arm", body, bodyPoint),
            Zone("left_leg", body, bodyPoint), Zone("right_leg", body, bodyPoint), Zone("shield", 100f, ""),
        }),
        ("nothing_left", "dismemberment\\dismemberment_point\\" + left),
    });

    static BafDocument Blueprint() => BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
    {
        ("dismemberment_ext", new (string, object)[]
        {
            ("$ref", "entity_extensions\\dismemberment_ext"),
            ("dismemberment_by_type", new (string, object)[]
            {
                Row("bolter", 75f, "explode_head", 50f, "explode_left_side", "explode_body"),
                Row("chainsword", 0f, "", 0f, "", "smash_body"),
                Row("explosive_heavy", 100f, "explode_head", 100f, "explode_body", "explode_body"),
                Row("plasma_light", 25f, "plasma_head", 0f, "plasma_body", "plasma_body"),
            }),
        }),
    })));

    static (string, object) PlayFx(string name, float scale, string marker, string direction) => ("play_fx", new (string, object)[]
    {
        ("$ref", "action_commands\\play_fx"), ("fx_package", "master_fx"), ("fx_name", name), ("scale", scale), ("length", scale),
        ("trigger_point", "self"), ("marker", new (string, object)[] { ("marker", marker) }), ("trigger_direction", direction),
    });

    static BafDocument Point(params (string, object)[] commands) => BafDocument.Read(SyntheticBafWriter.Build(("actions", new (string, object)[]
    {
        ("action", new (string, object)[]
        {
            ("$ref", "action"),
            ("commands", new (string, object)[] { ("apply_gore", new (string, object)[] { ("$ref", "action_commands\\apply_gore"), ("amount", 0.25f) }) }.Concat(commands).ToArray()),
        }),
    })));

    static (string, object) Damage(string type) => ("do_damage", new (string, object)[] { ("$ref", "action_commands\\do_damage"), ("dismemberment_type", type) });

    static BafDocument Bolter() => BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[]
    {
        ("ability_definitions", new (string, object)[]
        {
            ("ability_ranged_primary", new (string, object)[]
            {
                ("delivery", new (string, object)[]
                {
                    ("penetration", new (string, object)[] { ("entry", new (string, object)[] { Damage("las_gun") }) }),   // a penetration's exit: not the shot's own
                    ("hit_sim", new (string, object)[] { ("actions_by_upgrade", new (string, object)[] { ("entry", new (string, object)[] { ("action", new (string, object)[] { Damage("bolter") }) }) }) }),
                }),
            }),
        }),
    })));

    static (string, object) Strike(string ability, string type) => (ability, new (string, object)[]
    {
        ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { Damage("none") }) }) }),
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { Damage(type) }) }) }),
    });

    static BafDocument Chainsword() => BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[]
    {
        ("ability_definitions", new (string, object)[]
        {
            Strike("ability_melee_x1", "chainsword"), Strike("ability_melee_x3", "chainsword_heavy"), Strike("ability_melee_y", "none"), Strike("ability_bullrush_01", "bullrush"),
        }),
    })));

    static OrkBlood Data() => OrkBlood.Build(
        new Dictionary<string, BafDocument> { ["ork_coop_boy"] = Blueprint() },
        new Dictionary<string, BafDocument>
        {
            ["explode_head"] = Point(PlayFx("body_dismemberment", 0.4f, "fx_gore_neck", "up"), PlayFx("body_dismemberment_drip", 0.8f, "fx_gore_neck", "negative_hit_direction")),
            ["explode_left_side"] = Point(PlayFx("body_dismemberment", 0.4f, "fx_gore_l_side", "up")),
            ["explode_body"] = Point(PlayFx("body_explode", 0.8f, "rig_world", "up"), PlayFx("body_dismemberment_limb", 0.5f, "rig_head", "negative_hit_direction")),
            ["smash_body"] = Point(PlayFx("body_explode", 1f, "rig_world", "up")),
            ["plasma_head"] = Point(PlayFx("plasma_dismemberment", 1f, "fx_gore_neck", "up")),
            ["plasma_body"] = Point(PlayFx("plasma_dismemberment", 2f, "rig_world", "up")),
        },
        new Dictionary<string, BafDocument> { ["sm_mp_ranged_bolter"] = Bolter(), ["sm_mp_melee_chainsword"] = Chainsword() });

    [Fact]
    public void The_table_is_read_per_type_and_zone_with_its_points()
    {
        var data = Data();
        Assert.Equal("ork_coop_boy", data.Source);
        Assert.Equal(4, data.Types.Count);
        var bolter = data.Types["bolter"];
        Assert.Equal(new OrkBloodZone(75f, "explode_head"), bolter.Zones[(int)OrkBloodZoneKind.Head]);
        Assert.Equal(new OrkBloodZone(50f, "explode_left_side"), bolter.Zones[(int)OrkBloodZoneKind.RightLeg]);
        Assert.Equal("explode_body", bolter.NothingLeft);
        Assert.Equal(6, bolter.Zones.Count);   // shield is not a zone of ours
        Assert.Empty(data.Notes);
    }

    [Fact]
    public void A_points_effects_are_its_play_fx_commands_with_marker_scale_and_direction()
    {
        var plays = Data().PointEffects("explode_head");
        Assert.Equal(["body_dismemberment", "body_dismemberment_drip"], plays.Select(p => p.Name));
        Assert.Equal(0.4f, plays[0].Scale);
        Assert.Equal("fx_gore_neck", plays[0].Marker);
        Assert.Equal("negative_hit_direction", plays[1].Direction);
        Assert.Empty(Data().PointEffects("missing"));
        Assert.Contains("body_explode", Data().EffectNames);
    }

    [Fact]
    public void A_missing_point_is_noted()
    {
        var data = OrkBlood.Build(new Dictionary<string, BafDocument> { ["ork_coop_boy"] = Blueprint() },
            new Dictionary<string, BafDocument> { ["explode_head"] = Point() }, new Dictionary<string, BafDocument>());
        Assert.Contains(data.Notes, n => n.Contains("explode_body"));
    }

    [Fact]
    public void A_weapon_names_its_shot_and_each_strike_and_never_a_penetration_or_a_secondary_target()
    {
        var data = Data();
        Assert.Equal("bolter", data.Weapon("sm_mp_ranged_bolter")!.Ranged);
        var sword = data.Weapon("sm_mp_melee_chainsword")!;
        Assert.Equal("", sword.Ranged);
        Assert.Equal("chainsword", sword.Of("ability_melee_x1"));
        Assert.Equal("chainsword_heavy", sword.Of("ability_melee_x3"));
        Assert.Equal("none", sword.Of("ability_melee_y"));
        Assert.Equal("bullrush", sword.Of(OrkBloodWeapon.ChargeAbility));
        Assert.Null(data.Weapon("sm_mp_ranged_melta"));
    }

    [Fact]
    public void A_deaths_type_comes_from_the_killers_weapon_and_strike()
    {
        var data = Data();
        var bolter = data.Weapon("sm_mp_ranged_bolter");
        var sword = data.Weapon("sm_mp_melee_chainsword");
        Assert.Equal("bolter", OrkBloodRules.TypeOf(MusouDeathKind.Shot, "sm_mp_ranged_bolter", bolter, sword, "", false));
        Assert.Equal("chainsword_heavy", OrkBloodRules.TypeOf(MusouDeathKind.Melee, null, bolter, sword, "ability_melee_x3", false));
        Assert.Equal("chainsword", OrkBloodRules.TypeOf(MusouDeathKind.Melee, null, bolter, sword, "ability_unknown", false));   // unknown strike: the first light one
        Assert.Equal("bullrush", OrkBloodRules.TypeOf(MusouDeathKind.Melee, null, bolter, sword, "ability_melee_x3", true));
        Assert.Equal("explosive_heavy", OrkBloodRules.TypeOf(MusouDeathKind.Blast, null, null, null, "", false));
        Assert.Equal("explosive_heavy", OrkBloodRules.TypeOf(MusouDeathKind.Barrage, null, null, null, "", false));
        Assert.Equal("heavy_bolter", OrkBloodRules.TypeOf(MusouDeathKind.Strafe, null, null, null, "", false));
        Assert.Equal("", OrkBloodRules.TypeOf(MusouDeathKind.World, "sm_mp_ranged_bolter", bolter, sword, "", false));
        var plasma = new OrkBloodWeapon("", new Dictionary<string, string>());   // the plasma gun's shot names no type
        Assert.Equal(OrkBloodRules.PlasmaFallback, OrkBloodRules.TypeOf(MusouDeathKind.Shot, "sm_mp_ranged_plasma_gun", plasma, null, "", false));
    }

    [Theory]
    [InlineData(true, 0.9f, OrkBloodZoneKind.Head)]
    [InlineData(false, 0f, OrkBloodZoneKind.Body)]
    [InlineData(false, 0.25f, OrkBloodZoneKind.LeftArm)]
    [InlineData(false, 0.999f, OrkBloodZoneKind.RightLeg)]
    public void The_zone_is_the_head_for_a_head_shot_else_a_seeded_part(bool head, float roll, OrkBloodZoneKind zone) => Assert.Equal(zone, OrkBloodRules.ZoneOf(head, roll));

    [Fact]
    public void A_kill_plays_the_zones_point_under_its_chance_and_nothing_left_for_a_blast()
    {
        var data = Data();
        Assert.Equal("explode_head", OrkBloodRules.Kill(data, "bolter", MusouDeathKind.Shot, true, 0f, 0.7f).Point);   // 70 < 75
        Assert.Equal("", OrkBloodRules.Kill(data, "bolter", MusouDeathKind.Shot, true, 0f, 0.8f).Point);   // 80 >= 75
        Assert.Equal("", OrkBloodRules.Kill(data, "chainsword", MusouDeathKind.Melee, false, 0f, 0f).Point);   // the light chainsword never dismembers an Ork
        var blast = OrkBloodRules.Kill(data, "explosive_heavy", MusouDeathKind.Blast, false, 0.5f, 0.99f);
        Assert.Equal("explode_body", blast.Point);
        Assert.True(blast.WholeBody);
        Assert.False(OrkBloodRules.Kill(data, "bolter", MusouDeathKind.Shot, false, 0f, 0f).WholeBody);   // a side
        Assert.Equal("", OrkBloodRules.Kill(data, "unknown", MusouDeathKind.Shot, false, 0f, 0f).Point);
    }

    [Theory]
    [InlineData("explode_body", true)]
    [InlineData("smash_small_body", true)]
    [InlineData("explode_body_ig", true)]
    [InlineData("explode_head", false)]
    [InlineData("power_left_side", false)]
    [InlineData("", false)]
    public void Whole_body_points_end_in_body(string point, bool whole) => Assert.Equal(whole, OrkBloodRules.IsWholeBody(point));

    [Fact]
    public void Markers_sit_by_name_round_the_chest()
    {
        Assert.True(OrkBloodRules.MarkerOffset("fx_gore_neck").Z > 0.3f);
        Assert.True(OrkBloodRules.MarkerOffset("fx_gore_l_side").X < 0f);
        Assert.True(OrkBloodRules.MarkerOffset("fx_gore_r_side").X > 0f);
        Assert.True(OrkBloodRules.MarkerOffset("rig_world").Z < 0f);   // the feet
        Assert.True(OrkBloodRules.MarkerOffset("rig_lcalf").Z < 0f);
        Assert.Equal(System.Numerics.Vector3.Zero, OrkBloodRules.MarkerOffset("rig_chest"));
    }

    [Fact]
    public void The_original_blood_draws_in_musou_only_and_never_with_the_old_gore_or_none()
    {
        Assert.True(OrkBloodRules.Active(musou: true, goreOff: false, oldGore: false));
        Assert.False(OrkBloodRules.Active(musou: false, goreOff: false, oldGore: false));   // classic PvP
        Assert.False(OrkBloodRules.Active(musou: true, goreOff: true, oldGore: false));
        Assert.False(OrkBloodRules.Active(musou: true, goreOff: false, oldGore: true));
        Assert.Equal(1.5f, OrkBloodRules.MusouScale);
    }
}
