using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmSim.Extras.Warfront;

/// <summary>
/// The events of a node battle as the stats ledger counts them (slice W9b; the W4b review's feeder table; every "who is credited" reading is an interpretation, spec "Warfront: the squad wiring"). Each method builds the
/// ledger's event id from the deployment in progress (<see cref="WarfrontStatsLedger.EventId"/>) so a repeated event counts once: an ordinary kill carries no id (the pool reports each death once), an elite or boss kill, a death,
/// a revive, a capture and a battle do. The ledger holds a human's OWN figures only (a retainer's kills are its service record's, never here). Pure; the wiring feeds it.
/// </summary>
public sealed class WarfrontLedgerFeed(WarfrontStatsLedger ledger, Func<int> deployment)
{
    public WarfrontStatsLedger Ledger { get; } = ledger;

    int Deployment => deployment();

    /// <summary>A human's own kill of an enemy of <paramref name="type"/> (pool id <paramref name="enemy"/>, World tick <paramref name="tick"/>): ordinary kills with no id, elite and boss ones once per death.</summary>
    public bool Kill(ulong key, MusouEnemyType type, int enemy, uint tick) =>
        Ledger.OnKill(key, type, WarfrontXpLedger.Classify(type) == WarfrontKillClass.Ordinary ? 0 : WarfrontStatsLedger.EventId(Deployment, enemy, (int)(tick & 0xFFFFF)));

    /// <summary>An assist (the pool reports each once).</summary>
    public bool Assist(ulong key) => Ledger.OnAssist(key);

    /// <summary>A human fell (each fall counts, revived or not [interpretation]): once per (player, tick).</summary>
    public bool Death(ulong key, int player, uint tick) => Ledger.OnDeath(key, WarfrontStatsLedger.EventId(Deployment, player, (int)(tick & 0xFFFFF)));

    /// <summary>A human revived a retainer (a revive by a retainer is its own record's): once per (player, tick).</summary>
    public bool Revive(ulong key, int player, uint tick) => Ledger.OnRevive(key, WarfrontStatsLedger.EventId(Deployment, player, (int)(tick & 0xFFFFF)));

    /// <summary>A position was secured with the human inside (D, index 3, is the final capture).</summary>
    public bool Capture(ulong key, int index) => Ledger.OnCapture(key, index == 3, WarfrontStatsLedger.EventId(Deployment, index));

    /// <summary>A human took part in the deployment (landed, flown in late, or re-bound): once per deployment.</summary>
    public bool Battle(ulong key) => Ledger.OnBattle(key, WarfrontStatsLedger.EventId(Deployment, 0));
}
