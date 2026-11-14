using SmFormats.Baf;
using SmFormats.Locale;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>One competitive multiplayer map the install's front-end data lists (<c>multiplayer_map</c>): the archive name, the English display name and the game types it offers.</summary>
/// <param name="Name">The map's archive name (<c>mp_tdm_17</c>).</param>
/// <param name="DisplayName">The install's English name (<c>Shattered Bridge</c>), or the archive name when the locale is missing.</param>
/// <param name="GameTypes">The <c>objective_game_type</c> strings of the map's entry (<c>teamdeathmatch</c>, <c>domination</c>, ...).</param>
/// <param name="Playable">Whether the loader reads this map's region archive (collision, spawns, points): the dedicated server can run it.</param>
public sealed record MapEntry(string Name, string DisplayName, IReadOnlyList<string> GameTypes, bool Playable)
{
    /// <summary>Annihilation: the map offers <c>teamdeathmatch</c>.</summary>
    public bool SupportsAnnihilation => GameTypes.Contains("teamdeathmatch");
    /// <summary>Seize Ground: the map offers <c>domination</c>.</summary>
    public bool SupportsSeize => GameTypes.Contains("domination");
}

/// <summary>
/// The competitive map roster as the install lists it (classic-maps-all, 2026-11-15). Read from <c>data/packages/multiplayer_map.oppc</c> (one BAF entry per front-end map:
/// <c>map_name</c>, <c>display_name</c> as a locale id, <c>map_objectives</c> with the <c>objective_game_type</c> of each game type) and the English locale.
/// Co-op arenas (<c>cp_*</c>, <c>coop_grid_test</c>) and entries without a team game type are left out. Read only; nothing is copied.
/// </summary>
public static class MapCatalog
{
    public const string Package = "data/packages/multiplayer_map.oppc";

    /// <summary>The competitive maps (a <c>mp_*</c> map that offers Annihilation or Seize Ground), in archive-name order. Empty when the package is missing.</summary>
    public static IReadOnlyList<MapEntry> Read(string gameDir)
    {
        string path = Path.Combine(gameDir, Package.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return [];
        var notes = new List<string>();
        var locale = CatalogData.ReadLocale(gameDir, "english", notes);
        var playable = MapContent.Available(gameDir).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, MapEntry>(StringComparer.Ordinal);
        using var archive = ObpkArchive.Open(path);
        archive.ReadEntries(e => archive.GroupName(e) == "attr_pc", (e, data) =>
        {
            var root = BafDocument.Read(data).Root;
            string? map = root.String("map_name");
            if (map is null || !map.StartsWith("mp_", StringComparison.Ordinal) || !MapNameShape.Valid(map)) return;
            var types = new List<string>();
            if (root["map_objectives"] is { Type: BafType.Table } objectives)
                foreach (var o in objectives.AsTable().Entries)
                    if (o.Type == BafType.Table && o.AsTable().String("objective_game_type") is { } t) types.Add(t);
            if (!types.Contains("teamdeathmatch") && !types.Contains("domination")) return;
            string name = root["display_name"] is { Type: BafType.Int } d && locale?.Get(d.Raw) is { Length: > 0 } text ? text : map;
            found[map] = new MapEntry(map, name, types, playable.Contains(map));
        });
        return [.. found.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
    }
}

static class MapNameShape
{
    /// <summary>The same shape the wire allows for a map name (letters, digits, '_'; at most 24).</summary>
    public static bool Valid(string name) => name.Length is > 0 and <= 24 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
