using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;

namespace SmSim.Extras.Warfront.Retainers;

/// <summary>One roster Marine landed in the battle under way: its roster place, its owner, its World player id (0 once out) and its state under the squad's rules.</summary>
public sealed class WarfrontRetainer
{
    /// <summary>The battle index the wire carries (the owner's fighting slot x <see cref="SquadRules.MaxPerPlayer"/> + the Marine's slot; 0 to 35).</summary>
    public int Index { get; internal set; }
    public ulong Key { get; internal set; }
    public int Slot { get; internal set; }
    public int OwnerSlot { get; internal set; }
    /// <summary>The player id its owner fights under this battle (re-bound when he returns).</summary>
    public int Owner { get; internal set; }
    /// <summary>Its World player id (a puppet id); 0 when it is not in the World (out, or never landed for want of an id).</summary>
    public int WorldId { get; internal set; }
    public SquadState State { get; internal set; }
    /// <summary>Ticks left of the downed window.</summary>
    public int DownedTicks { get; internal set; }
    /// <summary>How far a revive of it has come (0 to 1).</summary>
    public float ReviveShare { get; internal set; }
    /// <summary>Phase B T4: its jump-pound mark while the order runs (a jump-pack retainer; its own landing round the owner's mark), null otherwise.</summary>
    public System.Numerics.Vector3? PoundAt { get; internal set; }
}

/// <summary>
/// Warfront's retainers as real World players (Phase B, T2 "the retainer spine"; spec "Added content: Warfront: retainers as World players"): the registry of the Marines landed in the battle under way and their
/// state under the squad's rules (downed 20 s, revived by anyone, out for that battle), their orders, records and scales. The World player does the moving, aiming, fighting, animation, effects and sound; its
/// input comes from <see cref="Brain"/> (Phase B T4 plugs in the Classic bot brain; none: it stands). The squad sim's movement, combat and targets are retired: nothing here moves a Marine.
/// One object per Warfront server, built by <see cref="WarfrontSquadWiring"/> and reached by the World through <see cref="WarfrontBattleRules.Retainers"/>. Deterministic: rosters in key order, no wall clock.
/// </summary>
public sealed class WarfrontRetainers
{
    readonly World _world;
    readonly SquadRosters _rosters;
    readonly List<WarfrontRetainer> _all = [];
    readonly ulong[] _ownerKeys = new ulong[SquadRules.MaxOwners];
    readonly bool[] _ownerUsed = new bool[SquadRules.MaxOwners];
    readonly Dictionary<ulong, (float Damage, float Health, float Armour)> _scale = [];
    readonly Dictionary<ushort, (int A, uint TA, int B, uint TB)> _hits = [];
    readonly List<SquadKill> _kills = [];
    readonly List<SquadRevive> _revives = [];
    readonly List<SquadDown> _downs = [];

    public WarfrontRetainers(World world, SquadRosters rosters) { _world = world; _rosters = rosters; }

    /// <summary>
    /// The retainers' brain (Phase B T4's seam, the Classic bot brain through a Warfront strategy): the input of retainer <c>id</c> as it stands this tick; null, or no brain: it stands still keeping its heading.
    /// Shipped builds have none until T4 lands; the stand/follow stub of the tests is test-only.
    /// </summary>
    public Func<int, PlayerState, PlayerInput?>? Brain { get; set; }

    /// <summary>The owners' orders (Follow until told otherwise; Musou's own rule: a resend or an older sequence is refused).</summary>
    public MarineOrders Orders { get; } = new();

    /// <summary>Where a retainer's kill is paid (the squad wiring: the owner's XP by the squad's rule; null: nowhere).</summary>
    public Action<SquadKill>? KillSink { get; set; }

    /// <summary>The server's log line sink (null: none).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Every Marine landed in this battle (out ones included), in landing order.</summary>
    public IReadOnlyList<WarfrontRetainer> All => _all;
    /// <summary>The kills, revives and downs of the last <see cref="Step"/> (and of the kill credit since).</summary>
    public IReadOnlyList<SquadKill> Kills => _kills;
    public IReadOnlyList<SquadRevive> Revives => _revives;
    public IReadOnlyList<SquadDown> Downs => _downs;

    /// <summary>Whether any retainer stands (the wipe rule).</summary>
    public bool AnyStanding { get { foreach (var r in _all) if (r.State == SquadState.Standing) return true; return false; } }

