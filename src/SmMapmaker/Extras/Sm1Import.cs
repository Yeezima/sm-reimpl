using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// import-sm1-map {map, name?}: every drawn placement of an original SM1 map becomes an editable object of this map, under one group named after the
/// source map. Objects keep the SM1 reference (map + definition) and the exact placement (decomposed once, see TransformMath), never any mesh data.
/// Left out (said in the result's warnings): placements whose definition draws nothing (markers, lights, child-only groups), placements whose matrix
/// does not decompose (shear), anything past <see cref="MaxImported"/> (the .smmap size limit), and what the document cannot hold at all
/// (decals, lights, terrain, collision, the map's own spawns and logic).
/// </summary>
internal sealed partial class Batch
{
    /// <summary>Under the file limit (8 MB, 20000 objects) with room for the user's own additions.</summary>
    public const int MaxImported = Sm1Import.MaxObjects;

    void ImportSm1Map(JsonObject op)
    {
        var map = Json.ReqStr(op, "map", "import-sm1-map", "bad-op");
        var source = Sm1 ?? throw new MapException("missing-install", "The Space Marine install was not found, so an SM1 map cannot be imported. Set SM_GAME_DIR or pass --game-dir.");
        var scene = source.LoadMap(map);
        var groupName = Json.Str(op["name"], "name", "bad-op", nullable: true) ?? map;
        var layer = "default";
        // The map may already hold objects (an earlier import, the owner's own): the whole document must stay under the loader's limits.
        int room = Math.Min(MaxImported, MapDocument.MaxObjects - Doc.Objects.Count - 1);
        if (room <= 0) throw new MapException("too-big", $"This map already holds {Doc.Objects.Count} objects; a map file holds at most {MapDocument.MaxObjects}, so nothing could be imported.");
        var groupId = NewId();
        Doc.Objects.Add(new MapObject(groupId, groupName, null, layer, true, MapTransform.Identity, null, null, [], null));
        Created.Add(groupId);
        int imported = 0, noVisual = 0, sheared = 0, over = 0;
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            if (scene.Hidden?.Contains(i) == true) { noVisual++; continue; }
            if (!scene.Definitions.TryGetValue(o.Definition, out var def) || def.Visuals == 0) { noVisual++; continue; }
            if (imported >= room) { over++; continue; }
            MapTransform t;
            try { t = TransformMath.Decompose(o.Transform); }
            catch (MapException) { sheared++; continue; }
            Doc.Objects.Add(new MapObject(NewId(), o.Name.Length > 0 ? o.Name : o.Definition, groupId, layer, true, t, AssetRef.Sm1(map, o.Definition), null, [], null));
            imported++;
        }
        // The 8 MB file limit: drop the last imported objects until the document fits (a map full of long names may reach it before the object limit).
        int bySize = 0;
        while (imported > 0 && Doc.ToJson().Length > MapDocument.MaxChars)
        {
            int drop = Math.Max(1, imported / 20);
            Doc.Objects.RemoveRange(Doc.Objects.Count - drop, drop); imported -= drop; bySize += drop;
        }
        Warnings.Add($"Imported {imported} of {scene.Objects.Count} placements of '{map}' under '{groupName}'.");
        if (noVisual > 0) Warnings.Add($"{noVisual} placements draw nothing (markers, lights, empty groups) and were left out.");
        if (sheared > 0) Warnings.Add($"{sheared} placements have a sheared matrix the editor cannot hold and were left out.");
        if (over > 0) Warnings.Add($"{over} placements were left out: a map file holds at most {MapDocument.MaxObjects} objects (and imports stop at {MaxImported}).");
        if (bySize > 0) Warnings.Add($"{bySize} placements were left out to keep the map file under {MapDocument.MaxChars / 1_000_000} MB.");
        Warnings.Add("Not imported: decals, lights, terrain, collision and the original's spawns and logic. SM1 pieces are drawn in the editor only; put Invisible walls round them to make them solid.");
    }
}

/// <summary>Public limits of import-sm1-map.</summary>
public static class Sm1Import
{
    /// <summary>Imported objects at most (under the file limit of 8 MB / 20000 objects, with room for the user's own additions).</summary>
    public const int MaxObjects = 15_000;
}
