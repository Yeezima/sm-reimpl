using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmSim.Extras.Warfront;

/// <summary>
/// The last two human attackers of each Ork, for assists (slice W3b, from the W15 review): a hit records its attacker and the tick; when another one lands the kill, every recorded attacker within <see cref="WindowTicks"/> of it
/// (default 5 s at 30 ticks a second) other than the killer has one assist. Indexed by pool id; a spawn or a kill clears the id.
/// </summary>
public sealed class WarfrontAssists
{
    /// <summary>The assist window in ticks (the World sets 5 s of its tick rate).</summary>
    public uint WindowTicks { get; set; } = 150;

    readonly int[] _first = new int[MusouSim.Capacity + 1], _second = new int[MusouSim.Capacity + 1];
    readonly uint[] _firstTick = new uint[MusouSim.Capacity + 1], _secondTick = new uint[MusouSim.Capacity + 1];

    public void Hit(ushort enemy, int attacker, uint tick)
    {
        if (enemy >= _first.Length || attacker <= 0) return;
        if (_first[enemy] == attacker) { _firstTick[enemy] = tick; return; }
        (_first[enemy], _second[enemy], _firstTick[enemy], _secondTick[enemy]) = (attacker, _first[enemy], tick, _firstTick[enemy]);   // the newest first; the one before is kept (a repeat of the second moves up)
    }

    public void Clear(ushort enemy)
    {
        if (enemy >= _first.Length) return;
        _first[enemy] = _second[enemy] = 0;
        _firstTick[enemy] = _secondTick[enemy] = 0;
    }

    /// <summary>The attackers of <paramref name="enemy"/> within the window of <paramref name="tick"/> other than <paramref name="killer"/>, into <paramref name="into"/> (at most two); the record is cleared.</summary>
    public int Take(ushort enemy, int killer, uint tick, Span<int> into)
    {
        int n = 0;
        if (enemy < _first.Length)
        {
            if (_first[enemy] > 0 && _first[enemy] != killer && tick - _firstTick[enemy] <= WindowTicks) into[n++] = _first[enemy];
            if (_second[enemy] > 0 && _second[enemy] != killer && tick - _secondTick[enemy] <= WindowTicks) into[n++] = _second[enemy];
        }
        Clear(enemy);
        return n;
    }
}

/// <summary>A human credited with a kill (squad binding and stats read it): his key and player id, the enemy's pool id and type, and the World tick.</summary>
public readonly record struct WarfrontHumanKill(ulong Key, int Player, ushort Enemy, MusouEnemyType Type, uint Tick);

/// <summary>
/// W7's progression library at work in a Warfront battle (slice W3b; plan section 5): the campaign's players and XP ledger, the server-side picker, and the lookups the hooks make (the key of a player id, the cards' bonuses, the protection of a standing
/// offer, the Ork level scale). Pure: no World access here (the World side is <c>World.WarfrontProgression.cs</c>). It hangs on <see cref="WarfrontBattleRules.Progression"/>.
/// <para>
/// Until W5 supplies the real campaign and the campaign keys, it creates its own campaign and maps a player id to a key through <see cref="KeyOf"/> (default: the player id itself, joined on first sight).
/// </para>
/// </summary>
public sealed class WarfrontProgressionRuntime
{
    /// <summary>XP of a captured position A, B or C (the plan, section 5), and of D, the node's last one.</summary>
    public const int CaptureXp = 400, FinalCaptureXp = 800;
    /// <summary>The seed of the stand-in campaign (offers roll from it); W5's campaign brings the run's own.</summary>
    public const ulong StandInSeed = 0x57464F4E4F444531UL;

    readonly Dictionary<int, ulong> _keys = new();
    readonly Dictionary<ulong, int> _ids = new();
    readonly Dictionary<ulong, double> _lastKill = new();
    readonly Dictionary<ulong, int> _assists = new();
    readonly Dictionary<ulong, (int Cards, WarfrontStatBonuses Bonuses)> _bonuses = new();
    readonly HashSet<int> _present = new();
    readonly HashSet<int> _paid = new();
    readonly List<int> _newlyPaid = new();
    readonly List<int> _gone = new();
    TfwBattle? _battle;
    long _serial;

    readonly bool _ownsCampaign;

    /// <summary>Without a campaign the runtime keeps its own stand-in and joins and leaves its players itself; with one (the server's: W5) the server alone joins and leaves, and the runtime only maps ids to keys.</summary>
    public WarfrontProgressionRuntime(WarfrontCampaign? campaign = null)
    {
        _ownsCampaign = campaign is null;
        Campaign = campaign ?? new WarfrontCampaign(StandInSeed);
        Xp = new WarfrontXpLedger(Campaign);
        Picker = new WarfrontPicker(Campaign);
    }

    public WarfrontCampaign Campaign { get; }
    public WarfrontXpLedger Xp { get; }
    public WarfrontPicker Picker { get; }
    public WarfrontAssists Assists { get; } = new();

    /// <summary>Raised when a human is credited a kill (after its XP is paid).</summary>
    public Action<WarfrontHumanKill>? HumanKill { get; set; }