    /// <summary>Standing retainers of player <paramref name="player"/>.</summary>
    public int StandingOf(int player) { int n = 0; foreach (var r in _all) if (r.State == SquadState.Standing && r.Owner == player) n++; return n; }

    /// <summary>The retainer in World id <paramref name="id"/> (null: none).</summary>
    public WarfrontRetainer? ByWorldId(int id)
    {
        if (id == 0) return null;
        foreach (var r in _all) if (r.WorldId == id) return r;
        return null;
    }

    /// <summary>Whether <paramref name="id"/> is a retainer's World id this battle.</summary>
    public bool IsRetainer(int id) => ByWorldId(id) is not null;

    /// <summary>The retainer of battle index <paramref name="index"/> (null: none).</summary>
    public WarfrontRetainer? ByIndex(int index)
    {
        foreach (var r in _all) if (r.Index == index) return r;
        return null;
    }

    /// <summary>The retainer of <paramref name="player"/>'s roster slot <paramref name="slot"/> this battle (null: none).</summary>
    public WarfrontRetainer? Of(int player, int slot)
    {
        foreach (var r in _all) if (r.Owner == player && r.Slot == slot) return r;
        return null;
    }

    /// <summary>The battle index of <paramref name="player"/>'s Marine <paramref name="slot"/> (-1: not landed).</summary>
    public int IndexOf(int player, int slot) => Of(player, slot)?.Index ?? -1;

    /// <summary>The squad order of <paramref name="player"/>'s retainers.</summary>
    public SquadOrder OrderOf(int player) => (SquadOrder)(byte)OrderNow(player);

    /// <summary>The order in force for <paramref name="player"/>'s retainers: the server's own (<see cref="ServerOrder"/>) until his next order, else his newest (Follow before any).</summary>
    public MarineOrder OrderNow(int player) => _serverOrders.TryGetValue(player, out var o) ? o : Orders.Of(player);

    readonly Dictionary<int, MarineOrder> _serverOrders = [];

    /// <summary>The card and level scales of a key: damage dealt (applied as the attacker's scale on the Orks), health (as 1 / it on the damage taken while unshielded), armour (while shielded).</summary>
    public void SetScale(ulong key, float damage, float health, float armour = 1f) => _scale[key] = (damage, health, armour);
    public (float Damage, float Health, float Armour) ScaleOf(ulong key) => _scale.TryGetValue(key, out var s) ? s : (1f, 1f, 1f);

    // ---- the battle ------------------------------------------------------------------------------------------------------------------------

    /// <summary>A new battle: nobody landed (the World's puppets of the last battle went with its field).</summary>
    public void BeginBattle()
    {
        _all.Clear(); _hits.Clear(); _kills.Clear(); _revives.Clear(); _downs.Clear();
        Array.Clear(_ownerUsed);
        Orders.Clear(); _serverOrders.Clear();   // every battle lands on Follow (review fix 2026-11-25: a Hold of the last battle, or a leaver's, does not carry over)
    }

    /// <summary>The battle ended: every standing Marine survived it (one more in its record). The World's puppets were dismissed with the field already.</summary>
    public int EndBattle()
    {
        int n = 0;
        foreach (var r in _all)
            if (r.State == SquadState.Standing && _rosters.Marine(r.Key, r.Slot) is { } m) { m.Record.BattlesSurvived++; n++; }
        _all.Clear(); _hits.Clear();
        Array.Clear(_ownerUsed);
        return n;
    }

    /// <summary>Whether the key's squad is in this battle.</summary>
    public bool Landed(ulong key) { for (int s = 0; s < _ownerUsed.Length; s++) if (_ownerUsed[s] && _ownerKeys[s] == key) return true; return false; }

