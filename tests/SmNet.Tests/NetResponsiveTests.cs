using System.Numerics;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>net/responsive: the input-lead slew, the own marine drawn ahead, the adaptive interpolation delay, 30 Hz snapshots and the predicted hit ledger.</summary>
public class NetResponsiveTests(ITestOutputHelper output)
{
    const double Tick = 1.0 / 30.0;

    // ---- adaptive interpolation delay ----

    /// <summary>Arrivals of every tick's snapshot (or every 1.5 ticks at 20 Hz), each seen at the next 60 fps frame after <paramref name="oneWay"/> ± <paramref name="jitter"/>.</summary>
    static double Run(InterpolationDelayEstimator e, double seconds, double ticksPerSnapshot, double jitter, int seed, double startAt = 0, uint startTick = 100, double lossEvery = 0)
    {
        var rng = new Random(seed);
        double delay = double.NaN;
        int n = (int)(seconds / (ticksPerSnapshot * Tick));
        for (int i = 0; i < n; i++)
        {
            if (lossEvery > 0 && i % (int)lossEvery == 0) continue;
            uint tick = startTick + (uint)Math.Round(i * ticksPerSnapshot);
            double sent = startAt + i * ticksPerSnapshot * Tick;
            double arrival = sent + 0.03 + (rng.NextDouble() * 2 - 1) * jitter;
            double frame = Math.Ceiling(arrival / (1.0 / 60)) * (1.0 / 60);
            delay = e.Note(tick, frame, Tick, 1.5 * Tick, 0.1, 0.1);
        }
        return delay;
    }

    [Fact]
    public void Interpolation_delay_is_one_and_a_half_snapshot_intervals_on_a_clean_link()
    {
        double at30 = Run(new InterpolationDelayEstimator(), 6, 1, 0.002, 1);
        double at20 = Run(new InterpolationDelayEstimator(), 6, 1.5, 0.002, 1);
        output.WriteLine($"30 Hz {at30 * 1000:F1} ms, 20 Hz {at20 * 1000:F1} ms");
        Assert.InRange(at30, 1.5 * Tick - 1e-9, 1.5 * Tick + 0.004);   // 50 ms
        Assert.InRange(at20, 0.075, 0.095);   // at 20 Hz the gaps alternate 1 and 2 ticks: the 2-tick gap plus jitter (about 89 ms)
    }

    [Fact]
    public void Interpolation_delay_rises_with_jitter_and_falls_back_slowly()
    {
        var e = new InterpolationDelayEstimator();
        double clean = Run(e, 10, 1, 0.002, 2);
        double jittery = Run(e, 3, 1, 0.045, 3, startAt: 10, startTick: 400);
        double soon = Run(e, 0.5, 1, 0.002, 4, startAt: 13, startTick: 490);
        double later = Run(e, 8, 1, 0.002, 5, startAt: 13.5, startTick: 505);
        output.WriteLine($"clean {clean * 1000:F1}, jittery {jittery * 1000:F1}, 0.5 s after {soon * 1000:F1}, 8 s after {later * 1000:F1} ms");
        Assert.True(jittery > clean + 0.004, "45 ms of jitter needs more delay");
        Assert.True(jittery <= 0.1 + 1e-9, "never past the server's lag-compensation bound");
        Assert.True(soon > clean + 0.005, "falls slowly");
        Assert.InRange(later, clean - 1e-9, clean + 0.004);
    }

    [Fact]
    public void Interpolation_delay_covers_lost_snapshots_up_to_the_ceiling()
    {
        double lossy = Run(new InterpolationDelayEstimator(), 6, 1, 0.002, 6, lossEvery: 3);   // every third snapshot lost: gaps of two ticks
        output.WriteLine($"one in three lost: {lossy * 1000:F1} ms");
        Assert.True(lossy > 1.5 * Tick + 0.01 && lossy <= 0.1 + 1e-9);
    }

    // ---- predicted hits ----

