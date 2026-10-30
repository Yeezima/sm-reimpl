using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/jump-pack-2 (the owner's decision of 2026-10-29: "revert to the old jump behavior. space bar tap makes a hop dash, space bar medium hold
/// makes a small jump, and space bar long hold is a full jump"): <see cref="JumpPackSpec.HoldTiers"/>. With the press as tick 0: Jump let go by
/// tick 5 (the dash's decision tick) is the dash; still down on tick 5 and let go by tick 12 is the small jump (every one with the same apex, the
/// M44 shortest jump's); still down on tick 12 (the data's full hold, 0.4 s) is the full jump, tick for tick the M14 / M44 one. Synthetic specs
/// shaped like the install's values (the M44 dash config with the tiers switched on). No original bytes.
/// </summary>
public class JumpTierTests(ITestOutputHelper output)
{
    const int Assault = 0;
    const float Dt = 1f / 30f;

    static readonly JumpPackSpec Tiered = JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true };
    static readonly SimConfig On = JumpPackDashTests.Config with { JumpPacks = [Tiered] };
    static readonly SimConfig Off = JumpPackDashTests.Config;

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, default, Vector3.Zero, 0f, true) { Class = Assault }, c);

    static void Step(ref PlayerState s, InputButtons b, SimConfig c, float my = 0f, CollisionWorld? world = null) =>
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b, 0f), c, out _, world);

    /// <summary>The whole jump with Jump down on ticks 0 .. held − 1 (the press is tick 0): every state until the landing (or the dash's end).</summary>
    static List<PlayerState> Jump(SimConfig c, int held, float my = 0f, Func<int, PlayerState, InputButtons>? extra = null, CollisionWorld? world = null)
    {
        var s = Fresh(c);
        var states = new List<PlayerState>();
        for (int k = 0; k < 400; k++)
        {
            var b = k < held ? InputButtons.Jump : InputButtons.None;
            if (extra is not null) b |= extra(k, s);
            Step(ref s, b, c, my, world);
            states.Add(s);
            if (s.Jump is JumpPhase.Landed or JumpPhase.Recover || (k > 6 && s.Jump == JumpPhase.None && s.EvadeTicks == 0 && s.Grounded)) break;
        }
        return states;
    }

    static float Apex(List<PlayerState> states) => states.Max(s => s.Position.Z);
    static bool Dashed(List<PlayerState> states) => states.Any(s => s.EvadeTicks > 0) && !states.Any(s => s.Jump == JumpPhase.Ascent);

    [Fact]
    public void The_tier_ticks_are_the_dash_decision_and_the_full_hold()
    {
        Assert.Equal((5, 12), JumpPack.TierTicks(Tiered, JumpPackDashTests.Dash.ToJumpAt, Dt));
        Assert.Equal(6, JumpPack.LiftOffTick(Tiered, Dt));
        Assert.Equal(0.4f, Tiered.MaxHold, 5);
    }

    [Theory]
    [InlineData(1)]   // a one-tick tap (what the tap latch makes of a click shorter than a tick)
    [InlineData(2)]
    [InlineData(5)]   // Jump down on ticks 0..4, up on tick 5: the last tap
    public void A_tap_released_by_the_decision_tick_is_the_dash_as_before(int held)
    {
        var tier = Jump(On, held, my: 1f);
        var old = Jump(Off, held, my: 1f);
        Assert.True(Dashed(tier), $"held {held}: not a dash");
        Assert.Equal(old.Count, tier.Count);
        for (int k = 0; k < old.Count; k++) Assert.Equal(old[k], tier[k]);
        Assert.DoesNotContain(tier, s => s.JumpCut);
    }

    [Theory]
    [InlineData(6)]    // down on 0..5 (committed on tick 5), let go on the lift-off tick: the small jump itself
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]   // down on 0..11, let go on tick 12 (the hold would have reached 0.4 s on it): still the small jump
    public void A_medium_hold_is_the_small_jump_with_one_fixed_apex(int held)
    {
        var small = Jump(Off, 6);   // the M44 shortest jump (hold 0.167 s)
        float target = Apex(small);
        var tier = Jump(On, held);
        Assert.Contains(tier, s => s.Jump == JumpPhase.Ascent);
        Assert.DoesNotContain(tier, s => s.EvadeTicks > 0);
        float apex = Apex(tier);
        output.WriteLine($"Jump let go on tick {held} ({held * Dt:F3} s after the press): apex {apex:F3} m (the small jump {target:F3} m; M44 rules {Apex(Jump(Off, held)):F3} m)");
        Assert.Equal(target, apex, 2);   // within 5 mm
        Assert.Equal(JumpPhase.Landed, tier[^1].Jump);
        if (held == 6) for (int k = 0; k < small.Count; k++) Assert.Equal(small[k], tier[k]);   // let go on the lift-off tick: the M44 arc itself, no cut
        else
        {
            // Let go in the ascent: the cut on the release tick, its mark the release tick, latched to the apex.
            Assert.Equal((JumpPhase.Ascent, true, held, false), (tier[held].Jump, tier[held].JumpCut, tier[held].JumpMark, tier[held].JumpHolding));
            int apexTick = tier.FindIndex(s => s.Jump == JumpPhase.Hang);
            for (int k = held; k < apexTick; k++) Assert.True(tier[k].JumpCut);
            Assert.False(tier[apexTick].JumpCut);
            // The cut's deceleration is constant (one gravity per release tick) and at least the jumping gravity.
            float g = JumpPack.TierCutGravity(Tiered, On, 5, held);
            Assert.True(g >= Tiered.JumpingGravity);
            for (int k = held + 1; k < apexTick; k++) Assert.Equal(-g * Dt, tier[k].Velocity.Z - tier[k - 1].Velocity.Z, 3);
            output.WriteLine($"  cut gravity {g:F2} m/s² from tick {held}; apex tick {apexTick}");
        }
    }

    [Theory]
    [InlineData(13)]   // down on 0..12: the hold reaches 0.4 s on tick 12
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(400)]
    public void A_long_hold_is_the_full_jump_tick_for_tick(int held)
    {
        var full = Jump(Off, 400);
        var tier = Jump(On, held);
        output.WriteLine($"Jump let go on tick {held}: apex {Apex(tier):F3} m (full {Apex(full):F3} m)");
        Assert.Equal(full.Count, tier.Count);
        for (int k = 0; k < full.Count; k++) Assert.Equal(full[k] with { JumpHeld = false }, tier[k] with { JumpHeld = false });
        Assert.DoesNotContain(tier, s => s.JumpCut);
        Assert.True(Apex(tier) > 17f);
    }

    [Fact]
    public void The_three_jumps_are_distinct_and_the_boundaries_exact()
    {
        float smallApex = Apex(Jump(On, 6)), fullApex = Apex(Jump(On, 400));
        output.WriteLine($"small jump {smallApex:F2} m, full jump {fullApex:F2} m");
        Assert.InRange(smallApex, 7f, 8.5f);
        Assert.InRange(fullApex, 17f, 18f);
        Assert.True(Dashed(Jump(On, 5)));
        Assert.False(Dashed(Jump(On, 6)));
        Assert.Equal(smallApex, Apex(Jump(On, 12)), 2);
        Assert.Equal(fullApex, Apex(Jump(On, 13)), 4);
    }

    [Theory]
    [InlineData(5, 35f)]    // the dash: nothing spent
    [InlineData(6, 35f)]    // the small jump: the 0.167 s hold's cost
    [InlineData(10, 35f)]   // a longer medium hold spent more on the way; given back at the release
    [InlineData(12, 35f)]
    [InlineData(13, 55f)]   // the full jump: the full hold's cost
    public void A_jump_costs_its_tier(int held, float cost)
    {
        var s = Jump(On, held, my: held <= 5 ? 1f : 0f);
        if (held <= 5) { Assert.Equal(Tiered.MaxPower, s.Min(p => p.JumpPower), 2); return; }
        Assert.Equal(JumpPhase.Landed, s[^1].Jump);
        Assert.Equal(Tiered.MaxPower - cost, s[^1].JumpPower, 2);
    }

    [Fact]
    public void The_tap_latch_turns_a_click_shorter_than_a_tick_into_a_one_tick_press_which_dashes()
    {
        var latch = new InputLatch();
        latch.Press(InputButtons.Jump); latch.Release(InputButtons.Jump);   // down and up between two samples
        var first = latch.Sample(InputButtons.None);
        var second = latch.Sample(InputButtons.None);
        Assert.Equal((InputButtons.Jump, InputButtons.None), (first, second));
        var s = Fresh(On);
        var states = new List<PlayerState>();
        foreach (var b in new[] { first, second }.Concat(Enumerable.Repeat(InputButtons.None, 40))) { Step(ref s, b, On, 1f); states.Add(s); }
        Assert.True(Dashed(states));
        // Held across six samples (about 0.2 s): the small jump; across thirteen (0.43 s): the full jump.
        foreach (var (samples, apex) in new[] { (6, Apex(Jump(On, 6))), (13, Apex(Jump(On, 400))) })
        {
            var l = new InputLatch();
            l.Press(InputButtons.Jump);
            var held = new List<PlayerState>();
            var p = Fresh(On);
            for (int k = 0; k < 400; k++)
            {
                if (k == samples) l.Release(InputButtons.Jump);
                var b = l.Sample(k < samples ? InputButtons.Jump : InputButtons.None);
                Step(ref p, b, On);
                held.Add(p);
                if (p.Jump == JumpPhase.Landed) break;
            }
            Assert.Equal(apex, Apex(held), 3);
        }
    }

    [Fact]
    public void A_press_on_the_take_off_s_early_ticks_never_counts_twice_and_a_re_press_after_the_decision_does_not_lift_the_small_jump()
    {
        // Let go on tick 6 and down again from tick 8 on: the hold stopped at the release (as M14), the re-press is no hold.
        var re = Jump(On, 0, extra: (k, _) => k < 6 || k >= 8 ? InputButtons.Jump : InputButtons.None);
        Assert.Equal(Apex(Jump(On, 6)), Apex(re), 3);
        // Let go on tick 9 and down again from tick 10 on: the small jump's cut stays latched to the apex.
        var cut = Jump(On, 0, extra: (k, _) => k < 9 || k >= 10 ? InputButtons.Jump : InputButtons.None);
        Assert.Equal(Apex(Jump(On, 6)), Apex(cut), 2);
    }

    [Fact]
    public void The_small_jump_on_a_level_tops_out_at_the_same_height()
    {
        var v = new[] { new Vector3(-300, -300, 0), new Vector3(300, -300, 0), new Vector3(300, 300, 0), new Vector3(-300, 300, 0) };
        var mesh = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var world = new CollisionWorld(mesh, mesh);
        float flat = Apex(Jump(On, 6, world: world));
        foreach (int held in new[] { 8, 11, 12 }) Assert.Equal(flat, Apex(Jump(On, held, world: world)), 2);
        Assert.True(Apex(Jump(On, 13, world: world)) > flat + 8f);
    }

    [Fact]
    public void Space_held_through_a_landing_still_jumps_and_a_tap_in_the_air_dashes_after_it()
    {
        // A full jump; from its fall Space is pressed and held: buffered through the landing, it takes off on the first allowed tick and,
        // held on, is a jump (here the full one).
        var c = On with { InputBufferWindow = 0.3f };   // the install's buffer (feel/buffer-speed)
        var s = Fresh(c);
        var states = new List<PlayerState>();
        bool pressedInFall = false;
        for (int k = 0; k < 400; k++)
        {
            if (s.Jump == JumpPhase.Fall) pressedInFall = true;
            bool down = k < 20 || pressedInFall;   // the first jump held full, then Space down again from the fall on
            Step(ref s, down ? InputButtons.Jump : InputButtons.None, c);
            states.Add(s);
            if (states.Count(p => p.Jump == JumpPhase.Landed) > 0 && s.Jump == JumpPhase.Hang) break;   // the second jump's apex
        }
        int landing = states.FindIndex(p => p.Jump == JumpPhase.Landed);
        int second = states.FindIndex(landing, p => p.Jump == JumpPhase.TakeOff);
        output.WriteLine($"landed on tick {landing}; the held Space took off again on tick {second} ({second - landing} ticks later); its apex {Apex(states.Skip(second).ToList()):F2} m");
        Assert.True(second > landing);
        Assert.Equal(Apex(Jump(On, 400)), Apex(states.Skip(second).ToList()), 2);   // held on: the full jump
        // A tap in the fall (one tick) is buffered for 0.3 s: replayed after the landing as a one-tick press, it is the dash.
        var t = Fresh(c);
        var taps = new List<PlayerState>();
        int landed = -1;
        bool tapped = false;
        for (int k = 0; k < 400; k++)
        {
            var b = k < 20 ? InputButtons.Jump : InputButtons.None;
            if (!tapped && t.Jump == JumpPhase.Fall && t.Position.Z < 1.2f) { b = InputButtons.Jump; tapped = true; }
            Step(ref t, b, c, my: 1f);
            taps.Add(t);
            if (landed < 0 && t.Jump == JumpPhase.Landed) landed = k;
            if (landed >= 0 && k > landed + 40) break;
        }
        Assert.True(tapped && landed > 0);
        Assert.Contains(taps.Skip(landed), p => p.EvadeTicks > 0);
        Assert.DoesNotContain(taps.Skip(landed), p => p.Jump == JumpPhase.Ascent);
    }

    [Fact]
    public void The_reader_switches_the_tiers_on_and_the_m45_cut_off()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f), (0.25f, 13f), (0.4f, 18f)], [(0.1f, 35f), (0.25f, 35f), (0.4f, 55f)],
            100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f));
        var pack = JumpPackTuning.Read("sm_mp_abil_jumppack", doc, [])!;
        Assert.True(pack.HoldTiers);
        Assert.False(pack.ReleaseCut);
    }

    [Fact]
    public void Protocol_38_carries_the_small_jump_s_cut_and_the_config_hash_covers_the_tiers()
    {
        Assert.Equal(46, Protocol.Version);
        var s = Jump(On, 10);
        var p = s.First(x => x.JumpCut) with { PlayerId = 7 };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [p], Self: 7)))).Players[0];
        Assert.Equal((JumpPhase.Ascent, true, p.JumpMark, false), (back.Jump, back.JumpCut, back.JumpMark, back.JumpHolding));
        Assert.NotEqual(Off.Hash(), On.Hash());
        // The next tick from the state read back is the server's next tick.
        var a = p; var b = back with { PlayerId = 7 };
        Step(ref a, InputButtons.None, On); Step(ref b, InputButtons.None, On);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Velocity, b.Velocity);
    }

    [Theory]
    [InlineData(471)]
    [InlineData(472)]
    public void Prediction_matches_the_server_through_taps_medium_and_long_holds_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = On with
        {
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
            JumpPacks = [Tiered with { RechargeRate = 100f }],
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(700 + i), options: new ClientOptions { RequestedClass = Assault });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        int[] holds = [2, 6, 8, 11, 12, 13, 30];
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 23) / 120, phase = (t + (uint)i * 23) % 120;
            int hold = holds[(int)(cycle % (uint)holds.Length)];
            var buttons = phase < hold ? InputButtons.Jump : InputButtons.None;
            return new PlayerInput(t, cycle % 2 == 0 ? 1f : 0f, 1f, 0.3f * i, buttons, -0.6f);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int cuts = mine.Zip(mine.Skip(1)).Count(p => !p.First.JumpCut && p.Second.JumpCut);
            int launches = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.TakeOff && p.Second.Jump == JumpPhase.Ascent);
            int dashes = mine.Zip(mine.Skip(1)).Count(p => p.First.EvadeTicks == 0 && p.Second.EvadeTicks > 0);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {launches} lift-offs, {cuts} small-jump cuts, {dashes} dashes");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(launches >= 4 && cuts >= 2 && dashes >= 1, $"client {i}: {launches} lift-offs, {cuts} cuts, {dashes} dashes");
            Assert.Empty(mismatches);
        }
    }

    [Fact]
    public void The_landing_locks_follow_the_configs_lock_scale()
    {
        // feel/jump-pack-2 (the owner's request, an estimate): SimConfig.LockTime scales the landings' move and jump locks (the install's 0.7).
        var scaled = On with { ActionLockScale = 0.7f };
        foreach (bool moving in new[] { false, true })
        {
            Assert.Equal(JumpPack.JumpLock(Tiered, moving) * 0.7f, JumpPack.JumpLock(Tiered, moving, scaled), 5);
            Assert.Equal(JumpPack.MoveLock(Tiered, moving) * 0.7f, JumpPack.MoveLock(Tiered, moving, scaled), 5);
            Assert.Equal(JumpPack.JumpLock(Tiered, moving), JumpPack.JumpLock(Tiered, moving, On));
        }
        // The first tick after an idle landing a fresh press takes off: earlier with the scale.
        int FirstTakeOff(SimConfig c)
        {
            var landed = Jump(c, 6)[^1];
            Assert.Equal(JumpPhase.Landed, landed.Jump);
            for (int wait = 1; wait < 40; wait++)
            {
                var s = landed;
                for (int k = 1; k < wait; k++) Step(ref s, InputButtons.None, c);
                Step(ref s, InputButtons.Jump, c);
                if (s.Jump == JumpPhase.TakeOff) return wait;
            }
            return -1;
        }
        int data = FirstTakeOff(On), quick = FirstTakeOff(scaled);
        output.WriteLine($"idle landing: a press takes off {data} ticks after it at the data's lock ({JumpPack.JumpLock(Tiered, false):F3} s), {quick} at 0.7 ({JumpPack.JumpLock(Tiered, false, scaled):F3} s)");
        Assert.Equal((int)MathF.Ceiling(JumpPack.JumpLock(Tiered, false) / Dt - 1e-3f), data);
        Assert.Equal((int)MathF.Ceiling(JumpPack.JumpLock(Tiered, false, scaled) / Dt - 1e-3f), quick);
        Assert.True(quick < data);
    }

    /// <summary>
    /// The review (the owner: Space held during a landing should jump): a small jump lands; Space goes down <paramref name="pressAfter"/> ticks after the landing
    /// and stays down for <paramref name="held"/> ticks. Returns the states from the landing on (until the next landing or 120 ticks) and the landing's index.
    /// </summary>
    static List<PlayerState> AfterALanding(SimConfig c, int pressAfter, int held, float my = 0f)
    {
        var s = Fresh(c);
        var states = new List<PlayerState>();
        int landed = -1;
        for (int k = 0; k < 400; k++)
        {
            bool down = k < 6 || (landed >= 0 && k >= landed + pressAfter && k < landed + pressAfter + held);
            Step(ref s, down ? InputButtons.Jump : InputButtons.None, c, landed >= 0 ? my : 0f);
            if (landed < 0 && s.Jump == JumpPhase.Landed) landed = k;
            if (landed >= 0) states.Add(s);
            if (landed >= 0 && (k > landed + 120 || (states.Count > 2 && s.Jump == JumpPhase.Landed && states[^2].Jump != JumpPhase.Landed))) break;
        }
        return states;
    }

    [Theory]
    [InlineData(1, 7)]   // down 7 ticks inside the 9-tick idle landing lock, up 1 tick before it ends: the small jump (before the review: the dash)
    [InlineData(2, 6)]   // down exactly to the decision length (ticks 0..5 of the press): the small jump
    [InlineData(1, 12)]  // still down on the free tick, let go 4 ticks later: committed, the small jump
    [InlineData(1, 30)]  // held on: the full jump
    [InlineData(2, 5)]   // let go on the press's decision tick: a short press, the dash
    [InlineData(3, 1)]   // a one-tick tap: the dash
    public void A_press_held_in_a_landing_lock_replays_as_long_as_it_was_held(int pressAfter, int held)
    {
        var c = On with { InputBufferWindow = 0.3f };
        var states = AfterALanding(c, pressAfter, held);
        var buffered = states.Select(p => p.Buffered).Where(b => b != BufferedAction.None).Distinct().ToList();
        bool dashed = states.Any(p => p.EvadeTicks > 0);
        float apex = states.Any(p => p.Jump == JumpPhase.Ascent) ? states.Max(p => p.Position.Z) : 0f;
        int takeOff = states.FindIndex(p => p.Jump == JumpPhase.TakeOff);
        output.WriteLine($"Space down {pressAfter} ticks after the landing for {held} ticks: buffered {string.Join(", ", buffered)}; take-off {takeOff} ticks after the landing; {(dashed ? "dash" : $"apex {apex:F2} m")}");
        float small = Apex(Jump(On, 6)), full = Apex(Jump(On, 400));
        if (held <= 5) { Assert.True(dashed); Assert.DoesNotContain(BufferedAction.JumpLong, buffered); return; }
        Assert.False(dashed);
        Assert.Contains(BufferedAction.JumpLong, buffered);
        Assert.Equal(held >= 30 ? full : small, apex, 2);
    }

    [Fact]
    public void The_long_press_rides_the_own_record()
    {
        var c = On with { InputBufferWindow = 0.3f };
        var s = AfterALanding(c, 1, 7).First(p => p.Buffered == BufferedAction.JumpLong) with { PlayerId = 7 };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [s], Self: 7)))).Players[0];
        Assert.Equal((BufferedAction.JumpLong, s.BufferTicks), (back.Buffered, back.BufferTicks));
        // A pre-committed take-off (its mark ahead of its ticks) round-trips too.
        var t = AfterALanding(c, 1, 7).First(p => p.Jump == JumpPhase.TakeOff) with { PlayerId = 7 };
        Assert.True(t.JumpMark > t.JumpTicks);
        var tb = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [t], Self: 7)))).Players[0];
        Assert.Equal((t.Jump, t.JumpTicks, t.JumpMark, t.JumpHolding), (tb.Jump, tb.JumpTicks, tb.JumpMark, tb.JumpHolding));
    }

    [Theory]
    [InlineData(481)]
    [InlineData(482)]
    public void Prediction_matches_the_server_through_presses_held_in_landing_locks_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = On with
        {
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
            JumpPacks = [Tiered with { RechargeRate = 100f }], InputBufferWindow = 0.3f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(800 + i), options: new ClientOptions { RequestedClass = Assault });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        // 150-tick cycles: a small jump (Space down 6 ticks), then Space down again for 3, 7 or 12 ticks from somewhere around its landing.
        int[] secondAt = [56, 60, 62, 64, 66], secondFor = [3, 7, 12];   // the small jump lands about 61 ticks after its press
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 31) / 150, phase = (t + (uint)i * 31) % 150;
            int at = secondAt[(int)(cycle % (uint)secondAt.Length)], length = secondFor[(int)(cycle % (uint)secondFor.Length)];
            bool down = phase < 6 || (phase >= at && phase < at + length);
            return new PlayerInput(t, 0f, 0f, 0.3f * i, down ? InputButtons.Jump : InputButtons.None, 0f);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int longs = mine.Zip(mine.Skip(1)).Count(p => p.First.Buffered != BufferedAction.JumpLong && p.Second.Buffered == BufferedAction.JumpLong);
            int committed = mine.Count(p => p.Jump == JumpPhase.TakeOff && p.JumpTicks == 0 && p.JumpMark > 0);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {longs} long presses, {committed} committed take-offs");            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(longs >= 2 && committed >= 2, $"client {i}: {longs} long presses, {committed} committed take-offs");
            Assert.Empty(mismatches);
        }
    }
}
