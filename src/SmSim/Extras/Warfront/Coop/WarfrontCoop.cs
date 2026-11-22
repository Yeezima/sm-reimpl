using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmSim.Extras.Warfront.Coop;

/// <summary>Where a connected human stands in the co-op rules (slice W12). The fallen player's own phase (downed, spectator) is W3's Down section, not a role.</summary>
public enum WarfrontCoopRole : byte
{
    Player = 0,
    /// <summary>A first-time key that joined while a battle was under way: body aboard, held and unharmed, camera on the fighting squad until the next ship stop.</summary>
    SpectatingJoiner = 1,
    /// <summary>On the ship: the key has not confirmed its class (the customizer, or the 90 s gate).</summary>
    AwaitClass = 2,
    /// <summary>On the ship: the class is confirmed, the catch-up cards are not all taken.</summary>
    AwaitPicks = 3,
}

public enum WarfrontWatchKind : byte { None = 0, Player = 1, Marine = 2 }

/// <summary>What a spectating client looks at: a human by player id, or a squad Marine by its id (W10's model; players only until it is merged).</summary>
public readonly record struct WarfrontWatch(WarfrontWatchKind Kind, int Id);

/// <summary>Where a leaving key stood in the battle at that moment (ruling 5): decides how it comes back.</summary>
public enum WarfrontLeaveState : byte { NotInBattle = 0, Alive = 1, Downed = 2, Spectating = 3 }

public enum WarfrontAwaitReason : byte { Class = 0, Picks = 1 }

/// <summary>One line of the leader's awaited list: a connected key the deploy waits for.</summary>
public readonly record struct WarfrontAwaited(string Name, WarfrontAwaitReason Reason, int PicksLeft);

/// <summary>The answer to a connect. <see cref="FlyIn"/>: the existing late join may land the player (alive when it left, same battle). <see cref="SpectatorSeconds"/> above 0: it comes back as a fallen player's spectator with that much of its 60 s.</summary>
public sealed record WarfrontCoopJoin(bool Admitted, string? Refusal, WarfrontJoinKind Kind, WarfrontCoopRole Role, bool FlyIn, float SpectatorSeconds);

/// <summary>What the rules need from the server and the squad system (tests use a double).</summary>
public interface IWarfrontCoopHost
{
    /// <summary>The key has a squad roster (W9 <c>SquadRosters.Has</c>).</summary>
    bool HasRoster(ulong key);
    /// <summary>Gives the key its one starting retainer (W9 <c>SquadRosters.AddOwner</c>); called only for a key without a roster.</summary>
    void AddStartingRetainer(ulong key);
    /// <summary>The owner left: its fighting squad holds where it stands (order Hold) and keeps counting as standing; it is not removed.</summary>
    void HoldSquad(ulong key);
    /// <summary>The owner is back in the same battle as <paramref name="player"/>: the squad is bound to it as it is (W9 <c>Rebind</c>; downed and out Marines stay downed and out).</summary>
    void RebindSquad(ulong key, int player);
    /// <summary>Writes the run save (every grant is followed by one).</summary>
    void Save();
}

/// <summary>What the Coop section says to one client (the wire's content).</summary>
public sealed record WarfrontCoopView(
    WarfrontCoopRole Role, WarfrontWatch Watch, bool YouLead, bool LeaderChanged, bool GrantAppliedThisStop,
    int ClassGateSecondsLeft, string LeaderName, int GrantedLevel, int PicksLeft, IReadOnlyList<WarfrontAwaited> Awaited);

