using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Weather W4: the shelter smoother and its hysteresis, the sheltered mix's levels and the particle auto-trim controller (pure classes; the client only feeds them).</summary>
public class WeatherShelterTrimTests
{
    static void Run(WeatherShelter s, double seconds, double dt = 1.0 / 60) { for (double t = 0; t < seconds; t += dt) s.Step(dt); }

    [Fact]
    public void Shelter_starts_in_the_open_and_a_roof_fades_in_over_about_a_second()
    {
        var s = new WeatherShelter();
        Assert.Equal(0f, s.Level);
        s.Sample(1f, 3f);
        Assert.True(s.Covered);
        Run(s, 0.35);
        Assert.InRange(s.Level, 0.3f, 0.7f);   // halfway in 0.7 s
        Run(s, 0.5);
        Assert.Equal(1f, s.Level);
        s.Sample(0f, 40f);
        Run(s, 0.45);
        Assert.InRange(s.Level, 0.3f, 0.7f);   // 0.9 s out
        Run(s, 0.6);
        Assert.Equal(0f, s.Level);
    }

    [Fact]
    public void A_doorway_does_not_flicker_between_the_enter_and_leave_lines()
    {
        var s = new WeatherShelter();
        s.Sample(1f, 3f); Run(s, 1.0);
        // a share between the lines (0.3 and 0.6) keeps the state, whichever way it came
        for (int i = 0; i < 20; i++) { s.Sample(0.45f, 3f); Run(s, 0.17); }
        Assert.True(s.Covered);
        Assert.Equal(1f, s.Level);
        s.Sample(0.1f, 40f); Run(s, 1.0);
        for (int i = 0; i < 20; i++) { s.Sample(0.45f, 3f); Run(s, 0.17); }
        Assert.False(s.Covered);
        Assert.Equal(0f, s.Level);
    }

    [Fact]
    public void A_flip_waits_the_minimum_hold_so_an_alternating_probe_cannot_chatter()
    {
        var s = new WeatherShelter();
        int flips = 0; bool last = false;
        for (int i = 0; i < 120; i++)   // 0.1 s per sample, alternating roof and sky for 12 s
        {
            s.Sample(i % 2 == 0 ? 1f : 0f, 3f); Run(s, 0.1);
            if (s.Covered != last) { flips++; last = s.Covered; }
        }
        Assert.InRange(flips, 1, 40);   // at most one flip per 0.4 s hold (the 0.1 s alternation would flip 120 times)
        Assert.True(flips <= 12.0 / WeatherShelter.MinHoldSeconds + 1);
    }

    [Fact]
    public void A_low_ceiling_is_a_tunnel_with_its_own_hysteresis_and_open_air_is_never_one()
    {
        var s = new WeatherShelter();
        s.Sample(1f, 3f);
        Assert.True(s.Tunnel);
        s.Sample(1f, 6.5f);
        Assert.True(s.Tunnel);   // between 5 and 8 m: stays
        s.Sample(1f, 12f);
        Assert.False(s.Tunnel);
        s.Sample(1f, 6.5f);
        Assert.False(s.Tunnel);   // not back until under 5 m
        s.Sample(1f, 4f);
        Assert.True(s.Tunnel);
        var open = new WeatherShelter(); open.Sample(0f, 3f);
        Assert.False(open.Tunnel);
    }

    [Fact]
    public void Bad_numbers_are_the_open_air_and_a_stalled_clock_changes_nothing()
    {
        var s = new WeatherShelter();
        s.Sample(float.NaN, float.NaN); s.Step(double.NaN); s.Step(-1); s.Step(double.PositiveInfinity);
        Assert.False(s.Covered);
        Assert.Equal(0f, s.Level);
        s.Sample(1f, 3f); Run(s, 1.0); s.Reset();
        Assert.Equal(0f, s.Level); Assert.False(s.Covered);
    }

