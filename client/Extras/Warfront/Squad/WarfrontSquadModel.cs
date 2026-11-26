using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmContent.Customization;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront.Squad;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>Where a retainer's World player stands as the client draws it this frame (from the snapshot's remote players): its place, facing and whether it is alive (a fallen retainer is downed).</summary>
public readonly record struct WarfrontWorldBody(Vector3 Position, float Yaw, bool Alive);

/// <summary>One squad Marine as the HUD reads it at one moment: its battle index and World id, whether it is downed, where it stands, its downed seconds and the own revive's share, and who it is.</summary>
public readonly record struct WarfrontSquadShown(int Index, int WorldId, bool Downed, Vector3 Position, float Yaw, float DownedLeft, float ReviveShare, string Name, byte Class, bool OwnPieces, ulong Owner, bool Own, int OwnerPlayer);

/// <summary>One row of the squad panel: a Marine of a roster with its record and where it stands.</summary>
public sealed record WarfrontSquadRow(ulong Owner, bool Own, int OwnerPlayer, int Slot, string Name, byte Class, IReadOnlyList<ushort> Record, string State);

/// <summary>
/// What the client knows of the squad (slice W10; Phase B T5: retainers are real World players). The names, classes, records and each Marine's World id come from the roster sections (tag 33; a section replaces
/// only its owner's); where a Marine stands and whether it is downed come from its World player in the snapshot (<see cref="World"/>: a fallen retainer is a downed one). The retired SquadState (tag 32) is
/// not read. Engine-free, tested; every call is given <c>now</c>. Distances (metres) and times are ours/estimates, in spec/behavior.md.
/// </summary>
public sealed class WarfrontSquadModel
{
    /// <summary>A human revives a downed Marine within this (<c>SquadRules.PlayerReviveReach</c>), holding Use this long (<c>SquadRules.PlayerReviveSeconds</c>).</summary>
    public const float ReviveReach = SquadRules.PlayerReviveReach, ReviveSeconds = SquadRules.PlayerReviveSeconds;
    /// <summary>A downed Marine is out after this (s; the server's <c>WarfrontRetainerRules.DownedSeconds</c>): the client counts it down from the Downed event (or from when it first saw it down).</summary>
    public const float DownedSeconds = SquadRules.DownedSeconds;
    /// <summary>A feed line shows this long (s).</summary>
    public const double FeedSeconds = 5.0;

    readonly Dictionary<ulong, SquadRosterSection> _rosters = new();
    readonly Dictionary<int, (ulong Owner, SquadRosterWire Marine)> _byIndex = new();
    readonly Dictionary<int, int> _indexOfWorld = new();
    /// <summary>Battle indices an Out event (tag 34) has taken out before the roster section says so (T5 review F4: the HUD counted them standing in between).</summary>
    readonly HashSet<int> _out = new();
    readonly Dictionary<int, double> _downedAt = new();
    int _holdTarget = -1;
    double _holdSince;

    /// <summary>The viewer's own player id (a roster whose owner player is this one is the own squad); -1 until known.</summary>
    public int OwnPlayer { get; set; } = -1;

    /// <summary>The World player of an id as drawn this frame (null: not in the snapshot). The networked view reads its remote players; the tests give a table.</summary>
    public Func<int, WarfrontWorldBody?> World { get; set; } = _ => null;

    public int Malformed { get; private set; }

    // ---- input

    /// <summary>A malformed payload counted by the caller.</summary>
    public void CountMalformed() => Malformed++;

