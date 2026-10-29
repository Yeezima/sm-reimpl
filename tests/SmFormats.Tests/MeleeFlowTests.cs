using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// anim/melee-flow: the blends a melee weapon's graph puts into, between and out of its strikes (<see cref="MeleeFlowBlends"/>), and the hammer's own navigation
/// (<see cref="LocomotionAnimations.WithHammer"/>), read from synthetic packfiles.
/// </summary>
public class MeleeFlowTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    // Events: 0 BRANCH_Melee_Combo, 1 BRANCH_Melee_End, 2 BRANCH_Melee_Run, 3 BRANCH_To_Slam. Variables: 0 FLT_Input_Nav_Speed.
    static StructuredGraph MeleeGraph()
    {
        static SequencerNode Seq(string name, string clip) => new(name, new ClipNode(name + " clip", clip, Mode: 0), []);
        var attack = new StateMachineNode("Test Attack Behavior",
        [
            new StateFixture(17, "ENTRY - Test Attack Chain", new ClipNode("conduit", "SM_Ref"), [new TransitionFixture(3, BlendDuration: 0.167f), new TransitionFixture(44, BlendDuration: 0.1f)]),
            new StateFixture(18, "ENTRY - Test Attack Chain From Stowed", new ClipNode("conduit2", "SM_Ref"), [new TransitionFixture(3, BlendDuration: 0.1f)]),
            new StateFixture(3, "Test Default 1 ( X )", Seq("Seq_X1", "SM_MEL_X1"), [new TransitionFixture(4, BranchEventId: 0, BlendDuration: 0.1f), new TransitionFixture(45, BranchEventId: 0)]),
            new StateFixture(4, "Test Default 2 ( X - X )", Seq("Seq_X2", "SM_MEL_X2")),
            new StateFixture(44, "Test Combo End 1 ( Y )", Seq("Seq_Y", "SM_MEL_Y"), [new TransitionFixture(52, BranchEventId: 3, BlendDuration: 0.267f)]),
            new StateFixture(45, "Test Combo End 2 ( X , Y )", Seq("Seq_XY", "SM_MEL_XY")),
            new StateFixture(52, "Test Slam ( Y , Y )", Seq("Seq_Slam", "SM_MEL_Slam")),
        ], StartStateId: 18);
        var root = new StateMachineNode("Test Behavior",
        [
            new StateFixture(3, "Test Attack MP", new ModifierGeneratorNode("attack gen", attack),
            [
                new TransitionFixture(4, BranchEventId: 1, BlendDuration: 0.333f),
                new TransitionFixture(4, BranchEventId: 2, BlendDuration: 0.4f, Condition: new ThresholdFixture(0, 0.5f, 100f)),
            ]),
            new StateFixture(4, "Test Nav", new ClipNode("nav", "SM_Nav"), [new TransitionFixture(3, BlendDuration: 0.333f)]),
        ], StartStateId: 4);
        return new StructuredGraph("MP_Test_MEL", ["BRANCH_Melee_Combo", "BRANCH_Melee_End", "BRANCH_Melee_Run", "BRANCH_To_Slam"], ["FLT_Input_Nav_Speed"], root);
    }

    static readonly MeleeTuning.WeaponMap Map = new("test_melee", "MP_Test_MEL", ["Seq_X1", "Seq_X2"], ["", ""])
    {
        HeavySequencers = ["Seq_Y", "Seq_XY"], FinisherSequencers = ["Seq_Slam"],
    };

    [Fact]
    public void Entries_come_from_the_attack_chain_conduit_not_the_stowed_one()
    {
        var flow = MeleeFlowBlends.Read(Read(MeleeGraph()), Map)!;
        Assert.Equal(["Test Default 1 ( X )", "Test Default 2 ( X - X )"], flow.LightStates);
        Assert.Equal(["Test Combo End 1 ( Y )", "Test Combo End 2 ( X , Y )", "Test Slam ( Y , Y )"], flow.HeavyStates);
        Assert.Equal(0.167f, flow.EntryOf(false, 0)!.Value, 3);   // 'Attack Chain' over 'From Stowed' (0.1)
        Assert.Equal(0.1f, flow.EntryOf(true, 0)!.Value, 3);
        Assert.Null(flow.EntryOf(false, 1));                      // no entry into the second strike
    }

    [Fact]
    public void Chains_carry_their_transition_blend_or_zero_without_one()
    {
        var flow = MeleeFlowBlends.Read(Read(MeleeGraph()), Map)!;
        Assert.Equal(0.1f, flow.ChainOf(false, 0, false, 1)!.Value, 3);
        Assert.Equal(0f, flow.ChainOf(false, 0, true, 1));         // a transition without a blending effect: the clips run into each other
        Assert.Equal(0.267f, flow.ChainOf(true, 0, true, 2)!.Value, 3);
        Assert.Null(flow.ChainOf(false, 1, false, 0));            // no such transition in the graph
    }

    [Fact]
    public void The_exits_are_the_attack_states_ways_back_to_the_navigation()
    {
        var flow = MeleeFlowBlends.Read(Read(MeleeGraph()), Map)!;
        Assert.Equal(0.333f, flow.ExitEnd!.Value, 3);
        Assert.Equal(0.4f, flow.ExitRun!.Value, 3);   // the move branch with the speed condition
        Assert.Empty(flow.Notes);
        Assert.Contains(flow.Describe(), l => l.StartsWith("exit: played out 0.333 s, moving on 0.4 s"));
    }

    [Fact]
    public void A_missing_graph_gives_null_and_a_missing_strike_a_note()
    {
        Assert.Null(MeleeFlowBlends.Read(Read(MeleeGraph()), Map with { Graph = "Gone" }));
        var flow = MeleeFlowBlends.Read(Read(MeleeGraph()), Map with { Sequencers = ["Seq_X1", "Seq_Missing"] })!;
        Assert.Null(flow.LightStates[1]);
        Assert.Contains(flow.Notes, n => n.Contains("no state found for strike 1"));
    }

    static StructuredGraph HammerNav(bool withIdle = true)
    {
        static BlendNode Speed(string dir, float run) => new($"Nav {dir}",
            [new BlendChildFixture(2, new ClipNode("w", $"SM_TH_Walk_{dir}")), new BlendChildFixture(run, new ClipNode("r", $"SM_TH_Run_{dir}"))], ParameterVariable: 1);
        var directions = new BlendNode("Flat Terrain Blend",
            [new BlendChildFixture(-180, Speed("B", 4.5f)), new BlendChildFixture(0, Speed("F", 5.5f)), new BlendChildFixture(180, Speed("B", 4.5f))], ParameterVariable: 0, Flags: 0x31);
        var states = new List<StateFixture> { new(1, "Run", new ModifierGeneratorNode("run gen", new SequencerNode("Seq", directions, []))) };
        if (withIdle) states.Insert(0, new StateFixture(0, "Idle", new SequencerNode("Seq idle", new ClipNode("idle", "SM_TH_Idle"), [])));
        return new StructuredGraph("SM_TH_NAV", [], ["FLT_Nav_Motion_Angle_DAMPED", "FLT_Input_Nav_Speed_DAMPED"], new StateMachineNode("Stand Relaxed Behavior", states.ToArray()));
    }

    [Fact]
    public void The_hammer_has_its_own_run_and_idle()
    {
        var empty = new LocomotionAnimations([], [], []);
        var loco = LocomotionAnimations.WithHammer(empty, Read(HammerNav()), clip => clip.Contains("Run") ? 0.867f : 1.333f);
        Assert.Equal("SM_TH_Idle", loco.HammerIdle);
        var set = loco.Hammer!;
        Assert.Equal([-180f, 0f], set.Directions.Select(d => d.AngleDegrees));
        Assert.Equal(new LocoBand(5.5f, "SM_TH_Run_F", 0.867f, false), set.Directions[1].Run);
        Assert.Equal(new LocoBand(2f, "SM_TH_Walk_F", 1.333f, false), set.Directions[1].Walk);
        Assert.Empty(loco.Notes);
        Assert.Empty(loco.Relaxed);   // the weapon families' relaxed sets are untouched
    }

    [Fact]
    public void Without_the_hammer_graph_or_its_idle_the_data_is_noted()
    {
        var empty = new LocomotionAnimations([], [], []);
        var none = LocomotionAnimations.WithHammer(empty, Read(MeleeGraph()), _ => 1f);
        Assert.Null(none.Hammer);
        Assert.Null(none.HammerIdle);
        Assert.Contains(none.Notes, n => n.StartsWith("hammer navigation:"));
        var noIdle = LocomotionAnimations.WithHammer(empty, Read(HammerNav(withIdle: false)), _ => 1f);
        Assert.NotNull(noIdle.Hammer);
        Assert.Null(noIdle.HammerIdle);
        Assert.Contains(noIdle.Notes, n => n.Contains("no 'Idle' clip"));
    }

    [Fact]
    public void The_hammer_sprint_is_a_family_of_its_own_in_the_default_maps()
    {
        var map = Assert.Single(LocomotionAnimations.DefaultSprints, m => m.Family == SprintFamily.Hammer);
        Assert.Equal("SM_TH_NuSprint", map.Graph);
        Assert.Null(map.TransitionGraph);   // its 0.1 s entry ('Blend Three Frame' in the hammer graph) is the default blend
        // The weapon names never pick it: the hammer stance is the client's choice (a marine with the hammer whose pistol is down).
        Assert.NotEqual(SprintFamily.Hammer, LocomotionAnimations.SprintFamilyOf("sm_mp_melee_2hand_thunder_hammer"));
    }
}
