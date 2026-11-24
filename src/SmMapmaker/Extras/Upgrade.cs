using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// Older <c>.smmap</c> files in newer Mapmaker builds (owner's pass 3, item 13). Every format change is a numbered step (<see cref="Steps"/>: version n to n+1, a pure JSON rewrite that
/// loses nothing); <see cref="MapDocument.Parse"/> runs the steps from the file's version up to the current one, so any older file reads in memory exactly the same way on the server,
/// the client and the editor (same file, same hash). A file newer than this build is refused in plain words (never half read). The editor writes the upgraded file only after
/// <see cref="Backup"/> put the original beside it (<c>name.smmap.bak-v1</c>, a name the map search never matches), and says so.
/// </summary>
public static class MapUpgrade
{
    /// <summary>version n -> n + 1. 1 -> 2: files carry the definitions of the prefabs they use (<c>prefabs</c>, empty until the editor embeds them on open or save); nothing else changes. 2 -> 3: objects may carry an editor-only <c>src</c> and the map a game-visible <c>look</c> (docs/mapmaker.md "Classic link"); an identity step. An older Mapmaker refuses a version 3 file with the "newer Mapmaker" message.</summary>
    public static readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> Steps = new Dictionary<int, Func<JsonObject, JsonObject>>
    {
        [1] = o => o,
        [3] = o => o,   // 3 -> 4: the optional piece "mesh" and "material" and the map's "materials" and "images" (docs/mapmaker.md "Edit mode contract"); absent in an older file, nothing to rewrite
        [2] = o => o,   // 2 -> 3: the optional object "src" (editor-only Classic link) and map "look"; both are absent in an older file, so there is nothing to rewrite
    };

    /// <summary>
    /// Brings <paramref name="o"/> (read at <paramref name="from"/>) up to <paramref name="to"/> by the steps, setting <c>formatVersion</c> as it goes. A version with no step, or a file newer than
    /// <paramref name="to"/>, is a plain <see cref="MapException"/>.
    /// </summary>
    public static JsonObject ToCurrent(JsonObject o, int from, int to, IReadOnlyDictionary<int, Func<JsonObject, JsonObject>>? steps = null)
    {
        steps ??= Steps;
        if (from > to) throw new MapException("bad-file", $"This map was made by a newer Mapmaker (file format {from}; this build reads up to {to}). Update the Mapmaker (the launcher's newest build) to open it.");
        if (from < 1) throw new MapException("bad-file", $"Unsupported formatVersion {from}.");
        for (int v = from; v < to; v++)
        {
            if (!steps.TryGetValue(v, out var step)) throw new MapException("bad-file", $"This map's file format {from} cannot be upgraded here (no step from {v} to {v + 1}).");
            o = step(o);
            o["formatVersion"] = v + 1;
        }
        return o;
    }

    /// <summary>The file format a <c>.smmap</c> file declares, without parsing the rest; 0 when it cannot be read.</summary>
    public static int VersionOfFile(string path)
    {
        try { return Json.ParseText(File.ReadAllText(path), "The map") is JsonObject o && o["formatVersion"] is { } v ? Json.Int(v, "formatVersion", "bad-file") : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MapException) { return 0; }
    }

    /// <summary>
    /// Copies <paramref name="file"/> to <c>file.bak-v&lt;version&gt;</c> (then <c>-2</c>, <c>-3</c>... when that exists: an earlier backup is never replaced) and returns the backup's path.
    /// The extension is not <c>.smmap</c>, so no map list or search finds it.
    /// </summary>
    public static string Backup(string file, int version)
    {
        string baseName = file + ".bak-v" + version;
        string target = baseName;
        for (int i = 2; File.Exists(target); i++) target = $"{baseName}-{i}";
        File.Copy(file, target, overwrite: false);
        return target;
    }
}