/// <summary>
/// Warfront's co-op rules (slice W12; plan sections 3 and 4, owner-brief "Co-op and the Thunderhawk", designer rulings 1 to 5): join order and leadership, the returning key, the spectating joiner, the ship-stop grant,
/// the class gate, the catch-up cards, the deploy gate, the cap. Pure: no World and no sockets; the server and the run feed it connects, leaves, ticks and commands. Everything saved is in
/// <see cref="WarfrontCampaign"/> (the key in the join order, <c>JoinGrantApplied</c>, <c>ClassConfirmed</c>, <c>PicksTaken</c> against <c>Level</c>, the roster's existence); what is kept here is only the live part (roles, the 90 s clock, the watch target),
/// so a restart loses nothing a grant depends on. Nothing is granted twice: each grant checks its fact first and is followed by a save.
/// </summary>
public sealed class WarfrontCoop
{
    /// <summary>The class gate's clock (ours, an estimate: the designer's ruling 3, so a joiner cannot hold the squad in the ship).</summary>
    public const float ClassGateSeconds = 90f;
    /// <summary>How long the Coop section keeps saying "leader changed" after a change (ours).</summary>
    public const float LeaderChangedSeconds = 4f;
    public const int MaxAwaitedOnWire = 4;

    sealed class Entry
    {
        public int Player = -1;
        public WarfrontCoopRole Role;   // Player, AwaitClass or AwaitPicks; the hold is the flag below
        public bool Held;
        public bool Seen;   // this object has run Connect for the key's present connection
        public float GateElapsed;
        public WarfrontWatch Watch;
        public bool GrantThisStop;
        public WarfrontLeaveState LeftAs;
        public float LeftSpectatorSeconds;
    }

    readonly WarfrontCampaign _c;
    readonly IWarfrontCoopHost _host;
    readonly Dictionary<ulong, Entry> _e = new();
    ulong? _lastLeader;
    double _now, _leaderChangedUntil = double.NegativeInfinity;

    public WarfrontCoop(WarfrontCampaign campaign, IWarfrontCoopHost host)
    {
        _c = campaign;
        _host = host;
        HumanCap = WarfrontCampaign.MaxHumans;
        _lastLeader = campaign.Leader;
    }

    /// <summary>Connected humans allowed (<c>--warfront-max-humans</c>, at most the campaign's four).</summary>
    public int HumanCap { get; set; }

    public WarfrontCampaign Campaign => _c;
    public int LeaderChanges { get; private set; }

    public static string FullText(int cap) => $"The squad is full ({cap} of {cap})";

    Entry Of(ulong key) => _e.TryGetValue(key, out var e) ? e : _e[key] = new Entry();

    public WarfrontCoopRole RoleOf(ulong key) => _c.IsConnected(key) && _e.TryGetValue(key, out var e) ? (e.Held ? WarfrontCoopRole.SpectatingJoiner : e.Role) : WarfrontCoopRole.Player;
    public int PlayerOf(ulong key) => _e.TryGetValue(key, out var e) ? e.Player : -1;
    public ulong? KeyOf(int player)
    {
        foreach (var (k, e) in _e) if (e.Player == player && _c.IsConnected(k)) return k;
        return null;
    }

    /// <summary>Rule 4: the body stays aboard, locked and unharmed (W3b's <c>WarfrontCoopHeld</c>).</summary>
    public bool IsHeld(int player) => KeyOf(player) is { } k && RoleOf(k) == WarfrontCoopRole.SpectatingJoiner;

    /// <summary>The late join answer for a player who spawned in the Stormbird (<c>WarfrontJoinLate</c>): false while the key is a spectating joiner. Unknown players (a bot, a puppet) are allowed.</summary>
    public bool AllowsLateJoin(int player) => !IsHeld(player);

    /// <summary>Rule 4 (a spectating joiner, a key awaiting its class: none; interpretation): shared XP goes to connected Players only (a spectating joiner, a key awaiting its class earns nothing).</summary>
    public bool EarnsXp(ulong key) => _c.IsConnected(key) && RoleOf(key) is WarfrontCoopRole.Player or WarfrontCoopRole.AwaitPicks;

    /// <summary>
    /// Connects the three seams of the battle rules and the XP ledger to these rules: <c>CoopHeld</c> (W3b: locked and unharmed), <c>LateJoinAllowed</c> (a held key waits in the Stormbird) and the returned predicate for the ledger's
    /// <c>Eligible</c> (a spectating key gets no shared XP). The server calls this once per run object.
    /// </summary>
    public Func<ulong, bool> AttachTo(WarfrontBattleRules rules)
    {
        rules.CoopHeld = IsHeld;
        rules.LateJoinAllowed = AllowsLateJoin;
        return EarnsXp;
    }

