using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// import-sm1-map {map, name?}: every drawn placement of an original SM1 map becomes an editable object of this map, under one group named after the
/// source map. Objects keep the SM1 reference (map + definition) and the exact placement (decomposed once, see TransformMath), never any mesh data.
/// Left out (said in the result's warnings): placements whose definition draws nothing (markers, lights, child-only groups), placements whose matrix
/// does not decompose (shear), anything past <see cref="MaxImported"/> (the .smmap size limit), and what the document cannot hold at all
/// (decals, lights, terrain, the script logic). Classic's own walls, kill boxes and cover come in as pieces and the placements Classic hides come in switched
/// off (see Sm1ImportGameplay.cs, docs/mapmaker.md "As Classic plays it").
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
        bool spawnsFit = spawnPlan.Objects <= room;   // a nearly full map keeps its placements and gets no spawn pieces (said below)
        if (spawnsFit) room -= spawnPlan.Objects;
        // Classic's own gameplay (invisible walls, kill boxes, movable cover) and the placements, planned first so the caps can be applied in order:
        // spawns (above), walls, kill boxes, cover, visible placements, then the placements Classic hides (imported switched off).
        var gameplay = source.LoadGameplay(map);
        var counts = new GameplayCounts();
        var walls = gameplay is null ? [] : PlanWalls(map, gameplay, counts);
        var kills = gameplay is null ? [] : PlanKills(map, gameplay, counts);
        var cover = gameplay is null ? [] : PlanCover(map, gameplay, counts);
        var placementKeys = Sm1ClassicKeys.PlacementKeys(scene.Objects);   // over the whole list, so a later refresh computes the same keys
        var shownPlan = new List<(Sm1Object O, string Key, MapTransform T)>();
        var hiddenPlan = new List<(Sm1Object O, string Key, MapTransform T)>();
        int noVisual = 0, sheared = 0, over = 0;
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            if (!scene.Definitions.TryGetValue(o.Definition, out var def) || def.Visuals == 0) { noVisual++; continue; }
            MapTransform t;
            try { t = TransformMath.Decompose(o.Transform); }
            catch (MapException) { sheared++; continue; }
            (scene.Hidden?.Contains(i) == true ? hiddenPlan : shownPlan).Add((o, placementKeys[i], t));
        }
        // A sub-group costs one object; it is only made for a category that gets at least one.
        int Take(int wanted, bool subGroup)
        {
            int n = Math.Min(wanted, room - (subGroup ? 1 : 0));
            if (n <= 0) { over += wanted; return 0; }
            room -= n + (subGroup ? 1 : 0);
            over += wanted - n;
            return n;
        }
        string SubGroup(string name)
        {
            var id = NewId();
            Doc.Objects.Add(new MapObject(id, name, groupId, layer, true, MapTransform.Identity, null, null, [], null));
            return id;
        }
        void AddPlanned(List<Planned> plan, int n, string subGroupName)
        {
            if (n == 0) return;
            var sub = SubGroup(subGroupName);
            foreach (var p in plan.Take(n)) Doc.Objects.Add(Stamp(new MapObject(NewId(), p.Name, sub, layer, true, p.Transform, null, null, [], p.Piece, p.Src)));
        }
        int nWalls = Take(walls.Count, true), nKills = Take(kills.Count, true), nCover = Take(cover.Count, true);
        AddPlanned(walls, nWalls, "Walls"); AddPlanned(kills, nKills, "Kill boxes"); AddPlanned(cover, nCover, "Cover");
        MapObject Placement(Sm1Object o, string key, MapTransform t, string parent, bool hidden) =>
            Stamp(new(NewId(), o.Name.Length > 0 ? o.Name : o.Definition, parent, layer, !hidden, t, AssetRef.Sm1(map, o.Definition), null, [],
                null, new MapObjectSrc(map, key, Sm1ClassicKeys.PlacementFp(o, hidden), MapObjectSrc.Placement, hidden)));
        var shownObjs = new List<MapObject>();
        var hiddenObjs = new List<MapObject>();
        foreach (var (o, key, t) in shownPlan.Take(Take(shownPlan.Count, false))) shownObjs.Add(Placement(o, key, t, groupId, false));
        Doc.Objects.AddRange(shownObjs);
        string? hiddenGroup = null;
        int nHidden = Take(hiddenPlan.Count, true);
        if (nHidden > 0)
        {
            hiddenGroup = SubGroup("Hidden by Classic");
            foreach (var (o, key, t) in hiddenPlan.Take(nHidden)) hiddenObjs.Add(Placement(o, key, t, hiddenGroup, true));
            Doc.Objects.AddRange(hiddenObjs);
        }
        if (MapLook.IsMapName(map)) Doc.Look = new MapLook(map);   // the original lighting, sky and fog in play (slice S4 draws it)
        // The spawns go in before the size trim, so the 8 MB limit counts them too (the loop below only ever drops placements).
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        foreach (var x in shownObjs.Concat(hiddenObjs)) { low = Vector3.Min(low, x.Transform.Position); high = Vector3.Max(high, x.Transform.Position); }
        bool any = shownObjs.Count + hiddenObjs.Count > 0;
        var spawnNotes = spawnsFit ? AddSpawns(spawnPlan, groupId, layer, any ? (low + high) / 2f : Vector3.Zero, any ? low.Z : 0f)
            : ["The map file is nearly full, so the spawn areas, doors and capture points were not added; the check will ask for them."];
        // The 8 MB file limit: drop the placements Classic hides first, then the visible ones (last first); the gameplay pieces always stay.
        int bySize = 0, hiddenBySize = 0;
        while ((shownObjs.Count > 0 || hiddenObjs.Count > 0) && Doc.ToJson().Length > MapDocument.MaxChars)
        {
            var from = hiddenObjs.Count > 0 ? hiddenObjs : shownObjs;
            int drop = Math.Max(1, (shownObjs.Count + hiddenObjs.Count) / 20);
            drop = Math.Min(drop, from.Count);
            var gone = from.GetRange(from.Count - drop, drop).Select(x => x.Id).ToHashSet();
            from.RemoveRange(from.Count - drop, drop);
            Doc.Objects.RemoveAll(x => gone.Contains(x.Id));
            if (ReferenceEquals(from, hiddenObjs)) hiddenBySize += drop; else bySize += drop;
        }
        if (hiddenGroup is not null && hiddenObjs.Count == 0) Doc.Objects.RemoveAll(x => x.Id == hiddenGroup);
        int imported = shownObjs.Count, hiddenImported = hiddenObjs.Count;
        Warnings.Add($"Imported {imported} of {scene.Objects.Count} placements of '{map}' under '{groupName}'.");
        if (gameplay is null) Warnings.Add("The original's invisible walls, kill boxes and cover were not available for this map (no install reader, or a map the game server does not load), so none were imported.");
        else Warnings.Add($"Classic's own gameplay came in as editable pieces: {nWalls} invisible wall(s) (players only), {nKills} kill box(es) and {nCover} cover box(es) (shoot-through crates and barrels, one box each); see the Walls, Kill boxes and Cover groups.");
        if (hiddenPlan.Count > 0) Warnings.Add($"{hiddenImported} placement(s) Classic hides were imported switched off under 'Hidden by Classic'; turn one on in the Scene list to bring it back.");
        if (counts.Spheres > 0) Warnings.Add($"{counts.Spheres} round invisible wall(s) (spheres) have no piece and were left out.");
        if (counts.Tilted > 0) Warnings.Add($"{counts.Tilted} invisible wall(s) are tilted in the original; only their turn about the vertical axis was kept.");
        if (counts.Degenerate > 0) Warnings.Add($"{counts.Degenerate} walls, kill boxes or cover props have no usable size and were left out.");
        if (noVisual > 0) Warnings.Add($"{noVisual} placements draw nothing (markers, lights, empty groups) and were left out.");
        if (sheared > 0) Warnings.Add($"{sheared} placements have a sheared matrix the editor cannot hold and were left out.");
        if (over > 0) Warnings.Add($"{over} objects were left out: a map file holds at most {MapDocument.MaxObjects} objects (and imports stop at {MaxImported}).");
        if (bySize + hiddenBySize > 0) Warnings.Add($"{bySize + hiddenBySize} placements ({hiddenBySize} of them hidden by Classic) were left out to keep the map file under {MapDocument.MaxChars / 1_000_000} MB.");
        Warnings.AddRange(spawnNotes);
        if (MapLook.IsMapName(map)) Warnings.Add($"The map's look is set to '{map}': Classic's lighting, sky and fog in play (Map tab, Look).");
        Warnings.Add("Not imported: decals, lights, terrain and the original's script logic. SM1 placements are drawn in the editor and solid in play; the spawn areas only get a thin placeholder floor.");
    }

    /// <summary>What the import adds besides placements: a spawn area (and a floor under it) and door per original marker, and what the data lacked.</summary>
    sealed record SpawnPlan(List<(string Name, string Kind, string Side, Vector3 Position, float YawDegrees, Vector3 Size)> Pieces, int Cylinders, bool Unreadable)
    {
        /// <summary>Every object the spawns will add: the pieces, a floor under each area, and an area with a floor for each side the data lacks.</summary>
        public int Objects => Pieces.Count + Pieces.Count(p => p.Kind is PieceKind.SpawnArea or PieceKind.CapturePoint) + 2 * MissingSides.Count() + (HasPoints ? 0 : 2 * PlaceholderPoints);
        public bool HasPoints => Pieces.Any(p => p.Kind == PieceKind.CapturePoint);
        public const int PlaceholderPoints = 3;
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
        // Seize Ground points, in the script order the server uses (the first is A); the piece order is what names them mkr_objective_N when compiled.
        if (spawns?.ControlPoints is { } points)
            foreach (var p in points)
                pieces.Add(($"Capture point ({p.Name})", PieceKind.CapturePoint, "", p.Position, p.Yaw * 180f / MathF.PI, new Vector3(p.Scale.X, p.Scale.X, p.Scale.Z)));
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

        int areas = 0, doors = 0, points = 0;
        var sides = new HashSet<string>();
        foreach (var (name, kind, side, position, yaw, size) in plan.Pieces)
        {
            var bottom = position - new Vector3(0, 0, size.Z / 2f);
            Add(name, kind, kind == PieceKind.CapturePoint ? null : side, bottom, yaw, size);
            if (kind == PieceKind.SpawnArea) { areas++; sides.Add(side); Floor("Spawn floor (placeholder)", bottom, yaw, size); }
            else if (kind == PieceKind.CapturePoint) { points++; Floor("Point floor (placeholder)", bottom, yaw, size); }
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
        if (!plan.HasPoints)
            for (int i = 0; i < SpawnPlan.PlaceholderPoints; i++)
            {
                var psize = PieceKind.Find(PieceKind.CapturePoint)!.DefaultSize;
                var pat = new Vector3(centre.X, centre.Y + (i - 1) * 25f, floorZ);
                Add($"Capture point {(char)('A' + i)} (placeholder)", PieceKind.CapturePoint, null, pat, 0f, psize);
                Floor("Point floor (placeholder)", pat, 0f, psize);
            }
        if (!plan.Unreadable && !plan.HasPoints) notes.Add("The original's data has no Seize Ground capture points, so three placeholder points (A, B, C) were added in a row; move them onto the floor.");
        else if (plan.Unreadable) notes.Add("The original's capture points could not be read either, so three placeholder points were added.");
        if (plan.HasPoints) notes.Add($"Seize Ground: {points} capture point(s) were placed where the original's are (the server's own reader, in its order: A first).");
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
