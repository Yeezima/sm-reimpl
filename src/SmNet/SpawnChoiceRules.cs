using SmSim;

namespace SmNet;

/// <summary>
/// The death screen's respawn choice on the client (protocol 58, fix/pvp-deploy-spawn; classic PvP): which choices to offer from what the client knows (the scoreboard's
/// mode and rows, the snapshot's control points), the next one when the player cycles, and the line shown. The server decides again at the respawn
/// (<see cref="World.SetSpawnChoice"/>, <see cref="World.SpawnOptions"/>); these rules only keep the menu honest.
/// </summary>
public static class SpawnChoiceRules
{
    /// <summary>Seconds between resends of the current choice while dead (the message is unacknowledged; ours).</summary>
    public const double ResendSeconds = 0.5;

    /// <summary>Classic PvP: a team match (the scoreboard says teams) that is not Musou Mode.</summary>
    public static bool Classic(Scoreboard? board, ContentExtras extras) => board is { Teams: true } && !extras.HasFlag(ContentExtras.Musou);

    /// <summary>
    /// The choices for player <paramref name="self"/> of side <paramref name="side"/>: the spawn room first, then in Seize Ground each control point the side holds (map
    /// order), then each ally the scoreboard marks as a living Teleport Homer carrier (by id). Outside classic PvP: none.
    /// </summary>
    public static IReadOnlyList<SpawnChoice> Options(Scoreboard? board, ContentExtras extras, int self, Team side, IReadOnlyList<ControlPointView> points)
    {
        var options = new List<SpawnChoice>();
        if (!Classic(board, extras)) return options;
        options.Add(SpawnChoice.SpawnRoom);
        if (side is not (Team.SpaceMarines or Team.Chaos)) return options;
        if (board!.Seize is not null)
            for (int i = 0; i < points.Count; i++)
                if (points[i].Owner == side) options.Add(new SpawnChoice(SpawnChoiceKind.ControlPoint, i));
        foreach (var row in board.Players.OrderBy(r => r.PlayerId))
            if (row.Homer && row.Team == side && row.PlayerId != self) options.Add(new SpawnChoice(SpawnChoiceKind.Homer, row.PlayerId));
        return options;
    }

    /// <summary>The choice after <paramref name="current"/> in <paramref name="options"/> (wrapping; a current choice no longer offered: the first after the spawn room).</summary>
    public static SpawnChoice Next(IReadOnlyList<SpawnChoice> options, SpawnChoice current, int step = 1)
    {
        if (options.Count == 0) return SpawnChoice.SpawnRoom;
        int at = -1;
        for (int i = 0; i < options.Count; i++) if (options[i] == current) { at = i; break; }
        if (at < 0) at = 0;
        int n = options.Count;
        return options[((at + step) % n + n) % n];
    }

    /// <summary>The choice in effect: <paramref name="wanted"/> while it is offered, else the spawn room (what the server does too).</summary>
    public static SpawnChoice Effective(IReadOnlyList<SpawnChoice> options, SpawnChoice wanted) => options.Contains(wanted) ? wanted : SpawnChoice.SpawnRoom;

    /// <summary>A point's letter (A, B, C ... as the Seize Ground markers).</summary>
    public static string PointLetter(int index) => index is >= 0 and < 26 ? ((char)('A' + index)).ToString() : index.ToString();

    /// <summary>The choice's name on the death screen ("SPAWN ROOM", "POINT B", "BROTHER AUREX (TELEPORT HOMER)").</summary>
    public static string Label(SpawnChoice choice, Scoreboard? board) => choice.Kind switch
    {
        SpawnChoiceKind.ControlPoint => "POINT " + PointLetter(choice.Target),
        SpawnChoiceKind.Homer => PlayerNames.Display(choice.Target, board?.Players.FirstOrDefault(r => r.PlayerId == choice.Target).Name).ToUpperInvariant() + " (TELEPORT HOMER)",
        _ => "SPAWN ROOM",
    };
}
