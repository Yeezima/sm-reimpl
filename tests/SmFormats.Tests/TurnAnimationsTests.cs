using System.Numerics;
using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M40-T5: the turn-in-place, aim-offset idle and run-start data read from behaviour graphs (<see cref="TurnAnimations"/>), the start clip's distance functions and the
/// start-to-run pose match (<see cref="StartMatch"/>). Synthetic packfiles and numbers only.
/// </summary>
public class TurnAnimationsTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    // Variables: 0 aim horizontal, 1 aim vertical, 2 requested speed.
    static readonly string[] Variables = ["FLT_Aim_Horizontal_Deg_DAMPED", "FLT_Aim_Vertical_Deg_DAMPED", "FLT_Input_Nav_Speed_LOGIC"];
    static readonly string[] Events = ["BRANCH_Turn_End", "BRANCH_Idle_To_Run_End"];
    static readonly string[] Bones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineA"];

    static BlendNode Column(string name, params (float Pitch, string Clip)[] clips) =>
        new(name, clips.Select(c => new BlendChildFixture(c.Pitch, new ClipNode(c.Clip, c.Clip))).ToArray(), ParameterVariable: 1);

    static BlendNode IdleGrid() => new("Idle Blend",
    [
        new BlendChildFixture(-90, Column("Lft", (-80, "L_Down"), (0, "L_Mid"), (40, "L_FwdUp"), (80, "L_Up"))),
        new BlendChildFixture(0, Column("Fwd", (-80, "F_Down"), (0, "F_Mid"), (80, "F_Up"))),
        new BlendChildFixture(90, Column("Rgt", (-80, "R_Down"), (0, "R_Mid"), (80, "R_Up"))),
    ], ParameterVariable: 0);

    static BlendNode TurnState(string name, string clip) => new(name,
    [
        new BlendChildFixture(1, new SequencerNode("SEQ_" + clip, new ClipNode(clip, clip, Mode: 0), [new Tag("Branch", 0.92f, 1f, 0)]), [1f, 1f, 0f]),
        new BlendChildFixture(1, IdleGrid(), [0f, 0f, 1f]),
    ], Flags: 0);

    static StructuredGraph NavGraph(float? startSpeed = 3f)
    {
        var postures = new StateMachineNode("Postures",
        [
            new StateFixture(0, "Idle Relaxed Point", IdleGrid(),
            [
                new TransitionFixture(2, BlendDuration: 0.1f, Condition: new ThresholdFixture(0, -180, -90)),
                new TransitionFixture(3, BlendDuration: 0.1f, Condition: new ThresholdFixture(0, 90, 180)),
            ]),
            new StateFixture(2, "Idle Relaxed Turn Left", TurnState("Idle Turn Left", "SM_Turn_Left")),
            new StateFixture(3, "Idle Relaxed Turn Right", TurnState("Idle Turn Right", "SM_Turn_Right")),
        ]);
        var motion = new StateMachineNode("Motion",
        [
            new StateFixture(0, "Idle Relaxed", postures,
                [new TransitionFixture(2, BlendDuration: 0.333f, Condition: new ThresholdFixture(2, startSpeed ?? 3f, 10000f)), new TransitionFixture(1, BlendDuration: 0.4f, Condition: new ThresholdFixture(2, 0.5f, 1000f))]),
            new StateFixture(1, "Run Relaxed", new ClipNode("run", "SM_Run"), [new TransitionFixture(0, BlendDuration: 0.25f, Condition: new ThresholdFixture(2, -10f, 0.5f))]),
            new StateFixture(2, "Idle Relaxed To Run Relaxed", new SequencerNode("Start", new ClipNode("s", "SM_Start", Mode: 0), [new Tag("Branch", 1f, 1f, 1)]),
                [new TransitionFixture(1, BranchEventId: 1, BlendDuration: 0.1f), new TransitionFixture(0, BlendDuration: 0.3f, Condition: new ThresholdFixture(2, -10f, 0.5f))]),
        ]);
        var root = new StateMachineNode("Root", [new StateFixture(0, "Stand", motion), new StateFixture(1, "Other", new ClipNode("x", "SM_X"))]);
        return new StructuredGraph("SM_Test_NAV", Events, Variables, root);
    }

    static HavokExtractedMotion TurnMotion(float sign) =>
        new(new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0.833f, Enumerable.Range(0, 26).Select(i => new Vector4(0, 0, 0, sign * 2.356f * i / 25f)).ToArray());

    static HavokExtractedMotion StartMotion() =>
        new(new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0.7f, Enumerable.Range(0, 22).Select(i => new Vector4(0, 4.43f * MathF.Pow(i / 21f, 2f), 0, 0)).ToArray());

    static HavokExtractedMotion? Motion(string clip) => clip switch { "SM_Turn_Left" => TurnMotion(1f), "SM_Turn_Right" => TurnMotion(-1f), "SM_Start" => StartMotion(), _ => null };

    static readonly TurnAnimations.PostureMap Posture = new(HipFireWeaponClass.Rifle, false, "SM_Test_NAV", "Idle Relaxed Point", "Idle Relaxed Turn Left", "Idle Relaxed Turn Right", "Run Relaxed", "Idle Relaxed");
    static readonly TurnAnimations.StartMap Start = new(HipFireWeaponClass.Rifle, false, "SM_Test_NAV", "Idle Relaxed", "Idle Relaxed To Run Relaxed", "Run Relaxed");

    static TurnAnimations Load(StructuredGraph? graph = null) =>
        TurnAnimations.From(Read(graph ?? NavGraph()), Bones, clip => clip.StartsWith("SM_Turn") ? 0.833f : clip == "SM_Start" ? 0.7f : 1.5f, Motion, [Posture], [Start]);

    [Fact]
    public void The_standing_aim_offset_idle_is_the_horizontal_blend_of_pitch_columns()
    {
        var turns = Load();
        Assert.Empty(turns.Notes);
        var set = Assert.Single(turns.Sets);
        Assert.Same(set, turns.For(HipFireWeaponClass.Rifle));
        Assert.Null(turns.For(HipFireWeaponClass.Heavy));
        var posture = Assert.IsType<TurnPosture>(set.Relaxed);
        Assert.Null(set.Ready);
        Assert.Equal([-90f, 0f, 90f], posture.Idle.Columns.Select(c => c.HorizontalDegrees));
        Assert.Equal([-80f, 0f, 40f, 80f], posture.Idle.Columns[0].Pitches.Select(p => p.PitchDegrees));
        Assert.Equal(["F_Down", "F_Mid", "F_Up"], posture.Idle.Columns[1].Pitches.Select(p => p.Clip));
        Assert.False(posture.Idle.ProceduralTwist);
    }

    [Fact]
    public void The_turn_threshold_the_blends_and_the_turn_clips_come_from_the_graph()
    {
        var posture = Load().Sets[0].Relaxed!;
        Assert.Equal(90f, posture.EnterDegrees);
        Assert.Equal(0.1f, posture.TurnBlend, 1e-5f);
        Assert.Equal(0.25f, posture.StopBlend, 1e-5f);   // Run Relaxed -> Idle Relaxed
        var left = Assert.IsType<TurnClip>(posture.Left);
        var right = Assert.IsType<TurnClip>(posture.Right);
        Assert.Equal("SM_Turn_Left", left.Clip);
        Assert.Equal(0.833f, left.Length, 1e-4f);
        Assert.Equal(135f, left.YawDegrees, 0.1f);
        Assert.Equal(-135f, right.YawDegrees, 0.1f);
        Assert.Equal(0.92f, left.EndFraction, 1e-5f);
        // The turn child's mask: the root and pelvis at 1, the spine at 0.
        Assert.Equal(1f, left.LegMask!["RIG_Pelvis"]);
        Assert.Equal(0f, left.LegMask["RIG_SpineA"]);
        // The root rotation over the clip: linear for this synthetic one.
        Assert.Equal(0f, left.YawAt(0f), 0.01f);
        Assert.Equal(67.5f, left.YawAt(0.4165f), 0.5f);
        Assert.Equal(135f, left.YawAt(5f), 0.1f);
    }

    [Fact]
    public void A_pitch_only_idle_without_a_horizontal_blend_is_one_column_and_a_twist_state_name_marks_the_procedural_twist()
    {
        var twist = new StateMachineNode("Postures", [new StateFixture(0, "Twist Idle", Column("Pitch", (-80, "P_Down"), (0, "P_Mid"), (80, "P_Up")), [new TransitionFixture(1, Condition: new ThresholdFixture(0, -180, -75))]),
            new StateFixture(1, "Idle Turn Left", TurnState("Idle Turn Left", "SM_Turn_Left"))]);
        var graph = new StructuredGraph("SM_Test_NAV", Events, Variables, twist);
        var map = new TurnAnimations.PostureMap(HipFireWeaponClass.Pistol, true, "SM_Test_NAV", "Twist Idle", "Idle Turn Left", "Idle Turn Right");
        var turns = TurnAnimations.From(Read(graph), Bones, _ => 1.5f, Motion, [map], []);
        var posture = Assert.IsType<TurnPosture>(turns.For(HipFireWeaponClass.Pistol)!.Ready);
        Assert.Equal(75f, posture.EnterDegrees);   // the pistol turns at 75 degrees
        var column = Assert.Single(posture.Idle.Columns);
        Assert.Equal(3, column.Pitches.Count);
        Assert.True(posture.Idle.ProceduralTwist);
        Assert.Null(posture.Right);
        Assert.Contains(turns.Notes, n => n.Contains("no turn states"));
    }

    [Fact]
    public void The_run_start_has_its_speed_blends_and_root_distance_from_the_graph()
    {
        var start = Load().Sets[0].RelaxedStart!;
        Assert.Equal("SM_Start", start.Clip);
        Assert.Equal(0.7f, start.Length, 1e-4f);
        Assert.Equal(3f, start.EnterSpeed);
        Assert.Equal(0.333f, start.BlendIn, 1e-4f);
        Assert.Equal(0.1f, start.BlendOut, 1e-4f);
        Assert.Equal(0.3f, start.BlendAbort, 1e-4f);
        Assert.Equal(1f, start.EndFraction);
        Assert.Equal(4.43f, start.RootForward[^1], 1e-3f);
    }

    [Fact]
    public void Missing_graphs_states_and_motion_are_noted_not_thrown()
    {
        var turns = TurnAnimations.From(Read(NavGraph()), Bones, _ => 1f, _ => null,
            [Posture with { Graph = "Gone" }, Posture with { IdleState = "Nope" }, Posture with { LeftState = "NoLeft", RightState = "NoRight" }],
            [Start with { Graph = "Gone" }, Start with { StartState = "Nope" }, Start]);
        Assert.Contains(turns.Notes, n => n.Contains("graph Gone not found"));
        Assert.Contains(turns.Notes, n => n.Contains("no state 'Nope'"));
        Assert.Contains(turns.Notes, n => n.Contains("no extracted motion"));   // the start clip without motion
        Assert.Contains(turns.Notes, n => n.Contains("no turn states"));
        Assert.Null(turns.Sets.FirstOrDefault()?.RelaxedStart);
    }

    [Fact]
    public void A_start_clip_is_driven_by_distance_both_ways_and_holds_at_its_ends()
    {
        var start = Load().Sets[0].RelaxedStart!;   // distance = 4.43 * (t / 0.7)^2
        Assert.Equal(0f, start.DistanceAt(-1f));
        Assert.Equal(4.43f, start.DistanceAt(9f), 1e-3f);
        Assert.Equal(4.43f * 0.25f, start.DistanceAt(0.35f), 0.02f);
        Assert.Equal(0f, start.TimeAtDistance(0f));
        Assert.Equal(0f, start.TimeAtDistance(-3f));
        Assert.Equal(0.7f, start.TimeAtDistance(99f), 1e-4f);
        Assert.Equal(0.35f, start.TimeAtDistance(4.43f * 0.25f), 0.01f);
        // Round trip, and a monotonic mapping.
        float last = -1f;
        for (float d = 0f; d <= 4.43f; d += 0.1f)
        {
            float time = start.TimeAtDistance(d);
            Assert.True(time >= last);
            last = time;
            Assert.Equal(d, start.DistanceAt(time), 0.03f);
        }
    }

    [Fact]
    public void The_start_to_run_match_picks_the_cycle_pose_with_the_nearest_toes()
    {
        var candidates = new List<(Vector3, Vector3)>();
        for (int i = 0; i < 10; i++) candidates.Add((new Vector3(-0.2f, 0.5f - 0.1f * i, 0f), new Vector3(0.2f, -0.5f + 0.1f * i, 0f)));
        Assert.Equal(3, StartMatch.Nearest(candidates, (new Vector3(-0.2f, 0.2f, 0f), new Vector3(0.2f, -0.2f, 0f))));
        Assert.Equal(0, StartMatch.Nearest(candidates, (new Vector3(-0.2f, 0.9f, 0f), new Vector3(0.2f, -0.9f, 0f))));
        // A foot in the air against one on the ground counts for more than a few centimetres along the ground.
        var swing = new List<(Vector3, Vector3)> { (new Vector3(0, 0.30f, 0.30f), new Vector3(0.2f, 0f, 0f)), (new Vector3(0, 0.0f, 0.00f), new Vector3(0.2f, 0f, 0f)) };
        Assert.Equal(1, StartMatch.Nearest(swing, (new Vector3(0, 0.25f, 0f), new Vector3(0.2f, 0f, 0f))));
        Assert.Equal(-1, StartMatch.Nearest([], (Vector3.Zero, Vector3.Zero)));
    }
}
