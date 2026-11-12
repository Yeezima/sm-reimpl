using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Install transitions (2026-11-12, polish item 10 batch 2): the chainsword's and the thunder hammer's whole-body drop and landing (<c>SM_NAV_CS_Dropdown</c> / <c>_Landing</c>,
/// <c>SM_2HM_NAV_TH_Dropdown</c> / <c>_Landing</c>) read like the heavy bolter's (<see cref="FallFamily.Hammer"/>, <see cref="FallFamily.Chainsword"/>), the pistol graph's nested chainsword
/// machine included, and the family helper. Synthetic packfiles and numbers only.
/// </summary>
public class InstallTransitionsMeleeFallTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static readonly string[] Variables = ["FLT_Fall_Speed", "FLT_Distance_Above_Ground", "FLT_Input_Nav_Speed_LOGIC"];
    static readonly string[] Events = ["BRANCH_To_Nav", "BRANCH_To_Run_MP"];
    static readonly string[] Bones = ["RIG_Root", "RIG_Pelvis"];

    static ThresholdFixture Height(float lo, float hi) => new(1, lo, hi);

    // SM_TH_NAV's shape: "Drop Down" holds the machine of Drop and Land.
    static StructuredGraph HammerGraph() => new("SM_Test_TH", Events, Variables, new StateMachineNode("Top",
    [
        new StateFixture(0, "Idle", new ClipNode("idle", "SM_Idle"), [new TransitionFixture(2, BlendDuration: 0.1f, Condition: Height(1f, 99999f))]),
        new StateFixture(2, "Drop Down", new ModifierGeneratorNode("Mod", new StateMachineNode("Inner",
        [
            new StateFixture(0, "Drop", new ClipNode("drop", "SM_TH_Drop", Mode: 0), [new TransitionFixture(1, BlendDuration: 0.1f, Condition: Height(0f, 0.5f))]),
            new StateFixture(1, "Land", new SequencerNode("SeqLand", new ClipNode("land", "SM_TH_Land", Mode: 0), [new Tag("Branch", 0.8f, 1f, 0), new Tag("Branch", 0.25f, 1f, 1)])),
        ])),
        [new TransitionFixture(0, BranchEventId: 0, Priority: 1, BlendDuration: 0.1f), new TransitionFixture(0, BranchEventId: 1, Priority: 3, BlendDuration: 0.1f)]),
    ]));

    // SM_Pistol_NAV's shape: "Drop Down" (the entry and the exits) holds a chooser machine whose states are the weapons' machines; the chainsword's drop is two machines down.
    static StructuredGraph PistolGraph() => new("SM_Test_Pistol", Events, Variables, new StateMachineNode("Top",
    [
        new StateFixture(0, "Idle", new ClipNode("idle", "SM_Idle"), [new TransitionFixture(4, BlendDuration: 0.1f, Condition: Height(1f, 99999f))]),
        new StateFixture(4, "Drop Down", new ModifierGeneratorNode("Mod", new StateMachineNode("Chooser",
        [
            new StateFixture(0, "Drop Down with Chainsword", new StateMachineNode("Cs",
            [
                new StateFixture(0, "CS Drop", new ClipNode("drop", "SM_CS_Drop", Mode: 0), [new TransitionFixture(1, BlendDuration: 0.1f, Condition: Height(0f, 0.5f))]),
                new StateFixture(1, "CS Land", new SequencerNode("SeqLand", new ClipNode("land", "SM_CS_Land", Mode: 0), [])),
            ])),
            new StateFixture(1, "Drop Down with Thunderhammer", new StateMachineNode("Th",
            [
                new StateFixture(0, "TH Drop", new ClipNode("drop", "SM_TH_Drop", Mode: 0), [new TransitionFixture(1, BlendDuration: 0.1f, Condition: Height(0f, 0.5f))]),
                new StateFixture(1, "TH Land", new SequencerNode("SeqLand", new ClipNode("land", "SM_TH_Land", Mode: 0), [])),
            ])),
        ])),
        [new TransitionFixture(0, BranchEventId: 0, Priority: 1, BlendDuration: 0.1f), new TransitionFixture(0, BranchEventId: 1, Priority: 4, BlendDuration: 0.1f)]),
    ]));

    static float Length(string clip) => clip switch { "SM_TH_Drop" or "SM_CS_Drop" => 0.833f, "SM_TH_Land" => 1.033f, "SM_CS_Land" => 1.067f, _ => 1.5f };

    static readonly FallAnimations.FallMap HammerMap = new(FallFamily.Hammer, "SM_Test_TH", "Drop Down", "Drop", "Land");
    static readonly FallAnimations.FallMap ChainswordMap = new(FallFamily.Chainsword, "SM_Test_Pistol", "Drop Down", "CS Drop", "CS Land");

    static FallAnimations Load() => FallAnimations.From(Read(HammerGraph(), PistolGraph()), Bones, Length, [HammerMap, ChainswordMap]);

    [Fact]
    public void The_hammers_fall_reads_like_the_heavy_bolters_whole_body_drop()
    {
        var falls = Load();
        var set = Assert.IsType<FallSet>(falls.For(FallFamily.Hammer));
        Assert.Null(set.Legs);
        Assert.Equal(("SM_TH_Drop", 0.833f), (set.Drop!.Clip, set.Drop.Length));
        Assert.Equal(("SM_TH_Land", 1.033f), (set.Land.Clip, set.Land.Length));
        Assert.Equal(1f, set.EnterHeight);
        Assert.Equal(0.5f, set.LandHeight, 1e-4f);
        Assert.Equal(0f, set.HardFallSpeed);
        Assert.Equal(0.8f * 1.033f, set.Exit.At, 1e-4f);
        Assert.Equal(0.25f * 1.033f, set.ProtectedExit.At, 1e-4f);
    }

    [Fact]
    public void The_chainswords_drop_is_found_two_machines_down_in_the_pistol_graph_with_the_outer_states_entry_and_exits()
    {
        var falls = Load();
        Assert.DoesNotContain(falls.Notes, n => n.Contains("no '"));
        var set = Assert.IsType<FallSet>(falls.For(FallFamily.Chainsword));
        Assert.Equal(("SM_CS_Drop", 0.833f), (set.Drop!.Clip, set.Drop.Length));
        Assert.Equal(("SM_CS_Land", 1.067f), (set.Land.Clip, set.Land.Length));   // not the thunder hammer's states in the same chooser
        Assert.Equal(1f, set.EnterHeight);                                         // the OUTER state's entry
        Assert.Equal(0.5f, set.LandHeight, 1e-4f);
    }

    [Fact]
    public void Every_family_but_the_rifle_drops_whole_body()
    {
        Assert.False(FallFamily.Rifle.WholeBodyDrop());
        foreach (var family in new[] { FallFamily.Heavy, FallFamily.Hammer, FallFamily.Chainsword }) Assert.True(family.WholeBodyDrop());
    }

    [Fact]
    public void The_default_maps_name_the_install_graphs_of_all_four_families()
    {
        var byFamily = FallAnimations.DefaultMaps.ToDictionary(m => m.Family);
        Assert.Equal(("SM_TH_NAV", "Drop Down", "Drop", "Land"), (byFamily[FallFamily.Hammer].Graph, byFamily[FallFamily.Hammer].FallState, byFamily[FallFamily.Hammer].DropState, byFamily[FallFamily.Hammer].LandState));
        Assert.Equal(("SM_Pistol_NAV", "Drop Down", "CS Drop", "CS Land"), (byFamily[FallFamily.Chainsword].Graph, byFamily[FallFamily.Chainsword].FallState, byFamily[FallFamily.Chainsword].DropState, byFamily[FallFamily.Chainsword].LandState));
        Assert.Equal(4, byFamily.Count);
    }
}

