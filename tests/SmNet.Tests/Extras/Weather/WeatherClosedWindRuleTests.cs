using SmSim.Extras.Weather;
using Xunit;

namespace SmNet.Tests.Extras.Weather;

public class WeatherClosedWindRuleTests
{
    static readonly string[] Names = ["play_wind_tunnels"];

    [Fact]
    public void Low_tunnel_then_tall_hall_then_low_tunnel_then_open_keeps_one_loop_with_a_stop_in_the_hall_and_the_open_and_a_repost_on_re_entry()
    {
        string? playing = null; int posts = 0, stops = 0, loops = 0;
        void Step(bool covered, bool tunnel, float level = 1f, bool expectStop = false, bool expectPost = false)
        {
            var a = WeatherClosedWindRule.Decide(covered, level, tunnel, 0.8f, playing, Names, n => n == "play_wind_tunnels", out var name);
            if (a == ClosedWindAction.Post) { posts++; playing = name; loops = 1; }
            if (a == ClosedWindAction.Stop) { stops++; playing = null; loops = 0; }
            Assert.Equal(expectStop, a == ClosedWindAction.Stop);
            Assert.Equal(expectPost, a == ClosedWindAction.Post);
            Assert.True(loops <= 1);
        }
        Step(true, true, expectPost: true);          // low tunnel
        Step(true, true);                            // stays: keep, no second post
        Step(true, false, expectStop: true);         // tall covered hall: stop
        Step(true, false);                           // stays quiet
        Step(true, true, expectPost: true);          // low tunnel again: repost
        Step(false, false, level: 0f, expectStop: true);   // open air: stop
        Step(false, false, level: 0f);
        Assert.Equal(2, posts); Assert.Equal(2, stops); Assert.Null(playing);
    }

    [Fact]
    public void A_dying_wind_a_shallow_cover_or_a_missing_event_wants_no_loop()
    {
        Assert.Equal(ClosedWindAction.None, WeatherClosedWindRule.Decide(true, 1f, true, 0.01f, null, Names, _ => true, out _));
        Assert.Equal(ClosedWindAction.None, WeatherClosedWindRule.Decide(true, 0.3f, true, 0.8f, null, Names, _ => true, out _));
        Assert.Equal(ClosedWindAction.None, WeatherClosedWindRule.Decide(true, 1f, true, 0.8f, null, Names, _ => false, out _));
        Assert.Equal(ClosedWindAction.Stop, WeatherClosedWindRule.Decide(true, 1f, true, 0.01f, "play_wind_tunnels", Names, _ => true, out _));
    }

    [Fact]
    public void The_stop_fade_runs_one_second_down_to_the_floor()
    {
        Assert.Equal(0f, WeatherClosedWindRule.FadeDb(0));
        Assert.Equal(WeatherClosedWindRule.FadeFloorDb, WeatherClosedWindRule.FadeDb(1.0));
        Assert.Equal(WeatherClosedWindRule.FadeFloorDb, WeatherClosedWindRule.FadeDb(5.0));
        Assert.True(WeatherClosedWindRule.FadeDb(0.5) is < 0f and > -60f);
    }

    [Fact]
    public void The_fade_timer_completes_in_one_second_of_frame_time_and_ends_at_the_floor()
    {
        var f = new WeatherClosedWindFade();
        Assert.Equal(0f, f.Step(0.1)); Assert.False(f.Active);
        f.Start();
        float last = 0f; int n = 0;
        while (f.Active && n++ < 1000) { float db = f.Step(1.0 / 60); Assert.True(db <= last + 1e-4f); last = db; }
        Assert.True(f.Done); Assert.Equal(WeatherClosedWindRule.FadeFloorDb, last);
        Assert.InRange(n, 59, 62);
        Assert.Equal(0f, f.Step(0.1)); Assert.False(f.Done);
    }

    [Fact]
    public void Cancel_drops_a_fade_and_bad_frame_times_do_not_move_it()
    {
        var f = new WeatherClosedWindFade();
        f.Start(); f.Step(0.5);
        f.Cancel(); Assert.False(f.Active); Assert.False(f.Done);
        f.Start(); Assert.Equal(0f, f.Step(double.NaN)); Assert.Equal(0f, f.Step(-1)); Assert.True(f.Active);
    }

    [Fact]
    public void The_boundary_values_are_wanted()
    {
        Assert.Equal(ClosedWindAction.Post, WeatherClosedWindRule.Decide(true, 0.5f, true, 0.06f, null, Names, _ => true, out _));
        Assert.Equal(ClosedWindAction.None, WeatherClosedWindRule.Decide(true, 0.5f, true, 0.05f, null, Names, _ => true, out _));
        Assert.Equal(ClosedWindAction.None, WeatherClosedWindRule.Decide(true, 0.4999f, true, 0.8f, null, Names, _ => true, out _));
    }
}
