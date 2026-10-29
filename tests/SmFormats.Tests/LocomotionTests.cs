using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M36: the relaxed run (eight directions, walk and run clips, back included) and the sprint loops (a three-way blend over the movement
/// angle) read from behaviour graphs, and the weapon-to-sprint-family mapping. Synthetic packfiles only.
/// </summary>
public class LocomotionTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static readonly string[] NavVariables = ["FLT_Nav_Motion_Angle_DAMPED", "FLT_Input_Nav_Speed_DAMPED"];

    static BlendNode SpeedBlend(string dir, float run) => new($"Nav Relaxed {dir}",
        [new BlendChildFixture(0, new ClipNode("w0", "SM_WOS_F")), new BlendChildFixture(2, new ClipNode("w", $"SM_Walk_{dir}")), new BlendChildFixture(run, new ClipNode("r", $"SM_Run_{dir}"))],
        ParameterVariable: 1);

    static StructuredGraph NavGraph(string name = "SM_Test_NAV")
    {
        var back = SpeedBlend("B", 4.5f);
        var directions = new BlendNode("Nav Relaxed Blend",
            [new BlendChildFixture(-180, back), new BlendChildFixture(-90, SpeedBlend("L", 4.5f)), new BlendChildFixture(0, SpeedBlend("F", 6.5f)),
             new BlendChildFixture(90, SpeedBlend("R", 4.5f)), new BlendChildFixture(180, back)], ParameterVariable: 0, Flags: 0x31);
        var relaxed = new ModifierGeneratorNode("Run Relaxed Gen", new SequencerNode("Seq", new BlendNode("Incline", [new BlendChildFixture(0, directions)]), []));
        var machine = new StateMachineNode("Run", [new StateFixture(0, "Idle Relaxed", new ClipNode("i", "SM_Idle")), new StateFixture(2, "Run Relaxed", relaxed)]);
        return new StructuredGraph(name, [], NavVariables, machine);
    }

    static StructuredGraph SprintGraph(string name, string prefix)
    {
        var blend = new BlendNode("Sprint Blend",
            [new BlendChildFixture(45, new ClipNode("r", $"{prefix}_Strafe_R")), new BlendChildFixture(-45, new ClipNode("l", $"{prefix}_Strafe_L")),
             new BlendChildFixture(0, new ClipNode("f", $"{prefix}_F"))], ParameterVariable: 0, Flags: 0x11);
        var machine = new StateMachineNode("Sprint", [new StateFixture(1, "Transition To Idle", new ClipNode("t", $"{prefix}_Start")), new StateFixture(2, "Sprint Loop", new ModifierGeneratorNode("m", blend))]);
        return new StructuredGraph(name, [], ["FLT_Nav_Motion_Angle_SPRINT_DAMP"], machine);
    }

    static StructuredGraph BaseGraph()
    {
        var machine = new StateMachineNode("TestBehavior",
        [
            new StateFixture(1, "Test Navigation", new ClipNode("n", "SM_N"), [new TransitionFixture(4, BlendDuration: 0.1f)]),
            new StateFixture(4, "Test Sprint", new ClipNode("s", "SM_S")),
        ]);
        return new StructuredGraph(HipFireAnimations.BaseGraph, [], [], new StateMachineNode("Base", [new StateFixture(0, "Ranged - Test", machine)]));
    }

    static readonly LocomotionAnimations.RelaxedMap RifleMap = new(HipFireWeaponClass.Rifle, "SM_Test_NAV", "Run Relaxed");
    static readonly LocomotionAnimations.SprintMap RifleSprint = new(SprintFamily.Rifle, "SM_Test_Sprint", "Sprint Loop", "TestBehavior", "Test Navigation", "Test Sprint");

    [Fact]
    public void The_relaxed_run_has_a_walk_and_a_run_clip_per_direction_back_included()
    {
        var loco = LocomotionAnimations.From(Read(NavGraph()), clip => clip.Contains("Run") ? 0.867f : 1.333f, [RifleMap], []);
        Assert.Empty(loco.Notes);
        var set = Assert.Single(loco.Relaxed);
        Assert.Same(set, loco.RelaxedFor(HipFireWeaponClass.Rifle));
        Assert.Null(loco.RelaxedFor(HipFireWeaponClass.Heavy));
        // The cyclic blend lists the back clips at -180 and +180: kept once.
        Assert.Equal([-180f, -90f, 0f, 90f], set.Directions.Select(d => d.AngleDegrees));
        Assert.Equal(["B", "L", "F", "R"], set.Directions.Select(d => d.Label));
        var back = set.Directions[0];
        Assert.Equal(new LocoBand(4.5f, "SM_Run_B", 0.867f, false), back.Run);
        Assert.Equal(new LocoBand(2f, "SM_Walk_B", 1.333f, false), back.Walk);   // the walk-on-the-spot band (speed 0) is not the walk
        Assert.Equal(6.5f, set.Directions[2].Run!.Speed);
    }

    [Fact]
    public void The_speed_zero_band_is_the_walk_on_the_spot_clip_of_each_direction()
    {
        var loco = LocomotionAnimations.From(Read(NavGraph()), clip => clip.Contains("WOS") ? 1.333f : 0.867f, [RifleMap], []);
        var set = loco.Relaxed[0];
        Assert.All(set.Directions, d => Assert.Equal(new LocoBand(0f, "SM_WOS_F", 1.333f, false), d.WalkOnSpot));
        // Without a speed blend there is none, and the moving bands are unchanged.
        Assert.Null(new LocoDirection(0f, null, null).WalkOnSpot);
    }

    [Fact]
    public void A_sprint_is_a_three_way_blend_over_the_angle_with_the_navigation_to_sprint_blend()
    {
        var loco = LocomotionAnimations.From(Read(SprintGraph("SM_Test_Sprint", "SM_Spr"), BaseGraph()), _ => 0.633f, [], [RifleSprint]);
        Assert.Empty(loco.Notes);
        var sprint = Assert.Single(loco.Sprints);
        Assert.Same(sprint, loco.SprintFor(SprintFamily.Rifle));
        Assert.Null(loco.SprintFor(SprintFamily.Heavy));
        Assert.Equal([new SprintClip(-45f, "SM_Spr_Strafe_L", 0.633f), new SprintClip(0f, "SM_Spr_F", 0.633f), new SprintClip(45f, "SM_Spr_Strafe_R", 0.633f)], sprint.Clips);
        Assert.Equal(0.1f, sprint.EnterBlend);
        Assert.Null(sprint.PlaybackVariable);   // the synthetic clips have no bound playback speed
    }

    [Fact]
    public void A_sprint_without_a_base_graph_transition_uses_the_default_blend_and_missing_data_is_noted()
    {
        var loco = LocomotionAnimations.From(Read(SprintGraph("SM_Test_Sprint", "SM_Spr")), _ => 1f,
            [RifleMap with { Graph = "Missing" }, RifleMap with { State = "No such state" }],
            [RifleSprint, RifleSprint with { Graph = "Gone" }, RifleSprint with { State = "Nope" }]);
        Assert.Equal(LocomotionAnimations.DefaultEnterBlend, Assert.Single(loco.Sprints).EnterBlend);
        Assert.Empty(loco.Relaxed);
        Assert.Contains(loco.Notes, n => n.Contains("graph Missing not found"));
        Assert.Contains(loco.Notes, n => n.Contains("graph Gone not found"));
        Assert.Contains(loco.Notes, n => n.Contains("no state 'Nope'"));
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", SprintFamily.Rifle)]
    [InlineData("sm_mp_pistol_bolt_pistol", SprintFamily.Rifle)]
    [InlineData("sm_mp_exotic_plasma_gun", SprintFamily.Rifle)]
    [InlineData("sm_mp_exotic_melta_gun", SprintFamily.Rifle)]
    [InlineData("sm_mp_exotic_heavy_bolter", SprintFamily.Heavy)]
    [InlineData("sm_mp_exotic_las_cannon", SprintFamily.Cannon)]
    [InlineData("sm_mp_exotic_plasma_cannon", SprintFamily.Cannon)]
    public void Heavy_bolter_and_the_cannons_have_their_own_sprint_graphs(string weapon, SprintFamily family) =>
        Assert.Equal(family, LocomotionAnimations.SprintFamilyOf(weapon));
}