    /// <summary>One owner's roster section: it replaces that owner's earlier one and no other.</summary>
    public void ApplyRoster(SquadRosterSection section)
    {
        if (section is null || section.Marines is null) return;   // T5 review F5: a malformed section changes nothing
        if (_rosters.TryGetValue(section.Handle, out var old))
            foreach (var m in old.Marines)
            {
                if (_byIndex.TryGetValue(m.Index, out var at) && at.Owner == section.Handle) _byIndex.Remove(m.Index);
                if (m.WorldId != SquadRosterWire.NoWorldId && _indexOfWorld.TryGetValue(m.WorldId, out int was) && was == m.Index) _indexOfWorld.Remove(m.WorldId);
            }
        _rosters[section.Handle] = section;
        foreach (var m in section.Marines)
        {
            if (m.Index == SquadRosterWire.NotInBattle) continue;
            if (m.WorldId == SquadRosterWire.NoWorldId) _out.Remove(m.Index);   // the roster has caught up with the Out event
            // T5 review F5: an index another owner's section held goes to this one; that owner's World id mapping goes with it
            if (_byIndex.TryGetValue(m.Index, out var held) && held.Owner != section.Handle && _indexOfWorld.TryGetValue(held.Marine.WorldId, out int heldAt) && heldAt == m.Index) _indexOfWorld.Remove(held.Marine.WorldId);
            _byIndex[m.Index] = (section.Handle, m);
            if (m.WorldId != SquadRosterWire.NoWorldId) _indexOfWorld[m.WorldId] = m.Index;
        }
        if (_rosters.Count > 64)   // a server never sends more than four owners; a hostile one does not grow this
        {
            var drop = _rosters.Keys.First(k => k != section.Handle);
            foreach (var m in _rosters[drop].Marines)
            {
                if (_byIndex.TryGetValue(m.Index, out var at) && at.Owner == drop) _byIndex.Remove(m.Index);
                if (_indexOfWorld.TryGetValue(m.WorldId, out int was) && was == m.Index && !(_byIndex.TryGetValue(m.Index, out var now) && now.Owner != drop)) _indexOfWorld.Remove(m.WorldId);   // T5 review F5: only its own mapping
            }
            _rosters.Remove(drop);
        }
    }

    /// <summary>The feed lines of a set of events (and the pound points are returned through <paramref name="pounds"/>); <paramref name="playerName"/> names a player id. A Downed event starts the Marine's count.</summary>
    public List<string> ApplyEvents(IReadOnlyList<SquadEventWire> events, Func<int, string?> playerName, List<Vector3>? pounds = null, double now = 0)
    {
        var lines = new List<string>();
        foreach (var e in events)
        {
            if (e.Kind == SquadEventKind.Pound) { pounds?.Add(e.Point); continue; }
            if (e.Kind == SquadEventKind.Downed) _downedAt[e.Index] = now;
            else if (e.Kind is SquadEventKind.Revived or SquadEventKind.Out) _downedAt.Remove(e.Index);
            if (e.Kind == SquadEventKind.Out) _out.Add(e.Index);
            else if (e.Kind == SquadEventKind.Revived) _out.Remove(e.Index);
            string name = NameOfIndex(e.Index);
            string by = e.By switch
            {
                255 => "",
                >= 200 => NameOfIndex(e.By - 200),
                var p => playerName(p) ?? "a player",
            };
            lines.Add(e.Kind switch
            {
                SquadEventKind.Downed => $"{name} IS DOWN",
                SquadEventKind.Revived => by.Length > 0 ? $"{name} REVIVED BY {by}" : $"{name} REVIVED",
                SquadEventKind.Out => $"{name} IS OUT",
                _ => $"{name} JOINS THE SQUAD",
            });
        }
        return lines;
    }

    /// <summary>"Brother Castor": the name of the Marine at a battle index as the feed writes it (a name that already says Brother is left alone).</summary>
    public string NameOfIndex(int index)
    {
        string n = _byIndex.TryGetValue(index, out var at) ? at.Marine.Name : $"Marine {index + 1}";
        return n.StartsWith("Brother ", StringComparison.OrdinalIgnoreCase) ? n : "Brother " + n;
    }

    /// <summary>
    /// The frame: a Marine seen fallen with no Downed event (a client that joined while it was down) starts its count now; one standing again forgets it. Call once a frame before reading the Marines.
    /// </summary>
    public void Frame(double now)
    {
        foreach (var (index, (_, m)) in _byIndex)
        {
            if (m.WorldId == SquadRosterWire.NoWorldId || World(m.WorldId) is not { } body) continue;
            if (!body.Alive) _downedAt.TryAdd(index, now);
            else _downedAt.Remove(index);
        }
    }

    /// <summary>Forgets everything (a new map or battle).</summary>
    public void Clear() { _rosters.Clear(); _byIndex.Clear(); _indexOfWorld.Clear(); _downedAt.Clear(); _out.Clear(); _holdTarget = -1; }

    /// <summary>Whether battle index <paramref name="index"/> is out by an Out event the roster has not yet caught up with.</summary>
    public bool IsOut(int index) => _out.Contains(index);

    // ---- output

    /// <summary>The battle indices of the Marines in the World now (a World id on the roster), in order.</summary>
    public IEnumerable<int> Indices => _byIndex.Where(kv => kv.Value.Marine.WorldId != SquadRosterWire.NoWorldId).Select(kv => kv.Key).OrderBy(i => i);
    public IReadOnlyCollection<SquadRosterSection> Rosters => _rosters.Values;

