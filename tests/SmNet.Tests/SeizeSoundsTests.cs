using Xunit;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>Fidelity gap 9: the Seize Ground sounds (capture tick and stop, objective noise, score tick) as a pure state machine.</summary>
public class SeizeSoundsTests
{
    static ControlPointView Neutral() => new(Team.None, Team.None, 0f, false);
    static ControlPointView Capturing(Team side, float progress) => new(Team.None, side, progress, false);
    static ControlPointView Owned(Team owner) => new(owner, owner, 1f, false);

    static ControlPointView[] Points(params ControlPointView[] p) => p;

    [Fact]
    public void The_event_names_are_the_banks()
    {
        Assert.Equal("play_mp_point_being_captured", SeizeSounds.EventOf(SeizeSoundKind.CaptureTick));
        Assert.Equal("stop_mp_point_being_captured", SeizeSounds.EventOf(SeizeSoundKind.CaptureStop));
        Assert.Equal("Play_Objective_Complete", SeizeSounds.EventOf(SeizeSoundKind.ObjectiveComplete));
        Assert.Equal("Play_MP_Points_Total", SeizeSounds.EventOf(SeizeSoundKind.ScoreTick));
    }

    [Fact]
    public void The_first_step_and_a_change_of_layout_only_note_the_state()
    {
        var s = new SeizeSounds();
        Assert.Empty(s.Step(Points(Owned(Team.SpaceMarines), Neutral()), 0, Team.SpaceMarines, 10, 0.0));   // an owned point at join is not a capture
        Assert.Empty(s.Step(Points(Neutral()), -1, Team.SpaceMarines, 10, 0.1));                            // the count changed: noted again
    }

    [Fact]
    public void Standing_on_a_moving_point_ticks_once_a_second_and_stops_when_it_stops()
    {
        var s = new SeizeSounds();
        s.Step(Points(Neutral()), 0, Team.SpaceMarines, 0, 0.0);
        var first = s.Step(Points(Capturing(Team.SpaceMarines, 0.05f)), 0, Team.SpaceMarines, 0, 0.1);
        Assert.Equal(new[] { new SeizeSoundEvent(SeizeSoundKind.CaptureTick, 0) }, first);                  // starts at once
        Assert.Empty(s.Step(Points(Capturing(Team.SpaceMarines, 0.10f)), 0, Team.SpaceMarines, 0, 0.5));   // not again within the interval
        var second = s.Step(Points(Capturing(Team.SpaceMarines, 0.20f)), 0, Team.SpaceMarines, 0, 0.1 + SeizeSounds.TickInterval);
        Assert.Equal(new[] { new SeizeSoundEvent(SeizeSoundKind.CaptureTick, 0) }, second);
        // The progress stops changing (contested into a standstill): after the hold the stop is posted once.
        double t = 0.1 + SeizeSounds.TickInterval;
        Assert.Empty(s.Step(Points(Capturing(Team.SpaceMarines, 0.20f)), 0, Team.SpaceMarines, 0, t + 0.2));
        var stop = s.Step(Points(Capturing(Team.SpaceMarines, 0.20f)), 0, Team.SpaceMarines, 0, t + SeizeSounds.MovingHold + 0.1);
        Assert.Equal(new[] { new SeizeSoundEvent(SeizeSoundKind.CaptureStop, 0) }, stop);
        Assert.Empty(s.Step(Points(Capturing(Team.SpaceMarines, 0.20f)), 0, Team.SpaceMarines, 0, t + SeizeSounds.MovingHold + 0.2));   // only once
    }

    [Fact]
    public void A_point_the_player_is_not_on_makes_no_tick()
    {
        var s = new SeizeSounds();
        s.Step(Points(Neutral(), Neutral()), -1, Team.SpaceMarines, 0, 0.0);
        Assert.Empty(s.Step(Points(Capturing(Team.Chaos, 0.1f), Neutral()), -1, Team.SpaceMarines, 0, 0.1));
        Assert.Empty(s.Step(Points(Capturing(Team.Chaos, 0.2f), Neutral()), 1, Team.SpaceMarines, 0, 0.2));   // on the other, idle one
    }

    [Fact]
    public void Leaving_the_point_stops_the_tick()
    {
        var s = new SeizeSounds();
        s.Step(Points(Neutral()), 0, Team.Chaos, 0, 0.0);
        s.Step(Points(Capturing(Team.Chaos, 0.1f)), 0, Team.Chaos, 0, 0.1);
        var left = s.Step(Points(Capturing(Team.Chaos, 0.15f)), -1, Team.Chaos, 0, 0.2);
        Assert.Equal(new[] { new SeizeSoundEvent(SeizeSoundKind.CaptureStop, 0) }, left);
    }

