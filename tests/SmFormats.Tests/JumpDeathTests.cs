using System.Numerics;
using SmContent;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// The jump-pack death in mid-air (2026-11-11): the graph's "Jumppack Death" read with its tags (the hit clip's branch into the fall and its StartAction, the looping fall, the floor clip's
/// powered-ragdoll tag and StartAction), the route chosen in the air and nothing changed on the ground, and the run that places the dead body: the hit clip, the fall loop until the body is on a
/// floor, the floor clip, its impact action, the ragdoll at its tag, the cap without a floor, walls and ceilings, a floor reached before the branch. Synthetic graphs and levels only.
/// </summary>
public class JumpDeathTests
{
    const int JumpDeathStart = 0, StartAction = 1;
    static readonly string[] Events = ["BRANCH_Jump_Death_Start", "StartAction", "BRANCH_Death_Ranged_Left"];
    const int IsDead = 0, IsRagdoll = 1, DeathDisabled = 2, Jump = 3;
    static readonly string[] Variables = ["BOOL_IsDead", "BOOL_IsRagdoll", "BOOL_Death_State_Disabled", "BOOL_JumpIsActive"];
    static readonly ConditionFixture Dead = new BoolFixture([IsDead], [IsRagdoll, DeathDisabled]);

    // The install's lengths (s): the hit 0.6, the fall loop 1.467 at speed 0.65, the floor 0.833; a default death for the ground.
    static float Length(string clip) => clip switch { "SM_Air_Hit" => 0.6f, "SM_Air_Fall" => 1.467f, "SM_Air_Impact" => 0.833f, "SM_Death_F" => 0.733f, "SM_Death_B" => 0.667f, _ => 0.5f };

    static Tag Ragdoll(float at) => new("Modifier", at, 1f, new ModifierValue("hkbPoweredRagdollControlsModifier", "Powered Ragdoll Controls Modifier07"));

