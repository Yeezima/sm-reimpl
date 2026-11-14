using SmContent;

namespace SmNet.Tests;

/// <summary>anim/jump-pack-blend: the steady flight keys, the landing lead and the dropped-snapshot hold (<see cref="JumpAirBlend"/>). Engine-free.</summary>
public class JumpAirBlendTests
{
    [Fact]
    public void A_flight_starts_with_the_plain_rule()
    {
        var st = new JumpAirBlend.KeyState();
        Assert.Equal(('F', 'U'), JumpAirBlend.Choose(st, 8f, 0f, 0.5f, 0.0));
    }

    [Fact]
    public void A_direction_flip_waits_out_the_minimum_hold()
    {
        var st = new JumpAirBlend.KeyState();
        JumpAirBlend.Choose(st, 8f, 0f, 0f, 0.0);
        Assert.Equal('F', JumpAirBlend.Choose(st, 1f, 8f, 0f, 0.05f).Dir);                 // clearly right, but too soon
        Assert.Equal('R', JumpAirBlend.Choose(st, 1f, 8f, 0f, JumpAirBlend.MinKeyHold + 0.01f).Dir);
    }

    [Fact]
    public void A_stick_wobbling_round_the_diagonal_does_not_flip_the_clip()
    {
        var st = new JumpAirBlend.KeyState();
        JumpAirBlend.Choose(st, 8f, 7f, 0f, 0.0);   // forward just wins
        int changes = 0; char last = st.Dir;
        for (int i = 1; i < 200; i++)
        {
            float wobble = (i % 2 == 0 ? 0.3f : -0.3f);
            var (d, _) = JumpAirBlend.Choose(st, 7.5f - wobble, 7.5f + wobble, 0f, i * 0.0167);   // forward and right alternate in the lead
            if (d != last) { changes++; last = d; }
        }
        Assert.Equal(0, changes);
    }

    [Fact]
    public void The_neutral_band_keeps_a_slow_drift_from_flickering()
    {
        var st = new JumpAirBlend.KeyState();
        JumpAirBlend.Choose(st, 5f, 0f, 0f, 0.0);                                   // F
        Assert.Equal('F', JumpAirBlend.Choose(st, 2.9f, 0f, 0f, 1.0).Dir);          // under the plain neutral speed (3) but inside the band
        Assert.Equal('U', JumpAirBlend.Choose(st, 1.5f, 0f, 0f, 2.0).Dir);
        Assert.Equal('U', JumpAirBlend.Choose(st, 3.2f, 0f, 0f, 3.0).Dir);          // over 3 but inside the band on the way out
        Assert.Equal('F', JumpAirBlend.Choose(st, 4.5f, 0f, 0f, 4.0).Dir);
    }

    [Fact]
    public void The_aim_has_hysteresis_round_its_threshold()
    {
        var st = new JumpAirBlend.KeyState();
        JumpAirBlend.Choose(st, 6f, 0f, 0f, 0.0);
        Assert.Equal('U', JumpAirBlend.Choose(st, 6f, 0f, 0.40f, 1.0).Aim);
        Assert.Equal('U', JumpAirBlend.Choose(st, 6f, 0f, 0.30f, 2.0).Aim);         // under 0.35, still up inside the band
        Assert.Equal('F', JumpAirBlend.Choose(st, 6f, 0f, 0.20f, 3.0).Aim);
        Assert.Equal('D', JumpAirBlend.Choose(st, 6f, 0f, -0.50f, 4.0).Aim);
    }

    [Fact]
    public void Reversing_at_speed_flips_to_back_without_waiting_for_a_margin()
    {
        var st = new JumpAirBlend.KeyState();
        JumpAirBlend.Choose(st, 8f, 0f, 0f, 0.0);
        Assert.Equal('B', JumpAirBlend.Choose(st, -8f, 0f, 0f, 1.0).Dir);
    }

    [Theory]
    [InlineData(0f, -10f, 20f, 0f)]
    [InlineData(4f, -5f, 20f, 0f)]
    public void Time_to_contact_solves_the_fall(float h, float vz, float g, float _)
    {
        float t = JumpAirBlend.TimeToContact(h, vz, g);
        Assert.True(t >= 0f);
        Assert.Equal(h, -vz * t + 0.5f * g * t * t, 2);
    }

    [Fact]
    public void Time_to_contact_is_infinite_when_rising_or_unknown()
    {
        Assert.True(float.IsPositiveInfinity(JumpAirBlend.TimeToContact(3f, 2f, 20f)));
        Assert.True(float.IsPositiveInfinity(JumpAirBlend.TimeToContact(float.PositiveInfinity, -5f, 20f)));
        Assert.False(JumpAirBlend.LandingImminent(float.PositiveInfinity, -5f, 20f));
    }

    [Fact]
    public void The_landing_is_imminent_inside_the_lead()
    {
        Assert.True(JumpAirBlend.LandingImminent(0.5f, -10f, 20f));    // 0.05 s
        Assert.False(JumpAirBlend.LandingImminent(5f, -10f, 20f));
        Assert.False(JumpAirBlend.LandingImminent(0.5f, 0f, 20f));     // hanging
    }

    [Fact]
    public void A_dropped_snapshot_is_held_only_briefly()
    {
        Assert.True(JumpAirBlend.HoldsAir(1.03, 1.0));
        Assert.False(JumpAirBlend.HoldsAir(1.2, 1.0));
    }
}
