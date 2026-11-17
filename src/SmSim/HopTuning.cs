using System.Numerics;

namespace SmSim;

/// <summary>
/// sim/hop-rules (protocol 72; the owner, 2026-11-14, three requests for the jump-pack classes, Assault Marine and Raptor):
/// (1) "you should be able to full jump as normal when you are charging off of a ledge" (Classic PvP and Musou Mode): a Jump press that ends a bull rush held in the air
/// opens the air jump (<see cref="PlayerState.JumpAirOpen"/>) until the body lands or another jump-pack phase or a hop starts, so the long press (the hold past the hop's decision tick)
/// lifts the pack in the air as on the ground, in Classic PvP too (<see cref="SimConfig.JumpPackAirJumps"/> alone allowed it in Musou Mode);
/// (2) "melee swing > hop > melee swing has a delay ... keep the cooldown on light combo attack melee swings but make a hop reduce it": the start of a hop (the dash) cuts the melee wait still running
/// (<see cref="PlayerState.MeleeLock"/>) to <see cref="LockKeep"/> of what is left, never below <see cref="LockFloor"/> (<see cref="HopLock"/>), so swing > hop > swing flows but hop-spamming cannot beat the
/// hop's own spacing nor drop the wait under the floor; (3) "aerial tackle/flying tackle should cancel a hop": a Jump press in any phase of the dash attack ends it and starts the hop in the same tick, past the
/// hop's spacing (<see cref="SimConfig.HopRules"/>). Every number is ours.
/// </summary>
public static class HopTuning
{
    /// <summary>(2) The share of the melee wait still running that a hop leaves (ours: half).</summary>
    public const float LockKeep = 0.5f;

    /// <summary>(2) The least wait a hop leaves (seconds; ours): a wait already shorter is not touched, a longer one is not cut below this, so swing > hop > swing never exceeds about four swings a second.</summary>
    public const float LockFloor = 0.25f;

    /// <summary>(2) The melee wait after a hop starts: <paramref name="remaining"/> cut to <see cref="LockKeep"/> of itself but not below <see cref="LockFloor"/> (unchanged when it is already at or under the floor).</summary>
    public static float HopLock(float remaining) =>
        remaining <= LockFloor ? remaining : MathF.Max(LockFloor, remaining * LockKeep);
}

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/hop-rules (protocol 72): the ledge jump after a charge, the hop's shortening of the melee wait and the hop that cancels the aerial tackle (<see cref="HopTuning"/>). False (restore switch
    /// <c>--old-hop-rules</c> on the server and every client; config hash): the protocol 71 rules. Hashed only when false.
    /// </summary>
    public bool HopRules { get; init; } = true;

    /// <summary>
    /// sim/ledge-charge-hop (protocol 85): the hop out of a ledge charge keeps the charge's speed and eases it down to the hop's top speed (<see cref="LedgeHop"/>). False (restore switch <c>--old-ledge-hop</c> on the server and every
    /// client; config hash): the protocol 84 rule (the body stops dead until the tap resolves, the hop starts at its own speed). Hashed only when false.
    /// </summary>
    public bool LedgeHopCarry { get; init; } = true;
}