    /// <summary>A base graph with the install's jump death (a modifier generator over its machine; the fall a blender with the floor clip at weight 0, as on the install) and a default death.</summary>
    static IReadOnlyList<BehaviorGraphDetail> Graphs(bool withFloorClip = true)
    {
        var jump = new StateMachineNode("Jumppack Death Behavior",
        [
            new StateFixture(0, "Jumppack Death Start", new SequencerNode("Jumppack Death Start Sequencer", new ClipNode("air hit", "SM_Air_Hit", Mode: 0),
                [new Tag("Branch", 0.9412f, 1f, JumpDeathStart), new Tag("Event", 0.0333f, 0.0333f, StartAction)]),
                [new TransitionFixture(1, BranchEventId: JumpDeathStart, Priority: 1, BlendDuration: 0.1f)]),
            new StateFixture(1, "Jumppack Death Fall", new BlendNode("Blend06",
            [
                new BlendChildFixture(1f, new SequencerNode("Jumppack Death Fall Sequencer", new ClipNode("air fall", "SM_Air_Fall", PlaybackSpeed: 0.65f, Mode: 1), [new Tag("FloorCollisionTest", 0f, 1f)])),
                new BlendChildFixture(0f, new ClipNode("air impact posed", "SM_Air_Impact", Mode: 2)),
            ]), [new TransitionFixture(2, Priority: 2)]),
            .. withFloorClip
                ? new[] { new StateFixture(2, "Jumppack Death Hit Floor", new SequencerNode("Jumppack Hit Floor Sequencer", new ClipNode("air impact", "SM_Air_Impact", Mode: 0),
                    [Ragdoll(0.44f), new Tag("Event", 0.04f, 0.04f, StartAction)])) }
                : [],
        ]);
        var death = new StateMachineNode("Behavior Death",
        [
            new StateFixture(0, "Death - Default", new StateMachineNode("Default Death Behavior",
            [
                new StateFixture(0, "Default Death Front", new SequencerNode("df", new ClipNode("df", "SM_Death_F", Mode: 0), [Ragdoll(0.8182f)])),
                new StateFixture(1, "Default Death Back", new SequencerNode("db", new ClipNode("db", "SM_Death_B", Mode: 0), [Ragdoll(0.8f)])),
            ])),
            new StateFixture(1, "ENTRY - Death State", new ClipNode("conduit", "SM_Ref"), [new TransitionFixture(0, Priority: 1, BlendDuration: 0.5f, EffectName: "Long Blend00")]),
            new StateFixture(15, "Jumppack Death", new ModifierGeneratorNode("ModifierGenerator10", jump)),
        ], StartStateId: 1);
        TransitionFixture ToDeath(short priority, int nested, ConditionFixture? condition = null) =>
            new(63, Priority: priority, BlendDuration: 0.1f, Condition: condition ?? Dead, ToNestedStateId: nested);
        var root = new StateMachineNode("SM_Base Behavior",
        [
            new StateFixture(63, "DEATH", new ModifierGeneratorNode("Death Modifier Generator", death)),
            new StateFixture(2, "ROOT", new ClipNode("idle", "SM_Idle"),
                [ToDeath(10, 15, new MultiFixture(0, [new BoolFixture([Jump], []), Dead])), ToDeath(1, -1)]),
        ], StartStateId: 2);
        return HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure([new StructuredGraph(SwitchAnimations.Graph, Events, Variables, root)])));
    }

    static DeathAnimations Death(bool withFloorClip = true) => DeathAnimations.From(Graphs(withFloorClip), Length);

    static DeathContext Ctx(bool jump, string[]? branches = null, bool blast = false, bool sprint = false, float along = -1f, float right = 0f) =>
        new(jump, sprint, false, blast, branches ?? [], true, along, right);

    static JumpDeathRun Run(DeathAnimations d, Vector3 feet, Vector3 velocity, float gravity = 20f, float maxFall = 60f) =>
        new(d.Get(MarineDeathKind.JumpStart)!, d.Get(MarineDeathKind.JumpFall)!, d.Get(MarineDeathKind.JumpImpact)!, feet, velocity, gravity, maxFall);

    /// <summary>A flat floor at z = 0 (a ray down from above it).</summary>
    static float? Floor(Vector3 origin, Vector3 direction, float max) => direction.Z < -1e-6f && origin.Z >= 0f && origin.Z / -direction.Z <= max ? origin.Z / -direction.Z : null;
    /// <summary>A pit: nothing to land on.</summary>
    static float? Pit(Vector3 origin, Vector3 direction, float max) => null;

    const float Dt = 1f / 120f;

    /// <summary>Steps a run until the ragdoll (at most 10 s), noting when each event first came.</summary>
    static Dictionary<JumpDeathEvents, float> Play(JumpDeathRun run, JumpDeathRay ray, List<(float Age, JumpDeathPhase Phase, float Playback, float Z)>? frames = null)
    {
        var first = new Dictionary<JumpDeathEvents, float>();
        for (int i = 0; i < 1200 && run.Phase != JumpDeathPhase.Ragdoll; i++)
        {
            var events = run.Step(Dt, ray);
            foreach (var e in new[] { JumpDeathEvents.Fall, JumpDeathEvents.Landed, JumpDeathEvents.Impact, JumpDeathEvents.ImpactAction, JumpDeathEvents.Ragdoll })
                if ((events & e) != 0 && !first.ContainsKey(e)) first[e] = run.Age;
            if (frames is not null && run.Phase != JumpDeathPhase.Ragdoll) frames.Add((run.Age, run.Phase, run.Shown().Playback, run.Feet.Z));
        }
        return first;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------- the data

    [Fact]
    public void The_jump_death_clips_and_their_action_tags_are_read()
    {
        var d = Death();
        var hit = d.Get(MarineDeathKind.JumpStart)!;
        var fall = d.Get(MarineDeathKind.JumpFall)!;
        var impact = d.Get(MarineDeathKind.JumpImpact)!;
        Assert.Equal(("SM_Air_Hit", "SM_Air_Fall", "SM_Air_Impact"), (hit.Clip, fall.Clip, impact.Clip));
        Assert.Equal(0.9412f * 0.6f, hit.RagdollAt, 1e-3f);          // the branch into the fall: 0.565 s
        Assert.Equal(0.0333f * 0.6f, hit.ActionAt, 1e-3f);           // actn_jumpdeath_start: 0.02 s, the effects' own delay
        Assert.Equal(DeathEffectRules.StartOf(DeathKind.Jump), hit.ActionAt, 1e-3f);
        Assert.Equal(1.467f / 0.65f, fall.Length, 1e-3f);            // the loop's playback length (speed 0.65), under the blender's weight-1 child
        Assert.Equal(0.44f * 0.833f, impact.RagdollAt, 1e-3f);       // the powered ragdoll from 0.367 s
        Assert.Equal(0.04f * 0.833f, impact.ActionAt, 1e-3f);        // actn_jumpdeath_impact: 0.033 s into the floor clip
        Assert.Equal(0.04f * 0.833f, JumpDeathRules.ImpactActionAt(impact), 1e-3f);
        Assert.Equal(0f, impact.Blend, 1e-4f);                       // the fall's floor transitions have no blend effect
        Assert.Equal(0.1f, fall.Blend, 1e-4f);
        Assert.True(float.IsNaN(d.Get(MarineDeathKind.DefaultFront)!.ActionAt));   // a death state without the event tag
        // Without the tag the floor's action falls back to the frame after the state is entered (1/30 s, where every death state's tag lands).
        Assert.Equal(DeathEffectRules.StartActionDelay, JumpDeathRules.ImpactActionAt(impact with { ActionAt = float.NaN }), 1e-6f);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------- the route

    [Fact]
    public void A_kill_in_a_jump_pack_jump_takes_the_jump_death_whatever_the_blow()
    {
        var d = Death();
        foreach (var data in new DeathAnimations?[] { d, null })   // the read routes and the install's defaults agree
        {
            Assert.Equal(MarineDeathKind.JumpStart, DeathRules.Choose(Ctx(jump: true), data));
            Assert.Equal(MarineDeathKind.JumpStart, DeathRules.Choose(Ctx(jump: true, branches: ["BRANCH_Death_Ranged_Left"]), data));   // priority 10 before the shot's branch
            Assert.Equal(MarineDeathKind.JumpStart, DeathRules.Choose(Ctx(jump: true, blast: true), data));   // no reaction in flight: a blast in the air takes the jump death
            Assert.Equal(MarineDeathKind.JumpStart, DeathRules.Choose(Ctx(jump: true, sprint: true, along: 1f), data));
        }
        Assert.True(JumpDeathRules.Applies(jumpActive: true, fellOutOfLevel: false, d));
        Assert.False(JumpDeathRules.Applies(jumpActive: true, fellOutOfLevel: true, d));      // a fall out of the level: the ragdoll at once, as before
        Assert.False(JumpDeathRules.Applies(jumpActive: true, fellOutOfLevel: false, null));  // no data
        Assert.False(JumpDeathRules.Applies(jumpActive: true, fellOutOfLevel: false, Death(withFloorClip: false)));   // a clip missing: not played
    }

    [Fact]
    public void Nothing_changes_on_the_ground()
    {
        var d = Death();
        Assert.False(JumpDeathRules.Applies(jumpActive: false, fellOutOfLevel: false, d));
        Assert.Equal(MarineDeathKind.DefaultFront, DeathRules.Choose(Ctx(jump: false), d));
        Assert.Equal(MarineDeathKind.DefaultBack, DeathRules.Choose(Ctx(jump: false, along: 1f), d));
        Assert.Equal(MarineDeathKind.RangedLeft, DeathRules.Choose(Ctx(jump: false, branches: ["BRANCH_Death_Ranged_Left"])));
        Assert.True(DeathRules.Holds(fellOutOfLevel: false, airborneAtLastLivingFrame: false));   // the standing clip as before
        Assert.False(DeathRules.Holds(fellOutOfLevel: false, airborneAtLastLivingFrame: true));   // airborne without a jump: the ragdoll at once, as before
        Assert.Equal(DeathKind.None, DeathEffectRules.Choose(jumping: false, hasJumpPack: true, DeathFamily.None));   // a jump-pack marine on the ground: no jump death effects
        Assert.Equal(DeathKind.Jump, DeathEffectRules.Choose(jumping: true, hasJumpPack: true, DeathFamily.Plasma));
        Assert.Equal(1.5f, DeathRules.HoldSeconds(new DeathClip("c", 3f, 2.4f), 1.5f), 1e-6f);   // the standing hold's cap is untouched
    }

    // ---------------------------------------------------------------------------------------------------------------------------------- the run

    [Fact]
    public void The_hit_clip_hands_over_to_the_fall_loop_which_loops_until_the_floor()
    {
        var d = Death();
        var fall = d.Get(MarineDeathKind.JumpFall)!;
        // 20 m up, at rest, under 4 m/s² (slow, so that the loop wraps): about 3.2 s to the floor, under the cap.
        var run = Run(d, new Vector3(0f, 0f, 20f), Vector3.Zero, gravity: 4f);
        var frames = new List<(float Age, JumpDeathPhase Phase, float Playback, float Z)>();
        var first = Play(run, Floor, frames);
        Assert.Equal(0.9412f * 0.6f, first[JumpDeathEvents.Fall], 2 * Dt);
        Assert.All(frames.Where(f => f.Age < 0.9412f * 0.6f - Dt), f => Assert.Equal(JumpDeathPhase.Hit, f.Phase));
        var loop = frames.Where(f => f.Phase == JumpDeathPhase.Fall).ToList();
        Assert.True(loop.Count > 0);
        Assert.All(loop, f => Assert.True(f.Z > 0f && f.Playback >= 0f && f.Playback < fall.Length));
        // The loop wraps (mode 1): its playback falls back once it passes the clip's length, and the body is still falling.
        Assert.Contains(loop.Zip(loop.Skip(1)), p => p.Second.Playback < p.First.Playback);
        Assert.True(loop[^1].Age - loop[0].Age > fall.Length);
        // The floor ends it: the feet on the floor, the floor clip from the landing.
        Assert.Equal(first[JumpDeathEvents.Landed], first[JumpDeathEvents.Impact], 1e-5f);
        Assert.Equal(MathF.Sqrt(2f * 20f / 4f), run.LandedAt, 2 * Dt);
        Assert.Equal(0f, run.Feet.Z, 1e-3f);
        Assert.Equal(run.LandedAt, run.ImpactStartedAt, 1e-5f);
        Assert.Equal(20f, run.Dropped, 1e-3f);
        Assert.Equal(run.LandedAt - 0.9412f * 0.6f, run.FallSeconds, 2 * Dt);
    }

    [Fact]
    public void The_floor_clip_fires_its_action_then_hands_the_body_to_the_ragdoll_at_its_tag()
    {
        var d = Death();
        var impact = d.Get(MarineDeathKind.JumpImpact)!;
        // A Raptor shot 6 m up while rising (3 m/s) and flying ahead (4 m/s), under the world's 20 m/s².
        var run = Run(d, new Vector3(0f, 0f, 6f), new Vector3(0f, 4f, 3f));
        var first = Play(run, Floor);
        float floor = run.ImpactStartedAt;
        Assert.Equal(floor + 0.04f * 0.833f, first[JumpDeathEvents.ImpactAction], 2 * Dt);   // actn_jumpdeath_impact at the floor clip's StartAction
        Assert.Equal(floor + 0.44f * 0.833f, first[JumpDeathEvents.Ragdoll], 2 * Dt);         // the ragdoll at 0.367 s into the floor clip
        Assert.Equal(JumpDeathEnd.Floor, run.End);
        Assert.True(run.ImpactActionFired);
        Assert.Equal(impact.RagdollAt, run.PhaseAge, 2 * Dt);
        // The ballistic arc: the apex 0.15 s up, then the drop of 6.225 m.
        Assert.Equal(0.15f + MathF.Sqrt(2f * 6.225f / 20f), run.LandedAt, 3 * Dt);
        Assert.True(run.Feet.Y > 4f * run.LandedAt - 0.1f);   // it flew on ahead, then slid a little on the floor
        Assert.Equal(0f, run.Velocity.Z);
        // The floor clip is shown from its entry with its own blend; the run never shows the floor clip before the landing.
        Assert.Equal(JumpDeathPhase.Ragdoll, run.Phase);
    }

    [Fact]
    public void The_lowest_point_of_the_posed_body_meets_the_floor_first_and_the_feet_follow_in_the_floor_clip()
    {
        // The install's fall clip hangs the hands about 0.5 m below the feet: the floor clip begins when they reach the floor (BOOL_Floor_Hit), the body falls on to its feet (BOOL_IsSupported).
        var d = Death();
        var run = Run(d, new Vector3(0f, 0f, 6f), Vector3.Zero);
        var first = new Dictionary<JumpDeathEvents, float>();
        float feetAtImpact = float.NaN;
        for (int i = 0; i < 1200 && run.Phase != JumpDeathPhase.Ragdoll; i++)
        {
            var events = run.Step(Dt, Floor, bottom: -0.5f);
            foreach (var e in new[] { JumpDeathEvents.FloorHit, JumpDeathEvents.Landed, JumpDeathEvents.Impact, JumpDeathEvents.Ragdoll })
                if ((events & e) != 0 && !first.ContainsKey(e)) first[e] = run.Age;
            if ((events & JumpDeathEvents.Impact) != 0) feetAtImpact = run.Feet.Z;
        }
        Assert.InRange(feetAtImpact, 0.3f, 0.5f);   // the hands at the floor, the feet still up to half a metre above it
        Assert.True(first[JumpDeathEvents.FloorHit] < first[JumpDeathEvents.Landed]);
        Assert.Equal(first[JumpDeathEvents.FloorHit], first[JumpDeathEvents.Impact], 1e-5f);
        Assert.Equal(run.FloorHitAt, run.ImpactStartedAt, 1e-5f);
        Assert.InRange(run.LandedAt - run.ImpactStartedAt, Dt, 0.1f);   // on its feet within the floor clip's first tenth of a second (falling at about 10 m/s)
        Assert.Equal(run.ImpactStartedAt + 0.44f * 0.833f, first[JumpDeathEvents.Ragdoll], 2 * Dt);   // the tag counts from the floor clip's start
        Assert.Equal(0f, run.Feet.Z, 1e-3f);
        // A lowest point above the feet counts as the feet; one far below is bounded.
        var above = Run(d, new Vector3(0f, 0f, 6f), Vector3.Zero);
        var plain = Run(d, new Vector3(0f, 0f, 6f), Vector3.Zero);
        for (int i = 0; i < 1200 && above.Phase != JumpDeathPhase.Ragdoll; i++) { above.Step(Dt, Floor, bottom: 0.4f); plain.Step(Dt, Floor); }
        Assert.Equal(plain.ImpactStartedAt, above.ImpactStartedAt, 1e-5f);
        var deep = Run(d, new Vector3(0f, 0f, 6f), Vector3.Zero);
        for (int i = 0; i < 1200 && deep.Phase is JumpDeathPhase.Hit or JumpDeathPhase.Fall; i++) deep.Step(Dt, Floor, bottom: -9f);
        Assert.True(deep.Feet.Z <= JumpDeathRun.MaxBottom + 0.2f);
    }

    [Fact]
    public void A_body_with_no_floor_goes_to_the_ragdoll_at_the_cap_without_an_impact()
    {
        var d = Death();
        var run = Run(d, new Vector3(0f, 0f, 8f), new Vector3(2f, 0f, 0f));
        var first = Play(run, Pit);
        Assert.Equal(JumpDeathRules.FallCap, first[JumpDeathEvents.Ragdoll], 2 * Dt);
        Assert.Equal(4f, JumpDeathRules.FallCap);   // ours, the same as the impact's wait for a ragdoll's floor
        Assert.Equal(DeathEffectRules.JumpImpactLimit, JumpDeathRules.FallCap);
        Assert.Equal(JumpDeathEnd.Cap, run.End);
        Assert.False(first.ContainsKey(JumpDeathEvents.Impact));
        Assert.False(first.ContainsKey(JumpDeathEvents.ImpactAction));
        Assert.False(run.ImpactActionFired);
        Assert.True(float.IsNaN(run.ImpactStartedAt));
        // The ragdoll takes the falling body's velocity: 20 m/s² for 4 s, capped by the fall speed (60), still moving along.
        Assert.Equal(-60f, run.Velocity.Z, 1e-3f);
        Assert.Equal(2f, run.Velocity.X, 1e-4f);
        // A slower fall cap: the capped speed holds.
        var slow = Run(d, new Vector3(0f, 0f, 8f), Vector3.Zero, maxFall: 30f);
        Play(slow, Pit);
        Assert.Equal(-30f, slow.Velocity.Z, 1e-3f);
    }

    [Fact]
    public void A_body_on_the_floor_before_the_branch_goes_from_the_hit_clip_to_the_floor_clip()
    {
        // A dive killed half a metre up: on the floor in a few hundredths, the hit clip plays on to its branch, then the floor clip at once (no frame of the fall loop).
        var d = Death();
        var run = Run(d, new Vector3(0f, 0f, 0.5f), new Vector3(0f, 6f, -12f));
        var frames = new List<(float Age, JumpDeathPhase Phase, float Playback, float Z)>();
        var first = Play(run, Floor, frames);
        Assert.True(first[JumpDeathEvents.Landed] < 0.1f);
        Assert.False(first.ContainsKey(JumpDeathEvents.Fall));
        Assert.DoesNotContain(frames, f => f.Phase == JumpDeathPhase.Fall);
        Assert.Equal(0.9412f * 0.6f, first[JumpDeathEvents.Impact], 2 * Dt);
        Assert.Equal(0.9412f * 0.6f + 0.44f * 0.833f, first[JumpDeathEvents.Ragdoll], 2 * Dt);
        Assert.All(frames.Where(f => f.Age > 0.1f), f => Assert.Equal(0f, f.Z, 1e-3f));   // on the floor: the slide keeps it there
        // The slide slows the body (time constant 0.2 s): by the ragdoll it has nearly stopped.
        Assert.True(MathF.Abs(run.Velocity.Y) < 0.6f);
        Assert.Equal(0f, run.Velocity.Z);
    }

    [Fact]
    public void Walls_stop_the_motion_along_the_level_and_ceilings_the_rise()
    {
        // A floor at z = 0, a wall at x = 1, a ceiling at z = 4: the body flies toward the wall and up into the ceiling.
        static float? Room(Vector3 o, Vector3 dir, float max)
        {
            float? best = null;
            void Plane(float t) { if (t >= 0f && t <= max && (best is null || t < best)) best = t; }
            if (dir.Z < -1e-6f && o.Z >= 0f) Plane(o.Z / -dir.Z);
            if (dir.X > 1e-6f && o.X <= 1f) Plane((1f - o.X) / dir.X);
            if (dir.Z > 1e-6f && o.Z <= 4f) Plane((4f - o.Z) / dir.Z);
            return best;
        }
        var d = Death();
        var run = Run(d, new Vector3(0f, 0f, 1.5f), new Vector3(5f, 0f, 12f));
        float highest = 0f;
        for (int i = 0; i < 1200 && run.Phase != JumpDeathPhase.Ragdoll; i++)
        {
            run.Step(Dt, Room);
            Assert.True(run.Feet.X <= 1f - JumpDeathRules.WallMargin + 1e-3f, $"through the wall at {run.Feet.X}");
            highest = MathF.Max(highest, run.Feet.Z);
        }
        Assert.True(highest + JumpDeathRules.BodyTop <= 4f + 0.25f, $"through the ceiling: feet {highest}");
        Assert.Equal(JumpDeathEnd.Floor, run.End);
        Assert.Equal(0f, run.Feet.Z, 1e-3f);
    }

    [Fact]
    public void Each_phase_is_entered_once_the_loop_wraps_without_a_new_entry_and_a_cancel_ends_it()
    {
        var d = Death();
        var run = Run(d, new Vector3(0f, 0f, 20f), Vector3.Zero, gravity: 3f);   // about 3.7 s to the floor: the loop wraps once
        var entries = new List<(JumpDeathPhase, float)>();
        for (int i = 0; i < 1200 && run.Phase != JumpDeathPhase.Ragdoll; i++)
        {
            run.Step(Dt, Floor);
            if (run.Phase == JumpDeathPhase.Ragdoll) break;
            var (_, _, entered) = run.Shown();
            if (entered) entries.Add((run.Phase, run.Age));
        }
        Assert.Equal([JumpDeathPhase.Hit, JumpDeathPhase.Fall, JumpDeathPhase.Impact], entries.Select(e => e.Item1));
        // The client cancels (a late cause: a fall out of the level): the ragdoll at once, no impact.
        var cancelled = Run(d, new Vector3(0f, 0f, 10f), Vector3.Zero);
        cancelled.Step(0.05f, Floor);
        cancelled.Cancel();
        Assert.Equal((JumpDeathPhase.Ragdoll, JumpDeathEnd.Cancelled), (cancelled.Phase, cancelled.End));
        Assert.Equal(JumpDeathEvents.None, cancelled.Step(0.1f, Floor));
        Assert.False(cancelled.ImpactActionFired);
        // A frame longer than the run's limit (a hitch) advances at most that much.
        var hitch = Run(d, new Vector3(0f, 0f, 10f), Vector3.Zero);
        hitch.Step(2f, Floor);
        Assert.Equal(JumpDeathRules.MaxFrame, hitch.Age, 1e-4f);
    }
}