    /// <summary>Raised for each pick that goes through (by the player or by the timeout).</summary>
    public Action<WarfrontPickResult>? Picked { get; set; }

    readonly List<WarfrontPickResult> _picks = new();

    /// <summary>The picks taken since the last call, then cleared (a Retainer card becomes one recruit).</summary>
    public List<WarfrontPickResult> TakePicks()
    {
        var taken = new List<WarfrontPickResult>(_picks);
        _picks.Clear();
        return taken;
    }

    /// <summary>A player's pick (through the picker; records it for <see cref="TakePicks"/> and <see cref="Picked"/>).</summary>
    public bool TryPick(ulong key, int slot, int pickIndex)
    {
        if (!Picker.TryPick(key, slot, pickIndex, out var result, out _)) return false;
        Record(result);
        return true;
    }

    /// <summary>The picker's timeouts at <paramref name="now"/> (card 1 taken for each offer open too long).</summary>
    public void TickPicker(double now)
    {
        foreach (var result in Picker.Tick(now)) Record(result);
    }

    void Record(WarfrontPickResult result)
    {
        _picks.Add(result);
        Picked?.Invoke(result);
    }

    /// <summary>Co-op's say on who shares an elite or boss kill or a capture (a spectating key gets none); default: every connected player.</summary>
    public Func<ulong, bool>? Eligible { get => Xp.Eligible; set => Xp.Eligible = value; }

    /// <summary>W5 seam: the campaign key of a player id (null: no key yet, the player is not counted). Default: the id itself.</summary>
    public Func<int, ulong?> KeyOf { get; set; } = id => (ulong)id;

    /// <summary>W9 seam: the squad's size of a key, for the Retainer card's eligibility (default: the retainer cards taken plus the first Marine, or the saved squad if larger).</summary>
    public Func<ulong, int>? RosterSize { get; set; }

    /// <summary>A human's assist (the assisting player id, the killer's id (0: a squad or Guardsman), the enemy's type): the later stats ledger counts them. <see cref="AssistsOf"/> counts them per key.</summary>
    public Action<int, int, MusouEnemyType>? Assisted { get; set; }

    /// <summary>Called after XP was paid, so the level-ups it caused refill at once (the World sets it).</summary>
    internal Action? Refill { get; set; }

    /// <summary>The humans' ids with a key now.</summary>
    public IReadOnlyCollection<int> Ids => _keys.Keys;

    public bool TryKey(int id, out ulong key) => _keys.TryGetValue(id, out key);

    public bool TryId(ulong key, out int id) => _ids.TryGetValue(key, out id);

    /// <summary>Assists the player of <paramref name="key"/> has been paid this run.</summary>
    public int AssistsOf(ulong key) => _assists.GetValueOrDefault(key);

    /// <summary>
    /// The humans in the match now (player ids, not the AI Marines): a new one gets its key through <see cref="KeyOf"/> and joins the campaign (the cap is the campaign's four connected humans); one gone
    /// leaves it (its standing offer and protection with it; its pending picks stay for its return).
    /// </summary>
    public void Sync(IReadOnlyList<int> humans)
    {
        _present.Clear();
        foreach (int id in humans)
        {
            _present.Add(id);
            if (_keys.ContainsKey(id) || KeyOf(id) is not { } key) continue;
            if (_ids.TryGetValue(key, out int other) && other != id) continue;   // one live id per key
            if (_ownsCampaign && !Campaign.Join(key, out _)) continue;
            _keys[id] = key;
            _ids[key] = id;
        }
        _gone.Clear();
        foreach (int id in _keys.Keys) if (!_present.Contains(id)) _gone.Add(id);
        foreach (int id in _gone)
        {
            ulong key = _keys[id];
            _keys.Remove(id);
            _ids.Remove(key);
            _lastKill.Remove(key);
            if (_ownsCampaign) Campaign.Leave(key);
            Picker.Leave(key);
        }
    }

    // ---- XP ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An enemy died (every kill of the war's tick). Only a human's kill pays: an ordinary one its type's points to the killer, an elite or boss one every connected player once (the killer not twice). An attacker 0 (a squad's or a Guardsman's kill)
    /// or a puppet pays no XP here: the squad's kills are W9's own list. <paramref name="eventKey"/> identifies the death (tick and pool id). Every human who hit it within the assist window and did not land the kill has one assist,
    /// whoever landed it.
    /// </summary>
    public void Kill(ushort enemy, int attacker, MusouEnemyType type, int points, long eventKey, uint tick, double now, uint worldTick = 0)
    {
        Span<int> helpers = stackalloc int[2];
        int n = Assists.Take(enemy, attacker, tick, helpers);
        for (int i = 0; i < n; i++)
            if (_keys.TryGetValue(helpers[i], out ulong helperKey))
            {
                _assists[helperKey] = _assists.GetValueOrDefault(helperKey) + 1;
                Assisted?.Invoke(helpers[i], attacker, type);
            }
        if (attacker <= 0 || !_keys.TryGetValue(attacker, out ulong key)) return;
        Xp.Kill(key, points, WarfrontXpLedger.Classify(type), eventKey);
        _lastKill[key] = now;
        HumanKill?.Invoke(new WarfrontHumanKill(key, attacker, enemy, type, worldTick));
        Refill?.Invoke();
    }

