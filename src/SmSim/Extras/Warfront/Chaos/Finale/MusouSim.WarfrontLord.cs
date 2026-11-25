using System.Numerics;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmSim.Extras.Musou;

/// <summary>One of the Chaos Lord's telegraphs in the duel (the wire's kind 17): the tick, the ability (1 slam, 2 charge, 3 bolt, 4 leap), where it lands and how long until it does.</summary>
public readonly record struct WarfrontLordTelegraph(uint Tick, byte Ability, Vector3 At, float Seconds)
{
    public const byte Slam = 1, Charge = 2, Bolt = 3, Leap = 4;
}

/// <summary>A round of the Lord's bolt that hit the duelist (the World applies it as a ranged hit at the start of the next tick).</summary>
public readonly record struct WarfrontLordBolt(ushort Lord, int Player, float Damage, Vector3 From);

/// <summary>
/// The Chaos Lord on the enemy pool (Chaos Warfront C5): the leash held at home (aggro 0) until the duel and freed at its start, the clean slate at a handoff, the duel's on switch the boss code reads,
/// the Lord's own boss rules (no Waaagh!, the slam's telegraph and both recoveries x1.5), his telegraphs and his bolt's hits. Every member returns at once unless the run carries the Chaos roster.
/// </summary>
public sealed partial class MusouSim
{
    readonly List<WarfrontLordTelegraph> _lordTelegraphs = new();
    readonly List<WarfrontLordBolt> _lordBolts = new();

    /// <summary>The Lord's pool id and the duelist's player id while a duel is on (0, 0: none).</summary>
    internal ushort LordDuelId { get; set; }
    internal int LordDuelist { get; set; }

    /// <summary>Telegraphs raised since the battle began, counted (the server sends each once it sees the count move; the list keeps the last 32).</summary>
    public int LordTelegraphCount { get; private set; }
    public IReadOnlyList<WarfrontLordTelegraph> LordTelegraphs => _lordTelegraphs;

    internal void AddLordTelegraph(in WarfrontLordTelegraph t)
    {
        _lordTelegraphs.Add(t);
        if (_lordTelegraphs.Count > 32) _lordTelegraphs.RemoveAt(0);
        LordTelegraphCount++;
    }

    internal void AddLordBolt(in WarfrontLordBolt b) => _lordBolts.Add(b);

    /// <summary>The bolts waiting to land (taken by the World).</summary>
    internal List<WarfrontLordBolt> LordBolts => _lordBolts;

    /// <summary>Whether the duel is on with <paramref name="id"/> as the Lord, and who the duelist is.</summary>
    internal bool WarfrontLordDuelOn(ushort id, out int duelist)
    {
        duelist = LordDuelist;
        return ChaosRoster is not null && id != 0 && id == LordDuelId && duelist > 0;
    }

    /// <summary>The leashed enemy stays at home whoever comes (aggro 0; his group's leash kept): the Lord before the duel.</summary>
    internal void WarfrontHoldHome(ushort id)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _group[i] == 0) return;
        _aggro[i] = 0f;
    }

    /// <summary>Frees the enemy of its leash for good (the Chosen at the plaza's entry, the Lord at the duel).</summary>
    internal void WarfrontFree(ushort id)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i]) return;
        _group[i] = 0;
        _aggro[i] = float.PositiveInfinity;
    }

    /// <summary>Leashes the enemy again at <paramref name="home"/> in <paramref name="group"/>, staying there whoever comes (aggro 0): the Lord between two challengers.</summary>
    internal void WarfrontLeashHome(ushort id, Vector3 home, int group)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || group <= 0) return;
        _home[i] = home;
        _group[i] = group;
        _aggro[i] = 0f;
    }

    /// <summary>The Lord's clean slate at a handoff: no stun, no script, no drive (his health untouched).</summary>
    internal void WarfrontLordSlate(ushort id)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i]) return;
        _stunLeft[i] = 0;
        Script(id, false);
        Drive(id, Vector2.Zero, 0);
    }

    /// <summary>
    /// The Lord's boss rules (the Warboss of a Chaos fortress; <see cref="MusouBoss"/>'s constructor asks): no Waaagh! (no summons, no frenzy), the slam's telegraph x<see cref="WarfrontDuelRules.SlamWindUpScale"/>
    /// and the slam's and the charge's recoveries x<see cref="WarfrontDuelRules.RecoveryScale"/>. Any other boss keeps the pool's.
    /// </summary>
    internal void WarfrontLordRules(ushort id, ref MusouBossRules rules)
    {
        int i = id - 1;
        if (Warfront is not { ChaosRoster: not null, Fortress: not null } || i < 0 || i >= Capacity || !_used[i] || _type[i] != MusouEnemyType.Warboss) return;
        rules = rules with
        {
            WaaaghAt = [],
            SlamWindUp = rules.SlamWindUp * WarfrontDuelRules.SlamWindUpScale,
            SlamRecover = rules.SlamRecover * WarfrontDuelRules.RecoveryScale,
            ChargeRecover = rules.ChargeRecover * WarfrontDuelRules.RecoveryScale,
        };
    }

    /// <summary>The telegraphs and bolts of a battle that ended are forgotten (the counts go on).</summary>
    internal void WarfrontLordReset()
    {
        _lordTelegraphs.Clear();
        _lordBolts.Clear();
        LordDuelId = 0;
        LordDuelist = 0;
    }
}