    /// <summary>Whether World player <paramref name="id"/> is a Warfront retainer on a known roster.</summary>
    public bool IsRetainer(int id) => id != SquadRosterWire.NoWorldId && _indexOfWorld.ContainsKey(id);

    /// <summary>The battle index of World player <paramref name="id"/> (-1: not a retainer).</summary>
    public int IndexOfWorld(int id) => id != SquadRosterWire.NoWorldId && _indexOfWorld.TryGetValue(id, out int i) ? i : -1;

    /// <summary>The World id of the Marine at battle index <paramref name="index"/> (0: none, or not in the World).</summary>
    public int WorldIdOf(int index) => _byIndex.TryGetValue(index, out var at) ? at.Marine.WorldId : SquadRosterWire.NoWorldId;

    /// <summary>The player id that owns World player <paramref name="id"/> as the roster says now (re-bound after a reconnect; -1: none or not a retainer).</summary>
    public int OwnerPlayerOf(int id)
    {
        int i = IndexOfWorld(id);
        if (i < 0 || !_byIndex.TryGetValue(i, out var at) || !_rosters.TryGetValue(at.Owner, out var sec)) return -1;
        return sec.OwnerPlayer == SquadRosterSection.NoPlayer ? -1 : sec.OwnerPlayer;
    }

    /// <summary>Marine <paramref name="index"/> at <paramref name="now"/>, as its World player stands; false when it is not in the World or not in the snapshot.</summary>
    public bool TrySample(int index, double now, out WarfrontSquadShown shown)
    {
        shown = default;
        if (!_byIndex.TryGetValue(index, out var r) || r.Marine.WorldId == SquadRosterWire.NoWorldId || World(r.Marine.WorldId) is not { } body) return false;
        var sec = _rosters[r.Owner];
        int ownerPlayer = sec.OwnerPlayer == SquadRosterSection.NoPlayer ? -1 : sec.OwnerPlayer;
        bool downed = !body.Alive;
        float left = downed ? Math.Max(0f, DownedSeconds - (float)(now - _downedAt.GetValueOrDefault(index, now))) : 0f;
        float share = downed && _holdTarget == index ? Math.Clamp((float)(now - _holdSince) / ReviveSeconds, 0f, 1f) : 0f;
        shown = new WarfrontSquadShown(index, r.Marine.WorldId, downed, body.Position, body.Yaw, left, share, r.Marine.Name, r.Marine.Class, r.Marine.OwnPieces, r.Owner, ownerPlayer >= 0 && ownerPlayer == OwnPlayer, ownerPlayer);
        return true;
    }

    // ---- the rules the views read

    /// <summary>The own squad's standing and roster counts for the HUD line, and how many are downed. Marines not in the World (not landed, or out) do not count as standing.</summary>
    public (int Standing, int Roster, int Downed) OwnCounts(double now)
    {
        int standing = 0, downed = 0, roster = 0;
        foreach (var sec in _rosters.Values)
        {
            if (sec.OwnerPlayer == SquadRosterSection.NoPlayer || sec.OwnerPlayer != OwnPlayer) continue;
            roster += sec.Marines.Count;
            foreach (var m in sec.Marines)
            {
                if (m.Index == SquadRosterWire.NotInBattle || m.WorldId == SquadRosterWire.NoWorldId || _out.Contains(m.Index)) continue;   // out by its event: not standing while the roster catches up (F4)
                if (World(m.WorldId) is { Alive: false }) downed++; else standing++;   // not yet in the snapshot: it has just landed, standing
            }
        }
        return (standing, roster, downed);
    }

    /// <summary>The line under the XP HUD: "SQUAD 7/9", "2 DOWN" while any is downed; null when the own squad is not in the battle.</summary>
    public string? HudLine(double now)
    {
        var (standing, roster, downed) = OwnCounts(now);
        if (roster == 0 || standing + downed == 0) return null;
        return downed > 0 ? $"{downed} DOWN" : $"SQUAD {standing}/{roster}";
    }