    /// <summary>
    /// Lands <paramref name="key"/>'s roster round <paramref name="at"/> as World players of player <paramref name="player"/> (one per Marine, in a fighting slot of four): how many landed. 0, and nothing changed,
    /// for a held, empty or landed roster or with four squads fighting. A Marine with no free puppet id is not landed (logged) and counts as out.
    /// </summary>
    public int Land(ulong key, int player, Vector3 at)
    {
        int count = Math.Min(_rosters.Count(key), SquadRules.MaxPerPlayer);
        if (count == 0 || _rosters.IsHeld(key) || Landed(key)) return 0;
        int ownerSlot = Array.IndexOf(_ownerUsed, false);
        if (ownerSlot < 0) return 0;
        _ownerUsed[ownerSlot] = true; _ownerKeys[ownerSlot] = key;
        int landed = 0;
        for (int slot = 0; slot < count; slot++)
        {
            var m = _rosters.Marine(key, slot)!;
            float angle = MathF.Tau * (slot + 0.5f) / Math.Max(2, count) + MathF.PI;   // behind and beside the owner first
            var place = at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * (WarfrontRetainerRules.LandRadius + slot * WarfrontRetainerRules.LandRadiusStep);
            int? id = _world.WarfrontSpawnRetainer(player, RetainerLoadout.ChoiceOf(_world.Config, m.Loadout), m.Name, place);
            var r = new WarfrontRetainer { Index = ownerSlot * SquadRules.MaxPerPlayer + slot, Key = key, Slot = slot, OwnerSlot = ownerSlot, Owner = player, WorldId = id ?? 0, State = id is null ? SquadState.Out : SquadState.Standing };
            if (id is null) Log?.Invoke($"warfront: no free World id for key {key}'s Marine {slot} ({m.Name}); it sits this battle out");
            else landed++;
            _all.Add(r);
        }
        return landed;
    }

    /// <summary>A returning player's new id for his squad already fighting; false when the key has none in this battle.</summary>
    public bool Rebind(ulong key, int player)
    {
        bool any = false;
        foreach (var r in _all) if (r.Key == key) { r.Owner = player; any = true; }
        return any;
    }

    /// <summary>An order of <paramref name="player"/> to his retainers (Follow, Hold, Attack): a Hold holds the point it names (else where each stands), an Attack goes for its point. False for a resend or no order.</summary>
    public bool Order(int player, TfwCommandRecord command)
    {
        if (!Orders.Apply(player, command)) return false;
        _serverOrders.Remove(player);   // his own order replaces the server's
        Place(player, Orders.Of(player), command.Point);
        return true;
    }

    /// <summary>
    /// The jump-pound order of <paramref name="player"/> (the squad's rule since W11, Phase B T4: max jump, then a ground pound round the marked point): each of his standing jump-pack retainers gets its own landing
    /// on a ring round <paramref name="mark"/> (<see cref="HoldPlace"/>); the brain plays it out and drops it (<see cref="PoundDone"/>). The order in force is kept for after. False when none of them carries a jump pack.
    /// </summary>
    public bool OrderJumpPound(int player, Vector3 mark)
    {
        var packs = new List<WarfrontRetainer>();
        foreach (var r in _all)
            if (r.Owner == player && r.State == SquadState.Standing && _world.Players.TryGetValue(r.WorldId, out var p) && Loadouts.JumpPack(_world.Config, p) is not null) packs.Add(r);
        for (int k = 0; k < packs.Count; k++) packs[k].PoundAt = HoldPlace(mark, k, packs.Count);
        return packs.Count > 0;
    }

    /// <summary>The brain played the jump-pound of World id <paramref name="worldId"/> out (or could not): the order in force applies again.</summary>
    public void PoundDone(int worldId) { if (ByWorldId(worldId) is { } r) r.PoundAt = null; }

    /// <summary>
    /// The server's own order for <paramref name="player"/>'s retainers (a leaver's squad holds, a returner's follows): in force until his next order, outside his sequence numbers (review fix 2026-11-25: a server
    /// sequence of its own was refused by the wrapping compare against a client's small counter). Always true.
    /// </summary>
    public bool ServerOrder(int player, MarineOrder order, Vector3 point = default)
    {
        _serverOrders[player] = order;
        Place(player, order, point);
        return true;
    }

    /// <summary>The order's places: a Hold with a point gives each retainer its own place on a ring round it (the old squad sim's rule), without one each holds where it stands; an Attack goes for its point.</summary>
    void Place(int player, MarineOrder order, Vector3 point)
    {
        bool place = point != default;
        int count = 0;
        foreach (var r in _all) if (r.Owner == player && r.WorldId != 0) count++;
        int k = 0;
        foreach (var r in _all)
        {
            if (r.Owner != player || r.WorldId == 0 || _world.Tfw?.MarineOf(r.WorldId) is not { } marine || !_world.Players.TryGetValue(r.WorldId, out var p)) continue;
            if (order == MarineOrder.Hold) marine.HoldAt = place ? HoldPlace(point, k, count) : p.Position;
            marine.AttackAt = order == MarineOrder.Attack && place ? point : null;
            k++;
        }
    }

