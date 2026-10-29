using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/melee-charge-2 (protocol 42; the owner's requests of 2026-10-29): the heavy (F) combo's Y,Y chain (backhand → stomp), the longer melee lunge
/// (<see cref="SimConfig.MeleeLungeScale"/>), the faster weapon switch (an estimate), the eased alignment turn and the lock's hysteresis
/// (<see cref="MeleeLockSpec.AlignEase"/>, <see cref="MeleeLockSpec.Hysteresis"/>), and prediction against the server under 150 ms RTT and 5 % loss.
/// The ledge charge's landing is in <see cref="JumpPackAttackTests"/>. Synthetic specs; the install part prints the before / after numbers and is
/// skipped without an install.
/// </summary>
public class MeleeCharge2Tests(ITestOutputHelper output)
{
    static float T(float ticks) => ticks / 30f;
    const float Deg = MathF.PI / 180f;

    // ---------------------------------------------------------------- the Y,Y chain

    /// <summary>A heavy strike shaped like the knife's: 1 s long, hit at 0.3 s, moves again at 0.7 s; a Y window 0.45..0.7 s to strike <paramref name="yNext"/> (0: none).</summary>
    static MeleeStep Heavy(int yNext, float damage = 10f) => new MeleeStep(1.0f, 0.3f, 0.4f, 0.6f, 0.5f, 0.7f, damage, damage / 2f, 0f)
    { YNext = yNext, YComboOpen = yNext > 0 ? 0.45f : 0f, YComboClose = yNext > 0 ? 0.7f : 0f };
    static readonly MeleeStep Light = new(1.0f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);

    /// <summary>The knife's shape after this branch: backhand, kick and shoulder chain to the stomp (strike 4); the stomp chains to nothing.</summary>
    static readonly SimConfig YConfig = new()
    {
        Classes = [new ClassLoadout("tactical", -1, -1, [Light, Light, Light]) { MeleeHeavy = [Heavy(4), Heavy(4), Heavy(4), Heavy(0, 30f)], MeleeRush = RushTests.Spec }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);

    /// <summary>Presses F on tick 0 and again on tick <paramref name="second"/> (-1: never), holding <paramref name="moveY"/>; the (step, heavy) of every tick and the heavy hits.</summary>
    static (List<(int Step, bool Heavy)> Steps, List<(int Tick, int Step)> Hits) FF(SimConfig c, int second, float moveY = 0f, int ticks = 70)
    {
        var s = Fresh(c);
        var steps = new List<(int, bool)>();
        var hits = new List<(int, int)>();
        for (int k = 0; k < ticks; k++)
        {
            var b = k == 0 || k == second ? InputButtons.MeleeHeavy : InputButtons.None;
            s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, moveY, 0f, b), c, out var e);
            steps.Add((s.MeleeStep, s.MeleeHeavy));
            if (e.MeleeHitStep > 0 && e.MeleeHeavy) hits.Add((k, e.MeleeHitStep));
        }
        return (steps, hits);
    }

    [Theory]
    [InlineData(5)]    // before the backhand's hit: buffered, chains when the window opens
    [InlineData(14)]   // inside the window (0.45..0.7 s = ticks 13.5..21)
    [InlineData(20)]   // its last tick
    public void A_second_F_in_the_backhands_window_stomps(int second)
    {
        var (steps, hits) = FF(YConfig, second);
        output.WriteLine($"second F at tick {second}: heavy hits {string.Join(", ", hits.Select(h => $"strike {h.Step} at tick {h.Tick}"))}");
        Assert.Equal((1, true), steps[0]);                                    // F: the backhand (no W: not the charge)
        Assert.Contains(steps, x => x == (4, true));                          // F, F: the stomp
        Assert.Equal([1, 4], hits.Select(h => h.Step));                       // the backhand's hit, then the stomp's
        int stompAt = steps.IndexOf((4, true));
        Assert.InRange(stompAt, 14, 21);                                      // from the window's opening (and after the hit)
    }

    [Fact]
    public void A_second_F_after_the_window_or_during_the_stomp_does_not_chain()
    {
        var late = FF(YConfig, 23);   // after the window's close (tick 21): nothing chains (no input buffer in this config); the backhand runs out
        Assert.DoesNotContain(late.Steps, x => x == (4, true));
        // F, F, F: the stomp chains to nothing.
        var s = Fresh(YConfig);
        var heavyKinds = new HashSet<int>();
        for (int k = 0; k < 90; k++)
        {
            var b = k is 0 or 15 or 30 ? InputButtons.MeleeHeavy : InputButtons.None;
            s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, 0f, 0f, b), YConfig, out _);
            if (s.MeleeHeavy) heavyKinds.Add(s.MeleeStep);
        }
        Assert.Equal([1, 4], heavyKinds.Order());
    }

    [Fact]
    public void With_W_held_the_second_F_still_stomps_and_does_not_charge()
    {
        // The first F with W held is the charge (protocol 39); from rest without W the backhand. W pressed during the backhand: the second F stomps.
        var s = Fresh(YConfig);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.MeleeHeavy), YConfig, out _);
        Assert.Equal((1, true, RushPhase.None), (s.MeleeStep, s.MeleeHeavy, s.Rush));
        for (int k = 1; k < 25; k++)
            s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, 1f, 0f, k == 15 ? InputButtons.MeleeHeavy : InputButtons.None), YConfig, out _);
        Assert.Equal(RushPhase.None, s.Rush);
        Assert.Equal((4, true), (s.MeleeStep, s.MeleeHeavy));
    }

    [Fact]
    public void The_input_buffer_keeps_a_second_F_pressed_just_after_the_window()
    {
        // With the owner's 0.3 s input buffer a press after the Y window is replayed once the backhand ends: a new backhand, not the stomp (the window is the data's).
        var c = YConfig with { InputBufferWindow = 0.3f };
        var (steps, _) = FF(c, 23);
        Assert.DoesNotContain(steps, x => x == (4, true));
        var (inWindow, _) = FF(c, 16);
        Assert.Contains(inWindow, x => x == (4, true));
    }

    // ---------------------------------------------------------------- the lunge

    static MeleeVariant Variant(float distance, float duration, float hit) => new(distance, Enumerable.Range(0, 61).Select(i =>
    {
        float t = duration * i / 60f;
        return MathF.Max(0f, distance - 1.5f) * Math.Clamp(t / hit, 0f, 1f) + 0.6f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
    }).ToArray());

    /// <summary>MeleeLockTests' attack: hit at tick 7, moves again at 22, over at 31; alignment over ticks 0..3; variants 1, 3.5, 7 m; start blend 1.</summary>
    static MeleeStep Reaching(float damage = 0f, float alignTicks = 3f)
    {
        float duration = T(30.5f), hit = T(6.5f);
        return new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), damage, damage / 2f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(alignTicks), 0f, hit, 3.5f, 1f, [Variant(1f, duration, hit), Variant(3.5f, duration, hit), Variant(7f, duration, hit)]),
        };
    }

    static SimConfig LungeConfig(float scale) => new()
    {
        MeleeSteps = [Reaching(), Reaching(), Reaching()], MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, MeleeLungeScale = scale,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>One attack from rest at the origin facing +Y, with an optional locked target; the states of every tick.</summary>
    static List<PlayerState> Swing(SimConfig c, Vector3? target, int ticks = 22, float targetYaw = 0f)
    {
        var self = Fresh(c);
        var view = target is { } t ? new Dictionary<int, PlayerState> { [2] = Combat.Respawned(new PlayerState(2, t, Vector3.Zero, targetYaw, true), c) } : null;
        var states = new List<PlayerState>();
        int locked = 0;
        for (int i = 0; i < ticks; i++)
        {
            var input = new PlayerInput((uint)i, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None);
            if (view is not null) { input = input with { MeleeTarget = MeleeLock.Choose(self, input, view, c, null, locked) }; locked = input.MeleeTarget; }
            self = Combat.StepPlayer(self, input, c, out _, null, view);
            states.Add(self);
        }
        return states;
    }

    [Fact]
    public void Without_a_target_the_swing_steps_forward_by_the_lunge_scale()
    {
        float before = Swing(LungeConfig(1f), null)[^1].Position.Y, after = Swing(LungeConfig(1.3f), null)[^1].Position.Y;
        output.WriteLine($"no target, to MoveAt: {before:F3} m → {after:F3} m");
        Assert.True(before > 0.3f);
        Assert.Equal(1.3f * before, after, 3);
    }

    [Theory]
    [InlineData(3.5f)]
    [InlineData(5f)]
    public void With_a_target_the_swing_closes_further_but_never_within_the_separation(float distance)
    {
        var target = new Vector3(0f, distance, 0f);
        var before = Swing(LungeConfig(1f), target, 31);
        var after = Swing(LungeConfig(1.3f), target, 31);
        float separation = LungeConfig(1.3f).MeleeLock.Separation;
        float gapBefore = distance - before[6].Position.Y, gapAfter = distance - after[6].Position.Y;   // at the hit (tick 7)
        output.WriteLine($"target {distance} m: gap at the hit {gapBefore:F3} → {gapAfter:F3} m; moved by MoveAt {before[20].Position.Y:F3} → {after[20].Position.Y:F3} m");
        Assert.True(gapAfter <= gapBefore + 1e-3f, "the scaled approach stops short");
        // The approach still aims at the strike distance; the extra shows in the follow-through, up to the separation.
        Assert.True(after[20].Position.Y > before[20].Position.Y + 0.05f || distance - after[20].Position.Y <= separation + 0.01f, "no further step");
        Assert.All(after, s => Assert.True(distance - s.Position.Y >= separation - 1e-3f, $"within the separation at {s.Position.Y}"));
    }

    [Fact]
    public void Backward_motion_and_the_charge_are_not_scaled()
    {
        // A clip that steps 1 m back: the same with any scale.
        float duration = T(30.5f);
        var back = new MeleeStep(duration, T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 0f, 0f, 0f)
        { Reach = new MeleeReach(0f, T(3f), 0f, T(6.5f), 0f, 0f, [new MeleeVariant(0f, Enumerable.Range(0, 61).Select(i => -1f * i / 60f).ToArray())]) };
        float y1 = Swing(LungeConfig(1f) with { MeleeSteps = [back] }, null)[^1].Position.Y, y2 = Swing(LungeConfig(1.3f) with { MeleeSteps = [back] }, null)[^1].Position.Y;
        Assert.True(y1 < -0.3f);
        Assert.Equal(y1, y2, 4);
        // The charge: RushTests' clip moves the same either way.
        var c1 = LungeConfig(1f) with { Classes = [new ClassLoadout("c", -1, -1, [Reaching()]) { MeleeRush = RushTests.Spec }] };
        var c2 = c1 with { MeleeLungeScale = 1.3f };
        PlayerState Charge(SimConfig c)
        {
            var s = Fresh(c);
            for (int k = 0; k < 12; k++) s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, k == 0 ? 1f : 0f, 0f, k == 0 ? InputButtons.MeleeHeavy : InputButtons.None), c, out _);
            return s;
        }
        Assert.Equal(Charge(c1).Position.Y, Charge(c2).Position.Y, 4);
        // The hash covers the scale.
        Assert.NotEqual(c1.Hash(), c2.Hash());
        Assert.Equal(new SimConfig().Hash(), (new SimConfig() with { MeleeLungeScale = 1f }).Hash());
    }

    // ---------------------------------------------------------------- the weapon switch

    static WeaponSpec Gun(string name, WeaponSlot slot = WeaponSlot.Ranged) =>
        new(name, slot, TriggerKind.SemiAuto, 10f, 0.1f, 30, 90, 1f, 250f, 245f, 250f, 1f, 1f, 1f, 1f, 1f);

    [Theory]
    [InlineData(0.6f, 18)]    // the placeholder (ownerFeel off)
    [InlineData(0.48f, 15)]   // × 0.8, the owner's estimate
    public void The_switch_takes_its_time_and_the_raise_after_it_still_applies(float switchTime, int ticks)
    {
        var c = new SimConfig
        {
            Weapons = [Gun("rifle") with { RaiseDelay = 0.2f }, Gun("pistol", WeaponSlot.Pistol) with { RaiseDelay = 1f / 6f }],
            Classes = [new ClassLoadout("t", 0, 1, SimConfig.PlaceholderMeleeSteps) { Evade = -1 }],
            WeaponSwitchTime = switchTime,
        };
        var s = Fresh(c);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Switch), c, out _);
        int done = 0;
        for (int k = 1; k < 40 && s.SwitchLeft > 0f; k++) { s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, 0f, 0f, InputButtons.None), c, out _); done = k; }
        Assert.Equal(ticks, done);
        // Fire right after the switch: the pistol waits for its raise (5 ticks for 1/6 s; M32's rule kept).
        int shot = -1;
        for (int k = 0; k < 20 && shot < 0; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput((uint)(100 + k), 0f, 0f, 0f, k == 0 ? InputButtons.Fire : InputButtons.None), c, out var e);
            if (e.Fired) shot = k;
        }
        output.WriteLine($"switch {switchTime} s: done after {done} ticks; first shot {shot} ticks later");
        Assert.Equal(5, shot);
    }

    // ---------------------------------------------------------------- the eased alignment and the hysteresis

    /// <summary>
    /// Yaw per tick (degrees) of an attack aligning to a target <paramref name="degrees"/> off the view over a window of <paramref name="alignTicks"/> ticks, and
    /// how far (degrees) the heading is off the target as seen from where the attacker stands at the window's end.
    /// </summary>
    static (List<float> Yaws, float OffAtEnd) AlignYaws(bool ease, float degrees, float alignTicks)
    {
        var c = LungeConfig(1f) with { MeleeSteps = [Reaching(0f, alignTicks)], MeleeLock = MeleeLockSpec.Default with { AlignEase = ease } };
        float a = degrees * Deg;
        var target = new Vector3(-MathF.Sin(a) * 3f, MathF.Cos(a) * 3f, 0f);
        var states = Swing(c, target, (int)alignTicks + 4);
        var end = states[(int)alignTicks];
        float off = MeleeLock.Delta(end.Yaw, MeleeLock.YawOf(new Vector2(target.X - end.Position.X, target.Y - end.Position.Y))) / Deg;
        return (states.Select(s => s.Yaw / Deg).ToList(), off);
    }

    /// <summary>The largest change of the turn rate from one tick to the next (degrees a second per tick), the rate starting and ending at rest.</summary>
    static float Jerk(List<float> yaws)
    {
        var rates = new List<float> { 0f };
        for (int i = 1; i < yaws.Count; i++) rates.Add((yaws[i] - yaws[i - 1]) * 30f);
        rates.Add(0f);
        return rates.Zip(rates.Skip(1)).Max(p => MathF.Abs(p.Second - p.First));
    }

    [Theory]
    [InlineData(40f, 6f)]
    [InlineData(40f, 8f)]
    [InlineData(25f, 8f)]
    public void The_eased_alignment_reaches_the_target_with_less_jerk(float degrees, float alignTicks)
    {
        var (before, offBefore) = AlignYaws(false, degrees, alignTicks);
        var (after, offAfter) = AlignYaws(true, degrees, alignTicks);
        float jb = Jerk(before), ja = Jerk(after);
        output.WriteLine($"{degrees}° over {alignTicks} ticks: before {string.Join(" ", before.Select(y => y.ToString("F1")))}; jerk {jb:F0} °/s per tick; off the target at the end {offBefore:F2}°");
        output.WriteLine($"{"",-22} after  {string.Join(" ", after.Select(y => y.ToString("F1")))}; jerk {ja:F0} °/s per tick; off the target at the end {offAfter:F2}°");
        Assert.True(MathF.Abs(offBefore) < 0.5f && MathF.Abs(offAfter) < 0.5f, "not facing the target by the window's end");   // both face it (the lunge moves the attacker: re-planned every tick)
        Assert.True(ja < jb * 0.8f, $"jerk {ja} not below {jb}");
        // The first turning tick is gentler than the constant rate's.
        Assert.True(MathF.Abs(after[1]) < MathF.Abs(before[1]));
    }

    [Fact]
    public void A_short_alignment_window_is_widened_to_the_minimum_span_up_to_the_hit_then_earlier()
    {
        var c = LungeConfig(1f);   // MinAlignSpan 0.2 s
        // The test attack's hit is at tick 6.5 (0.217 s); a 3-tick window from 0: widened to 0.2 s (it reaches the hit's side first).
        Assert.Equal((0f, 0.2f), MeleeLock.AlignWindow(Reaching(0f, 3f), c));
        // A 2-tick window late in the attack (like the knife's kick): its end moves to the hit, then its start back to keep 0.2 s.
        var late = Reaching() with { HitTime = 0.30f, Reach = Reaching().Reach! with { AlignFrom = 0.18f, AlignTo = 0.24f } };
        var (open, close) = MeleeLock.AlignWindow(late, c);
        Assert.Equal(0.30f, close, 4);
        Assert.Equal(0.10f, open, 4);
        // Long enough already, or the M32 rule: unchanged.
        Assert.Equal((0f, T(8f)), MeleeLock.AlignWindow(Reaching(0f, 8f), c));
        Assert.Equal((0.18f, 0.24f), MeleeLock.AlignWindow(late, c with { MeleeLock = c.MeleeLock with { AlignEase = false } }));
    }

    [Fact]
    public void Without_a_target_the_attack_eases_toward_the_view_instead_of_snapping()
    {
        var c = LungeConfig(1f) with { MeleeSteps = [Reaching(0f, 6f)] };
        var s = Fresh(c);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0f, InputButtons.Melee), c, out _);
        var yaws = new List<float>();
        for (int k = 1; k < 9; k++) { s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0, 0, 0.6f, InputButtons.None), c, out _); yaws.Add(s.Yaw); }
        output.WriteLine("toward a view turned 34°: " + string.Join(" ", yaws.Select(y => (y / Deg).ToString("F1"))));
        Assert.True(yaws[0] < 0.3f);                     // not at once
        Assert.Equal(0.6f, yaws[5], 3);                  // by the window's end (tick 6)
        Assert.True(yaws.Zip(yaws.Skip(1)).All(p => p.Second >= p.First - 1e-5f));
    }

    [Fact]
    public void A_held_lock_has_hysteresis_a_fresh_search_does_not()
    {
        var c = LungeConfig(1f);
        var self = Fresh(c) with { MeleeStep = 1, MeleeTime = 0.5f };
        // 3 m away, 62° off the heading: 47.7° from the cone's apex 1 m behind: outside the 45° cone, inside 45 + 15.
        float a = 62f * Deg;
        var view = new Dictionary<int, PlayerState> { [2] = Combat.Respawned(new PlayerState(2, new Vector3(-MathF.Sin(a) * 3f, MathF.Cos(a) * 3f, 0f), Vector3.Zero, 0f, true), c) };
        var none = new PlayerInput(0, 0, 0, 0f, InputButtons.None);
        Assert.Equal(0, MeleeLock.Acquire(self, MeleeLock.Forward(0f), view, c, null, c.MeleeLock.Range, c.MeleeLock.HalfAngle));   // a fresh search: no
        Assert.Equal(2, MeleeLock.Choose(self, none, view, c, null, previous: 2));                                                   // held: kept
        Assert.Equal(0, MeleeLock.Choose(self, none, view, c with { MeleeLock = c.MeleeLock with { Hysteresis = 0f } }, null, previous: 2));   // M32: lost
        // At a new attack's start the remembered lock is offered as the previous one and kept in the same wider cone.
        var rest = Fresh(c);
        Assert.Equal(2, MeleeLock.Choose(rest, none with { Buttons = InputButtons.Melee }, view, c, null, previous: 2));
        Assert.Equal(0, MeleeLock.Choose(rest, none with { Buttons = InputButtons.Melee }, view, c, null, previous: 0));
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    public void Prediction_is_exact_through_F_F_chains_scaled_lunges_eased_turns_and_quick_switches_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // Two players close in and fight: light presses, F,F chains, and switches; the lunge scale 1.3, the switch 0.48 s, the eased lock. No damage (not predicted).
        MeleeStep R(MeleeStep m) => m with { Damage = 0f, SecondaryDamage = 0f };
        var reach = Reaching();
        var heavy = new[] { 4, 4, 4, 0 }.Select(n => R(reach with { YNext = n, YComboOpen = n > 0 ? T(13f) : 0f, YComboClose = n > 0 ? T(20f) : 0f })).ToArray();
        var config = LungeConfig(1.3f) with
        {
            Weapons = [Gun("rifle") with { Damage = 0f, RaiseDelay = 0.2f }, Gun("pistol", WeaponSlot.Pistol) with { Damage = 0f, RaiseDelay = 1f / 6f }],
            Classes = [new ClassLoadout("t", 0, 1, [R(reach), R(reach), R(reach)]) { MeleeHeavy = heavy, Evade = -1 }],
            WeaponSwitchTime = 0.48f, InputBufferWindow = 0.3f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 2, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var c = h.Clients[i];
            var me = c.Predicted;
            var other = c.RemotePlayers().Players.FirstOrDefault();
            var to = new Vector2(other.Position.X - me.Position.X, other.Position.Y - me.Position.Y);
            float yaw = to.LengthSquared() > 1e-4f ? MeleeLock.YawOf(to) + 0.35f * MathF.Sin(t * 0.07f + i) : 0f;
            float distance = to.Length();
            float moveY = distance > 6f ? 1f : distance < 2f ? -1f : 0f;
            float moveX = MathF.Sin(t * 0.05f + i * 2f) * 0.8f;
            uint phase = (t + (uint)i * 7) % 120;
            var b = InputButtons.None;
            if (phase is 0 or 16) b = InputButtons.MeleeHeavy;               // F, F
            else if (phase is 50 or 62 or 74) b = InputButtons.Melee;        // light presses
            else if (phase == 100) b = InputButtons.Switch;
            if (b == InputButtons.MeleeHeavy) moveY = 0f;                      // F without W: the strike, not the charge
            return new PlayerInput(t, moveX, moveY, yaw, b, 0.1f);
        }
        h.Run(TimeSpan.FromSeconds(24), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int stomps = mine.Zip(mine.Skip(1)).Count(p => p.Second.MeleeHeavy && p.Second.MeleeStep == 4 && !(p.First.MeleeHeavy && p.First.MeleeStep == 4));
            int switches = mine.Zip(mine.Skip(1)).Count(p => p.Second.Slot != p.First.Slot);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; F,F stomps {stomps}, switches {switches}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(stomps >= 4, $"client {i}: only {stomps} stomps");
            Assert.True(switches >= 2, $"client {i}: only {switches} switches");
            Assert.Empty(mismatches);
        }
    }

    // ---------------------------------------------------------------- the install (read-only; skipped without it)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    /// <summary>
    /// The install with the owner's feel: the lunge scale, the switch time, the eased lock, the knife's and chainsword's F, F → stomp; and the before / after
    /// numbers (spec/behavior.md "Melee and charge, second pass"): each marine melee attack's forward travel to its MoveAt without a target, and the alignment's jerk.
    /// </summary>
    [InstallFact]
    public void The_install_loads_the_changes_and_prints_the_numbers()
    {
        var tuning = GameplayTuning.Load(Game.Value!);
        var c = tuning.Config;
        var data = GameplayTuning.Load(Game.Value!, ownerFeel: false).Config;
        Assert.Equal((GameplayTuning.OwnerLungeScale, 1f), (c.MeleeLungeScale, data.MeleeLungeScale));
        Assert.Equal(0.6f * GameplayTuning.OwnerSwitchScale, c.WeaponSwitchTime, 4);
        Assert.Equal(0.6f, data.WeaponSwitchTime, 4);
        Assert.True(c.MeleeLock.AlignEase && c.MeleeLock.Hysteresis == 15f);
        output.WriteLine($"weapon switch {data.WeaponSwitchTime:0.000} s → {c.WeaponSwitchTime:0.000} s ({MathF.Ceiling(data.WeaponSwitchTime * 30f - 1e-3f)} → {MathF.Ceiling(c.WeaponSwitchTime * 30f - 1e-3f)} ticks)");
        output.WriteLine("| attack | forward to MoveAt, no target, before (m) | after (m) |");
        var seen = new HashSet<string>();
        foreach (var k in c.Classes)
            foreach (var combo in k.MeleeChoices)
            {
                if (!combo.Wargear.StartsWith("sm_mp_melee", StringComparison.Ordinal) || !seen.Add(combo.Wargear)) continue;
                void Row(string what, MeleeStep m)
                {
                    if (m.Reach is not { } r || r.Variants.Count == 0) return;
                    float d0 = Math.Clamp(r.DefaultDistance, r.Range.Min, r.Range.Max), to = MathF.Min(m.MoveAt * c.ActionLockScale, m.Duration), dt = c.TickSeconds;
                    float before = 0f, after = 0f;
                    for (float t = 0f; t + 1e-4f < to; t += dt)
                    {
                        before += MeleeLock.LungeStep(m, t, MathF.Min(t + dt, to), 0f, null, data with { ActionLockScale = c.ActionLockScale });
                        after += MeleeLock.LungeStep(m, t, MathF.Min(t + dt, to), 0f, null, c);
                    }
                    output.WriteLine($"| {combo.Wargear} {what} | {before:0.00} | {after:0.00} |");
                }
                for (int i = 0; i < combo.Steps.Count; i++) Row($"light {i + 1}", combo.Steps[i]);
                for (int i = 0; i < combo.Heavy.Count; i++) Row($"heavy {i + 1}", combo.Heavy[i]);
            }
        // The alignment's jerk on the knife's attacks, a target 60° off at 3 m: the M32 constant rate against the eased turn.
        var knife = c.Classes.SelectMany(k => k.MeleeChoices).First(m => m.Wargear == "sm_mp_melee_combat_knife");
        foreach (var (what, m) in knife.Steps.Select((s, i) => ($"light {i + 1}", s)).Concat(knife.Heavy.Select((s, i) => ($"heavy {i + 1}", s))))
        {
            if (m.Reach is not { } r || r.AlignTo <= r.AlignFrom) continue;
            float J(bool ease)
            {
                var cfg = c with { MeleeLock = c.MeleeLock with { AlignEase = ease } };
                float yaw = 0f, prevRate = 0f, worst = 0f;
                var offset = new Vector2(-MathF.Sin(60f * Deg) * 3f, MathF.Cos(60f * Deg) * 3f);
                for (float t = 0f; t < r.AlignTo + 2f * c.TickSeconds; t += c.TickSeconds)
                {
                    float next = MeleeLock.Align(yaw, 0f, m, t, t + c.TickSeconds, offset, cfg);
                    float rate = (next - yaw) / c.TickSeconds / Deg;
                    worst = MathF.Max(worst, MathF.Abs(rate - prevRate));
                    (yaw, prevRate) = (next, rate);
                }
                return MathF.Max(worst, MathF.Abs(prevRate));
            }
            output.WriteLine($"knife {what}: alignment {r.AlignFrom:0.000}..{r.AlignTo:0.000} s; jerk (largest change of turn rate in a tick, 60° target) {J(false):0} → {J(true):0} °/s");
        }
        // F, F → stomp on the knife: the press ticks that chain (the backhand's Y window).
        var y1 = knife.Heavy[0];
        output.WriteLine($"knife backhand: hit {y1.HitTime:0.000} s, Y window {y1.YComboOpen:0.000}..{y1.YComboClose:0.000} s of {y1.Duration:0.000} s → strike {y1.YNext} (the stomp, hit {knife.Heavy[3].HitTime:0.000} s)");
        Assert.Equal(4, y1.YNext);
        // F, F on the install's Tactical (knife) and Assault (chainsword), the second F at 0.2 s and at 1.0 s: the stomp follows the backhand.
        foreach (var (cls, name) in c.Classes.Select((k, i) => (i, k.Name)).Where(x => x.Name is "sm - tactical" or "sm - assault marine"))
            foreach (int second in new[] { 6, 30 })
            {
                var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);
                int stompAt = -1;
                var hits = new List<string>();
                for (int k = 0; k < 90 && stompAt < 0; k++)
                {
                    s = Combat.StepPlayer(s, new PlayerInput((uint)k, 0f, 0f, 0f, k == 0 || k == second ? InputButtons.MeleeHeavy : InputButtons.None), c, out var e);
                    if (e.MeleeHitStep > 0) hits.Add($"{(e.MeleeHeavy ? "heavy" : "light")} {e.MeleeHitStep} at {k * c.TickSeconds:0.00} s");
                    if (s.MeleeHeavy && s.MeleeStep == 4) stompAt = k;
                }
                output.WriteLine($"{name}: F, F (second at {second * c.TickSeconds:0.00} s): the stomp starts at {stompAt * c.TickSeconds:0.00} s; hits {string.Join(", ", hits)}");
                Assert.True(stompAt > 0, $"{name}: no stomp after F, F");
            }
    }
}
