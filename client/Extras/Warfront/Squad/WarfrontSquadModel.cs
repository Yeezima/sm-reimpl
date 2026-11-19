using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmContent.Customization;
using SmNet.Extras.Warfront;
using SmSim;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>One Marine as the client draws it at one moment: the interpolated place and facing, what it does, and who it is.</summary>
public readonly record struct WarfrontSquadShown(int Index, bool Downed, Vector3 Position, float Yaw, float Speed, bool Firing, bool Melee, bool Airborne, int Target, float Health, float DownedLeft,
    bool BeingRevived, float ReviveShare, int Order, string Name, byte Class, ushort Primary, ushort Secondary, ushort Melee0, bool OwnPieces, ulong Owner, bool Own, int OwnerPlayer);

/// <summary>One row of the squad panel: a Marine of a roster with its record and where it stands.</summary>
public sealed record WarfrontSquadRow(ulong Owner, bool Own, int OwnerPlayer, int Slot, string Name, byte Class, IReadOnlyList<ushort> Record, string State);

/// <summary>
/// What the client knows of the squad (slice W10; engine-free, tested). The Marines come from SquadState records (10 Hz; the last two of each index are kept and the picture is drawn <see cref="RenderDelay"/> behind,
/// as the Guardsmen's; an index unseen for <see cref="DropAfter"/> s is dropped), the names, classes, weapons and records from the roster sections (a section replaces only its owner's). Pure: no Godot, no clock of its own
/// (every call is given <c>now</c>). Distances (metres) and times are ours/estimates, in spec/behavior.md.
/// </summary>
public sealed class WarfrontSquadModel
{
    /// <summary>Render this far behind the newest record (s), and drop an index unseen this long (s) [ours/estimate].</summary>
    public const double RenderDelay = 0.1, DropAfter = 1.0;
    /// <summary>A Marine farther than this from its last record is placed at once, not slid there (a landing, a leap's end), metres.</summary>
    public const float SnapMetres = 12f;
    /// <summary>A standing Marine's name shows for the one under the crosshair within this (m), and for <see cref="EventNameSeconds"/> s after an event about it (review 1: nine names over one squad cannot be read); a downed Marine always shows its name and seconds. The full-detail character is within <see cref="LodMetres"/> [estimate].</summary>
    public const float CrosshairNameMetres = 25f, LodMetres = 40f;
    public const double EventNameSeconds = 2.0;
    /// <summary>A human revives a downed Marine within this (<c>SquadRules.PlayerReviveReach</c>).</summary>
    public const float ReviveReach = SmSim.Extras.Warfront.Squad.SquadRules.PlayerReviveReach;
    /// <summary>A feed line shows this long (s).</summary>
    public const double FeedSeconds = 5.0;
    /// <summary>Extrapolating past the newest record is allowed this long (s).</summary>
    const double HoldAfter = 0.25;

    sealed class Entry
    {
        public SquadMarineWire Last, Prev;
        public double LastAt, PrevAt;
        public bool HasPrev;
        public double DownedSyncAt;
        public float DownedSyncSeconds;
        public float Revive;
    }

    readonly Dictionary<int, Entry> _marines = new();
    readonly Dictionary<int, double> _eventAt = new();
    readonly Dictionary<ulong, SquadRosterSection> _rosters = new();
    readonly Dictionary<int, (ulong Owner, SquadRosterWire Marine)> _byIndex = new();

    /// <summary>The viewer's own player id (a roster whose owner player is this one is the own squad); -1 until known.</summary>
    public int OwnPlayer { get; set; } = -1;

    public int Known => _marines.Count;
    public int Dropped { get; private set; }
    public int StatesRead { get; private set; }
    public int Malformed { get; private set; }

    // ---- input

    /// <summary>A SquadState record set (one or both halves of the list).</summary>
    public void ApplyState(SquadStateSection section, double now)
    {
        StatesRead++;
        foreach (var m in section.Marines)
        {
            if (_marines.TryGetValue(m.Index, out var e) && now >= e.LastAt)
            {
                e.Prev = e.Last; e.PrevAt = e.LastAt; e.HasPrev = true;
                if (Vector3.Distance(e.Prev.Position, m.Position) > SnapMetres || e.Last.State != m.State && m.State == 1) e.HasPrev = false;
                e.Last = m; e.LastAt = now;
            }
            else if (e is null) e = _marines[m.Index] = new Entry { Last = m, LastAt = now };
            else continue;
            if (!m.BeingRevived) { e.DownedSyncAt = now; e.DownedSyncSeconds = m.Aux / 10f; e.Revive = 0f; }
            else e.Revive = m.Aux / (float)SquadWire.MaxAux;
        }
    }

    /// <summary>A malformed payload counted by the caller.</summary>
    public void CountMalformed() => Malformed++;

