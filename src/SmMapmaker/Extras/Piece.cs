using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>What a gameplay piece is (docs/mapmaker.md "Pieces"). The id is the stable string stored in the <c>.smmap</c>.</summary>
public enum PieceGroup { Build, Gameplay }

/// <summary>One kind of piece the editor offers: its stored id, labels, default size and what it needs.</summary>
/// <param name="Id">Stored in the document (<c>"piece": {"kind": id}</c>).</param>
/// <param name="Label">Short button text.</param>
/// <param name="Tip">One plain sentence for the tooltip.</param>
/// <param name="DefaultSize">Size in metres (X, Y, Z) of a new piece; the object's scale is the size.</param>
/// <param name="HasSide">Marines or Chaos must be chosen.</param>
/// <param name="Solid">Players walk on it and shots stop at it.</param>
public sealed record PieceKind(string Id, string Label, string Tip, PieceGroup Group, Vector3 DefaultSize, bool HasSide, bool Solid)
{
    public const string Box = "box", Ramp = "ramp", Column = "column", CollisionBox = "collision-box",
        SpawnArea = "spawn-area", Blocker = "blocker", CapturePoint = "capture-point", KillZone = "kill-zone";

    /// <summary>The pieces, in the order the editor lists them.</summary>
    public static readonly IReadOnlyList<PieceKind> All =
    [
        new(Box, "Block", "A solid block: floors, walls, platforms and cover. Players stand on it and shots stop at it. Scale sets its size in metres.", PieceGroup.Build, new(4, 4, 3), false, true),
        new(Ramp, "Ramp", "A solid slope that rises toward its +Y side (the green arrow). Rotate it to point uphill where you want.", PieceGroup.Build, new(4, 8, 2), false, true),
        new(Column, "Column", "A solid round pillar. Good cover that does not block the whole lane.", PieceGroup.Build, new(1.5f, 1.5f, 4), false, true),
        new(CollisionBox, "Invisible wall", "A solid block that is not drawn in the game. Put it around decoration (SM1 pieces, imported models) that should stop players.", PieceGroup.Build, new(4, 4, 3), false, true),
        new(SpawnArea, "Spawn area", "Where one side appears (and respawns). Players spawn on the floor inside this box, facing its +Y side (the green arrow). Every side needs one.", PieceGroup.Gameplay, new(10, 10, 3), true, false),
        new(Blocker, "Spawn door", "A shield that only the other side cannot cross, across the exit of a spawn room. It also tells spawning players which way to face.", PieceGroup.Gameplay, new(8, 1, 6), true, false),
        new(CapturePoint, "Capture point", "A Seize Ground point: a cylinder (diameter, diameter, height). The side standing on it captures it. Place 3 for the classic A, B, C.", PieceGroup.Gameplay, new(8, 8, 4), false, false),
        new(KillZone, "Kill zone", "Anyone who enters this box dies. Use it for pits and the sea. (The map already kills players who fall far below the lowest floor.)", PieceGroup.Gameplay, new(20, 20, 4), false, false),
    ];

    public static PieceKind? Find(string? id) => All.FirstOrDefault(k => k.Id == id);
}

/// <summary>
/// A gameplay piece on an object: the kind and, for spawn areas and doors, the side (<c>marines</c> or <c>chaos</c>). A piece has no asset or prefab; its
/// transform is its place: position = the middle of its footprint on the ground, scale = size in metres, rotation Z = facing in degrees.
/// </summary>
public sealed record PieceSpec(string Kind, string? Side = null)
{
    public const string Marines = "marines", Chaos = "chaos";

    public PieceKind Info => PieceKind.Find(Kind) ?? throw new MapException("bad-piece", $"Unknown piece kind '{Kind}'.");

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["kind"] = Kind };
        if (Side is not null) o["side"] = Side;
        return o;
    }

    public static PieceSpec FromNode(JsonNode? n, string what, string code)
    {
        var o = Json.Obj(n, what, code);
        var kind = Json.ReqStr(o, "kind", what, code);
        var info = PieceKind.Find(kind) ?? throw new MapException(code, $"{what}.kind '{kind}' is not a piece kind ({string.Join(", ", PieceKind.All.Select(k => k.Id))}).");
        var side = Json.Str(o["side"], what + ".side", code, nullable: true);
        if (info.HasSide && side is not (Marines or Chaos)) throw new MapException(code, $"{what}: a {info.Label.ToLowerInvariant()} needs \"side\": \"marines\" or \"chaos\".");
        if (!info.HasSide && side is not null) throw new MapException(code, $"{what}: a {info.Label.ToLowerInvariant()} has no side.");
        return new PieceSpec(kind, side);
    }

    /// <summary>A readable name for a new piece: "Marine spawn area", "Block".</summary>
    public string DefaultName => (Side == Marines ? "Marine " : Side == Chaos ? "Chaos " : "") + (Side is null ? Info.Label : Info.Label.ToLowerInvariant());
}
