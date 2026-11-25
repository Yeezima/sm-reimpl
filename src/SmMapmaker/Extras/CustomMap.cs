using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SmContent;
using SmFormats.Havok;
using SmSim;

namespace SmMapmaker;

/// <summary>The unit shapes of the solid pieces (origin at the bottom centre, size 1 x 1 x 1), shared by the compiler (collision) and the views (drawing).</summary>
public static class PieceMesh
{
    /// <summary>The triangles of a solid kind's unit shape; null for a kind that is not solid.</summary>
    public static (Vector3[] Vertices, int[] Indices)? Unit(string kind) => kind switch
    {
        PieceKind.Box or PieceKind.CollisionBox => BoxMesh(),
        PieceKind.Ramp => RampMesh(),
        PieceKind.Column => ColumnMesh(12),
        _ => null,
    };

    /// <summary>The unit box of any marker kind (spawn areas, doors, points, kill zones), centred on the footprint with its bottom on z = 0.</summary>
    public static (Vector3[] Vertices, int[] Indices) MarkerBox() => BoxMesh();

    static (Vector3[], int[]) BoxMesh()
    {
        Vector3[] v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? 0f : 1f);
        int[] t =
        [
            0, 2, 1, 1, 2, 3,   // bottom
            4, 5, 6, 5, 7, 6,   // top
            0, 1, 4, 1, 5, 4,   // -Y
            2, 6, 3, 3, 6, 7,   // +Y
            0, 4, 2, 2, 4, 6,   // -X
            1, 3, 5, 3, 7, 5,   // +X
        ];
        return (v, t);
    }

    static (Vector3[], int[]) RampMesh()
    {
        // Low edge at y = -0.5 (z = 0), high edge at y = +0.5 (z = 1).
        Vector3[] v =
        [
            new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0), new(0.5f, 0.5f, 1), new(-0.5f, 0.5f, 1),
        ];
        int[] t =
        [
            0, 2, 1, 0, 3, 2,   // bottom
            0, 1, 4, 0, 4, 5,   // slope
            3, 5, 4, 3, 4, 2,   // back wall (+Y)
            0, 5, 3,            // left side (-X)
            1, 2, 4,            // right side (+X)
        ];
        return (v, t);
    }

    /// <summary>A unit cylinder (bottom centre origin, diameter 1, height 1) with <paramref name="sides"/> sides.</summary>
    public static (Vector3[] Vertices, int[] Indices) Cylinder(int sides) => ColumnMesh(sides);

    static (Vector3[], int[]) ColumnMesh(int sides)
    {
        var v = new List<Vector3>();
        for (int i = 0; i < sides; i++)
        {
            var (cos, sin) = DetMath.Ring(i, sides);   // deterministic: the cylinder's collision must be the same bits on every OS
            v.Add(new Vector3((float)(0.5 * cos), (float)(0.5 * sin), 0));
            v.Add(new Vector3((float)(0.5 * cos), (float)(0.5 * sin), 1));
        }
        int bc = v.Count; v.Add(new Vector3(0, 0, 0));
        int tc = v.Count; v.Add(new Vector3(0, 0, 1));
        var t = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            int b0 = i * 2, t0 = i * 2 + 1, b1 = j * 2, t1 = j * 2 + 1;
            t.AddRange([b0, b1, t0, b1, t1, t0, bc, b1, b0, tc, t0, t1]);
        }
        return ([.. v], [.. t]);
    }
}

/// <summary>One piece as the views draw it: id, kind, side, name and its world matrix (document frame, Z up; includes the object's scale).</summary>
public sealed record DrawPiece(string Id, string Name, PieceSpec Piece, Matrix4x4 World, bool Visible)
{
    /// <summary>The edited shape (unit space; null: the stock shape). Draw <see cref="Shape"/>.</summary>
    public EditMesh? Mesh { get; init; }
    /// <summary>The object's own scale (sized space = unit space times this; UVs are in metres of sized space).</summary>
    public Vector3 Scale { get; init; } = Vector3.One;
    /// <summary>The material id (<see cref="MapDocument.Materials"/>); null: the default grid look.</summary>
    public string? Material { get; init; }

