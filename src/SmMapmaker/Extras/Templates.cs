using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>A starter layout: a name, a plain description and the operations that build it (one batch, so New from template is a single undo step).</summary>
public sealed record MapTemplate(string Id, string Label, string Description, Func<IReadOnlyList<JsonObject>> Ops)
{
    public static readonly IReadOnlyList<MapTemplate> All =
    [
        new("arena", "Two-base arena (recommended)", "Two spawn rooms with doors at the ends of a walled arena, a raised middle with ramps, three capture points (A, B, C) and cover. Plays Annihilation and Seize Ground.", Arena),
        new("open-field", "Open field", "A flat square with no walls, a spawn area for each side and three capture points. Edges are safe: anyone who falls off dies.", OpenField),
        new("blank", "Blank (floor and spawns)", "Just a floor, a spawn area and a door for each side. Add cover and capture points yourself.", Blank),
    ];

    public static MapTemplate? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    // ---- builders ----

    static JsonArray V(float x, float y, float z) => new(JsonValue.Create((double)x), JsonValue.Create((double)y), JsonValue.Create((double)z));

    /// <summary>A piece at <paramref name="at"/> (the middle of its footprint on the ground it stands on), size in metres, turn in degrees.</summary>
    public static JsonObject Piece(string kind, string name, Vector3 at, Vector3 size, float turn = 0f, string? side = null)
    {
        var piece = new JsonObject { ["kind"] = kind };
        if (side is not null) piece["side"] = side;
        return new JsonObject
        {
            ["op"] = "place", ["name"] = name, ["piece"] = piece,
            ["transform"] = new JsonObject { ["position"] = V(at.X, at.Y, at.Z), ["rotation"] = V(0, 0, turn), ["scale"] = V(size.X, size.Y, size.Z) },
        };
    }

    static void Walls(List<JsonObject> o, float halfX, float halfY, float height)
    {
        const float t = 1f;
        o.Add(Piece("box", "Wall north", new(0, halfY + t / 2f, 0), new(2 * halfX + 2 * t, t, height)));
        o.Add(Piece("box", "Wall south", new(0, -halfY - t / 2f, 0), new(2 * halfX + 2 * t, t, height)));
        o.Add(Piece("box", "Wall east", new(halfX + t / 2f, 0, 0), new(t, 2 * halfY, height)));
        o.Add(Piece("box", "Wall west", new(-halfX - t / 2f, 0, 0), new(t, 2 * halfY, height)));
    }

    static IReadOnlyList<JsonObject> Arena()
    {
        var o = new List<JsonObject>();
        o.Add(Piece("box", "Floor", new(0, 0, -1), new(100, 60, 1)));
        Walls(o, 50, 30, 8);
        // Spawn rooms: 22 m deep at each end, walled off from the arena except for a 12 m doorway guarded by a door.
        foreach (var (sx, side, name, turn) in new[] { (-1f, "marines", "Marine", -90f), (1f, "chaos", "Chaos", 90f) })
        {
            float wallX = sx * 28f;
            o.Add(Piece("box", $"{name} room wall (north)", new(wallX, 18, 0), new(1, 24, 6)));
            o.Add(Piece("box", $"{name} room wall (south)", new(wallX, -18, 0), new(1, 24, 6)));
            o.Add(Piece("blocker", $"{name} spawn door", new(wallX, 0, 0), new(1, 12, 6), 0f, side));
            o.Add(Piece("spawn-area", $"{name} spawn area", new(sx * 40f, 0, 0), new(14, 20, 3), turn, side));
        }
        // The middle: a raised platform with a ramp on each side, point B on top.
        o.Add(Piece("box", "Middle platform", new(0, 0, 0), new(16, 16, 1.2f)));
        o.Add(Piece("ramp", "Middle ramp (west)", new(-12, 0, 0), new(6, 8, 1.2f), -90f));
        o.Add(Piece("ramp", "Middle ramp (east)", new(12, 0, 0), new(6, 8, 1.2f), 90f));
        o.Add(Piece("capture-point", "Point B", new(0, 0, 1.2f), new(8, 8, 4)));
        // Points A and C in the side lanes, each with cover.
        o.Add(Piece("capture-point", "Point A", new(0, 21, 0), new(8, 8, 4)));
        o.Add(Piece("capture-point", "Point C", new(0, -21, 0), new(8, 8, 4)));
        foreach (float y in new[] { 21f, -21f })
        {
            o.Add(Piece("box", "Cover", new(-7, y, 0), new(2, 6, 1.4f)));
            o.Add(Piece("box", "Cover", new(7, y, 0), new(2, 6, 1.4f)));
        }
        // Cover in the lanes between the rooms and the middle.
        foreach (float x in new[] { -18f, 18f })
        {
            o.Add(Piece("column", "Pillar", new(x, 10, 0), new(1.6f, 1.6f, 4)));
            o.Add(Piece("column", "Pillar", new(x, -10, 0), new(1.6f, 1.6f, 4)));
            o.Add(Piece("box", "Crate", new(x, 0, 0), new(3, 3, 1.5f)));
        }
        return o;
    }

    static IReadOnlyList<JsonObject> OpenField()
    {
        var o = new List<JsonObject>();
        o.Add(Piece("box", "Floor", new(0, 0, -1), new(90, 90, 1)));
        o.Add(Piece("spawn-area", "Marine spawn area", new(-36, 0, 0), new(12, 16, 3), -90f, "marines"));
        o.Add(Piece("spawn-area", "Chaos spawn area", new(36, 0, 0), new(12, 16, 3), 90f, "chaos"));
        o.Add(Piece("blocker", "Marine spawn door", new(-28, 0, 0), new(16, 1, 6), -90f, "marines"));
        o.Add(Piece("blocker", "Chaos spawn door", new(28, 0, 0), new(16, 1, 6), 90f, "chaos"));
        o.Add(Piece("capture-point", "Point A", new(0, 22, 0), new(8, 8, 4)));
        o.Add(Piece("capture-point", "Point B", new(0, 0, 0), new(8, 8, 4)));
        o.Add(Piece("capture-point", "Point C", new(0, -22, 0), new(8, 8, 4)));
        foreach (var (x, y) in new[] { (-10f, 12f), (10f, 12f), (-10f, -12f), (10f, -12f), (0f, 11f), (0f, -11f), (-18f, 0f), (18f, 0f) })
            o.Add(Piece("box", "Cover", new(x, y, 0), new(3, 3, 1.5f)));
        return o;
    }

    static IReadOnlyList<JsonObject> Blank()
    {
        var o = new List<JsonObject>();
        o.Add(Piece("box", "Floor", new(0, 0, -1), new(70, 50, 1)));
        o.Add(Piece("spawn-area", "Marine spawn area", new(-26, 0, 0), new(12, 16, 3), -90f, "marines"));
        o.Add(Piece("spawn-area", "Chaos spawn area", new(26, 0, 0), new(12, 16, 3), 90f, "chaos"));
        o.Add(Piece("blocker", "Marine spawn door", new(-18, 0, 0), new(16, 1, 6), -90f, "marines"));
        o.Add(Piece("blocker", "Chaos spawn door", new(18, 0, 0), new(16, 1, 6), 90f, "chaos"));
        return o;
    }
}