    /// <summary>Retainer <paramref name="k"/> of <paramref name="count"/>'s hold place round <paramref name="point"/>: a ring of radius <see cref="WarfrontRetainerRules.LandRadius"/> (one alone holds the point itself).</summary>
    public static Vector3 HoldPlace(Vector3 point, int k, int count)
    {
        if (count <= 1) return point;
        float angle = MathF.Tau * k / count;
        return point + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * WarfrontRetainerRules.LandRadius;
    }

    /// <summary>
    /// One tick of the squad's rules over the World players (after the fight): a Marine that fell is downed (its window opens, one more time downed), one alive again stands, one whose window closed is out
    /// for the battle and leaves the World. The kills, revives and downs lists start afresh.
    /// </summary>
    public void Step()
    {
        _kills.Clear(); _revives.Clear(); _downs.Clear();
        int window = Math.Max(1, (int)MathF.Round(WarfrontRetainerRules.DownedSeconds * Math.Max(1, _world.Config.TickRate)));
        foreach (var r in _all)
        {
            if (r.WorldId == 0) { if (r.State != SquadState.Out) r.State = SquadState.Out; continue; }
            if (!_world.Players.TryGetValue(r.WorldId, out var p)) { _world.Tfw?.Battle?.WarfrontRetainerOut(r.WorldId); r.State = SquadState.Out; r.WorldId = 0; continue; }   // dismissed by someone else
            if (r.State == SquadState.Out) continue;   // waits for the puppets' step to leave the World (TakeOut)
            switch (r.State)
            {
                case SquadState.Standing when !p.Alive:
                    r.State = SquadState.Downed; r.DownedTicks = window; r.ReviveShare = 0f;
                    if (_rosters.Marine(r.Key, r.Slot) is { } m) m.Record.TimesDowned++;
                    _downs.Add(new SquadDown(r.Key, r.Slot));
                    break;
                case SquadState.Downed when p.Alive:
                    r.State = SquadState.Standing; r.ReviveShare = 0f;
                    break;
                case SquadState.Downed:
                    if (--r.DownedTicks > 0) break;
                    r.State = SquadState.Out;   // the World player leaves at the puppets' step (this runs inside the fight, where the World's id lists are in use)
                    break;
            }
        }
    }

    /// <summary>From the puppets' step (outside the fight): an out retainer's World player leaves the World and its id is free. True when <paramref name="worldId"/> was taken out.</summary>
    internal bool TakeOut(int worldId)
    {
        if (ByWorldId(worldId) is not { State: SquadState.Out } r) return false;
        _world.TfwDismissMarine(worldId);
        _world.Tfw?.Battle?.WarfrontRetainerOut(worldId);
        r.WorldId = 0;
        return true;
    }

    /// <summary>
    /// A downed Marine revived (by human <paramref name="byHuman"/> or retainer index <paramref name="byMarine"/>, -1 for none): back with the revive share of its health and armour where it fell; the reviver's record
    /// counts it. False when it is not downed.
    /// </summary>
    public bool Revive(WarfrontRetainer r, int byHuman = -1, int byMarine = -1)
    {
        if (r.State != SquadState.Downed || r.WorldId == 0 || !_world.TfwRevive(r.WorldId)) return false;
        r.State = SquadState.Standing; r.ReviveShare = 0f;
        if (byMarine >= 0 && ByIndex(byMarine) is { } by && _rosters.Marine(by.Key, by.Slot) is { } m) m.Record.Revives++;
        _revives.Add(new SquadRevive(byHuman, byMarine, r.Key, r.Slot, -1));
        return true;
    }

    /// <summary>A retainer (index <paramref name="byMarine"/>) revived a fallen human: its record counts it.</summary>
    public void RevivedHuman(int byMarine, int human)
    {
        if (ByIndex(byMarine) is not { } by) return;
        if (_rosters.Marine(by.Key, by.Slot) is { } m) m.Record.Revives++;
        _revives.Add(new SquadRevive(-1, byMarine, 0, -1, human));
    }