    /// <summary>The shape to draw, unit space: the edited mesh or the piece's stock shape.</summary>
    public EditMesh Shape => Mesh ?? EditMesh.Stock(Piece);

    /// <summary>Triangles with normals, tangents and UVs in the document frame (<see cref="RenderMesh"/>).</summary>
    public RenderMesh Render() => RenderMesh.Build(Shape, Scale, World);
}

/// <summary>
/// A <c>.smmap</c> compiled for play (docs/mapmaker.md "Playing a map"): the collision of its solid pieces, its spawn areas, doors, capture points and
/// kill zones as the simulation takes them (<see cref="Content"/>, in the shape of an install's map), and the hash both sides check. The server and
/// the client compile the same file the same way, so both build the same collision from it. Decoration (SM1 pieces, imported models) is not compiled:
/// the client draws it; the server never reads it.
/// </summary>
public sealed class CustomMap
{
    /// <summary>Bump when the way pieces become collision, areas or points changes (it is part of the hash).</summary>
    public const int CompilerVersion = 7;   // 7: an edited piece (stored mesh, docs/mapmaker.md "Edit mode contract") collides, walls and covers by its mesh, and an edited marker's box is its mesh's box (plus an exact volume in Volumes); 6: the new piece kinds move-wall and cover fill MapContent.Walls and MapContent.Props, and the document gained look (so every custom map's hash changes: the server and every client must run the same build); 5: the solid pieces, cylinders, marker positions and yaw and the SM1 node transforms go through the deterministic path too (the whole custom-map collision is bit-identical on every OS); 4: placement maths in double with a deterministic sin/cos, rotation snapped to 1/1000 degree (bit-identical collision on Linux and Windows); 3: decoration positions are quantised to 1/4096 m and the digest covers the source data (cross-OS); 2: SM1 placements, prefab instances and imported models are solid (docs/mapmaker.md "SM1 pieces in play"); the hash covers their triangles
    public const string Prefix = "cm_";
    /// <summary>Metres below the lowest solid piece's underside at which the map kills a falling player (ours; the pit under every custom map).</summary>
    public const float KillFloorDepth = 6f;
    /// <summary>
    /// Whether an edited marker's record carries its exact shape into the simulation (plan M2; docs/mapmaker.md "Edit mode in play"): on since contract v3 (it was held off on v2,
    /// lead 39, 2026-11-25). Off, the records keep only the mesh's box and the shapes stay in <see cref="Volumes"/>: the one switch that restores the M0 play of edited maps (their
    /// hash is the same either way). Unedited markers never carry a shape.
    /// </summary>
    public const bool VolumesInPlay = true;
    /// <summary>The surface id a cover piece's triangles give a shot that hits them (the id of Classic's crate metal, <c>CrateSurface</c> in the prop tests; ours).</summary>
    public const int CoverSurface = 4;
    /// <summary><see cref="MapWall.Group"/> of a movement wall: ALL (every player is stopped; the group Classic's own walls have).</summary>
    const int WallGroup = 2;

