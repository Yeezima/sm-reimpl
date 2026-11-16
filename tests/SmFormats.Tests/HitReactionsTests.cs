using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Hit reactions (2026-11-11): the death clip chosen by the base graph's transitions into DEATH (priorities, the killing hit's flinch branches, the nested state each enters, the conduit), the blast deaths of
/// the reaction graph, the flinch sets picked by the attacker's reaction type (the graph's ports), the slots by front / back, high / low and side, the random flinches, the weapons' reaction types, and
/// the hand-over to the ragdoll (its time and the clip's motion carried on). Synthetic graphs, documents and numbers only.
/// </summary>
public class HitReactionsTests
{
    // Events (indices are the branch / event ids of the fixtures).
    const int ReactionEnd = 0, Headshot = 1, RangedLeft = 2, RangedRight = 3, LasDeath = 4, BullrushDeath = 5, JumpDeathStart = 6, HitFlinch = 7;
    static readonly string[] Events = ["BRANCH_Reaction_End", "BRANCH_Headshot", "BRANCH_Death_Ranged_Left", "BRANCH_Death_Ranged_Right", "BRANCH_Las_Death",
        "BRANCH_Bullrush_Death", "BRANCH_Jump_Death_Start", "Hit_Flinch", "PORT_rng_bolter", "PORT_rng_lascannon"];
    // Variables.
    const int IsDead = 0, IsRagdoll = 1, DeathDisabled = 2, Sprint = 3, Jump = 4;
    static readonly string[] Variables = ["BOOL_IsDead", "BOOL_IsRagdoll", "BOOL_Death_State_Disabled", "BOOL_SprintIsActive", "BOOL_JumpIsActive"];

    static float Length(string clip) => clip switch
    {
        "SM_Death_F" => 0.733f, "SM_Death_B" => 0.667f, "SM_Stagger" => 1.6f, "SM_ChaosDeath" => 0.467f, "SM_Head_F" => 0.7f, "SM_Head_B" => 0.667f, "SM_Las_F" => 0.667f,
        "SM_Las_B" => 0.567f, "SM_Las_R" => 0.633f, "SM_Las_L" => 0.667f, "SM_Air_Hit" => 0.6f, "SM_Air_Fall" => 1.467f, "SM_Air_Impact" => 0.833f, "SM_Grenade_F" => 1f,
        "SM_Flinch_Head" => 0.367f, "SM_Flinch_High" => 0.2f, "SM_Flinch_Mid" => 0.2f, "SM_Flinch_Back" => 0.367f, _ => 0.2f,
    };

    static readonly ConditionFixture Dead = new BoolFixture([IsDead], [IsRagdoll, DeathDisabled]);

    static StateFixture Leaf(int id, string name, string clip, float ragdoll, params Tag[] more) =>
        new(id, name, new SequencerNode("d" + id + name, new ClipNode(clip, clip, Mode: 0),
            [new Tag("Modifier", ragdoll, 1f, new ModifierValue("hkbPoweredRagdollControlsModifier", "Powered Ragdoll Controls Modifier07")), .. more]));

    static SequencerNode Flinch(string name, string clip, params int[] branches) =>
        new(name, new ClipNode(name, clip, Mode: 0), [new Tag("Branch", 1f, 1f, ReactionEnd), .. branches.Select(b => new Tag("Branch", 0f, 1f, b))]);

