using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>The kit's categories, as the generated library's manifest names them (Ground: the procedural prototypes AC-033..040).</summary>
public enum AshfallKitCategory : byte { Architecture, Cover, Debris, Landmark, Ground }

/// <summary>
/// One model of the Ashfall kit (the owner's generated asset library, 2026-11-05; decision agent-coordination decisions/2026-11-05-ashfall-kit-dressing.md).
/// The models are NOT in the repository: the client reads them from the owner's private folder (<c>%LOCALAPPDATA%\sm-reimpl\ashfall-kit\source\</c> + <see cref="File"/>).
/// <see cref="RawMin"/>/<see cref="RawMax"/> are the measured POSITION bounds in the file's own glTF frame (Y up; the Meshy models are normalised to about 2 m and centred,
/// the ground prototypes are in metres with the paved surface at Y = 0). <see cref="ProposedFootprint"/> and <see cref="ProposedHeight"/> are the manifest's provisional
/// design targets in metres (text, not measured). <see cref="Review"/>: the manifest flags the geometry for review (AC-015, AC-016, AC-026): decoration only or unused.
/// </summary>
public sealed record AshfallKitModel(string Id, string Name, AshfallKitCategory Category, string File, Vector3 RawMin, Vector3 RawMax, int Triangles,
    string ProposedFootprint, string ProposedHeight, bool Review)
{
    /// <summary>Measured size in the file's glTF frame (X, Y up, Z).</summary>
    public Vector3 RawSize => RawMax - RawMin;
}

/// <summary>The kit catalogue (measured 2026-11-05 from the delivered files by the lead; numbers only, no asset bytes).</summary>
public static class AshfallKit
{
    /// <summary>Folder under <c>%LOCALAPPDATA%\sm-reimpl\</c> that holds the extracted library (<c>source\</c>).</summary>
    public const string PrivateFolder = "ashfall-kit";

