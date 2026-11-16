using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fx/jumppack-jets (the owner, 2026-11-06): the jump pack's ready jets show only while a jump can start (<see cref="JetReadiness"/>) and a strike in the air has no
/// clip foot lock (<see cref="AirLegRules"/>). The readiness is checked against the simulation itself: on every tick of a jump and its landing, a Jump press on a
/// state the rule calls ready starts the take-off, and on one it calls not ready (the take-off, the flight, the landing's lock) it does not. Synthetic specs
/// (<see cref="JumpPackDashTests.Config"/>); no original bytes.
/// </summary>
public class JetReadinessTests(ITestOutputHelper output)
{
    const int Assault = 0;
    static readonly SimConfig C = JumpPackDashTests.Config;
    static JumpPackSpec Pack => C.JumpPacks[0];

    static PlayerState Fresh() => Combat.Respawned(new PlayerState(1, default, Vector3.Zero, 0f, true) { Class = Assault }, C);

    static PlayerState Step(PlayerState s, InputButtons b) => Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b, 0f), C, out _);

    [Fact]
    public void Standing_with_power_is_ready()
    {
        var s = Fresh();
        Assert.True(JetReadiness.Own(s, Pack, C, hasDash: true));
        Assert.False(JetReadiness.Own(s with { Alive = false }, Pack, C, hasDash: true));
        Assert.False(JetReadiness.Own(s with { JumpPower = 0f }, Pack, C, hasDash: true));   // only the dash: no jump to show the jets for
    }

    [Fact]
    public void A_full_jump_cuts_the_jets_until_the_landing_lock_ends_and_the_rule_matches_the_simulation()
    {
        var s = Fresh();
        var states = new List<PlayerState>();
        for (int k = 0; k < 300; k++)
        {
            states.Add(s);
            s = Step(s, k < 13 ? InputButtons.Jump : InputButtons.None);
            if (k > 20 && s.Grounded && s.Jump == JumpPhase.None) { states.Add(s); break; }
        }
        Assert.Contains(states, x => x.Jump == JumpPhase.Ascent);
        Assert.Contains(states, x => x.Jump == JumpPhase.Landed);
        int cutAt = -1, backAt = -1, landedAt = states.FindIndex(x => x.Jump == JumpPhase.Landed);
        for (int k = 1; k < states.Count; k++)
        {
            var x = states[k];
            bool ready = JetReadiness.Own(x, Pack, C, hasDash: true);
            if (!ready && cutAt < 0) cutAt = k;
            if (ready && cutAt >= 0 && backAt < 0) backAt = k;
            if (!x.Grounded) { Assert.False(ready && x.Jump is JumpPhase.TakeOff or JumpPhase.Ascent, $"tick {k}: ready in {x.Jump}"); continue; }
            if (x.Buffered != BufferedAction.None) continue;   // a buffered press would replay: the press below is not the only input
            // The simulation's own answer: a fresh Jump press on this state starts the take-off exactly when the rule says ready.
            var pressed = Step(x with { JumpHeld = false }, InputButtons.Jump);
            bool started = pressed.Jump == JumpPhase.TakeOff && pressed.JumpTicks == 0;
            Assert.True(ready == started, $"tick {k} ({x.Jump}, ticks {x.JumpTicks}): rule {ready}, simulation {started}");
        }
        int groundBack = Enumerable.Range(landedAt, states.Count - landedAt).FirstOrDefault(k => JetReadiness.Own(states[k], Pack, C, hasDash: true), -1);
        int airBack = states.FindIndex(x => !x.Grounded && JetReadiness.Own(x, Pack, C, hasDash: true));
        output.WriteLine($"jets cut on tick {cutAt}, back in the air (the hop) on tick {airBack} ({states[Math.Max(0, airBack)].Jump}), landed on tick {landedAt} (cut again), back on the ground on tick {groundBack} ({(groundBack - landedAt) * C.TickSeconds:F2} s after the landing; lock {JumpPack.JumpLock(Pack, false, C):F2} s)");
        Assert.True(cutAt is > 0 and < 3, $"cut on tick {cutAt}");
        Assert.True(airBack < 0 || states[airBack].Jump is JumpPhase.Hang or JumpPhase.Fall, "in the air only once the hop is allowed (after the apex)");
        // sim/jump-escape (protocol 62): the Assault's press cuts the landing's lock, so with the power for a jump (the descent recharged it) the jets are back on the landing tick.
        Assert.True(JetReadiness.Own(states[landedAt], Pack, C, hasDash: true), "ready on the landing");
        Assert.Equal(landedAt, groundBack);
    }

    [Fact]
    public void In_the_air_the_hop_decides()
    {
        var falling = Fresh() with { Grounded = false, Jump = JumpPhase.Fall, Velocity = new Vector3(0f, 0f, -3f) };
        Assert.True(JetReadiness.Own(falling, Pack, C, hasDash: true));
        Assert.False(JetReadiness.Own(falling, Pack, C, hasDash: false));
        Assert.False(JetReadiness.Own(falling with { EvadeLock = 10 }, Pack, C, hasDash: true));   // the dash's spacing still runs
        Assert.False(JetReadiness.Own(falling with { Jump = JumpPhase.Dive }, Pack, C, hasDash: true));
        Assert.False(JetReadiness.Own(falling with { Jump = JumpPhase.Ascent }, Pack, C, hasDash: true));
    }

    [Fact]
    public void Others_are_timed_from_their_landing_and_dash()
    {
        var standing = Fresh();
        float lockS = JumpPack.JumpLock(Pack, false, C);
        // sim/jump-escape (protocol 62): a dash class's press cuts the landing lock, so it is ready at once; a pack without the dash still waits for the lock.
        Assert.True(JetReadiness.Other(standing, Pack, C, true, sinceLanded: lockS * 0.5, sinceDash: double.PositiveInfinity, rising: false, dashSpacing: 0.8f));
        Assert.False(JetReadiness.Other(standing, Pack, C, false, sinceLanded: lockS * 0.5, sinceDash: double.PositiveInfinity, rising: false, dashSpacing: 0.8f));
        Assert.True(JetReadiness.Other(standing, Pack, C, true, sinceLanded: lockS + 0.01, sinceDash: double.PositiveInfinity, rising: false, dashSpacing: 0.8f));
        var air = standing with { Grounded = false, Jump = JumpPhase.Fall };
        Assert.False(JetReadiness.Other(air, Pack, C, true, double.PositiveInfinity, sinceDash: 0.3, rising: false, dashSpacing: 0.8f));
        Assert.True(JetReadiness.Other(air, Pack, C, true, double.PositiveInfinity, sinceDash: 0.9, rising: false, dashSpacing: 0.8f));
        Assert.False(JetReadiness.Other(air, Pack, C, true, double.PositiveInfinity, double.PositiveInfinity, rising: true, dashSpacing: 0.8f));
    }

    [Fact]
    public void The_jets_fade_smoothly()
    {
        float o = 1f;
        int frames = 0;
        while (o > 0f && frames < 100) { o = JetReadiness.Step(o, ready: false, 1f / 60f); frames++; }
        Assert.InRange(frames, 11, 13);   // 0.2 s at 60 fps
        Assert.Equal(0.5f, JetReadiness.Step(0f, ready: true, JetReadiness.FadeInSeconds / 2f), 3);
        Assert.Equal(1f, JetReadiness.Step(float.NaN, ready: true, 0f));
    }

    [Fact]
    public void No_clip_foot_lock_in_the_air()
    {
        Assert.True(AirLegRules.FootLock(true, JumpPhase.None, grounded: true, airSeconds: 0f));
        Assert.True(AirLegRules.FootLock(true, JumpPhase.None, grounded: false, airSeconds: 0.05f));   // a curb step
        Assert.False(AirLegRules.FootLock(true, JumpPhase.None, grounded: false, airSeconds: 0.3f));   // a strike after a hop
        Assert.False(AirLegRules.FootLock(true, JumpPhase.Dive, grounded: false, airSeconds: 0f));
        Assert.False(AirLegRules.FootLock(false, JumpPhase.None, grounded: true, airSeconds: 0f));
        Assert.Equal(0f, AirLegRules.AirTime(1f, grounded: true, 0.1f));
        Assert.Equal(0.3f, AirLegRules.AirTime(0.2f, grounded: false, 0.1f), 4);
    }
}
