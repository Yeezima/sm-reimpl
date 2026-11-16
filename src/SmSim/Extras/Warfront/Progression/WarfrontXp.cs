using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Campaign;

namespace SmSim.Extras.Warfront.Progression;

/// <summary>A player's level changed. The server refills that player's HP and armour for each one (owner: "On level-up, refill HP and armor").</summary>
public readonly record struct WarfrontLevelUp(ulong Key, int From, int To);

/// <summary>How an enemy's death pays XP.</summary>
public enum WarfrontKillClass { Ordinary, Elite, Boss }

/// <summary>
/// XP sources (plan section 5; owner brief). Personal: the type's own points to the killer, or, for a squad kill, to the squad's owner. Shared: elite kills, boss kills and captures pay every (connected) player
/// once per event; the event id makes a second report of the same event pay nothing, and the killer of a shared kill gets the shared amount once, never personal and shared both.
/// Player and squad advance in parallel: a retainer has no XP of its own, its owner's level is the squad's level. Levels come from <see cref="WarfrontCampaign.XpForLevel"/> and
/// <see cref="WarfrontCampaign.LevelForXp"/> (W4), never duplicated here.
/// </summary>
public sealed class WarfrontXpLedger
{
    readonly WarfrontCampaign _campaign;
    readonly HashSet<string> _events = new();
    readonly List<WarfrontLevelUp> _levelUps = new();

    public WarfrontXpLedger(WarfrontCampaign campaign) => _campaign = campaign;

    /// <summary>Every level-up so far, in order (the server drains it).</summary>
    public IReadOnlyList<WarfrontLevelUp> LevelUps => _levelUps;

    public List<WarfrontLevelUp> Drain() { var l = new List<WarfrontLevelUp>(_levelUps); _levelUps.Clear(); return l; }

    /// <summary>Forget the dedupe set (a new battle; event ids are unique within one).</summary>
    public void ResetEvents() => _events.Clear();

    /// <summary>The kill class of an enemy type: Nob, Big Shoota Nob and Scrap Mek are elite; the Warboss and the Boss Orks are boss; everything else ordinary (plan section 5; the Weirdboy stays ordinary [interpretation]).</summary>
    public static WarfrontKillClass Classify(MusouEnemyType type) =>
        type == MusouEnemyType.Warboss || MusouBossOrks.Is(type) ? WarfrontKillClass.Boss
        : type is MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.ScrapMek ? WarfrontKillClass.Elite
        : WarfrontKillClass.Ordinary;

    /// <summary>
    /// An enemy died. <paramref name="creditedKey"/>: the player who killed it, or the owner of the retainer that did (0 or unknown: nobody). <paramref name="eventId"/> identifies this one death (e.g. battle serial + enemy id).
    /// Returns the keys paid. A repeated <paramref name="eventId"/> pays nothing.
    /// </summary>
    public IReadOnlyList<ulong> Kill(ulong creditedKey, int points, WarfrontKillClass cls, string eventId)
    {
        if (points <= 0 || !_events.Add("kill:" + eventId)) return [];
        if (cls == WarfrontKillClass.Ordinary)
            return _campaign.Player(creditedKey) is not null && Grant(creditedKey, points) ? [creditedKey] : [];
        return Shared(points);
    }

    /// <summary>A capture (A/B/C 400, D 800, HQ stage held 400): every connected player once.</summary>
    public IReadOnlyList<ulong> Capture(int points, string eventId) =>
        points > 0 && _events.Add("capture:" + eventId) ? Shared(points) : [];

    List<ulong> Shared(int points)
    {
        var paid = new List<ulong>();
        foreach (var p in _campaign.Players.OrderBy(p => p.Key))
            if (_campaign.IsConnected(p.Key) && Grant(p.Key, points)) paid.Add(p.Key);
        return paid;
    }

    bool Grant(ulong key, int points)
    {
        var p = _campaign.Player(key);
        if (p is null) return false;
        int before = p.Level;
        _campaign.AddXp(key, points);
        if (p.Level > before) _levelUps.Add(new WarfrontLevelUp(key, before, p.Level));
        return true;
    }
}
