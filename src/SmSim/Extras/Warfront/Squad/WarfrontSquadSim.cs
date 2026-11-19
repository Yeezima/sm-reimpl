using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// Warfront's lightweight squad (slice W9; owner, 2026-11-17: "All retainers use a lightweight squad system: 9 per player, 36 in all ... simpler rule-driven combat"). A Marine here is an
/// agent, not a World player: a place on the ground, a health and armour, a state, an order. A battle is <see cref="BeginBattle"/>, a <see cref="Step"/> per tick, <see cref="EndBattle"/>.
/// Orks fight it through <see cref="AppendTargets"/> (their targets, ids <see cref="SquadRules.TargetId"/>) and <see cref="Hit"/> (an attack that landed); the squad fights back by
/// <see cref="ISquadArena"/>. Orders are Musou Mode's Follow, Hold and Attack (<see cref="Order"/>). A Marine at 0 health is downed for 20 s (any player, or a squad-mate when no Ork is
/// within 6 m, revives it at full health), else out for the battle only. <see cref="AnyStanding"/> answers W3's spectator and wipe rule. Deterministic: agents in index order, every roll a
/// hash of the tick and the Marine. Label: provisional approximation of player combat (no recoil, perks as numbers).
/// </summary>
public sealed partial class WarfrontSquadSim
{
    const int Max = SquadRules.MaxTotal;

    sealed partial class Agent
    {
        public RosterMarine Marine = null!;
        public int Owner, OwnerSlot, Slot;   // Owner: the player id this battle; OwnerSlot: the fighting slot 0 to 3
        public ulong Key;
        public SquadState State;
        public Vector3 Pos, HoldAt;
        public float Yaw, Health, Armour, HealthMax, ArmourMax, ReviveProgress;
        public int DownedLeft;                  // ticks
        public uint NextShot;
        public ushort Target;
        public uint TargetSince;                // the tick the current target was chosen (S1: sticky targeting)
        public int DutyMate = -1, DutyHuman = int.MinValue, Attendant = -1, ReviverMarine = -1, ReviverHuman = -1;
        public bool Progressed;
    }

    struct DamageNote { public int M0, M1; public uint T0, T1; }

    readonly SquadRosters _rosters;
    readonly ISquadArena _arena;
    readonly Agent[] _a = new Agent[Max];
    readonly SquadOrder[] _order = new SquadOrder[SquadRules.MaxOwners];
    readonly Vector3?[] _point = new Vector3?[SquadRules.MaxOwners];
    readonly int[] _lastSequence = [-1, -1, -1, -1];
    readonly ulong[] _key = new ulong[SquadRules.MaxOwners];
    readonly int[] _player = new int[SquadRules.MaxOwners];
    readonly bool[] _used = new bool[SquadRules.MaxOwners];
    readonly List<Vector3>?[] _path = new List<Vector3>?[SquadRules.MaxOwners];
    readonly uint[] _pathBuilt = new uint[SquadRules.MaxOwners];
    readonly Vector3[] _pathTo = new Vector3[SquadRules.MaxOwners];
    readonly float[] _damageScale = [1f, 1f, 1f, 1f], _healthScale = [1f, 1f, 1f, 1f], _armourScale = [1f, 1f, 1f, 1f];
    readonly Dictionary<ushort, DamageNote> _notes = [];
    readonly Dictionary<int, int> _humanAttendant = [];
    readonly Dictionary<int, (float Progress, uint Touched)> _humanProgress = [];
    readonly List<SquadKill> _kills = [];
    readonly List<SquadRevive> _revives = [], _pendingRevives = [];
    readonly List<SquadDown> _downs = [], _pendingDowns = [];
    readonly ushort[] _seen = new ushort[4];
    readonly ushort[] _one = new ushort[1];
    uint _tick;
    bool _inStep;
    // the search budget (W9b bench item): one path or route search a tick for the whole system, round robin over the squads, a failed one remembered per squad
    bool _searched;
    int _searchCursor;
    readonly bool[] _failed = new bool[SquadRules.MaxOwners], _pathPartial = new bool[SquadRules.MaxOwners];
    readonly uint[] _failTick = new uint[SquadRules.MaxOwners];
    readonly Vector3[] _failOwner = new Vector3[SquadRules.MaxOwners];
    readonly bool[] _stranded = new bool[SquadRules.MaxOwners];
    readonly uint[] _strandedSince = new uint[SquadRules.MaxOwners];
    uint _lastRelocate;
    bool _relocated;

    /// <summary>Path and route searches made so far (this sim's own count; the arena's counters say the same).</summary>
    public int SearchesMade { get; private set; }
    /// <summary>Squads relocated by the stranded failsafe so far.</summary>
    public int Relocations { get; private set; }
    /// <summary>The stranded failsafe's log seam (W9b): one record per Marine brought to its owner; the wiring writes them to the stuck log.</summary>
    public Action<SquadStuck>? StuckSink { get; set; }

    /// <summary>Takes this tick's one search; false when it was already taken.</summary>
    bool ClaimSearch()
    {
        if (_searched) return false;
        _searched = true; SearchesMade++;
        return true;
    }

    public WarfrontSquadSim(SquadRosters rosters, ISquadArena arena)
    {
        _rosters = rosters;
        _arena = arena;
        for (int i = 0; i < Max; i++) _a[i] = new Agent();
    }

    /// <summary>The rosters this sim fights from.</summary>
    public SquadRosters Rosters => _rosters;

    /// <summary>Called when a Marine finishes reviving a fallen human (W3's <c>WarfrontRevive</c>); true when the revive took.</summary>
    public Func<int, bool>? ReviveHuman { get; set; }

    /// <summary>Kills by squad Marines this tick (cleared by each <see cref="Step"/>).</summary>
    public IReadOnlyList<SquadKill> Kills => _kills;
    /// <summary>Revives completed this tick.</summary>
    public IReadOnlyList<SquadRevive> Revives => _revives;
    /// <summary>Marines knocked down this tick.</summary>
    public IReadOnlyList<SquadDown> Downs => _downs;

    float Dt => 1f / Math.Max(1, _arena.TickRate);

    /// <summary>Marine index of the fighting player <paramref name="owner"/>'s roster slot <paramref name="slot"/>; -1 when the owner has no roster or the slot is out of range.</summary>
    public int IndexOf(int owner, int slot)
    {
        int s = SlotOfPlayer(owner);
        return s >= 0 && slot >= 0 && slot < SquadRules.MaxPerPlayer ? s * SquadRules.MaxPerPlayer + slot : -1;
    }

