namespace SmContent;

/// <summary>
/// The scoreboard's player icons (the owner, 2026-11-07: "make it so the player icon is just the icon they spawned in with when they first joined the lobby. it should update only if they
/// leave and return and then use a new shoulder emblem icon before the next game starts"): each player's icon is the first one known for it after it appears on the scoreboard and stays
/// so while it stays on the board, whatever it wears later (a new look from the in-match customizer does not change it). A player that leaves (its id is gone from a board) is
/// forgotten, so when it returns its icon is taken again from its look then. Engine-free; <typeparamref name="TIcon"/> is what names an icon (the client: the look's shoulder decal).
/// </summary>
public sealed class JoinIcons<TIcon> where TIcon : class
{
    readonly Dictionary<int, TIcon> _icons = new();
    readonly HashSet<int> _present = new();

    /// <summary>The frozen icon of <paramref name="playerId"/>, or false while none has been known since it joined.</summary>
    public bool TryGet(int playerId, out TIcon icon) => _icons.TryGetValue(playerId, out icon!);

    /// <summary>How many players have an icon frozen (tests and logs).</summary>
    public int Count => _icons.Count;

    /// <summary>
    /// A scoreboard arrived listing <paramref name="playerIds"/>. Players not on it any more are forgotten; every listed player without an icon takes <paramref name="current"/>'s
    /// (null: not known yet, asked again with the next board); one with an icon keeps it. Returns the players whose icon was taken now.
    /// </summary>
    public IReadOnlyList<int> Observe(IEnumerable<int> playerIds, Func<int, TIcon?> current)
    {
        var listed = new HashSet<int>(playerIds);
        foreach (int gone in _present.Where(id => !listed.Contains(id)).ToList())
        {
            _present.Remove(gone);
            _icons.Remove(gone);
        }
        var taken = new List<int>();
        foreach (int id in listed.OrderBy(i => i))
        {
            _present.Add(id);
            if (_icons.ContainsKey(id)) continue;
            if (current(id) is { } icon) { _icons[id] = icon; taken.Add(id); }
        }
        return taken;
    }

    /// <summary>Forgets everyone (a new connection).</summary>
    public void Clear() { _icons.Clear(); _present.Clear(); }
}
