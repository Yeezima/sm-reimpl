using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// The install's transition clips the client did not play (2026-11-12, polish item 10): the sprint start's interrupt edge (the half-speed stop, <see cref="SprintInterruptRules"/>), the 180-degree turn into a sprint
/// (<see cref="SprintTurnTracker"/>, <see cref="SprintEdgeClip.TurnProgress"/>) and their reading from the graphs (<see cref="SprintTransitions"/>). Synthetic packfiles and numbers only.
/// </summary>
public class InstallTransitionsTests
{
    const double Frame = 1.0 / 60.0;

    static readonly string[] Events = ["BRANCH_Sprint_Finished", "BRANCH_Idle2SprintLoop", "BRANCH_Sprint_Interrupt"];
    static readonly string[] Variables = ["FLT_Input_Nav_Speed", "BOOL_SprintIsActive"];

    // The rifle sprint graph's shape: the entry state chooses the start or the 180 turn; the start leaves for the loop at its end or, from 0.4286, for the half-speed stop; the 180 turn leaves for the loop.
    static StructuredGraph Graph(string name, bool withTurn, bool withInterrupt) => new(name, Events, Variables, new StateMachineNode("Sprint",
    [
        new StateFixture(23, "MP Sprint - ENTRY", new ClipNode("conduit", "SM_Conduit"),
            withTurn ? [new TransitionFixture(1, Priority: 1, BlendDuration: 0.1f), new TransitionFixture(24, Priority: 2, BlendDuration: 0.1f)] : [new TransitionFixture(1, BlendDuration: 0.1f)]),
        new StateFixture(1, "Transition To Idle", new SequencerNode("SeqStart", new ClipNode("start", "SM_Start", Mode: 0), [new Tag("Branch", 1f, 1f, 1), new Tag("Branch", 0.4286f, 1f, 2)]),
            withInterrupt
                ? [new TransitionFixture(2, BranchEventId: 1, BlendDuration: 0.1f), new TransitionFixture(5, BranchEventId: 2, Priority: 5, BlendDuration: 0.333f)]
                : [new TransitionFixture(2, BranchEventId: 1, BlendDuration: 0.1f)]),
        new StateFixture(2, "Sprint Loop", new SequencerNode("SeqLoop", new ClipNode("loop", "SM_Loop"), []), [new TransitionFixture(4, Priority: 2, BlendDuration: 0.267f)]),
        new StateFixture(4, "Sprint to Idle", new SequencerNode("SeqStop", new ClipNode("stop", "SM_Stop", Mode: 0), [new Tag("Branch", 0.625f, 1f, 0)])),
        new StateFixture(5, "Transition To Idle on Interrupt", new SequencerNode("SeqHalf", new ClipNode("half", "SM_Half", Mode: 0), [new Tag("Branch", 0.963f, 1f, 0)])),
        new StateFixture(24, "180 Idle to Sprint", new SequencerNode("SeqTurn", new ClipNode("turn", "SM_Turn", Mode: 0), [new Tag("Branch", 1f, 1f, 1), new Tag("Branch", 0.4286f, 1f, 2)]),
            [new TransitionFixture(2, BranchEventId: 1, BlendDuration: 0.1f)]),
    ], StartStateId: 23));

    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static float Length(string clip) => clip switch { "SM_Start" => 0.933f, "SM_Stop" => 1.333f, "SM_Half" => 0.9f, "SM_Turn" => 1.233f, "SM_Loop" => 0.633f, _ => 1.5f };

