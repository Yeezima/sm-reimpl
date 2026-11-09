namespace SmNet.Extras.Musou;

/// <summary>One boss's bar as the Musou HUD's column needs it: its enemy id, name, health (0..1) and whether it is the battle's commander (the Warboss).</summary>
public readonly record struct MusouBossBar(ushort Id, string Name, float Health, bool Commander);

/// <summary>
/// The order of the Musou HUD's boss column (owner, 2026-11-10: "put your player armor and health in the bottom left, then the boss health bars above that when the bosses do spawn. prioritize showing
/// the health of bosses being actively damaged by the local player viewing their own ui"), per viewer and engine-free: first the bosses the own player hit within <see cref="HitWindow"/> s, the most
/// recently hit first; then the commander; then the rest by distance from the player. The first <see cref="Shown"/> get a bar (the first nearest the player's own bar), the rest are counted on a
/// "+N" line. The order changes at most once every <see cref="HoldSeconds"/> s (a boss that dies leaves at once; a new one joins at the end until the next change), so a bar does not jump about
/// while two bosses are struck in turn. Every value ours.
/// </summary>
public sealed class MusouBossBarOrder
{
    /// <summary>Bars drawn (up to 8 bosses can live at once, owner 2026-11-10), the "actively damaged" window (s) and the shortest time between two changes of the order (s).</summary>
    public const int Shown = 3;
    public const double HitWindow = 5.0, HoldSeconds = 1.0;

    readonly List<ushort> _order = new();
    readonly List<ushort> _desired = new();
    readonly List<ushort> _kept = new();
    double _changedAt = double.NegativeInfinity;

    /// <summary>Every living boss in drawing order (the first <see cref="Shown"/> drawn).</summary>
    public IReadOnlyList<ushort> Order => _order;

    /// <summary>The bosses beyond <see cref="Shown"/> (the "+N" line).</summary>
    public int Hidden => Math.Max(0, _order.Count - Shown);

    /// <summary>Whether boss <paramref name="id"/> counts as being damaged by the own player at <paramref name="now"/>.</summary>
    public static bool Damaged(double lastHit, double now) => now - lastHit <= HitWindow;

    /// <summary>
    /// One frame: <paramref name="bosses"/> the living bosses, <paramref name="lastHit"/> when the own player last hit each (negative infinity: never), <paramref name="distance"/> each one's
    /// distance from the player (m; unknown: <see cref="float.MaxValue"/>), <paramref name="now"/> in the same seconds as the hits.
    /// </summary>
    public void Update(IReadOnlyList<MusouBossBar> bosses, Func<ushort, double> lastHit, Func<ushort, float> distance, double now)
    {
        _desired.Clear();
        foreach (var b in bosses) _desired.Add(b.Id);
        bool Commander(ushort id) { foreach (var b in bosses) if (b.Id == id) return b.Commander; return false; }
        int Group(ushort id) => Damaged(lastHit(id), now) ? 0 : Commander(id) ? 1 : 2;
        _desired.Sort((a, b) =>
        {
            int ga = Group(a), gb = Group(b);
            if (ga != gb) return ga.CompareTo(gb);
            if (ga == 0) { int h = lastHit(b).CompareTo(lastHit(a)); if (h != 0) return h; }
            if (ga == 2) { int d = distance(a).CompareTo(distance(b)); if (d != 0) return d; }
            return a.CompareTo(b);
        });
        // The order as it stands: the dead leave, the new join at the end.
        _kept.Clear();
        foreach (ushort id in _order) if (_desired.Contains(id)) _kept.Add(id);
        foreach (ushort id in _desired) if (!_kept.Contains(id)) _kept.Add(id);
        bool differs = !_kept.SequenceEqual(_desired);
        if (_order.Count == 0 && _kept.Count > 0) _changedAt = now;   // the first bosses: the hold starts here
        _order.Clear();
        if (differs && now - _changedAt >= HoldSeconds) { _order.AddRange(_desired); _changedAt = now; }
        else _order.AddRange(_kept);
    }
}