    public required MapDocument Document { get; init; }
    /// <summary>The name on the wire: <c>cm_</c> and the map's name as a slug, at most 24 characters (<see cref="WireName"/>).</summary>
    public required string Name { get; init; }
    public required MapContent Content { get; init; }
    public required ulong Hash { get; init; }
    public required IReadOnlyList<DrawPiece> Pieces { get; init; }
    /// <summary>The box round every solid piece (null when there is none).</summary>
    public Bounds? SolidBounds { get; init; }
    /// <summary>Everything the compiler noticed that is not an error (a rotated marker, a name clash).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>How many solid pieces made the collision.</summary>
    public int SolidCount { get; init; }
    /// <summary>The SM1 placements, prefab instances and imported models: what the client draws and the triangles they added to the collision (null: compiled without a content source).</summary>
    public DecorResult? Decor { get; init; }
    /// <summary>
    /// The exact shapes of edited markers, by the compiled name of their record (<c>cm_start_marines_1</c>, <c>marines_blocker_1</c>, <c>mkr_objective_1</c>, <c>cm_kill_1</c>): world-space
    /// closed meshes built with the deterministic maths (<see cref="MeshVolume.Contains"/> is the point-in-shape test). The record itself carries the mesh's box, so a consumer that only knows
    /// boxes still sees the edited extent, and the record also carries the shape (<c>Volume</c>) into the simulation, which then uses it exactly (plan M2). Unedited markers have no entry and behave exactly as before.
    /// </summary>
    public IReadOnlyDictionary<string, MeshVolume> Volumes { get; init; } = new Dictionary<string, MeshVolume>();
    /// <summary>The object id of each marker record (spawn areas, doors, capture points, kill zones), by the record's name: pairs a compiled record with its piece without relying on order.</summary>
    public IReadOnlyDictionary<string, string> RecordIds { get; init; } = new Dictionary<string, string>();

    public CollisionWorld ToCollisionWorld() => Content.ToCollisionWorld();