    /// <summary>A base graph shaped like the install's: ROOT's transitions into DEATH (nested ids, branches, conditions), the death machine with its conduit, the flinch machines, the random flinches.</summary>
    static StructuredGraph BaseGraph()
    {
        var bolter = new StateMachineNode("Bolter Flinch Behavior",
        [
            new StateFixture(0, "Head Front Left", Flinch("hfl", "SM_Flinch_Head", Headshot)), new StateFixture(1, "Head Front Right", Flinch("hfr", "SM_Flinch_Head", Headshot)),
            new StateFixture(4, "High Left", Flinch("hl", "SM_Flinch_High", RangedLeft)), new StateFixture(5, "High Right", Flinch("hr", "SM_Flinch_High", RangedRight)),
            new StateFixture(6, "Low Left", Flinch("ll", "SM_Flinch_Mid", RangedLeft)), new StateFixture(8, "Back Left", Flinch("bl", "SM_Flinch_Back")),
        ]);
        var lascannon = new StateMachineNode("Lascannon Behavior",
            [new StateFixture(4, "Heavy High Left", Flinch("lhl", "SM_Flinch_Back", LasDeath)), new StateFixture(5, "Heavy High Right", Flinch("lhr", "SM_Flinch_Back", LasDeath))]);
        var ranged = new StateMachineNode("Ranged Flinch Behavior",
            [new StateFixture(0, "Ranged Flinch - ENTRY", new ClipNode("conduit", "SM_Ref")), new StateFixture(1, "Bolter Flinch", bolter), new StateFixture(5, "Lascannon Flinch", lascannon)]);
        var random = new WeightedRandomNode("WeightedRandomSelectorGenerator02",
            [(1f, Flinch("r1", "SM_Flinch_L_High")), (1f, Flinch("r2", "SM_Flinch_M_Mid")), (2f, Flinch("r3", "SM_Flinch_R_High"))]);
        var upper = new StateMachineNode("Upper Body Flinch Behavior",
        [
            new StateFixture(7, "Default - No Reaction", new ClipNode("none", "SM_Flinch_High"), [new TransitionFixture(8, EventId: HitFlinch, Priority: 2, BlendDuration: 0.1f)]),
            new StateFixture(8, "Random Flinches", random), new StateFixture(9, "Flinch States", ranged),
        ], StartStateId: 7);

        var death = new StateMachineNode("Behavior Death",
        [
            new StateFixture(0, "Death - Default", new StateMachineNode("Default Death Behavior", [Leaf(0, "Default Death Front", "SM_Death_F", 0.8182f), Leaf(1, "Default Death Back", "SM_Death_B", 0.8f)])),
            new StateFixture(1, "ENTRY - Death State", new ClipNode("conduit2", "SM_Ref"),
            [
                new TransitionFixture(6, BranchEventId: BullrushDeath, Priority: 6, BlendDuration: 0.1f),
                new TransitionFixture(5, Priority: 5, BlendDuration: 0.1f, Condition: new BoolFixture([Sprint, IsDead], [])),
                new TransitionFixture(16, Priority: 4, BlendDuration: 0.167f),   // the conduit's untriggered transition to the lascannon deaths (not taken)
                new TransitionFixture(0, Priority: 1, BlendDuration: 0.5f, EffectName: "Long Blend00"),
            ]),
            Leaf(5, "Sprinting Death", "SM_Stagger", 0.1875f), Leaf(6, "Bullrush Death", "SM_Las_F", 0.4f),
            new StateFixture(7, "Headshot Death", new StateMachineNode("Behavior01", [Leaf(0, "Headshot Front", "SM_Head_F", 0.8667f), Leaf(1, "Headshot Back", "SM_Head_B", 0.9f)])),
            Leaf(11, "Death Ranged Right", "SM_ChaosDeath", 0.8571f), Leaf(12, "Death Ranged Left", "SM_Death_F", 0.7273f),
            new StateFixture(15, "Jumppack Death", new ModifierGeneratorNode("ModifierGenerator10", new StateMachineNode("Jumppack Death Behavior",
            [
                new StateFixture(0, "Jumppack Death Start", new SequencerNode("start", new ClipNode("air hit", "SM_Air_Hit", Mode: 0), [new Tag("Branch", 0.9412f, 1f, JumpDeathStart)]),
                    [new TransitionFixture(1, BranchEventId: JumpDeathStart, Priority: 1, BlendDuration: 0.1f)]),
                new StateFixture(1, "Jumppack Death Fall", new SequencerNode("fall", new ClipNode("air fall", "SM_Air_Fall", PlaybackSpeed: 0.65f, Mode: 1), []), [new TransitionFixture(2, Priority: 1)]),
                Leaf(2, "Jumppack Death Hit Floor", "SM_Air_Impact", 0.44f),
            ]))),
            new StateFixture(16, "Lascannon Deaths", new StateMachineNode("Lascannon Death Behavior",
                [Leaf(0, "Las Death Front", "SM_Las_F", 0.65f), Leaf(1, "Las Death Back", "SM_Las_B", 0.5882f), Leaf(2, "Las Death Right", "SM_Las_R", 0.5263f), Leaf(3, "Las Death Left", "SM_Las_L", 0.45f)])),
        ], StartStateId: 1);

        TransitionFixture ToDeath(short priority, int nested, short branch = -1, ConditionFixture? condition = null) =>
            new(63, BranchEventId: branch, Priority: priority, BlendDuration: 0.1f, Condition: condition ?? Dead, ToNestedStateId: nested);
        var root = new StateMachineNode("SM_Base Behavior",
        [
            new StateFixture(63, "DEATH", new ModifierGeneratorNode("Death Modifier Generator", death)),
            new StateFixture(2, "ROOT", new StateMachineNode("Upper Body Behaviors", [new StateFixture(0, "Upper Body Flinches", upper)]),
            [
                ToDeath(9, 7, Headshot), ToDeath(1, -1), ToDeath(4, 12, RangedLeft), ToDeath(3, 11, RangedRight),
                ToDeath(10, 15, condition: new MultiFixture(0, [new BoolFixture([Jump], []), Dead])),
                ToDeath(6, 5, condition: new MultiFixture(0, [new BoolFixture([Sprint], []), Dead])), ToDeath(8, 16, LasDeath),
            ]),
        ], StartStateId: 2);
        return new StructuredGraph(SwitchAnimations.Graph, Events, Variables, root);
    }

