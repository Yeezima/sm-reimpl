using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Animation pass 3: the sprint's start and stop clips with their blends, branches and root motion (<see cref="SprintTransitions"/>), the base graph's ways out of the sprint,
/// the pistol's relaxed idle, the stop clip's speed mapping and the fall that a charge in the air begins at once. Synthetic packfiles and numbers only.
/// </summary>
public class SprintTransitionsTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static readonly string[] Events = ["BRANCH_Sprint_Finished", "BRANCH_Idle2SprintLoop"];
    static readonly string[] Variables = ["FLT_Input_Nav_Speed", "BOOL_SprintIsActive"];

    static StructuredGraph SprintGraph(string name, bool start) => new(name, Events, Variables, new StateMachineNode("Sprint",
    [
        new StateFixture(23, "MP Sprint - ENTRY", new ClipNode("conduit", "SM_Conduit"), start ? [new TransitionFixture(1, BlendDuration: 0.1f)] : null),
        new StateFixture(1, "Transition To Idle", new SequencerNode("SeqStart", new ClipNode("start", start ? "SM_Start" : "SM_Loop", Mode: 0), [new Tag("Branch", 1f, 1f, 1)]),
            [new TransitionFixture(2, BranchEventId: 1, BlendDuration: 0.1f)]),
        new StateFixture(2, "Sprint Loop", new SequencerNode("SeqLoop", new ClipNode("loop", "SM_Loop"), []), [new TransitionFixture(4, Priority: 2, BlendDuration: 0.267f)]),
        new StateFixture(4, "Sprint to Idle", new SequencerNode("SeqStop", new ClipNode("stop", "SM_Stop", Mode: 0), [new Tag("Branch", 0.625f, 1f, 0)])),
    ], StartStateId: 23));

    static StructuredGraph PistolNav() => new("SM_Pistol_NAV", Events, Variables, new StateMachineNode("Behavior05",
    [
        new StateFixture(0, "Idle Ready", new ClipNode("ready", "SM_1HR_Idle")),
        new StateFixture(5, "Idle Relaxed", new SequencerNode("SeqIdle", new BlendNode("Blend03", [new BlendChildFixture(0f, new ClipNode("cs", "SM_CS_Idle")), new BlendChildFixture(30f, new ClipNode("slope", "SM_Slope"))]), [])),
    ]));

    static StructuredGraph BaseGraph(float runOn, float stopExit) => new("SM_Base_MP", Events, Variables, new StateMachineNode("BolterBehavior",
    [
        new StateFixture(1, "Bolter Navigation", new ClipNode("nav", "SM_Nav")),
        new StateFixture(13, "Bolter Sprint", new ClipNode("sprint", "SM_Loop"),
        [
            new TransitionFixture(1, Priority: 1, BlendDuration: runOn, Condition: new MultiFixture(0, [new ThresholdFixture(0, 0.5f, 100f), new BoolFixture([], [1])])),
            new TransitionFixture(1, BranchEventId: 0, Priority: 3, BlendDuration: stopExit),
        ]),
    ]));

    static float Length(string clip) => clip switch { "SM_Start" => 0.933f, "SM_Stop" => 1.333f, "SM_Loop" => 0.633f, _ => 1.5f };

    // Root motion along +Y: the start speeds up from rest, the stop slows from 10 m/s to rest at 1 s and stands still to its end.
    static HavokExtractedMotion? Motion(string clip)
    {
        int n = 41;
        float duration = Length(clip);
        var samples = new System.Numerics.Vector4[n];
        float y = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = duration * i / (n - 1);
            samples[i] = new System.Numerics.Vector4(0f, y, 0f, 0f);
            float dt = duration / (n - 1);
            float v = clip == "SM_Stop" ? MathF.Max(0f, 10f * (1f - t)) : clip == "SM_Start" ? 10f * t / duration : 0f;
            y += v * dt;
        }
        return clip is "SM_Stop" or "SM_Start" ? new HavokExtractedMotion(System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY, duration, samples) : null;
    }

    static readonly SprintTransitions.Map RifleMap = new(SprintFamily.Rifle, false, "MP_Test_Sprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "Bolter Sprint", "Bolter Navigation");
    static readonly SprintTransitions.Map HeavyMap = new(SprintFamily.Heavy, false, "SM_Test_Heavy_Sprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "Heavy Bolter Sprint", "Heavy Bolter Nav");

    [Fact]
    public void The_sprint_stop_and_start_are_read_with_their_blends_branches_and_root_motion()
    {
        var edges = SprintTransitions.From(Read(SprintGraph("MP_Test_Sprint", true), SprintGraph("SM_Test_Heavy_Sprint", false), PistolNav(), BaseGraph(0.333f, 0.1f)), Length, Motion, [RifleMap, HeavyMap]);
        var rifle = Assert.IsType<SprintEdges>(edges.For(SprintFamily.Rifle, false));
        var stop = Assert.IsType<SprintEdgeClip>(rifle.Stop);
        Assert.Equal(("SM_Stop", 1.333f), (stop.Clip, stop.Length));
        Assert.Equal(0.267f, stop.BlendIn, 1e-4f);
        Assert.Equal(0.625f * 1.333f, stop.BranchAt, 1e-4f);
        var start = Assert.IsType<SprintEdgeClip>(rifle.Start);
        Assert.Equal("SM_Start", start.Clip);
        Assert.Equal((0.1f, 0.1f), (start.BlendIn, start.BlendOut));
        Assert.Equal(0.933f, start.BranchAt, 1e-4f);
        // The base graph's ways out of the sprint: with the stick held, and at the stop's branch.
        Assert.Equal((0.333f, 0.1f), (rifle.RunOnBlend, rifle.StopExitBlend));
        // The heavy graph's entry plays the loop's own clip: no start; its base states are missing, so the default blend out and a note.
        var heavy = Assert.IsType<SprintEdges>(edges.For(SprintFamily.Heavy, false));
        Assert.Null(heavy.Start);
        Assert.NotNull(heavy.Stop);
        Assert.Equal(SprintTransitions.DefaultBlendOut, heavy.RunOnBlend);
        Assert.Contains(edges.Notes, n => n.Contains("Heavy Bolter Sprint"));
        Assert.Equal("SM_CS_Idle", edges.PistolRelaxedIdle);
        // Asking for the one-handed loop of a family that has none gives the family's own.
        Assert.Same(rifle, edges.For(SprintFamily.Rifle, true));
    }

    static SprintEdgeClip StopClip() =>
        new("g", "Sprint to Idle", "SM_Stop", 1.333f, 0.267f, 0.833f, 0.333f, Enumerable.Range(0, 41).Select(i => Motion("SM_Stop")!.Samples[i].Y).ToArray(), 1.333f);

    [Fact]
    public void The_stop_clip_is_posed_where_its_root_has_slowed_to_the_body_speed()
    {
        var stop = StopClip();
        var (peak, peakAt) = stop.Peak;
        Assert.InRange(peak, 9.5f, 10.01f);
        Assert.InRange(peakAt, 0f, 0.05f);
        // The root speed is 10 (1 - t): 5 m/s at 0.5 s, 2 m/s at 0.8 s; faster than the top: the top's time; at rest: when the root stops.
        Assert.InRange(stop.TimeAtSlowingSpeed(5f), 0.45f, 0.55f);
        Assert.InRange(stop.TimeAtSlowingSpeed(2f), 0.75f, 0.85f);
        Assert.Equal(peakAt, stop.TimeAtSlowingSpeed(20f));
        Assert.InRange(stop.RestAt, 0.95f, 1.05f);
        Assert.InRange(stop.TimeAtSlowingSpeed(0f), 0.95f, 1.05f);
        // Monotonic: a slower body is further into the clip.
        float last = -1f;
        for (float v = 9.5f; v >= 0.1f; v -= 0.5f) { float t = stop.TimeAtSlowingSpeed(v); Assert.True(t >= last - 1e-4f); last = t; }
    }

    [Fact]
    public void A_charge_held_in_the_air_falls_from_its_first_unsupported_frame()
    {
        var debounced = new FallTracker();
        Assert.Equal(FallEvent.None, debounced.Observe(1.0, true, true, 0f));
        Assert.Equal(FallEvent.None, debounced.Observe(1.0 + 1.0 / 60.0, true, false, -1f));
        var immediate = new FallTracker();
        Assert.Equal(FallEvent.None, immediate.Observe(1.0, true, true, 0f));
        Assert.Equal(FallEvent.Began, immediate.Observe(1.0 + 1.0 / 60.0, true, false, -1f, immediate: true));
        Assert.True(immediate.Falling);
    }
}
