namespace SmSim;

/// <summary>
/// feel/melee-cancels (protocol 54; the owner, 2026-11-02): a melee press ends the Assault and Raptor's hop (the jump-pack dash) and the bull rush at once and starts the
/// swing in the same tick, with no animation-cancel timing; the Tactical classes' bull rush the same. A cancel keeps the next swing from starting until the moment the
/// cancelled clip would have ended (<see cref="PlayerState.MeleeLock"/>, in classic PvP and Musou Mode alike), so cancelling is not a way to spam. A Jump press ends the pack
/// classes' bull rush at once (the jump pack then takes it, <see cref="JumpPack.Begin"/>). Not touched: the Tactical roll, the Devastator and Havoc hop and charge, the Ork Boy, the
/// charge's collision stop (a recoil: the data's own branches), the dash attack (already a swing).
/// </summary>
public static class MeleeCancel
{
    /// <summary>
    /// sim/hop-cancel-momentum (ours, an estimate for the owner's "some momentum carried over"): the fraction of the hop's horizontal speed a melee cancel keeps while the swing is in the
    /// air (about 6.5 m/s of the hop's 16.4). Spec "Hop cancelled by melee"; refine register.
    /// </summary>
    public const float HopCarry = 0.4f;

    /// <summary>Whether the class may cancel: a jump pack (Assault, Raptor), or a roll evade with a charge (the Tactical classes).</summary>
    public static bool Eligible(SimConfig c, PlayerState s) =>
        Races.Of(c, s.Class, s.Team) != Race.Orks && (Loadouts.JumpPack(c, s) is not null || (Loadouts.Rush(c, s) is not null && Combat.IsRoll(Loadouts.Evade(c, s))));

    /// <summary>
    /// The cancel this tick: the state with the hop or bull rush ended and the seconds the next swing must wait (0 for none), or null when nothing is cancelled.
    /// <paramref name="meleePress"/> and <paramref name="jumpPress"/> are this tick's fresh presses.
    /// </summary>
    internal static (PlayerState State, float Lock)? Apply(PlayerState s, SimConfig c, bool meleePress, bool jumpPress, CollisionWorld? world)
    {
        meleePress = meleePress && s.MeleeLock <= c.TickSeconds;   // a swing's or an earlier cancel's wait still running: no cancel, or swing, hop, cancel, swing would get round it
        if (!s.Alive || s.Reaction != ReactionKind.None || (!meleePress && !jumpPress) || !Eligible(c, s)) return null;
        bool pack = Loadouts.JumpPack(c, s) is not null;
        if (s.Rush == RushPhase.Rush && Rush.MoveOf(c, s) is { } move)
        {
            if (!meleePress && !(jumpPress && pack)) return null;
            float remaining = meleePress ? MathF.Max(0f, move.End - Rush.Time(s, c)) : 0f;
            return (Rush.Cleared(s), remaining);
        }
        // sim/jump-escape (protocol 62; the owner 2026-11-06): the pack classes' Jump ends every other phase of the charge too (its collision stop, the dash attack, its hit,
        // wall stop and run-out), at once.
        if (s.Rush != RushPhase.None && jumpPress && pack && JumpEscape.Eligible(c, s)) return (Rush.Cleared(s), 0f);
        if (s.Rush == RushPhase.None && meleePress && pack && Loadouts.Evade(c, s) is { Dash: true } evade && Evade.Evading(s) && !Rush.CanDashAttack(s, c, world))
        {
            var clip = evade.Move(s.EvadeDirection);
            float remaining = s.EvadeLanded && clip.Landing is { } landing
                ? MathF.Max(0f, landing.Duration - Evade.Time(s, c))
                : MathF.Max(0f, clip.Duration - Evade.Time(s, c)) + (clip.Landing?.Duration ?? 0f);
            // sim/hop-cancel-momentum (protocol 62; the owner 2026-11-06: "he should start moving horizontally only with some momentum carried over, combined with the step forward or lock
            // on lunge of the melee attack"): the hop's rise stops at once (no upward speed is kept) and HopCarry of its horizontal speed rides on under the swing while the body is in the
            // air (Combat: the air swing); the swing's own step or lunge adds to it.
            var v = s.Velocity;
            var carried = float.IsFinite(v.X) && float.IsFinite(v.Y) ? new System.Numerics.Vector3(v.X * HopCarry, v.Y * HopCarry, float.IsFinite(v.Z) ? MathF.Min(0f, v.Z) : 0f)
                : System.Numerics.Vector3.Zero;
            if (c.AerialTackleOff) remaining = 0f;   // musou/no-aerial-tackle (owner 2026-11-12): a light attack at any point in a hop, no wait before the next
            return (s with { EvadeTicks = 0, EvadeDirection = EvadeDirection.Forward, EvadeLanded = false, Velocity = carried }, remaining);
        }
        return null;
    }
}