    [Fact]
    public void A_point_that_becomes_the_local_sides_plays_the_objective_noise_and_ends_the_tick()
    {
        var s = new SeizeSounds();
        s.Step(Points(Neutral()), 0, Team.SpaceMarines, 0, 0.0);
        s.Step(Points(Capturing(Team.SpaceMarines, 0.99f)), 0, Team.SpaceMarines, 0, 0.1);
        var done = s.Step(Points(Owned(Team.SpaceMarines)), 0, Team.SpaceMarines, 0, 0.2);
        Assert.Contains(new SeizeSoundEvent(SeizeSoundKind.ObjectiveComplete, 0), done);
        Assert.Contains(new SeizeSoundEvent(SeizeSoundKind.CaptureStop, 0), done);
        Assert.DoesNotContain(done, e => e.Kind == SeizeSoundKind.CaptureTick);
    }

    [Fact]
    public void An_enemy_capture_or_a_neutralisation_is_no_objective_noise()
    {
        var s = new SeizeSounds();
        s.Step(Points(Owned(Team.SpaceMarines), Neutral()), -1, Team.SpaceMarines, 0, 0.0);
        // Chaos takes the second point; the first is neutralised.
        var events = s.Step(Points(Neutral(), Owned(Team.Chaos)), -1, Team.SpaceMarines, 0, 0.1);
        Assert.DoesNotContain(events, e => e.Kind == SeizeSoundKind.ObjectiveComplete);
    }

    [Fact]
    public void The_score_ticks_when_the_local_sides_score_rises_not_more_than_once_a_second()
    {
        var s = new SeizeSounds();
        s.Step(Points(Owned(Team.Chaos)), -1, Team.Chaos, 10, 0.0);
        Assert.Empty(s.Step(Points(Owned(Team.Chaos)), -1, Team.Chaos, 10, 0.5));
        Assert.Equal(new[] { new SeizeSoundEvent(SeizeSoundKind.ScoreTick, -1) }, s.Step(Points(Owned(Team.Chaos)), -1, Team.Chaos, 12, 1.0));
        Assert.Empty(s.Step(Points(Owned(Team.Chaos)), -1, Team.Chaos, 14, 1.4));                             // within the spacing
        Assert.Equal(new[] { new SeizeSoundEvent(SeizeSoundKind.ScoreTick, -1) }, s.Step(Points(Owned(Team.Chaos)), -1, Team.Chaos, 16, 2.1));
        Assert.Empty(s.Step(Points(Owned(Team.Chaos)), -1, Team.Chaos, 0, 3.0));                              // a restart resets the score: no tick
    }

    [Fact]
    public void Reset_forgets_the_points()
    {
        var s = new SeizeSounds();
        s.Step(Points(Neutral()), 0, Team.SpaceMarines, 0, 0.0);
        s.Step(Points(Capturing(Team.SpaceMarines, 0.1f)), 0, Team.SpaceMarines, 0, 0.1);
        s.Reset();
        Assert.Empty(s.Step(Points(Owned(Team.SpaceMarines)), 0, Team.SpaceMarines, 0, 0.2));                 // noted only, no objective noise
    }

    [Fact]
    public void Every_play_post_is_single_shot_so_no_endless_container_chain_stacks()
    {
        // BR-03: the Play events are endless trigger-rate containers (Stop events end them); a post that keeps its continuation stacked a growing hum.
        Assert.True(SeizeSounds.SingleShot(SeizeSoundKind.CaptureTick));
        Assert.True(SeizeSounds.SingleShot(SeizeSoundKind.ObjectiveComplete));
        Assert.True(SeizeSounds.SingleShot(SeizeSoundKind.ScoreTick));
        Assert.False(SeizeSounds.SingleShot(SeizeSoundKind.CaptureStop));

        // A long capture and the score ticking after it post a bounded, steady number of sounds (no per-frame or per-snapshot posts).
        var s = new SeizeSounds();
        s.Step(Points(Neutral()), 0, Team.SpaceMarines, 0, 0.0);
        int ticks = 0, scores = 0;
        for (int i = 1; i <= 600; i++)   // 20 s at 30 Hz of a moving capture, then the score rising 2 a second for 30 s
        {
            double t = i / 30.0;
            var pt = t < 20 ? Capturing(Team.SpaceMarines, (float)(t / 20)) : Owned(Team.SpaceMarines);
            foreach (var e in s.Step(Points(pt), 0, Team.SpaceMarines, t < 20 ? 0 : (int)((t - 20) * 2), t))
            {
                if (e.Kind == SeizeSoundKind.CaptureTick) ticks++;
                if (e.Kind == SeizeSoundKind.ScoreTick) scores++;
            }
        }
        Assert.InRange(ticks, 19, 21);
        Assert.InRange(scores, 0, 1);
    }
}