    static StructuredGraph ReactionGraph() => new(ReactionTuning.Graph, Events, Variables, new StateMachineNode("Behavior00",
    [
        new StateFixture(0, "REC Grenade F", new SequencerNode("SequencerGenerator06", new ClipNode("g", "SM_MP_REC_Grenade_F_01", Mode: 0),
            [new Tag("Modifier", 0f, 1f, new ModifierValue("hkbSetBoolModifier", "Set Reaction Death Bool")),
             new Tag("Modifier", 0.3f, 1f, new ModifierValue("hkbPoweredRagdollControlsModifier", "Death Powered Ragdoll Controls"))])),
        new StateFixture(1, "REC Grenade R", new SequencerNode("SequencerGenerator07", new ClipNode("r", "SM_MP_REC_Grenade_R_01", Mode: 0), [])),   // no death tag: no blast death
    ]));

    static IReadOnlyList<BehaviorGraphDetail> Graphs() => HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure([BaseGraph(), ReactionGraph()])));
    static float L(string clip) => clip == "SM_MP_REC_Grenade_F_01" || clip == "SM_MP_REC_Grenade_R_01" ? 1f : Length(clip);
    static DeathAnimations Death() => DeathAnimations.From(Graphs(), L);
    static FlinchAnimations Flinches() => FlinchAnimations.From(Graphs(), L);

    static DeathContext Ctx(bool jump = false, bool sprint = false, bool bullRush = false, bool blast = false, string[]? branches = null, bool has = true, float along = -1f, float right = 0f) =>
        new(jump, sprint, bullRush, blast, branches ?? [], has, along, right);

    // ---------------------------------------------------------------------------------------------------------------------------------- the reader

    [Fact]
    public void A_transition_into_a_nested_state_carries_its_id_and_flag()
    {
        var g = Graphs().First(x => x.Name == SwitchAnimations.Graph);
        var root = g.AllStates.First(x => x.State.Name == "ROOT").State;
        var headshot = root.Transitions.Single(t => t.BranchEvent == "BRANCH_Headshot");
        Assert.Equal((63, 7, BehaviorTransition.ToNestedValid), (headshot.ToStateId, headshot.ToNestedStateId, headshot.Flags));
        var plain = root.Transitions.Single(t => t.Priority == 1);
        Assert.Equal((-1, (short)0), (plain.ToNestedStateId, plain.Flags));
        // The weighted random selector is a node whose children are its generators, with their weights.
        var random = g.StateMachines.First(m => m.Name == "Upper Body Flinch Behavior").States.First(s => s.Name == "Random Flinches");
        var node = g.Nodes[random.Generator!.Value];
        Assert.Equal("hkbWeightedRandomSelector", node.Class);
        Assert.Equal(["random weight 1", "random weight 1", "random weight 2"], node.Children.Select(c => c.Label));
    }

    [Fact]
    public void The_routes_into_death_and_out_of_its_conduit_are_read_with_their_targets_and_blends()
    {
        var death = Death();
        Assert.Equal(7, death.Routes.Count);
        var byPriority = death.Routes.OrderByDescending(r => r.Priority).Select(r => (r.Priority, r.Branch, r.Target)).ToList();
        Assert.Equal((10, null, "Jumppack Death"), byPriority[0]);
        Assert.Equal((9, "BRANCH_Headshot", "Headshot Death"), byPriority[1]);
        Assert.Equal((1, null, null), byPriority[^1]);   // the default route enters the conduit
        Assert.All(death.Routes, r => Assert.Equal(0.1f, r.Blend, 1e-4f));
        Assert.Equal(["Bullrush Death", "Sprinting Death", "Lascannon Deaths", "Death - Default"], death.EntryRoutes.OrderByDescending(r => r.Priority).Select(r => r.Target));
        // The clips take the blend of the transition that enters them: the default pair the conduit's 0.5 s, the nested ones ROOT's 0.1 s, the jump death's floor none.
        Assert.Equal(0.5f, death.Get(MarineDeathKind.DefaultFront)!.Blend, 1e-4f);
        Assert.Equal(0.1f, death.Get(MarineDeathKind.RangedRight)!.Blend, 1e-4f);
        Assert.Equal(0f, death.Get(MarineDeathKind.JumpImpact)!.Blend, 1e-4f);
    }

    [Fact]
    public void The_new_death_states_hand_over_at_their_tags()
    {
        var death = Death();
        Assert.Equal(("SM_ChaosDeath", 0.8571f * 0.467f), (death.Get(MarineDeathKind.RangedRight)!.Clip, death.Get(MarineDeathKind.RangedRight)!.RagdollAt), new ClipComparer());
        Assert.Equal(0.7273f * 0.733f, death.Get(MarineDeathKind.RangedLeft)!.RagdollAt, 1e-3f);
        Assert.Null(death.Get(MarineDeathKind.RangedBack));   // not in this fixture
        Assert.Contains(death.Notes, n => n.Contains("Death Ranged Back"));
        // The jump death: the start hands over to the fall at its branch; the fall loops at the sequencer's speed; the floor clip hands over to the ragdoll at its tag.
        Assert.Equal(0.9412f * 0.6f, death.Get(MarineDeathKind.JumpStart)!.RagdollAt, 1e-3f);
        Assert.Equal(1.467f / 0.65f, death.Get(MarineDeathKind.JumpFall)!.Length, 1e-3f);
        Assert.Equal(0.44f * 0.833f, death.Get(MarineDeathKind.JumpImpact)!.RagdollAt, 1e-3f);
        // The blast death: the reaction graph's grenade clip with a "Death Powered Ragdoll" tag (0.3 of 1 s); a clip without the tag gives none.
        var blast = Assert.IsType<DeathClip>(death.Get(MarineDeathKind.BlastFront));
        Assert.Equal(("SM_MP_REC_Grenade_F_01", 0.3f), (blast.Clip, blast.RagdollAt));
        Assert.Null(death.Get(MarineDeathKind.BlastRight));
    }

    sealed class ClipComparer : IEqualityComparer<(string, float)>
    {
        public bool Equals((string, float) a, (string, float) b) => a.Item1 == b.Item1 && MathF.Abs(a.Item2 - b.Item2) < 1e-3f;
        public int GetHashCode((string, float) v) => v.Item1.GetHashCode();
    }

    [Theory]
    [InlineData(0.6f, 1.5f, 0.6f)]   // the default front's tag
    [InlineData(1.467f, 1.5f, 1.467f)]   // the plasma death's, under the cap
    [InlineData(2.4f, 1.5f, 1.5f)]   // capped (ours)
    [InlineData(-1f, 1.5f, 0f)]
    public void The_body_is_held_until_the_clips_hand_over_and_never_past_the_cap(float ragdollAt, float cap, float held) =>
        Assert.Equal(held, DeathRules.HoldSeconds(new DeathClip("c", 3f, ragdollAt), cap), 1e-5f);

    // ---------------------------------------------------------------------------------------------------------------------------------- the death choice

    [Fact]
    public void A_shot_kill_takes_the_death_its_flinch_branch_opens()
    {
        var death = Death();
        foreach (var data in new DeathAnimations?[] { death, null })   // the read routes and the install's defaults agree
        {
            Assert.Equal(MarineDeathKind.RangedLeft, DeathRules.Choose(Ctx(branches: ["BRANCH_Death_Ranged_Left"]), data));
            Assert.Equal(MarineDeathKind.RangedRight, DeathRules.Choose(Ctx(branches: ["BRANCH_Death_Ranged_Right"]), data));
            Assert.Equal(MarineDeathKind.HeadshotFront, DeathRules.Choose(Ctx(branches: ["BRANCH_Headshot"]), data));
            Assert.Equal(MarineDeathKind.HeadshotBack, DeathRules.Choose(Ctx(branches: ["BRANCH_Headshot"], along: 1f), data));
            Assert.Equal(MarineDeathKind.LasLeft, DeathRules.Choose(Ctx(branches: ["BRANCH_Las_Death"], along: 0f, right: 1f), data));
            // A body hit from behind with the bolter's set raises no branch: the conduit's default pair, by the blow.
            Assert.Equal(MarineDeathKind.DefaultBack, DeathRules.Choose(Ctx(along: 1f), data));
            Assert.Equal(MarineDeathKind.DefaultFront, DeathRules.Choose(Ctx(has: false), data));
        }
        Assert.Equal(MarineDeathKind.RangedBack, DeathRules.Choose(Ctx(branches: ["BRANCH_Death_Ranged_Back"])));
        // The plasma sets raise two branches: the plasma death's priority (5) beats the side's (4).
        Assert.Equal(MarineDeathKind.Plasma, DeathRules.Choose(Ctx(branches: ["BRANCH_Death_Ranged_Left", "BRANCH_Plasma_Death"])));
        Assert.Equal(MarineDeathKind.MeltaBack, DeathRules.Choose(Ctx(branches: ["BRANCH_Melta_Death"], along: 1f)));
    }

    [Fact]
    public void Sprinting_beats_the_side_deaths_but_not_a_head_shot_or_a_lascannon()
    {
        var death = Death();
        Assert.Equal(MarineDeathKind.Sprint, DeathRules.Choose(Ctx(sprint: true, branches: ["BRANCH_Death_Ranged_Left"]), death));     // 6 > 4
        Assert.Equal(MarineDeathKind.HeadshotFront, DeathRules.Choose(Ctx(sprint: true, branches: ["BRANCH_Headshot"]), death));  // 9 > 6
        Assert.Equal(MarineDeathKind.LasFront, DeathRules.Choose(Ctx(sprint: true, branches: ["BRANCH_Las_Death"]), death));      // 8 > 6
        Assert.Equal(MarineDeathKind.Sprint, DeathRules.Choose(Ctx(sprint: true), death));                                        // a melee kill while sprinting
    }

    [Fact]
    public void A_victim_in_its_bull_rush_dies_in_the_bull_rush_clip_unless_a_shot_opened_another_death()
    {
        var death = Death();
        Assert.Equal(MarineDeathKind.Bullrush, DeathRules.Choose(Ctx(bullRush: true), death));                                         // through the conduit (6)
        Assert.Equal(MarineDeathKind.RangedRight, DeathRules.Choose(Ctx(bullRush: true, branches: ["BRANCH_Death_Ranged_Right"]), death));   // ROOT's branch comes first
        // The conduit's untriggered transition to the lascannon deaths (priority 4) is not taken: a melee kill standing still plays the default pair.
        Assert.Equal(MarineDeathKind.DefaultFront, DeathRules.Choose(Ctx(), death));
    }

    [Fact]
    public void A_blast_kill_plays_the_grenade_reaction_from_the_side_it_came_from_and_a_jump_takes_the_jump_death()
    {
        // The blow travels away from the blast: travelling backward (along -1) means the blast was in front.
        Assert.Equal(MarineDeathKind.BlastFront, DeathRules.Choose(Ctx(blast: true, along: -1f)));
        Assert.Equal(MarineDeathKind.BlastBack, DeathRules.Choose(Ctx(blast: true, along: 1f)));
        Assert.Equal(MarineDeathKind.BlastLeft, DeathRules.Choose(Ctx(blast: true, along: 0f, right: 1f)));   // pushed to its right: the blast was on its left
        Assert.Equal(MarineDeathKind.BlastRight, DeathRules.Choose(Ctx(blast: true, along: 0f, right: -1f)));
        Assert.Equal(MarineDeathKind.BlastFront, DeathRules.Choose(Ctx(blast: true, has: false)));
        // A blast beats sprinting and the branches (the reaction holds the death state off) ...
        Assert.Equal(MarineDeathKind.BlastBack, DeathRules.Choose(Ctx(blast: true, sprint: true, branches: ["BRANCH_Headshot"], along: 1f)));
        // ... but no reaction plays in jump-pack flight: the jump death (priority 10) beats everything.
        Assert.Equal(MarineDeathKind.JumpStart, DeathRules.Choose(Ctx(jump: true, blast: true), Death()));
        Assert.Equal(MarineDeathKind.JumpStart, DeathRules.Choose(Ctx(jump: true, branches: ["BRANCH_Headshot"]), Death()));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("all[!BOOL_IsRagdoll, BOOL_IsDead, !BOOL_Death_State_Disabled]", true)]
    [InlineData("all[BOOL_SprintIsActive, all[!BOOL_IsRagdoll, BOOL_IsDead, !BOOL_Death_State_Disabled]]", false)]
    [InlineData("(BOOL_SprintIsActive & BOOL_IsDead)", false)]
    [InlineData("BOOL_Unknown_Thing", false)]
    [InlineData("any[BOOL_JumpIsActive, BOOL_IsDead]", true)]
    public void A_routes_condition_is_read_from_its_variables(string? condition, bool holds) =>
        Assert.Equal(holds, DeathRules.ConditionHolds(condition, Ctx()));

    // ---------------------------------------------------------------------------------------------------------------------------------- flinches

    [Fact]
    public void A_flinch_slot_carries_the_death_branch_its_sequencer_raises()
    {
        var flinch = Flinches();
        var bolter = flinch.For("Bolter Flinch")!;
        Assert.Equal(["BRANCH_Headshot"], bolter.Slot(0)!.DeathBranches!);
        Assert.Equal(["BRANCH_Death_Ranged_Left"], bolter.Slot(6)!.DeathBranches!);
        Assert.Null(bolter.Slot(8)!.DeathBranches);   // the back slots raise none (BRANCH_Reaction_End is not a death branch)
        Assert.Equal(["BRANCH_Las_Death"], flinch.For("Lascannon Flinch")!.Slot(5)!.DeathBranches!);
    }

    [Fact]
    public void The_ports_pick_the_set_of_the_attackers_reaction_type_in_the_order_of_the_set_states()
    {
        var flinch = Flinches();
        Assert.Equal(new Dictionary<string, string> { ["rng_bolter"] = "Bolter Flinch", ["rng_lascannon"] = "Lascannon Flinch" }, flinch.Ports);
        Assert.Equal("Lascannon Flinch", FlinchRules.SetFor(flinch, "rng_lascannon", "sm_mp_ranged_bolter"));
        // A type without a port, or none read: the set by the weapon's name.
        Assert.Equal("Bolter Flinch", FlinchRules.SetFor(flinch, "rng_stormbolter", "sm_mp_ranged_storm_bolter"));
        Assert.Equal("Ranged Light Flinch", FlinchRules.SetFor(null, "reaction_ranged_light", "sm_mp_pistol_bolter"));
        Assert.Null(flinch.SetOfReaction(null));
    }

    [Fact]
    public void The_random_flinches_are_read_with_their_weights_and_picked_alike_for_one_seed()
    {
        var random = Flinches().Random;
        Assert.Equal(["SM_Flinch_L_High", "SM_Flinch_M_Mid", "SM_Flinch_R_High"], random.Select(r => r.Clip.Clip));
        Assert.Equal([1f, 1f, 2f], random.Select(r => r.Weight));
        var counts = new int[3];
        for (uint seed = 0; seed < 4000; seed++) counts[FlinchRules.PickRandom(random, seed)]++;
        Assert.All(counts, c => Assert.True(c > 500));
        Assert.InRange(counts[2] / (float)(counts[0] + counts[1]), 0.8f, 1.25f);   // weight 2 against 1 + 1
        Assert.Equal(FlinchRules.PickRandom(random, 1234u), FlinchRules.PickRandom(random, 1234u));
        Assert.Equal(-1, FlinchRules.PickRandom([], 1u));
    }

    [Fact]
    public void A_body_hit_takes_a_front_or_back_high_or_low_slot_by_its_side()
    {
        // The chooser's order: 4 front high left, 5 front high right, 6 front low left, 7 front low right, 8 back high left, 9 back high right, 10 back low left, 11 back low right.
        Assert.Equal(6, FlinchRules.Slot(false, false, true, low: true));
        Assert.Equal(7, FlinchRules.Slot(false, false, false, low: true));
        Assert.Equal(10, FlinchRules.Slot(false, true, true, low: true));
        Assert.Equal(11, FlinchRules.Slot(false, true, false, low: true));
        Assert.Equal(2, FlinchRules.Slot(true, true, true, low: true));   // a head hit is never low
        var bolter = Flinches().For("Bolter Flinch")!;
        Assert.Equal(6, FlinchRules.Choose(bolter, false, false, true, low: true));
        Assert.Equal(5, FlinchRules.Choose(bolter, false, false, false, low: true));   // no low right slot: the same side's high one
        Assert.True(FlinchRules.IsLow(0.9f, 0f));
        Assert.False(FlinchRules.IsLow(1.5f, 0f));
        Assert.False(FlinchRules.IsLow(float.NaN, 0f));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------- the weapons' reaction types

    static (string, object)[] Hit(string type) => [("actions", new (string, object)[] { ("trigger_reaction", new (string, object)[] { ("reaction_by_intensity", new (string, object)[] {
        ("entry", new (string, object)[] { ("intensity", 1f), ("reaction_type", type) }) }) }) })];

    [Fact]
    public void A_weapons_ranged_hit_names_its_reaction_type_the_primary_ability_first()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_ranged_primary_oldcharged", new (string, object)[] { ("delivery", new (string, object)[] { ("hit_sim", Hit("rng_bolter")) }) }),
            ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[]
            {
                ("penetration", new (string, object)[] { ("entry", Hit("reaction_ranged_light")) }),   // a penetration exit: left out
                ("hit_sim", Hit("rng_autocannon")),
            }) }),
            ("ability_melee_x1", Hit("rng_venglaunch_long")),
        }) })));
        Assert.Equal("rng_autocannon", WeaponReactions.Of(doc));
        var melee = BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[] { ("ability_melee_x1", Hit("rng_bolter")) }) })));
        Assert.Null(WeaponReactions.Of(melee));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------- the hand-over

    [Fact]
    public void The_ragdoll_takes_the_clips_motion_over_its_last_frame_capped()
    {
        // A body moving 3 cm forward in 1/60 s: 1.8 m/s, no spin.
        var (linear, angular) = DeathHandover.Velocity(Quaternion.Identity, Vector3.Zero, Quaternion.Identity, new Vector3(0, 0.03f, 0), Vector3.Zero, 1f / 60f);
        Assert.Equal(1.8f, linear.Y, 1e-3f);
        Assert.Equal(Vector3.Zero, angular);
        // Turning 6 degrees about z in 1/60 s: 6.28 rad/s about +z; a centre of mass 0.5 m out along x moves with it (0.5 x 6.28 = 3.1 m/s along +y).
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 6f * MathF.PI / 180f);
        (linear, angular) = DeathHandover.Velocity(Quaternion.Identity, Vector3.Zero, turn, Vector3.Zero, new Vector3(0.5f, 0, 0), 1f / 60f);
        Assert.Equal(2f * MathF.PI, angular.Z, 0.05f);
        Assert.Equal(3.14f, linear.Y, 0.05f);
        // Caps: a 1 m jump in one frame and a half turn in one frame are capped.
        (linear, angular) = DeathHandover.Velocity(Quaternion.Identity, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 3f), new Vector3(1f, 0, 0), Vector3.Zero, 1f / 60f);
        Assert.Equal(DeathHandover.MaxSpeed, linear.Length(), 1e-3f);
        Assert.Equal(DeathHandover.MaxSpin, angular.Length(), 1e-3f);
        // No time: nothing.
        Assert.Equal((Vector3.Zero, Vector3.Zero), DeathHandover.Velocity(Quaternion.Identity, Vector3.Zero, turn, Vector3.One, Vector3.Zero, 0f));
    }

    [Fact]
    public void The_clips_throw_and_the_push_do_not_add_up_but_the_limbs_keep_their_own_motion()
    {
        var up = Vector3.UnitZ;
        float[] masses = [1f, 1f];
        // Both bodies thrown 5 m/s along a 6 m/s push: the push is larger, the clip adds nothing.
        Assert.All(DeathHandover.Contributions([new Vector3(5, 0, 0), new Vector3(5, 0, 0)], masses, new Vector3(6, 0, 0), up), v => Assert.Equal(Vector3.Zero, v));
        // Along a 2 m/s push only the 3 m/s beyond it counts; across the push all of it.
        Assert.All(DeathHandover.Contributions([new Vector3(5, 1, 0), new Vector3(5, 1, 0)], masses, new Vector3(2, 0, 0), up), v => Assert.Equal(new Vector3(3, 1, 0), v));
        // Against the push they add (cancel in part).
        Assert.All(DeathHandover.Contributions([new Vector3(-2, 0, 0), new Vector3(-2, 0, 0)], masses, new Vector3(4, 0, 0), up), v => Assert.Equal(new Vector3(-2, 0, 0), v));
        // The limbs' own motion around the whole body stays: one arm swings +1, the other -1.
        var limbs = DeathHandover.Contributions([new Vector3(1, 0, 0), new Vector3(-1, 0, 0)], masses, new Vector3(3, 0, 0), up);
        Assert.Equal([new Vector3(1, 0, 0), new Vector3(-1, 0, 0)], limbs);
        // The whole body lifts at most 3 m/s; no push: the clip's motion as it is (below the lift cap).
        Assert.All(DeathHandover.Contributions([new Vector3(0, 0, 8), new Vector3(0, 0, 8)], masses, Vector3.Zero, up), v => Assert.Equal(DeathHandover.MaxLift, v.Z, 1e-5f));
        Assert.All(DeathHandover.Contributions([new Vector3(0, 2, 1), new Vector3(0, 2, 1)], masses, Vector3.Zero, up), v => Assert.Equal(new Vector3(0, 2, 1), v));
        Assert.Empty(DeathHandover.Contributions([], masses, Vector3.UnitX, up));
    }
}