    // Squad kills (attacker 0) pay no XP here: W9's SquadKill carries the class only, so the squad binding needs the type or the points (add the type to SquadKill, or read it before the damage) and pays the owner's key through Xp.Kill.

    /// <summary>Seconds since the player's last credited kill (infinite when none).</summary>
    public double SecondsSinceKill(ulong key, double now) => _lastKill.TryGetValue(key, out double at) ? now - at : double.PositiveInfinity;

    /// <summary>
    /// The battle's step changed something: a position secured (A, B, C: 400 to every connected player, once per position per battle: a position retaken and secured again pays nothing) or the node won (D: 800, once).
    /// Returns the indices paid now (0 to 2, 3 for D); the list is reused.
    /// </summary>
    public IReadOnlyList<int> OnBattleChange(TfwBattle battle, TfwBattleChange change)
    {
        SyncBattle(battle);
        _newlyPaid.Clear();
        if (battle.IsWarfrontFortress) return _newlyPaid;   // the fortress pays boss-kill and kill XP only: its waypoints are never captures, and its Won is the Warboss's death (W14b review 1)
        if (change.HasFlag(TfwBattleChange.PositionSecured))
            for (int k = 0; k < 3 && k < battle.Objectives.Count; k++)
                if (battle.Objectives[k].State == TfwPositionState.Secured && _paid.Add(k))
                {
                    Xp.Capture(CaptureXp, (_serial << 4) | (long)k);
                    _newlyPaid.Add(k);
                }
        if (change.HasFlag(TfwBattleChange.Won) && !battle.WarfrontHq && !battle.IsWarfrontFortress && _paid.Add(3))   // the HQ defence has no D (its win pays the positions still held, above, 400 each); the fortress's Won is the Warboss's death: boss-kill XP only, never D's 800 (W3b review, W14b)
        {
            Xp.Capture(FinalCaptureXp, (_serial << 4) | 3L);
            _newlyPaid.Add(3);
        }
        if (_newlyPaid.Count > 0) Refill?.Invoke();
        return _newlyPaid;
    }

    void SyncBattle(TfwBattle? battle)
    {
        if (ReferenceEquals(battle, _battle)) return;
        _battle = battle;
        _serial++;
        _paid.Clear();
        Xp.ResetEvents();
    }

    /// <summary>The battle is over (or the run reset): the per-battle XP bookkeeping goes; offers and pending picks stay.</summary>
    internal void EndBattle()
    {
        _battle = null;
        _paid.Clear();
        Xp.ResetEvents();
    }

    // ---- cards ------------------------------------------------------------------------------------------------------------------------------

    public WarfrontStatBonuses BonusesOfKey(ulong key)
    {
        var p = Campaign.Player(key);
        if (p is null) return WarfrontStatBonuses.None;
        if (_bonuses.TryGetValue(key, out var cached) && cached.Cards == p.Cards.Count) return cached.Bonuses;
        var b = WarfrontStatBonuses.For(p.Cards);
        _bonuses[key] = (p.Cards.Count, b);
        return b;
    }

    /// <summary>The cards' bonuses of player <paramref name="id"/> (none for an unknown one).</summary>
    public WarfrontStatBonuses BonusesOf(int id) => _keys.TryGetValue(id, out ulong key) ? BonusesOfKey(key) : WarfrontStatBonuses.None;

    /// <summary>The scale on the seconds of a support cooldown (the orbital strike, the drop pod, the grenade's refill): 1 minus 8 % a card, never below 40 %.</summary>
    public float CooldownScale(int id) => BonusesOf(id).CooldownScale;

    /// <summary>The +1 Retainer cards taken (W9 reads the count; nothing here spawns one).</summary>
    public int Retainers(int id) => BonusesOf(id).Retainers;

    public int RosterSizeOf(ulong key)
    {
        if (RosterSize is { } custom) return custom(key);
        var p = Campaign.Player(key);
        return p is null ? 1 : Math.Max(p.Squad.Count, 1 + BonusesOfKey(key).Retainers);
    }

    /// <summary>A standing card offer protects its player (no damage, no input) until the pick or the timeout.</summary>
    public bool IsProtected(int id) => _keys.TryGetValue(id, out ulong key) && Picker.Protected(key);

    // ---- Ork scaling ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The highest level among the connected humans (1 without any).</summary>
    public int HighestLevel()
    {
        int level = 1;
        foreach (ulong key in _ids.Keys)
            if (Campaign.Player(key) is { } p && Campaign.IsConnected(key) && p.Level > level) level = p.Level;
        return level;
    }

    /// <summary>The Orks' level multipliers at a spawn now: the level part only (the humans' factor is the battle's own).</summary>
    public (float Health, float Damage) OrkLevelScale()
    {
        var s = WarfrontOrkScaling.LevelScale(HighestLevel());
        return (s.Health, s.Damage);
    }
}