    // The turn clip's root: 180 degrees (W = pi) between 0.3 and 0.4 s, travelling away from 0.3 s; the half-speed stop's root speed 5 m/s for 0.7 s; the rest in place.
    static HavokExtractedMotion? Motion(string clip)
    {
        const int n = 41;
        float duration = Length(clip);
        var samples = new System.Numerics.Vector4[n];
        float y = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = duration * i / (n - 1);
            float turn = clip == "SM_Turn" ? MathF.PI * Math.Clamp((t - 0.3f) / 0.1f, 0f, 1f) : 0f;
            samples[i] = new System.Numerics.Vector4(0f, y, 0f, turn);
            y += (clip == "SM_Half" && t < 0.7f ? 5f : clip == "SM_Turn" && t > 0.3f ? -8f : 0f) * duration / (n - 1);
        }
        return clip is "SM_Turn" or "SM_Half" ? new HavokExtractedMotion(System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY, duration, samples) : null;
    }

    static readonly SprintTransitions.Map RifleMap = new(SprintFamily.Rifle, false, "MP_Test_Sprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "Bolter Sprint", "Bolter Navigation", TurnState: "180 Idle to Sprint");

    [Fact]
    public void The_interrupt_stop_the_start_window_and_the_180_turn_are_read_from_the_graph()
    {
        var edges = SprintTransitions.From(Read(Graph("MP_Test_Sprint", withTurn: true, withInterrupt: true)), Length, Motion, [RifleMap]);
        var rifle = Assert.IsType<SprintEdges>(edges.For(SprintFamily.Rifle, false));
        var half = Assert.IsType<SprintEdgeClip>(rifle.Interrupt);
        Assert.Equal("SM_Half", half.Clip);
        Assert.Equal(0.333f, half.BlendIn, 1e-4f);                   // the start's interrupt edge: the 10 frame blend
        Assert.Equal(0.963f * 0.9f, half.BranchAt, 1e-4f);
        var window = Assert.IsType<SprintStartWindow>(rifle.StartWindow);
        Assert.Equal(0.933f, window.Length, 1e-4f);
        Assert.Equal(0.4286f * 0.933f, window.InterruptAt, 1e-4f);   // BRANCH_Sprint_Interrupt opens at 0.4286 of the entry state
        var turn = Assert.IsType<SprintEdgeClip>(rifle.Turn);
        Assert.Equal("SM_Turn", turn.Clip);
        Assert.Equal((0.1f, 0.1f), (turn.BlendIn, turn.BlendOut));    // entry 'Default 3 Frame Blend', into the loop at its branch
        Assert.Equal(1.233f, turn.BranchAt, 1e-4f);
        Assert.NotNull(turn.Turn);
        var turnWindow = Assert.IsType<SprintStartWindow>(rifle.TurnWindow);   // the 180 state's own interrupt window, not the start's
        Assert.Equal((1.233f, 0.4286f * 1.233f), (turnWindow.Length, turnWindow.InterruptAt));
        Assert.Equal(MathF.PI, turn.Turn![^1], 1e-4f);
    }

    [Fact]
    public void A_graph_without_the_interrupt_or_the_turn_state_gives_none_and_the_other_edges_stay()
    {
        var edges = SprintTransitions.From(Read(Graph("MP_Test_Sprint", withTurn: false, withInterrupt: false)), Length, Motion, [RifleMap]);
        var rifle = Assert.IsType<SprintEdges>(edges.For(SprintFamily.Rifle, false));
        Assert.Null(rifle.Interrupt);
        Assert.Null(rifle.Turn);
        Assert.NotNull(rifle.Start);
        Assert.NotNull(rifle.Stop);
        Assert.NotNull(rifle.StartWindow);   // the window is the entry state's, whether or not the interrupt edge exists
    }

    [Fact]
    public void A_family_with_no_base_states_still_reads_its_hammer_style_edges_without_a_note_about_them()
    {
        var hammer = new SprintTransitions.Map(SprintFamily.Hammer, false, "MP_Test_Sprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "", "", TurnState: "180 Idle to Sprint");
        var edges = SprintTransitions.From(Read(Graph("MP_Test_Sprint", withTurn: true, withInterrupt: true)), Length, Motion, [hammer]);
        var set = Assert.IsType<SprintEdges>(edges.For(SprintFamily.Hammer, false));
        Assert.NotNull(set.Stop);
        Assert.Equal((SprintTransitions.DefaultBlendOut, SprintTransitions.DefaultBlendOut), (set.RunOnBlend, set.StopExitBlend));
        Assert.DoesNotContain(edges.Notes, n => n.Contains("no states"));
    }

    [Fact]
    public void The_default_maps_cover_the_hammers_sprint_graph_and_give_the_cannons_no_turn()
    {
        var maps = SprintTransitions.DefaultMaps;
        Assert.Contains(maps, m => m.Family == SprintFamily.Hammer && m.Graph == "SM_TH_NuSprint" && m.TurnState == "180 Idle to Sprint");
        Assert.Contains(maps, m => m.Family == SprintFamily.Cannon && m.TurnState is null);
        Assert.Contains(maps, m => m.Family == SprintFamily.Rifle && m.OneHanded && m.TurnClipState == "1HM 180 Sprint");
        Assert.Contains(maps, m => m.Family == SprintFamily.Rifle && !m.OneHanded && m.TurnClipState == "2HR 180 Sprint");
    }

    [Fact]
    public void The_interrupt_applies_from_the_branch_to_a_little_past_the_start_clips_end()
    {
        var window = new SprintStartWindow(0.933f, 0.4f);
        Assert.False(SprintInterruptRules.Applies(0.39f, window));
        Assert.True(SprintInterruptRules.Applies(0.4f, window));
        Assert.True(SprintInterruptRules.Applies(0.9f, window));
        Assert.True(SprintInterruptRules.Applies(0.933f + SprintInterruptRules.Slack, window));
        Assert.False(SprintInterruptRules.Applies(0.933f + SprintInterruptRules.Slack + 0.01f, window));
    }

    [Fact]
    public void The_start_tracker_is_in_its_window_only_when_armed_for_this_standing_and_within_the_span()
    {
        var start = new SprintStartTracker();
        var window = new SprintStartWindow(0.933f, 0.4f);
        double t = 5.0;
        start.Track(t, 0f, true, 0f);                       // standing
        Assert.False(start.Armed);
        Assert.False(start.InStartWindow(t + 0.6, window)); // not armed yet: a sprint that never began is no start
        var clip = new SprintEdgeClip("g", "Transition To Idle", "SM_Start", 0.933f, 0.1f, 0.933f, 0.1f, Enumerable.Range(0, 41).Select(i => i * 0.1f).ToArray(), 0.933f);
        start.Place(t + 0.2, clip, out _);                  // the sprint is shown: armed
        Assert.True(start.Armed);
        Assert.False(start.InStartWindow(t + 0.3, window)); // too early (before 0.4 s)
        Assert.True(start.InStartWindow(t + 0.7, window));
        Assert.False(start.InStartWindow(t + 2.0, window)); // a long sprint is stopped by the full stop
        start.Track(t + 2.5, 0f, true, 0f);                 // standing again: re-armed for the next sprint, not in a window
        Assert.False(start.Armed);
    }

    [Fact]
    public void The_turn_applies_beyond_100_degrees_either_way_and_the_offset_wraps()
    {
        float Rad(float d) => d * MathF.PI / 180f;
        Assert.False(SprintTurnTracker.Applies(Rad(99f)));
        Assert.True(SprintTurnTracker.Applies(Rad(101f)));
        Assert.True(SprintTurnTracker.Applies(Rad(-135f)));
        Assert.False(SprintTurnTracker.Applies(0f));
        Assert.Equal(Rad(-170f), SprintTurnTracker.Offset(Rad(10f), Rad(180f)), 1e-4f);   // 10 vs 180: the body is 170 degrees clockwise of the aim
        Assert.Equal(Rad(170f), SprintTurnTracker.Offset(Rad(-10f), Rad(-180f)), 1e-4f);
        Assert.Equal(Rad(20f), SprintTurnTracker.Offset(Rad(350f), Rad(330f)), 1e-4f);
    }

    static SprintEdgeClip TurnClip()
    {
        const int n = 38; const float duration = 1.233f;
        var turn = new float[n];
        for (int i = 0; i < n; i++) { float t = duration * i / (n - 1); turn[i] = MathF.PI * Math.Clamp((t - 0.3f) / 0.1f, 0f, 1f); }
        return new SprintEdgeClip("g", "180 Idle to Sprint", "SM_Turn", duration, 0.1f, duration, 0.1f, new float[n], duration, turn);
    }

    [Fact]
    public void The_clips_root_turn_progress_runs_from_0_to_1_over_its_turn()
    {
        var clip = TurnClip();
        Assert.Equal(0f, clip.TurnProgress(0.1f), 1e-3f);
        Assert.InRange(clip.TurnProgress(0.35f), 0.35f, 0.65f);
        Assert.Equal(1f, clip.TurnProgress(0.5f), 1e-3f);
        Assert.Equal(1f, clip.TurnProgress(5f), 1e-3f);
        Assert.Equal(0f, new SprintEdgeClip("g", "s", "c", 1f, 0.1f, 1f, 0.1f, [0f, 1f], 1f).TurnProgress(0.5f));   // a clip without turn data: no turn
    }

    [Fact]
    public void The_turn_plays_in_real_time_turns_the_body_with_the_clip_and_hands_over_to_the_loop_at_the_right_foot()
    {
        var clip = TurnClip();
        var turn = new SprintTurnTracker();
        float offset = 130f * MathF.PI / 180f;
        turn.Begin(offset, 0.15f);
        Assert.True(turn.Active);
        Assert.Equal(0.15f, turn.Time, 1e-4f);
        Assert.Equal(offset, turn.Residual(clip), 1e-3f);   // the body still stands where it did: the clip's root has not turned yet
        double now = 10.0;
        long frame = 600;
        float? seed = null;
        float lastResidual = float.MaxValue;
        int guard = 0;
        while (turn.Place(now, clip, out seed, frame) && guard++ < 1000)
        {
            float r = MathF.Abs(turn.Residual(clip));
            Assert.True(r <= lastResidual + 1e-4f);        // the body only ever turns toward the aim
            lastResidual = r;
            now += Frame; frame++;
        }
        Assert.False(turn.Active);
        Assert.Equal(SprintStartTracker.LoopSeedFraction, seed);   // the loop takes over at its right foot
        Assert.Equal(0f, lastResidual, 1e-3f);
        Assert.InRange(turn.Time, clip.BranchAt - 0.05f, clip.BranchAt + 0.05f);   // real time: it ends at the clip's branch
        Assert.Equal(0f, turn.Residual(clip));              // no turn, no residual
    }

    [Fact]
    public void A_turn_that_was_not_placed_for_more_than_three_frames_is_over_and_a_hitch_between_frames_is_not()
    {
        var clip = TurnClip();
        var turn = new SprintTurnTracker();
        turn.Begin(2f);
        Assert.True(turn.Place(1.0, clip, out _, 60));
        Assert.True(turn.Place(1.0 + 0.2, clip, out _, 61));   // one long frame: still on
        turn.Track(1.2 + 3 * Frame, 64);                        // three skipped frames: still on
        Assert.True(turn.Active);
        turn.Track(1.2 + 6 * Frame, 67);                        // more than three skipped: over
        Assert.False(turn.Active);
        Assert.False(turn.Place(1.5, clip, out var seed, 70));
        Assert.Null(seed);
    }

    [Fact]
    public void Drop_ends_the_turn_and_the_start_cannot_replay_for_this_standing_after_consume()
    {
        var turn = new SprintTurnTracker();
        turn.Begin(2f);
        turn.Drop();
        Assert.False(turn.Active);
        var start = new SprintStartTracker();
        start.Track(1.0, 0f, true, 0f);
        start.Consume();
        var clip = new SprintEdgeClip("g", "Transition To Idle", "SM_Start", 0.933f, 0.1f, 0.933f, 0.1f, Enumerable.Range(0, 41).Select(i => i * 0.1f).ToArray(), 0.933f);
        Assert.False(start.Place(1.2, clip, out _));   // the turn took this standing's start
        Assert.False(start.Active);
    }
}