    [Fact]
    public void Predicted_hits_are_not_shown_twice_but_kills_and_extra_hits_are()
    {
        var l = new PredictedHitLedger();
        var none = new ReportFeedback(false, false);
        var both = new ReportFeedback(true, true);
        var markerOnly = new ReportFeedback(true, false);
        l.Predict(5, head: false, 0.0);
        Assert.Equal(none, l.Confirm(1, 5, head: false, kill: false, melee: false, splash: false, 0.15));   // the prediction showed it
        l.Predict(5, false, 1.0);
        Assert.Equal(markerOnly, l.Confirm(1, 5, false, kill: true, false, false, 1.15));                // review M1: the kill marker, no second smack
        l.Predict(5, false, 2.0);
        Assert.Equal(both, l.Confirm(2, 5, false, false, false, false, 2.15));                           // one hit no prediction showed: its marker and sound
        l.Predict(5, false, 3.0);
        Assert.Equal(markerOnly, l.Confirm(1, 5, head: true, false, false, false, 3.15));                // review M1: a predicted body hit ruled a headshot: the head marker, no second smack
        l.Predict(5, head: true, 3.5);
        Assert.Equal(none, l.Confirm(1, 5, head: true, false, false, false, 3.6));                       // a predicted headshot confirmed: nothing again
        Assert.Equal(both, l.Confirm(1, 7, false, false, melee: true, false, 3.7));                      // melee is not a ranged prediction
        Assert.Equal(both, l.Confirm(1, 7, false, false, false, false, 3.8));                            // nothing predicted
        Assert.Equal(both, l.Confirm(1, 7, false, kill: true, false, false, 3.9));                       // an unpredicted kill: its marker and sound
        l.Predict(9, false, 4.0);
        Assert.Equal(both, l.Confirm(1, 9, false, false, false, false, 4.0 + PredictedHitLedger.Window + 0.1));   // too late: expired
        Assert.Equal(1, l.Unconfirmed);
        Assert.Equal(0, l.Pending);
    }

    [Fact]
    public void A_prediction_on_one_victim_covers_a_report_on_another()
    {
        var l = new PredictedHitLedger();
        l.Predict(5, false, 0.0);
        Assert.Equal(new ReportFeedback(false, false), l.Confirm(1, 6, false, false, false, false, 0.1));   // the marker was shown; the report names another victim (two in the ray)
        l.Predict(5, false, 1.0);
        l.Clear();
        Assert.Equal(0, l.Pending);
        Assert.Equal(new ReportFeedback(true, true), l.Confirm(1, 5, false, false, false, false, 1.1));   // after a respawn's clear nothing is matched
    }

    // ---- clip lead ----

    [Fact]
    public void Clip_time_with_a_lead_runs_a_tick_ahead_and_never_past_it()
    {
        float t = (float)Tick;
        Assert.Equal(1f - 0.5f * t, OwnClipClock.Time(1f, 0.5f, t), 5);
        Assert.Equal(1f + 0.5f * t, OwnClipClock.Time(1f, 0.5f, t, lead: 1f), 5);
        Assert.Equal(1f + t, OwnClipClock.Time(1f, 1.5f, t, lead: 1f), 5);
        var clock = new OwnClipClock();
        float a = clock.Step(1, 1f, 0.9f, t, lead: 1f), b = clock.Step(1, 1f + t, 0.0f, t, lead: 1f);
        Assert.True(b >= a - 1e-6f, "no step back at the tick");
    }

    // ---- the own marine ahead, the lead, 30 Hz ----

