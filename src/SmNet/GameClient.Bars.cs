namespace SmNet;

/// <summary>
/// The enemy bars of the readout perks on the client (protocol 44): the newest <see cref="EnemyBars"/> message, shown for <see cref="BarsLifetime"/> seconds after it
/// arrived. A player without the perks never receives one, so <see cref="EnemyBarFor"/> is always null for it.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>Seconds a bars message stays valid (the server sends ten a second; lost ones are bridged).</summary>
    public const double BarsLifetime = 0.5;
    EnemyBars? _bars;
    TimeSpan _barsAt;

    void OnBars(EnemyBars b)
    {
        if (_bars is not null && b.ServerTick < _bars.ServerTick) return;
        _bars = b; _barsAt = _clock.Now;
    }

    /// <summary>The enemy's bar from the newest message, or null (no message, an old one, or the player is not an enemy shown to this player).</summary>
    public EnemyBarView? EnemyBarFor(int playerId)
    {
        if (_bars is null || (_clock.Now - _barsAt).TotalSeconds > BarsLifetime) return null;
        foreach (var e in _bars.Entries) if (e.PlayerId == playerId) return e;
        return null;
    }

    /// <summary>Whether a fresh bars message is held (the player's perks show enemy bars).</summary>
    public bool HasEnemyBars => _bars is not null && (_clock.Now - _barsAt).TotalSeconds <= BarsLifetime;
}