public class MeleeFallChoiceTests
{
    [Theory]
    [InlineData("sm_mp_melee_thunder_hammer", FallFamily.Hammer)]
    [InlineData("sm_mp_melee_power_mace", FallFamily.Hammer)]
    [InlineData("sm_mp_melee_chainsword", FallFamily.Chainsword)]
    [InlineData("sm_mp_melee_chainsword_gold", FallFamily.Chainsword)]
    [InlineData("sm_mp_melee_power_sword", FallFamily.Rifle)]
    [InlineData("sm_mp_melee_combat_knife", FallFamily.Rifle)]
    public void The_wargear_picks_the_family(string wargear, FallFamily expected) =>
        Assert.Equal(expected, MeleeFallChoice.Family(wargear, extraTransitionsOff: false));

    [Fact]
    public void The_switch_or_no_wargear_gives_the_rifles_fall()
    {
        Assert.Equal(FallFamily.Rifle, MeleeFallChoice.Family("sm_mp_melee_thunder_hammer", extraTransitionsOff: true));
        Assert.Equal(FallFamily.Rifle, MeleeFallChoice.Family("sm_mp_melee_chainsword", extraTransitionsOff: true));
        Assert.Equal(FallFamily.Rifle, MeleeFallChoice.Family(null, extraTransitionsOff: false));
    }

    [Fact]
    public void The_landing_is_the_hammers_only_for_a_hammer_with_the_switch_on_and_the_clip_present()
    {
        Assert.Equal("TH", MeleeFallChoice.Landing("x_thunder_hammer", false, "plain", "TH"));
        Assert.Equal("TH", MeleeFallChoice.Landing("x_power_mace", false, "plain", "TH"));
        Assert.Equal("plain", MeleeFallChoice.Landing("x_thunder_hammer", true, "plain", "TH"));       // --no-extra-transitions
        Assert.Equal("plain", MeleeFallChoice.Landing("x_thunder_hammer", false, "plain", null));      // the clip is not in the rig
        Assert.Equal("plain", MeleeFallChoice.Landing("x_chainsword", false, "plain", "TH"));
        Assert.Equal("plain", MeleeFallChoice.Landing(null, false, "plain", "TH"));
    }

    [Fact]
    public void Musous_spear_counts_as_the_hammer_through_its_alias()
    {
        // The alias of the spear carries the hammer's wargear name (AnimationAlias.Of, Extras); the choice only sees the aliased name.
        Assert.True(MeleeFallChoice.IsHammer("sm_mp_melee_thunder_hammer"));
        Assert.False(MeleeFallChoice.IsHammer(null));
    }
}