    [Fact]
    public void Own_marine_is_drawn_from_the_newest_tick_and_moves_on_without_steps()
    {
        foreach (bool ahead in new[] { false, true })
        {
            var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.Zero, 0), 3, 0);
            var c = h.AddClient(nonce: 100, options: new ClientOptions { DrawOwnAhead = ahead });
            h.Run(TimeSpan.FromSeconds(2));
            var steps = new List<float>();
            var lagBehindNewest = new List<float>();
            Vector3? last = null;
            for (int ms = 0; ms < 3000; ms++)
            {
                h.Clock.Advance(TimeSpan.FromMilliseconds(1));
                h.Server.Update();
                if (ms % 16 != 0) continue;
                c.Update(t => new PlayerInput(t, 0, 1, 0f, InputButtons.None));   // run forward (+Y)
                var (from, to, f) = c.DrawPair();
                var drawn = ViewSmoothing.Interpolate(from.Position, to.Position, f) + c.DrawBlendOffset();
                if (ms > 500 && last is { } l) steps.Add(Vector3.Distance(drawn, l));
                if (ms > 500) lagBehindNewest.Add(c.Predicted.Position.Y - drawn.Y);
                last = drawn;
            }
            float mean = steps.Average();
            output.WriteLine($"ahead {ahead}: frame step mean {mean * 1000:F1} mm max {steps.Max() * 1000:F1} mm; drawn behind the newest predicted tick by mean {lagBehindNewest.Average() * 1000:F0} mm, max {lagBehindNewest.Max() * 1000:F0} mm");
            Assert.True(steps.Max() < 1.6f * mean, "no steps while running");
            if (ahead) Assert.True(lagBehindNewest.Max() <= 0.001f, "never behind the newest predicted tick");
            else Assert.True(lagBehindNewest.Average() > 0.05f, "the old draw is about half a tick behind");
        }
    }

    [Fact]
    public void Lead_settles_at_the_target_after_an_inflated_handshake_and_the_old_steps_do_not()
    {
        var link = new NetLatencyProbe.Link("warmup", 30, 5, 0, HandshakeOneWay: 200);
        var fresh = NetLatencyProbe.Measure(link, 8);
        var old = NetLatencyProbe.Measure(link, 8, NetLatencyProbe.OldNet, NetLatencyProbe.OldServer);
        double Median(NetLatencyProbe.Run r) => NetLatencyProbe.Median(r.Leads.Where(x => x.At > 5).Select(x => (double)x.Lead));
        output.WriteLine($"lead after 2 s: new {Median(fresh)}, old {Median(old)}; new shot-hit {NetLatencyReadout.Stats(fresh.Readout.All("shot-hit"))}");
        Assert.Equal(1, Median(fresh));   // protocol 65: the slack at a tick plus jitter reads as a whole-tick lead of 1
        Assert.True(Median(old) >= 4);
        var slack = fresh.Readout.All("slack").Skip(150).ToList();
        output.WriteLine($"slack after 5 s: {NetLatencyReadout.Stats(slack)}");
        Assert.InRange(NetLatencyProbe.Median(slack), 34, 70);   // a tick, the measured jitter (5 ms link jitter, 60 fps frames), 5 ms
        Assert.Equal(0, fresh.Corrections);
    }

    [Fact]
    public void Lead_slew_stretches_ticks_by_at_most_the_slew_and_ends()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(0), TimeSpan.Zero, 0), 3, 0);   // a handshake on 127.0.0.1 starts the lead at 3
        var c = h.AddClient(nonce: 100);
        var scales = new HashSet<int>();
        h.Run(TimeSpan.FromSeconds(12), null, () => scales.Add(c.LeadSlew));
        output.WriteLine($"slews seen {string.Join(",", scales)}; filtered slack {c.FilteredInputSlack:F1} ms, target {c.TargetInputSlack:F1}, jitter {c.SlackJitter:F1}; jumps {c.LeadJumps}");
        Assert.Contains(1, scales);
        Assert.Equal(0, c.LeadSlew);
        Assert.InRange(c.FilteredInputSlack - c.TargetInputSlack, -NetResponsiveDeadband, NetResponsiveDeadband);
        Assert.InRange(c.TargetInputSlack, 1000 * Tick, 1000 * Tick + 12);   // a tick, ~no jitter, the 5 ms margin
        Assert.Equal(0, c.LeadJumps);
        Assert.InRange(c.CurrentTickSeconds, Tick * 0.959, Tick * 1.041);
    }

    const double NetResponsiveDeadband = GameClient.SlackDeadbandMs + 2;

    [Fact]
    public void Snapshots_carry_the_input_slack_and_a_tick_run_without_the_input_reads_negative()
    {
        var snap = new Snapshot(9, 8, -3, [], InputSlack: -33);
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(snap)));
        Assert.Equal((-3, -33), (back.InputLead, back.InputSlack));
        var none = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 8, Protocol.NoLead, []))));
        Assert.Equal((Protocol.NoLead, Protocol.NoSlack), (none.InputLead, none.InputSlack));
        Assert.Equal(65, Protocol.Version);

        // A live client: positive slack; with its uplink cut for 200 ms (under the 500 ms neutral-input wait) the server runs ticks without its input: negative slack.
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0), 5, 1);
        h.Run(TimeSpan.FromSeconds(3));
        var c = h.Clients[0];
        Assert.InRange(c.FilteredInputSlack, 1, 200);
        int missedBefore = c.MissedInputs;
        h.Net.Conditions = h.Net.Conditions with { Loss = 1.0 };
        h.Run(TimeSpan.FromMilliseconds(200));
        h.Net.Conditions = new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0);
        h.Run(TimeSpan.FromSeconds(1));
        output.WriteLine($"missed inputs {c.MissedInputs - missedBefore}, jitter guard {c.JitterGuard}");
        Assert.True(c.MissedInputs > missedBefore);
        Assert.True(c.JitterGuard >= 1);
    }

    [Fact]
    public void A_client_stall_does_not_pile_up_lead()
    {
        // The client freezes for 400 ms (a hitch) three times; its tick loop runs the missed ticks itself, so the lead must not grow by catch-up jumps on top.
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2), 0), 7, 1);
        var c = h.Clients[0];
        var leads = new List<int>();
        for (int ms = 0; ms < 14_000; ms++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            bool frozen = ms is (>= 4000 and < 4400) or (>= 7000 and < 7400) or (>= 10000 and < 10400);
            if (!frozen && ms % 16 == 0) c.Update(t => new PlayerInput(t, 0, 1, 0f, InputButtons.None));
            if (ms > 4400 && !frozen && double.IsFinite(c.FilteredInputLead)) leads.Add((int)Math.Round(c.FilteredInputLead));
        }
        output.WriteLine($"largest filtered lead after a stall {leads.Max()}; after three 400 ms stalls: filtered slack {c.FilteredInputSlack:F1} ms (target {c.TargetInputSlack:F1}), lead {c.FilteredInputLead:F2}, jumps {c.LeadJumps}, guard {c.JitterGuard}");
        Assert.True(c.FilteredInputLead <= 3.0, $"lead {c.FilteredInputLead:F2}");
        Assert.True(leads.Max() <= 3, $"lead after a stall {leads.Max()}");   // before the schedule moved with a stall's skipped ticks: 10
        Assert.True(c.StallSkips >= 3);
        Assert.True(c.FilteredInputSlack <= c.TargetInputSlack + 2 * 1000 * Tick, $"slack {c.FilteredInputSlack:F1}");
        Assert.Equal(0, c.JitterGuard);   // its own stalls are not the link's jitter
    }

    [Fact]
    public void A_late_snapshot_carries_remote_players_on_for_at_most_a_tick()
    {
        foreach (double cap in new[] { 1.0, 0.0 })
        {
            var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0), 2, 0);
            var viewer = h.AddClient(nonce: 100, options: new ClientOptions { MaxExtrapolationTicks = cap });
            var runner = h.AddClient(nonce: 101);
            h.Run(TimeSpan.FromSeconds(3), (i, t) => i == 1 ? new PlayerInput(t, 0, 1, 0f, InputButtons.None) : new PlayerInput(t, 0, 0, 0f, InputButtons.None));
            float Y() => viewer.RemotePlayers().Players.First(p => p.PlayerId == runner.PlayerId).Position.Y;
            float before = Y();
            // Snapshots stop arriving for 150 ms (the server's to the viewer and everyone's: the whole link drops); the runner keeps running on the server.
            h.Net.Conditions = h.Net.Conditions with { Loss = 1.0 };
            var ys = new List<float>();
            h.Run(TimeSpan.FromMilliseconds(150), (i, t) => i == 1 ? new PlayerInput(t, 0, 1, 0f, InputButtons.None) : new PlayerInput(t, 0, 0, 0f, InputButtons.None), () => ys.Add(Y()));
            float moved = ys.Max() - before, step = ys.Zip(ys.Skip(1)).Max(p => MathF.Abs(p.Second - p.First));
            output.WriteLine($"cap {cap}: drawn on {moved:F3} m in the gap, extrapolated {viewer.ExtrapolatedTicks:F2} ticks, largest step {step * 1000:F1} mm/ms");
            float speed = h.Config.RunSpeed;
            if (cap > 0) Assert.InRange(viewer.ExtrapolatedTicks, 0.99, 1.0);
            else Assert.Equal(0, viewer.ExtrapolatedTicks);
            // At most the interpolation delay's worth of interpolated motion plus the capped tick (never on without bound).
            Assert.True(moved <= speed * (0.1f + (float)cap * (float)Tick) + 0.05f, $"moved {moved}");
        }
    }

    [Fact]
    public void Server_sends_a_snapshot_every_tick_and_twenty_a_second_with_the_old_rate()
    {
        foreach (int rate in new[] { 30, 20 })
        {
            var h = new Harness(LinkConditions.Perfect, 1, 1, serverOptions: new ServerOptions { SnapshotRate = rate });
            h.Run(TimeSpan.FromSeconds(1));
            var ticks = new List<uint>();
            h.Run(TimeSpan.FromSeconds(2), null, () => { if (ticks.Count == 0 || ticks[^1] != h.Clients[0].LatestSnapshotTick) ticks.Add(h.Clients[0].LatestSnapshotTick); });
            Assert.InRange(ticks.Count, rate * 2 - 2, rate * 2 + 2);
        }
    }

    [Fact]
    public void The_latency_readout_is_off_by_default()
    {
        var h = new Harness(LinkConditions.Perfect, 1, 1);
        h.Run(TimeSpan.FromSeconds(1));
        if (NetLatencyReadout.EnvSeconds <= 0) Assert.Null(h.Clients[0].Latency);
        Assert.True(h.Clients[0].EchoRoundTripMs >= 0, "the echo round trip is measured anyway (the lead steering's hold)");
    }
}

