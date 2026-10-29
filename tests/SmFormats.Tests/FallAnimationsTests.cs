using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Animation pass 1: the fall, the hard landing and the exits read from the navigation graphs (<see cref="FallAnimations"/>), the player blueprint's landing
/// (<see cref="FallLanding"/>), the camera shake's shape and the engine-free landing rules (<see cref="FallRules"/>). Synthetic packfiles and numbers only.
/// </summary>
public class FallAnimationsTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    // Variables: 0 fall speed, 1 distance above ground, 2 requested speed.
    static readonly string[] Variables = ["FLT_Fall_Speed", "FLT_Distance_Above_Ground", "FLT_Input_Nav_Speed_LOGIC"];
    static readonly string[] Events = ["BRANCH_To_Run", "BRANCH_To_Run_MP", "BRANCH_To_Nav", "BRANCH_To_NAV_MP"];
    static readonly string[] Bones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineA"];

    static ThresholdFixture FallSpeed(float lo, float hi) => new(0, lo, hi);
    static ThresholdFixture Height(float lo, float hi) => new(1, lo, hi);

    static StructuredGraph RifleGraph(float hardSpeed = -20f)
    {
        // "Fall Behaviors": a machine of Dropping (legs clip on a masked child, the idle on the other) and Land (a sequencer with the two exit branches).
        var inner = new StateMachineNode("Behavior12",
        [
            new StateFixture(0, "Dropping", new ModifierGeneratorNode("Mod", new BlendNode("Blend01",
            [
                new BlendChildFixture(1, new SequencerNode("SeqFall", new ClipNode("fall", "SM_Fall"), []), [1f, 1f, 0f]),
                new BlendChildFixture(1, new ClipNode("idle", "SM_Idle"), [0f, 0f, 0f]),
            ])),
            [new TransitionFixture(1, BlendDuration: 0.1f, Condition: new MultiFixture(0, [Height(0f, 0.1f), FallSpeed(-99999f, hardSpeed)]))]),
            new StateFixture(1, "Land", new SequencerNode("SeqLand", new ClipNode("land", "SM_Land", Mode: 0), [new Tag("Branch", 0.5952f, 1f, 0), new Tag("Branch", 0.1563f, 1f, 1)])),
        ]);
        var top = new StateMachineNode("Behavior00",
        [
            new StateFixture(0, "Stand Behaviors", new ClipNode("stand", "SM_Stand"), [new TransitionFixture(2, BlendDuration: 0.167f, Condition: FallSpeed(-99999f, hardSpeed))]),
            new StateFixture(2, "Fall Behaviors", inner,
            [
                new TransitionFixture(0, BranchEventId: 0, Priority: 2, BlendDuration: 0.333f),
                new TransitionFixture(0, BranchEventId: 1, Priority: 3, BlendDuration: 0.267f),
                new TransitionFixture(0, Priority: 1, BlendDuration: 0.1f, Condition: new MultiFixture(0, [Height(0f, 0.1f), new NegateFixture(FallSpeed(-99999f, hardSpeed))])),
            ]),
        ]);
        return new StructuredGraph("SM_Test_NAV", Events, Variables, top);
    }

    static StructuredGraph HeavyGraph()
    {
        var inner = new StateMachineNode("Behavior01",
        [
            new StateFixture(0, "Drop", new ModifierGeneratorNode("Align", new ClipNode("drop", "SM_Drop", Mode: 0)), [new TransitionFixture(1, BlendDuration: 0.1f, Condition: Height(0f, 0.5f))]),
            new StateFixture(1, "Land", new SequencerNode("SeqLand", new ClipNode("land", "SM_Landing", Mode: 0), [new Tag("Branch", 0.8065f, 1f, 2), new Tag("Branch", 0.2258f, 1f, 3)])),
        ]);
        var top = new StateMachineNode("Behavior10",
        [
            new StateFixture(0, "Idle Ready", new ClipNode("idle", "SM_Idle"), [new TransitionFixture(4, BlendDuration: 0.1f, Condition: Height(1f, 99999f))]),
            new StateFixture(4, "Heavy Drop Down", new ModifierGeneratorNode("Mod", inner),
            [
                new TransitionFixture(0, BranchEventId: 2, Priority: 1, BlendDuration: 0.1f),
                new TransitionFixture(0, BranchEventId: 3, Priority: 3, BlendDuration: 0.1f),
            ]),
        ]);
        return new StructuredGraph("SM_Test_TB", Events, Variables, top);
    }

    static readonly FallAnimations.FallMap RifleMap = new(FallFamily.Rifle, "SM_Test_NAV", "Fall Behaviors", "Dropping", "Land");
    static readonly FallAnimations.FallMap HeavyMap = new(FallFamily.Heavy, "SM_Test_TB", "Heavy Drop Down", "Drop", "Land");

    static float Length(string clip) => clip switch { "SM_Fall" => 0.333f, "SM_Land" => 1.067f, "SM_Drop" => 0.833f, "SM_Landing" => 1.033f, _ => 1.5f };

    static FallAnimations Load(params StructuredGraph[] graphs) =>
        FallAnimations.From(Read(graphs), Bones, Length, [RifleMap, HeavyMap], new HashSet<string> { "core_evade" });

    [Fact]
    public void The_rifle_fall_is_a_legs_layer_a_whole_body_landing_and_two_exits()
    {
        var falls = Load(RifleGraph(), HeavyGraph());
        Assert.Empty(falls.Notes);
        var set = Assert.IsType<FallSet>(falls.For(FallFamily.Rifle));
        Assert.Equal(0.167f, set.EnterBlend, 1e-4f);
        Assert.Equal(0.1f, set.LandBlend, 1e-4f);
        Assert.Equal(0.1f, set.SoftExitBlend, 1e-4f);
        Assert.Equal(20f, set.HardFallSpeed);
        Assert.Equal(0f, set.EnterHeight);
        Assert.Equal(0.1f, set.LandHeight, 1e-4f);
        var legs = Assert.IsType<FallClip>(set.Legs);
        Assert.Equal(("SM_Fall", 0.333f), (legs.Clip, legs.Length));
        // The masked child's weights by bone name: the root and pelvis at 1, the spine at 0.
        Assert.Equal([1f, 1f, 0f], Bones.Select(b => legs.Mask![b]));
        Assert.Null(set.Drop);
        Assert.Equal(("SM_Land", 1.067f), (set.Land.Clip, set.Land.Length));
        // BRANCH_To_Run at 0.5952 of the clip and BRANCH_To_Run_MP at 0.1563 (the fall-protection exit).
        Assert.Equal(0.5952f * 1.067f, set.Exit.At, 1e-4f);
        Assert.Equal(0.333f, set.Exit.Blend, 1e-4f);
        Assert.Equal(0.1563f * 1.067f, set.ProtectedExit.At, 1e-4f);
        Assert.Equal(0.267f, set.ProtectedExit.Blend, 1e-4f);
        Assert.Contains("core_evade", falls.ProtectedWargear);
    }

    [Fact]
    public void The_heavy_fall_is_a_whole_body_drop_from_a_metre_and_a_landing_at_half_a_metre_at_any_speed()
    {
        var set = Assert.IsType<FallSet>(Load(RifleGraph(), HeavyGraph()).For(FallFamily.Heavy));
        Assert.Null(set.Legs);
        var drop = Assert.IsType<FallClip>(set.Drop);
        Assert.Equal(("SM_Drop", 0.833f), (drop.Clip, drop.Length));
        Assert.Null(drop.Mask);
        Assert.Equal(1f, set.EnterHeight);
        Assert.Equal(0.5f, set.LandHeight, 1e-4f);
        Assert.Equal(0f, set.HardFallSpeed);   // every fall plays the landing
        Assert.Equal(0.1f, set.EnterBlend, 1e-4f);
        Assert.Equal(0.8065f * 1.033f, set.Exit.At, 1e-4f);
        Assert.Equal(0.2258f * 1.033f, set.ProtectedExit.At, 1e-4f);
    }

    [Fact]
    public void A_missing_graph_or_state_is_noted_and_leaves_the_other_family()
    {
        var falls = Load(RifleGraph());
        Assert.NotNull(falls.For(FallFamily.Rifle));
        Assert.Null(falls.For(FallFamily.Heavy));
        Assert.Contains(falls.Notes, n => n.Contains("SM_Test_TB") && n.Contains("not found"));
        var wrong = FallAnimations.From(Read(RifleGraph()), Bones, Length, [new FallAnimations.FallMap(FallFamily.Rifle, "SM_Test_NAV", "No Such State", "Dropping", "Land")]);
        Assert.Empty(wrong.Sets);
        Assert.Contains(wrong.Notes, n => n.Contains("no such state"));
        var noLand = FallAnimations.From(Read(RifleGraph()), Bones, Length, [new FallAnimations.FallMap(FallFamily.Rifle, "SM_Test_NAV", "Fall Behaviors", "Dropping", "Missing")]);
        Assert.Empty(noLand.Sets);
        Assert.Contains(noLand.Notes, n => n.Contains("Missing"));
    }

    [Fact]
    public void The_protected_wargear_are_those_whose_lifetime_actions_apply_the_upgrade_string()
    {
        var evade = BafDocument.Read(SyntheticBafWriter.EvadeAbility("evade", "evade_default_mp", 0.8f, 25, 40));
        var other = BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "ranged") })));
        var names = FallAnimations.ProtectedIn([new("sm_mp_core_abil_evade", evade), new("sm_mp_ranged_bolter", other)]);
        Assert.Equal(["sm_mp_core_abil_evade"], names);
    }

    [Fact]
    public void The_blueprints_landing_has_its_effect_and_a_shake_with_the_data_amplitudes()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("health_ext", new (string, object)[] { ("ragdoll_death", true) }),
            ("biped_locomotion_ext", new (string, object)[]
            {
                ("finish_falling_actions", new (string, object)[] { ("commands", new (string, object)[]
                {
                    ("post_sound_event", new (string, object)[] { ("start_event_name", "play_sm_land_normal") }),
                    ("camera_effect", new (string, object)[]
                    {
                        ("duration", 0.1f), ("ease_out", 0.15f), ("ease_in", 0f),
                        ("shake_info", new (string, object)[]
                        {
                            ("horizontal", new (string, object)[] { ("amplitude", 0.1f), ("frequency", 12f) }),
                            ("vertical", new (string, object)[] { ("amplitude", 0.15f), ("frequency", 12f) }),
                            ("pitch", new (string, object)[] { ("amplitude", 0f), ("frequency", 25f) }),
                        }),
                    }),
                    ("play_fx", new (string, object)[] { ("fx_package", "master_fx"), ("fx_name", "jump_land"), ("scale", 0.5f), ("length", 0.45f) }),
                }) }),
            }),
        })));
        var landing = FallLanding.Read(doc);
        Assert.Equal("play_sm_land_normal", landing.SoundEvent);
        var play = Assert.Single(landing.Plays);
        Assert.Equal(("jump_land", 0.5f), (play.Name, play.Scale));
        var shake = Assert.IsType<CameraShakeSpec>(landing.Shake);
        Assert.Equal((0.1f, 0.15f), (shake.Duration, shake.EaseOut));
        Assert.Equal((0.1f, 12f), shake.Horizontal);
        Assert.Equal((0.15f, 12f), shake.Vertical);
        Assert.Equal(0.25f, shake.Total, 1e-5f);
        Assert.Same(FallLanding.None, FallLanding.Read(BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("health_ext", new (string, object)[] { ("ragdoll_death", true) }) })))));
    }

    [Fact]
    public void The_shakes_envelope_is_full_for_its_duration_then_eases_out_and_its_offsets_follow_the_sines()
    {
        var shake = new CameraShakeSpec(0.1f, 0f, 0.15f, (0.1f, 12f), (0.15f, 12f), (0f, 0f), (0f, 0f), (0f, 0f));
        Assert.Equal(0f, shake.Envelope(-1f));
        Assert.Equal(1f, shake.Envelope(0.05f));
        Assert.Equal(1f, shake.Envelope(0.1f));
        Assert.Equal(0.5f, shake.Envelope(0.175f), 1e-4f);
        Assert.Equal(0f, shake.Envelope(0.26f));
        var (h, v) = shake.OffsetAt(1f / 48f);   // a quarter of a 12 Hz period: the sine's peak
        Assert.Equal(0.1f, h, 1e-4f);
        Assert.Equal(0.15f, v, 1e-4f);
        Assert.Equal((0f, 0f), shake.OffsetAt(0.3f));
    }

    [Fact]
    public void A_fall_is_hard_from_the_graphs_speed_and_always_for_a_zero_threshold()
    {
        Assert.False(FallRules.IsHard(19.9f, 20f));
        Assert.True(FallRules.IsHard(20f, 20f));
        Assert.True(FallRules.IsHard(0f, 0f));       // the heavy bolter's landing needs no speed
        Assert.False(FallRules.IsHard(float.NaN, 20f));
    }

    [Fact]
    public void The_landing_clip_blends_in_holds_until_the_exit_and_blends_out()
    {
        var exit = new FallExit(0.167f, 0.267f);
        Assert.Equal(0f, FallRules.LandWeight(-0.1f, 0.1f, exit));
        Assert.Equal(0f, FallRules.LandWeight(0f, 0.1f, exit));
        Assert.Equal(0.5f, FallRules.LandWeight(0.05f, 0.1f, exit), 1e-5f);
        Assert.Equal(1f, FallRules.LandWeight(0.1f, 0.1f, exit));
        Assert.Equal(1f, FallRules.LandWeight(0.167f, 0.1f, exit));
        Assert.Equal(0.5f, FallRules.LandWeight(0.167f + 0.1335f, 0.1f, exit), 1e-4f);
        Assert.Equal(0f, FallRules.LandWeight(0.5f, 0.1f, exit));
        Assert.False(FallRules.LandOver(0.43f, exit));
        Assert.True(FallRules.LandOver(0.434f, exit));
        // An exit shorter than the blend in never exceeds the weight reached.
        Assert.InRange(FallRules.LandWeight(0.05f, 0.1f, new FallExit(0.02f, 0.02f)), 0f, 0f);
    }

    // ---- FallTracker: the engine-free state machine --------------------------------------------------------------------------------------

    const double Tick = 1.0 / 30.0, Frame = 1.0 / 60.0;

    static List<FallEvent> Run(FallTracker tracker, double start, double step, params (bool Eligible, bool Grounded, float Vz)[] frames)
    {
        var events = new List<FallEvent>();
        for (int i = 0; i < frames.Length; i++) events.Add(tracker.Observe(start + i * step, frames[i].Eligible, frames[i].Grounded, frames[i].Vz));
        return events;
    }

    static readonly (bool, bool, float) Ground = (true, true, 0f);
    static (bool, bool, float) Air(float vz = -1f) => (true, false, vz);

    [Fact]
    public void A_blip_of_one_tick_in_the_air_is_not_a_fall_and_two_ticks_are()
    {
        var oneTick = new FallTracker();
        Assert.All(Run(oneTick, 1.0, Tick, Ground, Air(), Ground, Ground), e => Assert.Equal(FallEvent.None, e));
        Assert.False(oneTick.Falling);
        var twoTicks = new FallTracker();
        // Unsupported at 1.0, 1.033, 1.067: the third frame is 2 ticks after the first, the fall begins; the touchdown is the landing.
        var events = Run(twoTicks, 1.0, Tick, Ground, Air(), Air(-2f), Air(-3f), Ground);
        Assert.Equal([FallEvent.None, FallEvent.None, FallEvent.None, FallEvent.Began, FallEvent.Landed], events);
        Assert.InRange(twoTicks.LastAirTime, 0.09, 0.11);
        Assert.Equal(3f, twoTicks.PeakDown);
        Assert.False(twoTicks.Falling);
    }

    [Fact]
    public void Interpolated_flags_of_a_remote_player_that_flicker_every_frame_never_start_a_fall()
    {
        var t = new FallTracker();
        var frames = Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? Air() : Ground).ToArray();
        Assert.All(Run(t, 5.0, Frame, frames), e => Assert.Equal(FallEvent.None, e));
        // A stretch of three air frames at 60 fps (0.05 s) is still under the debounce; five in a row (0.083 s) is over it.
        var a = new FallTracker();
        Assert.DoesNotContain(FallEvent.Began, Run(a, 0.0, Frame, Ground, Air(), Air(), Air(), Ground));
        var b = new FallTracker();
        Assert.Contains(FallEvent.Began, Run(b, 0.0, Frame, Ground, Air(), Air(), Air(), Air(), Air(), Air()));
    }

    [Fact]
    public void Dying_in_the_air_aborts_the_fall_and_the_next_supported_frame_is_not_a_landing()
    {
        var t = new FallTracker();
        var events = Run(t, 0.0, Tick, Air(), Air(), Air(), (false, false, -5f), (false, true, 0f), Ground);
        Assert.Equal([FallEvent.None, FallEvent.None, FallEvent.Began, FallEvent.Aborted, FallEvent.None, FallEvent.None], events);
        Assert.False(t.Falling);
        Assert.False(t.InAir);
    }

    [Fact]
    public void A_roll_that_ends_in_the_air_starts_a_new_spell_with_its_own_debounce_and_peak()
    {
        var t = new FallTracker();
        // Airborne but not eligible (rolling): nothing counts. Eligible again in the air: the debounce and the peak speed start then.
        var events = Run(t, 0.0, Tick, (false, false, -9f), (false, false, -12f), Air(-1f), Air(-2f), Air(-3f), Ground);
        Assert.Equal([FallEvent.None, FallEvent.None, FallEvent.None, FallEvent.None, FallEvent.Began, FallEvent.Landed], events);
        Assert.Equal(3f, t.PeakDown);   // the roll's -12 does not count
        Assert.InRange(t.LastAirTime, 0.09, 0.11);
    }

    [Fact]
    public void A_fall_taken_over_by_a_roll_is_aborted_not_landed()
    {
        var t = new FallTracker();
        var events = Run(t, 0.0, Tick, Air(), Air(), Air(), (false, false, -4f), (true, true, 0f));
        Assert.Equal([FallEvent.None, FallEvent.None, FallEvent.Began, FallEvent.Aborted, FallEvent.None], events);
    }

    [Fact]
    public void The_peak_speed_decides_a_soft_or_a_hard_landing_even_when_the_last_frame_dips_toward_zero()
    {
        var soft = new FallTracker();
        Run(soft, 0.0, Tick, Air(-5f), Air(-9f), Air(-12f), Ground);
        Assert.False(FallRules.IsHard(soft.PeakDown, 20f));
        var hard = new FallTracker();
        // The interpolated vertical speed of a remote player falls back toward 0 at the touchdown frame; the peak keeps the 25.
        var events = Run(hard, 0.0, Tick, Air(-10f), Air(-20f), Air(-25f), Air(-3f), Ground);
        Assert.Equal(FallEvent.Landed, events[^1]);
        Assert.True(FallRules.IsHard(hard.PeakDown, 20f));
        Assert.Equal(25f, hard.PeakDown);
        Assert.False(float.IsNaN(new FallTracker().PeakDown));
    }

    [Fact]
    public void A_gap_in_the_frames_forgets_the_fall_and_a_respawn_on_the_ground_is_not_a_landing()
    {
        var t = new FallTracker();
        Run(t, 0.0, Tick, Air(), Air(), Air());
        Assert.True(t.Falling);
        Assert.Equal(FallEvent.None, t.Observe(2.0, true, true, 0f));   // not shown for 1.9 s, then standing: no landing
        Assert.True(t.WasReset);
        Assert.False(t.Falling);
        Assert.Equal(FallEvent.None, t.Observe(2.033, true, true, 0f));
    }

    [Fact]
    public void The_heavy_bolters_drop_starts_at_a_metre_and_its_landing_at_half_a_metre_once()
    {
        var t = new FallTracker();
        Run(t, 0.0, Tick, Air(), Air(), Air());
        Assert.False(t.HeavyStep(0.1, 0.8f, 1f, 0.5f));      // under the entry height: no drop yet
        Assert.False(t.DropStarted);
        Assert.False(t.HeavyStep(0.13, 3f, 1f, 0.5f));
        Assert.True(t.DropStarted);
        Assert.Equal(0.13, t.DropSince, 6);
        Assert.False(t.HeavyStep(0.2, 0.9f, 1f, 0.5f));
        Assert.True(t.HeavyStep(0.25, 0.4f, 1f, 0.5f));
        Assert.True(t.HeavyLanded);
        Assert.False(t.HeavyStep(0.3, 0.1f, 1f, 0.5f));      // only once
        // A short hop that never reaches the entry height has no drop and no landing.
        var hop = new FallTracker();
        Run(hop, 0.0, Tick, Air(), Air(), Air());
        Assert.False(hop.HeavyStep(0.1, 0.7f, 1f, 0.5f));
        Assert.False(hop.HeavyStep(0.13, 0.3f, 1f, 0.5f));
        Assert.False(hop.DropStarted);
        // Not falling: nothing happens.
        Assert.False(new FallTracker().HeavyStep(0.0, 5f, 1f, 0.5f));
    }

    [Fact]
    public void The_hard_landing_window_ends_at_the_exit_and_a_new_fall_clears_it()
    {
        var exit = new FallExit(0.167f, 0.267f);
        var t = new FallTracker();
        t.BeginLanding(10.0, hard: true);
        Assert.True(t.LandingActive(10.2, exit));
        Assert.False(t.LandingActive(10.5, exit));
        t.BeginLanding(10.0, hard: false);
        Assert.False(t.LandingActive(10.1, exit));
        t.Observe(19.9, true, true, 0f);
        t.BeginLanding(20.0, hard: true);
        Assert.True(t.LandHard);
        Run(t, 20.033, Tick, Air(), Air(), Air());   // the next fall begins
        Assert.False(t.LandHard);
    }

    [Fact]
    public void A_moving_marine_keeps_less_of_the_landing_crouch()
    {
        Assert.Equal(1f, FallRules.MoveKeep(0f));
        Assert.Equal(1f, FallRules.MoveKeep(1f));
        Assert.Equal(0.5f, FallRules.MoveKeep(2f), 1e-5f);
        Assert.Equal(0f, FallRules.MoveKeep(3f));
        Assert.Equal(0f, FallRules.MoveKeep(9f));
        Assert.Equal(1f, FallRules.MoveKeep(float.NaN));
    }
}
