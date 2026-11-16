namespace SmContent;

/// <summary>
/// The Host panel's MAP row (classic-maps-all, 2026-11-15): Manufactorum (the alpha map, the server's default) first, then the other playable competitive maps by their install names, then ROTATION
/// (every playable map in turn, one match each: the server's <c>--maps all</c>). Engine-free so tests can pin the order and the arguments.
/// </summary>
public static class HostMapChoices
{
    public const string DefaultMap = "mp_tdm_09";
    /// <summary>The value of the rotation choice.</summary>
    public const string Rotation = "rotation";

    public readonly record struct Choice(string Value, string Label);

    /// <summary>The choices for the playable maps of <paramref name="catalog"/> (the default map first, the rest by name, then the rotation when there is more than one map).</summary>
    public static IReadOnlyList<Choice> For(IEnumerable<MapEntry> catalog)
    {
        var playable = catalog.Where(m => m.Playable && (m.SupportsAnnihilation || m.SupportsSeize)).ToList();
        var list = new List<Choice>();
        foreach (var m in playable.OrderBy(m => m.Name == DefaultMap ? 0 : 1).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            list.Add(new Choice(m.Name, m.DisplayName.ToUpperInvariant()));
        if (list.Count > 1) list.Add(new Choice(Rotation, "ROTATION (ALL MAPS)"));
        return list;
    }

    /// <summary>The server options for a choice: (map, maps), at most one set; the default map and no choice leave both null (the server's default).</summary>
    public static (string? Map, string? Maps) ServerOptions(string? value) => value switch
    {
        null or "" or DefaultMap => (null, null),
        Rotation => (null, "all"),
        _ => (value, null),
    };
}
