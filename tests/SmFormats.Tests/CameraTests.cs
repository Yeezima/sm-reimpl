using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;

namespace SmFormats.Tests;

/// <summary>
/// M20: the camera rig (<see cref="CameraRig"/>, sampled by pitch) and the camera tuning (<see cref="CameraTuning"/>: camera
/// sets, input tuning, the weapon → set mapping through wargear and aim-assist files). Synthetic files only.
/// </summary>
public class CameraTests
{
    static readonly Quaternion Tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30f * MathF.PI / 180f);   // looking 30° up

    /// <summary>A three-bone camera rig like the game's, with one 6 s / 181-frame curve whose outer bone slides along X.</summary>
    static byte[] Rig(string innerName = CameraRig.InnerBone) => SyntheticHavokWriter.Build(
        [
            new("RIG_Root", -1, new Vector3(0, 0, 1.2f), Quaternion.Identity),
            new(innerName, 0, new Vector3(0, 0, 0.55f), Quaternion.Identity),
            new(CameraRig.OuterBone, 0, new Vector3(0, 0, 1.25f), Quaternion.Identity),
        ],
        [
            new("navigationSet_standing", 6f, 181, [
                new(Vector3.Zero, Quaternion.Identity),
                new(new Vector3(0.6f, -0.7f, 2.2f), Quaternion.Identity),
                new(new Vector3(0f, -3.4f, 2.4f), Tilt, (0.2f, 1.0f))]),
            new("aimSet_standing", 6f, 181, [
                new(Vector3.Zero, Quaternion.Identity),
                new(new Vector3(0.645f, -0.5f, 2.16f), Quaternion.Identity),
                new(new Vector3(0.645f, -1.78f, 2.16f), Quaternion.Identity)]),
        ]);

    [Fact]
    public void The_rig_is_sampled_by_pitch_degrees_one_frame_per_degree()
    {
        var rig = CameraRig.Read(Rig());
        Assert.Equal(["navigationSet_standing", "aimSet_standing"], rig.CurveNames);
        Assert.True(rig.Has("navigationset_standing"));   // the camera sets name curves in lower case
        // Side slides 0.2 → 1.0 m over the clip: 0° (down) → 0.2, 45° → 0.4, 90° (level) → 0.6, 180° (up) → 1.0.
        foreach (var (deg, side) in new[] { (0f, 0.2f), (45f, 0.4f), (90f, 0.6f), (135f, 0.8f), (180f, 1.0f), (-30f, 0.2f), (400f, 1.0f) })
        {
            var o = rig.Offset("NAVIGATIONSET_STANDING", deg)!.Value;
            Assert.Equal(side, o.Side, 2e-3f);
            Assert.Equal((3.4f, 2.4f), (o.Back, o.Up));
            Assert.Equal((0.7f, 0.6f, 2.2f), (o.PivotBack, o.PivotSide, o.PivotUp));
            Assert.Equal(30f * MathF.PI / 180f, o.LookPitch, 1e-3f);
            Assert.Equal(new Vector3(o.Side, -3.4f, 2.4f), o.Camera);
            Assert.Equal(new Vector3(0.6f, -0.7f, 2.2f), o.Pivot);
        }
        var aim = rig.OffsetAtPitch("aimset_standing", 0f)!.Value;
        Assert.Equal((1.78f, 0.645f, 2.16f, 0f), (aim.Back, aim.Side, aim.Up, aim.LookPitch));
        Assert.Null(rig.Offset("no_such_curve", 90f));
        Assert.Null(rig.Offset("aimSet_standing", float.NaN));
        Assert.Equal(90f, CameraRig.FileDegrees(0f));
        Assert.Equal(135f, CameraRig.FileDegrees(MathF.PI / 4), 1e-4f);
        Assert.Equal(-MathF.PI / 2, CameraRig.SimRadians(0f), 1e-6f);
    }

    [Fact]
    public void A_rig_without_the_camera_bones_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CameraRig.Read(Rig(innerName: "RIG_Something_Else")));
    }

    // ---------------------------------------------------------------- camera tuning

    static readonly (string, object)[] NoUpgrade = [("$ref", "_base_templates\\required_upgrade_name"), ("required_list", Array.Empty<(string, object)>())];
    static readonly (string, object)[] SnapUpgrade = [("$ref", "_base_templates\\required_upgrade_name"), ("required_list", new (string, object)[] { ("entry", "snap") })];

    static BafDocument Set(float fov, string curve, float min, float max, float blend, bool zoomLevels = false) => BafDocument.Read(SyntheticBafWriter.Build(
        ("lens_properties", new (string, object)[] { ("$ref", "_base_templates\\cameraman_lens"), ("blur_radius", 0.1f) }),
        ("field_of_view", fov), ("animation_curve", curve), ("relax_delay", 0.1f), ("relax_speed", 10f), ("aim_blend_speed_scaler", 5f),
        ("pitch_minimum", min), ("pitch_maximum", max), ("blend_in_time", blend), ("blend_in_distance", 5f), ("target_lock_on_seconds", 0.25f), ("aim_use_zoom_levels", zoomLevels)));

    static BafDocument Input(float mouse, float horizontal, float vertical) => BafDocument.Read(SyntheticBafWriter.Build(
        ("speed_horizontal", horizontal), ("speed_vertical", vertical), ("request_damping_multiplier", 1f), ("speed_mouse", mouse)));

    static BafDocument Assist(string input) => BafDocument.Read(SyntheticBafWriter.Build(
        ("target_adhesion_strength", 0.5f), ("camera_input_tuning_set", input), ("max_angle_for_adhesion", 5f)));

    /// <summary>Wargear with camera data: a snap-upgrade entry first, then the default (the reader must pick the default).</summary>
    static BafDocument Weapon(string exploration, string aim, string autoAim, string autoAimZoom, float[]? zoom = null) => BafDocument.Read(SyntheticBafWriter.Build(
        ("camera_data", new (string, object)[]
        {
            ("entry", new (string, object)[] { ("required_upgrade_name", SnapUpgrade), ("camera_data", new (string, object)[] {
                ("aim_camera", "camera_curve_settings\\aim_upgraded"), ("exploration_camera", "camera_curve_settings\\aim_upgraded") }) }),
            ("entry", new (string, object)[] { ("required_upgrade_name", NoUpgrade), ("camera_data", new (string, object)[] {
                ("$ref", "wargear\\wargear_camera_mode"), ("auto_aim", autoAim), ("auto_aim_zoom", autoAimZoom),
                ("aim_camera", aim), ("exploration_camera", exploration), ("melee_camera", "camera_curve_settings\\melee_default_mp"),
                ("zoom_levels", new (string, object)[] { ("_base_templates\\zoom_levels", new (string, object)[] { ("$ref", "_base_templates\\zoom_levels"), ("fov_1", zoom?[0] ?? 33f), ("fov_2", zoom?[1] ?? 30f), ("fov_3", 5f),
                    ("num_zoom_levels", zoom is null ? 1 : 2), ("required_upgrade_name", NoUpgrade) }) }) }) }),
        }),
        ("common_data", new (string, object)[] { ("slot", "ranged") })));

    static CameraTuning Tuning() => CameraTuning.Read(
        new Dictionary<string, BafDocument>
        {
            ["exploration_default_mp"] = Set(40f, "navigationset_standing", 20f, 160f, 0.25f),
            ["aim_sm_rifle_mp"] = Set(20f, "aimset_standing", 5f, 175f, 0.15f),
            ["aim_sm_sniper_mp"] = Set(15f, "aimset_zoom", 10f, 170f, 0.05f, zoomLevels: true),
            ["aim_upgraded"] = Set(25f, "aimset_heavy", 30f, 145f, 0.08f),
            ["broken"] = Set(40f, "navigationset_standing", 160f, 20f, 0.25f),   // minimum above maximum: skipped
        },
        new Dictionary<string, BafDocument>
        {
            ["default"] = Input(100f, 300f, 200f), ["aim"] = Input(60f, 80f, 60f), ["aim_sniper_mp"] = Input(20f, 28f, 17f),
        },
        new Dictionary<string, BafDocument>
        {
            ["sm_auto_aim_bolter_mp"] = Assist("camera_input_tuning\\default"), ["sm_auto_aim_bolter_zoom_mp"] = Assist("camera_input_tuning\\aim"),
            ["sm_auto_aim_sniper_zoom_mp"] = Assist("camera_input_tuning\\aim_sniper_mp"),
        },
        new Dictionary<string, BafDocument>
        {
            ["sm_mp_ranged_bolter"] = Weapon("camera_curve_settings\\exploration_default_mp", "camera_curve_settings\\aim_sm_rifle_mp",
                "auto_aim_info\\multiplayer\\sm_auto_aim_bolter_mp", "auto_aim_info\\multiplayer\\sm_auto_aim_bolter_zoom_mp"),
            ["sm_mp_ranged_sniper_rifle"] = Weapon("camera_curve_settings\\exploration_default_mp", "camera_curve_settings\\aim_sm_sniper_mp",
                "", "auto_aim_info\\multiplayer\\sm_auto_aim_sniper_zoom_mp", zoom: [10f, 10f]),
            ["sm_mp_ranged_missing"] = Weapon("camera_curve_settings\\exploration_default_mp", "camera_curve_settings\\aim_nowhere", "", ""),
        });

    [Fact]
    public void Camera_sets_and_input_tuning_are_read_in_the_files_units()
    {
        var t = Tuning();
        var aim = t.Sets["aim_sm_rifle_mp"];
        Assert.Equal(new CameraSet("aim_sm_rifle_mp", 20f, "aimset_standing", 5f, 175f, 0.15f, 0.1f, 10f, 5f, 5f, 0.25f), aim);
        Assert.False(t.Sets.ContainsKey("broken"));
        Assert.Contains(t.Notes, n => n.Contains("broken"));
        Assert.Equal(new InputTuning("aim", 60f, 80f, 60f), t.Inputs["aim"]);
    }

    [Fact]
    public void Each_weapon_gets_its_default_camera_data_and_input_tuning_through_its_aim_assist_sets()
    {
        var t = Tuning();
        var bolter = t.For("sm_mp_ranged_bolter")!;
        Assert.Equal(("exploration_default_mp", "aim_sm_rifle_mp"), (bolter.Exploration.Name, bolter.Aim.Name));   // not the upgraded entry
        Assert.Equal(("default", 100f, "aim", 60f), (bolter.ExplorationInput.Name, bolter.ExplorationInput.SpeedMouse, bolter.AimInput.Name, bolter.AimInput.SpeedMouse));
        Assert.Equal(("sm_auto_aim_bolter_mp", "sm_auto_aim_bolter_zoom_mp"), (bolter.AutoAim, bolter.AutoAimZoom));
        Assert.StartsWith("wargear sm_mp_ranged_bolter", bolter.Evidence);
        Assert.Contains(t.Notes, n => n.Contains("sm_mp_ranged_bolter") && n.Contains("upgrade"));

        // No exploration aim-assist set: the default input tuning, said so; the zoom set's input applies in aim mode.
        var sniper = t.For("sm_mp_ranged_sniper_rifle")!;
        Assert.Equal(("aim_sm_sniper_mp", 15f, "aimset_zoom"), (sniper.Aim.Name, sniper.Aim.FieldOfView, sniper.Aim.AnimationCurve));
        Assert.Equal(("default", "aim_sniper_mp", 20f), (sniper.ExplorationInput.Name, sniper.AimInput.Name, sniper.AimInput.SpeedMouse));
        Assert.Contains("fallback", sniper.Evidence);

        // Zoom levels (fix/camera-zoom-misc): read only where the aim set says aim_use_zoom_levels (the sniper's), not for the bolter whose wargear also lists some.
        Assert.Equal([10f, 10f], sniper.ZoomFovs);
        Assert.Contains("zoom_levels 10 / 10", sniper.Evidence);
        Assert.Null(bolter.ZoomFovs);

        // A weapon whose aim set does not exist is not listed; it (and an unknown name) gets the bolter's sets as a fallback.
        Assert.False(t.Weapons.ContainsKey("sm_mp_ranged_missing"));
        foreach (var name in new[] { "sm_mp_ranged_missing", "no_such_weapon" })
        {
            var fallback = t.For(name)!;
            Assert.Equal((name, "exploration_default_mp", "aim_sm_rifle_mp", "default", "aim"),
                (fallback.Weapon, fallback.Exploration.Name, fallback.Aim.Name, fallback.ExplorationInput.Name, fallback.AimInput.Name));
            Assert.StartsWith("fallback:", fallback.Evidence);
        }
        Assert.True(CameraTuning.IsMultiplayerWargear("csm_mp_ranged_bolter"));
        Assert.False(CameraTuning.IsMultiplayerWargear("sm_ranged_bolter"));
    }
}