    /// <summary>The wire name for a map called <paramref name="mapName"/>: letters, digits and '_' only (the wire allows 24).</summary>
    public static string WireName(string mapName)
    {
        var sb = new StringBuilder(Prefix);
        foreach (char c in mapName.ToLowerInvariant())
        {
            if (sb.Length >= 24) break;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb[^1] != '_') sb.Append('_');
        }
        string name = sb.ToString().TrimEnd('_');
        return name.Length > Prefix.Length ? name : Prefix + "map";
    }

    /// <summary>Whether a map name from the wire names a custom map.</summary>
    public static bool IsCustomName(string? name) => name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) && name.Length > Prefix.Length;

    /// <summary>The hash both sides compare: the compiler version and the document's canonical text (the whole file: decoration too, so two testers with different edits do not meet).</summary>
    public static ulong HashOf(MapDocument doc, ulong? decorDigest = null)
    {
        // A map with SM1 placements, prefabs or imported models also hashes the triangles they made on this machine (DecorResult.Digest), so a PC whose install or models differ is refused.
        string tail = decorDigest is { } d ? $"\ndecoration {d:X16}" : "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"sm-reimpl custom map v{CompilerVersion}\n{doc.ToGameJson()}{tail}"));
        return BitConverter.ToUInt64(bytes, 0);
    }

    /// <summary>
    /// Compiles <paramref name="doc"/>. Never throws for a map that merely lacks things: <see cref="MapCheck"/> says what is missing. <paramref name="options"/> (null: none) says where the SM1
    /// placements, prefabs and imported models get their meshes; without it they are not compiled. With <see cref="CompileOptions.Strict"/> a placement whose content this PC lacks throws a
    /// <see cref="MapException"/> (<c>missing-install</c>, <c>unknown-map</c>, <c>missing-sm1-definition</c>, <c>missing-asset</c>, <c>missing-prefab</c>, <c>bad-asset</c>).
    /// </summary>
    public static CustomMap Compile(MapDocument doc, CompileOptions? options = null) => Compile(doc, options, VolumesInPlay);

    /// <summary>As <see cref="Compile(MapDocument, CompileOptions?)"/>, choosing whether edited markers' records carry their exact shape (<see cref="VolumesInPlay"/>; tests).</summary>
    public static CustomMap Compile(MapDocument doc, CompileOptions? options, bool volumesInPlay)
    {
        var notes = new List<string>();
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var pieces = new List<DrawPiece>();
        var points = new List<MapControlPoint>();
        var areas = new List<MapStartArea>();
        var blockers = new List<MapBlocker>();
        var kills = new List<MapKillRegion>();
        var walls = new List<MapWall>();
        var props = new List<MapProp>();
        var volumes = new Dictionary<string, MeshVolume>(StringComparer.Ordinal);
        var recordIds = new Dictionary<string, string>(StringComparer.Ordinal);
        Bounds? solid = null;
        int solidCount = 0, pointNumber = 0, blockerMarines = 0, blockerChaos = 0, areaMarines = 0, areaChaos = 0, killNumber = 0, wallNumber = 0, coverNumber = 0;
        foreach (var o in doc.Objects)
        {
            if (o.Piece is not { } piece) continue;
            Matrix4x4 world;
            double[] worldD;
            try { worldD = DecorGeometry.WorldOf(doc, o); world = DetMath.ToFloat(worldD); }   // the same deterministic path as the decoration: pieces are placed in double, bit-identical on every OS
            catch (MapException e) { notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
            pieces.Add(new DrawPiece(o.Id, o.Name, piece, world, o.Visible && IsVisibleChain(doc, o)) { Mesh = o.Mesh, Scale = o.Transform.Scale, Material = o.Material });
            var info = piece.Info;
            if (info.Solid)
            {
                var (v, tri) = o.Mesh is { } sm ? Unit(sm) : PieceMesh.Unit(piece.Kind)!.Value;
                int first = vertices.Count;
                Vector3[] moved;
                try { moved = v.Select(p => { var (x, y, z) = DetMath.Transform(p.X, p.Y, p.Z, worldD); return DetMath.Grid(x, y, z); }).ToArray(); }
                catch (MapException e) { notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
                vertices.AddRange(moved);
                indices.AddRange(tri.Select(i => first + i));
                var b = new Bounds(moved.Aggregate(Vector3.Min), moved.Aggregate(Vector3.Max));
                solid = solid is { } s ? s.Union(b) : b;
                solidCount++;
                continue;
            }
            if (piece.Kind == PieceKind.Cover)
            {
                // Cover: the unit box carried to the world, a prop (solid for movement and shots; pierce-capable shots pass it, as Classic's BakedProps).
                var (cv, ctri) = o.Mesh is { } cmesh ? Unit(cmesh) : PieceMesh.MarkerBox();
                Vector3[] cmoved;
                try { cmoved = cv.Select(p => { var (x, y, z) = DetMath.Transform(p.X, p.Y, p.Z, worldD); return DetMath.Grid(x, y, z); }).ToArray(); }
                catch (MapException e) { notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
                coverNumber++;
                props.Add(new MapProp($"cm_cover_{coverNumber}", "custom cover", "cover", CoverSurface, cmoved, ctri));
                var cb = new Bounds(cmoved.Aggregate(Vector3.Min), cmoved.Aggregate(Vector3.Max));   // cover counts for the kill floor and SolidCount (walls do not: movement-only, invisible)
                solid = solid is { } cs ? cs.Union(cb) : cb;
                solidCount++;
                continue;
            }
            // Markers: boxes (or a cylinder) in the document frame whose bottom is the piece's z. Only a turn about Z counts; a tilt is noted.
            if (MathF.Abs(Wrap(o.Transform.Rotation.X)) > 0.5f || MathF.Abs(Wrap(o.Transform.Rotation.Y)) > 0.5f)
                notes.Add($"'{o.Name}' ({o.Id}) is tilted; only its turn about the vertical axis is used.");
            float yaw = YawOf(doc, o);
            Vector3 size, at;
            try
            {
                // size = the lengths of the matrix rows (IEEE sqrt), position = its translation on the 1/4096 m grid
                size = new Vector3((float)Math.Sqrt(worldD[0] * worldD[0] + worldD[1] * worldD[1] + worldD[2] * worldD[2]), (float)Math.Sqrt(worldD[4] * worldD[4] + worldD[5] * worldD[5] + worldD[6] * worldD[6]), (float)Math.Sqrt(worldD[8] * worldD[8] + worldD[9] * worldD[9] + worldD[10] * worldD[10]));
                at = DetMath.Grid(worldD[12], worldD[13], worldD[14]);
            }
            catch (MapException e) { notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
            var centre = at + new Vector3(0, 0, size.Z / 2f);
            Vector3[]? edited = null;
            int[]? editedTri = null;
            if (o.Mesh is { } mm)
            {
                // An edited marker: its record is the box of its mesh along the object's own axes (centre and full size), and its exact shape goes to Volumes (the same deterministic path).
                try
                {
                    var (uv, ut) = Unit(mm);
                    edited = uv.Select(p => { var (x, y, z) = DetMath.Transform(p.X, p.Y, p.Z, worldD); return DetMath.Grid(x, y, z); }).ToArray();
                    editedTri = ut;
                    var ub = mm.UnitBounds();
                    var uc = (ub.Min + ub.Max) * 0.5f;
                    var (cx, cy, cz) = DetMath.Transform(uc.X, uc.Y, uc.Z, worldD);
                    centre = DetMath.Grid(cx, cy, cz);
                    size *= ub.Max - ub.Min;
                    // the vertical extent from the gridded world vertices themselves, so the record's bottom (Position.Z - Scale.Z / 2, where spawns stand) is exactly the volume's lowest point
                    float zMin = edited.Min(v => v.Z), zMax = edited.Max(v => v.Z);
                    centre.Z = (zMin + zMax) / 2f;
                    size.Z = zMax - zMin;
                }
                catch (MapException e) { notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
            }
            // An edited marker's exact shape: kept in Volumes under the record's name and carried by the record into the simulation (plan M2: inside the shape counts, inside only its box does not).
            // Unedited markers get none, so their records and every rule that reads them are exactly as before.
            MeshVolume? Exact(string recordName)
            {
                recordIds[recordName] = o.Id;   // every marker record, edited or not (CustomMap.RecordIds)
                if (edited is null) return null;
                var volume = new MeshVolume(recordName, edited, editedTri!);
                volumes[recordName] = volume;
                return volumesInPlay ? volume : null;   // VolumesInPlay: the switch back to M0 play
            }
            switch (piece.Kind)
            {
                case PieceKind.SpawnArea:
                {
                    var side = piece.Side == PieceSpec.Marines ? Team.SpaceMarines : Team.Chaos;
                    int n = side == Team.SpaceMarines ? ++areaMarines : ++areaChaos;
                    string rec = $"cm_start_{piece.Side}_{n}";
                    areas.Add(new MapStartArea(rec, side, centre, yaw, size, 1) { Volume = Exact(rec) });
                    break;
                }
                case PieceKind.Blocker:
                {
                    var side = piece.Side == PieceSpec.Marines ? Team.SpaceMarines : Team.Chaos;
                    int n = side == Team.SpaceMarines ? ++blockerMarines : ++blockerChaos;
                    string rec = $"{piece.Side}_blocker_{n}";
                    blockers.Add(new MapBlocker(rec, side, centre, yaw, size) { Volume = Exact(rec) });
                    break;
                }
                case PieceKind.CapturePoint:
                {
                    pointNumber++;
                    string rec = $"mkr_objective_{pointNumber}";
                    // unedited: X for both horizontal sizes, as before (a round point); edited: the mesh's box, and the exact shape
                    points.Add(new MapControlPoint(rec, centre, yaw, edited is null ? new Vector3(size.X, size.X, size.Z) : size, 3) { Volume = Exact(rec) });
                    break;
                }
                case PieceKind.MoveWall:
                    // a movement-only wall (MapWall -> CollisionWorld.Walls): position = the centre, rotation about Z in degrees, scale = the full size
                    wallNumber++;
                    var (wv, wi) = edited is not null ? (edited, editedTri!) : WallMesh(centre, size, YawDegreesOf(doc, o), piece.IsCylinder);
                    walls.Add(new MapWall($"cm_wall_{wallNumber}", centre, new Vector3(0, 0, (float)YawDegreesOf(doc, o)), size, piece.IsCylinder ? 3 : 1, WallGroup, 1) { Vertices = wv, Indices = wi });
                    break;
                case PieceKind.KillZone:
                {
                    killNumber++;
                    string rec = $"cm_kill_{killNumber}";
                    kills.Add(new MapKillRegion(rec, centre, yaw, size, 0) { Volume = Exact(rec) });
                    break;
                }
            }
        }
        if (areaMarines > 1 || areaChaos > 1) notes.Add("Only the first spawn area of each side is used by the match; the others are ignored.");
        // SM1 placements, prefab instances and imported models: drawn by the client and solid here, the same triangles on every machine (DecorGeometry).
        DecorResult? decor = options is null ? null
            : options.Cache is { } cache ? cache.GetOrBuild(DecorCache.KeyOf(doc, options.CacheTag), () => DecorGeometry.Build(doc, options))
            : DecorGeometry.Build(doc, options);
        if (decor is not null)
        {
            notes.AddRange(decor.Notes);
            if (decor.Triangles > 0)
            {
                int first = vertices.Count;
                vertices.AddRange(decor.Vertices);
                indices.AddRange(decor.Indices.Select(i => first + i));
                var db = new Bounds(decor.Vertices.Aggregate(Vector3.Min), decor.Vertices.Aggregate(Vector3.Max));
                solid = solid is { } s0 ? s0.Union(db) : db;
                solidCount += decor.Placements.Count(p => p.Solid);
            }
        }
        if (solid is { } sb)
        {
            // The pit under the map: anyone who falls this far below the lowest solid piece dies (a map with no walls round it is safe to build).
            var ext = (sb.Max - sb.Min);
            float margin = 200f;
            var size = new Vector3(ext.X + 2 * margin, ext.Y + 2 * margin, 40f);
            kills.Add(new MapKillRegion("cm_kill_floor", new Vector3((sb.Min.X + sb.Max.X) / 2f, (sb.Min.Y + sb.Max.Y) / 2f, sb.Min.Z - KillFloorDepth - 20f), 0f, size, 0));
        }
        var verts = vertices.Count == 0 ? [Vector3.Zero, Vector3.UnitX * 0.01f, Vector3.UnitY * 0.01f] : vertices.ToArray();
        var inds = indices.Count == 0 ? [0, 1, 2] : indices.ToArray();
        var layers = new HavokCollisionLayer[]
        {
            new(MapContent.CharacterLayer, verts, inds),
            new(MapContent.WeaponLayer, verts, inds),
        };
        string name = WireName(doc.Name);
        ulong hash = HashOf(doc, decor is { Placements.Count: > 0 } ? decor.Digest() : null);
        // MapContent.Spawns: one per start area, on the area's floor (World.Spawns is never empty for a team map).
        // an edited area: the spawn under a point inside its shape (its box centre may be outside the shape), on the box's floor
        static Vector3 SpawnAt(MapStartArea a, IReadOnlyDictionary<string, MeshVolume> v) => v.TryGetValue(a.Name, out var shape) && shape.InsidePoint() is { } inside
            ? new Vector3(inside.X, inside.Y, a.Position.Z - a.Scale.Z / 2f) : a.Position - new Vector3(0, 0, a.Scale.Z / 2f);
        var spawns = areas.Select(a => new MapSpawn($"cm_spawn_{a.Name}", SpawnAt(a, volumes), a.Yaw)).ToList();
        var content = new MapContent(name, "custom", layers, spawns, hash)
        {
            ControlPoints = points, StartAreas = areas, Blockers = blockers, KillRegions = kills, Walls = walls, Props = props, Notes = notes,
        };
        return new CustomMap { Document = doc, Name = name, Content = content, Hash = hash, Pieces = pieces, SolidBounds = solid, Notes = notes, SolidCount = solidCount, Decor = decor, Volumes = volumes, RecordIds = recordIds };
    }

    /// <summary>An edited shape's unit-space triangles (faces fanned from their first vertex).</summary>
    static (Vector3[] Vertices, int[] Indices) Unit(EditMesh mesh)
    {
        var (v, i, _) = mesh.Triangulate();
        return (v, i);
    }

    /// <summary>cos(pi / 16) as a literal (no libm): the circumscribed 16-sided prism's corner radius is 0.5 / this, as <see cref="WorldBlocker.Cylinder"/>.</summary>
    const double CosHalfStep16 = 0.98078528040323044913;

    /// <summary>
    /// A movement wall's world vertices, built in double with the deterministic maths only (the same shape <see cref="WorldBlocker.Box"/> / <see cref="WorldBlocker.Cylinder"/> make for Classic: a box, or a
    /// <see cref="WorldBlocker.CylinderSides"/>-sided prism whose corners lie at 0.5 / cos(pi / sides)); <c>MapWall.ToSim</c> would use float sin/cos, which may differ between operating systems.
    /// </summary>
    internal static (Vector3[] Vertices, int[] Indices) WallMesh(Vector3 centre, Vector3 size, double yawDegrees, bool cylinder)
    {
        var m = DetTransform.Local(centre.X, centre.Y, centre.Z, 0, 0, yawDegrees, size.X, size.Y, size.Z);
        Vector3 At(double x, double y, double z) { var (px, py, pz) = DetMath.Transform(x, y, z, m); return new Vector3((float)px, (float)py, (float)pz); }
        if (!cylinder)
        {
            var v = new Vector3[8];
            for (int k = 0; k < 8; k++)
            {
                (double x, double y) = (k % 4) switch { 0 => (-0.5, -0.5), 1 => (0.5, -0.5), 2 => (0.5, 0.5), _ => (-0.5, 0.5) };
                v[k] = At(x, y, k < 4 ? -0.5 : 0.5);
            }
            return (v, [.. SpawnBlocker.BoxIndices]);
        }
        int n = WorldBlocker.CylinderSides;
        double r = 0.5 / CosHalfStep16;
        var cv = new Vector3[2 * n];
        for (int i = 0; i < n; i++)
        {
            var (c, s) = DetMath.Ring(i, n);
            cv[i] = At(r * c, r * s, -0.5);
            cv[n + i] = At(r * c, r * s, 0.5);
        }
        return (cv, WorldBlocker.CylinderIndices(n));
    }

    static bool IsVisibleChain(MapDocument doc, MapObject o)
    {
        int guard = 0;
        for (var cur = o; cur is not null && guard++ < 1000; cur = cur.Parent is { } p ? doc.Find(p) : null)
            if (!cur.Visible) return false;
        return true;
    }

    static float Wrap(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; if (deg < -180f) deg += 360f; return deg; }

    /// <summary>The marker's facing (radians, counterclockwise from above, 0 = +Y): the sum of the snapped Z turns of the object and its parents, from the document's own numbers (no Atan2: a turn of z degrees about Z turns the +Y side by exactly z).</summary>
    static float YawOf(MapDocument doc, MapObject o) => (float)(YawDegreesOf(doc, o) * SmContent.DetTransform.DegToRad);

    /// <summary>The same turn in degrees (snapped to 1/1000 degree).</summary>
    static double YawDegreesOf(MapDocument doc, MapObject o)
    {
        double degrees = 0;
        int guard = 0;
        for (var cur = o; cur is not null && guard++ < 1000; cur = cur.Parent is { } p ? doc.Find(p) : null) degrees += DetMath.SnapDegrees(cur.Transform.Rotation.Z);
        return DetMath.SnapDegrees(degrees);
    }
}
