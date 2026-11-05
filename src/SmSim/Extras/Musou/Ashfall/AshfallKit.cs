using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>The kit's categories, as the generated library's manifest names them (Ground: the procedural prototypes AC-033..040 and the Meshy expansion's tiles; Boundary: the expansion's boundary panels).</summary>
public enum AshfallKitCategory : byte { Architecture, Cover, Debris, Landmark, Ground, Boundary }

/// <summary>
/// Placement restrictions and known defects of a kit model (draft 6, the Meshy expansion's QA of 2026-11-05: <c>QA\INTEGRATION-BLOCKERS.md</c>, <c>QA\GEOMETRY-QA.md</c>). Kept in the catalogue; the placement pass enforces them.
/// </summary>
[Flags]
public enum AshfallKitFlags : ushort
{
    None = 0,
    /// <summary>Never a traversal piece (no walkable top, stair or route may depend on it): 08H's upper stair landings end 0.155 raw below the deck; 03's ground bays are not proven open. Decoration, backdrop or a solid block only.</summary>
    TraversalQuarantined = 1,
    /// <summary>Roof or tower-top access is not established (02, 10): the ground level and the visible stairs may be used, the roofs are never walkable-critical (no objective, spawn or required route up there).</summary>
    RoofAccessUnverified = 2,
    /// <summary>The ends are squared and intact (01): never used as the broken end of a breach.</summary>
    IntactEnds = 4,
    /// <summary>The import normalises long and short cover alike (about 1.9 raw), so their 2:1 ratio is lost; <see cref="AshfallKitModel.DesignScale"/> restores the intended 4 m / 2 m lengths.</summary>
    CoverRatioNotPreserved = 8,
    /// <summary>A firing platform whose deck edges are bare by the owner's revision: no railing, parapet or lip may be added.</summary>
    BareEdges = 16,
    /// <summary>The file's axes are corrected on import (<see cref="AshfallKitModel.ImportFix"/>); the raw bounds and the collision are in the corrected frame.</summary>
    OrientationCorrected = 32,
    /// <summary>A termination piece (18C, broken top): a cap, never stacked under another panel or repeated as a seamless module.</summary>
    TerminationOnly = 64,
    /// <summary>A ground, road, plaza or boundary tile whose borders and textures are not engineered seamless: adjacent tiles may show seams.</summary>
    SeamsUnverified = 128,
}