    [Fact]
    public void The_sheltered_mix_only_ever_lowers_loudness_and_muffles_between_the_open_and_the_muffled_cutoff()
    {
        Assert.Equal(WeatherShelterMix.OpenHz, WeatherShelterMix.CutoffHz(0f), 1);
        Assert.Equal(WeatherShelterMix.MuffledHz, WeatherShelterMix.CutoffHz(1f), 1);
        float prev = float.MaxValue;
        for (float l = 0; l <= 1.0001f; l += 0.1f)
        {
            float hz = WeatherShelterMix.CutoffHz(l);
            Assert.True(hz <= prev + 1e-3f); prev = hz;
            Assert.True(WeatherShelterMix.RainDb(l) <= 0f && WeatherShelterMix.WindDb(l) <= 0f && WeatherShelterMix.ThunderDb(l) <= 0f);
        }
        Assert.Equal(0f, WeatherShelterMix.RainDb(0f)); Assert.Equal(0f, WeatherShelterMix.ThunderDb(0f));
        // rain is cut hardest, thunder least: it stays audible (the caps are untouched, a cut never raises)
        Assert.True(WeatherShelterMix.RainDb(1f) < WeatherShelterMix.WindDb(1f) && WeatherShelterMix.WindDb(1f) <= WeatherShelterMix.ThunderDb(1f));
        Assert.True(WeatherShelterMix.ThunderDb(1f) > -6f);
        // out of range and bad levels are clamped
        Assert.Equal(WeatherShelterMix.CutoffHz(1f), WeatherShelterMix.CutoffHz(5f), 1);
        Assert.Equal(WeatherShelterMix.OpenHz, WeatherShelterMix.CutoffHz(float.NaN), 1);
    }

    [Fact]
    public void The_near_layers_are_gone_under_full_cover_and_the_far_layer_keeps_a_share()
    {
        Assert.Equal(1f, WeatherShelterMix.NearAlpha(0f)); Assert.Equal(0f, WeatherShelterMix.NearAlpha(1f));
        Assert.Equal(1f, WeatherShelterMix.FarAlpha(0f)); Assert.Equal(WeatherShelterMix.FarShareAtFull, WeatherShelterMix.FarAlpha(1f), 3);
        Assert.True(WeatherShelterMix.FarAlpha(0.5f) > WeatherShelterMix.NearAlpha(0.5f));
    }

    // ---- the trim ----

    /// <summary>A frame time model: a fixed cost plus a particle cost that follows the trim level's share of the counts.</summary>
    static double FrameMs(double fixedMs, double particleMsAtFull, int level) => fixedMs + particleMsAtFull * WeatherTrim.Steps[level];

    [Fact]
    public void Counts_follow_the_level_and_never_go_below_the_floor_nor_above_the_full_count()
    {
        Assert.Equal(1300, WeatherTrim.Count(1300, 120, 0));
        Assert.Equal((int)(1300 * 0.8f), WeatherTrim.Count(1300, 120, 1));
        for (int l = 0; l <= WeatherTrim.MaxLevel; l++)
        {
            Assert.True(WeatherTrim.Count(1300, 120, l) >= 120);
            Assert.Equal(120, WeatherTrim.Count(130, 120, WeatherTrim.MaxLevel));   // the preset's own count was already near the floor
            Assert.True(WeatherTrim.Count(100, 120, l) <= 100);   // a preset below its floor (never trimmed up)
        }
        Assert.Equal(WeatherTrim.Count(1300, 120, WeatherTrim.MaxLevel), WeatherTrim.Count(1300, 120, 99));
        Assert.Equal(1300, WeatherTrim.Count(1300, 120, -3));
    }

    [Fact]
    public void Steps_are_strictly_decreasing_and_the_lowest_keeps_a_real_share()
    {
        for (int i = 1; i < WeatherTrim.Steps.Length; i++) Assert.True(WeatherTrim.Steps[i] < WeatherTrim.Steps[i - 1]);
        Assert.Equal(1f, WeatherTrim.Steps[0]);
        Assert.True(WeatherTrim.Steps[^1] >= 0.4f);
    }

    [Fact]
    public void Healthy_frames_never_trim()
    {
        var t = new WeatherTrim();
        for (int i = 0; i < 60 * 120; i++) t.Frame(1.0 / 60);
        Assert.Equal(0, t.Level); Assert.Equal(0, t.Changes);
    }

    [Fact]
    public void The_warm_up_and_a_single_hitch_do_not_trim()
    {
        var t = new WeatherTrim();
        for (int i = 0; i < 100; i++) t.Frame(0.2);   // 20 s of 200 ms frames, but the first two seconds are ignored (10 frames) and the rest is the next test
        Assert.True(t.Level >= 1);
        var h = new WeatherTrim();
        for (int i = 0; i < 300; i++) h.Frame(1.0 / 60);   // warm up past
        h.Frame(2.0);   // one 2 s stall, capped at 100 ms
        for (int i = 0; i < 200; i++) h.Frame(1.0 / 60);
        Assert.Equal(0, h.Level);
    }

    [Fact]
    public void Long_frames_step_the_counts_down_one_level_at_a_time_to_the_lowest_and_stay_there()
    {
        var t = new WeatherTrim();
        var levels = new List<int>();
        for (double s = 0; s < 40; s += 1.0 / 30) { if (t.Frame(1.0 / 30 * 1.5)) levels.Add(t.Level); }   // 50 ms frames throughout
        Assert.Equal(new[] { 1, 2, 3 }, levels);
        Assert.Equal(WeatherTrim.MaxLevel, t.Level);
    }