    /// <summary>One owner's roster section: it replaces that owner's earlier one and no other.</summary>
    public void ApplyRoster(SquadRosterSection section)
    {
        if (_rosters.TryGetValue(section.Handle, out var old))
            foreach (var m in old.Marines) if (_byIndex.TryGetValue(m.Index, out var at) && at.Owner == section.Handle) _byIndex.Remove(m.Index);
        _rosters[section.Handle] = section;
        foreach (var m in section.Marines) if (m.Index != SquadRosterWire.NotInBattle) _byIndex[m.Index] = (section.Handle, m);
        if (_rosters.Count > 64) _rosters.Remove(_rosters.Keys.First(k => k != section.Handle));   // a server never sends more than four owners; a hostile one does not grow this
    }

    /// <summary>The feed lines of a set of events (and the pound points are returned through <paramref name="pounds"/>); <paramref name="playerName"/> names a player id.</summary>
    public List<string> ApplyEvents(IReadOnlyList<SquadEventWire> events, Func<int, string?> playerName, List<Vector3>? pounds = null, double now = 0)
    {
        var lines = new List<string>();
        foreach (var e in events)
        {
            if (e.Kind == SquadEventKind.Pound) { pounds?.Add(e.Point); continue; }
            _eventAt[e.Index] = now;
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

    /// <summary>Drops the Marines not seen for <see cref="DropAfter"/> s.</summary>
    public void Prune(double now)
    {
        foreach (var i in _marines.Where(kv => now - kv.Value.LastAt > DropAfter).Select(kv => kv.Key).ToList()) { _marines.Remove(i); Dropped++; }
    }

    /// <summary>Forgets everything (a new map or battle).</summary>
    public void Clear() { _marines.Clear(); _rosters.Clear(); _byIndex.Clear(); }

    // ---- output

    public IEnumerable<int> Indices => _marines.Keys.OrderBy(i => i);
    public IReadOnlyCollection<SquadRosterSection> Rosters => _rosters.Values;

    /// <summary>Marine <paramref name="index"/> at <paramref name="now"/> (rendered <see cref="RenderDelay"/> behind); false when it is not known.</summary>
    public bool TrySample(int index, double now, out WarfrontSquadShown shown)
    {
        shown = default;
        if (!_marines.TryGetValue(index, out var e)) return false;
        double t = now - RenderDelay;
        Vector3 position = e.Last.Position;
        float yaw = e.Last.Yaw, speed = 0f;
        if (e.HasPrev && e.LastAt > e.PrevAt)
        {
            double span = e.LastAt - e.PrevAt;
            double f = (t - e.PrevAt) / span;
            var d = e.Last.Position - e.Prev.Position;
            speed = MathF.Sqrt(d.X * d.X + d.Y * d.Y) / (float)Math.Max(0.02, span);
            f = Math.Clamp(f, 0.0, 1.0 + HoldAfter / span);
            position = e.Prev.Position + d * (float)f;
            yaw = LerpAngle(e.Prev.Yaw, e.Last.Yaw, (float)Math.Clamp(f, 0.0, 1.0));
        }
        float downedLeft = e.Last.State == 2 ? Math.Max(0f, e.DownedSyncSeconds - (float)(now - e.DownedSyncAt)) : 0f;
        (ulong owner, string name, byte cls, ushort a, ushort b, ushort c, bool pieces, bool own, int ownerPlayer) = (0UL, $"Marine {index + 1}", (byte)255, (ushort)0xFFFF, (ushort)0xFFFF, (ushort)0xFFFF, true, false, -1);
        if (_byIndex.TryGetValue(index, out var r))
        {
            var sec = _rosters[r.Owner];
            (owner, name, cls, a, b, c, pieces) = (r.Owner, r.Marine.Name, r.Marine.Class, r.Marine.Primary, r.Marine.Secondary, r.Marine.Melee, r.Marine.OwnPieces);
            ownerPlayer = sec.OwnerPlayer == SquadRosterSection.NoPlayer ? -1 : sec.OwnerPlayer;
            own = ownerPlayer >= 0 && ownerPlayer == OwnPlayer;
        }
        shown = new WarfrontSquadShown(index, e.Last.State == 2, position, yaw, speed, e.Last.Firing, e.Last.Melee, e.Last.Airborne, e.Last.Target == SquadMarineWire.NoTarget ? -1 : e.Last.Target,
            SquadWire.HealthOf(e.Last.Health), downedLeft, e.Last.BeingRevived, e.Last.BeingRevived ? e.Revive : 0f, e.Last.Order, name, cls, a, b, c, pieces, owner, own, ownerPlayer);
        return true;
    }

    static float LerpAngle(float a, float b, float t)
    {
        float d = MathF.IEEERemainder(b - a, MathF.PI * 2f);
        return a + d * t;
    }

    // ---- the rules the views read

    /// <summary>Whether Marine <paramref name="index"/> had an event (down, revived, out, recruited) within <see cref="EventNameSeconds"/> s of <paramref name="now"/>.</summary>
    public bool RecentEvent(int index, double now) => _eventAt.TryGetValue(index, out var at) && now - at <= EventNameSeconds && now >= at;

    /// <summary>Whether a standing Marine's name shows: under the crosshair within 25 m, or an event about it a moment ago. (A downed Marine's always shows.)</summary>
    public static bool NameVisible(bool underCrosshair, bool recentEvent, float distance) => recentEvent || underCrosshair && distance <= CrosshairNameMetres;

    /// <summary>Whether a Marine at <paramref name="distance"/> metres gets the full-detail character (else the low LOD).</summary>
    public static bool FullDetail(float distance) => distance <= LodMetres;

    /// <summary>The own squad's standing and roster counts for the HUD line, and how many are downed. Marines not in this battle do not count as standing.</summary>
    public (int Standing, int Roster, int Downed) OwnCounts(double now)
    {
        int standing = 0, downed = 0, roster = 0;
        foreach (var sec in _rosters.Values)
        {
            if (sec.OwnerPlayer == SquadRosterSection.NoPlayer || sec.OwnerPlayer != OwnPlayer) continue;
            roster += sec.Marines.Count;
            foreach (var m in sec.Marines)
                if (m.Index != SquadRosterWire.NotInBattle && _marines.TryGetValue(m.Index, out var e))
                    if (e.Last.State == 2) downed++; else standing++;
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

    /// <summary>The downed Marine the player at <paramref name="player"/> can revive: the nearest within <see cref="ReviveReach"/> (flat); null when none.</summary>
    public (int Index, string Name, float Share)? ReviveCandidate(Vector3 player, double now)
    {
        int best = -1; float bestD = ReviveReach + 1f;
        foreach (var i in _marines.Keys.OrderBy(i => i))
        {
            if (!TrySample(i, now, out var s) || !s.Downed) continue;
            float dx = s.Position.X - player.X, dy = s.Position.Y - player.Y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= ReviveReach && d < bestD) { best = i; bestD = d; }
        }
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
                string state = sec.Held ? "ON HOLD" : m.Index == SquadRosterWire.NotInBattle ? "READY" : !_marines.TryGetValue(m.Index, out var e) ? "READY" : e.Last.State == 2 ? "DOWN" : "FIGHTING";
                rows.Add(new WarfrontSquadRow(sec.Handle, isOwn, sec.OwnerPlayer == SquadRosterSection.NoPlayer ? -1 : sec.OwnerPlayer, m.Slot, m.Name, m.Class, m.Record, state));
            }
        }
        return rows;
    }

    /// <summary>The headings of the eight record figures, in wire order.</summary>
    public static readonly string[] RecordHeadings = ["KILLS", "ELITE", "BOSS", "ASSISTS", "REVIVES", "DOWNED", "OBJECTIVES", "BATTLES"];

    // ---- the look of a Marine

    /// <summary>
    /// The paint a Marine's worn jump pack takes: the look's pack paint (<see cref="WornPackPaint"/>: the backpack slot's colours, as a player's own pack) for a Marine that wears a pack, else null.
    /// A null keeps the side's default scheme, which was the bug (2026-11-19): the retainer's pack never took the owner's backpack paint. Pure, so the rule is tested.
    /// </summary>
    public static ResolvedPaint? PackPaintFor(string? packWargear, ResolvedPaint? lookPackPaint) => string.IsNullOrEmpty(packWargear) ? null : lookPackPaint;

    /// <summary>
    /// The appearance code a Marine wears: the owner's own pieces and colours (<paramref name="ownPieces"/>, a standard retainer), or the class's own armour in the owner's colours (a Librarian's squad: pieces
    /// left to the class's default look, the palettes, pattern, decals, lens and emblem of the owner's). Pure, so the choice is tested.
    /// </summary>
    public static AppearanceCode LookCode(AppearanceCode owner, bool ownPieces)
    {
        if (ownPieces) return owner;
        var code = AppearanceCode.Default;
        foreach (var slot in Enum.GetValues<ArmourSlot>())
        {
            code = code.WithPalette(slot, owner.Palette(slot, 0), owner.Palette(slot, 1), owner.Palette(slot, 2));
            code = code.WithPattern(slot, owner.PatternOn(slot));
        }
        code = code.WithDecals(owner.LeftDecal, owner.RightDecal, owner.FallbackBadge);
        if (owner.HasLens) code = code.WithLens(owner.Lens);
        if (owner.UsesEmblem) code = code.WithEmblem(owner.Emblem);
        return code;
    }
}
