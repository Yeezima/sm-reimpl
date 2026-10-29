using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M40 T2: the smoothed render clock (client presentation, ours). The clock advances by the frame delta, never runs backwards and
/// moves at 0.95 to 1.05 of real time however the raw target jitters; integrated with the client, remote players are drawn at
/// that clock. Synthetic numbers only.
/// </summary>
public class RenderClockTests(ITestOutputHelper output)
{
    const double Rate = 30;   // ticks per second
    const double Frame = 1.0 / 60;

    /// <summary>The raw target as the old code computed it: re-anchored to each 20 Hz snapshot's processing frame (arrival jitter up to a frame), with late packets.</summary>
    static double RawTarget(double now, Random random, ref double anchorAt, ref double anchorTick, ref double nextSnapshot, double lateness)
    {
        // A snapshot is "processed" at the first frame at or after its arrival (arrival = nominal + jitter).
        while (nextSnapshot + random.NextDouble() * 0.012 + lateness <= now)
        {
            anchorAt = now; anchorTick = Math.Round(nextSnapshot * Rate); nextSnapshot += 0.05;
        }
        return anchorTick + (now - anchorAt) * Rate;
    }

    [Fact]
    public void Steps_stay_within_five_percent_and_never_go_backwards_with_jittered_and_late_arrivals()
    {
        var clock = new RenderClock();
        var random = new Random(3);
        double anchorAt = 0, anchorTick = 0, next = 0.05, previous = double.NaN, minRatio = 9, maxRatio = 0;
        double rawMaxStep = 0, rawPrev = double.NaN;
        for (int i = 1; i <= 60 * 60; i++)   // a minute at 60 fps
        {
            double now = i * Frame;
            // Every 5 s a snapshot is 120 ms late; every second one is up to 12 ms off.
            double late = (i / 300) % 2 == 1 && i % 300 < 20 ? 0.12 : 0;
            double target = RawTarget(now, random, ref anchorAt, ref anchorTick, ref next, late) - 3;   // the interpolation delay
            double value = clock.Update(target, Frame, Rate);
            if (!double.IsNaN(previous) && i > 5)
            {
                double ratio = (value - previous) / (Frame * Rate);
                minRatio = Math.Min(minRatio, ratio); maxRatio = Math.Max(maxRatio, ratio);
                Assert.True(value >= previous, $"went backwards at frame {i}");
                rawMaxStep = Math.Max(rawMaxStep, Math.Abs((target - rawPrev) / (Frame * Rate) - 1));
            }
            previous = value; rawPrev = target;
        }
        output.WriteLine($"step / real time: min {minRatio:F3} max {maxRatio:F3}; the raw target's worst step was {rawMaxStep:P0} off real time");
        Assert.InRange(minRatio, 0.95 - 1e-9, 1.05 + 1e-9);
        Assert.InRange(maxRatio, 0.95 - 1e-9, 1.05 + 1e-9);
        Assert.True(rawMaxStep > 0.5, "the raw target jumps (the test is not vacuous)");
    }

    [Fact]
    public void A_late_packet_that_moves_the_target_back_only_slows_the_clock()
    {
        var clock = new RenderClock();
        double t = 0, value = 0;
        for (int i = 0; i < 120; i++) { t += Frame; value = clock.Update(t * Rate, Frame, Rate); }
        double before = value;
        // The target jumps back 0.1 s (3 ticks): the clock keeps running (at 95 %), it does not stop or reverse.
        for (int i = 0; i < 30; i++)
        {
            t += Frame;
            double v = clock.Update(t * Rate - 3, Frame, Rate);
            Assert.InRange(v - value, Frame * Rate * 0.95 - 1e-9, Frame * Rate * 1.05 + 1e-9);
            value = v;
        }
        Assert.True(value > before);
    }

