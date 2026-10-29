using System.Numerics;
using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// Animation pass 3, review S6: the sprint stop and start state machines (<see cref="SprintStopTracker"/>, <see cref="SprintStartTracker"/>) and the cross-fade maths (<see cref="PoseBlendMath"/>),
/// engine-free. Includes the review's M1 cases: a stop an action interrupts does not resume later, Fire during a stop ends it, a cancelled simulation stop ends it.
/// </summary>
public class SprintEdgeRulesTests
{
    const double Frame = 1.0 / 60.0;
    const float Run = 5.85f;

    // A stop clip whose root speed is 10 (1 - t) m/s: 1.333 s long, at rest at 1.0 s, branch at 0.833 s.
    static SprintEdgeClip StopClip()
    {
        const int n = 41; const float duration = 1.333f;
        var forward = new float[n];
        float y = 0f;
        for (int i = 0; i < n; i++) { forward[i] = y; float t = duration * i / (n - 1); y += MathF.Max(0f, 10f * (1f - t)) * duration / (n - 1); }
        return new SprintEdgeClip("g", "Sprint to Idle", "SM_Stop", duration, 0.267f, 0.833f, 0.333f, forward, duration);
    }

    // A start clip whose root speeds up linearly to 9.6 m/s over 0.933 s (4.5 m).
    static SprintEdgeClip StartClip()
    {
        const int n = 41; const float duration = 0.933f;
        var forward = new float[n];
        for (int i = 0; i < n; i++) { float t = duration * i / (n - 1); forward[i] = 0.5f * (9.6f / duration) * t * t; }
        return new SprintEdgeClip("g", "Transition To Idle", "SM_Start", duration, 0.1f, duration, 0.1f, forward, duration);
    }

    // The frame number defaults to the 60 fps frame of the time (consecutive frames at 1/60 s steps; a gap in time is skipped frames); a hitch passes it explicitly.
    static StopFrame F(double now, float speed, bool flag = true, bool eligible = true, bool ready = false, float decel = 0f, long? frame = null) =>
        new(now, eligible, ready, flag, speed, speed, decel, frame ?? FrameOf(now));

    static long FrameOf(double now) => (long)Math.Round(now / Frame);

    // The simulation's stop: v² falls at a constant rate over 0.833 s from 9 m/s.
    static float SimSpeed(double since) => MathF.Sqrt(MathF.Max(0f, 81f * (1f - (float)since / 0.833f)));