    public IEnumerable<ulong> XpEarners() => _c.JoinOrder.Where(EarnsXp);

    // ---- connect, leave -----------------------------------------------------------------------------------------------------------------------

    bool BattleUnderway => _c.Phase == WarfrontPhase.Battle;
    bool Underway => _c.Phase is WarfrontPhase.Travel or WarfrontPhase.Battle;

    /// <summary>
    /// A human connects as <paramref name="player"/> (this process's player id). Rules 2 to 4 and 10: the cap (first come, first served), a returning key gets exactly its saved state (and in a battle ruling 5), a new key
    /// during a battle is a spectating joiner, a new key on the ship after the first deployment is gated at once. A key the campaign already has connected (W5a's identify path may call <c>Campaign.Join</c> first) but this object has not
    /// seen runs the whole role logic; only a key this object already connected is answered as it stands. One <see cref="WarfrontCoop"/> serves one run and one stage process: build a new one on a new run and at every stage start, then
    /// <c>Connect</c> the carried-over players.
    /// </summary>
    public WarfrontCoopJoin Connect(ulong key, string name, int player)
    {
        var e0 = Of(key);
        if (e0.Seen && _c.IsConnected(key)) { e0.Player = player; return new(true, null, _c.Player(key) is null ? WarfrontJoinKind.New : WarfrontJoinKind.Returning, RoleOf(key), false, 0f); }
        bool known = _c.Player(key) is not null;
        var kind = known ? WarfrontJoinKind.Returning : WarfrontJoinKind.New;
        if (!_c.IsConnected(key))
        {
            int connected = _c.Players.Count(x => _c.IsConnected(x.Key));
            if (connected >= HumanCap || !_c.Join(key, name, out kind)) return Refused(kind, FullText(HumanCap));
        }
        else if (known)
        {
            var q = _c.Player(key)!;
            kind = q.JoinGrantApplied || q.PicksTaken > 0 || q.Xp > 0 ? WarfrontJoinKind.Returning : WarfrontJoinKind.New;   // joined through the campaign before this call
        }
        var e = e0;
        e.Seen = true;
        e.Player = player;
        e.GateElapsed = 0f;
        e.Watch = default;
        e.GrantThisStop = false;
        var p = _c.Player(key)!;
        bool flyIn = false;
        float spectate = 0f;
        e.Held = false;

        if (BattleUnderway)
        {
            _host.RebindSquad(key, player);   // a key with a squad in this battle: the squad is the same as it was, downed and out Marines stay so
            if (e.LeftAs == WarfrontLeaveState.Alive) { flyIn = true; e.Role = WarfrontCoopRole.Player; }
            else if (e.LeftAs is WarfrontLeaveState.Downed or WarfrontLeaveState.Spectating)
            {
                spectate = e.LeftSpectatorSeconds > 0f ? e.LeftSpectatorSeconds : WarfrontBattleRules.SpectatorSeconds;   // never a free life
                e.Role = WarfrontCoopRole.Player;
            }
            else if (Gated(p)) e.Held = true;   // a first-time joiner (of this battle or an earlier stop) who has not confirmed: held aboard until the ship stop
            else { flyIn = true; e.Role = WarfrontCoopRole.Player; }   // a confirmed key that was not in this battle: its own saved state, flown in with its squad
        }
        else if (Underway) e.Held = Gated(p);   // Travel: a gated key is held (a new key, or one that never confirmed)
        else if (_c.Phase == WarfrontPhase.Ship)
        {
            if (Gated(p))
            {
                GrantAtStop(key, p, e, null);   // rule 3, at once: this is a ship stop
                e.Role = WarfrontCoopRole.AwaitClass;
                e.GateElapsed = 0f;
            }
            else e.Role = p.PendingPicks > 0 ? WarfrontCoopRole.AwaitPicks : WarfrontCoopRole.Player;   // derived: every confirmed key aboard with picks pending, founders too
        }
        e.LeftAs = WarfrontLeaveState.NotInBattle;
        e.LeftSpectatorSeconds = 0f;
        NoteLeader();
        return new(true, null, kind, RoleOf(key), flyIn, spectate);
    }