    readonly Dictionary<ulong, (float Damage, float Health, float Armour)> _scale = [];

    /// <summary>
    /// A campaign key's damage and health multipliers for its squad (W7's stat share), set before or after it lands: damage applies at once, health to Marines landed afterwards. 1 and 1 by default.
    /// </summary>
    public void SetScale(ulong key, float damage, float health, float armour = 1f)
    {
        _scale[key] = (Math.Max(0f, damage), Math.Max(0.01f, health), Math.Max(0.01f, armour));
        int s = SlotOfKey(key);
        if (s >= 0) { _damageScale[s] = _scale[key].Damage; _healthScale[s] = _scale[key].Health; _armourScale[s] = _scale[key].Armour; }
    }

    /// <summary>The owner levelled up (tuning ruling T3): every standing Marine of <paramref name="key"/> is back at full health and armour; downed and out ones are unchanged. The number refilled.</summary>
    public int Refill(ulong key)
    {
        int n = 0;
        foreach (var a in _a)
            if (a.State == SquadState.Standing && a.Key == key) { a.Health = a.HealthMax; a.Armour = a.ArmourMax; n++; }
        return n;
    }

    // ---- battle

    int SlotOfPlayer(int player) { for (int i = 0; i < SquadRules.MaxOwners; i++) if (_used[i] && _player[i] == player) return i; return -1; }

    int SlotOfKey(ulong key) { for (int i = 0; i < SquadRules.MaxOwners; i++) if (_used[i] && _key[i] == key) return i; return -1; }

    /// <summary>The fighting slot (0 to 3) of the player id bound this battle; -1 with none.</summary>
    public int FightingSlotOf(int player) => SlotOfPlayer(player);

    Vector3 Snap(Vector3 p) => _arena.Ground(p, out var g) ? g : p;

    /// <summary>
    /// A new battle: every earlier state is dropped, then each entry lands as <see cref="Land"/> does (at most four squads fight at once). The number of Marines landed.
    /// </summary>
    public int BeginBattle(ReadOnlySpan<(ulong Key, int Player, Vector3 Landing)> landings)
    {
        foreach (var a in _a) { a.State = SquadState.Free; a.Attendant = -1; }
        Array.Clear(_used);
        for (int i = 0; i < SquadRules.MaxOwners; i++) _path[i] = null;
        _notes.Clear(); _humanAttendant.Clear(); _humanProgress.Clear(); _kills.Clear(); _revives.Clear(); _downs.Clear(); _pendingDowns.Clear(); _pendingRevives.Clear();
        ClassReset();   // W11
        int landed = 0;
        foreach (var (key, player, landing) in landings) landed += Land(key, player, landing);
        return landed;
    }