    [Fact]
    public void Headroom_steps_back_up_after_the_hold_and_not_before()
    {
        var t = new WeatherTrim();
        double clock = 0;
        while (t.Level < 2) { t.Frame(0.04); clock += 0.04; }
        double mark = 0; int level = t.Level;
        // healthy frames: not before the hold, then one level at a time
        while (mark < WeatherTrim.UpHoldSeconds - 0.5) { Assert.False(t.Frame(1.0 / 60)); mark += 1.0 / 60; }
        Assert.Equal(level, t.Level);
        while (mark < WeatherTrim.UpHoldSeconds + 1.0) { t.Frame(1.0 / 60); mark += 1.0 / 60; }
        Assert.Equal(level - 1, t.Level);
        while (mark < 3 * WeatherTrim.UpHoldSeconds) { t.Frame(1.0 / 60); mark += 1.0 / 60; }
        Assert.Equal(0, t.Level);
    }

    [Fact]
    public void Frames_between_the_up_and_down_lines_hold_the_level()
    {
        var t = new WeatherTrim();
        while (t.Level < 1) t.Frame(0.04);
        int changes = t.Changes;
        for (int i = 0; i < 60 * 120; i++) t.Frame(0.020);   // 20 ms: above the up line (17.5), below the down line (22)
        Assert.Equal(1, t.Level);
        Assert.Equal(changes, t.Changes);
    }

    [Fact]
    public void A_machine_that_sits_on_the_line_does_not_oscillate_and_stops_retrying_a_step_that_does_not_hold()
    {
        // 28 ms of particles at full (no fixed cost): level 0 = 28 ms and level 1 = 22.4 ms (both over the 22 ms down line), level 2 = 17.4 ms (under the 17.5 ms up line), level 3 = 12.9 ms.
        // It settles at level 2; each step up from 2 to 1 is undone at once, and every undo doubles the wait.
        var t = new WeatherTrim();
        double now = 0; int ups = 0, downs = 0, prev = 0;
        while (now < 1800)   // half an hour of play
        {
            double dt = FrameMs(0.0, 28.0, t.Level) / 1000.0;
            t.Frame(dt); now += dt;
            if (t.Level > prev) downs++; else if (t.Level < prev) ups++;
            prev = t.Level;
        }
        Assert.True(t.Changes <= 40, $"{t.Changes} changes in 30 minutes");
        Assert.True(ups <= 20, $"{ups} step ups");
        Assert.True(t.Level >= 1);
        Assert.True(t.UpHold > WeatherTrim.UpHoldSeconds);   // the failed step up made it wait longer
    }

    [Theory]
    [InlineData(33.3, 33.5)]   // a 30 Hz vsync
    [InlineData(25.0, 25.0)]   // a 40 fps cap
    [InlineData(25.0, 27.0)]   // within 10 %
    public void A_frame_cap_or_vsync_explains_long_frames_and_does_not_trim(double expectedMs, double frameMs)
    {
        var t = new WeatherTrim { ExpectedFrameMs = expectedMs };
        for (int i = 0; i < (int)(120000 / frameMs); i++) t.Frame(frameMs / 1000.0);
        Assert.Equal(0, t.Level); Assert.Equal(0, t.Changes);
    }

    [Fact]
    public void Frames_well_over_the_cap_still_trim_and_a_60_Hz_vsync_does_not_hide_a_slow_machine()
    {
        var capped = new WeatherTrim { ExpectedFrameMs = 25.0 };
        for (int i = 0; i < 20 * 20; i++) capped.Frame(0.040);   // 40 ms against a 25 ms cap: the weather's cost
        Assert.True(capped.Level >= 1);
        var vsync = new WeatherTrim { ExpectedFrameMs = 16.7 };
        for (int i = 0; i < 20 * 25; i++) vsync.Frame(0.040);   // 40 ms on a 60 Hz display
        Assert.True(vsync.Level >= 1);
        var h = new WeatherTrim { ExpectedFrameMs = 16.7 };
        for (int i = 0; i < 20 * 30; i++) h.Frame(0.0167);
        Assert.Equal(0, h.Level);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(40.0)]
    [InlineData(60.0)]
    public void A_capped_client_that_stuttered_recovers_to_level_0_once_it_runs_exactly_on_the_cap(double hz)
    {
        var t = new WeatherTrim { ExpectedFrameMs = 1000.0 / hz };
        for (int i = 0; i < 12 * 20; i++) t.Frame(0.05);   // 12 s at 20 FPS
        Assert.Equal(WeatherTrim.MaxLevel, t.Level);
        for (int i = 0; i < (int)(180 * hz); i++) t.Frame(1.0 / hz);   // 180 s on target
        Assert.Equal(0, t.Level);
    }

