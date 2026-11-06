using SmSim;

namespace SmNet;

/// <summary>
/// fx/jumppack-jets (the owner, 2026-11-06: "the thruster jets should only look like [the ready jets] when a jump is available. they should cut out smoothly when it is
/// not possible for a jump to be initiated (shortly after a previous jump)"): whether a jump-pack player's Space press would start something now, read from the
/// simulation's own start rules (<see cref="JumpPack"/>'s take-off and air-hop conditions; nothing here changes them), and the jets' opacity that follows it.
/// <para>
/// Own player (the predicted record): on the ground a take-off can start in no jump phase or once a landing's <see cref="JumpPack.JumpLock"/> has run, and the
/// jump needs <see cref="JumpPackSpec.CostAt"/>(0) power (below it the press is only the dash); in the air the hop (the jump-pack dash) needs no jump phase or the
/// hang / fall and <see cref="Evade.CanDashInAir"/> (the dash's spacing). Never in the take-off, the rise, the dive or its recovery, a dash, a charge or a hit
/// reaction. Others (their slim records carry no ticks, locks or power): the same phases from their flags, a landing lock and the dash spacing timed on the client
/// from when their landing or dash was seen, power taken as enough. **Ours** (an interpretation of the owner's rule; the original shows the jets by power,
/// <c>action_idle_by_power</c>, which with the owner's 300-power pack would never cut out).
/// </para>
/// </summary>
public static class JetReadiness
{
    /// <summary>Seconds the jets take to cut out when a jump stops being available (ours; "cut out smoothly").</summary>
    public const float FadeOutSeconds = 0.2f;
    /// <summary>Seconds the jets take to come back when a jump is available again (ours).</summary>
    public const float FadeInSeconds = 0.25f;

    /// <summary>The own player's readiness from its predicted record.</summary>
    public static bool Own(PlayerState s, JumpPackSpec pack, SimConfig c, bool hasDash)
    {
        if (!Common(s, hasDash)) return false;
        float dt = c.TickSeconds;
        if (s.Grounded)
        {
            // sim/jump-escape (protocol 62): with the dash (Assault, Raptor) a press cuts the landing locks and the recovery, and ends a reaction or a charge.
            bool phaseOk = s.Jump == JumpPhase.None || (hasDash && s.Jump is JumpPhase.Landed or JumpPhase.Recover)
                || (s.Jump == JumpPhase.Landed && (s.JumpTicks + 1) * dt + 1e-5f >= JumpPack.JumpLock(pack, s.JumpLandMoving, c));
            return phaseOk && s.JumpPower + 1e-5f >= pack.CostAt(0f);
        }
        return hasDash && s.Jump is JumpPhase.None or JumpPhase.Hang or JumpPhase.Fall && Evade.CanDashInAir(s, c);
    }

    /// <summary>
    /// Another player's readiness from its slim record: <paramref name="sinceLanded"/> seconds since its jump was last seen to end on the ground, <paramref name="sinceDash"/>
    /// since its last dash started (infinity: none seen), <paramref name="rising"/> while its record shows it rising in a jump.
    /// </summary>
    public static bool Other(PlayerState s, JumpPackSpec pack, SimConfig c, bool hasDash, double sinceLanded, double sinceDash, bool rising, float dashSpacing)
    {
        if (!Common(s, hasDash)) return false;
        if ((JumpPack.Pounding(s.Jump) && !(hasDash && s.Grounded)) || rising) return false;
        if (s.Grounded) return !JumpPack.Jumping(s.Jump) && (hasDash || sinceLanded + 1e-6 >= JumpPack.JumpLock(pack, MovingOnGround(s), c));
        return hasDash && sinceDash + 1e-6 >= dashSpacing;
    }

    static bool Common(PlayerState s, bool hasDash) =>
        s.Alive && (hasDash || (s.Reaction == ReactionKind.None && s.Rush == RushPhase.None)) && !Evade.Evading(s)
        && s.Jump is not (JumpPhase.TakeOff or JumpPhase.Ascent or JumpPhase.Dive) && (hasDash || s.Jump != JumpPhase.Recover);

    /// <summary>Whether a body on the ground moves (the landing lock's moving variant): horizontal speed above 0.5 m/s (ours).</summary>
    public static bool MovingOnGround(PlayerState s) => s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y > 0.25f;

    /// <summary>The jets' opacity one frame on: toward 1 while ready, toward 0 when not, linearly over <see cref="FadeInSeconds"/> / <see cref="FadeOutSeconds"/>.</summary>
    public static float Step(float opacity, bool ready, float dt)
    {
        if (!float.IsFinite(opacity)) opacity = ready ? 1f : 0f;
        float rate = ready ? 1f / FadeInSeconds : -1f / FadeOutSeconds;
        return Math.Clamp(opacity + rate * Math.Max(0f, dt), 0f, 1f);
    }
}

/// <summary>
/// fx/jumppack-jets (the owner, 2026-11-06: in a melee attack in mid-air "the legs warp and jumble up together"): the cause was the clip foot lock (anim/melee-flow), which holds each
/// foot the strike's clip puts down (judged in the model's own space) at the place in the world it was put down: in the air the body flies on and the leg IK stretched each leg back
/// toward that point. A strike in the air now has its legs as the clip poses them (the original's air attacks play their clip on the whole body). **Ours**: the short grace keeps the
/// lock over a step or a curb (the body may leave the ground for a tick or two).
/// </summary>
public static class AirLegRules
{
    /// <summary>Seconds in the air within which a strike still holds its put-down feet (ours: 0.1 s, three ticks at 30 Hz).</summary>
    public const float GraceSeconds = 0.1f;

    /// <summary>Whether a strike's or charge's clip may hold its put-down feet: alive, not in a jump-pack jump, and on the ground or in the air no longer than <see cref="GraceSeconds"/>.</summary>
    public static bool FootLock(bool alive, JumpPhase jump, bool grounded, float airSeconds) =>
        alive && jump is JumpPhase.None or JumpPhase.Landed or JumpPhase.Recover && (grounded || airSeconds <= GraceSeconds + 1e-4f);

    /// <summary>The time in the air one frame on: 0 on the ground, else grown by <paramref name="dt"/>.</summary>
    public static float AirTime(float airSeconds, bool grounded, float dt) => grounded ? 0f : (float.IsFinite(airSeconds) ? airSeconds : 0f) + Math.Max(0f, dt);
}