    /// <summary>
    /// Lands the roster of campaign key <paramref name="key"/> in a ring round <paramref name="landing"/>, bound to <paramref name="player"/> (this battle's player id: it changes at every reconnect, the key
    /// does not), standing at full health, order Follow, without touching any other squad. A key already fighting is re-bound and its Marines landed again (a returning player). Returns the number landed:
    /// 0 for no roster, a roster on hold, or four other squads already fighting.
    /// </summary>
    public int Land(ulong key, int player, Vector3 landing)
    {
        if (!_rosters.Has(key) || _rosters.IsHeld(key)) return 0;
        int os = SlotOfKey(key);
        if (os < 0) { for (int i = 0; i < SquadRules.MaxOwners; i++) if (!_used[i]) { os = i; break; } }
        if (os < 0) return 0;
        _used[os] = true; _key[os] = key; _player[os] = player;
        _order[os] = SquadOrder.Follow; _point[os] = null; _lastSequence[os] = -1; _path[os] = null;
        (_damageScale[os], _healthScale[os], _armourScale[os]) = _scale.TryGetValue(key, out var sc) ? sc : (1f, 1f, 1f);
        int landed = 0;
        for (int slot = 0; slot < SquadRules.MaxPerPlayer; slot++)
        {
            var a = _a[os * SquadRules.MaxPerPlayer + slot];
            a.State = SquadState.Free; a.Attendant = -1;
            var m = _rosters.Marine(key, slot);
            if (m is null) continue;
            float angle = slot * 2.4f, radius = SquadRules.LandRadius + slot * SquadRules.LandRadiusStep;
            a.Marine = m; a.Owner = player; a.Key = key; a.OwnerSlot = os; a.Slot = slot; a.State = SquadState.Standing;
            a.Pos = Snap(landing + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f));
            a.HoldAt = a.Pos; a.Yaw = 0f;
            a.HealthMax = Math.Max(1f, m.Loadout.HealthMax * _healthScale[os]); a.ArmourMax = Math.Max(0f, m.Loadout.ArmourMax * _armourScale[os]);
            a.Health = a.HealthMax; a.Armour = a.ArmourMax;
            a.DownedLeft = 0; a.ReviveProgress = 0f; a.NextShot = 0; a.Target = 0; a.DutyMate = -1; a.DutyHuman = int.MinValue; a.Attendant = -1; a.ReviverMarine = -1; a.ReviverHuman = -1; a.Progressed = false;
            ClassLand(a);   // W11: pack power, no flight
            landed++;
        }
        return landed;
    }

    /// <summary>
    /// A returning player's new id for a squad already fighting (a mid-battle reconnect): only the binding changes; positions, health, armour, downed state and orders stay. False when the key is not fighting.
    /// <see cref="Land"/> is for real landings (it stands the Marines up afresh).
    /// </summary>
    public bool Rebind(ulong key, int player)
    {
        int os = SlotOfKey(key);
        if (os < 0) return false;
        _player[os] = player;
        for (int slot = 0; slot < SquadRules.MaxPerPlayer; slot++) { var a = _a[os * SquadRules.MaxPerPlayer + slot]; if (a.State != SquadState.Free) a.Owner = player; }
        return true;
    }

    /// <summary>
    /// The battle is over: every Marine still standing has survived it (service record), every other one (downed or out) comes back next battle (no permanent death). All agents are released.
    /// The number that survived.
    /// </summary>
    public int EndBattle()
    {
        int survived = 0;
        foreach (var a in _a)
        {
            if (a.State == SquadState.Standing) { a.Marine.Record.BattlesSurvived++; survived++; }
            a.State = SquadState.Free; a.Attendant = -1;
        }
        _notes.Clear(); _humanAttendant.Clear(); _humanProgress.Clear();
        ClassReset();   // W11
        Array.Clear(_used);
        return survived;
    }

    // ---- who stands (W3's wipe and respawn rule)

    /// <summary>Whether any Marine stands (W3's <c>AnyRetainerStanding</c>): not downed, not out.</summary>
    public bool AnyStanding { get { foreach (var a in _a) if (a.State == SquadState.Standing) return true; return false; } }

    /// <summary>Whether any Marine is downed with its window open (not counted as standing; shown so the wipe rule can be told apart from a lull).</summary>
    public bool AnyReviveWindowOpen { get { foreach (var a in _a) if (a.State == SquadState.Downed) return true; return false; } }

    /// <summary>Marines standing in <paramref name="owner"/>'s squad.</summary>
    public int StandingOf(int player) { int n = 0; foreach (var a in _a) if (a.State == SquadState.Standing && a.Owner == player) n++; return n; }

    /// <summary>The state of Marine <paramref name="index"/>.</summary>
    public SquadState StateOf(int index) => index is >= 0 and < Max ? _a[index].State : SquadState.Free;

    /// <summary>A Marine for the wire and the tests; null for an index out of range or a Marine not in this battle.</summary>
    public SquadMarineView? View(int index)
    {
        if (index is < 0 or >= Max || _a[index].State == SquadState.Free) return null;
        var a = _a[index];
        return new SquadMarineView(index, a.Key, a.Slot, a.State, a.Pos, a.Yaw, a.HealthMax > 0 ? Math.Clamp(a.Health / a.HealthMax, 0f, 1f) : 0f, a.State == SquadState.Downed ? a.DownedLeft * Dt : 0f,
            a.FiredAt != 0 && _tick >= a.FiredAt && _tick - a.FiredAt < (uint)FireHoldTicks, a.Target != 0 ? a.Target : -1,
            a.State == SquadState.Downed ? Math.Clamp(a.ReviveProgress / (a.ReviverHuman >= 0 ? SquadRules.PlayerReviveSeconds : SquadRules.MarineReviveSeconds), 0f, 1f) : 0f,
            a.Air);
    }

    /// <summary>Every Marine in this battle as the wire shows it, in index order; the number written.</summary>
    public int CopyTo(Span<SquadMarineView> into)
    {
        int n = 0;
        for (int i = 0; i < Max && n < into.Length; i++) if (View(i) is { } v) into[n++] = v;
        return n;
    }

    // ---- orders

    /// <summary>
    /// A player's order for its own squad (W3's <c>OrderSink</c>): Follow, Hold or Attack as Musou Mode's fireteam orders. A Hold with a point holds that place (each Marine a place round it),
    /// else every Marine holds where it stands now; an Attack with a point goes there and fights, else it fights what it sees (40 m) while following. A resent sequence and any other command are
    /// ignored (false).
    /// </summary>
    public bool Order(int player, TfwCommandRecord command)
    {
        int os = SlotOfPlayer(player);
        if (os < 0 || command.Command is not (TfwCommand.Follow or TfwCommand.Hold or TfwCommand.Attack)) return false;
        if (_lastSequence[os] == command.Sequence) return false;
        _lastSequence[os] = command.Sequence;
        var order = command.Command switch { TfwCommand.Hold => SquadOrder.Hold, TfwCommand.Attack => SquadOrder.Attack, _ => SquadOrder.Follow };
        bool place = command.Point != default && order != SquadOrder.Follow;
        _order[os] = order;
        _point[os] = place ? command.Point : null;
        for (int slot = 0; slot < SquadRules.MaxPerPlayer; slot++)
        {
            var a = _a[os * SquadRules.MaxPerPlayer + slot];
            if (a.State == SquadState.Free) continue;
            a.HoldAt = place && order == SquadOrder.Hold ? command.Point + Ring(slot, 1f, 0.35f) : a.Pos;
        }
        return true;
    }

    /// <summary>The order of <paramref name="owner"/>'s squad (Follow without a roster).</summary>
    public SquadOrder OrderOf(int player) => SlotOfPlayer(player) is var s and >= 0 ? _order[s] : SquadOrder.Follow;

    // ---- the Orks' side

    /// <summary>The standing Marines as the Orks see them (ids <see cref="SquadRules.TargetId"/>), written into <paramref name="into"/>; the number written.</summary>
    public int AppendTargets(Span<MusouTarget> into)
    {
        int n = 0;
        for (int i = 0; i < Max && n < into.Length; i++)
            if (_a[i].State == SquadState.Standing) into[n++] = new MusouTarget(SquadRules.TargetId(i), _a[i].Pos, true, SquadRules.BodyRadius);
        return n;
    }

    /// <summary>
    /// An Ork attack that landed on squad target <paramref name="targetId"/> (the hook line in World: an attack whose player is in the squad range): armour first, then health; at 0 the Marine is
    /// downed for 20 s. False for another id or a Marine not standing.
    /// </summary>
    public bool Hit(int targetId, float damage, Vector3 from)
    {
        int i = SquadRules.IndexOfTarget(targetId);
        if (i < 0 || _a[i].State != SquadState.Standing || !(damage > 0f)) return false;
        var a = _a[i];
        damage *= SquadRules.DamageTaken;
        a.SinceHit = 0f;   // W11: regeneration waits after a hit
        float soak = Math.Min(a.Armour, damage);
        a.Armour -= soak; a.Health -= damage - soak;
        if (a.Health <= 0f) GoDown(i);
        return true;
    }

    void GoDown(int i)
    {
        var a = _a[i];
        a.Health = 0f; a.State = SquadState.Downed; a.Target = 0; a.ReviveProgress = 0f; a.Attendant = -1; a.ReviverMarine = a.ReviverHuman = -1;
        a.DownedLeft = Math.Max(1, (int)MathF.Round(SquadRules.DownedSeconds * _arena.TickRate));
        a.DutyMate = -1; a.DutyHuman = int.MinValue;
        a.Marine.Record.TimesDowned++;
        ClassDown(a);   // W11: out of the air
        _pendingDowns.Add(new SquadDown(a.Key, a.Slot));   // reported by the next Step (a Hit comes before it)
    }

    /// <summary>Revives a downed Marine at full health and armour (W3/W12 route a player's hold here); false when it is not downed.</summary>
    public bool Revive(int index, int byHuman = -1)
    {
        if (index is < 0 or >= Max || _a[index].State != SquadState.Downed) return false;
        DoRevive(index, byHuman, -1);
        return true;
    }

    void DoRevive(int index, int byHuman, int byMarine)
    {
        var a = _a[index];
        a.State = SquadState.Standing; a.Health = a.HealthMax; a.Armour = a.ArmourMax; a.DownedLeft = 0; a.ReviveProgress = 0f; a.Attendant = -1; a.Target = 0;
        a.HoldAt = a.Pos;
        if (byMarine >= 0) _a[byMarine].Marine.Record.Revives++;
        (_inStep ? _revives : _pendingRevives).Add(new SquadRevive(byHuman, byMarine, a.Key, a.Slot, -1));
    }

    /// <summary>Marines standing within <paramref name="radius"/> of a secured point each score an objective (W3 calls it when a point is secured). The number credited.</summary>
    public int CreditObjective(Vector3 point, float radius)
    {
        int n = 0;
        foreach (var a in _a)
            if (a.State == SquadState.Standing && Flat(a.Pos - point) <= radius) { a.Marine.Record.Objectives++; n++; }
        return n;
    }

    /// <summary>A human killed an Ork the squad had hurt: those Marines share an assist.</summary>
    public void NoteHumanKill(ushort enemy) => PayAssists(enemy, -1);

    // ---- the tick

    /// <summary>One tick: downed timers and revives, then every standing Marine in index order (target, duty, move, fire), then separation.</summary>
    public void Step(uint tick, ReadOnlySpan<SquadHuman> humans)
    {
        _tick = tick;
        _kills.Clear();
        _revives.Clear(); _revives.AddRange(_pendingRevives); _pendingRevives.Clear();
        _downs.Clear(); _downs.AddRange(_pendingDowns); _pendingDowns.Clear();
        float dt = Dt;
        _inStep = true;
        _searched = false;
        _arena.Tick();
        RefreshPaths(humans);
        RelocateStranded(humans);
        ClassTick(humans);   // W11
        BeginDowned(humans, dt);
        for (int i = 0; i < Max; i++) if (_a[i].State == SquadState.Standing) StepAgent(i, humans, dt);
        EndDowned(humans);
        _inStep = false;
        Separate();
        if ((tick & 127) == 0) Prune();
    }

    void BeginDowned(ReadOnlySpan<SquadHuman> humans, float dt)
    {
        for (int d = 0; d < Max; d++)
        {
            var a = _a[d];
            if (a.State != SquadState.Downed) continue;
            a.Progressed = false;
            if (--a.DownedLeft <= 0) { a.State = SquadState.Out; a.Attendant = -1; a.ReviverMarine = a.ReviverHuman = -1; continue; }
            foreach (var h in humans)
            {
                if (!h.Alive || h.ReviveTarget != d || Flat(h.Position - a.Pos) > SquadRules.PlayerReviveReach) continue;
                a.ReviveProgress += dt; a.Progressed = true; a.ReviverHuman = h.Id; a.ReviverMarine = -1;
                break;
            }
        }
    }

    void EndDowned(ReadOnlySpan<SquadHuman> humans)
    {
        for (int d = 0; d < Max; d++)
        {
            var a = _a[d];
            if (a.State != SquadState.Downed) continue;
            if (!a.Progressed) { a.ReviveProgress = 0f; a.ReviverMarine = a.ReviverHuman = -1; continue; }
            float need = a.ReviverHuman >= 0 ? SquadRules.PlayerReviveSeconds : SquadRules.MarineReviveSeconds;
            if (a.ReviveProgress >= need - 1e-4f) DoRevive(d, a.ReviverHuman, a.ReviverMarine);
        }
        // fallen humans a Marine has been holding: complete, or reset when nobody worked on one this tick
        List<int>? finished = null, dropped = null;
        foreach (var (id, p) in _humanProgress)
        {
            if (p.Touched != _tick) (dropped ??= []).Add(id);
            else if (p.Progress >= SquadRules.MarineReviveSeconds - 1e-4f) (finished ??= []).Add(id);
        }
        if (dropped is not null) foreach (int id in dropped) _humanProgress.Remove(id);
        if (finished is null) return;
        foreach (int id in finished)
        {
            int by = _humanAttendant.TryGetValue(id, out var m) ? m : -1;
            _humanProgress.Remove(id); _humanAttendant.Remove(id);
            if (ReviveHuman?.Invoke(id) != true) continue;
            if (by >= 0 && _a[by].State == SquadState.Standing) _a[by].Marine.Record.Revives++;
            _revives.Add(new SquadRevive(-1, by, 0, -1, id));
        }
    }

    void StepAgent(int i, ReadOnlySpan<SquadHuman> humans, float dt)
    {
        var a = _a[i];
        if (ClassStep(i, humans, dt)) return;   // W11: regeneration, flight, pound
        int os = a.OwnerSlot;
        var order = _order[os];
        bool ownerKnown = false; Vector3 ownerPos = a.Pos;
        foreach (var h in humans) if (h.Id == a.Owner) { ownerKnown = h.Alive; ownerPos = h.Position; break; }
        var w = a.Marine.Loadout.Weapon;
        bool retarget = (_tick + (uint)i) % SquadRules.RetargetTicks == 0;

        // duty: a downed squad-mate or a fallen human, when no Ork is near the body
        if (a.DutyMate >= 0 || a.DutyHuman != int.MinValue) ValidateDuty(i, humans);
        if (a.DutyMate < 0 && a.DutyHuman == int.MinValue && retarget) ChooseDuty(i, humans);
        if (a.DutyMate >= 0 || a.DutyHuman != int.MinValue)
        {
            Vector3 body = a.DutyMate >= 0 ? _a[a.DutyMate].Pos : HumanPos(humans, a.DutyHuman);
            a.Target = 0;
            if (Flat(body - a.Pos) > SquadRules.MarineReviveReach - 0.2f) { MoveToward(a, body, MarineRules.ReviveApproach, SquadRules.SprintSpeed, dt); }
            else if (a.DutyMate >= 0)
            {
                var d = _a[a.DutyMate];
                if (!d.Progressed) { d.ReviveProgress += dt; d.Progressed = true; d.ReviverMarine = i; d.ReviverHuman = -1; }
            }
            else
            {
                var (prog, _) = _humanProgress.TryGetValue(a.DutyHuman, out var cur) ? cur : (0f, 0u);
                _humanProgress[a.DutyHuman] = (prog + dt, _tick);
            }
            return;
        }

        // target
        if (retarget) a.Target = PickTarget(i, a, order, w, ownerKnown, ownerPos);
        Vector3 tpos = default; SquadEnemyClass kind = default;
        bool haveTarget = a.Target != 0 && _arena.TryEnemy(a.Target, out tpos, out kind);
        if (!haveTarget) a.Target = 0;

        // where to stand: MarineBrain.PlanRetainer's rules (the owner's order decides; only Attack chases or walks to a place)
        Vector3 home = HomeOf(a, order, ownerKnown, ownerPos);
        bool sprint = false;
        Vector3? goal = null; float stop = 0.5f;
        float dOwner = Flat(ownerPos - a.Pos);
        switch (order)
        {
            case SquadOrder.Follow when ownerKnown:
                if (dOwner > MarineRules.FollowStandFar) { goal = home; stop = 1f; sprint = !haveTarget && dOwner > MarineRules.SprintBeyond; }
                else if (dOwner < MarineRules.FollowStandNear - 0.5f && dOwner > 1e-3f) { goal = ownerPos - (ownerPos - a.Pos) / dOwner * MarineRules.FollowStandNear; stop = 0.5f; }   // inside the band: back off
                break;
            case SquadOrder.Hold:
                if (Flat(a.HoldAt - a.Pos) > MarineRules.HoldStand) { goal = a.HoldAt; stop = MarineRules.HoldStand; sprint = !haveTarget && Flat(a.HoldAt - a.Pos) > MarineRules.SprintBeyond; }
                break;
            case SquadOrder.Attack:
            {
                float d = haveTarget ? Flat(tpos - a.Pos) : float.MaxValue;
                Vector3? objective = _point[os] ?? (ownerKnown ? ownerPos : null);
                if (w.Melee && haveTarget && d <= MarineRules.StrikeChase) { goal = tpos; stop = MarineRules.StrikeStandOff; }
                else if (objective is { } at && Flat(at - a.Pos) > MarineRules.ArriveRadius) { goal = at; stop = MarineRules.ArriveRadius * 0.75f; sprint = !haveTarget && Flat(at - a.Pos) > MarineRules.SprintBeyond; }
                else if (haveTarget) { goal = tpos; stop = w.Melee ? MarineRules.StrikeStandOff : MarineRules.GunStandOff; }
                break;
            }
        }
        if (goal is { } g) MoveToward(a, g, stop, sprint ? SquadRules.SprintSpeed : SquadRules.Speed, dt);

        // fire
        if (!haveTarget || sprint) return;   // a sprint fires nothing (MarineBrain.Decide)
        Vector3 to = tpos - a.Pos;
        if (Flat(to) > 0.01f) a.Yaw = MathF.Atan2(to.Y, to.X);
        // a melee class with a pistol (the Assault, the Raptor) fires it at range and strikes within the blade's reach, as the puppets' brain does (MarineBrain.Decide)
        var p = w.Melee && a.Marine.Loadout.Sidearm is { } side && Flat(to) > MarineRules.MeleeRange ? side : w;
        if (Flat(to) > p.Range || _tick < a.NextShot) return;
        if (!p.Melee && !_arena.HasSight(a.Pos, tpos)) return;
        a.NextShot = _tick + (uint)Math.Max(1, (int)MathF.Round(p.Interval * _arena.TickRate));
        a.FiredAt = _tick;   // W11 addition: the wire's Firing flag
        a.ShotMelee = p.Melee;
        if (Roll(_tick, i, 1) >= SquadAim.HitChance(p.HitChance, p.Melee, Flat(to))) return;   // S1: the weapon's chance times the Classic-bot aim chance (retainer misses 0.4 of a Classic-aimed shot's)
        var push = Flat(to) > 0.01f ? Vector3.Normalize(new Vector3(to.X, to.Y, 0f)) : Vector3.Zero;
        float amount = p.Damage * _damageScale[os] * SquadRules.DamageDealt;
        MusouEnemyType? type = _arena.TryEnemyType(a.Target, out var et) ? et : null;
        if (!_arena.DamageEnemy(a.Target, amount, push, out bool killed)) return;
        if (killed) Killed(i, a.Target, kind, type);
        else Note(a.Target, i);
        if (killed) a.Target = 0;
    }

    void Killed(int i, ushort enemy, SquadEnemyClass kind, MusouEnemyType? type = null)
    {
        var a = _a[i];
        var r = a.Marine.Record;
        r.Kills++;
        if (kind == SquadEnemyClass.Elite) r.EliteKills++; else if (kind == SquadEnemyClass.Boss) r.BossKills++;
        _kills.Add(new SquadKill(a.Key, a.Slot, enemy, kind, type));
        PayAssists(enemy, i);
    }

    void Note(ushort enemy, int marine)
    {
        _notes.TryGetValue(enemy, out var n);
        if (n.M0 == marine + 1 || n.M1 == marine + 1) { if (n.M0 == marine + 1) n.T0 = _tick; else n.T1 = _tick; }
        else if (n.M0 == 0 || n.T0 <= n.T1) { n.M0 = marine + 1; n.T0 = _tick; }
        else { n.M1 = marine + 1; n.T1 = _tick; }
        _notes[enemy] = n;
    }

    void PayAssists(ushort enemy, int killer)
    {
        if (!_notes.Remove(enemy, out var n)) return;
        uint window = (uint)(SquadRules.AssistSeconds * _arena.TickRate);
        if (n.M0 > 0 && n.M0 - 1 != killer && _tick - n.T0 <= window && _a[n.M0 - 1].State != SquadState.Free) _a[n.M0 - 1].Marine.Record.Assists++;
        if (n.M1 > 0 && n.M1 - 1 != killer && _tick - n.T1 <= window && _a[n.M1 - 1].State != SquadState.Free) _a[n.M1 - 1].Marine.Record.Assists++;
    }

    void Prune()
    {
        uint window = (uint)(SquadRules.AssistSeconds * _arena.TickRate);
        List<ushort>? old = null;
        foreach (var (id, n) in _notes) if (_tick - Math.Max(n.T0, n.T1) > window) (old ??= []).Add(id);
        if (old is not null) foreach (var id in old) _notes.Remove(id);
    }

    /// <summary>
    /// MarineBrain's choice (TfwMarineInput): the nearest few enemies in the order's sight, the first one in sight (a blow within <see cref="MarineRules.MeleeRange"/> needs none); a ranged Marine also in its weapon's range.
    /// S1 (owner 2026-11-19): an Ork threatening the owner or a downed squad-mate comes first, and the held target is kept for a second unless a threat or a much nearer Ork appears (<see cref="SquadTargeting"/>).
    /// </summary>
    ushort PickTarget(int self, Agent a, SquadOrder order, SquadWeapon w, bool ownerKnown, Vector3 ownerPos)
    {
        Vector3 centre = order == SquadOrder.Hold ? a.HoldAt : a.Pos;
        float radius = order switch { SquadOrder.Hold => MarineRules.HoldRadius, SquadOrder.Attack => MarineRules.AttackSight, _ => MarineRules.FollowEngage };
        float reach = w.Melee ? (a.Marine.Loadout.Sidearm?.Range ?? float.MaxValue) : w.Range;
        Span<Vector3> guarded = stackalloc Vector3[SquadRules.MaxPerPlayer + 1];
        int g = 0;
        if (ownerKnown) guarded[g++] = ownerPos;
        for (int m = 0; m < Max && g < guarded.Length; m++)
            if (m != self && _a[m].State == SquadState.Downed && _a[m].OwnerSlot == a.OwnerSlot) guarded[g++] = _a[m].Pos;
        var watch = guarded[..g];

        ushort best = 0; float bestD = 0f; bool bestThreat = false;
        int n = _arena.QueryEnemies(centre, radius, _seen);
        for (int k = 0; k < n && k < MarineRules.SightCandidates; k++)
        {
            if (!_arena.TryEnemy(_seen[k], out var pos, out _)) continue;
            float d = Flat(pos - a.Pos);
            if (d > reach) continue;
            if (d > MarineRules.MeleeRange && !_arena.HasSight(a.Pos, pos)) continue;
            bool threat = SquadTargeting.Threatens(pos, watch);
            if (best == 0 || (threat && !bestThreat)) { best = _seen[k]; bestD = d; bestThreat = threat; }   // the nearest in sight, unless one threatens
        }

        if (a.Target != 0 && a.Target != best && _arena.TryEnemy(a.Target, out var hp, out _))
        {
            float hd = Flat(hp - a.Pos);
            bool inReach = hd <= reach && Flat(hp - centre) <= radius && (hd <= MarineRules.MeleeRange || _arena.HasSight(a.Pos, hp));
            if (inReach)
            {
                float held = (_tick - a.TargetSince) / (float)_arena.TickRate;
                if (best == 0 || !SquadTargeting.PreferNew(held, hd, SquadTargeting.Threatens(hp, watch), bestD, bestThreat)) return a.Target;
            }
        }
        if (best != a.Target) a.TargetSince = _tick;
        return best;
    }

    Vector3 HomeOf(Agent a, SquadOrder order, bool ownerKnown, Vector3 ownerPos)
    {
        if (order == SquadOrder.Hold) return a.HoldAt;
        if (order == SquadOrder.Attack && _point[a.OwnerSlot] is { } at) return at + Ring(a.Slot, 1.5f, 0.4f);
        if (!ownerKnown) return a.Pos;
        float radius = MarineRules.FollowStandNear + (a.Slot % 3) * 1f;   // 4.5 to 6.5 m: inside the band
        return ownerPos + Ring(a.Slot, radius, 0f);
    }

    static Vector3 Ring(int slot, float radius, float step)
    {
        float angle = slot * 2.4f, r = radius + slot * step;
        return new Vector3(MathF.Cos(angle) * r, MathF.Sin(angle) * r, 0f);
    }

    // ---- revive duty

    void ChooseDuty(int i, ReadOnlySpan<SquadHuman> humans)
    {
        var a = _a[i];
        int bestMate = -1; int bestHuman = int.MinValue; float best = SquadRules.ReviveSearch;
        for (int d = 0; d < Max; d++)
        {
            var m = _a[d];
            if (m.State != SquadState.Downed || m.OwnerSlot != a.OwnerSlot || d == i) continue;
            if (m.Attendant >= 0 && m.Attendant != i && _a[m.Attendant].State == SquadState.Standing && _a[m.Attendant].DutyMate == d) continue;
            float dist = Flat(m.Pos - a.Pos);
            if (dist < best) { best = dist; bestMate = d; bestHuman = int.MinValue; }
        }
        foreach (var h in humans)
        {
            if (h.Alive || !h.Fallen) continue;
            if (_humanAttendant.TryGetValue(h.Id, out var att) && att != i && _a[att].State == SquadState.Standing && _a[att].DutyHuman == h.Id) continue;
            float dist = Flat(h.Position - a.Pos);
            if (dist < best) { best = dist; bestHuman = h.Id; bestMate = -1; }
        }
        if (bestMate < 0 && bestHuman == int.MinValue) return;
        Vector3 body = bestMate >= 0 ? _a[bestMate].Pos : HumanPos(humans, bestHuman);
        if (!Safe(body)) return;
        a.DutyMate = bestMate; a.DutyHuman = bestHuman;
        if (bestMate >= 0) _a[bestMate].Attendant = i; else _humanAttendant[bestHuman] = i;
    }

    void ValidateDuty(int i, ReadOnlySpan<SquadHuman> humans)
    {
        var a = _a[i];
        bool ok;
        Vector3 body = a.Pos;
        if (a.DutyMate >= 0) { ok = _a[a.DutyMate].State == SquadState.Downed; if (ok) body = _a[a.DutyMate].Pos; }
        else { ok = false; foreach (var h in humans) if (h.Id == a.DutyHuman) { ok = !h.Alive && h.Fallen; body = h.Position; break; } }
        if (ok && !Safe(body)) ok = false;
        if (ok) return;
        if (a.DutyMate >= 0 && _a[a.DutyMate].Attendant == i) _a[a.DutyMate].Attendant = -1;
        if (a.DutyHuman != int.MinValue) { if (_humanAttendant.TryGetValue(a.DutyHuman, out var att) && att == i) _humanAttendant.Remove(a.DutyHuman); }
        a.DutyMate = -1; a.DutyHuman = int.MinValue;
    }

    bool Safe(Vector3 body) => _arena.QueryEnemies(body, SquadRules.SafeRadius, _one) == 0;

    static Vector3 HumanPos(ReadOnlySpan<SquadHuman> humans, int id)
    {
        foreach (var h in humans) if (h.Id == id) return h.Position;
        return default;
    }

    // ---- movement

    /// <summary>
    /// Once per tick: each fighting squad whose owner stands and whose order has no fixed point gets one shared path from the squad's centre to the owner, rebuilt at most
    /// <see cref="SquadRules.PathRefreshPerSecond"/> times a second and only when the owner moved <see cref="SquadRules.PathMoved"/> m. Squads with a fixed point use the arena's flow field instead.
    /// </summary>
    void RefreshPaths(ReadOnlySpan<SquadHuman> humans)
    {
        uint every = (uint)Math.Max(1, _arena.TickRate / SquadRules.PathRefreshPerSecond);
        uint retry = (uint)Math.Max(1, (int)MathF.Round(SquadRules.FailedRetrySeconds * _arena.TickRate));
        for (int n = 0; n < SquadRules.MaxOwners; n++)
        {
            int os = (_searchCursor + n) % SquadRules.MaxOwners;   // round robin: the squad after the last one searched goes first
            if (!_used[os] || _point[os] is not null || _order[os] == SquadOrder.Hold) { _path[os] = null; _failed[os] = _stranded[os] = false; continue; }
            if (_pathBuilt[os] != 0 && (int)(_tick - _pathBuilt[os]) < (int)every) continue;
            bool known = false; Vector3 dest = default;
            foreach (var h in humans) if (h.Id == _player[os] && h.Alive) { known = true; dest = h.Position; break; }
            if (!known) { _path[os] = null; continue; }
            if (_path[os] is not null && !_pathPartial[os] && Flat(dest - _pathTo[os]) <= SquadRules.PathMoved) continue;   // a partial path is rebuilt from the new place whether or not the owner moved
            if (_failed[os] && _tick - _failTick[os] < retry && Flat(dest - _failOwner[os]) < SquadRules.FailedMoved) continue;   // a failed search waits 3 s, or until the owner has moved 10 m
            Vector3 sum = default; int count = 0;
            for (int k = 0; k < SquadRules.MaxPerPlayer; k++) { var a = _a[os * SquadRules.MaxPerPlayer + k]; if (a.State == SquadState.Standing) { sum += a.Pos; count++; } }
            if (count == 0) { _path[os] = null; continue; }
            if (!ClaimSearch()) continue;   // this tick's search is taken: the squad stays due and the cursor moves on
            _searchCursor = (os + 1) % SquadRules.MaxOwners;
            _pathBuilt[os] = (_pathBuilt[os] == 0 ? _tick + (uint)(os * (every / 4)) : _tick); _pathTo[os] = dest;   // the first rebuild of each squad is staggered so the searches do not land on one tick
            var found = _arena.FindPath(sum / count, dest);
            _path[os] = found is null ? null : [.. found];
            _pathPartial[os] = found is not null && _arena.LastPathPartial;
            if (found is null)
            {
                _failed[os] = true; _failTick[os] = _tick; _failOwner[os] = dest;
                if (!_stranded[os]) { _stranded[os] = true; _strandedSince[os] = _tick; }
            }
            else { _failed[os] = _stranded[os] = false; }
        }
    }

    /// <summary>
    /// The stranded failsafe (ruling of 2026-11-17): a squad whose owner has been unreachable for <see cref="SquadRules.StrandedSeconds"/> with the owner more than <see cref="SquadRules.StrandedDistance"/> m away has its standing,
    /// grounded Marines brought to the nearest graph floor <see cref="SquadRules.RelocateNear"/> to <see cref="SquadRules.RelocateFar"/> m from the owner on the squad's side, out of every human's sight when such places exist,
    /// one squad a second at most. A jump-pack Marine's leap (<c>ClassStep</c>, every 10 ticks) runs first: a leap that lands brings it within range and clears the case.
    /// </summary>
    void RelocateStranded(ReadOnlySpan<SquadHuman> humans)
    {
        if (_searched) return;   // a search took this tick's time: the relocation waits for the next (the squad stays flagged)
        uint gap = (uint)Math.Max(1, (int)MathF.Round(SquadRules.RelocateGapSeconds * _arena.TickRate));
        uint wait = (uint)Math.Max(1, (int)MathF.Round(SquadRules.StrandedSeconds * _arena.TickRate));
        if (_relocated && _tick - _lastRelocate < gap) return;
        for (int os = 0; os < SquadRules.MaxOwners; os++)
        {
            if (!_used[os] || !_stranded[os] || _tick - _strandedSince[os] < wait || !OwnerAt(humans, _player[os], out var owner)) continue;
            Vector3 sum = default; int count = 0;
            for (int k = 0; k < SquadRules.MaxPerPlayer; k++) { var a = _a[os * SquadRules.MaxPerPlayer + k]; if (a.State == SquadState.Standing && !a.Air) { sum += a.Pos; count++; } }
            if (count == 0 || Flat(sum / count - owner) <= SquadRules.StrandedDistance) continue;
            float baseAngle = MathF.PI;   // behind the owner's back: his yaw plus a half turn
            foreach (var h in humans) if (h.Id == _player[os]) { baseAngle = h.Yaw + MathF.PI; break; }
            var places = RelocationPlaces(owner, baseAngle, humans);   // one scan of the ring for the whole squad
            int moved = 0, next = 0;
            for (int k = 0; k < SquadRules.MaxPerPlayer && next < places.Count; k++)
            {
                var a = _a[os * SquadRules.MaxPerPlayer + k];
                if (a.State != SquadState.Standing || a.Air) continue;
                var place = places[next++];
                a.Pos = place; a.HoldAt = place; a.Target = 0; a.DutyMate = -1; a.DutyHuman = int.MinValue; a.Progressed = false;
                StuckSink?.Invoke(new SquadStuck(a.Key, a.Slot, place));
                moved++;
            }
            _strandedSince[os] = _tick; _failed[os] = false; _path[os] = null;   // the 10 s start again from the new place
            if (moved == 0) continue;
            Relocations++; _relocated = true; _lastRelocate = _tick;
            return;   // one squad a second
        }
    }

    /// <summary>
    /// Places for a stranded squad: graph floor 8 to 12 m from <paramref name="owner"/> (the squad's side first, fanning out), reachable by a straight walk from the owner and apart from each other, those in no human's sight first. At most
    /// <see cref="SquadRules.MaxPerPlayer"/>; fewer when the ring has no more floor.
    /// </summary>
    List<Vector3> RelocationPlaces(Vector3 owner, float baseAngle, ReadOnlySpan<SquadHuman> humans)
    {
        var hidden = new List<Vector3>(); var seen = new List<Vector3>();
        for (int t = 0; t < 36 && hidden.Count < SquadRules.MaxPerPlayer; t++)
        {
            float angle = baseAngle + (t % 2 == 0 ? 1 : -1) * ((t + 1) / 2) * 0.3f;
            float radius = SquadRules.RelocateNear + (SquadRules.RelocateFar - SquadRules.RelocateNear) * (t % 3) / 2f;
            var want = owner + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
            if (!_arena.Ground(want, out var g) || Flat(g - want) > 2f || !_arena.CanWalkLine(owner, g) || NearAny(hidden, g) || NearAny(seen, g)) continue;
            bool visible = false;
            foreach (var h in humans)
            {
                if (!h.Alive || !_arena.HasSight(h.Position, g)) continue;
                float to = MathF.Atan2(g.Y - h.Position.Y, g.X - h.Position.X), d = MathF.Abs(MathF.IEEERemainder(to - h.Yaw, MathF.Tau));
                if (d < SquadRules.ViewHalfAngle) { visible = true; break; }   // in his sight: a line to it and in front of him (what is behind his back is out of sight)
            }
            (visible ? seen : hidden).Add(g);
        }
        foreach (var g in seen) { if (hidden.Count >= SquadRules.MaxPerPlayer) break; hidden.Add(g); }
        return hidden;
    }

    static bool NearAny(List<Vector3> places, Vector3 g)
    {
        foreach (var p in places) if (Flat(p - g) < SquadRules.Separation) return true;
        return false;
    }

    /// <summary>The next place a Marine should walk to for a goal it cannot walk to straight: the squad's flow field to a fixed point, else the shared path; null when neither has one.</summary>
    Vector3? Route(Agent a, Vector3 goal)
    {
        int os = a.OwnerSlot;
        if (_point[os] is { } fixedPoint && Flat(goal - fixedPoint) < 12f && _arena.FlowNext(a.Pos, fixedPoint) is { } flow) return flow;
        var path = _path[os];
        if (path is null || path.Count == 0) return null;
        int near = 0; float best = float.MaxValue;
        for (int k = 0; k < path.Count; k++) { float d = Flat(path[k] - a.Pos); if (d < best) { best = d; near = k; } }
        // the nearest waypoint is the next one unless the Marine is already past it (W9b: a corner waypoint not yet reached is walked to, never skipped: a Marine stopped at a building's corner)
        int pick = Math.Min(near + 1, path.Count - 1);
        if (near < path.Count - 1)
        {
            var along = path[near + 1] - path[near]; var behind = a.Pos - path[near];
            if (along.X * behind.X + along.Y * behind.Y < 0f && Flat(path[near] - a.Pos) >= 0.5f) pick = near;
        }
        for (int k = Math.Min(near + 8, path.Count - 1); k > pick; k--)
            if (Flat(path[k] - a.Pos) >= 1.5f && _arena.CanWalkLine(a.Pos, path[k])) { pick = k; break; }
        return path[pick];
    }

    /// <summary>One step toward <paramref name="goal"/> (stopping <paramref name="stop"/> short of it): straight when the goal is near and the graph says the line is floor, else by the route. A step is taken only if it stays on the graph.</summary>
    void MoveToward(Agent a, Vector3 goal, float stop, float speed, float dt)
    {
        float left = Flat(goal - a.Pos);
        if (left <= stop) return;
        Vector3 aim = goal;
        if (left > SquadRules.DirectRange || !_arena.CanWalkLine(a.Pos, goal)) aim = Route(a, goal) ?? goal;
        var way = new Vector2(aim.X - a.Pos.X, aim.Y - a.Pos.Y);
        float len = way.Length();
        if (len < 1e-4f) return;
        float step = Math.Min(Math.Min(speed * dt, len), left - stop);
        var dir = way / len;
        if (!TryStep(a, dir, step)
            && !(MathF.Abs(dir.X) > 0.02f && TryStep(a, new Vector2(MathF.Sign(dir.X), 0f), step))
            && !(MathF.Abs(dir.Y) > 0.02f && TryStep(a, new Vector2(0f, MathF.Sign(dir.Y)), step))) return;   // slide along a wall (either axis toward the goal), else stay
        a.Yaw = MathF.Atan2(dir.Y, dir.X);
    }

    bool TryStep(Agent a, Vector2 dir, float step)
    {
        var to = a.Pos + new Vector3(dir.X * step, dir.Y * step, 0f);
        if (!_arena.CanStep(a.Pos, to, out var placed)) return false;
        a.Pos = placed;
        return true;
    }

    void Separate()
    {
        for (int i = 0; i < Max; i++)
        {
            if (_a[i].State != SquadState.Standing || _a[i].Air) continue;
            for (int j = i + 1; j < Max; j++)
            {
                if (_a[j].State != SquadState.Standing || _a[j].Air) continue;
                var d = new Vector2(_a[j].Pos.X - _a[i].Pos.X, _a[j].Pos.Y - _a[i].Pos.Y);
                float len = d.Length();
                if (len >= SquadRules.Separation) continue;
                var axis = len < 1e-4f ? new Vector2(MathF.Cos(i * 2.4f), MathF.Sin(i * 2.4f)) : d / len;
                float push = (SquadRules.Separation - len) * 0.5f;
                TryStep(_a[i], -axis, push);   // each Marine moves only where the graph has floor: no pushing through a wall
                TryStep(_a[j], axis, push);
            }
        }
    }

    static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    /// <summary>A deterministic roll in [0,1) of the tick, the Marine and a salt.</summary>
    static float Roll(uint tick, int index, uint salt)
    {
        uint h = tick * 2654435761u ^ (uint)(index + 1) * 2246822519u ^ salt * 3266489917u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }
}