/// <summary>An axis correction applied to a model file's positions before anything else reads them (the client draws with it, the collision is fitted after it).</summary>
public enum AshfallKitImportFix : byte
{
    None,
    /// <summary>The file stands upright in its X-Y plane (16E): a quarter turn about X, file (x, y, z) to model (x, z, -y), lays it flat with the file's +Z face up (both faces carry the same detail; the choice of face is ours).</summary>
    LayFlat,
}

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

    /// <summary>The model's id in the library it came from ("" for the first library; "08H" etc. for the Meshy expansion).</summary>
    public string SourceId { get; init; } = "";

    /// <summary>The placement family the model is meant for (draft 6; "" when the older pools decide).</summary>
    public string Family { get; init; } = "";

    /// <summary>What the model is, in short, from the library's catalogue.</summary>
    public string Purpose { get; init; } = "";

    /// <summary>Metres per raw unit at the model's intended real size (uniform; 0 when the catalogue records no scale). Derived from the library catalogue's intended dimensions; <see cref="ScaleSource"/> says how.</summary>
    public float DesignScale { get; init; }

    /// <summary>Where <see cref="DesignScale"/> comes from: the catalogue's dimension it reads, or "ours, estimate: ..." when it is our reading.</summary>
    public string ScaleSource { get; init; } = "";

    /// <summary>The tallest stair riser measured in the raw frame (tread levels of the up-facing surfaces; 0 when the model has no measured stair).</summary>
    public float RawRiser { get; init; }

    public AshfallKitFlags Flags { get; init; }

    /// <summary>The axis correction applied to the file's positions (<see cref="RawMin"/>, <see cref="RawMax"/> and the collision are in the corrected frame).</summary>
    public AshfallKitImportFix ImportFix { get; init; }

    /// <summary>Size in metres at <see cref="DesignScale"/> (X, Y up, Z; zero when no scale is recorded).</summary>
    public Vector3 DesignSize => RawSize * DesignScale;

    /// <summary>A position of the model's file in the model's frame (<see cref="ImportFix"/> applied).</summary>
    public Vector3 FileToModel(Vector3 p) => ImportFix switch
    {
        AshfallKitImportFix.LayFlat => new Vector3(p.X, p.Z, -p.Y),
        _ => p,
    };
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
        // The Meshy expansion of 2026-11-05 (36 models, the owner's request; draft 6). Bounds measured from the files (glTF frame, Y up; AC-071 after its import fix). Design scales: ScaleSource.
        X("AC-041", "01", "ruined curtain wall", AshfallKitCategory.Architecture, "01-ruined-curtain-wall.glb", new(-0.9518f, -0.2867f, -0.1772f), new(0.9511f, 0.3013f, 0.1762f), 25286, "wall run", "ruined curtain wall, walkable top between parapets", 8.490f,
            "ours, estimate: the catalogue's walkable 3 m top read as the wall's whole depth (0.353 raw, parapets included)") with { Flags = AshfallKitFlags.IntactEnds },
        X("AC-042", "02", "gatehouse", AshfallKitCategory.Architecture, "02-gatehouse.glb", new(-0.9522f, -0.6265f, -0.4702f), new(0.9503f, 0.6261f, 0.4692f), 24543, "stronghold gate", "gatehouse: two broken towers, wide open passage, exterior stair to a mid deck", 7.757f,
            "ours, estimate: the mid deck (0.774 raw above the base) at one 6 m storey (the catalogue's 6 m first floor)") with { Flags = AshfallKitFlags.RoofAccessUnverified },
        X("AC-043", "03", "corner bastion", AshfallKitCategory.Architecture, "03-corner-bastion.glb", new(-0.7680f, -0.3703f, -0.9512f), new(0.7670f, 0.3696f, 0.9504f), 24641, "stronghold corner", "square corner bastion with an exterior stair to its upper deck", 10.431f,
            "ours, estimate: the upper deck (0.575 raw) at one 6 m storey; its stair risers (about 0.024 raw) are then 0.25 m") with { Flags = AshfallKitFlags.TraversalQuarantined, RawRiser = 0.024f },
        X("AC-044", "04", "gothic hall keep", AshfallKitCategory.Architecture, "04-gothic-hall-keep.glb", new(-0.9511f, -0.7426f, -0.8262f), new(0.9505f, 0.7349f, 0.8291f), 26811, "keep", "gothic hall keep: front entrance, exterior stair, roof with a stair opening", 8.876f,
            "ours, estimate: the exterior stair's landing (0.676 raw) at one 6 m storey; the roof is then 11.1 m"),
        X("AC-045", "05", "stone bridge", AshfallKitCategory.Architecture, "05-stone-bridge.glb", new(-0.9516f, -0.3628f, -0.4095f), new(0.9509f, 0.3610f, 0.4101f), 27025, "bridge", "stone bridge on arches: flat deck, two parapets, open ends", 10.079f,
            "ours, estimate: the deck (0.595 raw) at 6 m, one storey like the ramparts"),
        X("AC-046", "06", "freestanding stair", AshfallKitCategory.Architecture, "06-freestanding-stair.glb", new(-0.9517f, -0.4676f, -0.3919f), new(0.9511f, 0.4656f, 0.3921f), 22694, "stair", "freestanding one-storey stair with side parapets and a top landing", 7.553f,
            "catalogue's 0.25 m riser: 24 risers of about 0.0331 raw up to the landing (0.796 raw), which is then 6.0 m") with { RawRiser = 0.0331f },
        X("AC-047", "07", "rubble ramp", AshfallKitCategory.Debris, "07-rubble-ramp.glb", new(-0.7085f, -0.2115f, -0.9517f), new(0.7167f, 0.1990f, 0.9510f), 29290, "ramp", "walkable rubble slope with a flat top landing, high end a vertical face", 7.308f,
            "catalogue's ramp reaching about 3 m (0.410 raw)"),
        X("AC-048", "08H", "firing platform high", AshfallKitCategory.Architecture, "08H-firing-platform-high.glb", new(-0.7879f, -0.3675f, -0.9523f), new(0.7880f, 0.3665f, 0.9508f), 24500, "shooting platform", "high firing platform: plinth, built-in stairs, bare deck edges", 11.364f,
            "catalogue's 0.25 m riser (about 0.022 raw); the deck is then 8.3 m") with { Flags = AshfallKitFlags.TraversalQuarantined | AshfallKitFlags.BareEdges, RawRiser = 0.022f },
        X("AC-049", "08L", "firing platform low", AshfallKitCategory.Architecture, "08L-firing-platform-low.glb", new(-0.8125f, -0.1997f, -0.9520f), new(0.8120f, 0.1988f, 0.9517f), 24267, "shooting platform", "low firing platform: plinth, built-in stair, bare deck edges", 9.091f,
            "catalogue's 0.25 m riser (about 0.0275 raw); the deck is then 3.6 m") with { Flags = AshfallKitFlags.BareEdges, RawRiser = 0.0275f },
        X("AC-050", "08M", "firing platform medium", AshfallKitCategory.Architecture, "08M-firing-platform-medium.glb", new(-0.6146f, -0.2775f, -0.9520f), new(0.6138f, 0.2734f, 0.9510f), 22657, "shooting platform", "medium firing platform: plinth, built-in stair, bare deck edges", 10.638f,
            "catalogue's 0.25 m riser (about 0.0235 raw); the deck is then 5.8 m") with { Flags = AshfallKitFlags.BareEdges, RawRiser = 0.0235f },
        X("AC-051", "09", "two storey ruin", AshfallKitCategory.Architecture, "09-two-storey-ruin.glb", new(-0.9507f, -0.8305f, -0.9514f), new(0.9388f, 0.8261f, 0.9507f), 26036, "ruin shell", "two-storey roofless shell: open interior, upper floor, internal stair", 8.423f,
            "catalogue's 6 m first floor: the upper floor (0.808 raw) over the ground floor (0.095 raw)"),
        X("AC-052", "10", "tower stump", AshfallKitCategory.Architecture, "10-tower-stump.glb", new(-0.7567f, -0.9520f, -0.7535f), new(0.7531f, 0.9509f, 0.7526f), 27420, "tall building", "tower stump: doorway, lookout roof, stair cut into the wall", 9.946f,
            "ours, estimate: the lookout roof (1.508 raw) at 15 m") with { Flags = AshfallKitFlags.RoofAccessUnverified },
        X("AC-053", "11", "climbable breach mound", AshfallKitCategory.Debris, "11-climbable-breach-mound.glb", new(-0.9517f, -0.1442f, -0.9527f), new(0.9429f, 0.1415f, 0.9529f), 30726, "rubble", "low rubble mound climbable from both sides", 7.001f,
            "ours, estimate: a low mound 2 m high (0.286 raw)"),
        X("AC-054", "12LA", "low cover long carved", AshfallKitCategory.Cover, "12LA-low-cover-long-carved.glb", new(-0.9517f, -0.2418f, -0.0988f), new(0.9507f, 0.2250f, 0.0924f), 25280, "cover run", "low cover, long, carved", 2.103f,
            "catalogue's 4 m length (1.902 raw)") with { Flags = AshfallKitFlags.CoverRatioNotPreserved },
        X("AC-055", "12LB", "low cover long broken", AshfallKitCategory.Cover, "12LB-low-cover-long-broken.glb", new(-0.9520f, -0.2861f, -0.0892f), new(0.9510f, 0.2849f, 0.0882f), 24793, "cover run", "low cover, long, broken top", 2.102f,
            "catalogue's 4 m length (1.903 raw)") with { Flags = AshfallKitFlags.CoverRatioNotPreserved },
        X("AC-056", "12LC", "low cover long blocks", AshfallKitCategory.Cover, "12LC-low-cover-long-blocks.glb", new(-0.9516f, -0.2114f, -0.1514f), new(0.9509f, 0.2134f, 0.1538f), 28557, "cover run", "low cover, long, blocks", 2.103f,
            "catalogue's 4 m length (1.902 raw)") with { Flags = AshfallKitFlags.CoverRatioNotPreserved },
        X("AC-057", "12SA", "low cover short carved", AshfallKitCategory.Cover, "12SA-low-cover-short-carved.glb", new(-0.9508f, -0.4706f, -0.1856f), new(0.9510f, 0.4712f, 0.1798f), 23968, "cover run", "low cover, short, carved", 1.052f,
            "catalogue's 2 m length (1.902 raw)") with { Flags = AshfallKitFlags.CoverRatioNotPreserved },
        X("AC-058", "12SB", "low cover short broken", AshfallKitCategory.Cover, "12SB-low-cover-short-broken.glb", new(-0.9520f, -0.4679f, -0.1281f), new(0.9510f, 0.4671f, 0.1271f), 25992, "cover run", "low cover, short, broken top", 1.051f,
            "catalogue's 2 m length (1.903 raw)") with { Flags = AshfallKitFlags.CoverRatioNotPreserved },
        X("AC-059", "12SC", "low cover short blocks", AshfallKitCategory.Cover, "12SC-low-cover-short-blocks.glb", new(-0.9518f, -0.3928f, -0.2531f), new(0.9448f, 0.3922f, 0.2551f), 28890, "cover run", "low cover, short, blocks", 1.055f,
            "catalogue's 2 m length (1.897 raw)") with { Flags = AshfallKitFlags.CoverRatioNotPreserved },
        X("AC-060", "13A", "high cover buttress", AshfallKitCategory.Cover, "13A-high-cover-buttress.glb", new(-0.8528f, -0.9519f, -0.4683f), new(0.8508f, 0.9510f, 0.4673f), 24427, "high cover", "high cover, buttress", 1.156f,
            "ours, estimate: standing Marine cover 2.2 m high (over the 1.6 m eye; the catalogue names no size)"),
        X("AC-061", "13B", "high cover wall", AshfallKitCategory.Cover, "13B-high-cover-wall.glb", new(-0.9520f, -0.4149f, -0.0727f), new(0.9490f, 0.4139f, 0.0716f), 22888, "high cover", "high cover, broken wall", 2.655f,
            "ours, estimate: standing Marine cover 2.2 m high (over the 1.6 m eye; the catalogue names no size)"),
        X("AC-062", "14A", "ash fine", AshfallKitCategory.Ground, "14A-ash-fine.glb", new(-0.9520f, -0.1143f, -0.9524f), new(0.9512f, 0.1124f, 0.9510f), 30302, "ground tile", "ash ground slab, fine", 4.203f,
            "catalogue's 8 m tile side") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-063", "14B", "ash compacted", AshfallKitCategory.Ground, "14B-ash-compacted.glb", new(-0.9521f, -0.1260f, -0.9522f), new(0.9515f, 0.1239f, 0.9511f), 30368, "ground tile", "ash ground slab, compacted", 4.203f,
            "catalogue's 8 m tile side") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-064", "14C", "ash small stone", AshfallKitCategory.Ground, "14C-ash-small-stone.glb", new(-0.9520f, -0.1166f, -0.9520f), new(0.9510f, 0.1231f, 0.9510f), 29301, "ground tile", "ash ground slab, small stones", 4.204f,
            "catalogue's 8 m tile side") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-065", "14D", "ash soot patches", AshfallKitCategory.Ground, "14D-ash-soot-patches.glb", new(-0.9515f, -0.1211f, -0.9516f), new(0.9506f, 0.1270f, 0.9507f), 28502, "ground tile", "ash ground slab, soot patches", 4.205f,
            "catalogue's 8 m tile side") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-066", "14E", "ash fractured crust", AshfallKitCategory.Ground, "14E-ash-fractured-crust.glb", new(-0.9519f, -0.1154f, -0.9520f), new(0.9510f, 0.1188f, 0.9519f), 29254, "ground tile", "ash ground slab, fractured crust", 4.202f,
            "catalogue's 8 m tile side") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-067", "14F", "ash windblown", AshfallKitCategory.Ground, "14F-ash-windblown.glb", new(-0.9520f, -0.1522f, -0.9520f), new(0.9510f, 0.1502f, 0.9510f), 30338, "ground tile", "ash ground slab, windblown", 4.204f,
            "catalogue's 8 m tile side") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-068", "15A", "avenue paving", AshfallKitCategory.Ground, "15A-avenue-paving.glb", new(-0.9522f, -0.0311f, -0.4626f), new(0.9512f, 0.0344f, 0.4618f), 23997, "road tile", "avenue road slab", 8.406f,
            "ours, estimate: 16 m long like the avenue segment AC-033 (16 x 8 m; the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-069", "15B", "avenue shell damaged", AshfallKitCategory.Ground, "15B-avenue-shell-damaged.glb", new(-0.9520f, -0.0539f, -0.5464f), new(0.9510f, 0.0567f, 0.5456f), 24083, "road tile", "avenue road slab, shell-damaged (lifted slabs)", 8.408f,
            "ours, estimate: 16 m long like the avenue segment AC-033 (16 x 8 m; the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-070", "16C", "plaza corner", AshfallKitCategory.Ground, "16C-plaza-corner.glb", new(-0.9518f, -0.0815f, -0.9518f), new(0.9509f, 0.0815f, 0.9509f), 23597, "plaza tile", "plaza corner tile, border on two sides", 4.204f,
            "ours, estimate: the brief's 8 m plaza tiles (the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-071", "16E", "plaza edge", AshfallKitCategory.Ground, "16E-plaza-edge.glb", new(-0.9519f, -0.1623f, -0.9506f), new(0.9510f, 0.1614f, 0.9519f), 25597, "plaza tile", "plaza edge tile, border on one side", 4.204f,
            "ours, estimate: the brief's 8 m plaza tiles (the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified | AshfallKitFlags.OrientationCorrected, ImportFix = AshfallKitImportFix.LayFlat },
        X("AC-072", "16I", "plaza inner", AshfallKitCategory.Ground, "16I-plaza-inner.glb", new(-0.9450f, -0.0970f, -0.9517f), new(0.9439f, 0.0965f, 0.9510f), 27826, "plaza tile", "plaza inner tile", 4.205f,
            "ours, estimate: the brief's 8 m plaza tiles (the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-073", "18A", "boundary flat", AshfallKitCategory.Boundary, "18A-boundary-flat.glb", new(-0.9521f, -0.8324f, -0.1714f), new(0.9510f, 0.8276f, 0.1704f), 23653, "boundary panel", "flat boundary panel, shallow gothic relief", 4.204f,
            "ours, estimate: 8 m panel width on the 8 m grid (the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-074", "18B", "boundary corner", AshfallKitCategory.Boundary, "18B-boundary-corner.glb", new(-0.8291f, -0.9518f, -0.8198f), new(0.8347f, 0.9510f, 0.8312f), 24502, "boundary panel", "boundary corner panel (L)", 4.204f,
            "ours, estimate: 8 m panel height, like the flat panel's 8 m width (the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified },
        X("AC-075", "18C", "boundary broken", AshfallKitCategory.Boundary, "18C-boundary-broken.glb", new(-0.9508f, -0.9518f, -0.1497f), new(0.9458f, 0.9511f, 0.1491f), 21562, "boundary panel", "boundary panel, broken top (a termination)", 4.218f,
            "ours, estimate: 8 m panel width on the 8 m grid (the catalogue names no size)") with { Flags = AshfallKitFlags.SeamsUnverified | AshfallKitFlags.TerminationOnly },
        X("AC-076", "20", "round objective dais", AshfallKitCategory.Landmark, "20-round-objective-dais.glb", new(-0.9514f, -0.0809f, -0.9519f), new(0.9507f, 0.0795f, 0.9509f), 26838, "objective dais", "round raised objective dais, concentric steps on all sides", 6.250f,
            "catalogue's 0.25 m riser: 4 rises over 0.160 raw (mean 0.040 raw; the tallest 0.057 raw, 0.36 m)") with { RawRiser = 0.0574f },
    ];

    /// <summary>The Meshy expansion's folder under <c>source\</c> (36 models, 2026-11-05; its catalogue <c>asset-catalog.json</c> and QA beside the models, read only).</summary>
    public const string ExpansionFolder = "meshy-expansion-2026-11-05";

    /// <summary>The expansion's two corrective previews that its QA rejected (they lost the stair access): never catalogued, never used.</summary>
    public static readonly IReadOnlyList<string> RejectedPreviews = ["08H-R1", "03-R1"];

    /// <summary>
    /// An expansion model: its file under <see cref="ExpansionFolder"/>/Models, the manifest's design text filled from the design scale (footprint length x depth, height, metres).
    /// </summary>
    static AshfallKitModel X(string id, string sourceId, string name, AshfallKitCategory category, string file, Vector3 rawMin, Vector3 rawMax, int triangles,
        string family, string purpose, float designScale, string scaleSource)
    {
        var s = (rawMax - rawMin) * designScale;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new(id, name, category, ExpansionFolder + "/Models/" + file, rawMin, rawMax, triangles, string.Format(inv, "{0:0.0} x {1:0.0}", s.X, s.Z), string.Format(inv, "{0:0.0}", s.Y), Review: false)
        {
            SourceId = sourceId, Family = family, Purpose = purpose, DesignScale = designScale, ScaleSource = scaleSource,
        };
    }

    static readonly Dictionary<string, AshfallKitModel> ById = Models.ToDictionary(m => m.Id);

    public static AshfallKitModel Get(string id) => ById[id];
}

