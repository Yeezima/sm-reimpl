using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;

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
        // The spawn areas and doors come first, so the file limit never costs the map its spawns (at most ~30 objects).
        var spawnPlan = PlanSpawns(source.LoadSpawns(map));
        room = Math.Max(0, room - spawnPlan.Objects);
        int imported = 0, noVisual = 0, sheared = 0, over = 0;
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
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
            low = Vector3.Min(low, t.Position); high = Vector3.Max(high, t.Position);
            imported++;
        }
        // The 8 MB file limit: drop the last imported objects until the document fits (a map full of long names may reach it before the object limit).
        int bySize = 0;
        while (imported > 0 && Doc.ToJson().Length > MapDocument.MaxChars)
        {
            int drop = Math.Max(1, imported / 20);
            Doc.Objects.RemoveRange(Doc.Objects.Count - drop, drop); imported -= drop; bySize += drop;
        }
        var spawnNotes = AddSpawns(spawnPlan, groupId, layer, imported > 0 ? (low + high) / 2f : Vector3.Zero, imported > 0 ? low.Z : 0f);
        Warnings.Add($"Imported {imported} of {scene.Objects.Count} placements of '{map}' under '{groupName}'.");
        if (noVisual > 0) Warnings.Add($"{noVisual} placements draw nothing (markers, lights, empty groups) and were left out.");
        if (sheared > 0) Warnings.Add($"{sheared} placements have a sheared matrix the editor cannot hold and were left out.");
        if (over > 0) Warnings.Add($"{over} placements were left out: a map file holds at most {MapDocument.MaxObjects} objects (and imports stop at {MaxImported}).");
        if (bySize > 0) Warnings.Add($"{bySize} placements were left out to keep the map file under {MapDocument.MaxChars / 1_000_000} MB.");
        Warnings.AddRange(spawnNotes);
        Warnings.Add("Not imported: decals, lights, terrain, collision and the original's script logic. SM1 pieces are drawn in the editor only; put Invisible walls round them to make them solid (the spawn areas only get a thin placeholder floor).");
    }

    /// <summary>What the import adds besides placements: a spawn area (and a floor under it) and door per original marker, and what the data lacked.</summary>
    sealed record SpawnPlan(List<(string Name, string Kind, string Side, Vector3 Position, float YawDegrees, Vector3 Size)> Pieces, int Cylinders, bool Unreadable)
    {
        /// <summary>Every object the spawns will add: the pieces, a floor under each area, and an area with a floor for each side the data lacks.</summary>
        public int Objects => Pieces.Count + Pieces.Count(p => p.Kind == PieceKind.SpawnArea) + 2 * MissingSides.Count();
        public IEnumerable<string> MissingSides => new[] { PieceSpec.Marines, PieceSpec.Chaos }.Where(s => !Pieces.Any(p => p.Kind == PieceKind.SpawnArea && p.Side == s));
    }

    SpawnPlan PlanSpawns(Sm1Spawns? spawns)
    {
        var pieces = new List<(string, string, string, Vector3, float, Vector3)>();
        int cylinders = 0;
        if (spawns is not null)
        {
            foreach (var a in spawns.StartAreas)
            {
                if (a.Side is not (SmSim.Team.SpaceMarines or SmSim.Team.Chaos)) continue;
                if (a.Shape == 3) cylinders++;
                pieces.Add((Label(a.Side, "spawn area", a.Name), PieceKind.SpawnArea, SideId(a.Side), a.Position, a.Yaw * 180f / MathF.PI, a.Scale));
            }
            foreach (var b in spawns.Blockers)
                pieces.Add((Label(b.Side, "spawn door", b.Name), PieceKind.Blocker, SideId(b.Side), b.Position, b.Yaw * 180f / MathF.PI, b.Scale));
        }
        return new SpawnPlan(pieces, cylinders, spawns is null);
    }

    static string SideId(SmSim.Team t) => t == SmSim.Team.SpaceMarines ? PieceSpec.Marines : PieceSpec.Chaos;
    static string Label(SmSim.Team t, string what, string source) => $"{(t == SmSim.Team.SpaceMarines ? "Marine" : "Chaos")} {what} ({source})";

    /// <summary>
    /// Creates the planned pieces under the import group. A marker is a centre and a full size; a piece's position is the middle of its footprint on the floor
    /// (its bottom), so z moves down by half the height. A spawn area gets a thin "Invisible wall" floor with its top at the area's bottom, the one thing
    /// the map check needs under a spawn; the original's own floor is collision the document cannot hold. A side the data has no area for gets a placeholder.
    /// </summary>
    List<string> AddSpawns(SpawnPlan plan, string groupId, string layer, Vector3 centre, float floorZ)
    {
        var notes = new List<string>();
        void Add(string name, string kind, string? side, Vector3 position, float yawDegrees, Vector3 size)
        {
            var piece = new PieceSpec(kind, side);
            var t = new MapTransform(TransformMath.Round(position), TransformMath.Round(new Vector3(0, 0, yawDegrees)), TransformMath.Round(size));
            var id = NewId();
            Doc.Objects.Add(new MapObject(id, name, groupId, layer, true, t, null, null, [], piece));
        }
        void Floor(string name, Vector3 bottomCentre, float yawDegrees, Vector3 size) =>
            Add(name, PieceKind.CollisionBox, null, bottomCentre - new Vector3(0, 0, Floor1), yawDegrees, new Vector3(size.X, size.Y, Floor1));

        int areas = 0, doors = 0;
        var sides = new HashSet<string>();
        foreach (var (name, kind, side, position, yaw, size) in plan.Pieces)
        {
            var bottom = position - new Vector3(0, 0, size.Z / 2f);
            Add(name, kind, side, bottom, yaw, size);
            if (kind == PieceKind.SpawnArea) { areas++; sides.Add(side); Floor("Spawn floor (placeholder)", bottom, yaw, size); }
            else doors++;
        }
        var missing = new List<string>();
        foreach (var (side, label, dx) in new[] { (PieceSpec.Marines, "Space Marine", -20f), (PieceSpec.Chaos, "Chaos", 20f) })
        {
            if (sides.Contains(side)) continue;
            var size = PieceKind.Find(PieceKind.SpawnArea)!.DefaultSize;
            var at = new Vector3(centre.X + dx, centre.Y, floorZ);
            Add($"{(side == PieceSpec.Marines ? "Marine" : "Chaos")} spawn area (placeholder)", PieceKind.SpawnArea, side, at, 0f, size);
            Floor("Spawn floor (placeholder)", at, 0f, size);
            missing.Add(label);
        }
        if (plan.Unreadable) notes.Add("The original's spawn data could not be read for this map (no install reader, or a map the game server does not load), so both spawn areas are placeholders.");
        else if (missing.Count > 0) notes.Add($"The original's data has no {string.Join(" or ", missing)} start area, so a placeholder was added; move it onto the floor.");
        if (!plan.Unreadable)
        {
            notes.Add($"Spawns: {plan.Pieces.Count(p => p.Kind == PieceKind.SpawnArea)} spawn area(s) and {doors} spawn door(s) were placed where the original's markers are (the server's own reader; a match uses the first area of each side).");
            if (plan.Cylinders > 0) notes.Add($"{plan.Cylinders} start area(s) are round in the original and became boxes of the same width and depth.");
        }
        return notes;
    }

    /// <summary>Thickness of the placeholder floor under a spawn area.</summary>
    const float Floor1 = 0.5f;
}

/// <summary>Public limits of import-sm1-map.</summary>
public static class Sm1Import
{
    /// <summary>Imported objects at most (under the file limit of 8 MB / 20000 objects, with room for the user's own additions).</summary>
    public const int MaxObjects = 15_000;
}
