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

    [Fact]
    public void The_client_wiring_values_are_the_documented_ones()
    {
        Assert.Equal(0.25f, SmClient_FlightEntry());
        Assert.Equal(0.4f, JumpAirBlend.EntryBlend(true));
        Assert.Equal(JumpAirBlend.FlightEntry, JumpAirBlend.EntryBlend(false));
        Assert.True(JumpAirBlend.IsOpposite('F', 'B') && JumpAirBlend.IsOpposite('L', 'R'));
        Assert.False(JumpAirBlend.IsOpposite('F', 'L') || JumpAirBlend.IsOpposite('U', 'F'));
        Assert.True(JumpAirBlend.LandingExitFade > 0.15f);
    }

    static float SmClient_FlightEntry() => JumpAirBlend.FlightEntry;   // ClipBlends.FlightEntry forwards to it (the client project is not referenced here)

    [Fact]
    public void A_direction_turned_to_its_opposite_is_flagged_on_that_call_only()
    {
        var st = new JumpAirBlend.KeyState();
        JumpAirBlend.Choose(st, 8f, 0f, 0f, 0.0);
        JumpAirBlend.Choose(st, -8f, 0f, 0f, 1.0);
        Assert.True(st.ChangedOpposite);
        JumpAirBlend.Choose(st, -8f, 0f, 0f, 1.1);
        Assert.False(st.ChangedOpposite);
    }

    [Fact]
    public void The_lead_applies_to_the_own_player_only_in_the_fall_phase()
    {
        // A low hop's apex near the ground: slowly drifting down in the hang, 0.4 m up, would be "imminent" by the fall's gravity.
        Assert.False(JumpAirBlend.LeadApplies(true, SmSim.JumpPhase.Hang, 0.4f, -1f, 20f));
        Assert.False(JumpAirBlend.LeadApplies(true, SmSim.JumpPhase.Ascent, 0.4f, -3f, 20f));
        Assert.True(JumpAirBlend.LeadApplies(true, SmSim.JumpPhase.Fall, 0.4f, -3f, 20f));
        Assert.False(JumpAirBlend.LeadApplies(true, SmSim.JumpPhase.Fall, 6f, -3f, 20f));
    }

    [Fact]
    public void A_remote_lead_needs_a_real_fall_speed()
    {
        Assert.False(JumpAirBlend.LeadApplies(false, SmSim.JumpPhase.None, 0.4f, -1f, 20f));   // the hang's drift
        Assert.True(JumpAirBlend.LeadApplies(false, SmSim.JumpPhase.None, 0.4f, -JumpAirBlend.RemoteMinFallSpeed - 1f, 20f));
    }

    [Fact]
    public void The_lead_landing_is_the_run_landing_by_the_stick_for_the_own_player_and_the_speed_for_others()
    {
        Assert.True(JumpAirBlend.LeadMoving(true, true, 0f, 0.5f));      // the stick held at touchdown: the simulation's run landing, whatever the drift
        Assert.False(JumpAirBlend.LeadMoving(true, false, 9f, 0.5f));
        Assert.True(JumpAirBlend.LeadMoving(false, false, 9f, 0.5f));
        Assert.False(JumpAirBlend.LeadMoving(false, true, 0.2f, 0.5f));
    }

    [Fact]
    public void Time_to_contact_matches_a_stepped_hang_then_fall()
    {
        // The simulation's tick (30 Hz): a hang under a low gravity, then the fall. The prediction from the first fall tick must land within a tick of the stepped contact.
        const float dt = 1f / 30f, hangG = 3f, fallG = 25f;
        float z = 2.0f, vz = 0f;
        for (int i = 0; i < 15; i++) { vz -= hangG * dt; z += vz * dt; }                 // the hang: the lead must not apply here, the clip would freeze
        Assert.False(JumpAirBlend.LeadApplies(true, SmSim.JumpPhase.Hang, z, vz, fallG));
        float predicted = JumpAirBlend.TimeToContact(z, vz, fallG);
        float t = 0f;
        while (z > 0f && t < 5f) { vz -= fallG * dt; z += vz * dt; t += dt; }
        Assert.InRange(predicted, t - 2 * dt, t + 2 * dt);
    }
}