    static WarfrontCoopJoin Refused(WarfrontJoinKind kind, string text) => new(false, text, kind, WarfrontCoopRole.Player, false, 0f);

    /// <summary>True for a key whose class gate (or catch-up) is still to pass: not a founder, not yet confirmed.</summary>
    static bool Gated(WarfrontPlayerState p) => !p.ClassConfirmed;

    /// <summary>Rule 5 (and 9): the one catch-up step of a key, once: the join flag (no XP grant since 2026-11-22: a new key starts at level 1), the starting retainer for a key without a roster; each fact checked first, then a save.</summary>
    void GrantAtStop(ulong key, WarfrontPlayerState p, Entry e, ICollection<ulong>? ignore)
    {
        bool changed = !p.JoinGrantApplied;
        if (_c.ApplyJoinGrant(key, ignore)) e.GrantThisStop = true;
        if (!_host.HasRoster(key)) { _host.AddStartingRetainer(key); changed = true; }
        if (changed) _host.Save();
    }

    /// <summary>The key's connection ended. <paramref name="state"/> is where it stood in the battle (ruling 5); <paramref name="spectatorSecondsLeft"/> the rest of a spectating key's 60 s.</summary>
    public void Left(ulong key, WarfrontLeaveState state = WarfrontLeaveState.NotInBattle, float spectatorSecondsLeft = 0f)
    {
        if (!_c.IsConnected(key)) return;
        var e = Of(key);
        bool wasSpectatingJoiner = e.Held;
        if (BattleUnderway)
        {
            e.LeftAs = wasSpectatingJoiner ? WarfrontLeaveState.NotInBattle : state;
            e.LeftSpectatorSeconds = state == WarfrontLeaveState.Spectating ? spectatorSecondsLeft : state == WarfrontLeaveState.Downed ? WarfrontBattleRules.SpectatorSeconds : 0f;
            _host.HoldSquad(key);   // ruling 4: the squad stays and holds
        }
        else e.LeftAs = WarfrontLeaveState.NotInBattle;
        _c.Leave(key);
        e.Seen = false;
        e.Watch = default;
        NoteLeader();
    }

    // ---- leader -----------------------------------------------------------------------------------------------------------------------------

    public ulong? Leader => _c.Leader;
    public string LeaderName => _c.Leader is { } k ? _c.Player(k)?.Name ?? "" : "";
    public bool LeaderChangedNow => _now < _leaderChangedUntil;

    void NoteLeader()
    {
        var now = _c.Leader;
        if (now != _lastLeader)
        {
            if (_lastLeader is not null && now is not null) { LeaderChanges++; _leaderChangedUntil = _now + LeaderChangedSeconds; }
            _lastLeader = now;
        }
    }

    // ---- ship stop, the class gate, picks -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Rule 5: the battle is resolved and everyone is back aboard. Every connected gated key (the spectating joiners) gets its one grant, the starting retainer if it has no roster, and the AwaitClass role with a fresh 90 s clock.
    /// All the grants of one stop read the same figures: a median never counts another joiner whose own grant is still to come.
    /// </summary>
    public void ShipStop()
    {
        if (_c.IsOver) throw new InvalidOperationException("the run is over: build a new WarfrontCoop for a new run (and at every stage start), then Connect the carried-over players");
        var pending = new HashSet<ulong>(_c.Players.Where(p => Gated(p) && !p.JoinGrantApplied).Select(p => p.Key));
        foreach (var k in _c.JoinOrder)
        {
            var e = Of(k);
            e.LeftAs = WarfrontLeaveState.NotInBattle;
            e.GrantThisStop = false;
            e.Held = false;
            if (!_c.IsConnected(k)) continue;
            var p = _c.Player(k)!;
            if (Gated(p))
            {
                GrantAtStop(k, p, e, pending);
                e.Role = WarfrontCoopRole.AwaitClass;
                e.GateElapsed = 0f;
            }
            else e.Role = p.PendingPicks > 0 ? WarfrontCoopRole.AwaitPicks : WarfrontCoopRole.Player;   // derived: a confirmed key aboard with picks pending, founders too
            e.Watch = default;
        }
    }

