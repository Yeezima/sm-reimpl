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
            float a = i * MathF.Tau / sides;
            v.Add(new Vector3(0.5f * MathF.Cos(a), 0.5f * MathF.Sin(a), 0));
            v.Add(new Vector3(0.5f * MathF.Cos(a), 0.5f * MathF.Sin(a), 1));
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

/// <summary>One piece as the views draw it: id, kind, side, name and its world matrix (document frame, Z up).</summary>
public sealed record DrawPiece(string Id, string Name, PieceSpec Piece, Matrix4x4 World, bool Visible);

/// <summary>
/// A <c>.smmap</c> compiled for play (docs/mapmaker.md "Playing a map"): the collision of its solid pieces, its spawn areas, doors, capture points and
/// kill zones as the simulation takes them (<see cref="Content"/>, in the shape of an install's map), and the hash both sides check. The server and
/// the client compile the same file the same way, so both build the same collision from it. Decoration (SM1 pieces, imported models) is not compiled:
/// the client draws it; the server never reads it.
/// </summary>
public sealed class CustomMap
{
    /// <summary>Bump when the way pieces become collision, areas or points changes (it is part of the hash).</summary>
    public const int CompilerVersion = 2;   // 2: SM1 placements, prefab instances and imported models are solid (docs/mapmaker.md "SM1 pieces in play"); the hash covers their triangles
    public const string Prefix = "cm_";
    /// <summary>Metres below the lowest solid piece's underside at which the map kills a falling player (ours; the pit under every custom map).</summary>
    public const float KillFloorDepth = 6f;

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
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"sm-reimpl custom map v{CompilerVersion}\n{doc.ToJson()}{tail}"));
        return BitConverter.ToUInt64(bytes, 0);
    }

    /// <summary>
    /// Compiles <paramref name="doc"/>. Never throws for a map that merely lacks things: <see cref="MapCheck"/> says what is missing. <paramref name="options"/> (null: none) says where the SM1
    /// placements, prefabs and imported models get their meshes; without it they are not compiled. With <see cref="CompileOptions.Strict"/> a placement whose content this PC lacks throws a
    /// <see cref="MapException"/> (<c>missing-install</c>, <c>unknown-map</c>, <c>missing-sm1-definition</c>, <c>missing-asset</c>, <c>missing-prefab</c>, <c>bad-asset</c>).
    /// </summary>
    public static CustomMap Compile(MapDocument doc, CompileOptions? options = null)
    {
        var notes = new List<string>();
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var pieces = new List<DrawPiece>();
        var points = new List<MapControlPoint>();
        var areas = new List<MapStartArea>();
        var blockers = new List<MapBlocker>();
        var kills = new List<MapKillRegion>();
        Bounds? solid = null;
        int solidCount = 0, pointNumber = 0, blockerMarines = 0, blockerChaos = 0, areaMarines = 0, areaChaos = 0, killNumber = 0;
        foreach (var o in doc.Objects)
        {
            if (o.Piece is not { } piece) continue;
            Matrix4x4 world;
            MapTransform t;
            try { world = doc.World(o.Id); t = TransformMath.Decompose(world); }
            catch (MapException e) { notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
            pieces.Add(new DrawPiece(o.Id, o.Name, piece, world, o.Visible && IsVisibleChain(doc, o)));
            var info = piece.Info;
            if (info.Solid)
            {
                var (v, tri) = PieceMesh.Unit(piece.Kind)!.Value;
                int first = vertices.Count;
                var moved = v.Select(p => Vector3.Transform(p, world)).ToArray();
                vertices.AddRange(moved);
                indices.AddRange(tri.Select(i => first + i));
                var b = new Bounds(moved.Aggregate(Vector3.Min), moved.Aggregate(Vector3.Max));
                solid = solid is { } s ? s.Union(b) : b;
                solidCount++;
                continue;
            }
            // Markers: boxes (or a cylinder) in the document frame whose bottom is the piece's z. Only a turn about Z counts; a tilt is noted.
            if (MathF.Abs(Wrap(t.Rotation.X)) > 0.5f || MathF.Abs(Wrap(t.Rotation.Y)) > 0.5f)
                notes.Add($"'{o.Name}' ({o.Id}) is tilted; only its turn about the vertical axis is used.");
            float yaw = YawOf(world);
            var size = new Vector3(MathF.Abs(t.Scale.X), MathF.Abs(t.Scale.Y), MathF.Abs(t.Scale.Z));
            var centre = t.Position + new Vector3(0, 0, size.Z / 2f);
            switch (piece.Kind)
            {
                case PieceKind.SpawnArea:
                {
                    var side = piece.Side == PieceSpec.Marines ? Team.SpaceMarines : Team.Chaos;
                    int n = side == Team.SpaceMarines ? ++areaMarines : ++areaChaos;
                    areas.Add(new MapStartArea($"cm_start_{piece.Side}_{n}", side, centre, yaw, size, 1));
                    break;
                }
                case PieceKind.Blocker:
                {
                    var side = piece.Side == PieceSpec.Marines ? Team.SpaceMarines : Team.Chaos;
                    int n = side == Team.SpaceMarines ? ++blockerMarines : ++blockerChaos;
                    blockers.Add(new MapBlocker($"{piece.Side}_blocker_{n}", side, centre, yaw, size));
                    break;
                }
                case PieceKind.CapturePoint:
                    pointNumber++;
                    points.Add(new MapControlPoint($"mkr_objective_{pointNumber}", centre, yaw, new Vector3(size.X, size.X, size.Z), 3));
                    break;
                case PieceKind.KillZone:
                    killNumber++;
                    kills.Add(new MapKillRegion($"cm_kill_{killNumber}", centre, yaw, size, 0));
                    break;
            }
        }
        if (areaMarines > 1 || areaChaos > 1) notes.Add("Only the first spawn area of each side is used by the match; the others are ignored.");
        // SM1 placements, prefab instances and imported models: drawn by the client and solid here, the same triangles on every machine (DecorGeometry).
        DecorResult? decor = options is null ? null : DecorGeometry.Build(doc, options);
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
        var spawns = areas.Select(a => new MapSpawn($"cm_spawn_{a.Name}", a.Position - new Vector3(0, 0, a.Scale.Z / 2f), a.Yaw)).ToList();
        var content = new MapContent(name, "custom", layers, spawns, hash)
        {
            ControlPoints = points, StartAreas = areas, Blockers = blockers, KillRegions = kills, Notes = notes,
        };
        return new CustomMap { Document = doc, Name = name, Content = content, Hash = hash, Pieces = pieces, SolidBounds = solid, Notes = notes, SolidCount = solidCount, Decor = decor };
    }

    static bool IsVisibleChain(MapDocument doc, MapObject o)
    {
        int guard = 0;
        for (var cur = o; cur is not null && guard++ < 1000; cur = cur.Parent is { } p ? doc.Find(p) : null)
            if (!cur.Visible) return false;
        return true;
    }

    static float Wrap(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; if (deg < -180f) deg += 360f; return deg; }

    /// <summary>The marker's facing as the simulation takes it: the angle (radians, counterclockwise from above) of the piece's +Y side measured from +Y, the same number as an install marker's rotation about Z.</summary>
    static float YawOf(Matrix4x4 world)
    {
        var forward = new Vector3(world.M21, world.M22, 0);   // the piece's local +Y in the document frame
        if (forward.LengthSquared() < 1e-8f) return 0f;
        return MathF.Atan2(-forward.X, forward.Y);
    }
}
