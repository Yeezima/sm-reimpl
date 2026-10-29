using SmSim;

namespace SmNet;

/// <summary>
/// How the client restores the weapon scatter's state at a reconcile (weapons/spread; the bloom is not on the wire, <see cref="Spread.Carry"/>).
/// <see cref="Choose"/> decides between the prediction's bloom for the snapshot's tick and rest, and <see cref="GameClient"/> counts each case.
/// </summary>
public static class SpreadReconcile
{
    /// <summary>What the reconcile did with the spread fields.</summary>
    public enum Outcome
    {
        /// <summary>The prediction's bloom for the snapshot's tick was kept.</summary>
        Carried,
        /// <summary>No prediction for that tick (before the first one; a snapshot ahead of the client after a stall): rest.</summary>
        NoHistory,
        /// <summary>The prediction and the record disagree on life, class, slot or chosen weapons: rest (<see cref="Spread.Carry"/>).</summary>
        Mismatch,
        /// <summary>A round began between the previous snapshot and this one with everyone respawned in place: rest.</summary>
        RoundRespawn,
    }

    /// <summary>
    /// Whether the server respawned every player in place between a snapshot with match state <paramref name="before"/> and one with
    /// <paramref name="after"/> (the rules of <c>World.StepMatch</c>: warm-up or an ended match into the countdown; an ended match straight into
    /// play; Seize Ground's warm-up straight into play). A living player's record then matches its prediction on every compared field, so this
    /// is the only way the client can tell that the server rested the bloom.
    /// </summary>
    public static bool RoundRespawned(MatchState before, MatchState after, GameMode mode) => after switch
    {
        MatchState.Countdown => before != MatchState.Countdown,
        MatchState.Playing => before == MatchState.Ended || (before == MatchState.Warmup && mode == GameMode.SeizeGround),
        _ => false,
    };

    /// <summary>
    /// The own record to replay from: <paramref name="record"/> (spread fields at rest, as decoded) with the prediction's spread for its tick when
    /// there is one (<paramref name="predicted"/>) and no round respawn happened in between; <paramref name="outcome"/> says which.
    /// </summary>
    public static PlayerState Choose(PlayerState record, PlayerState? predicted, bool roundRespawned, out Outcome outcome)
    {
        var rest = record with { SpreadBloom = 0f, SpreadSince = 0f, SpreadYaw = 0f, SpreadPitch = 0f };
        if (roundRespawned) { outcome = Outcome.RoundRespawn; return rest; }
        if (predicted is not { } p) { outcome = Outcome.NoHistory; return rest; }
        outcome = Spread.SameBloomOwner(record, p) ? Outcome.Carried : Outcome.Mismatch;
        return Spread.Carry(record, p);
    }
}
