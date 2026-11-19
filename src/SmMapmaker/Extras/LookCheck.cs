using SmContent;

namespace SmMapmaker;

/// <summary>
/// The map's look in play, engine-free parts (docs/mapmaker.md "Look in play"; owner's decision 2026-11-20): whether this PC's install has the Classic map a custom map is drawn with, the plain
/// sentence a player is refused with when it does not (no daylight fallback), and which SM1 definitions a <c>BezierCurve</c> turns (the ceiling fans), so imported placements of them spin.
/// The client does the drawing; nothing here reads more than a directory listing.
/// </summary>
public static class LookCheck
{
    /// <summary>The region archives <see cref="MapScene.Load"/> reads for a map, in its order.</summary>
    static readonly string[] Regions = ["wrld", "r000_main"];

    /// <summary>Whether the install at <paramref name="gameDir"/> holds the map archive <see cref="MapScene.Load"/> would read for <paramref name="map"/>. False for no install, a bad name or a missing file.</summary>
    public static bool Available(string? gameDir, string? map)
    {
        if (string.IsNullOrEmpty(gameDir) || !MapLook.IsMapName(map)) return false;
        foreach (var region in Regions)
            if (File.Exists(Path.Combine(gameDir, "data", "chunks", $"{map}__{region}.oppc"))) return true;
        return false;
    }

    /// <summary>The refusal for a PC that lacks the look's map, or null when it has it. <paramref name="displayName"/> is the map's name as a player knows it (null or empty: the archive name).</summary>
    public static string? Refusal(string? gameDir, string map, string? displayName = null)
    {
        if (Available(gameDir, map)) return null;
        return Text(map, displayName);
    }

    /// <summary>"This map uses the look of Shattered Bridge, which your game install does not have."</summary>
    public static string Text(string map, string? displayName = null) =>
        $"This map uses the look of {(string.IsNullOrWhiteSpace(displayName) ? map : displayName)}, which your game install does not have.";

    /// <summary>The definitions (by name, case-insensitive) of <paramref name="scene"/>'s spinning placements and their turn rates (degrees a second, negative clockwise): the key an imported placement is matched by.</summary>
    public static IReadOnlyDictionary<string, float> SpinningDefinitions(MapScene scene)
    {
        var found = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, degrees) in scene.Spinning)
            if (index >= 0 && index < scene.Objects.Count) found.TryAdd(scene.Objects[index].Definition, degrees);
        return found;
    }

    /// <summary>The <see cref="MapScene.Spinning"/> map for a list of placed objects drawn from a source scene: every placement of a spinning definition turns at that definition's rate.</summary>
    public static Dictionary<int, float> SpinningFor(IReadOnlyList<PlacedObject> objects, IReadOnlyDictionary<string, float> spinning)
    {
        var map = new Dictionary<int, float>();
        if (spinning.Count == 0) return map;
        for (int i = 0; i < objects.Count; i++)
            if (spinning.TryGetValue(objects[i].Definition, out var degrees)) map[i] = degrees;
        return map;
    }
}