/// <summary>
/// sim/ledge-charge-hop (protocol 85; the owner, 2026-11-17: "when hopping out of a ledge charge the player slows down to almost a stop and then gains normal hop momentum when pressing space bar and a direction mid
/// charge ... he smoothly transitions from the ledge charge fast momentum to the slightly slower max speed of a hop instead of slowing all the way down to the initial speed of a hop"). The Assault Marine and the Raptor, in
/// every mode (shared simulation: Classic PvP, Musou Mode, Warfront). Cause: the Jump press ends the bull rush held in the air, and the body is then a plain fall, so the walk-off rule (<see cref="Movement.Step"/>) zeroed its
/// horizontal velocity for every tick until the tap's release started the hop, whose flight then began at its own clip speed (the hop clip's own speed, about 20 m/s on the install, however fast the charge was). Now:
/// <list type="number">
/// <item>while the air jump the press opened (<see cref="PlayerState.JumpAirOpen"/>) waits for its tap or hold, the body keeps the charge's horizontal velocity (no walk-off drop), its excess over the hop's top speed
/// easing off with <see cref="EaseSeconds"/> (<see cref="Pending"/>);</item>
/// <item>the hop's first tick (<see cref="Evade.Step"/>, the pending state still open) and every tick after it adds the carried excess over the clip's own speed to the clip's speed, along the hop's own direction (the stick's: the
/// direction logic is unchanged), the excess decaying by the same <see cref="EaseSeconds"/> until the clip's speed alone remains (<see cref="Extra"/>). A hop that starts from rest, a walk or a run (below the clip's
/// speed), any other hop, and the other classes are untouched: no excess, no change.</item>
/// </list>
/// </summary>
public static class LedgeHop
{
    /// <summary>Time constant (s) of the carried speed's ease down to the hop's speed: the excess over it decays as exp(−t / this) (ours, an estimate: about 0.4 s to lose 93 % of it, so a 40 m/s charge is at the hop's 20 m/s after about half a second).</summary>
    public const float EaseSeconds = 0.15f;

    /// <summary>m/s of horizontal speed over the clip's own previous-tick speed that the hop's carry ignores (collision slides and rounding; ours).</summary>
    public const float Slack = 0.25f;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EvadeMove, object> Peaks = new();

    /// <summary>The fastest horizontal speed (m/s) of the hop's clip (its extracted root motion, per tick of <paramref name="dt"/>): the "max speed of a hop" the carried charge speed eases down to.</summary>
    public static float PeakSpeed(EvadeMove move, float dt)
    {
        if (!(dt > 0f)) return 0f;
        if (Peaks.TryGetValue(move, out var known)) return (float)known;
        float peak = 0f;
        for (float t = dt; t <= move.Duration + 1e-4f; t += dt)
            peak = MathF.Max(peak, (move.At(t) - move.At(t - dt)).Length() / dt);
        Peaks.AddOrUpdate(move, peak);
        return peak;
    }

    /// <summary>Whether the rule applies to this body this tick: the config switch, the hop rules, a jump pack with the dash.</summary>
    internal static bool Enabled(SimConfig c, PlayerState s) => c.LedgeHopCarry && c.HopRules && JumpEscape.Eligible(c, s);

    /// <summary>The factor by which an excess over the hop's speed is kept after one tick.</summary>
    public static float Decay(float dt) => dt > 0f ? MathF.Exp(-dt / EaseSeconds) : 0f;

    /// <summary>
    /// The horizontal velocity of a body waiting in the air for the air jump a ledge charge's Jump press opened: the charge's velocity, its excess over the hop's top speed one tick closer to it (never below it).
    /// </summary>
    internal static Vector2 Pending(Vector2 horizontal, SimConfig c, PlayerState s)
    {
        if (Loadouts.Evade(c, s) is not { Dash: true } spec) return horizontal;
        float peak = PeakSpeed(spec.Move(EvadeDirection.Forward), c.TickSeconds), speed = horizontal.Length();
        if (!(speed > peak) || !float.IsFinite(speed)) return horizontal;
        return horizontal * ((peak + (speed - peak) * Decay(c.TickSeconds)) / speed);
    }

    /// <summary>
    /// The extra speed (m/s) a hop's flight tick adds to the clip's: the carried excess over the clip's previous tick, decayed one tick. <paramref name="previous"/> is the body's horizontal speed now, <paramref name="clipBefore"/>
    /// the clip's speed on the previous tick (the first tick's own); <paramref name="first"/>: this is the hop's first tick, which only a hop out of a ledge charge (<see cref="PlayerState.JumpAirOpen"/>) carries into.
    /// </summary>
    internal static float Extra(SimConfig c, PlayerState s, bool first, float previous, float clipBefore)
    {
        if (!Enabled(c, s) || (first && !s.JumpAirOpen)) return 0f;
        float over = previous - clipBefore;
        return over > Slack && float.IsFinite(over) ? over * Decay(c.TickSeconds) : 0f;
    }
}