    [Fact]
    public void After_a_latency_spike_the_clock_never_trails_the_raw_estimate_by_more_than_the_cap()
    {
        var clock = new RenderClock();
        double t = 0, value = 0, previous = 0;
        for (int i = 0; i < 120; i++) { t += Frame; value = clock.Update(t * Rate, Frame, Rate); }
        // Snapshots were 0.3 s late for a while, then arrive on time again: the raw estimate jumps up 9 ticks and stays.
        double worstBehind = 0;
        for (int i = 0; i < 60 * 10; i++)
        {
            previous = value;
            t += Frame;
            value = clock.Update(t * Rate + 9, Frame, Rate);
            Assert.True(value >= previous, "went backwards");
            worstBehind = Math.Max(worstBehind, t * Rate + 9 - value);
        }
        output.WriteLine($"worst distance behind the raw estimate after a 9 tick jump: {worstBehind:F2} ticks");
        Assert.True(worstBehind <= RenderClock.MaxBehindSeconds * Rate + 1e-9, $"{worstBehind:F2} ticks behind");
        // Ahead: the raw estimate drops 12 ticks (0.4 s): the clock waits, it does not reverse, and is back in step afterwards.
        value = clock.Value;
        for (int i = 0; i < 20; i++)
        {
            previous = value;
            t += Frame;
            value = clock.Update(t * Rate + 9 - 12, Frame, Rate);
            Assert.True(value >= previous, "went backwards");
        }
        Assert.True(value - (t * Rate - 3) <= RenderClock.MaxAheadSeconds * Rate + 1e-9);
    }

    [Fact]
    public void A_clock_running_a_few_percent_fast_or_slow_is_followed()
    {
        foreach (double drift in new[] { 1.03, 0.97 })
        {
            var clock = new RenderClock();
            double t = 0, value = 0;
            for (int i = 0; i < 60 * 40; i++) { t += Frame; value = clock.Update(t * drift * Rate, Frame, Rate); }
            Assert.True(Math.Abs(value - t * drift * Rate) < 0.5, $"drift {drift}: {value - t * drift * Rate:F2} ticks off after 40 s");
        }
    }

    [Fact]
    public void The_first_update_and_a_far_target_are_taken_at_once()
    {
        var clock = new RenderClock();
        Assert.False(clock.Started);
        Assert.Equal(100.0, clock.Update(100.0, Frame, Rate));
        Assert.True(clock.Started);
        Assert.Equal(100.0 + Frame * Rate, clock.Update(100.0 + Frame * Rate, Frame, Rate), 9);
        Assert.Equal(1000.0, clock.Update(1000.0, Frame, Rate));   // a 30 s stall: snap
        Assert.Equal(1000.0, clock.Update(double.NaN, Frame, Rate));   // junk is ignored
        clock.Reset();
        Assert.False(clock.Started);
    }

    [Fact]
    public void A_zero_or_odd_step_does_not_move_the_clock()
    {
        var clock = new RenderClock();
        clock.Update(50, Frame, Rate);
        double v = clock.Value;
        Assert.Equal(v, clock.Update(50, 0, Rate));
        Assert.Equal(v, clock.Update(50, -1, Rate));
        Assert.Equal(v, clock.Update(50, double.NaN, Rate));
    }

    // ---- integrated with the client ----

    static readonly LinkConditions Jittery = new(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(25), 0.02);

    /// <summary>Largest relative step of the shown render tick per 60 Hz frame over a run, for the smoothed and the old clock.</summary>
    (double Min, double Max) FrameSteps(bool smooth)
    {
        var options = new ClientOptions { SmoothRenderClock = smooth };
        var h = new Harness(Jittery, seed: 11, clients: 2, clientOptions: options);
        double previous = double.NaN, previousNow = 0, min = 9, max = -9;
        double nextFrame = 0;
        h.Run(TimeSpan.FromSeconds(40), Harness.Scripted, () =>
        {
            double now = h.Clock.Now.TotalSeconds;
            if (now < nextFrame) return;
            nextFrame += Frame;
            var (tick, _) = h.Clients[0].RemotePlayers();
            if (now > 8 && tick > 0 && !double.IsNaN(previous))
            {
                double ratio = (tick - previous) / ((now - previousNow) * Rate);
                min = Math.Min(min, ratio); max = Math.Max(max, ratio);
            }
            previous = tick; previousNow = now;
        });
        return (min, max);
    }

