namespace SmBots.Extras.Warfront;

/// <summary>A Warfront retainer's order as the brain reads it (the owner's Follow, Hold or Attack; the jump-pound order while it runs).</summary>
public enum RetainerOrder : byte { Follow = 0, Hold = 1, Attack = 2, JumpPound = 3 }

/// <summary>Where a retainer stands against its Follow leash (<see cref="WarfrontLeash"/>).</summary>
public enum LeashState : byte
{
    /// <summary>Inside the circle round the owner: the Classic brain decides, every path goal kept inside the circle.</summary>
    Inside = 0,
    /// <summary>Outside, carried there by a committed Classic move (a jump, charge, dash, swing, throw, roll): the move finishes, then it returns (no grace).</summary>
    Overshoot = 1,
    /// <summary>Outside because the owner moved away while it was fighting: three seconds of continued combat (not reset by new attacks), then it returns.</summary>
    Grace = 2,
    /// <summary>Walking back to the owner (no fight) until it is well inside the circle again.</summary>
    Return = 3,
    /// <summary>An explicit order (Hold, Attack, the jump-pound) is in force: no leash.</summary>
    Ordered = 4,
}

/// <summary>
/// The Follow leash of a Warfront retainer (Phase B T4; the approved brief 2026-11-23 "Follow and orders", the leash doubled to 40 m by the owner on 2026-11-24): a pure state machine, one per retainer,
/// stepped once per input tick before the fight. Engine-free and deterministic: the clock is the caller's.
/// <list type="bullet">
/// <item>Follow keeps a <see cref="Radius"/> circle measured from the owning player (not from the retainer or the enemy).</item>
/// <item>A committed Classic move may carry it beyond the circle (overshoot): the move finishes, then it returns.</item>
/// <item>The owner moving away and leaving an engaged retainer outside: <see cref="GraceSeconds"/> of continued combat (new attacks never reset it), then it finishes a move in progress and returns. No teleport.</item>
/// <item>An explicit order overrides the leash while active; switching back to Follow restores it, and a retainer outside returns as soon as a committed move finishes, with no new grace.</item>
/// <item>A return ends <see cref="ReturnMargin"/> inside the circle, so the edge does not flicker.</item>
/// </list>
/// </summary>
public sealed class WarfrontLeash
{
    /// <summary>The Follow leash (owner 2026-11-24: "Increase the retainer leash distance by 2x", 20 -> 40 m). Kind: owner; confidence: stated.</summary>
    public const float Radius = 40f;
    /// <summary>The moving-owner grace (the approved brief: "allow three seconds of continued combat before returning"). Kind: owner; confidence: stated.</summary>
    public const double GraceSeconds = 3.0;
    /// <summary>A return ends this far inside the circle (ours: a small hysteresis so a retainer at the edge does not turn back and forth). Kind: ours; confidence: low.</summary>
    public const float ReturnMargin = 4f;
    /// <summary>A retainer counts as engaged when it fought within this before it left the circle (ours: the brain's fight is decided tick by tick). Kind: ours; confidence: low.</summary>
    public const double EngagedSeconds = 0.5;

    public LeashState State { get; private set; } = LeashState.Inside;
    /// <summary>When the grace began (the caller's clock), NaN outside a grace.</summary>
    public double GraceStarted { get; private set; } = double.NaN;
    /// <summary>The order of the last step.</summary>
    public RetainerOrder Order { get; private set; } = RetainerOrder.Follow;
    /// <summary>True from an order's end until the retainer is inside again: the return gets no grace (the brief: "with no new grace window for that transition").</summary>
    public bool NoGrace { get; private set; }

    /// <summary>
    /// One tick: <paramref name="order"/> in force, the retainer <paramref name="fromOwner"/> metres (flat) from its owner, in a committed move or not, and when it last fought
    /// (<paramref name="lastFight"/>, the caller's clock; -inf for never). The state after the step.
    /// </summary>
    public LeashState Step(double now, RetainerOrder order, float fromOwner, bool committed, double lastFight)
    {
        bool engaged = now - lastFight <= EngagedSeconds;
        if (order != RetainerOrder.Follow)
        {
            Order = order;
            GraceStarted = double.NaN;
            return State = LeashState.Ordered;
        }
        bool outside = fromOwner > Radius;
        if (Order != RetainerOrder.Follow)
        {
            // back to Follow: the leash again; outside, return once a committed move has finished (no new grace)
            Order = RetainerOrder.Follow;
            NoGrace = outside;
            State = !outside ? LeashState.Inside : committed ? LeashState.Overshoot : LeashState.Return;
            return State;
        }
        switch (State)
        {
            case LeashState.Inside:
            case LeashState.Ordered:
                if (!outside) { NoGrace = false; return State = LeashState.Inside; }
                if (committed) return State = LeashState.Overshoot;
                if (engaged && !NoGrace) { GraceStarted = now; return State = LeashState.Grace; }
                return State = LeashState.Return;
            case LeashState.Overshoot:
                if (!outside) { NoGrace = false; return State = LeashState.Inside; }
                return State = committed ? LeashState.Overshoot : LeashState.Return;
            case LeashState.Grace:
                if (!outside) { GraceStarted = double.NaN; return State = LeashState.Inside; }
                if (now - GraceStarted < GraceSeconds || committed) return State = LeashState.Grace;   // a move in progress at the end finishes first
                GraceStarted = double.NaN;
                return State = LeashState.Return;
            case LeashState.Return:
                if (fromOwner <= Radius - ReturnMargin) { NoGrace = false; return State = LeashState.Inside; }
                return State = LeashState.Return;
        }
        return State;
    }

    /// <summary>A new life or a new battle: inside, no grace, Follow.</summary>
    public void Reset()
    {
        State = LeashState.Inside; GraceStarted = double.NaN; Order = RetainerOrder.Follow; NoGrace = false;
    }
}