    /// <summary>
    /// The downed Marine the player at <paramref name="player"/> can revive: the nearest within <see cref="ReviveReach"/> (flat); null when none. <paramref name="useHeld"/>: whether the own Use is held now; the
    /// share is the own hold's on that Marine (the server's rule: per reviver, dropped when Use is released or the target changes), a client-side reading of the server's progress, which no wire carries.
    /// T5 review F3: none while the own player is not alive (<paramref name="selfAlive"/>), and none when a fallen human lies at least as near (<paramref name="fallenHuman"/>, flat metres): the server revives the
    /// nearer of the two, a tie going to the human (<c>WarfrontSquadWiring</c>), so the ring would show a revive that is not under way.
    /// </summary>
    public (int Index, string Name, float Share)? ReviveCandidate(Vector3 player, double now, bool useHeld = false, bool selfAlive = true, float fallenHuman = float.MaxValue)
    {
        int best = -1; float bestD = ReviveReach + 1f;
        if (!selfAlive) { _holdTarget = -1; return null; }
        foreach (int i in Indices)
        {
            if (!TrySample(i, now, out var s) || !s.Downed) continue;
            float dx = s.Position.X - player.X, dy = s.Position.Y - player.Y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= ReviveReach && d < bestD) { best = i; bestD = d; }
        }
        if (best >= 0 && fallenHuman <= bestD) best = -1;   // the human is the server's target
        if (best < 0 || !useHeld) _holdTarget = -1;
        else if (_holdTarget != best) { _holdTarget = best; _holdSince = now; }
        if (best < 0) return null;
        TrySample(best, now, out var shown);
        return (best, shown.Name, shown.ReviveShare);
    }

    /// <summary>The rows of the panel: the own roster's, or every other owner's (read only).</summary>
    public List<WarfrontSquadRow> PanelRows(bool own)
    {
        var rows = new List<WarfrontSquadRow>();
        foreach (var sec in _rosters.Values.OrderBy(s => s.Handle))
        {
            bool isOwn = sec.OwnerPlayer != SquadRosterSection.NoPlayer && sec.OwnerPlayer == OwnPlayer;
            if (isOwn != own) continue;
            foreach (var m in sec.Marines.OrderBy(m => m.Slot))
            {
                string state = sec.Held ? "ON HOLD"
                    : m.Index == SquadRosterWire.NotInBattle ? "READY"
                    : m.WorldId == SquadRosterWire.NoWorldId ? "OUT"
                    : World(m.WorldId) is { Alive: false } ? "DOWN" : "FIGHTING";
                rows.Add(new WarfrontSquadRow(sec.Handle, isOwn, sec.OwnerPlayer == SquadRosterSection.NoPlayer ? -1 : sec.OwnerPlayer, m.Slot, m.Name, m.Class, m.Record, state));
            }
        }
        return rows;
    }

    /// <summary>The headings of the eight record figures, in wire order.</summary>
    public static readonly string[] RecordHeadings = ["KILLS", "ELITE", "BOSS", "ASSISTS", "REVIVES", "DOWNED", "OBJECTIVES", "BATTLES"];

    // ---- the look of a Marine

    /// <summary>
    /// The appearance code a Marine wears: the owner's own pieces and colours (<paramref name="ownPieces"/>, a standard retainer), or the class's own armour in the owner's colours (a Librarian's squad: pieces
    /// left to the class's default look, the palettes, pattern, decals, lens and emblem of the owner's). Pure, so the choice is tested. (Phase B T5: the World player's look is the server's, which applies this
    /// same rule, <c>SmSim.Extras.Warfront.Retainers.RetainerLook.CodeOf</c>, since the T5 follow-up.)
    /// </summary>
    public static AppearanceCode LookCode(AppearanceCode owner, bool ownPieces) => SmSim.Extras.Warfront.Retainers.RetainerLook.CodeOf(owner, ownPieces);

    /// <summary>
    /// Whether the drawn melee arc of <paramref name="player"/> takes Warfront's cleave reach (x1.2, x1.4 a Dreadnought), as the server's <c>World.WarfrontCleaveBonus</c>: a human always, a retainer while
    /// <c>WarfrontRetainerRules.CleaveBonus</c> is on (on: "Exactly a human", owner 2026-11-25, T2 review), any other puppet (the allied pair, a pod's Marines) never. Pure, so the choice is tested.
    /// </summary>
    public static bool CleaveBonus(bool puppet, bool retainer) => !puppet || (retainer && SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.CleaveBonus);

    /// <summary>
    /// Whether a retainer's drawn arc takes the human-only x0.6 cleave trim, as the server strikes it (<c>World.WarfrontCleaveTrim</c>): while the server gives it a human's melee reach
    /// (<c>WarfrontRetainerRules.HumanMeleeReach</c>, on: "Exactly a human") or the human-only scales (<c>HumanOnlyScales</c>, off: base damage). Other puppets never. Pure, so the choice is tested
    /// (T5 review F1: the arc read only <c>HumanOnlyScales</c> and so drew a retainer's untrimmed AI reach).
    /// </summary>
    public static bool ArcHuman(bool retainer) => retainer && (SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.HumanOnlyScales || SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.HumanMeleeReach);
}