    /// <summary>Rule 6: the client closed the customizer after opening it in AwaitClass. Only in AwaitClass; sets the flag, saves; AwaitPicks if picks are pending, else Player.</summary>
    public bool ConfirmClass(ulong key)
    {
        if (!_c.IsConnected(key) || _c.Player(key) is not { } p || RoleOf(key) != WarfrontCoopRole.AwaitClass) return false;
        if (!p.ClassConfirmed) { p.ClassConfirmed = true; _host.Save(); }
        Of(key).Role = p.PendingPicks > 0 ? WarfrontCoopRole.AwaitPicks : WarfrontCoopRole.Player;
        return true;
    }

    /// <summary>Advances the clocks: the 90 s class gate confirms a key that did not (ruling 3, only connected keys count down), and a key whose picks are all taken becomes a Player (rule 7). Call every server tick or at least once a second.</summary>
    public void Tick(double dt)
    {
        _now += dt;
        foreach (var k in _c.JoinOrder)
        {
            if (!_c.IsConnected(k) || !_e.TryGetValue(k, out var e)) continue;
            if (e.Role == WarfrontCoopRole.AwaitClass)
            {
                e.GateElapsed += (float)dt;
                if (e.GateElapsed >= ClassGateSeconds) ConfirmClass(k);
            }
            int pending = _c.Player(k)?.PendingPicks ?? 0;
            if (e.Role == WarfrontCoopRole.AwaitPicks && pending <= 0) e.Role = WarfrontCoopRole.Player;
            else if (e.Role == WarfrontCoopRole.Player && !e.Held && pending > 0 && _c.Phase == WarfrontPhase.Ship && _c.Player(k)!.ClassConfirmed) e.Role = WarfrontCoopRole.AwaitPicks;   // picks queued aboard: derived, no saved flag
        }
    }

    /// <summary>Call after a card is taken (the picker's result or timeout): re-evaluates AwaitPicks at once.</summary>
    public void PicksChanged(ulong key)
    {
        if (_e.TryGetValue(key, out var e) && e.Role == WarfrontCoopRole.AwaitPicks && (_c.Player(key)?.PendingPicks ?? 0) <= 0) e.Role = WarfrontCoopRole.Player;
    }

    public int ClassGateSecondsLeft(ulong key) => RoleOf(key) == WarfrontCoopRole.AwaitClass ? Math.Max(0, (int)MathF.Ceiling(ClassGateSeconds - Of(key).GateElapsed)) : 0;

    /// <summary>The connected keys the deploy waits for, in join order.</summary>
    public IReadOnlyList<WarfrontAwaited> Awaited()
    {
        var list = new List<WarfrontAwaited>();
        foreach (var k in _c.JoinOrder)
        {
            if (!_c.IsConnected(k)) continue;
            var p = _c.Player(k)!;
            var role = RoleOf(k);
            if (role == WarfrontCoopRole.AwaitClass) list.Add(new(NameOf(p), WarfrontAwaitReason.Class, p.PendingPicks));
            else if (role == WarfrontCoopRole.AwaitPicks) list.Add(new(NameOf(p), WarfrontAwaitReason.Picks, p.PendingPicks));
        }
        return list;
    }

    static string NameOf(WarfrontPlayerState p) => p.Name.Length > 0 ? p.Name : "Player";

    /// <summary>
    /// Rule 8: null when the squad may deploy; else the status line naming who is awaited. A gated player who disconnects stops blocking and resumes the same role on return.
    /// </summary>
    public string? DeployRefusal()
    {
        var awaited = Awaited();
        if (awaited.Count == 0) return null;
        string Part(WarfrontAwaited a) => a.Reason == WarfrontAwaitReason.Class ? $"{a.Name} (class)" : $"{a.Name} ({a.PicksLeft} {(a.PicksLeft == 1 ? "card" : "cards")})";
        return "Waiting for " + string.Join(", ", awaited.Select(Part));
    }

