using System;
using System.Numerics;
using SmContent.Extras.Musou;

namespace SmFormats.Tests;

/// <summary>
/// The spear's world-space foot pin (<see cref="FootPin"/>; owner, 2026-11-11: the steps "with no floating", no sliding in a match): synthetic, no install. A planted foot keeps its spot in the
/// world while the body moves under it; a foot the clip lifts follows the keys; a foot left too far behind steps after the body; the strike's end steps the feet back onto their keyed spots;
/// with the body still (the preview stage) the keys are untouched.
/// </summary>
public class FootPinTests
{
    const float Dt = 1f / 60f;
    static Matrix4x4 At(float forward) => Matrix4x4.CreateTranslation(0f, forward, 0f);

    [Fact]
    public void A_planted_foot_stays_on_its_spot_in_the_world_while_the_body_lunges()
    {
        var pin = new FootPin();
        var keyed = new Vector3(-0.1f, -0.45f, 0.2f);   // the rear foot, skeleton space
        var world0 = Vector3.Transform(keyed, At(0f));
        float body = 0f;
        for (int f = 0; f < 18; f++)   // 0.3 s, 0.3 m of lunge: within the reach
        {
            var placed = pin.Place(1, keyed, planted: true, active: true, At(body), Dt);
            var world = Vector3.Transform(placed, At(body));
            Assert.True(Vector3.Distance(world, world0) < 1e-4f, $"frame {f}: the planted foot moved {Vector3.Distance(world, world0):0.0000} m in the world");
            body += 0.3f / 18f;
        }
        Assert.True(pin.IsPinned(1));
    }

    [Fact]
    public void A_body_jump_past_the_snap_distance_puts_the_foot_straight_on_its_keys()
    {
        var pin = new FootPin();
        var keyed = new Vector3(0f, -0.45f, 0.2f);
        for (int f = 0; f < 5; f++) pin.Place(1, keyed, true, true, At(0f), Dt);
        Assert.True(pin.IsPinned(1));
        Near(keyed, pin.Place(1, keyed, true, true, At(1.2f), Dt));   // a 1.2 m correction in one frame: no 0.18 s dash across it
        Assert.False(pin.IsStepping(1));
    }

    [Fact]
    public void With_the_body_still_the_keys_are_left_alone()
    {
        var pin = new FootPin();
        var keyed = new Vector3(0.2f, 0.4f, 0.2f);
        for (int f = 0; f < 30; f++) Near(keyed, pin.Place(0, keyed, true, true, At(0f), Dt));
        var lifted = keyed + new Vector3(0.1f, 0.2f, 0.16f);   // the clip lifts the foot for a step: it follows the keys after a short blend
        Vector3 last = default;
        for (int f = 0; f < 20; f++) last = pin.Place(0, lifted, false, true, At(0f), Dt);
        Near(lifted, last);
        Assert.False(pin.IsPinned(0));
    }

    [Fact]
    public void A_foot_left_too_far_behind_takes_a_lifted_catch_up_step_onto_its_keyed_spot()
    {
        var pin = new FootPin();
        var keyed = new Vector3(0f, -0.45f, 0.2f);
        float body = 0f, topLift = 0f; bool stepped = false;
        for (int f = 0; f < 90; f++)
        {
            var placed = pin.Place(1, keyed, true, true, At(body), Dt);
            var behind = new Vector2(placed.X - keyed.X, placed.Y - keyed.Y).Length();
            if (pin.IsStepping(1)) { stepped = true; topLift = MathF.Max(topLift, placed.Z - keyed.Z); }
            else Assert.True(behind <= FootPin.MaxBehind + 1e-3f, $"frame {f}: planted {behind:0.000} m behind its keyed spot");
            body += 1.5f / 30f;   // a long lunge: 1.5 m in 0.5 s, then still
            if (f >= 30) body = 1.5f;
        }
        Assert.True(stepped, "the foot never stepped after the body");
        Assert.InRange(topLift, FootPin.StepLift * 0.8f, FootPin.StepLift + 1e-3f);
        Assert.True(pin.IsPinned(1));
        Near(keyed, pin.Place(1, keyed, true, true, At(body), Dt));   // replanted on its keyed spot once the body stopped
    }

    [Fact]
    public void The_strikes_end_steps_a_pinned_foot_back_onto_its_keyed_spot_and_lets_it_go()
    {
        var pin = new FootPin();
        var keyed = new Vector3(0f, 0.4f, 0.2f);
        pin.Place(0, keyed, true, true, At(0f), Dt);
        var behind = pin.Place(0, keyed, true, true, At(0.2f), Dt);
        Assert.True(MathF.Abs(behind.Y - (keyed.Y - 0.2f)) < 1e-4f);
        Vector3 placed = default;
        int frames = 0;
        do { placed = pin.Place(0, keyed, true, active: false, At(0.2f), Dt); frames++; } while (pin.IsStepping(0) && frames < 60);
        Near(keyed, placed);
        Assert.False(pin.IsPinned(0));
        Assert.InRange(frames * Dt, FootPin.StepTime - 2 * Dt, FootPin.StepTime + 2 * Dt);
    }

    [Fact]
    public void A_teleport_puts_the_foot_straight_on_its_keys()
    {
        var pin = new FootPin();
        var keyed = new Vector3(0f, 0.4f, 0.2f);
        pin.Place(0, keyed, true, true, At(0f), Dt);
        Near(keyed, pin.Place(0, keyed, true, true, At(FootPin.SnapBeyond + 1f), Dt));
    }

    static void Near(Vector3 want, Vector3 got) => Assert.True(Vector3.Distance(want, got) < 1e-4f, $"{got} is not {want}");
}
