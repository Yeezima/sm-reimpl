using System.Numerics;
using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Install transitions batch 3: the braced heavy bolter's standing posture read from a brace-shaped graph (<see cref="TurnAnimations.Brace"/>): the idle grid's -60 / 0 / +77 columns,
/// the per-side thresholds (-60 / +77), the turn clip that sits directly in the turn blend under the state's sequencer, and the <c>BRANCH_TurnComplete</c> end. Synthetic packfiles and
/// numbers shaped like the install's graph; no original bytes.
/// </summary>
public class BraceTurnAnimationsTests
{
    static readonly string[] Variables = ["FLT_Aim_Horizontal_Deg_DAMPED", "FLT_Aim_Vertical_Deg_DAMPED"];
    static readonly string[] Events = ["BRANCH_TurnComplete"];
    static readonly string[] Bones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineA"];

    static BlendNode Column(string name, string side) =>
        new(name, [new BlendChildFixture(-80, new ClipNode(side + "Down", side + "_Down")), new BlendChildFixture(0, new ClipNode(side + "Mid", side + "_Mid")), new BlendChildFixture(80, new ClipNode(side + "Up", side + "_Up"))], ParameterVariable: 1);

    static BlendNode Grid() => new("Idle Blend",
    [
        new BlendChildFixture(77, Column("Aim Right Blend", "R")),
        new BlendChildFixture(0, Column("Aim Frwd Blend", "F")),
        new BlendChildFixture(-60, Column("Aim Left Blend", "L")),
    ], ParameterVariable: 0);

    // As the install's: the sequencer is the state, the turn clip a direct child of the turn blend (legs mask), the grid the other child (empty mask).
    static SequencerNode Turn(string side, string clip) => new($"Turn {side} Sequence", new BlendNode($"Turn {side} Blend",
    [
        new BlendChildFixture(1, new ClipNode(clip, clip), [1f, 1f, 0f]),
        new BlendChildFixture(1, Grid(), [0f, 0f, 0f]),
    ], Flags: 0), [new Tag("Branch", 0.9773f, 1f, 0)]);

    static StructuredGraph BraceGraph()
    {
        var idle = new StateMachineNode("Brace Idle Behavior",
        [
            new StateFixture(0, "Idle", Grid(),
            [
                new TransitionFixture(2, Priority: 2, BlendDuration: 0.1f, Condition: new ThresholdFixture(0, 77, 180)),
                new TransitionFixture(1, Priority: 1, BlendDuration: 0.1f, Condition: new ThresholdFixture(0, -180, -60)),
            ]),
            new StateFixture(1, "Turn Left", Turn("Left", "Brace_TurnLeft")),
            new StateFixture(2, "Turn Right", Turn("Right", "Brace_TurnRight")),
        ]);
        return new StructuredGraph("MP_Test_Brace", Events, Variables, idle);
    }

    static HavokExtractedMotion TurnMotion(float sign) =>
        new(new Vector3(0, 0, 1), new Vector3(0, 1, 0), 1.467f, Enumerable.Range(0, 45).Select(i => new Vector4(0, 0, 0, sign * 112.6f * MathF.PI / 180f * i / 44f)).ToArray());

    static HavokExtractedMotion? Motion(string clip) => clip switch { "Brace_TurnLeft" => TurnMotion(1f), "Brace_TurnRight" => TurnMotion(-1f), _ => null };

    static readonly TurnAnimations.PostureMap Map = new(HipFireWeaponClass.Heavy, true, "MP_Test_Brace", "Idle", "Turn Left", "Turn Right");

    static TurnAnimations Load() =>
        TurnAnimations.From(HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure([BraceGraph()]))), Bones, clip => clip.Contains("Turn") ? 1.467f : 2.533f, Motion, [], [], Map);

    [Fact]
    public void The_brace_posture_has_the_graphs_grid_and_a_threshold_per_side()
    {
        var turns = Load();
        var brace = Assert.IsType<TurnPosture>(turns.Brace);
        Assert.Empty(turns.Sets);   // the brace is not a navigation posture
        Assert.Equal([-60f, 0f, 77f], brace.Idle.Columns.Select(c => c.HorizontalDegrees));
        Assert.Equal(["L_Down", "L_Mid", "L_Up"], brace.Idle.Columns[0].Pitches.Select(p => p.Clip));
        Assert.False(brace.Idle.ProceduralTwist);
        Assert.Equal(60f, brace.LeftEnter);
        Assert.Equal(77f, brace.RightEnter);
        Assert.Equal(60f, brace.EnterDegrees);   // the smaller, as the navigation postures read it
        Assert.Equal(0.1f, brace.TurnBlend, 1e-5f);
    }

    [Fact]
    public void The_brace_turn_clips_sit_in_the_turn_blend_and_end_at_BRANCH_TurnComplete()
    {
        var brace = Load().Brace!;
        var left = Assert.IsType<TurnClip>(brace.Left);
        var right = Assert.IsType<TurnClip>(brace.Right);
        Assert.Equal("Brace_TurnLeft", left.Clip);
        Assert.Equal("Brace_TurnRight", right.Clip);
        Assert.Equal(1.467f, left.Length, 1e-4f);
        Assert.Equal(0.9773f, left.EndFraction, 1e-4f);
        Assert.Equal(112.6f, left.YawDegrees, 0.1f);
        Assert.Equal(-112.6f, right.YawDegrees, 0.1f);
        Assert.Equal(1f, left.LegMask!["RIG_Pelvis"]);
        Assert.Equal(0f, left.LegMask["RIG_SpineA"]);
        // At the branch (0.9773 x 1.467 s = 1.434 s) the root has turned 110 degrees.
        Assert.Equal(110f, left.YawAt(0.9773f * 1.467f), 0.3f);
    }

    [Fact]
    public void Navigation_postures_with_equal_sides_keep_one_threshold()
    {
        var idle = new StateMachineNode("Postures",
        [
            new StateFixture(0, "Idle", Grid(), [new TransitionFixture(1, Condition: new ThresholdFixture(0, -180, -75)), new TransitionFixture(2, Condition: new ThresholdFixture(0, 75, 180))]),
            new StateFixture(1, "Turn Left", Turn("Left", "Brace_TurnLeft")),
            new StateFixture(2, "Turn Right", Turn("Right", "Brace_TurnRight")),
        ]);
        var graph = new StructuredGraph("MP_Test_Brace", Events, Variables, idle);
        var turns = TurnAnimations.From(HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure([graph]))), Bones, _ => 1.467f, Motion, [], [], Map);
        var brace = turns.Brace!;
        Assert.Null(brace.EnterLeftDegrees);
        Assert.Null(brace.EnterRightDegrees);
        Assert.Equal(75f, brace.LeftEnter);
        Assert.Equal(75f, brace.RightEnter);
    }

    [Fact]
    public void Without_a_brace_map_the_test_postures_read_no_brace()
    {
        var turns = TurnAnimations.From(HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure([BraceGraph()]))), Bones, _ => 1.467f, Motion, [], []);
        Assert.Null(turns.Brace);
        Assert.Empty(turns.Notes);
    }
}