    public static readonly IReadOnlyList<AshfallKitModel> Models =
    [
        new("AC-033", "fractured avenue segment", AshfallKitCategory.Ground, "ashfall-ground/models/AC-033_fractured_avenue_segment.glb", new(-8.0000f, -0.3000f, -4.0000f), new(8.0000f, 0.0000f, 4.0000f), 6444, "measured", "measured", Review: false),
        new("AC-034", "avenue t junction", AshfallKitCategory.Ground, "ashfall-ground/models/AC-034_avenue_t_junction.glb", new(-8.0000f, -0.3000f, -8.0000f), new(8.0000f, 0.0000f, 8.0000f), 8942, "measured", "measured", Review: false),
        new("AC-035", "avenue crossroads", AshfallKitCategory.Ground, "ashfall-ground/models/AC-035_avenue_crossroads.glb", new(-8.0000f, -0.3000f, -8.0000f), new(8.0000f, 0.0000f, 8.0000f), 10250, "measured", "measured", Review: false),
        new("AC-036", "plaza edge tile", AshfallKitCategory.Ground, "ashfall-ground/models/AC-036_plaza_edge_tile.glb", new(-4.0000f, -0.3000f, -4.0000f), new(4.0000f, 0.0000f, 4.0000f), 1612, "measured", "measured", Review: false),
        new("AC-037", "objective plaza center tile", AshfallKitCategory.Ground, "ashfall-ground/models/AC-037_objective_plaza_center_tile.glb", new(-8.0000f, -0.3000f, -8.0000f), new(8.0000f, 1.2000f, 8.0000f), 7136, "measured", "measured", Review: false),
        new("AC-038", "plaza corner tile", AshfallKitCategory.Ground, "ashfall-ground/models/AC-038_plaza_corner_tile.glb", new(-4.0000f, -0.3000f, -4.0000f), new(4.0000f, 0.0000f, 4.0000f), 1780, "measured", "measured", Review: false),
        new("AC-039", "broken curb and drain strip", AshfallKitCategory.Ground, "ashfall-ground/models/AC-039_broken_curb_and_drain_strip.glb", new(-4.0000f, -0.3000f, -0.5000f), new(4.0000f, 0.1000f, 0.5000f), 940, "measured", "measured", Review: false),
        new("AC-040", "cratered roadway variant", AshfallKitCategory.Ground, "ashfall-ground/models/AC-040_cratered_roadway_variant.glb", new(-8.0000f, -1.2000f, -4.0000f), new(8.0000f, 0.0000f, 4.0000f), 9120, "measured", "measured", Review: false),
        new("AC-001", "Ruined defensive wall", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-001.glb", new(-1.0000f, -0.4396f, -0.2479f), new(1.0000f, 0.4396f, 0.2479f), 29825, "8 x 1.5", "4", Review: false),
        new("AC-002", "Gothic arch remnant", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-002.glb", new(-1.0000f, -0.5840f, -0.3088f), new(1.0000f, 0.5840f, 0.3088f), 30312, "8 x 2", "8", Review: false),
        new("AC-003", "Gothic 90-degree corner", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-003.glb", new(-1.0000f, -0.6422f, -0.9520f), new(1.0000f, 0.6422f, 0.9520f), 29580, "4 x 4", "4", Review: false),
        new("AC-004", "Broken three-bay facade", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-004.glb", new(-1.0000f, -0.5313f, -0.3180f), new(1.0000f, 0.5313f, 0.3180f), 30025, "12 x 2", "8", Review: false),
        new("AC-005", "Two-storey defensive ruin", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-005.glb", new(-1.0000f, -0.5292f, -0.4265f), new(1.0000f, 0.5292f, 0.4265f), 29889, "12 x 8", "16", Review: false),
        new("AC-006", "Fortified gate segment", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-006.glb", new(-1.0000f, -0.5140f, -0.3590f), new(1.0000f, 0.5140f, 0.3590f), 30188, "12 x 4", "8", Review: false),
        new("AC-007", "Exterior stair access ruin", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-007.glb", new(-0.4609f, -0.5506f, -1.0000f), new(0.4609f, 0.5506f, 1.0000f), 30208, "8 x 8", "8", Review: false),
        new("AC-008", "Elevated firing perch", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-008.glb", new(-1.0000f, -0.5160f, -0.3436f), new(1.0000f, 0.5160f, 0.3436f), 29947, "8 x 4", "8", Review: false),
        new("AC-009", "Elevated walkway span", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-009.glb", new(-1.0000f, -0.5489f, -0.2744f), new(1.0000f, 0.5489f, 0.2744f), 27748, "16 x 4", "8", Review: false),
        new("AC-010", "Tower lookout stump", AshfallKitCategory.Architecture, "completed_meshy_assets/models/AC-010.glb", new(-0.7921f, -1.0000f, -0.5693f), new(0.7921f, 1.0000f, 0.5693f), 30183, "8 x 8", "24", Review: false),
        new("AC-011", "Low barricade emplacement", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-011.glb", new(-1.0000f, -0.2651f, -0.2585f), new(1.0000f, 0.2651f, 0.2585f), 29846, "8 x 2", "2", Review: false),
        new("AC-012", "Crater edge cover segment", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-012.glb", new(-1.0000f, -0.2852f, -0.5971f), new(1.0000f, 0.2852f, 0.5971f), 30444, "8 x 4", "2.5", Review: false),
        new("AC-013", "Monument plinth fragment", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-013.glb", new(-1.0000f, -0.5164f, -0.4290f), new(1.0000f, 0.5164f, 0.4290f), 29323, "4 x 4", "4", Review: false),
        new("AC-014", "Short half-height wall variant", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-014.glb", new(-0.9517f, -0.6495f, -0.6115f), new(0.9507f, 0.6533f, 0.6136f), 10737, "4 x 1.5", "2", Review: false),
        new("AC-015", "Broken buttress cover chunk", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-015.glb", new(-0.9518f, -0.7419f, -0.2356f), new(0.9509f, 0.7452f, 0.2401f), 18499, "4 x 3", "3", Review: true),
        new("AC-016", "Half-height ruin partition", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-016.glb", new(-0.9512f, -0.5069f, -0.2144f), new(0.9498f, 0.5074f, 0.2131f), 14737, "8 x 1", "2", Review: true),
        new("AC-017", "Masonry and sandbag hybrid barrier", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-017.glb", new(-0.9507f, -0.4811f, -0.2725f), new(0.9497f, 0.4797f, 0.2684f), 16479, "4 x 2", "1.5", Review: false),
        new("AC-018", "Chokepoint debris barrier", AshfallKitCategory.Cover, "completed_meshy_assets/models/AC-018.glb", new(-0.9517f, -0.4004f, -0.9026f), new(0.8760f, 0.3876f, 0.9264f), 18299, "8 x 3", "3", Review: false),
        new("AC-019", "Large rubble cluster", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-019.glb", new(-1.0000f, -0.3194f, -0.8499f), new(1.0000f, 0.3194f, 0.8499f), 30676, "8 x 5", "4", Review: false),
        new("AC-020", "Scatter rubble cluster", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-020.glb", new(-0.9856f, -0.1621f, -1.0000f), new(0.9856f, 0.1621f, 1.0000f), 30505, "3 x 2.2", "0.5", Review: false),
        new("AC-021", "Column debris cluster", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-021.glb", new(-1.0000f, -0.4836f, -0.8425f), new(1.0000f, 0.4836f, 0.8425f), 30865, "6 x 5", "3", Review: false),
        new("AC-022", "Broken paving patch", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-022.glb", new(-0.9519f, -0.0289f, -0.9517f), new(0.9507f, 0.0359f, 0.9510f), 8051, "4 x 4", "0.4", Review: false),
        new("AC-023", "Masonry chunk pile", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-023.glb", new(-0.9059f, -0.4250f, -0.9468f), new(0.9097f, 0.4216f, 0.9398f), 8109, "4 x 3", "1.8", Review: false),
        new("AC-024", "Small ash-stone scatter", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-024.glb", new(-0.8235f, -0.1510f, -0.8362f), new(0.8248f, 0.1461f, 0.7770f), 7545, "2 x 2", "0.2", Review: false),
        new("AC-025", "Carved trim fragment cluster", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-025.glb", new(-0.9507f, -0.2049f, -0.6665f), new(0.9508f, 0.2018f, 0.6712f), 12373, "3 x 2", "0.8", Review: false),
        new("AC-026", "Fallen arch pieces", AshfallKitCategory.Debris, "completed_meshy_assets/models/AC-026.glb", new(-0.7918f, -0.6083f, -0.9508f), new(0.8094f, 0.6082f, 0.9507f), 7941, "6 x 3", "2", Review: true),
        new("AC-027", "Shattered saint monument", AshfallKitCategory.Landmark, "completed_meshy_assets/models/AC-027.glb", new(-1.0000f, -0.7855f, -0.9019f), new(1.0000f, 0.7855f, 0.9019f), 30891, "8 x 6", "8", Review: false),
        new("AC-028", "Monument pedestal core", AshfallKitCategory.Landmark, "completed_meshy_assets/models/AC-028.glb", new(-1.0000f, -0.4982f, -0.9104f), new(1.0000f, 0.4982f, 0.9104f), 29914, "8 x 6", "4", Review: false),
        new("AC-029", "Ruined civic shrine fragment", AshfallKitCategory.Landmark, "completed_meshy_assets/models/AC-029.glb", new(-0.5425f, -0.7060f, -0.9507f), new(0.5419f, 0.7061f, 0.9481f), 7958, "8 x 4", "8", Review: false),
        new("AC-030", "Collapsed buttress mass", AshfallKitCategory.Landmark, "completed_meshy_assets/models/AC-030.glb", new(-1.0000f, -0.4887f, -0.3830f), new(1.0000f, 0.4887f, 0.3830f), 30357, "24 x 12", "16", Review: false),
        new("AC-031", "Broken imperial relief slab", AshfallKitCategory.Landmark, "completed_meshy_assets/models/AC-031.glb", new(-0.9509f, -0.6680f, -0.2323f), new(0.9504f, 0.6597f, 0.2375f), 8238, "4 x 2", "2", Review: false),
        new("AC-032", "Statue fragment cluster", AshfallKitCategory.Landmark, "completed_meshy_assets/models/AC-032.glb", new(-0.9512f, -0.5771f, -0.5890f), new(0.9496f, 0.5730f, 0.5888f), 8197, "5 x 4", "2.5", Review: false),
    ];

    static readonly Dictionary<string, AshfallKitModel> ById = Models.ToDictionary(m => m.Id);

    public static AshfallKitModel Get(string id) => ById[id];
}

/// <summary>What a dressing item is to the game.</summary>
public enum AshfallDressRole : byte
{
    /// <summary>
    /// Draft 3: a scaled replacement. The item draws over a whole graybox structure (<see cref="AshfallDressItem.Part"/> is one representative part of it, or -1); every solid part of the structure except the
    /// ground kinds (Ground, Road, Paving, Curb) and the boundary is listed in <see cref="AshfallDressing.HiddenParts"/> and the kit look draws none of them. The item's box stays within its structure's bounds grown by 0.5 m.
    /// </summary>
    Skin,
    /// <summary>Walk-over decoration: no collision, never above 0.35 m where it stands in a walked place (unused since draft 3).</summary>
    Decoration,
    /// <summary>An added model with its own collision proxy (draft 2 only; the level of draft 3 has none).</summary>
    Prop,
    /// <summary>A ground, road or plaza tile laid over walkable ground (visual only; the ground part under it is the collision).</summary>
    Surface,
}

/// <summary>
/// One placed kit model, in the simulation frame (X east, Y north, Z up, metres). The client fits the model's raw bounds into the item's box:
/// the box is centred on <see cref="Position"/> in X/Y with its bottom at <see cref="Position"/>.Z, <see cref="Size"/>.X along the item's local X (length),
/// <see cref="Size"/>.Y along local Y (depth), <see cref="Size"/>.Z up; <see cref="Yaw"/> turns local X toward +Y (radians, counter-clockwise seen from above).
/// Model axes: glTF +Y is up; when <see cref="SwapXZ"/> is false glTF X is local X and glTF -Z is local Y, when true glTF Z is local X and glTF X is local Y.
/// Scale per axis = Size / (raw size on the mapped axis), so the drawn model fills the box exactly (non-uniform: draft 3 scales the models up to the graybox structures).
/// The fit is a rotation, not a mirror: with <see cref="SwapXZ"/> false the model's front (glTF +Z) faces local -Y (south at yaw 0). The dressing turns wall fronts outward.
/// </summary>
public sealed record AshfallDressItem(string Asset, Vector3 Position, float Yaw, Vector3 Size, bool SwapXZ, AshfallDressRole Role, int Part);

/// <summary>One structure of the dressing (draft 3): a group of touching hidden graybox parts (a building, a tower with its stair, a wall run, a rampart...), its parts and the bounds of their corners. A Skin item's box stays within these bounds grown by 0.5 m.</summary>
public sealed record AshfallStructure(IReadOnlyList<int> Parts, Vector3 Min, Vector3 Max);

/// <summary>The dressing of a level: its items in a fixed order, the graybox part indices the kit look hides (draft 3: every solid part except Ground, Road, Paving, Curb and Boundary) and the structures.</summary>
public sealed record AshfallDressing(IReadOnlyList<AshfallDressItem> Items, IReadOnlyList<int> HiddenParts, uint Version, IReadOnlyList<AshfallStructure>? Structures = null);