    // ---- watching ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The watch target of a spectating key (a spectating joiner; the fallen player's own spectating is W3's): the current one while it is still a candidate, else the leader (a human), else the first candidate.
    /// <paramref name="candidates"/>: living humans then standing Marines, in a fixed order (W10's model adds the Marines).
    /// </summary>
    public WarfrontWatch ResolveWatch(ulong key, IReadOnlyList<WarfrontWatch> candidates)
    {
        var e = Of(key);
        if (candidates.Contains(e.Watch)) return e.Watch;
        e.Watch = default;
        if (_c.Leader is { } l && l != key && _e.TryGetValue(l, out var le) && le.Player >= 0)
        {
            var lead = new WarfrontWatch(WarfrontWatchKind.Player, le.Player);
            if (candidates.Contains(lead)) return e.Watch = lead;
        }
        return e.Watch = candidates.Count > 0 ? candidates[0] : default;
    }

    /// <summary>Watch step -1 or +1 through the candidates (wraps). A target that is not a candidate steps from the leader's place. Returns the new target.</summary>
    public WarfrontWatch Step(ulong key, int step, IReadOnlyList<WarfrontWatch> candidates)
    {
        if (candidates.Count == 0) return Of(key).Watch = default;
        var cur = ResolveWatch(key, candidates);
        int at = 0;
        for (int i = 0; i < candidates.Count; i++) if (candidates[i] == cur) at = i;
        int n = candidates.Count;
        at = ((at + (step < 0 ? -1 : 1)) % n + n) % n;
        return Of(key).Watch = candidates[at];
    }

    /// <summary>The watchable targets in their fixed order: the living humans (ids ascending), then the standing Marines (slot order). Empty when everyone is down: the watch is then None and the client shows the own body.</summary>
    public static List<WarfrontWatch> Candidates(IEnumerable<int> livingHumans, IEnumerable<int> standingMarines)
    {
        var list = new List<WarfrontWatch>();
        foreach (int id in livingHumans.OrderBy(i => i)) list.Add(new WarfrontWatch(WarfrontWatchKind.Player, id));
        foreach (int i in standingMarines) list.Add(new WarfrontWatch(WarfrontWatchKind.Marine, i));
        return list;
    }

    /// <summary>
    /// Once a tick, the default watch (owner's rule, fallen-watch fix): a key that is spectating (a held joiner, or a fallen player in the 60 s spectator phase) always has a target, the leader else the first candidate, without waiting for the first
    /// watch step; a key that is not spectating has none (a stale target never outlives the spectating). Returns whether the key's target changed.
    /// </summary>
    public bool DefaultWatch(ulong key, bool spectating, IReadOnlyList<WarfrontWatch> candidates)
    {
        if (!_e.TryGetValue(key, out var e)) return false;
        var before = e.Watch;
        if (spectating) ResolveWatch(key, candidates); else e.Watch = default;
        return e.Watch != before;
    }

    public WarfrontWatch WatchOf(ulong key) => _e.TryGetValue(key, out var e) ? e.Watch : default;

    // ---- the wire's content -----------------------------------------------------------------------------------------------------------------

    /// <summary>What the Coop section says to <paramref name="key"/> now.</summary>
    public WarfrontCoopView ViewFor(ulong key)
    {
        var p = _c.Player(key);
        var e = _e.GetValueOrDefault(key);
        var role = RoleOf(key);
        return new WarfrontCoopView(role, e?.Watch ?? default, _c.Leader == key, LeaderChangedNow, e?.GrantThisStop == true, ClassGateSecondsLeft(key), LeaderName,
            p?.Level ?? 1, p?.PendingPicks ?? 0, Awaited().Take(MaxAwaitedOnWire).ToArray());
    }
}
