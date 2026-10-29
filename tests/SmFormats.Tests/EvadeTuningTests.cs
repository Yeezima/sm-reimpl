using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M21 reader: extracted motion (root motion) of clips, Modifier and EntityProperty tags, and the evade (wargear values,
/// the base graph's directional sequencers, their branch times and the clips' paths). Synthetic files only.
/// </summary>
public class EvadeTuningTests
{
    // ---------------------------------------------------------------- Havok: extracted motion and tags

    static readonly Bone[] Bones = [new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity)];

    [Fact]
    public void Extracted_motion_is_read_from_the_reference_frame_and_is_null_without_one()
    {
        Vector4[] motion = [new(0, 0, 0, 0), new(0.1f, 1f, 0, 0), new(0.2f, 3f, 0.5f, 0.25f)];
        var file = new HavokCharacterFile(Build(Bones,
        [
            new Clip("Roll", 1.5f, 3, [new(Vector3.Zero, Quaternion.Identity)], motion),
            new Clip("Idle", 1f, 2, [new(Vector3.Zero, Quaternion.Identity)]),
        ]));
        var m = file.LoadExtractedMotion("Roll")!;
        Assert.Equal((Vector3.UnitZ, Vector3.UnitY, 1.5f), (m.Up, m.Forward, m.Duration));
        Assert.Equal(motion, m.Samples);
        Assert.Null(file.LoadExtractedMotion("Idle"));   // no reference frame
        Assert.Equal(1.5f, file.LoadAnimation("Roll").Animation.Duration);   // the animation itself still loads
        Assert.Throws<KeyNotFoundException>(() => file.LoadExtractedMotion("Missing"));
    }

    [Fact]
    public void Modifier_tags_carry_the_modifier_name_and_entity_property_tags_their_data_class_and_mode()
    {
        var graphs = HavokBehavior.Read(new HavokPackfile(BuildBehavior(
        [
            new Graph("SM_Base_MP", ["BRANCH_Run"],
            [
                new Sequencer("Ranged Evade Fwd Sequencer", ["SM_EVD_R2R_Roll_F_02_8m"],
                [
                    new Tag("Modifier", 0f, 0.1f, new ModifierValue("hkbAlignCharacterModifier", "Align To Nav Direction01")),
                    new Tag("EntityProperty", 0f, 0.5957f, new PropertyValue("hkbEntityPropertyTargetableModeData", 1)),
                    new Tag("EntityProperty", 0f, 0.4681f, new PropertyValue("hkbEntityPropertyCollisionModeData", 2)),
                    new Tag("Branch", 0.5714f, 1f, 0),
                    new Tag("Modifier", 0f, 0.05f),   // a modifier tag without a target
                ]),
            ]),
        ])));
        var tags = graphs.Single().Sequencers.Single().Tags;
        Assert.Equal(new BehaviorTag("Modifier", 0f, 0.1f, "Align To Nav Direction01", null), tags[0]);
        Assert.Equal(new BehaviorTag("EntityProperty", 0f, 0.5957f, "TargetableMode", null, 1), tags[1]);
        Assert.Equal(new BehaviorTag("EntityProperty", 0f, 0.4681f, "CollisionMode", null, 2), tags[2]);
        Assert.Equal(new BehaviorTag("Branch", 0.5714f, 1f, "BRANCH_Run", null), tags[3]);   // other kinds unchanged, no mode
        Assert.Equal(new BehaviorTag("Modifier", 0f, 0.05f, null, null), tags[4]);
    }

    // ---------------------------------------------------------------- the evade reader

    const float Length = 1.1666666f;   // 35 frames at 30 Hz

    static BehaviorTag B(float start, string name) => new("Branch", start, 1f, name, null);

    /// <summary>A set's four sequencers in SM_Base_MP, shaped like the install's ranged rolls (branch fractions as there).</summary>
    static BehaviorSequencer Roll(string prefix, char letter, bool align = false, bool withSwitch = true, float speed = 1f) => new($"{letter} seq", [$"{prefix}{letter}_01_8m"],
    [
        new("EntityProperty", 0f, 0.5957f, "TargetableMode", null, 1),
        new("EntityProperty", 0f, 0.4681f, "CollisionMode", null, 1),
        .. align ? [new BehaviorTag("Modifier", 0f, 0.1f, "Align To Nav Direction01", null)] : Array.Empty<BehaviorTag>(),
        B(0.5714f, "BRANCH_Run"), B(0.5714f, "BRANCH_ToRanged"), B(0.6857f, "BRANCH_Melee_Combo"), B(1f, "BRANCH_Evade"),
        .. withSwitch ? [B(0.6596f, "BRANCH_To_Weapon_Switch")] : Array.Empty<BehaviorTag>(),
    ], [speed]);

    static readonly BehaviorGraph Base = new(EvadeTuning.Graph, [],
    [
        Roll("SM_EVD_R2R_Roll_", 'R'), Roll("SM_EVD_R2R_Roll_", 'F', align: true), Roll("SM_EVD_R2R_Roll_", 'B'), Roll("SM_EVD_R2R_Roll_", 'L'),
        Roll("SM_EVD_TB_Evade_", 'F', withSwitch: false), Roll("SM_EVD_TB_Evade_", 'B', withSwitch: false), Roll("SM_EVD_TB_Evade_", 'L', withSwitch: false),
        Roll("SM_EVD_TB_Evade_", 'R', withSwitch: false, speed: 2f),
    ]);

    /// <summary>Paths: forward rolls go +Y, back −Y, left −X, right +X, 9 m in three samples (the first offset, as a start not at 0 would be).</summary>
    static HavokExtractedMotion? Motion(string clip)
    {
        if (clip.Contains("NoMotion")) return null;
        var dir = clip[16] switch { 'F' => Vector2.UnitY, 'B' => -Vector2.UnitY, 'L' => -Vector2.UnitX, _ => Vector2.UnitX };
        Vector4 At(float d) => new(0.5f + dir.X * d, 0.25f + dir.Y * d, 0f, 0f);
        return new HavokExtractedMotion(Vector3.UnitZ, Vector3.UnitY, Length, [At(0f), At(6f), At(9f)]);
    }

    static BafDocument Wargear(string set = "evade", string? other = null) =>
        BafDocument.Read(SyntheticBafWriter.EvadeAbility(set, set == "evade" ? "evade_default_mp" : "evade_heavy_default_mp", 0.8f, 25, 40, other));

    [Fact]
    public void The_evade_is_read_from_the_wargear_and_the_base_graph_rolls_with_their_paths_and_branch_times()
    {
        var notes = new List<string>();
        var e = EvadeTuning.Read("sm_mp_core_abil_evade", Wargear(), [Base], _ => Length, Motion, notes)!;
        Assert.Equal(("sm_mp_core_abil_evade", "evade", "evade_default_mp"), (e.Name, e.AnimationSet, e.Camera));
        Assert.Equal((0.8f, 25f, 40f, true), (e.MinInterval, e.WallHitAngle, e.MaxTurnRate, e.AlignForward));   // ints and floats both read
        Assert.Equal(0.5957f * Length, e.TargetableModeTime, 5);
        Assert.Equal(0.4681f * Length, e.CollisionModeTime, 5);
        var f = e.Forward;
        Assert.Equal(("SM_EVD_R2R_Roll_F_01_8m", Length), (f.Animation, f.Duration));
        Assert.Equal([Vector2.Zero, new(0f, 6f), new Vector2(0f, 9f)], f.Path);   // relative to the first sample
        Assert.Equal(0.5714f * Length, f.MoveAt, 5);
        Assert.Equal(0.5714f * Length, f.RangedAt, 5);
        Assert.Equal(0.6596f * Length, f.SwitchAt, 5);
        Assert.Equal(0.6857f * Length, f.MeleeAt, 5);
        Assert.Equal(Length, f.NextAt, 5);
        Assert.Equal(new Vector2(0f, -9f), e.Back.Distance);
        Assert.Equal(new Vector2(-9f, 0f), e.Left.Distance);
        Assert.Equal(new Vector2(9f, 0f), e.Right.Distance);
        Assert.Equal("SM_EVD_R2R_Roll_R_01_8m", e.Move(EvadeDirection.Right).Animation);
        Assert.Contains(notes, n => n.Contains("max_turn_rate 40 not simulated"));
        Assert.Contains(notes, n => n.Contains("mp_fall_protection"));
        // Interpolation along the path: linear between samples, clamped at the ends.
        Assert.Equal(new Vector2(0f, 3f), f.At(Length / 4f));
        Assert.Equal(new Vector2(0f, 7.5f), f.At(Length * 0.75f));
        Assert.Equal(f.Distance, f.At(10f));
        Assert.Equal(Vector2.Zero, f.At(-1f));
    }

    [Fact]
    public void The_heavy_set_uses_the_heavy_bolter_clips_missing_branches_fall_back_to_the_end_and_playback_speed_scales_time()
    {
        var notes = new List<string>();
        var e = EvadeTuning.Read("sm_mp_core_abil_heavy_evade", Wargear("heavy_evade", "ability_displace_kick"), [Base], _ => Length, Motion, notes)!;
        Assert.Equal("SM_EVD_TB_Evade_F_01_8m", e.Forward.Animation);
        Assert.False(e.AlignForward);
        Assert.Equal(e.Forward.Duration, e.Forward.SwitchAt);   // no BRANCH_To_Weapon_Switch: the clip's end
        Assert.Contains(notes, n => n.Contains("no BRANCH_To_Weapon_Switch"));
        Assert.Contains(notes, n => n.Contains("ability_displace_kick not simulated"));
        Assert.Equal(Length / 2f, e.Right.Duration, 5);          // playback speed 2
        Assert.Equal(0.5714f * Length / 2f, e.Right.MoveAt, 5);
    }

    [Fact]
    public void Negative_controls_no_evade_without_the_ability_a_known_set_the_graph_every_direction_or_usable_motion()
    {
        var notes = new List<string>();
        var bolter = BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.133f, 30, 270, 1.3f, 250f, [], 1f, 1f, 1f, 1f));
        Assert.False(EvadeTuning.IsEvade(bolter));
        Assert.True(EvadeTuning.IsEvade(Wargear()));
        Assert.Null(EvadeTuning.Read("bolter", bolter, [Base], _ => Length, Motion, notes));
        Assert.Null(EvadeTuning.Read("odd", Wargear("jump_evade"), [Base], _ => Length, Motion, notes));
        Assert.Contains(notes, n => n.Contains("odd") && n.Contains("no known evade clips"));
        Assert.Null(EvadeTuning.Read("nograph", Wargear(), [Base with { Name = "Other" }], _ => Length, Motion, notes));
        Assert.Null(EvadeTuning.Read("noleft", Wargear(), [Base with { Sequencers = Base.Sequencers.Where(s => s.Name != "L seq").ToArray() }], _ => Length, Motion, notes));
        Assert.Contains(notes, n => n.Contains("noleft") && n.Contains("R2R_Roll_L_"));
        Assert.Null(EvadeTuning.Read("nomotion", Wargear(), [Base], _ => Length, _ => null, notes));
        Assert.Null(EvadeTuning.Read("frame", Wargear(), [Base], _ => Length, c => Motion(c)! with { Forward = Vector3.UnitX }, notes));
        Assert.Contains(notes, n => n.Contains("frame") && n.Contains("not +Z up, +Y forward"));
        Assert.Null(EvadeTuning.Read("short", Wargear(), [Base], _ => Length, c => Motion(c)! with { Samples = [Vector4.Zero] }, notes));
        Assert.Null(EvadeTuning.Read("broken", Wargear(), [Base], _ => throw new InvalidDataException("bad clip"), Motion, notes));
        Assert.Contains(notes, n => n.Contains("broken") && n.Contains("bad clip"));
        Assert.NotNull(EvadeTuning.Read("control", Wargear(), [Base], _ => Length, Motion, notes));
    }

    // ---------------------------------------------------------------- the heavy hop

    [Fact]
    public void The_hop_curve_is_a_parabola_over_the_main_run_of_foot_clearance()
    {
        float[] feet = [0.006f, 0.006f, 0.2f, 0.1f, 0.3f, 0.2f, 0.006f, 0.05f, 0.006f];   // jagged, with a second small bump
        var h = EvadeTuning.Clearance(feet);
        float peak = 0.3f - 0.006f;
        float[] want = [0f, 0f, 4f * peak * 0.16f, 4f * peak * 0.24f, 4f * peak * 0.24f, 4f * peak * 0.16f, 0f, 0f, 0f];
        Assert.Equal(want.Length, h.Length);
        for (int k = 0; k < want.Length; k++) Assert.Equal(want[k], h[k], 5);
        Assert.All(EvadeTuning.Clearance([0.006f, 0.02f, 0.025f, 0.006f]), v => Assert.Equal(0f, v));   // under the 2 cm threshold
        Assert.Empty(EvadeTuning.Clearance([]));
    }

    [Fact]
    public void The_lowest_foot_is_read_from_the_clip_in_model_space()
    {
        // The root turned −90° about Y maps the foot's local X onto model Z: the foot rises from 0.1 to 0.4 m over the clip.
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var rig = new HavokCharacterFile(Build(
            [new("RIG_Root", -1, Vector3.Zero, turn), new("RIG_L_Foot", 0, new Vector3(0.1f, 0, 0), Quaternion.Identity)],
            [new Clip("Hop", 1f, 31, [new(Vector3.Zero, turn), new(Vector3.Zero, Quaternion.Identity, (0.1f, 0.4f))])]));
        var feet = EvadeTuning.LowestFoot(rig, "Hop", 4)!;
        Assert.Equal(4, feet.Length);
        for (int k = 0; k < 4; k++) Assert.Equal(0.1f + 0.1f * k, feet[k], 2);
        var noFeet = new HavokCharacterFile(Build(Bones, [new Clip("Idle", 1f, 2, [new(Vector3.Zero, Quaternion.Identity)])]));
        Assert.Null(EvadeTuning.LowestFoot(noFeet, "Idle", 4));
    }

    [Fact]
    public void Only_the_heavy_set_hops_and_without_feet_it_stays_on_the_ground()
    {
        var notes = new List<string>();
        IReadOnlyList<float>? Feet(string clip, int n) => Enumerable.Range(0, n).Select(k => k == 1 ? 0.3f : 0f).ToArray();
        var heavy = EvadeTuning.Read("heavy", Wargear("heavy_evade"), [Base], _ => Length, Motion, notes, Feet)!;
        Assert.True(heavy.Forward.Hops && heavy.Back.Hops && heavy.Left.Hops && heavy.Right.Hops);
        Assert.Equal([0f, 0.3f, 0f], heavy.Forward.Height);   // take-off at 0, landing at 2: the parabola's middle is the peak
        Assert.Contains(notes, n => n.Contains("a hop: peak 0.300"));
        var roll = EvadeTuning.Read("roll", Wargear(), [Base], _ => Length, Motion, notes, (_, _) => throw new InvalidOperationException("rolls are not read"))!;
        Assert.False(roll.Forward.Hops);
        var flat = EvadeTuning.Read("flat", Wargear("heavy_evade"), [Base], _ => Length, Motion, notes)!;
        Assert.False(flat.Forward.Hops);
        Assert.Contains(notes, n => n.Contains("flat") && n.Contains("feet not read"));
        var broken = EvadeTuning.Read("broken", Wargear("heavy_evade"), [Base], _ => Length, Motion, notes, (_, _) => throw new InvalidDataException("bad pose"))!;
        Assert.False(broken.Forward.Hops);
        Assert.Contains(notes, n => n.Contains("broken") && n.Contains("bad pose"));
        // A config refuses a height curve of the wrong length or out of range.
        ClassLoadout[] classes = [new("heavy", -1, -1, []) { Evade = 0 }];
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [heavy]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [heavy with { Left = heavy.Left with { Height = [0f, 0.3f] } }]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [heavy with { Left = heavy.Left with { Height = [0f, -0.1f, 0f] } }]));
    }

    [Fact]
    public void Classes_with_a_jump_pack_get_no_evade_the_others_their_base_wargear_evade_read_once()
    {
        var classes = new[]
        {
            new MpClass("sm - assault marine", "assault", "space_marine", ["sm_mp_abil_jumppack", "sm_mp_core_abil_jump_evade"], [], [], null),
            new MpClass("sm - tactical", "tactical", "space_marine", ["sm_mp_abil_sprint", "sm_mp_core_abil_evade"], [], [], null),
            new MpClass("sm - devastator", "heavy", "space_marine", ["sm_mp_core_abil_heavy_evade"], [], [], null),
            new MpClass("csm - tactical", "tactical", "chaos", ["sm_mp_core_abil_evade"], [], [], null),
        };
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_abil_jumppack"] = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f)], [(0.1f, 35f)], 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f)),
            ["sm_mp_core_abil_jump_evade"] = Wargear(),   // has an ability_evade too: the pack wins
            ["sm_mp_abil_sprint"] = BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "coreability") }))),
            ["sm_mp_core_abil_evade"] = Wargear(),
            ["sm_mp_core_abil_heavy_evade"] = Wargear("heavy_evade"),
        };
        var notes = new List<string>();
        var result = MpClasses.Build(classes, wargear, [Base], _ => Length, notes, extractedMotion: Motion);
        Assert.Equal([-1, 0, 1, 0], result.Loadouts.Select(l => l.Evade));
        Assert.Equal(0, result.Loadouts[0].JumpPack);
        Assert.Equal(["sm_mp_core_abil_evade", "sm_mp_core_abil_heavy_evade"], result.Evades.Select(e => e.Name));
        // Into a config: the classes' evades resolve; without a motion reader no evade is read.
        var config = GameplayTuning.WithClasses(new SimConfig(), result.Weapons, result.Loadouts, result.Thrown, result.JumpPacks, result.Evades)!;
        Assert.Equal("sm_mp_core_abil_heavy_evade", Loadouts.Evade(config, 2)!.Name);
        Assert.Null(Loadouts.Evade(config, 0));
        var none = MpClasses.Build(classes, wargear, [Base], _ => Length, notes);
        Assert.All(none.Loadouts, l => Assert.Equal(-1, l.Evade));
        Assert.Contains(notes, n => n.Contains("no extracted motion reader"));
    }

    [Fact]
    public void A_config_refuses_unusable_evades_and_out_of_range_indices()
    {
        var notes = new List<string>();
        var e = EvadeTuning.Read("sm_mp_core_abil_evade", Wargear(), [Base], _ => Length, Motion, notes)!;
        ClassLoadout[] classes = [new("tactical", -1, -1, []) { Evade = 0 }];
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e]));   // control
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], [classes[0] with { Evade = 1 }], evades: [e]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: []));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { Forward = e.Forward with { Duration = 9f } }]));   // 270 ticks: too long for a byte
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { Left = e.Left with { MoveAt = 2f } }]));        // after the end
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { Back = e.Back with { Path = [Vector2.Zero] } }]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { Right = e.Right with { Path = [Vector2.Zero, new(float.NaN, 0f)] } }]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { MinInterval = 9f }]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { WallHitAngle = 91f }]));
        // The config keeps its own copies of the paths.
        var path = new List<Vector2>(e.Forward.Path);
        var config = GameplayTuning.WithClasses(new SimConfig(), [], classes, evades: [e with { Forward = e.Forward with { Path = path } }])!;
        ulong hash = config.Hash();
        path[1] = new Vector2(5f, 5f);
        Assert.Equal(hash, config.Hash());
    }
}