    [Fact]
    public void The_simulations_stop_starts_the_clip_at_sprint_speed_and_plays_it_to_the_end_over_the_slow_stop()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        double t = 1.0 + Frame;
        Assert.Equal(StopEnd.None, stop.Update(F(t, SimSpeed(0)), clip, Run));
        Assert.True(stop.Active);
        Assert.InRange(stop.Time, 0f, 0.15f);   // 9 m/s is near the clip's top (10 m/s at 0)
        StopEnd ended = StopEnd.None;
        float last = stop.Time;
        for (int i = 1; i < 200 && ended == StopEnd.None; i++)
        {
            double since = i * Frame;
            bool flag = since < 0.833;
            ended = stop.Update(F(t + since, SimSpeed(since), flag), clip, Run);
            if (ended == StopEnd.None) { Assert.True(stop.Time >= last); last = stop.Time; }
        }
        Assert.Equal(StopEnd.Done, ended);   // never given up while the flag held, nor when it dropped at rest
        Assert.False(stop.Active);
    }

    [Fact]
    public void An_interrupted_stop_does_not_resume_later()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        Assert.Equal(StopEnd.None, stop.Update(F(1.02, 9f), clip, Run));
        // A stun or a roll: not eligible for a frame.
        Assert.Equal(StopEnd.Interrupted, stop.Update(F(1.04, 7f, eligible: false), clip, Run));
        // At rest afterwards, the flag gone: nothing starts again (one stop per sprint).
        Assert.Equal(StopEnd.None, stop.Update(F(1.6, 0f, flag: false), clip, Run));
        Assert.False(stop.Active);
    }

    [Fact]
    public void A_stop_an_action_clip_hid_is_stale_and_does_not_resume()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        stop.Update(F(1.02, 9f), clip, Run);
        // The networked view skipped the locomotion for 0.4 s (a reaction clip showed): placement frames were skipped; back at rest.
        Assert.Equal(StopEnd.Stale, stop.Update(F(1.42, 0.2f, flag: false), clip, Run));
        Assert.False(stop.Active);
        Assert.Equal(StopEnd.None, stop.Update(F(1.44, 0.2f, flag: false), clip, Run));
        Assert.False(stop.Active);
    }

    [Fact]
    public void Fire_during_a_stop_ends_it_and_none_starts_with_the_weapon_up()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        stop.Update(F(1.02, 9f), clip, Run);
        Assert.Equal(StopEnd.Interrupted, stop.Update(F(1.04, 8f, ready: true), clip, Run));
        var other = new SprintStopTracker();
        other.SprintShown(1.0);
        Assert.Equal(StopEnd.None, other.Update(F(1.02, 9f, ready: true), clip, Run));
        Assert.False(other.Active);
    }

    [Fact]
    public void A_cancelled_simulation_stop_ends_the_clip_while_the_body_still_moves()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        stop.Update(F(1.02, 9f), clip, Run);
        Assert.Equal(StopEnd.Cancelled, stop.Update(F(1.04, 6f, flag: false), clip, Run));
    }

    [Fact]
    public void Without_the_flag_a_hard_slowdown_below_the_run_is_a_stop_and_running_on_is_not()
    {
        var clip = StopClip();
        var stop = new SprintStopTracker();
        stop.SprintShown(1.0);
        Assert.Equal(StopEnd.None, stop.Update(F(1.05, 4.5f, flag: false, decel: 40f), clip, Run));
        Assert.True(stop.Active);
        var runOn = new SprintStopTracker();
        runOn.SprintShown(1.0);
        for (int i = 1; i < 30; i++) runOn.Update(F(1.0 + i * Frame, Run, flag: false, decel: i < 6 ? 40f : 0f), clip, Run);
        Assert.False(runOn.Active);
        // Slowing gently (a walk) is not a stop either.
        var gentle = new SprintStopTracker();
        gentle.SprintShown(1.0);
        gentle.Update(F(1.05, 3f, flag: false, decel: 5f), clip, Run);
        Assert.False(gentle.Active);
    }

    [Fact]
    public void The_start_follows_the_distance_at_most_three_times_real_time_and_hands_over_at_the_right_foot()
    {
        var start = new SprintStartTracker();
        var clip = StartClip();
        start.Track(1.0, 0f, true, 0f);   // standing
        double t = 1.0;
        float? seed = null;
        bool shown = false;
        int frames = 0;
        float last = 0f;
        for (int i = 1; i < 120; i++)
        {
            t += Frame;
            start.Track(t, 9f, true, 9f * (float)Frame);   // the simulation is at sprint speed at once
            if (start.Place(t, clip, out seed)) { shown = true; frames++; Assert.True(start.Time >= last); Assert.True(start.Time - last <= 3f * (float)Frame + 1e-3f || frames == 1); last = start.Time; }
            else if (seed is not null) break;
        }
        Assert.True(shown);
        Assert.Equal(SprintStartTracker.LoopSeedFraction, seed);
        // Not played again until the marine stands.
        Assert.False(start.Place(t + Frame, clip, out _));
    }

    [Fact]
    public void A_start_that_was_left_does_not_resume()
    {
        var start = new SprintStartTracker();
        var clip = StartClip();
        start.Track(1.0, 0f, true, 0f, 60);
        start.Track(1.1, 9f, true, 0.3f, 66);
        Assert.True(start.Place(1.1, clip, out _, 66));
        start.Track(1.5, 9f, true, 3f, 90);   // not placed for 0.4 s (a charge): frames 67 to 89 skipped
        Assert.False(start.Active);
        Assert.False(start.Place(1.5, clip, out _, 90));
        // Without frame numbers the time backstop (0.5 s) ends it.
        var late = new SprintStartTracker();
        late.Track(1.0, 0f, true, 0f);
        late.Track(1.1, 9f, true, 0.3f);
        Assert.True(late.Place(1.1, clip, out _));
        late.Track(1.7, 9f, true, 3f);
        Assert.False(late.Active);
    }

    [Fact]
    public void A_hitch_of_0_2_s_between_consecutive_frames_does_not_end_a_stop_or_a_start()
    {
        // Review S-2: Stale was on wall time (0.15 s), so one long frame ended a stop for good. Now only skipped placement frames do; with frame numbers no time limit applies.
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        Assert.Equal(StopEnd.None, stop.Update(F(1.02, 9f, frame: 100), clip, Run));
        Assert.True(stop.Active);
        Assert.Equal(StopEnd.None, stop.Update(F(1.22, 6f, frame: 101), clip, Run));   // the next frame, 0.2 s later
        Assert.True(stop.Active);
        // A hitch of 0.6 s between consecutive frames: still no end (the time backstop is only for callers without frame numbers).
        var slow = new SprintStopTracker();
        slow.SprintShown(1.0);
        slow.Update(F(1.02, 9f, frame: 10), clip, Run);
        Assert.Equal(StopEnd.None, slow.Update(F(1.62, 5f, frame: 11), clip, Run));
        Assert.True(slow.Active);

        var start = new SprintStartTracker();
        var startClip = StartClip();
        start.Track(1.0, 0f, true, 0f, 200);
        start.Track(1.02, 9f, true, 0.2f, 201);
        Assert.True(start.Place(1.02, startClip, out _, 201));
        start.Track(1.22, 9f, true, 1.8f, 202);   // 0.2 s hitch
        Assert.True(start.Active);
        Assert.True(start.Place(1.22, startClip, out _, 202));
    }

    [Fact]
    public void A_remote_flicker_of_up_to_three_skipped_frames_keeps_the_stop_and_four_end_it()
    {
        // Review: a remote marine's one-snapshot reaction or throw flag hides the locomotion for a frame or two; the stop goes on. A real action clip skips far more frames.
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        Assert.Equal(StopEnd.None, stop.Update(F(1.02, 9f, frame: 100), clip, Run));
        Assert.Equal(StopEnd.None, stop.Update(F(1.04, 8.5f, frame: 101), clip, Run));
        Assert.Equal(StopEnd.None, stop.Update(F(1.10, 7.5f, frame: 105), clip, Run));   // frames 102 to 104 skipped
        Assert.True(stop.Active);
        Assert.Equal(StopEnd.Stale, stop.Update(F(1.20, 6f, frame: 110), clip, Run));    // frames 106 to 109 skipped
        Assert.False(stop.Active);
        Assert.Equal(StopEnd.None, stop.Update(F(1.22, 6f, frame: 111), clip, Run));     // and it never resumes (review M1)
        Assert.False(stop.Active);

        var start = new SprintStartTracker();
        var startClip = StartClip();
        start.Track(1.0, 0f, true, 0f, 200);
        start.Track(1.02, 9f, true, 0.2f, 201);
        Assert.True(start.Place(1.02, startClip, out _, 201));
        start.Track(1.09, 9f, true, 0.6f, 205);   // 202 to 204 skipped
        Assert.True(start.Active);
        Assert.True(start.Place(1.09, startClip, out _, 205));
        start.Track(1.19, 9f, true, 0.9f, 210);   // 206 to 209 skipped
        Assert.False(start.Active);
        Assert.Equal(3, SprintEdgeStaleness.MaxSkippedFrames);
    }

    [Fact]
    public void For_falls_back_to_the_familys_first_set_follows_a_with_copy_and_equality_ignores_the_cache()
    {
        var rifle = new SprintEdges(SprintFamily.Rifle, false, null, StopClip(), 0.333f, 0.333f);
        var pistol = new SprintEdges(SprintFamily.Rifle, true, StartClip(), StopClip(), 0.1f, 0.1f);
        var heavy = new SprintEdges(SprintFamily.Heavy, false, null, null, 0.1f, 0.1f);
        var t = new SprintTransitions(new[] { rifle, pistol, heavy }, "idle", Array.Empty<string>());
        Assert.Same(rifle, t.For(SprintFamily.Rifle, false));
        Assert.Same(pistol, t.For(SprintFamily.Rifle, true));
        Assert.Same(heavy, t.For(SprintFamily.Heavy, true));     // no one-handed heavy set: the family's first
        Assert.Null(t.For(SprintFamily.Cannon, false));           // no set of the family at all
        // A copy with other sets looks them up, not the copied cache.
        var cannon = new SprintEdges(SprintFamily.Cannon, false, null, null, 0.2f, 0.2f);
        var copy = t with { Sets = new[] { cannon } };
        Assert.Same(cannon, copy.For(SprintFamily.Cannon, false));
        Assert.Null(copy.For(SprintFamily.Rifle, false));
        Assert.Same(rifle, t.For(SprintFamily.Rifle, false));
        // Equality and the hash are over the data only: a used and an unused instance with the same data are equal.
        var sets = new[] { rifle, heavy };
        var notes = Array.Empty<string>();
        var used = new SprintTransitions(sets, "idle", notes);
        var fresh = new SprintTransitions(sets, "idle", notes);
        used.For(SprintFamily.Rifle, false);
        Assert.Equal(fresh, used);
        Assert.Equal(fresh.GetHashCode(), used.GetHashCode());
        Assert.NotEqual(fresh, fresh with { PistolRelaxedIdle = "other" });
    }

    [Fact]
    public void A_stop_re_arms_after_an_ended_stop_when_the_sprint_is_shown_again()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        stop.Update(F(1.0 + Frame, 9f), clip, Run);
        Assert.Equal(StopEnd.Cancelled, stop.Update(F(1.0 + 2 * Frame, 6f, flag: false), clip, Run));
        Assert.False(stop.CanArm(1.0 + 3 * Frame));   // one stop per sprint
        Assert.Equal(StopEnd.None, stop.Update(F(1.0 + 3 * Frame, 9f), clip, Run));
        Assert.False(stop.Active);
        // A new sprint arms a new stop.
        stop.SprintShown(2.0);
        Assert.True(stop.CanArm(2.0 + Frame));
        Assert.Equal(StopEnd.None, stop.Update(F(2.0 + Frame, 9f), clip, Run));
        Assert.True(stop.Active);
        // Not armed any more 0.5 s after the sprint was last shown.
        var cold = new SprintStopTracker();
        cold.SprintShown(1.0);
        Assert.False(cold.CanArm(1.6));
        Assert.Equal(StopEnd.None, cold.Update(F(1.6, 9f), clip, Run));
        Assert.False(cold.Active);
    }

    [Fact]
    public void A_charge_that_ends_arms_the_stop_in_the_same_frame()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        Assert.False(stop.CanArm(5.0));
        stop.ChargeEnded(5.0);
        Assert.True(stop.CanArm(5.0));   // armed a moment before now, so the stop may start in the frame the charge ended
        Assert.Equal(StopEnd.None, stop.Update(F(5.0, 8f), clip, Run));
        Assert.True(stop.Active);
    }

    [Fact]
    public void Drop_forgets_the_stop_and_none_starts_again_for_the_same_sprint()
    {
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        stop.Update(F(1.02, 9f), clip, Run);
        Assert.True(stop.Active);
        stop.Drop();
        Assert.False(stop.Active);
        Assert.Equal(StopEnd.None, stop.Update(F(1.04, 8f), clip, Run));
        Assert.False(stop.Active);

        var start = new SprintStartTracker();
        var startClip = StartClip();
        start.Track(1.0, 0f, true, 0f, 60);
        start.Track(1.02, 9f, true, 0.2f, 61);
        Assert.True(start.Place(1.02, startClip, out _, 61));
        start.Drop();
        Assert.False(start.Active);
        start.Track(1.04, 9f, true, 0.2f, 62);
        Assert.False(start.Place(1.04, startClip, out _, 62));   // used: not again until the marine stands
    }

    [Fact]
    public void A_missing_clip_starts_nothing_and_ends_a_stop_under_way()
    {
        var stop = new SprintStopTracker();
        stop.SprintShown(1.0);
        Assert.Equal(StopEnd.None, stop.Update(F(1.02, 9f), null, Run));
        Assert.False(stop.Active);
        var playing = new SprintStopTracker();
        playing.SprintShown(1.0);
        playing.Update(F(1.02, 9f), StopClip(), Run);
        Assert.Equal(StopEnd.Interrupted, playing.Update(F(1.04, 8f), null, Run));
        Assert.False(playing.Active);

        var start = new SprintStartTracker();
        start.Track(1.0, 0f, true, 0f);
        start.Track(1.02, 9f, true, 0.2f);
        Assert.False(start.Place(1.02, null, out var seed));
        Assert.Null(seed);
        Assert.False(start.Active);
    }

    [Fact]
    public void With_the_flag_on_a_body_creeping_between_0_3_and_1_m_s_still_plays_the_stop_to_its_end()
    {
        // A remote marine's velocity can hover above rest while the simulation's flag is on: the clip must still finish (not cancelled below 1 m/s, at least half real time).
        var stop = new SprintStopTracker();
        var clip = StopClip();
        stop.SprintShown(1.0);
        double t = 1.0 + Frame;
        Assert.Equal(StopEnd.None, stop.Update(F(t, 9f), clip, Run));
        StopEnd ended = StopEnd.None;
        int i;
        for (i = 1; i < 400 && ended == StopEnd.None; i++) ended = stop.Update(F(t + i * Frame, i < 20 ? 9f - i * 0.4f : 0.6f), clip, Run);
        Assert.Equal(StopEnd.Done, ended);
        Assert.InRange(i * Frame, 0.3, 3.0);   // the clip follows the slowing body at up to 4 times real time, then at least half
    }

    [Fact]
    public void The_arc_slerp_keeps_its_arc_when_the_target_passes_180_degrees_and_holds_antipodal_targets()
    {
        var from = Quaternion.Identity;
        // A target turning about Z from 170 to 190 degrees: the plain slerp flips sides at 180; the arc slerp keeps turning the same way.
        Quaternion? last = null;
        float previousAngle = 0f;
        for (int deg = 170; deg <= 190; deg += 2)
        {
            var to = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180f);
            var (r, target) = PoseBlendMath.ArcSlerp(from, to, 0.5f, last);
            last = target;
            // The half-way rotation about +Z: its signed angle grows steadily (85 .. 95 degrees), no jump to -85.
            float angle = 2f * MathF.Atan2(r.Z, r.W) * 180f / MathF.PI;
            if (deg > 170) Assert.InRange(angle - previousAngle, 0f, 2f);
            previousAngle = angle;
        }
        // Exactly opposite quaternions (no defined arc): the start is kept.
        var opposite = Quaternion.Negate(from);   // kept on that side by its own last frame
        var (held, _) = PoseBlendMath.ArcSlerp(from, opposite, 0.5f, opposite);
        Assert.True(MathF.Abs(Quaternion.Dot(held, from)) > 0.999f);
    }

    [Fact]
    public void The_moving_snapshot_carries_the_measured_velocity_and_dies_away()
    {
        var a = Quaternion.Identity;
        var b = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f);   // 0.1 rad in 1/60 s = 6 rad/s
        var w = PoseBlendMath.AngularVelocity(a, b, 1f / 60f, 100f);
        Assert.InRange(w.X, 5.9f, 6.1f);
        Assert.InRange(PoseBlendMath.AngularVelocity(a, b, 1f / 60f, 4f).Length(), 3.99f, 4.01f);   // capped
        Assert.Equal(0f, PoseBlendMath.Drift(0f, 0.06f));
        Assert.InRange(PoseBlendMath.Drift(10f, 0.06f), 0.0599f, 0.0601f);   // at most decay x velocity
        var carried = PoseBlendMath.Carry(Quaternion.Identity, w, 0.06f);
        Assert.InRange(2f * MathF.Acos(carried.W), 0.35f, 0.37f);   // 6 rad/s for 0.06 s
    }
}