    [Fact]
    public void A_step_up_on_a_cap_that_brings_the_stutter_back_steps_down_and_doubles_the_wait()
    {
        var t = new WeatherTrim { ExpectedFrameMs = 1000.0 / 30 };
        for (int i = 0; i < 12 * 20; i++) t.Frame(0.05);
        Assert.Equal(WeatherTrim.MaxLevel, t.Level);
        double hold0 = t.UpHold;
        int n = 0;
        while (t.Level == WeatherTrim.MaxLevel && n++ < 30 * 60) t.Frame(1.0 / 30);   // on target until the first step up
        Assert.Equal(WeatherTrim.MaxLevel - 1, t.Level);
        for (int i = 0; i < 20 * 5 && t.Level < WeatherTrim.MaxLevel; i++) t.Frame(0.05);   // the stutter returns
        Assert.Equal(WeatherTrim.MaxLevel, t.Level);
        Assert.Equal(hold0 * 2, t.UpHold);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(40.0)]
    [InlineData(60.0)]
    public void A_steady_cap_with_no_stutter_never_trims(double hz)
    {
        var t = new WeatherTrim { ExpectedFrameMs = 1000.0 / hz };
        for (int i = 0; i < (int)(600 * hz); i++) t.Frame(1.0 / hz);
        Assert.Equal(0, t.Level); Assert.Equal(0, t.Changes);
    }

    [Fact]
    public void The_install_sounds_are_cut_under_cover_and_never_raised()
    {
        Assert.Equal(0f, WeatherShelterMix.InstallThunderDb(0f)); Assert.Equal(0f, WeatherShelterMix.InstallGritDb(0f));
        Assert.Equal(-7f, WeatherShelterMix.InstallThunderDb(1f)); Assert.Equal(-9f, WeatherShelterMix.InstallGritDb(1f));
        Assert.True(WeatherShelterMix.InstallThunderDb(0.5f) is < 0f and > -7f);
        Assert.Equal(0f, WeatherShelterMix.InstallGritDb(float.NaN));
        Assert.Equal(0.5f, WeatherShelterMix.GritStopAbove);
    }

    [Fact]
    public void A_load_that_ends_lets_the_counts_recover_fully()
    {
        var t = new WeatherTrim();
        double now = 0;
        while (now < 20) { t.Frame(0.05); now += 0.05; }   // a heavy scene
        Assert.Equal(WeatherTrim.MaxLevel, t.Level);
        while (now < 20 + 5 * 60) { t.Frame(1.0 / 60); now += 1.0 / 60; }   // then calm
        Assert.Equal(0, t.Level);
    }

    [Fact]
    public void Bad_frame_times_change_nothing_and_reset_starts_over()
    {
        var t = new WeatherTrim();
        Assert.False(t.Frame(double.NaN)); Assert.False(t.Frame(-1)); Assert.False(t.Frame(0)); Assert.False(t.Frame(double.PositiveInfinity));
        while (t.Level < 2) t.Frame(0.05);
        t.Reset();
        Assert.Equal(0, t.Level); Assert.Equal(0, t.Changes); Assert.Equal(WeatherTrim.UpHoldSeconds, t.UpHold);
    }

    [Fact]
    public void Warfront_wind_bed_is_untouched_in_the_open_and_muffled_under_cover()
    {
        Assert.Equal(0f, WeatherShelterMix.BedWindDb(0f)); Assert.Equal(0f, WeatherShelterMix.BedWindLpf(0f));
        Assert.Equal(WeatherShelterMix.BedWindCutDb, WeatherShelterMix.BedWindDb(1f));
        Assert.True(WeatherShelterMix.BedWindDb(0.5f) < 0f && WeatherShelterMix.BedWindDb(0.5f) > WeatherShelterMix.BedWindCutDb);
        // the units land on the same 1.1 kHz as the weather bus (the sound system's curve: 20 kHz x (150/20000)^(u/100))
        double hz = 20000.0 * System.Math.Pow(150.0 / 20000.0, WeatherShelterMix.BedWindLpf(1f) / 100.0);
        Assert.InRange(hz, WeatherShelterMix.MuffledHz * 0.98, WeatherShelterMix.MuffledHz * 1.02);
        Assert.Equal(0f, WeatherShelterMix.BedWindDb(float.NaN)); Assert.Equal(WeatherShelterMix.BedWindLpfAtFull, WeatherShelterMix.BedWindLpf(7f));
    }
}
