using System.Numerics;
using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M27: behaviour graph structure (state machines, transitions, conditions, blends, bone masks, bindings), the animation
/// binding's blend hint, and the hip-fire data built from them. Synthetic packfiles only.
/// </summary>
public class HipFireTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    [Fact]
    public void State_machines_transitions_conditions_and_choosers_are_read()
    {
        string[] events = ["BTN_Ranged", "BRANCH_Shot_Finished", "BRANCH_to_Fire"];
        string[] variables = ["BOOL_IsReady", "BOOL_IsAiming", "FLT_Speed"];
        var a = new ClipNode("A", "SM_A");
        var b = new ClipNode("B", "SM_B", PlaybackSpeed: 0.5f, Mode: 0);
        var condition = new MultiFixture(0, [new NegateFixture(new BoolFixture([1], [])), new MultiFixture(1, [new BoolFixture([0], [2]), new ThresholdFixture(2, 0.5f, 1000f)])]);
        var machine = new StateMachineNode("Machine",
        [
            new StateFixture(3, "Nav", a, [new TransitionFixture(5, EventId: 0, Priority: 2, BlendDuration: 0.1f, EffectName: "Nav to Fire")]),
            new StateFixture(5, "Fire", b, [new TransitionFixture(3, BranchEventId: 1, Priority: 1, Condition: condition)]),
        ], StartStateId: 3, Wildcards: [new TransitionFixture(5, Condition: new ExpressionFixture("BOOL_IsAiming == 1"))],
        Chooser: [(5, new BoolFixture([0], [])), (3, null)]);

        var g = Assert.Single(Read(new StructuredGraph("G", events, variables, machine)));
        Assert.Equal("G", g.Name);
        Assert.Equal(variables, g.Variables);
        var sm = Assert.Single(g.StateMachines);
        Assert.Equal(g.Root, sm.Offset);
        Assert.Equal("Machine", sm.Name);
        Assert.Equal(3, sm.StartStateId);
        Assert.Equal([3, 5], sm.States.Select(s => s.Id));
        Assert.Equal(["Nav", "Fire"], sm.States.Select(s => s.Name));

        var toFire = Assert.Single(sm.States[0].Transitions);
        Assert.Equal(("BTN_Ranged", 5, (short)2, (string?)null), (toFire.Event, toFire.ToStateId, toFire.Priority, toFire.BranchEvent));
        var effect = g.EffectAt(toFire.Transition);
        Assert.Equal(("Nav to Fire", 0.1f), (effect!.Name, effect.Duration));

        var back = Assert.Single(sm.States[1].Transitions);
        Assert.Null(back.Event);
        Assert.Equal(-1, back.EventId);
        Assert.Equal("BRANCH_Shot_Finished", back.BranchEvent);
        Assert.Null(g.EffectAt(back.Transition));
        Assert.Equal("all[not BOOL_IsAiming, any[(BOOL_IsReady & !FLT_Speed), (FLT_Speed in [0.5, 1000])]]", back.ConditionText);

        Assert.Equal("(BOOL_IsAiming == 1)", Assert.Single(sm.WildcardTransitions).ConditionText);
        Assert.Equal([(5, (string?)"BOOL_IsReady"), (3, null)], sm.StartChoices);

        var clips = g.Clips.OrderBy(c => c.Animation).ToList();
        Assert.Equal(("SM_A", 1f, 1), (clips[0].Animation, clips[0].PlaybackSpeed, clips[0].Mode));
        Assert.Equal(("SM_B", 0.5f, 0), (clips[1].Animation, clips[1].PlaybackSpeed, clips[1].Mode));
        // The generator tree links each state to its generator.
        Assert.Equal(["state 3 'Nav'", "state 5 'Fire'"], g.Nodes[sm.Offset].Children.Select(c => c.Label));
    }

    [Fact]
    public void Blends_weights_bound_parameters_and_bone_masks_are_read()
    {
        var lower = new[] { 1f, 1f, 0f, 0f };
        var upper = new[] { 0f, 0f };
        var pitch = new BlendNode("Pitch", [new BlendChildFixture(-80, new ClipNode("D", "SM_Down")), new BlendChildFixture(80, new ClipNode("U", "SM_Up"))],
            ParameterVariable: 1, BlendParameter: 3f, Flags: 0x31);
        var layers = new BlendNode("Layers", [new BlendChildFixture(1, new ClipNode("Legs", "SM_Legs"), lower), new BlendChildFixture(1, pitch, upper)], Flags: 0);
        var g = Assert.Single(Read(new StructuredGraph("G", [], ["FLT_X", "FLT_Aim_Vertical_Deg_DAMPED"], layers)));

        var p = g.Blenders.Single(b => b.Name == "Pitch");
        Assert.Equal((3f, (short)0x31, false), (p.BlendParameter, p.Flags, p.SubtractLastChild));
        Assert.Equal([-80f, 80f], p.Children.Select(c => c.Weight));
        Assert.All(p.Children, c => Assert.Null(c.BoneWeights));
        Assert.Equal("FLT_Aim_Vertical_Deg_DAMPED", g.Nodes[p.Offset].Bindings["blendParameter"]);

        var l = g.Blenders.Single(b => b.Name == "Layers");
        Assert.Empty(g.Nodes[l.Offset].Bindings);
        var lowerMask = g.BoneWeightsAt(l.Children[0].BoneWeights)!;
        var upperMask = g.BoneWeightsAt(l.Children[1].BoneWeights)!;
        Assert.Equal(lower, lowerMask.Weights);
        Assert.Equal(upper, upperMask.Weights);
        // Bones past a mask's end weigh 1: the short upper mask covers the whole upper body.
        Assert.Equal([0f, 0f, 1f, 1f, 1f], Enumerable.Range(0, 5).Select(upperMask.WeightOf));
        Assert.Equal(2, g.MaskedChildren.Count());
        Assert.Equal(["weight 1 mask @" + l.Children[0].BoneWeights, "weight 1 mask @" + l.Children[1].BoneWeights],
            g.Nodes[l.Offset].Children.Select(c => c.Label));
    }

    [Fact]
    public void A_state_machine_that_contains_itself_is_read_and_walked_once()
    {
        // A state whose generator is its own state machine (arrays are mutable, so the fixture can point back at itself).
        var states = new StateFixture[2];
        var machine = new StateMachineNode("Loop", states);
        states[0] = new StateFixture(0, "Run Ready", machine);
        states[1] = new StateFixture(1, "Other", new ClipNode("C", "SM_C"));
        var g = Assert.Single(Read(new StructuredGraph("SM_Test_NAV", [], [], machine)));
        var sm = Assert.Single(g.StateMachines);
        Assert.Equal(sm.Offset, sm.States[0].Generator);
        var hip = HipFireAnimations.From([g], [], _ => 1f, _ => null,
            [new HipFireAnimations.ClassMap(HipFireWeaponClass.Rifle, "SM_Test_NAV", "Run Ready", "Missing", "M", "F", "N", "S", null)]);
        var set = Assert.Single(hip.Sets);
        Assert.Empty(set.Directions);
        Assert.Contains(set.Notes, n => n.Contains("no blend on the movement direction"));
        Assert.Contains(set.Notes, n => n.Contains("graph Missing not found"));
    }

    [Fact]
    public void The_binding_blend_hint_is_read_and_a_bad_value_refused()
    {
        var bones = new[] { new Bone("RIG_Root", -1, Vector3.Zero, Quaternion.Identity) };
        var track = new[] { new Track(Vector3.Zero, Quaternion.Identity) };
        var file = new HavokCharacterFile(Build(bones,
        [
            new Clip("SM_Run", 1f, 31, track),
            new Clip("SM_Recoil", 0.5f, 16, track, Additive: true),
            new Clip("SM_Bad", 0.5f, 16, track, BlendHint: 7),
        ]));
        Assert.Equal(AnimationBlendHint.Normal, file.LoadBlendHint("SM_Run"));
        Assert.Equal(AnimationBlendHint.Additive, file.LoadBlendHint("SM_Recoil"));
        Assert.Throws<InvalidDataException>(() => file.LoadBlendHint("SM_Bad"));
        Assert.Throws<KeyNotFoundException>(() => file.LoadBlendHint("SM_None"));
    }

    // ---- HipFireAnimations on a synthetic rifle family ----

    static readonly string[] NavVariables = ["FLT_Nav_Motion_Angle_DAMPED", "FLT_Input_Nav_Speed_DAMPED", "FLT_Aim_Vertical_Deg_DAMPED", "BOOL_IsReady"];

    static BlendNode PitchBlend(string dir, string speed) => new($"{speed} {dir}",
        [new BlendChildFixture(-80, new ClipNode("d", $"SM_{speed}_{dir}_AimDown")), new BlendChildFixture(0, new ClipNode("f", $"SM_{speed}_{dir}_AimF")),
         new BlendChildFixture(80, new ClipNode("u", $"SM_{speed}_{dir}_AimUp"))], ParameterVariable: 2);

    static BlendNode SpeedBlend(string dir, float run) => new($"Nav {dir}",
        [new BlendChildFixture(0, PitchBlend(dir, "WOS")), new BlendChildFixture(2, PitchBlend(dir, "Walk")), new BlendChildFixture(run, PitchBlend(dir, "Run"))],
        ParameterVariable: 1);

    static StructuredGraph NavGraph()
    {
        var back = SpeedBlend("B", 4.5f);
        var directions = new BlendNode("Flat Terrain Blend",
            [new BlendChildFixture(-180, back), new BlendChildFixture(0, SpeedBlend("F", 6.5f)), new BlendChildFixture(90, SpeedBlend("R", 4.5f)), new BlendChildFixture(180, back)],
            ParameterVariable: 0, Flags: 0x31);
        var runReady = new ModifierGeneratorNode("Run Ready Gen", new SequencerNode("Seq", new BlendNode("Incline", [new BlendChildFixture(0, directions)]), []));
        var ready = new StateMachineNode("Ready", [new StateFixture(0, "Idle Ready", new ClipNode("i", "SM_Idle")), new StateFixture(1, "Run Ready", runReady)]);
        var relaxed = new ClipNode("r", "SM_Relaxed");
        var stand = new StateMachineNode("Stand",
        [
            new StateFixture(0, "Stand Relaxed", relaxed, [new TransitionFixture(1, BlendDuration: 0.267f, Condition: new BoolFixture([3], []))]),
            new StateFixture(1, "Stand Ready", ready, [new TransitionFixture(0, BlendDuration: 0.5f, Condition: new BoolFixture([], [3]))]),
        ]);
        return new StructuredGraph("SM_Test_NAV", [], NavVariables, stand);
    }

    static StructuredGraph FireGraph()
    {
        string[] events = ["BRANCH_Refire", "BRANCH_Shot_Finished", "BRANCH_to_Fire"];
        var recoil = new SequencerNode("Shoot Sequence", new ClipNode("c", "SM_Recoil", Mode: 0), [new Tag("Branch", 0.1f, 1f, 0), new Tag("Branch", 0.8f, 1f, 1)]);
        var raise = new SequencerNode("Raise", new ClipNode("c2", "SM_Recoil", Mode: 0), [new Tag("Branch", 0.4f, 1f, 2)]);
        var inner = new StateMachineNode("Recoil Behaviors", [new StateFixture(0, "Recoil", recoil), new StateFixture(2, "Set Weapon Ready00", new ModifierGeneratorNode("m", raise))]);
        return new StructuredGraph("SM_Test_Recoil", events, [], new StateMachineNode("Shoot", [new StateFixture(1, "Shoot", new ModifierGeneratorNode("Ability", inner))]));
    }

    static StructuredGraph BaseGraph()
    {
        string[] events = ["BTN_Ranged", "BRANCH_Shot_Finished", "BRANCH_Sprint_Evade"];
        var nav = new ReferenceNode("Nav Ref", "Behaviors\\SM_Test_NAV.hkxg");
        var fire = new BlendNode("Fire Blend", [new BlendChildFixture(1, nav), new BlendChildFixture(1, new ReferenceNode("Fire Ref", "Behaviors\\SM_Test_Recoil.hkxg"))], Flags: 0);
        var reload = new BlendNode("Test_ReloadBlend",
            [new BlendChildFixture(1, nav, [1f, 1f, 0f]), new BlendChildFixture(1, new ClipNode("rl", "SM_Reload"), [0f, 0f])], Flags: 0);
        var machine = new StateMachineNode("TestBehavior",
        [
            new StateFixture(1, "Test Navigation", nav, [new TransitionFixture(2, EventId: 0, BlendDuration: 0.1f)]),
            new StateFixture(2, "Test Fire", fire, [new TransitionFixture(1, BranchEventId: 1, BlendDuration: 0.15f), new TransitionFixture(2, EventId: 0)]),
            new StateFixture(3, "Test Reload", reload),
            new StateFixture(4, "Test Sprint", new ClipNode("sp", "SM_Sprint"), [new TransitionFixture(2, EventId: 0, BranchEventId: 2, BlendDuration: 0.1f)]),
        ]);
        return new StructuredGraph(HipFireAnimations.BaseGraph, events, [], new StateMachineNode("Base", [new StateFixture(0, "Ranged - Test", machine)]));
    }

    static readonly HipFireAnimations.ClassMap TestMap = new(HipFireWeaponClass.Rifle, "SM_Test_NAV", "Run Ready", "SM_Test_Recoil", "TestBehavior",
        "Test Fire", "Test Navigation", "Test Sprint", "Test_ReloadBlend");

    [Fact]
    public void Hip_fire_data_is_built_from_the_ready_movement_the_recoil_and_the_weapon_state_machine()
    {
        var graphs = Read(NavGraph(), FireGraph(), BaseGraph());
        string[] bones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineA", "RIG_Head"];
        var hip = HipFireAnimations.From(graphs, bones, clip => clip.StartsWith("SM_Run") ? 0.867f : clip == "SM_Recoil" ? 0.5f : 1.333f,
            clip => clip == "SM_Recoil" ? AnimationBlendHint.Additive : AnimationBlendHint.Normal, [TestMap]);
        var set = Assert.Single(hip.Sets);
        Assert.Empty(set.Notes);
        Assert.Same(set, hip.For(HipFireWeaponClass.Rifle));
        Assert.Null(hip.For(HipFireWeaponClass.Pistol));

        // Directions: the cyclic blend lists back twice; it is kept once.
        Assert.Equal([-180f, 0f, 90f], set.Directions.Select(d => d.AngleDegrees));
        Assert.Equal(["B", "F", "R"], set.Directions.Select(d => d.Label));
        var forward = set.Directions[1];
        Assert.Equal([0f, 2f, 6.5f], forward.Bands.Select(b => b.Speed));
        Assert.Equal(6.5f, forward.Run!.Speed);
        Assert.Equal(2f, forward.Walk!.Speed);
        Assert.Equal(0f, forward.OnTheSpot!.Speed);
        Assert.Equal([new AimClip(-80, "SM_Run_F_AimDown", 0.867f), new AimClip(0, "SM_Run_F_AimF", 0.867f), new AimClip(80, "SM_Run_F_AimUp", 0.867f)],
            forward.Run.Clips);
        Assert.Equal(4.5f, set.Directions[0].Run!.Speed);

        // Recoil: additive, hands back to movement at 0.8 × 0.5 s, refire from 0.1 × 0.5 s; the raise takes 0.4 × 0.5 s.
        Assert.Equal(new RecoilClip("Shoot Sequence", "SM_Recoil", 0.5f, 1f, true, 0.4f, 0.05f), set.Recoil);
        Assert.Equal(0.2f, set.RaiseDelay!.Value, 5);

        Assert.Equal(0.1f, set.NavToFireBlend);
        Assert.Equal(0.15f, set.FireToNavBlend);
        Assert.Equal("on BTN_Ranged in BRANCH_Sprint_Evade", set.SprintToFire);
        Assert.Equal("BOOL_IsReady", set.ReadyCondition);
        Assert.Equal((0.267f, 0.5f), (set.RelaxedToReadyBlend!.Value, set.ReadyToRelaxedBlend!.Value));

        Assert.Null(set.FireMask);
        Assert.Equal(new Dictionary<string, float> { ["RIG_Root"] = 0f, ["RIG_Pelvis"] = 0f, ["RIG_SpineA"] = 1f, ["RIG_Head"] = 1f }, set.UpperBodyMask);
    }

    [Fact]
    public void A_family_without_relaxed_posture_reload_blend_or_shot_blends_reports_nulls_and_zeros()
    {
        var graphs = Read(NavGraph() with { Root = ((StateMachineNode)NavGraph().Root).States[1].Generator }, FireGraph(), BaseGraph());
        var map = TestMap with { ReloadBlend = null };
        var set = Assert.Single(HipFireAnimations.From(graphs, ["RIG_Root"], _ => 1f, _ => null, [map]).Sets);
        Assert.Null(set.ReadyCondition);
        Assert.Null(set.RelaxedToReadyBlend);
        Assert.Null(set.UpperBodyMask);
        Assert.False(set.Recoil!.Additive);   // unknown blend hint
        Assert.Equal(3, set.Directions.Count);
    }
}
