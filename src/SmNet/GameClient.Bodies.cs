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
            if (tick == input.BodyTick)
            {
                var view = PlayerCollision.See(players.Values, PlayerId, tick, input.Tick, _config);
                ExtrasSolids?.AddBodies(tick, ref view);   // EXTRAS-HOOK: added content's solid bodies at the same tick (Musou: the big Orks; World.MusouSolids.cs on the server)
                return view;
            }
        return null;
    }

    /// <summary>
    /// Added content's solid bodies and melee-lock targets as this client knows them (musou/boss-collision; the client's added content sets it, null otherwise). The server's
    /// World answers the same questions from its own record (<c>World.ExtrasBodies</c>, <c>World.ExtrasLockView</c>).
    /// </summary>
    public IExtrasSolids? ExtrasSolids { get; set; }

    /// <summary>
    /// The others a melee lock is chosen and checked against at view tick <paramref name="tick"/> for a player standing as <paramref name="self"/>: <see cref="ViewAt"/>, plus
    /// added content's lock targets (<see cref="ExtrasSolids"/>); null without a view. Only the lock reads it (the charge's contact keeps <see cref="ViewAt"/>, as the server).
    /// </summary>
    /// <summary>The lock target sent last (<see cref="MeleeLockTarget"/>) as the lock's view shows it at the newest held snapshot (presentation: an added-content target is no player); null without one.</summary>
    public PlayerState? LockTargetShown() =>
        MeleeLockTarget > 0 && HeldSnapshotTick(RenderTick()) is uint tick && LockViewAt(tick, Predicted) is { } view && view.TryGetValue(MeleeLockTarget, out var target) ? target : null;

    IReadOnlyDictionary<int, PlayerState>? LockViewAt(uint tick, in PlayerState self)
    {
        var view = ViewAt(tick);
        return view is not null && ExtrasSolids is { } extras ? extras.WithLockTargets(tick, self, view) : view;
    }

    /// <summary>The bodies the newest input collided with (tests and probes; null: none).</summary>
    public BodyView? LastBodies { get; private set; }
}

/// <summary>
/// Added content's solid bodies and melee-lock targets on a client (musou/boss-collision; <see cref="GameClient.ExtrasSolids"/>): what the server's World adds in its hooks, from
/// what this client was sent.
/// </summary>
public interface IExtrasSolids
{
    /// <summary>Adds the solid bodies as they stood at server tick <paramref name="tick"/> to <paramref name="view"/> (null stays null).</summary>
    void AddBodies(uint tick, ref BodyView? view);
    /// <summary><paramref name="view"/> with the lock targets at server tick <paramref name="tick"/> for a player standing as <paramref name="self"/>.</summary>
    IReadOnlyDictionary<int, PlayerState> WithLockTargets(uint tick, in PlayerState self, IReadOnlyDictionary<int, PlayerState> view);
}
