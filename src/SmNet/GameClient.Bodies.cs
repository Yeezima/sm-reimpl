using SmSim;

namespace SmNet;

/// <summary>
/// sim/player-collision (protocol 43): the other players' capsules the own prediction collides with. Each input names the newest snapshot this client
/// held when it was sampled (<see cref="PlayerInput.BodyTick"/>); the prediction, every replay of it and the server collide with the other players as
/// that snapshot had them (<see cref="PlayerCollision.See"/>; the server keeps the same floats, <c>World.RecordBodies</c>), so meeting a player is
/// predicted exactly.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>The body tick for an input of tick <paramref name="tick"/>: the newest snapshot's, when there is one before it and no more than <see cref="PlayerCollision.MaxBodyLagTicks"/> back; else 0 (no bodies, on the server too).</summary>
    uint BodyTickFor(uint tick) =>
        _config.PlayerBodies && _snapshots.Count > 0 && _latestSnapshotTick > 0 && _latestSnapshotTick < tick && tick - _latestSnapshotTick <= PlayerCollision.MaxBodyLagTicks
            ? _latestSnapshotTick : 0;

    /// <summary>The bodies an input collides with: the snapshot of its body tick (still held), seen from it to the input's tick; null without one.</summary>
    BodyView? BodiesFor(PlayerInput input)
    {
        if (!_config.PlayerBodies || input.BodyTick == 0) return null;
        foreach (var (tick, players, _) in _snapshots)
            if (tick == input.BodyTick) return PlayerCollision.See(players.Values, PlayerId, tick, input.Tick, _config);
        return null;
    }

    /// <summary>The bodies the newest input collided with (tests and probes; null: none).</summary>
    public BodyView? LastBodies { get; private set; }
}