/// <summary>What a dressing item is to the game.</summary>
public enum AshfallDressRole : byte
{
    /// <summary>Draft 3's scaled replacement of a graybox structure. Unused since draft 5 (kept so older readers still compile).</summary>
    Skin,
    /// <summary>Walk-over decoration: no collision, never above 0.35 m where it stands in a walked place (unused since draft 3).</summary>
    Decoration,
    /// <summary>
    /// Draft 5: a placed asset, the one model that stands for a whole graybox structure. Its collision is the box parts listed in <see cref="AshfallDressing.ItemParts"/> for this item
    /// (fitted boxes of the model, transformed with the item); the client draws the model and never the collision. A backdrop block (outside the boundary) has no parts.
    /// </summary>
    Prop,
    /// <summary>A ground, road or plaza tile laid over walkable ground (visual only; the ground part under it is the collision; no entry in <see cref="AshfallDressing.ItemParts"/>).</summary>
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
public sealed record AshfallDressItem(string Asset, Vector3 Position, float Yaw, Vector3 Size, bool SwapXZ, AshfallDressRole Role, int Part)
{
    /// <summary>
    /// A point of the model's file (raw glTF frame, Y up) in the simulation frame: the same mapping the client draws with and the collision of the item was transformed with. The raw bounds fill the item's box
    /// (<see cref="Size"/> over the raw size on the mapped axis); glTF X is local X and glTF -Z is local Y (glTF Z is local X and glTF X is local Y when <see cref="SwapXZ"/>); glTF Y is up from <see cref="Position"/>.Z; the local frame is turned by <see cref="Yaw"/> about the box's centre.
    /// </summary>
    public Vector3 ToWorld(Vector3 raw)
    {
        var m = AshfallKit.Get(Asset);
        var u = (raw - m.RawMin) / m.RawSize;
        float lx = SwapXZ ? (u.Z - 0.5f) * Size.X : (u.X - 0.5f) * Size.X;
        float ly = SwapXZ ? (u.X - 0.5f) * Size.Y : (0.5f - u.Z) * Size.Y;
        float s = MathF.Sin(Yaw), c = MathF.Cos(Yaw);
        return new Vector3(Position.X + c * lx - s * ly, Position.Y + s * lx + c * ly, Position.Z + u.Y * Size.Z);
    }
}

/// <summary>One structure of the dressing (draft 3): a group of touching hidden graybox parts (a building, a tower with its stair, a wall run, a rampart...), its parts and the bounds of their corners. A Skin item's box stays within these bounds grown by 0.5 m.</summary>
public sealed record AshfallStructure(IReadOnlyList<int> Parts, Vector3 Min, Vector3 Max);

/// <summary>
/// The dressing of a level. Draft 5: <see cref="Items"/> in a fixed order (the surface tiles first, then one Prop per placed asset); <see cref="HiddenParts"/> every non-ground part of the level (the collision of the assets: the client never draws it);
/// <see cref="ItemParts"/> the range of level parts each item owns (parallel to the items; (0, 0) for a Surface); <see cref="Flags"/> the review list (gaps, mass mismatches, dropped pieces, models whose collision needs a manual clean-up);
/// <see cref="Structures"/> one entry per placed structure (the item's parts and the bounds of the v5 structure it replaced).
/// </summary>
public sealed record AshfallDressing(IReadOnlyList<AshfallDressItem> Items, IReadOnlyList<int> HiddenParts, uint Version, IReadOnlyList<AshfallStructure>? Structures = null)
{
    public IReadOnlyList<(int First, int Count)> ItemParts { get; init; } = [];

    public IReadOnlyList<string> Flags { get; init; } = [];
}

/// <summary>One line of the draft-5 placement log: the v5 structure group, its family, the asset (or "" when none), the item index (-1 when none) and the outcome ("placed", "dropped: ...").</summary>
public sealed record AshfallPlacement(string Group, string Family, string Asset, int Item, string Outcome);

/// <summary>The draft-5 result: the level, its dressing, the v5 graybox it started from, and what happened to every structure.</summary>
public sealed record AshfallDraft5(AshfallLevel Level, AshfallDressing Dressing, AshfallBuild Graybox, IReadOnlyList<AshfallPlacement> Placements);
