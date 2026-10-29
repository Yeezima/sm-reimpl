namespace SmSim;

public sealed partial class World
{
    /// <summary>
    /// sim/player-collision: how many ticks of end states the world keeps for the bodies inputs name (<see cref="PlayerInput.BodyTick"/>; 64 ticks,
    /// 2.1 s, above <see cref="PlayerCollision.MaxBodyLagTicks"/>). Not cleared by a round restart: a client's snapshot from before it is still answered.
    /// </summary>
    public const int BodyHistoryTicks = 64;

    readonly Dictionary<uint, PlayerState[]> _bodyHistory = new();

    /// <summary>
    /// The end state of tick <see cref="Tick"/> (every player, in id order): exactly what a snapshot sent after this step carries (positions and
    /// velocities travel as floats), so a client's prediction sees the same bodies the server does.
    /// </summary>
    void RecordBodies()
    {
        if (!Config.PlayerBodies) return;   // nothing reads it (the review)
        var all = _players.Values.ToArray();
        Array.Sort(all, (a, b) => a.PlayerId.CompareTo(b.PlayerId));
        _bodyHistory[Tick] = all;
        if (Tick >= BodyHistoryTicks) _bodyHistory.Remove(Tick - BodyHistoryTicks);
    }

    /// <summary>
    /// The bodies the input of player <paramref name="playerId"/> for the current tick collides with (<see cref="PlayerCollision.See"/> over the
    /// tick it names): null (no player collision) when the config turns it off, the input names no tick, a tick not before this one, one more than
    /// <see cref="PlayerCollision.MaxBodyLagTicks"/> back, or one the world does not have (the client predicts the same in every case it can see).
    /// </summary>
    BodyView? BodiesFor(int playerId, PlayerInput input)
    {
        if (!Config.PlayerBodies) return null;
        uint oldest = Tick > PlayerCollision.MaxBodyLagTicks ? Tick - PlayerCollision.MaxBodyLagTicks : 1;
        uint tick = input.BodyTick;
        // The review (must-fix): the world bounds the picture itself. A tick older than the lag cap is raised to it; with BoundBodies (the server), so is
        // none (0): a client cannot shed the bodies by naming none or an old snapshot. Without it (tests, tools) 0 still means no bodies.
        if (tick == 0) { if (!BoundBodies) return null; tick = oldest; }
        else if (tick < oldest) tick = oldest;
        if (tick >= Tick) return null;
        return _bodyHistory.TryGetValue(tick, out var seen) ? PlayerCollision.See(seen, playerId, tick, Tick, Config) : null;
    }

    /// <summary>
    /// sim/player-collision (the review's must-fix): an input naming no body tick collides with the oldest picture allowed
    /// (<see cref="PlayerCollision.MaxBodyLagTicks"/> back) instead of with nothing. The server sets it; an honest client names a snapshot from its
    /// first ticks on (only a join's first ticks, before its first snapshot, then predict without bodies while the server has them).
    /// </summary>
    public bool BoundBodies { get; init; }

    /// <summary>The players at the end of tick <paramref name="tick"/> as the bodies record keeps them (in id order), or null (tests and probes).</summary>
    public IReadOnlyList<PlayerState>? BodiesAt(uint tick) => _bodyHistory.TryGetValue(tick, out var all) ? all : null;

    /// <summary>
    /// A spawn spot when every spot is taken (within 2 m of a living player): the one whose nearest living player is farthest (ties: the first from
    /// <paramref name="start"/>), so a crowded spawn overlaps as little as it can (sim/player-collision; ours).
    /// </summary>
    int LeastCrowded(IReadOnlyList<System.Numerics.Vector3> spots, int start, int self)
    {
        int best = start % spots.Count;
        float bestDistance = -1f;
        for (int k = 0; k < spots.Count; k++)
        {
            int i = (start + k) % spots.Count;
            float nearest = float.PositiveInfinity;
            foreach (var (id, p) in _players)
                if (id != self && p.Alive) nearest = MathF.Min(nearest, System.Numerics.Vector3.DistanceSquared(p.Position, spots[i]));
            if (nearest > bestDistance) { bestDistance = nearest; best = i; }
        }
        return best;
    }
}
