using System.Numerics;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>feel/melee-lock, the client's presentation rules (<see cref="MeleePresentation"/>): hit-stop, start distance, root-driven motion, camera assist, the victim guess.</summary>
public class MeleePresentationTests
{
    static float T(float ticks) => ticks / 30f;

    static MeleeStep Step()
    {
        float duration = T(30.5f), hit = T(6.5f);
        MeleeVariant V(float d) => new(d, Enumerable.Range(0, 61).Select(i =>
        {
            float t = duration * i / 60f;
            return MathF.Max(0f, d - 1.5f) * Math.Clamp(t / hit, 0f, 1f) + 0.6f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
        }).ToArray(), $"clip_{d}m");
        return new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 1f, [V(1f), V(3.5f), V(7f)]),
        };
    }

    [Fact]
    public void Hit_stop_holds_then_catches_up()
    {
        Assert.Equal(0f, MeleePresentation.Hold(0f));
        Assert.Equal(0.05f, MeleePresentation.Hold(0.05f), 5);
        Assert.Equal(MeleePresentation.StopSeconds, MeleePresentation.Hold(MeleePresentation.StopSeconds), 5);
        Assert.Equal(MeleePresentation.StopSeconds / 2f, MeleePresentation.Hold(MeleePresentation.StopSeconds + MeleePresentation.CatchUpSeconds / 2f), 4);
        Assert.Equal(0f, MeleePresentation.Hold(MeleePresentation.StopSeconds + MeleePresentation.CatchUpSeconds + 0.01f));
        Assert.Equal(0f, MeleePresentation.Hold(-1f));
        Assert.Equal(0f, MeleePresentation.Hold(float.NaN));
        Assert.False(MeleePresentation.Active(1f));
    }

    [Fact]
    public void Start_distance_adds_back_the_ground_already_closed()
    {
        var m = Step();
        var reach = m.Reach!;
        // A target 5 m away at the start: halfway to the hit the attacker has closed 1.75 m of the 3.5 m.
        float now = 5f - reach.ForwardAt(m.HitTime / 2f, m.Duration, 5f);
        Assert.Equal(5f, MeleePresentation.StartDistance(reach, m.Duration, m.HitTime / 2f, now), 1);
        Assert.Equal(1f, MeleePresentation.StartDistance(reach, m.Duration, 0.1f, null));   // no target: the start blend
        Assert.Equal(7f, MeleePresentation.StartDistance(reach, m.Duration, 0f, 20f));      // clamped to the variants
        var (low, high, w) = reach.Blend(5f);
        Assert.Equal(("clip_3.5m", "clip_7m"), (low!.Clip, high!.Clip));
        Assert.Equal(1.5f / 3.5f, w, 4);
    }

    [Fact]
    public void Root_motion_keeps_the_legs_and_the_stick_hands_them_over()
    {
        var m = Step();
        // After the hit the follow-through moves 0.6 m over 0.8 s (0.75 m/s); the 7 m clip's lunge before it is 25 m/s.
        float t = T(10f);
        Assert.True(MeleePresentation.RootDriven(m, t, new Vector2(0f, 0.75f), 0f, 1f / 30f, 1f));     // along the heading at the clip's speed
        Assert.True(MeleePresentation.RootDriven(m, t, Vector2.Zero, 0f, 1f / 30f, 1f));
        Assert.False(MeleePresentation.RootDriven(m, t, new Vector2(3f, 0f), 0f, 1f / 30f, 1f));      // the stick sideways
        Assert.False(MeleePresentation.RootDriven(m, t, new Vector2(0f, 6.5f), 0f, 1f / 30f, 1f));    // running on
        Assert.False(MeleePresentation.RootDriven(m with { Reach = null }, t, new Vector2(0f, 0.5f), 0f, 1f / 30f, 1f));
        // Before the hit the blend shown decides: the 7 m clip lunges 25 m/s, the 1 m clip stands.
        float early = T(3f);
        Assert.True(MeleePresentation.RootDriven(m, early, new Vector2(0f, 20f), 0f, 1f / 30f, 7f));
        Assert.False(MeleePresentation.RootDriven(m, early, new Vector2(0f, 20f), 0f, 1f / 30f, 1f));
    }

    [Fact]
    public void A_view_between_snapshots_interpolates_skips_teleports_and_the_own_player()
    {
        PlayerState P(int id, float y, float yaw = 0f) => new(id, new Vector3(0, y, 0), new Vector3(0, 3, 0), yaw, true) { Alive = true };
        var first = new Dictionary<int, PlayerState> { [1] = P(1, 0f), [2] = P(2, 0f, 0f), [3] = P(3, 0f), [4] = P(4, 0f) };
        var second = new Dictionary<int, PlayerState> { [1] = P(1, 9f), [2] = P(2, 1f, 1f), [3] = P(3, 50f) };   // 3 respawned far away; 4 left
        var mid = GameClient.Between(10, first, 12, second, 11, self: 1);
        Assert.False(mid.ContainsKey(1));                    // the own player is never in the view
        Assert.Equal(0.5f, mid[2].Position.Y, 5);
        Assert.Equal(0.5f, mid[2].Yaw, 5);
        Assert.Equal(0f, mid[3].Position.Y);                 // a teleport is not streaked: the first snapshot's place
        Assert.Equal(0f, mid[4].Position.Y);                 // gone from the second: the first's
        Assert.Equal(second[2], GameClient.Between(10, first, 12, second, 12, self: 1)[2]);   // at a snapshot's tick: that snapshot exactly
        Assert.Equal(first[2], GameClient.Between(10, first, 12, second, 10, self: 1)[2]);
        Assert.Equal(first[2], GameClient.Between(10, first, 10, first, 15, self: 1)[2]);     // one snapshot: it, whatever the tick
    }

    [Fact]
    public void Camera_assist_turns_toward_the_target_capped_and_only_near_the_view()
    {
        float step = MeleePresentation.AssistRate * MathF.PI / 180f * 0.1f;
        Assert.Equal(step, MeleePresentation.Assist(0f, 0.8f, 0.1f), 4);
        Assert.Equal(0.1f, MeleePresentation.Assist(0f, 0.1f, 0.5f), 4);   // never past the target
        Assert.Equal(0f, MeleePresentation.Assist(0f, 1.5f, 0.1f));         // 86° off: no assist
        Assert.True(MeleePresentation.AssistWindow(Step(), T(5f)));
        Assert.False(MeleePresentation.AssistWindow(Step(), T(8f)));
    }

    [Fact]
    public void Victim_prefers_the_lock_in_reach_else_the_nearest_in_the_cone()
    {
        var c = new SimConfig();
        var me = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        PlayerState P(int id, float x, float y) => Combat.Respawned(new PlayerState(id, new Vector3(x, y, 0), Vector3.Zero, 0f, true), c);
        Assert.Equal(3, MeleePresentation.Victim(me, [P(2, 0, 1.5f), P(3, 0.5f, 2.5f)], c, preferred: 3));
        Assert.Equal(2, MeleePresentation.Victim(me, [P(2, 0, 1.5f), P(3, 0.5f, 2.5f)], c));
        Assert.Equal(0, MeleePresentation.Victim(me, [P(2, 0, -1.5f), P(3, 0, 6f)], c));   // behind, out of reach
    }
}
