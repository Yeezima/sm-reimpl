using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Animation pass 2: the weapon switch, the flinch sets, the death clips and the jump pack's get-up read from behaviour graphs (<see cref="UpperBodyBundle"/>), and the engine-free choice rules
/// (<see cref="FlinchRules"/>, <see cref="DeathRules"/>). Synthetic packfiles and numbers only.
/// </summary>
public class UpperBodyAnimationsTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static readonly string[] Bones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineA"];
    static readonly string[] Events = ["BRANCH_to_Getup", "BRANCH_To_Idle_Getup", "BRANCH_Run"];
    static readonly string[] Variables = ["FLT_Input_Nav_Speed"];

    static float Length(string clip) => clip switch
    {
        "SM_Swap_Rifle_to_Rifle_01" => 0.467f, "SM_Swap_Rifle_to_Melee_01" => 0.667f, "SM_Death_F" => 0.733f, "SM_Death_B" => 0.667f, "SM_Stagger" => 1.6f,
        "SM_Flinch_Head" => 0.367f, "SM_Flinch_High" => 0.2f, "SM_Flinch_Back" => 0.367f, "SM_Impact" => 1.067f, "SM_MedRun" => 1f, "SM_JMP_Blend_90_Pos_Fast" => 1f, _ => 1.5f,
    };

    static BlendNode SwapBlend(string swapClip, params Tag[] tags) => new("Blend", [
        new BlendChildFixture(1, new SequencerNode("Swap", new ClipNode("swap", swapClip, Mode: 0), tags), [0f, 0f]),   // the first two bones (the "legs") at 0, the rest read as 1
        new BlendChildFixture(1, new ClipNode("nav", "SM_Nav"), [1f, 1f]),
    ]);

    static StructuredGraph BaseGraph()
    {
        var pistol = new StateMachineNode("Behavior05", [new StateFixture(0, "State00", new ModifierGeneratorNode("Stow", SwapBlend("SM_Swap_Rifle_to_Melee_01",
            new Tag("Branch", 1f, 1f, 2), new Tag("BranchIn", 0.4f, 0.4f), new Tag("Modifier", 0.6f, 1f, new ModifierValue("hkbEntityPropertyControlsModifier", "Weapons - Melee and Ranged")))))]);
        var rifle = new ModifierGeneratorNode("Swap", SwapBlend("SM_Swap_Rifle_to_Rifle_01",
            new Tag("Modifier", 0f, 0.5f, new ModifierValue("hkbEntityPropertyControlsModifier", "Weapons - Melee Weapon")),
            new Tag("Modifier", 0.5f, 1f, new ModifierValue("hkbEntityPropertyControlsModifier", "Weapons - Ranged Weapon")), new Tag("BranchIn", 0.45f, 0.45f)));

        BlendChildFixture Flinch(string clip) => new(1, new SequencerNode("seq_" + clip, new ClipNode(clip, clip, Mode: 0), []));
        StateMachineNode FlinchSet(string name, params (int Id, string Clip)[] slots) => new(name, slots.Select(s => new StateFixture(s.Id, "slot" + s.Id, new SequencerNode("s" + s.Id, new ClipNode("c" + s.Id, s.Clip, Mode: 0), []))).ToArray());
        var flinches = new StateMachineNode("Ranged Flinch Behavior",
        [
            new StateFixture(1, "Bolter Flinch", FlinchSet("Bolter Flinch Behavior", (0, "SM_Flinch_Head"), (4, "SM_Flinch_High"), (8, "SM_Flinch_Back"))),
            new StateFixture(6, "Plasma Weak Flinch", FlinchSet("Plasma Weak Behavior", (4, "SM_Flinch_High"), (5, "SM_Flinch_High"))),
        ]);

        StateFixture Leaf(int id, string name, string clip, float ragdoll) => new(id, name, new SequencerNode("d" + id, new ClipNode(clip, clip, Mode: 0), [new Tag("Modifier", ragdoll, 1f, new ModifierValue("hkbPoweredRagdollControlsModifier", "Powered Ragdoll Controls Modifier07"))]));
        var death = new StateMachineNode("Behavior Death",
        [
            new StateFixture(0, "Death - Default", new StateMachineNode("Default Death Behavior", [Leaf(0, "Default Death Front", "SM_Death_F", 0.8182f), Leaf(1, "Default Death Back", "SM_Death_B", 0.8f)])),
            Leaf(5, "Sprinting Death", "SM_Stagger", 0.1875f),
        ]);
        var root = new StateMachineNode("Root",
        [
            new StateFixture(0, "BoltPistol Weapon Switch", pistol), new StateFixture(1, "Bolter Weapon Switch", rifle),
            new StateFixture(2, "Flinches", flinches), new StateFixture(3, "DEATH", death),
        ]);
        return new StructuredGraph(SwitchAnimations.Graph, Events, Variables, root);
    }

    static StructuredGraph JumpGraph(bool withNoise = true)
    {
        var machine = new StateMachineNode("GP",
        [
            new StateFixture(5, "GroundPoundImpact", new SequencerNode("Impact", new ClipNode("i", "SM_Impact", Mode: 0), [new Tag("Branch", 0.3333f, 1f, 0), new Tag("Branch", 0.8f, 1f, 1)]),
            [
                new TransitionFixture(7, BranchEventId: 0, Priority: 2, BlendDuration: 0.167f, Condition: new ThresholdFixture(0, 0.5f, 1000f)),
                new TransitionFixture(8, BranchEventId: 1, Priority: 1, BlendDuration: 0.167f),
            ]),
            new StateFixture(7, "GroundPoundRun", new SequencerNode("Run", new ClipNode("r", "SM_MedRun", Mode: 0), [new Tag("Branch", 0.6f, 1f, 2)])),
            new StateFixture(8, "GroundPound Idle", new ClipNode("idle", "SM_Idle", Mode: 0)),
        ]);
        GraphNode root = machine;
        if (withNoise) root = new StateMachineNode("Top", [new StateFixture(0, "GP", machine), new StateFixture(1, "Jump", new BlendNode("Noise", [new BlendChildFixture(1, new ClipNode("n", "SM_JMP_Blend_90_Pos_Fast", Mode: 1))]))]);
        return new StructuredGraph(JumpPackAnimations.Graph, Events, Variables, root);
    }

    [Fact]
    public void The_switches_are_upper_body_clips_that_swap_the_weapon_at_the_graphs_modifier_tag()
    {
        var upper = UpperBodyBundle.From(Read(BaseGraph(), JumpGraph()), Bones, Length);
        var rifle = Assert.IsType<SwitchClip>(upper.Switch.ToRifle);
        Assert.Equal(("SM_Swap_Rifle_to_Rifle_01", 0.467f), (rifle.Clip, rifle.Length));
        Assert.Equal(0.5f * 0.467f, rifle.SwapAt, 1e-4f);     // "Weapons - Ranged Weapon" from 0.5
        Assert.Equal(0.45f * 0.467f, rifle.BranchIn, 1e-4f);
        Assert.Equal([0f, 0f, 1f], Bones.Select(b => rifle.Mask[b]));   // the legs at 0, the rest (past the mask's entries) at 1
        var pistol = Assert.IsType<SwitchClip>(upper.Switch.ToPistol);
        Assert.Equal(("SM_Swap_Rifle_to_Melee_01", 0.667f), (pistol.Clip, pistol.Length));
        Assert.Equal(0.6f * 0.667f, pistol.SwapAt, 1e-4f);    // "Weapons - Melee and Ranged" from 0.6
        Assert.Equal(0.4f * 0.667f, pistol.BranchIn, 1e-4f);
        Assert.Equal((1f, 1f), (rifle.Speed, pistol.Speed));   // fix/stow-right: the sequencers' playback speed is carried (1 here and on the install; the client prints it)
    }

    [Fact]
    public void A_missing_graph_or_state_is_noted()
    {
        var none = SwitchAnimations.From([], Bones, Length);
        Assert.Null(none.ToRifle);
        Assert.Contains(none.Notes, n => n.Contains("not found"));
        var wrong = new StructuredGraph(SwitchAnimations.Graph, Events, Variables, new StateMachineNode("Root", [new StateFixture(0, "Other", new ClipNode("x", "SM_X"))]));
        var upper = SwitchAnimations.From(Read(wrong), Bones, Length);
        Assert.Null(upper.ToPistol);
        Assert.Contains(upper.Notes, n => n.Contains("Bolter Weapon Switch"));
        Assert.Empty(DeathAnimations.From(Read(wrong), Length).Clips);
        Assert.Empty(FlinchAnimations.From(Read(wrong), Length).Sets);
    }

    [Fact]
    public void The_flinch_sets_are_read_by_weapon_with_a_slot_per_state_id()
    {
        var flinch = UpperBodyBundle.From(Read(BaseGraph(), JumpGraph()), Bones, Length, minHitReactionTime: 1f).Flinch;
        Assert.Equal(["Bolter Flinch", "Plasma Weak Flinch"], flinch.Sets.Keys.Order());
        var bolter = flinch.For("Bolter Flinch")!;
        Assert.Equal(new FlinchClip("SM_Flinch_Head", 0.367f), bolter.Slot(0));
        Assert.Null(bolter.Slot(1));
        Assert.Equal("SM_Flinch_High", bolter.Slot(4)!.Clip);
        Assert.Equal("SM_Flinch_Back", bolter.Slot(8)!.Clip);
        Assert.Null(flinch.For("Plasma Weak Flinch")!.Slot(0));   // no head clips in this set
        Assert.Equal(1f, flinch.MinHitReactionTime);
        Assert.Equal(FlinchAnimations.DefaultMinHitReactionTime, FlinchAnimations.From(Read(BaseGraph()), Length).MinHitReactionTime);
        Assert.Equal(0.25f, FlinchAnimations.From(Read(BaseGraph()), Length, 0.25f).MinHitReactionTime);
        Assert.Equal(FlinchAnimations.DefaultMinHitReactionTime, FlinchAnimations.From(Read(BaseGraph()), Length, float.NaN).MinHitReactionTime);
    }

    [Fact]
    public void A_hit_takes_the_slot_of_its_zone_and_side_and_falls_back_to_the_bodys_when_the_set_has_no_head_clips()
    {
        Assert.Equal(0, FlinchRules.Slot(true, false, true));
        Assert.Equal(1, FlinchRules.Slot(true, false, false));
        Assert.Equal(2, FlinchRules.Slot(true, true, true));
        Assert.Equal(3, FlinchRules.Slot(true, true, false));
        Assert.Equal(4, FlinchRules.Slot(false, false, true));
        Assert.Equal(5, FlinchRules.Slot(false, false, false));
        Assert.Equal(8, FlinchRules.Slot(false, true, true));
        Assert.Equal(9, FlinchRules.Slot(false, true, false));
        var set = UpperBodyBundle.From(Read(BaseGraph()), Bones, Length).Flinch;
        Assert.Equal(0, FlinchRules.Choose(set.For("Bolter Flinch")!, head: true, fromBehind: false, fromLeft: true));
        Assert.Equal(5, FlinchRules.Choose(set.For("Plasma Weak Flinch")!, head: true, fromBehind: false, fromLeft: false));   // no head slot: the body's
        Assert.Equal(4, FlinchRules.Choose(set.For("Bolter Flinch")!, head: false, fromBehind: false, fromLeft: false));       // no right body slot: the other side's body slot
        Assert.Equal(-1, FlinchRules.Choose(new FlinchSet("none", new FlinchClip?[12]), false, false, false));
    }

    [Fact]
    public void A_shooters_side_is_found_in_the_victims_frame()
    {
        // The victim at the origin faces +Y (yaw 0); a shooter ahead is in front, behind is behind; yaw turns left, so +X is on its right.
        Assert.Equal((false, false), FlinchRules.Side(0, 0, 0f, 5f, 10f));
        Assert.Equal((false, true), FlinchRules.Side(0, 0, 0f, -5f, 10f));
        Assert.Equal((true, true), FlinchRules.Side(0, 0, 0f, -5f, -10f));
        Assert.Equal((true, false), FlinchRules.Side(0, 0, 0f, 5f, -10f));
        // Turned left by 90 degrees the victim faces -X: a shooter at -X is in front.
        Assert.False(FlinchRules.Side(0, 0, MathF.PI / 2f, -10f, 0f).Behind);
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", false, "Bolter Flinch")]
    [InlineData("sm_mp_pistol_bolt_pistol", false, "Bolt Pistol Flinch")]
    [InlineData("sm_mp_exotic_heavy_bolter", false, "Heavy Bolter Flinch")]
    [InlineData("sm_mp_exotic_plasma_cannon", true, "Plasma Cannon Strong Flinch")]
    [InlineData("sm_mp_exotic_plasma_cannon", false, "Plasma Cannon Weak Flinch")]
    [InlineData("sm_mp_ranged_plasma_gun", false, "Plasma Weak Flinch")]
    [InlineData("sm_mp_pistol_plasma_pistol", true, "Plasma Pistol Strong Flinch")]
    [InlineData("sm_mp_exotic_las_cannon", false, "Lascannon Flinch")]
    [InlineData("sm_mp_exotic_melta", false, "Melta Flinch")]
    [InlineData("sm_mp_ranged_stalker_pattern_bolter", false, "Stalker Pattern Flinch")]
    public void A_weapon_uses_its_own_flinch_set(string weapon, bool charging, string set) => Assert.Equal(set, FlinchRules.SetFor(weapon, charging));

    [Fact]
    public void The_death_clips_and_the_time_the_ragdoll_takes_over_come_from_the_powered_ragdoll_tag()
    {
        var death = DeathAnimations.From(Read(BaseGraph()), Length);
        var front = Assert.IsType<DeathClip>(death.Get(MarineDeathKind.DefaultFront));
        Assert.Equal(("SM_Death_F", 0.733f), (front.Clip, front.Length));
        Assert.Equal(0.8182f * 0.733f, front.RagdollAt, 1e-3f);
        Assert.Equal(0.8f * 0.667f, death.Get(MarineDeathKind.DefaultBack)!.RagdollAt, 1e-3f);
        Assert.Equal(0.1875f * 1.6f, death.Get(MarineDeathKind.Sprint)!.RagdollAt, 1e-3f);
        Assert.Null(death.Get(MarineDeathKind.Plasma));
        Assert.Contains(death.Notes, n => n.Contains("Plasma Death"));
    }

    [Fact]
    public void A_death_picks_its_clip_by_the_blow_the_weapon_and_the_victims_motion()
    {
        // Along the facing = travelling the way the victim faces = from behind.
        Assert.Equal(MarineDeathKind.DefaultFront, DeathRules.Choose(false, false, false, WeaponDeath.Bolter, true, -1f, 0f));
        Assert.Equal(MarineDeathKind.DefaultBack, DeathRules.Choose(false, false, false, WeaponDeath.Bolter, true, 1f, 0f));
        Assert.Equal(MarineDeathKind.DefaultFront, DeathRules.Choose(false, false, false, WeaponDeath.Bolter, false, 0f, 0f));   // no direction: the front
        Assert.Equal(MarineDeathKind.HeadshotFront, DeathRules.Choose(false, false, true, WeaponDeath.Bolter, true, -1f, 0f));
        Assert.Equal(MarineDeathKind.HeadshotBack, DeathRules.Choose(false, false, true, WeaponDeath.Bolter, true, 1f, 0f));
        Assert.Equal(MarineDeathKind.Sprint, DeathRules.Choose(true, false, false, WeaponDeath.Bolter, true, -1f, 0f));
        Assert.Equal(MarineDeathKind.Bullrush, DeathRules.Choose(true, true, false, WeaponDeath.Bolter, true, -1f, 0f));
        Assert.Equal(MarineDeathKind.Plasma, DeathRules.Choose(false, false, false, WeaponDeath.Plasma, true, -1f, 0f));
        // Lascannon wedges: front within 55 degrees of the facing, back beyond 125, the sides between (a blow travelling to the victim's right comes from its left).
        Assert.Equal(MarineDeathKind.LasFront, DeathRules.Choose(false, false, false, WeaponDeath.Las, true, -1f, 0.3f));
        Assert.Equal(MarineDeathKind.LasBack, DeathRules.Choose(false, false, false, WeaponDeath.Las, true, 1f, 0.3f));
        Assert.Equal(MarineDeathKind.LasLeft, DeathRules.Choose(false, false, false, WeaponDeath.Las, true, 0f, 1f));
        Assert.Equal(MarineDeathKind.LasRight, DeathRules.Choose(false, false, false, WeaponDeath.Las, true, 0f, -1f));
        Assert.Equal(MarineDeathKind.MeltaFront, DeathRules.Choose(false, false, false, WeaponDeath.Melta, false, 0f, 0f));
        Assert.Equal(MarineDeathKind.MeltaRight, DeathRules.Choose(false, false, false, WeaponDeath.Melta, true, 0f, -1f));
        Assert.Equal(WeaponDeath.Las, DeathRules.WeaponOf("sm_mp_exotic_plasma_cannon"));
        Assert.Equal(WeaponDeath.Melta, DeathRules.WeaponOf("sm_mp_exotic_melta"));
        Assert.Equal(WeaponDeath.Plasma, DeathRules.WeaponOf("sm_mp_pistol_plasma_pistol"));
        Assert.Equal(WeaponDeath.Bolter, DeathRules.WeaponOf(null));
    }

    [Fact]
    public void The_ground_pound_get_up_runs_when_moving_from_the_impacts_getup_window_and_the_flight_has_its_noise()
    {
        var jump = JumpPackAnimations.From(Read(JumpGraph()), Length);
        Assert.Empty(jump.Notes);
        Assert.Equal("SM_MedRun", jump.RunClip);
        Assert.Equal(0.3333f * 1.067f, jump.FromImpact, 1e-3f);
        Assert.Equal(0.5f, jump.MinSpeed);
        Assert.Equal(0.167f, jump.Blend, 1e-4f);
        Assert.Equal(0.6f, jump.RunEnd, 1e-4f);
        Assert.Equal(("SM_JMP_Blend_90_Pos_Fast", 1f), (jump.NoiseClip, jump.NoiseLength));
        var plain = JumpPackAnimations.From(Read(JumpGraph(withNoise: false)), Length);
        Assert.Null(plain.NoiseClip);
        Assert.Contains(plain.Notes, n => n.Contains("noise"));
        Assert.Null(JumpPackAnimations.From([], Length).RunClip);
    }

    [Theory]
    [InlineData(false, false, true)]    // a standing victim: the death clip plays
    [InlineData(false, true, false)]    // killed in the air (jump-pack flight, pound, plain fall): straight to the ragdoll
    [InlineData(true, false, false)]    // fell out of the level
    [InlineData(true, true, false)]
    public void A_death_in_the_air_or_out_of_the_level_plays_no_standing_clip(bool fellOutOfLevel, bool airborne, bool holds) =>
        Assert.Equal(holds, DeathRules.Holds(fellOutOfLevel, airborne));

    [Fact]
    public void The_held_bodys_slide_decays_and_never_grows()
    {
        Assert.Equal(1f, DeathRules.HeldVelocityFactor(0f, 0.2f));
        Assert.Equal(MathF.Exp(-1f), DeathRules.HeldVelocityFactor(0.2f, 0.2f), 1e-6f);
        Assert.True(DeathRules.HeldVelocityFactor(0.6f, 0.2f) < DeathRules.HeldVelocityFactor(0.3f, 0.2f));
        Assert.Equal(1f, DeathRules.HeldVelocityFactor(-1f, 0.2f));   // before the hold: all of it
        Assert.Equal(1f, DeathRules.HeldVelocityFactor(0.5f, 0f));    // no time constant: no decay model
        // A sprinting victim (10 m/s) held 0.3 s starts its ragdoll at 10 x exp(-1.5) = 2.2 m/s, a walking one held 0.6 s (the default front) at 5 x exp(-3) = 0.25 m/s: never the raw running speed.
        Assert.Equal(2.23f, 10f * DeathRules.HeldVelocityFactor(0.3f, 0.2f), 0.01f);
        Assert.Equal(0.249f, 5f * DeathRules.HeldVelocityFactor(0.6f, 0.2f), 0.01f);
    }
}
