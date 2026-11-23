using System.Numerics;
using SmContent;
using SmFormats.Havok;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/ledge-charge-air, answer 3 (the owner 2026-11-23: "Keep landings like today"): a Classic ledge charge off a given ledge looks, sounds and feels as at protocol 89 although it falls under 4 x gravity.
/// Engine-free: the simulation's states of a synthetic ledge charge are fed, one per tick, through <see cref="LedgeChargeFallClock"/> and the client's fall-presentation rules as <c>NetworkView.Fall</c> and
/// <c>NetworkView.Sound</c> apply them (the fall pose's delays, <see cref="FallTracker"/>, <see cref="FallRules"/>, <see cref="WeightFeel"/>, the landing sound's air time and speed parameter). Before = the restore
/// switch (the clock is the identity there), after = the default.
/// </summary>
public class LedgeChargeLandingTests(ITestOutputHelper output)
{
    const int Assault = 0, Marine = 1;
    const float HardSpeed = 20f;   // the rifle family's hard-landing speed (FallSet.HardFallSpeed from the graph)

    sealed class Level
    {
        readonly List<Vector3> _v = new();
        readonly List<int> _i = new();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = _v.Count; _v.AddRange([a, b, c, d]); _i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        public Level Box(Vector3 min, Vector3 max)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0)); Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1));
            Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0)); Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)); Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
            return this;
        }
        public CollisionWorld World() { var m = new StaticMesh(_v.ToArray(), _i.ToArray()); return new CollisionWorld(m, m); }
    }

    static CollisionWorld Ledge(float height) => new Level().Box(new(-100, -100, -1), new(100, 300, 0)).Box(new(-100, -100, 0), new(100, 0, height)).World();

    /// <summary>A ledge charge (sprint, then Sprint + F, stick forward until the charge, none after) off <paramref name="height"/> m; <paramref name="press"/> pressed on that air tick; states until the landing.</summary>
    static List<PlayerState> Run(SimConfig c, int cls, float height, InputButtons press = InputButtons.None, int pressAt = 4)
    {
        var world = Ledge(height);
        var s = Combat.Respawned(new PlayerState(1, new Vector3(0f, -9f, height), Vector3.Zero, 0f, true) { Class = cls }, c);
        for (int k = 0; k < 6; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), c, out _, world);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), c, out _, world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        var trace = new List<PlayerState> { s };
        int air = -1;
        bool left = false;
        for (int k = 0; k < 400; k++)
        {
            if (!s.Grounded) air++;
            var b = !s.Grounded && air == pressAt ? press : InputButtons.None;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, press == InputButtons.Jump ? 1f : 0f, 0f, b), c, out _, world);
            trace.Add(s);
            if (!s.Grounded) left = true;
            else if (left) break;
        }
        Assert.True(left && s.Grounded, "left the ledge and landed");
        return trace;
    }

    sealed record Presentation(bool Posed, float PoseDrop, int PoseTick, bool Landed, float Peak, bool Hard, float Settle, float Dip, bool Feel, bool Sound, float SpeedParam, double AirTime);

    /// <summary>The client's fall presentation of a trace, frame = tick (NetworkView.Fall.FallObserve, Landed; NetworkView.Sound's landing).</summary>
    static Presentation Present(List<PlayerState> trace, SimConfig c, Action<string>? log = null)
    {
        var clock = new LedgeChargeFallClock();
        var fall = new FallTracker();
        double chargeAirSince = double.NegativeInfinity;
        bool chargeFall = false, posed = false, landed = false, sound = false;
        int poseTick = -1;
        float poseDrop = float.NaN, top = trace[0].Position.Z, soundAir = 0f, speedParam = 0f;
        Presentation? result = null;
        PlayerState? prev = null;
        for (int k = 0; k < trace.Count; k++)
        {
            var s = trace[k];
            double now = k / 30.0;
            clock.Feed(now, s, c, s.Position.Y > 0.5f ? s.Position.Z : float.NaN);   // the floor beyond the edge is z = 0
            double n = clock.Now;
            log?.Invoke($"k {k} z {s.Position.Z:F2} vz {s.Velocity.Z:F2} g {s.Grounded} rush {s.Rush} now {n:F3} veq {clock.VerticalSpeed:F2}");
            bool chargeHeld = s.Alive && !s.Grounded && s.Rush == RushPhase.Rush && Rush.MoveOf(c, s) is { } move && Rush.Held(s, move, c);
            bool evadeFalling = s.Alive && !s.Grounded && s.Velocity.Z < 0f && Evade.Move(s, c) is { Flight: false } em && !(em.HeightAt(Evade.Time(s, c)) > 0f);
            bool heldInAir = chargeHeld || evadeFalling;
            if (!heldInAir) { chargeAirSince = double.NegativeInfinity; chargeFall = false; }
            else
            {
                if (double.IsNegativeInfinity(chargeAirSince)) chargeAirSince = n;
                if (!chargeFall && (chargeHeld ? FallRules.ChargeShowsFall(n - chargeAirSince) : FallRules.EvadeShowsFall(n - chargeAirSince)))
                {
                    chargeFall = true;
                    if (!posed) { posed = true; poseDrop = top - s.Position.Z; poseTick = k; }
                }
            }
            bool eligible = s.Alive && s.Jump == JumpPhase.None && (s.Rush == RushPhase.None || chargeFall) && s.Reaction == ReactionKind.None && (!Evade.Evading(s) || chargeFall);
            var seen = fall.Observe(n, eligible, s.Grounded, clock.VerticalSpeed, immediate: chargeFall);
            if (seen == FallEvent.Began && !posed && !s.Grounded) { posed = true; poseDrop = top - s.Position.Z; poseTick = k; }
            // the landing sound (NetworkView.Sound: a fall without the pack, after 0.4 s in the air) and its speed parameter
            float param = Math.Max(clock.Speed, clock.LastSpeed);
            if (prev is { } p && s.Grounded && !p.Grounded) { sound = soundAir + Math.Max(0f, (float)clock.Step - 1f / 30f) > FallRules.LandingFeelSeconds; speedParam = param; }   // as NetworkView.Sound
            soundAir = s.Grounded ? 0f : Math.Max(0f, soundAir + (float)clock.Step);
            if (seen == FallEvent.Landed)
            {
                landed = true;
                float peak = fall.PeakDown;
                bool hard = FallRules.IsHard(peak, HardSpeed);
                result = new Presentation(posed, poseDrop, poseTick, true, peak, hard, hard ? 0f : WeightFeel.SettleWeight(peak), WeightFeel.LandingDipDegrees(peak), fall.LastAirTime >= FallRules.LandingFeelSeconds, false, 0f, fall.LastAirTime);
            }
            prev = s;
        }
        return (result ?? new Presentation(posed, poseDrop, poseTick, landed, 0f, false, 0f, 0f, false, false, 0f, 0.0)) with { Sound = sound, SpeedParam = speedParam };
    }

    void Same(string name, Presentation before, Presentation after, float poseTolerance)
    {
        output.WriteLine($"{name}: before {before}");
        output.WriteLine($"{name}: after  {after}");
        Assert.Equal(before.Posed, after.Posed);
        if (before.Posed) Assert.InRange(after.PoseDrop, before.PoseDrop - poseTolerance, before.PoseDrop + poseTolerance);
        Assert.Equal(before.Landed, after.Landed);
        Assert.Equal(before.Hard, after.Hard);
        Assert.Equal(before.Feel, after.Feel);
        Assert.Equal(before.Sound, after.Sound);
        Assert.InRange(after.Settle, before.Settle - 0.06f, before.Settle + 0.06f);
        Assert.InRange(after.Dip, before.Dip - 0.1f, before.Dip + 0.1f);
        Assert.InRange(after.Peak, before.Peak - 1.5f, before.Peak + 1.5f);
        Assert.InRange(after.SpeedParam, before.SpeedParam - 1.5f, before.SpeedParam + 1.5f);
        Assert.InRange(after.AirTime, before.AirTime - 0.1, before.AirTime + 0.1);
    }

    static SimConfig After => JumpPackAttackTests.Config;
    static SimConfig Before => After with { LedgeChargeHalfAir = false };

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.4f)]
    [InlineData(2.5f)]   // (2.0 m sits on WeightFeel's 8 m/s settle threshold: 7.999999 against 8.0 m/s, a float tie)
    [InlineData(4f)]
    [InlineData(8f)]
    [InlineData(12f)]
    [InlineData(16f)]
    public void A_ledge_charge_lands_with_the_protocol_89_pose_hard_landing_sound_and_feel_for_its_drop(float height)
    {
        var before = Present(Run(Before, Marine, height), Before);
        var after = Present(Run(After, Marine, height), After);
        // the pose begins at the same drop (to one 30 Hz tick of the faster fall: up to about 1 m at the end of a 16 m charge)
        Same($"{height} m charge", before, after, poseTolerance: 0.35f + 0.08f * height);
    }

    [Fact]
    public void The_fall_pose_starts_at_the_drop_the_0_4_s_delay_gave()
    {
        // protocol 89: 0.4 s of free fall at 20 m/s^2 is 1.6 m
        var after = Present(Run(After, Marine, 8f), After);
        Assert.True(after.Posed);
        Assert.InRange(after.PoseDrop, 1.3f, 2.0f);
        Assert.True(after.Hard == Present(Run(Before, Marine, 8f), Before).Hard);
    }

    [Theory]
    [InlineData(Assault, 12f)]
    [InlineData(Marine, 12f)]
    [InlineData(Marine, 4f)]
    public void A_light_melee_out_of_a_ledge_charge_lands_as_today(int cls, float height)
    {
        var c = cls == Assault ? MeleeCancelTests.Pack : After;
        var before = Present(Run(c with { LedgeChargeHalfAir = false }, cls, height, InputButtons.Melee), c with { LedgeChargeHalfAir = false });
        var after = Present(Run(c, cls, height, InputButtons.Melee), c, height == 4f ? output.WriteLine : null);
        // The press comes lower than at protocol 89 (the faster fall before it, by design), so the pose (at the press plus the tracker's debounce, the same tick) is compared by time, not by drop.
        Same($"light melee, class {cls}, {height} m", before with { PoseDrop = 0f }, after with { PoseDrop = 0f }, poseTolerance: 0f);
        Assert.Equal(before.PoseTick, after.PoseTick);
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(4f)]
    public void A_hop_out_of_a_ledge_charge_lands_as_today(float height)
    {
        var c = MeleeCancelTests.Pack;
        var before = Present(Run(c with { LedgeChargeHalfAir = false }, Assault, height, InputButtons.Jump), c with { LedgeChargeHalfAir = false });
        var after = Present(Run(c, Assault, height, InputButtons.Jump), c);
        Same($"hop, {height} m", before, after, poseTolerance: 0.35f + 0.08f * height);
    }

    [Fact]
    public void Outside_a_classic_ledge_charge_the_clock_is_the_render_clock()
    {
        foreach (var c in new[] { Before, After with { Extras = ContentExtras.Musou | ContentExtras.Warfront } })
        {
            var clock = new LedgeChargeFallClock();
            var trace = Run(c, Marine, 8f);
            for (int k = 0; k < trace.Count; k++)
            {
                clock.Feed(k / 30.0, trace[k], c);
                Assert.Equal(k / 30.0, clock.Now, 9);
                Assert.Equal(trace[k].Velocity.Z, clock.VerticalSpeed);
            }
        }
    }

    [Fact]
    public void The_clock_constants_are_the_fall_trackers()
    {
        Assert.Equal(FallRules.DebounceSeconds, LedgeChargeFallClock.FallDebounceSeconds);
        Assert.Equal(FallTracker.GapSeconds, LedgeChargeFallClock.GapSeconds);
        Assert.True(LedgeChargeFallClock.MaxStep < FallTracker.GapSeconds);
        Assert.True(LedgeChargeFallClock.MaxHitch > FallTracker.GapSeconds);
    }

    [Theory]
    [InlineData(5f, 1)]    // the ray saw ground 5 m below the real one (the body lands on a crate)
    [InlineData(-1.5f, 1)] // or 1.5 m above it (a slope the ray missed)
    [InlineData(5f, 2)]    // at 15 fps (every second tick drawn)
    [InlineData(-1.5f, 2)]
    public void A_light_melee_landing_on_other_ground_than_the_ray_saw_still_lands_with_no_gap(float rayError, int every)
    {
        // review (Fable High, 2026-11-23): the touchdown jump is kept under FallTracker.GapSeconds, so the tracker still sees a landing (clip, settle, dip, feel) and the sound no huge air time.
        var c = After;
        var trace = Run(c, Marine, 8f, InputButtons.Melee);
        var clock = new LedgeChargeFallClock();
        var fall = new FallTracker();
        bool landed = false;
        double worst = 0, realAir = 0;
        float soundAir = 0f, heard = -1f;
        PlayerState? prev = null;
        var frames = Enumerable.Range(0, trace.Count).Where(k => k % every == 0 || k == trace.Count - 1).ToList();
        foreach (int k in frames)
        {
            var s = trace[k];
            double now = k / 30.0;
            clock.Feed(now, s, c, s.Position.Y > 0.5f ? MathF.Max(0f, s.Position.Z + rayError) : float.NaN);
            worst = Math.Max(worst, clock.Step);
            bool eligible = s.Alive && s.Jump == JumpPhase.None && s.Rush == RushPhase.None && s.Reaction == ReactionKind.None;
            if (fall.Observe(clock.Now, eligible, s.Grounded, clock.VerticalSpeed) == FallEvent.Landed) landed = true;
            // the landing sound's air time as NetworkView.Sound counts it
            float delta = every / 30f;
            if (prev is { } p && s.Grounded && !p.Grounded) heard = soundAir + Math.Max(0f, (float)clock.Step - delta);
            if (prev is { Grounded: false }) realAir += delta;
            soundAir = s.Grounded ? 0f : Math.Max(0f, soundAir + (float)clock.Step);
            prev = s;
        }
        output.WriteLine($"ray error {rayError} m, every {every} tick(s): largest step {worst:F3} s; landed {landed}; the sound's air time at touchdown {heard:F3} s (real {realAir:F3} s)");
        Assert.True(landed);
        Assert.True(worst <= Math.Max(every / 30.0, LedgeChargeFallClock.MaxStep) + 1e-9, $"step {worst}");
        // an 8 m fall: the landing sound plays (over 0.4 s), with an air time near protocol 89's (no gap fed into it)
        Assert.True(heard > FallRules.LandingFeelSeconds, $"heard {heard}");
        Assert.InRange(heard, realAir * 0.5, realAir * 2.0 + 0.3);
    }

    [Fact]
    public void A_fall_with_one_hitch_frame_keeps_the_landing_sounds_air_time_outside_a_ledge_charge()
    {
        // review C: outside a Classic ledge charge the sound's air time is the sum of the frame times, a hitch (one frame over the tracker's 0.25 s gap) included, as before this branch
        foreach (var c in new[] { Before, After with { Extras = ContentExtras.Musou | ContentExtras.Warfront } })
        {
            var trace = Run(c, Marine, 8f);
            int air = trace.FindIndex(p => !p.Grounded);
            var clock = new LedgeChargeFallClock();
            float soundAir = 0f, heard = -1f, real = 0f;
            PlayerState? prev = null;
            double now = 0, last = 0;
            for (int k = 0; k < trace.Count; k++)
            {
                now = k / 30.0 + (k > air + 5 ? 0.3 : 0.0);   // one 0.33 s frame five ticks into the fall
                float delta = (float)(now - last); last = now;
                var s = trace[k];
                clock.Feed(now, s, c);
                if (prev is { } p && s.Grounded && !p.Grounded) heard = soundAir + Math.Max(0f, (float)clock.Step - delta);
                if (prev is { Grounded: false } && k > 0) real += delta;
                soundAir = s.Grounded ? 0f : Math.Max(0f, soundAir + (float)clock.Step);
                prev = s;
            }
            output.WriteLine($"{(c.LedgeChargeHalfAir ? "warfront" : "restore switch")}: the sound's air time {heard:F3} s, the frames' {real:F3} s");
            Assert.True(heard > 0f, "the landing was heard");
            Assert.Equal(real, heard, 3);   // the first air frame's time counted, the touchdown frame's not: the same count
        }
    }

    [Fact]
    public void A_clock_first_fed_mid_charge_departs_from_where_it_was_first_seen()
    {
        // review B: a new or reset clock keeps no height from another time (a remote player first drawn mid ledge charge)
        var c = After;
        var other = Run(c, Marine, 12f);
        var trace = Run(c, Marine, 4f);
        int from = trace.FindIndex(p => !p.Grounded) + 3;
        var stale = new LedgeChargeFallClock();
        for (int k = 0; k < other.Count; k++) stale.Feed(k / 30.0, other[k] with { Position = other[k].Position + new Vector3(40f, 0f, 20f) }, c);
        var fresh = new LedgeChargeFallClock();
        double t0 = 100.0;
        for (int k = from; k < trace.Count; k++)
        {
            double now = t0 + (k - from) / 30.0;
            stale.Feed(now, trace[k], c);
            fresh.Feed(now, trace[k], c);
            Assert.Equal(fresh.Now, stale.Now, 9);
            Assert.Equal(fresh.VerticalSpeed, stale.VerticalSpeed);
        }
        // and the landing jump is bounded by where it was first seen (not by the 12 m ledge 20 m higher)
        Assert.True(fresh.Step <= LedgeChargeFallClock.MaxStep + 1e-9);
    }

    [Fact]
    public void A_stale_clock_starts_afresh_with_no_step_over_the_gap()
    {
        // review: a player not drawn for a while (or a returning id) gets no frame whose Step is the whole gap
        var c = After;
        var trace = Run(c, Marine, 8f);
        var clock = new LedgeChargeFallClock();
        int air = trace.FindIndex(p => !p.Grounded) + 4;
        for (int k = 0; k <= air; k++) clock.Feed(k / 30.0, trace[k], c);
        Assert.True(clock.Mapping);
        Assert.True(clock.Now > air / 30.0, "the held charge's clock ran ahead");
        double later = air / 30.0 + 5.0;
        clock.Feed(later, trace[^1], c);
        Assert.Equal(0.0, clock.Step);
        Assert.Equal(later, clock.Now, 9);
        Assert.False(clock.Mapping);
        Assert.True(clock.Fed(later));
        Assert.False(clock.Fed(later + 1.0 / 30.0));
    }

}