    [Fact]
    public void The_client_draws_remote_players_at_a_clock_that_runs_within_five_percent_of_real_time()
    {
        var (min, max) = FrameSteps(smooth: true);
        var (oldMin, oldMax) = FrameSteps(smooth: false);
        output.WriteLine($"per-frame step / real time: smoothed {min:F3} to {max:F3}; old {oldMin:F3} to {oldMax:F3}");
        Assert.InRange(min, 0.95 - 1e-6, 1.05 + 1e-6);
        Assert.InRange(max, 0.95 - 1e-6, 1.05 + 1e-6);
        Assert.True(oldMax - oldMin > 0.5, "the old clock re-anchors with every snapshot (the test is not vacuous)");
    }
}

/// <summary>Review S1 (M40 T2): the smoothed clock must not push the view tick below the server's lag-compensation floor.</summary>
public class ViewTickBoundTests(ITestOutputHelper output)
{
    sealed class SpyTransport(IClientTransport inner) : IClientTransport
    {
        public List<InputBatch> Batches { get; } = new();
        public void Send(byte[] packet) { if (Protocol.Read(packet) is InputBatch b) Batches.Add(b); inner.Send(packet); }
        public bool TryReceive(out byte[] packet) => inner.TryReceive(out packet);
    }

    /// <summary>The server accepts a view tick down to this many ticks before the snapshot the input acknowledges (GameServer.BoundedViewTick: delay ticks + 4).</summary>
    const int ServerFloorTicks = 7;

    (int Checked, int Below, long Worst) Run(Action<int, Harness> schedule, int steps)
    {
        var normal = new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), 0);
        var h = new Harness(normal, seed: 31, clients: 2);
        var spy = new SpyTransport(h.Net.Connect());
        var client = new GameClient(h.Config, Harness.Fingerprint, spy, h.Clock, nonce: 777);
        int checkedInputs = 0, below = 0;
        long worst = 0;
        for (int i = 0; i < steps; i++)
        {
            schedule(i, h);
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            for (int c = 0; c < h.Clients.Count; c++) { int index = c; h.Clients[c].Update(t => Harness.Scripted(index, t)); }
            int before = spy.Batches.Count;
            client.Update(t => Harness.Scripted(5, t));
            if (i < 4000) continue;
            foreach (var batch in spy.Batches.Skip(before))
            {
                var last = batch.Inputs[0];   // the newest input is first
                if (last.ViewTick == 0 || batch.SnapshotTick == 0) continue;
                checkedInputs++;
                long behind = (long)batch.SnapshotTick - last.ViewTick;   // ticks the view is before the acknowledged snapshot
                worst = Math.Max(worst, behind);
                if (behind > ServerFloorTicks) below++;
            }
        }
        return (checkedInputs, below, worst);
    }

    [Fact]
    public void An_input_after_a_latency_spike_never_carries_a_view_tick_the_server_would_clamp()
    {
        var normal = new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), 0);
        // 6 s normal, then 8 s of 340 ms latency (the clock settles to the late snapshots), then normal again: the raw estimate jumps up 9 ticks at once.
        var (checkedInputs, below, worst) = Run((i, h) =>
        {
            if (i == 6000) h.Net.Conditions = normal with { Latency = TimeSpan.FromMilliseconds(340) };
            if (i == 14000) h.Net.Conditions = normal;
        }, 30_000);
        output.WriteLine($"latency spike: {checkedInputs} inputs checked; the view tick was at most {worst} ticks before the acknowledged snapshot (the server clamps beyond {ServerFloorTicks}); {below} below the floor");
        Assert.True(checkedInputs > 300);
        Assert.Equal(0, below);
    }

    [Fact]
    public void An_input_after_a_snapshot_gap_of_a_third_of_a_second_never_carries_a_view_tick_the_server_would_clamp()
    {
        var normal = new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), 0);
        // Every 3 s the link loses everything for 0.3 s (no snapshot gets through), 0.4 s once.
        var (checkedInputs, below, worst) = Run((i, h) =>
        {
            int phase = i % 3000;
            bool gap = i > 5000 && phase < (i / 3000 == 4 ? 400 : 300);
            h.Net.Conditions = gap ? normal with { Loss = 1.0 } : normal;
        }, 30_000);
        output.WriteLine($"snapshot gaps: {checkedInputs} inputs checked; the view tick was at most {worst} ticks before the acknowledged snapshot (the server clamps beyond {ServerFloorTicks}); {below} below the floor");
        Assert.True(checkedInputs > 300);
        Assert.Equal(0, below);
    }
}