    /// <summary>The owner levelled up: his standing Marines are back at full health and armour. How many.</summary>
    public int Refill(ulong key)
    {
        int n = 0;
        foreach (var r in _all) if (r.Key == key && r.State == SquadState.Standing && _world.WarfrontRefillPlayer(r.WorldId)) n++;
        return n;
    }

    /// <summary>D or a position secured: every standing or downed Marine within <paramref name="radius"/> of <paramref name="point"/> counts an objective. How many.</summary>
    public int CreditObjective(Vector3 point, float radius)
    {
        int n = 0;
        foreach (var r in _all)
        {
            if (r.State is not (SquadState.Standing or SquadState.Downed) || !_world.Players.TryGetValue(r.WorldId, out var p)) continue;
            if (new Vector2(p.Position.X - point.X, p.Position.Y - point.Y).Length() > radius || _rosters.Marine(r.Key, r.Slot) is not { } m) continue;
            m.Record.Objectives++; n++;
        }
        return n;
    }

    // ---- the kill credit --------------------------------------------------------------------------------------------------------------------

    /// <summary>A retainer's hit on Ork <paramref name="enemy"/> at tick <paramref name="tick"/> (the assists' record: the last two retainers).</summary>
    public void Hit(ushort enemy, int worldId, uint tick)
    {
        if (ByWorldId(worldId) is not { } r) return;
        (int A, uint TA, int B, uint TB) h = _hits.TryGetValue(enemy, out var was) ? was : (-1, 0u, -1, 0u);
        if (h.A == r.Index) h = (r.Index, tick, h.B, h.TB);
        else h = (r.Index, tick, h.A, h.TA);
        _hits[enemy] = h;
    }

    /// <summary>
    /// Ork <paramref name="enemy"/> of type <paramref name="type"/> died, killed by player <paramref name="killer"/> (a human, a retainer, 0 for nobody): a retainer's kill counts in its record and is returned to pay
    /// its owner; every other retainer that hit it within <see cref="WarfrontRetainerRules.AssistSeconds"/> counts an assist.
    /// </summary>
    public SquadKill? Killed(ushort enemy, MusouEnemyType type, int killer, uint tick)
    {
        var by = ByWorldId(killer);
        if (_hits.Remove(enemy, out var h))
        {
            uint window = (uint)Math.Max(1, MathF.Round(WarfrontRetainerRules.AssistSeconds * Math.Max(1, _world.Config.TickRate)));
            foreach (var (index, at) in new[] { (h.A, h.TA), (h.B, h.TB) })
                if (index >= 0 && index != by?.Index && tick - at <= window && ByIndex(index) is { } helper && _rosters.Marine(helper.Key, helper.Slot) is { } hm) hm.Record.Assists++;
        }
        if (by is null) return null;
        var cls = WarfrontXpLedger.Classify(type) switch { WarfrontKillClass.Boss => SquadEnemyClass.Boss, WarfrontKillClass.Elite => SquadEnemyClass.Elite, _ => SquadEnemyClass.Fodder };
        if (_rosters.Marine(by.Key, by.Slot) is { } m)
        {
            m.Record.Kills++;
            if (cls == SquadEnemyClass.Elite) m.Record.EliteKills++;
            else if (cls == SquadEnemyClass.Boss) m.Record.BossKills++;
        }
        var kill = new SquadKill(by.Key, by.Slot, enemy, cls, type);
        _kills.Add(kill);
        return kill;
    }

    // ---- the wire's view -------------------------------------------------------------------------------------------------------------------

    /// <summary>A Marine as the sender and the spectator watch read it (null: no Marine of that index in this battle).</summary>
    public SquadMarineView? View(int index)
    {
        if (ByIndex(index) is not { } r) return null;
        if (!_world.Players.TryGetValue(r.WorldId, out var p)) return new SquadMarineView(r.Index, r.Key, r.Slot, r.State, Vector3.Zero, 0f, 0f, 0f);
        var body = Loadouts.Body(_world.Config, p);
        float share = body.HealthMax > 0f ? Math.Clamp(p.Health / body.HealthMax, 0f, 1f) : 0f;
        float left = r.State == SquadState.Downed ? r.DownedTicks / (float)Math.Max(1, _world.Config.TickRate) : 0f;
        return new SquadMarineView(r.Index, r.Key, r.Slot, r.State, p.Position, p.Yaw, share, left, ReviveShare: r.ReviveShare, Airborne: !p.Grounded);
    }
}
