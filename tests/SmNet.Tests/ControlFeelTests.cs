using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// feel/controls (client input handling and presentation, ours): the tap latch, the early tick (an input edge runs the next client tick at
/// once; the server and the prediction must still agree tick for tick), the own clip clock and the fresh aim ray. Synthetic configs only.
/// </summary>
public class ControlFeelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // ---- InputLatch ----

    [Fact]
    public void A_tap_between_two_samples_is_held_for_exactly_one_sample()
    {
        var latch = new InputLatch();
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
        latch.Press(InputButtons.Fire);
        latch.Release(InputButtons.Fire);
        Assert.True(latch.Pending);
        Assert.Equal(InputButtons.Fire, latch.Sample(InputButtons.None));
        Assert.False(latch.Pending);
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
    }

    [Fact]
    public void Held_buttons_read_as_before_when_no_event_came()
    {
        var latch = new InputLatch();
        Assert.Equal(InputButtons.Fire | InputButtons.Sprint, latch.Sample(InputButtons.Fire | InputButtons.Sprint));
        Assert.Equal(InputButtons.Fire | InputButtons.Sprint, latch.Sample(InputButtons.Fire | InputButtons.Sprint));
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
    }

    [Fact]
    public void A_release_and_press_of_a_held_button_between_samples_becomes_a_release_then_a_press()
    {
        var latch = new InputLatch();
        Assert.Equal(InputButtons.Melee, latch.Sample(InputButtons.Melee));
        latch.Release(InputButtons.Melee);
        latch.Press(InputButtons.Melee);
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.Melee));    // released this sample ...
        Assert.Equal(InputButtons.Melee, latch.Sample(InputButtons.Melee));   // ... so this one is a press edge
    }

    [Fact]
    public void A_whole_second_tap_of_a_held_button_is_carried_to_the_next_sample()
    {
        var latch = new InputLatch();
        Assert.Equal(InputButtons.Jump, latch.Sample(InputButtons.Jump));
        latch.Release(InputButtons.Jump);
        latch.Press(InputButtons.Jump);
        latch.Release(InputButtons.Jump);
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
        Assert.True(latch.Pending, "the carried press keeps the early tick wanted");
        Assert.Equal(InputButtons.Jump, latch.Sample(InputButtons.None));
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
    }

    [Fact]
    public void Other_buttons_are_untouched_and_reset_forgets_everything()
    {
        var latch = new InputLatch();
        latch.Press(InputButtons.Grenade);
        Assert.Equal(InputButtons.Grenade | InputButtons.Aim, latch.Sample(InputButtons.Aim));
        latch.Press(InputButtons.Fire);
        latch.MoveChanged();
        latch.Reset();
        Assert.False(latch.Pending);
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
        latch.MoveChanged();
        Assert.True(latch.Pending);
        Assert.Equal(InputButtons.None, latch.Sample(InputButtons.None));
        Assert.False(latch.Pending);
    }

    /// <summary>
    /// Random taps shorter than a tick, sampled every 33 ms: with the latch every tap gives one press edge; read held only, most are lost.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Short_taps_all_reach_the_simulation_as_press_edges_with_the_latch(bool latched)
    {
        var rng = new Random(11);
        var latch = new InputLatch();
        int taps = 0, edges = 0;
        bool held = false, last = false;
        double nextTap = 0.1, releaseAt = double.MaxValue, nextSample = 0;
        for (int ms = 0; ms < 60_000; ms++)
        {
            double t = ms / 1000.0;
            if (t >= nextTap) { held = true; latch.Press(InputButtons.Fire); taps++; releaseAt = t + 0.002 + rng.NextDouble() * 0.025; nextTap = t + 0.12 + rng.NextDouble() * 0.2; }
            if (t >= releaseAt) { held = false; latch.Release(InputButtons.Fire); releaseAt = double.MaxValue; }
            if (t >= nextSample)
            {
                nextSample += 1.0 / 30;
                var heldNow = held ? InputButtons.Fire : InputButtons.None;
                var sampled = latch.Sample(heldNow);
                bool down = ((latched ? sampled : heldNow) & InputButtons.Fire) != 0;
                if (down && !last) edges++;
                last = down;
            }
        }
        Assert.True(taps > 200);
        if (latched) Assert.Equal(taps, edges);
        else Assert.True(edges < taps * 0.8, $"held-only reading: {edges} of {taps} taps");
    }

    // ---- EarlyTick ----

    [Fact]
    public void An_early_tick_is_allowed_only_within_one_tick_of_the_next_due_one()
    {
        const double tick = 1.0 / 30;
        Assert.True(EarlyTick.Allowed(10.0, 10.0 + tick / 2, tick));
        Assert.True(EarlyTick.Allowed(10.0, 10.0 + tick, tick));
        Assert.False(EarlyTick.Allowed(10.0, 10.0, tick));                 // due: it runs anyway
        Assert.False(EarlyTick.Allowed(10.0, 10.0 + tick * 1.5, tick));    // the newest tick already ran early
        Assert.False(EarlyTick.Allowed(10.0, 10.0 + tick / 2, 0));
        Assert.False(EarlyTick.Allowed(double.NaN, 10.0, tick));
    }

    [Fact]
    public void The_clip_fraction_after_an_early_tick_runs_from_its_run_time_to_the_next_due_tick()
    {
        Assert.Equal(0f, EarlyTick.ClipFraction(5.0, 5.0, 5.05), 4);
        Assert.Equal(0.5f, EarlyTick.ClipFraction(5.025, 5.0, 5.05), 4);
        Assert.Equal(1f, EarlyTick.ClipFraction(5.06, 5.0, 5.05), 4);
        Assert.Equal(0f, EarlyTick.ClipFraction(4.9, 5.0, 5.05), 4);
        Assert.Equal(1f, EarlyTick.ClipFraction(5.0, 5.0, 5.0));
    }

    [Fact]
    public void The_pair_behind_is_drawn_only_until_the_early_ticks_scheduled_time()
    {
        const double tick = 1.0 / 30;
        Assert.True(EarlyTick.DrawsBehind(true, 10.0, 10.0 + tick * 1.5, tick));
        Assert.False(EarlyTick.DrawsBehind(true, 10.0, 10.0 + tick * 0.9, tick));    // its scheduled time has come
        Assert.False(EarlyTick.DrawsBehind(false, 10.0, 10.0 + tick * 1.5, tick));   // no early tick (the lead steering's pause): as before
    }

    /// <summary>
    /// A client running forward taps Fire at random: the drawn position (DrawPair) moves on every 1 ms step by about the running speed and never
    /// jumps or stands still across an early tick; the clip fraction never goes back within a tick.
    /// </summary>
    [Fact]
    public void The_drawn_position_keeps_moving_smoothly_across_early_ticks()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(15), TimeSpan.Zero, 0), seed: 4, clients: 1, Config(),
            new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var c = h.Clients[0];
        var latch = new InputLatch();
        var rng = new Random(9);
        double nextTap = 2.0;
        Vector3? last = null;
        int early = 0, steps = 0, still = 0;
        float maxStep = 0f;
        for (int ms = 0; ms < 8_000; ms++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            double now = ms / 1000.0;
            if (c.Status == ClientStatus.Connected && now >= nextTap) { latch.Press(InputButtons.Fire); latch.Release(InputButtons.Fire); nextTap = now + 0.1 + rng.NextDouble() * 0.2; }
            int before = c.EarlyTicks;
            c.Update(t => new PlayerInput(t, 0f, 1f, 0f, latch.Sample(InputButtons.None), 1.3f), tickNow: latch.Pending);
            early += c.EarlyTicks - before;
            if (c.Status != ClientStatus.Connected || now < 3.0) { last = null; continue; }
            var (from, to, f) = c.DrawPair();
            var drawn = ViewSmoothing.Interpolate(from.Position, to.Position, f);
            if (last is { } l && c.CorrectionErrors.Count > 0)
            {
                float step = Vector3.Distance(drawn, l);
                steps++;
                maxStep = MathF.Max(maxStep, step);
                if (step < 1e-5f) still++;
            }
            last = drawn;
        }
        Assert.True(early > 10, $"early ticks {early}");
        // 6.5 m/s is 6.5 mm a millisecond: no step much larger (a jump) and no millisecond without movement (a stall).
        Assert.True(maxStep < 0.0075f, $"largest step {maxStep * 1000:F2} mm");
        Assert.Equal(0, still);
    }

    // ---- the early tick end to end: client and server agree ----

    static SimConfig Config() => new SimConfig() with
    {
        Damage = 22f, ShotCooldown = 0.133f, ClipSize = 1000, ReserveSize = 0, ReloadTime = 1.3f, MaxRange = 250f,   // no reload in the run
        HealthMax = 100f, ShieldMax = 100f, RespawnTime = 5f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, WalkSpeed = 3.6f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>
    /// A client whose taps of Fire (2 to 27 ms long) and stick changes run early ticks: every tap is a shot on the server, the prediction matches
    /// the server's state on every compared tick, and the client runs no more ticks than the schedule (plus at most one).
    /// </summary>
    [Fact]
    public void Early_ticks_keep_the_prediction_and_the_server_in_agreement_and_lose_no_tap()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0), seed: 3, clients: 1, Config(),
            new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var c = h.Clients[0];
        var latch = new InputLatch();
        var rng = new Random(5);
        bool fire = false;
        float stickY = 0f;
        double nextTap = 2.0, releaseAt = double.MaxValue, nextStick = 2.0;
        int taps = 0;
        var predicted = new Dictionary<uint, PlayerState>();
        var truth = new SortedDictionary<uint, PlayerState>();
        uint firstTick = 0, connectedAt = 0;
        int ms = 0;
        PlayerInput Sample(uint t)
        {
            var held = fire ? InputButtons.Fire : InputButtons.None;
            // Aim at the sky (nobody is hit) and move back and forth.
            return new PlayerInput(t, 0f, stickY, 0.3f, latch.Sample(held), 1.3f);
        }
        for (; ms < 25_000; ms++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            double now = ms / 1000.0;
            if (c.Status == ClientStatus.Connected && now >= nextTap && now < 22.0) { fire = true; latch.Press(InputButtons.Fire); taps++; releaseAt = now + 0.002 + rng.NextDouble() * 0.025; nextTap = now + 0.25 + rng.NextDouble() * 0.2; }
            if (now >= releaseAt) { fire = false; latch.Release(InputButtons.Fire); releaseAt = double.MaxValue; }
            if (c.Status == ClientStatus.Connected && now >= nextStick) { stickY = stickY > 0 ? -1f : 1f; latch.MoveChanged(); nextStick = now + 0.3 + rng.NextDouble() * 0.4; }
            c.Update(Sample, tickNow: latch.Pending);
            if (c.Status == ClientStatus.Connected)
            {
                if (connectedAt == 0) { connectedAt = (uint)ms; firstTick = c.Tick; }
                predicted[c.Tick] = c.Predicted;
            }
            var w = h.Server.World;
            if (w.Players.TryGetValue(c.PlayerId, out var s)) truth[w.Tick] = s;
        }
        Assert.Equal(ClientStatus.Connected, c.Status);
        Assert.True(c.EarlyTicks > 50, $"early ticks {c.EarlyTicks}");

        // Every tap reached the server as one shot (the clip dropped by one; a reload refills it).
        int shots = 0;
        PlayerState? prev = null;
        foreach (var s in truth.Values) { if (prev is { } p && s.Clip == p.Clip - 1) shots++; prev = s; }
        Assert.True(taps > 40, $"taps {taps}");
        Assert.Equal(taps, shots);

        // The prediction equals the server's state on every tick both have (after the first authoritative state).
        int compared = 0;
        foreach (var (tick, pred) in predicted)
        {
            if (tick < firstTick + 90 || !truth.TryGetValue(tick, out var real)) continue;
            compared++;
            Assert.True(pred.Position == real.Position && pred.Clip == real.Clip && pred.Cooldown == real.Cooldown,
                $"tick {tick}: predicted {pred.Position} clip {pred.Clip} cd {pred.Cooldown}; server {real.Position} clip {real.Clip} cd {real.Cooldown}");
        }
        Assert.True(compared > 300, $"compared {compared}");
        Assert.True(c.CorrectionErrors.Skip(5).All(e => e < 1e-4f), $"corrections max {c.CorrectionErrors.Skip(5).DefaultIfEmpty().Max()}");

        // The schedule is kept: 30 ticks a second since the connection, at most one ahead (the lead steering may add or skip a tick on its own).
        double seconds = (ms - connectedAt) / 1000.0;
        long ran = c.Tick - firstTick;
        Assert.InRange(ran, (long)(seconds * 30) - 3, (long)(seconds * 30) + 3);
    }

    /// <summary>
    /// An edge that lands in an update where a scheduled tick is due is sampled by that tick: no second, early tick runs in the same update
    /// (it would only use up the one-tick allowance). The client updates every 16 ms (60 fps), presses land at random milliseconds.
    /// </summary>
    [Fact]
    public void An_input_in_an_update_where_a_tick_is_due_runs_no_extra_tick()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0), seed: 6, clients: 1, Config(),
            new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var c = h.Clients[0];
        var latch = new InputLatch();
        var rng = new Random(2);
        double nextTap = 1.5;
        int dueWithEdge = 0, earlyWithEdge = 0;
        for (int ms = 0; ms < 20_000; ms++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            double now = ms / 1000.0;
            if (c.Status == ClientStatus.Connected && now >= nextTap) { latch.Press(InputButtons.Fire); latch.Release(InputButtons.Fire); nextTap = now + 0.05 + rng.NextDouble() * 0.1; }
            if (ms % 16 != 0) continue;   // the client updates once per 60 fps frame: about every other frame has a due tick
            bool edge = latch.Pending;
            uint tickBefore = c.Tick;
            int earlyBefore = c.EarlyTicks;
            c.Update(t => new PlayerInput(t, 0f, 0f, 0f, latch.Sample(InputButtons.None), 1.3f), tickNow: edge);
            if (!edge || c.Status != ClientStatus.Connected || now < 2.0) continue;
            bool early = c.EarlyTicks > earlyBefore;
            int ran = (int)(c.Tick - tickBefore);
            if (early) { earlyWithEdge++; Assert.Equal(1, ran); }
            else if (ran > 0) dueWithEdge++;
            Assert.False(latch.Pending, "every edge is sampled in the update it arrives in, by the due tick or an early one");
        }
        Assert.True(dueWithEdge > 20 && earlyWithEdge > 20, $"edges in a due tick's update {dueWithEdge}, early {earlyWithEdge}");
    }

    sealed class SpyTransport(IClientTransport inner) : IClientTransport
    {
        public List<InputBatch> Batches { get; } = new();
        public void Send(byte[] packet) { if (Protocol.Read(packet) is InputBatch b) Batches.Add(b); inner.Send(packet); }
        public bool TryReceive(out byte[] packet) => inner.TryReceive(out packet);
    }

    sealed record LossyRun(int Compared, int Mismatches, int Corrections, float WorstCorrection, long TicksRan, double Seconds, float MaxDrawnStep,
        int StillWhileMoving, float CalmMaxStep, int CalmStill, long WorstViewBehind, int ViewChecked, int EarlyTicks, int Taps, int Shots);

    /// <summary>
    /// Two minutes on a lossy, jittery link (70 ms, 25 ms jitter, 5 % loss) of a client running a circle, tapping Fire and turning its stick, with
    /// early ticks on (and, for comparison, off with the same seed). Lead steering acts on its own. Measured: ticks where the prediction
    /// differs from the server, corrections, ticks run against the schedule, the drawn position (DrawPair plus the faded correction, as the
    /// client draws it) per millisecond, every input's view tick against the server's floor, taps against shots.
    /// </summary>
    static LossyRun RunLossy(bool earlyTicks, int seconds = 120)
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(25), 0.05);
        var h = new Harness(link, seed: 21, clients: 0, Config(), new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var spy = new SpyTransport(h.Net.Connect());
        var c = new GameClient(h.Config, Harness.Fingerprint, spy, h.Clock, nonce: 4242);
        var latch = new InputLatch();
        var rng = new Random(13);
        var correction = new CorrectionOffset();
        int serial = 0, taps = 0, still = 0, viewChecked = 0;
        long worstView = 0;
        float yaw = 0f, stickX = 0f, maxStep = 0f, calmMax = 0f;
        int calmStill = 0;
        double nextTap = 3.0, nextStick = 3.0;
        Vector3? lastDrawn = null;
        var predicted = new Dictionary<uint, PlayerState>();
        var truth = new SortedDictionary<uint, PlayerState>();
        uint firstTick = 0; int connectedAt = 0;
        int ms = 0;
        for (; ms < seconds * 1000; ms++)
        {
            // Disruptions that make the server miss or late-apply inputs (real corrections) and the lead steering act: a latency spike, a loss burst.
            if (ms == 30_000 || ms == 75_000) h.Net.Conditions = link with { Latency = TimeSpan.FromMilliseconds(260) };
            if (ms == 36_000 || ms == 80_000) h.Net.Conditions = link with { Loss = 0.6 };
            if (ms == 38_000 || ms == 82_000) h.Net.Conditions = link;
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            double now = ms / 1000.0;
            yaw += 0.0008f;   // a slow circle: 0.8 rad/s
            if (c.Status == ClientStatus.Connected && now >= nextTap && now < seconds - 3) { latch.Press(InputButtons.Fire); latch.Release(InputButtons.Fire); taps++; nextTap = now + 0.2 + rng.NextDouble() * 0.3; }
            if (c.Status == ClientStatus.Connected && now >= nextStick) { stickX = stickX == 0f ? 0.5f : 0f; latch.MoveChanged(); nextStick = now + 0.3 + rng.NextDouble() * 0.5; }
            int before = spy.Batches.Count;
            c.Update(t => new PlayerInput(t, stickX, 1f, yaw, latch.Sample(InputButtons.None), 1.3f), tickNow: earlyTicks && latch.Pending);
            foreach (var batch in spy.Batches.Skip(before))
            {
                var newest = batch.Inputs[0];
                if (newest.ViewTick == 0 || batch.SnapshotTick == 0 || now < 5) continue;
                viewChecked++;
                worstView = Math.Max(worstView, (long)batch.SnapshotTick - newest.ViewTick);
            }
            var w = h.Server.World;
            if (c.PlayerId >= 0 && w.Players.TryGetValue(c.PlayerId, out var s)) truth[w.Tick] = s;
            if (c.Status != ClientStatus.Connected) continue;
            if (connectedAt == 0) { connectedAt = ms; firstTick = c.Tick; }
            predicted[c.Tick] = c.Predicted;
            if (c.CorrectionSerial != serial) { serial = c.CorrectionSerial; correction.Add(c.LastCorrection); }
            var (from, to, f) = c.DrawPair();
            var drawn = ViewSmoothing.Interpolate(from.Position, to.Position, f) + correction.Step(0.001f);
            if (lastDrawn is { } l && now > 5)
            {
                float step = Vector3.Distance(drawn, l);
                bool calm = now is (> 5 and < 29) or (> 46 and < 74) or > 91;   // away from the disruptions and the lead steering they set off
                if (calm) { calmMax = MathF.Max(calmMax, step); if (step < 1e-5f) calmStill++; }
                maxStep = MathF.Max(maxStep, step);
                if (step < 1e-5f) still++;
            }
            lastDrawn = drawn;
        }
        int compared = 0, mismatches = 0;
        foreach (var (tick, pred) in predicted)
        {
            if (tick < firstTick + 150 || !truth.TryGetValue(tick, out var real)) continue;
            compared++;
            if (pred.Position != real.Position || pred.Clip != real.Clip) mismatches++;
        }
        int shots = 0;
        PlayerState? prev = null;
        foreach (var (tick, s) in truth) { if (tick > firstTick + 60 && prev is { } p && s.Clip == p.Clip - 1) shots++; prev = s; }
        var errors = c.CorrectionErrors.Skip(10).ToList();
        return new LossyRun(compared, mismatches, errors.Count(e => e > 1e-4f), errors.DefaultIfEmpty().Max(), c.Tick - firstTick, (ms - connectedAt) / 1000.0,
            maxStep, still, calmMax, calmStill, worstView, viewChecked, c.EarlyTicks, taps, shots);
    }

    [Fact]
    public void Early_ticks_on_a_lossy_jittery_link_keep_the_prediction_the_schedule_the_drawing_and_the_view_tick_floor()
    {
        var early = RunLossy(earlyTicks: true);
        var control = RunLossy(earlyTicks: false);
        string Text(LossyRun r) => $"{r.Compared} ticks compared, {r.Mismatches} differ; {r.Corrections} corrections (worst {r.WorstCorrection * 1000:F1} mm); " +
            $"{r.TicksRan} ticks in {r.Seconds:F1} s (schedule {r.Seconds * 30:F0}); drawn step max {r.MaxDrawnStep * 1000:F2} mm/ms, still {r.StillWhileMoving} ms (calm periods: max {r.CalmMaxStep * 1000:F2} mm/ms, still {r.CalmStill}); " +
            $"view tick at most {r.WorstViewBehind} ticks before the acked snapshot over {r.ViewChecked} inputs; early ticks {r.EarlyTicks}; taps {r.Taps}, shots {r.Shots}";
        output.WriteLine("early ticks: " + Text(early));
        output.WriteLine("control:     " + Text(control));
        Assert.True(early.EarlyTicks > 200);
        Assert.True(early.Compared > 3000);
        // The prediction differs from the server on as few ticks as without early ticks (losses and late inputs, not the early ticks).
        Assert.True(early.Mismatches <= Math.Max(control.Mismatches * 2, early.Compared / 200), $"{early.Mismatches} vs control {control.Mismatches}");
        // Ticks run against the schedule: the lead steering adds or skips a few on its own; early ticks add none in the long run.
        Assert.InRange(early.TicksRan, (long)(early.Seconds * 30) - 10, (long)(early.Seconds * 30) + 10);
        // The drawn position (6.5 m/s is 6.5 mm a millisecond; corrections fade over 0.1 s): in calm periods smooth (no jump, no stall) with early
        // ticks and corrections; during the disruptions the lead steering's catch-up and pause steps (a whole tick) are as in the control run.
        Assert.True(early.CalmMaxStep < 0.012f, $"calm step {early.CalmMaxStep * 1000:F2} mm");
        Assert.Equal(0, early.CalmStill);
        Assert.True(early.MaxDrawnStep <= control.MaxDrawnStep * 1.1f, $"{early.MaxDrawnStep} vs {control.MaxDrawnStep}");
        Assert.True(early.StillWhileMoving <= control.StillWhileMoving * 1.5 + 20, $"still {early.StillWhileMoving} vs {control.StillWhileMoving}");
        // Every input's view tick is within the server's floor (GameServer.BoundedViewTick: delay ticks + 4 = 7).
        Assert.True(early.ViewChecked > 3000);
        Assert.InRange(early.WorstViewBehind, 0, 7);
        // Taps reach the server as shots, as far as the link delivers inputs (8 redundant copies).
        Assert.True(early.Shots >= early.Taps * 0.97, $"taps {early.Taps}, shots {early.Shots}");
    }

    // ---- OwnClipClock ----

    [Fact]
    public void The_own_clip_is_drawn_a_tick_back_plus_the_fraction()
    {
        const float tick = 1f / 30;
        Assert.Equal(0.5f - tick, OwnClipClock.Time(0.5f, 0f, tick), 5);
        Assert.Equal(0.5f - tick / 2, OwnClipClock.Time(0.5f, 0.5f, tick), 5);
        Assert.Equal(0.5f, OwnClipClock.Time(0.5f, 1f, tick), 5);
        Assert.Equal(0f, OwnClipClock.Time(0.01f, 0f, tick));   // a clip that just began starts at 0
        Assert.Equal(0f, OwnClipClock.Time(float.NaN, 0.5f, tick));
    }

    [Fact]
    public void The_own_clip_clock_moves_every_frame_never_backwards_and_restarts_on_a_new_clip()
    {
        const float tick = 1f / 30, frame = 1f / 144;
        var clock = new OwnClipClock();
        float predicted = 0f, last = -1f, now = 0f;
        int still = 0, frames = 0;
        for (int i = 0; i < 144; i++)
        {
            now += frame;
            float due = MathF.Floor(now / tick) * tick;   // the predicted timer steps once per tick
            predicted = due;
            float fraction = (now - due) / tick;
            float shown = clock.Step(42, predicted, fraction, tick);
            Assert.True(shown <= predicted + 1e-6f && shown >= last - 1e-6f, $"frame {i}: shown {shown} predicted {predicted} last {last}");
            if (last >= 0f && shown > 1e-4f) { frames++; if (shown - last < 1e-6f) still++; }
            last = shown;
        }
        Assert.True(still <= 1, $"stood still in {still} of {frames} frames");
        Assert.Equal(0f, clock.Step(43, 0.01f, 0f, tick));          // another clip
        Assert.Equal(0.2f - tick, clock.Step(43, 0.2f, 0f, tick), 5);
        Assert.Equal(0f, clock.Step(43, 0.0f, 0f, tick));           // the same clip began again
    }

    // ---- FreshAim ----

    [Fact]
    public void The_fresh_ray_is_the_old_one_turned_by_the_view_change()
    {
        var pivot = new Vector3(3f, 4f, 0f);
        var origin = pivot + new Vector3(0.6f, -2.5f, 1.8f);
        var dir = Combat.AimDirection(0.4f, 0.1f);
        var (o, d) = FreshAim.Turn(origin, dir, pivot, 0.25f, -0.05f, 1.3f);
        var expected = Combat.AimDirection(0.65f, 0.05f);
        Assert.True(Vector3.Distance(d, expected) < 1e-4f, $"{d} vs {expected}");
        Assert.Equal(Vector3.Distance(origin, pivot), Vector3.Distance(o, pivot), 4);
        Assert.Equal(origin.Z, o.Z, 5);
        // No change: the same ray.
        var (o0, d0) = FreshAim.Turn(origin, dir, pivot, 0f, 0f, 1.3f);
        Assert.True(Vector3.Distance(o0, origin) < 1e-5f && Vector3.Distance(d0, dir) < 1e-5f);
        // The pitch is kept within the limit.
        var (_, up) = FreshAim.Turn(origin, Combat.AimDirection(0f, 1.2f), pivot, 0f, 0.5f, 1.3f);
        Assert.Equal(MathF.Sin(1.3f), up.Z, 4);
    }
}
