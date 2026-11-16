using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>The three kinds of ground of the Musou front pilot (the owner-approved plan of 2026-11-16; docs/musou-ashfall-graybox.md "Front pilot"): lane B's three positions, south to north.</summary>
public enum AshfallBandKind : byte { Wasteland = 0, Outskirts = 1, Core = 2 }

/// <summary>How a band's fabric is laid: scattered islands on open ground, or rows of buildings along the streets (street walls).</summary>
public enum AshfallFabric : byte { Islands, Rows }

/// <summary>
/// One reusable band profile of the front pilot (spec "Musou front pilot: band profiles"): the density rules a band's fabric is generated from, after the routes and the capture points are laid out and checked.
/// Every number is ours, an estimate taken from today's Ashfall data (draft 11's field, draft 12's city and draft 13's heights) for the owner's playtest (refine register); none is a universal level-design number.
/// </summary>
public sealed record AshfallBandProfile
{
    public required AshfallBandKind Kind { get; init; }
    public required string Name { get; init; }
    public required AshfallFabric Fabric { get; init; }
    /// <summary>The drawn footprint share of the buildable land the fabric fills to (a range; the generator aims at its middle).</summary>
    public required float CoverageMin { get; init; }
    public required float CoverageMax { get; init; }
    /// <summary>Least open ground between two islands (m) and between the buildings of one cluster or row (m).</summary>
    public required float IslandGap { get; init; }
    public required float ClusterGap { get; init; }
    /// <summary>Least open ground round a fill piece (m; the second pass's single buildings in the gaps the templates left).</summary>
    public required float FillGap { get; init; }
    /// <summary>The candidate grid's spacing (m; islands) before seeded jitter.</summary>
    public required float Spacing { get; init; }
    /// <summary>Building heights (m): the usual range and the tall pieces (edges and landmarks only).</summary>
    public required float HeightMin { get; init; }
    public required float HeightMax { get; init; }
    public required float TallHeight { get; init; }
    /// <summary>Low structures (trench walls, berms; m), the wasteland's cover.</summary>
    public required float LowMin { get; init; }
    public required float LowMax { get; init; }
    /// <summary>The share of buildings drawn from models with walkable upper floors (their kit stairs must pass the stair rules; the rest are stairless masses).</summary>
    public required float UpperFloorShare { get; init; }
    /// <summary>Side streets between blocks or clusters where no avenue runs (m): two Marines abreast with room, not an army route.</summary>
    public required float SideStreet { get; init; }
    /// <summary>Rows: a breach gap in the street wall every this many metres (range).</summary>
    public required float BreachMin { get; init; }
    public required float BreachMax { get; init; }
    /// <summary>Rows: a breach gap's width (m).</summary>
    public required float BreachWidth { get; init; }
    /// <summary>Rows of buildings along the corridors' sides (the frontage row on the corridor's edge, then back rows behind 6 m alleys; 0: none) and the share of the frontage left as gaps (the outskirts' looser fronts).</summary>
    public required int FrontageTiers { get; init; }
    public float FrontageGapShare { get; init; }
    /// <summary>Plazas (open squares kept round the capture points; the core's three) and their radius (m, from the point's centre).</summary>
    public required int Plazas { get; init; }
    public required float PlazaRadius { get; init; }
    /// <summary>Walk-through breached blocks (rows only; never on the main avenue: at least <see cref="BreachedBlockOffset"/> m from it).</summary>
    public required int BreachedBlocks { get; init; }
    public const float BreachedBlockOffset = 40f;
    /// <summary>The landmark slots: name, kit family, place (band frame, m) and size (m).</summary>
    public required (string Name, string Family, Vector2 At, Vector3 Size)[] Landmarks { get; init; }
    /// <summary>Shooting platforms the kit pass adds in all (its high-ground rule keeps any one from seeing every point).</summary>
    public required int PlatformTarget { get; init; }
    /// <summary>The corridors' width (m), the army's least width (m), the open ring round each point (m beyond its radius), and the approaches each point needs and their separation (degrees).</summary>
    public float CorridorWidth { get; init; } = 24f;
    public float MinArmyWidth { get; init; } = 20f;
    public float ObjectiveClear { get; init; } = 16f;
    public int Approaches { get; init; } = 2;
    public float ApproachSeparation { get; init; } = 60f;
    /// <summary>The keep-out round each corridor for the fabric's footprints (m).</summary>
    public float RouteBuffer { get; init; } = 4f;
    /// <summary>The coverage the generator aims at.</summary>
    public float Coverage => (CoverageMin + CoverageMax) / 2f;

    /// <summary>The wasteland (lane B's first position): low trench walls and berms on flat ground, scattered low ruins, long views. Coverage 0.05-0.08 (draft 11's no-man's-land measured 3-5 %), no upper floors.</summary>
    public static readonly AshfallBandProfile Wasteland = new()
    {
        Kind = AshfallBandKind.Wasteland, Name = "wasteland", Fabric = AshfallFabric.Islands,
        CoverageMin = 0.05f, CoverageMax = 0.08f, IslandGap = 12f, ClusterGap = 6f, FillGap = 10f, Spacing = 16f, FrontageTiers = 0,
        HeightMin = 4f, HeightMax = 8f, TallHeight = 14f, LowMin = 1.2f, LowMax = 2.2f, UpperFloorShare = 0f, SideStreet = 12f,
        BreachMin = 30f, BreachMax = 50f, BreachWidth = 8f, Plazas = 0, PlazaRadius = 0f, BreachedBlocks = 0,
        Landmarks = [("wrecked hulk", "wreck", new(-84f, -44f), new(16f, 10f, 7f)), ("fallen saint", "monument", new(96f, 56f), new(9f, 9f, 14f))],
        PlatformTarget = 10,
    };

    /// <summary>The outskirts (the second position): spaced ruins and blocks, courtyards, walkable upper floors. Coverage 0.20-0.24 (draft 11's districts 8-14 %, draft 12's city 23-39 %), buildings 10-13 m, half with walkable upper floors (the owner, 2026-11-16: more of them, fewer rubble heaps; the plan had 10 %), 12 m side streets.</summary>
    public static readonly AshfallBandProfile Outskirts = new()
    {
        Kind = AshfallBandKind.Outskirts, Name = "outskirts", Fabric = AshfallFabric.Islands,
        CoverageMin = 0.20f, CoverageMax = 0.24f, IslandGap = 8f, ClusterGap = 6f, FillGap = 5f, Spacing = 12f, FrontageTiers = 1, FrontageGapShare = 0.15f,
        HeightMin = 10f, HeightMax = 13f, TallHeight = 26f, LowMin = 1.2f, LowMax = 2.2f, UpperFloorShare = 0.50f, SideStreet = 12f,
        BreachMin = 30f, BreachMax = 50f, BreachWidth = 8f, Plazas = 0, PlazaRadius = 0f, BreachedBlocks = 0,
        Landmarks = [("hab tower", "spire", new(-200f, -150f), new(16f, 16f, 30f)), ("hab tower east", "spire", new(200f, -150f), new(16f, 16f, 30f))],
        PlatformTarget = 18,
    };

    /// <summary>The urban core (the third position, the final assault): rows of buildings as street walls with breach gaps every 30-50 m, three plazas, a few walk-through breached blocks off the main route. Coverage 0.34-0.38 (draft 13's districts), 13-26 m, tall 44 m at the edges.</summary>
    public static readonly AshfallBandProfile Core = new()
    {
        Kind = AshfallBandKind.Core, Name = "urban core", Fabric = AshfallFabric.Rows,
        CoverageMin = 0.34f, CoverageMax = 0.38f, IslandGap = 6f, ClusterGap = 6f, FillGap = 2.5f, Spacing = 10f, FrontageTiers = 3,
        HeightMin = 16f, HeightMax = 28f, TallHeight = 44f, LowMin = 1.2f, LowMax = 2.2f, UpperFloorShare = 0.35f, SideStreet = 12f,
        BreachMin = 30f, BreachMax = 50f, BreachWidth = 8f, Plazas = 3, PlazaRadius = 36f, BreachedBlocks = 3,
        Landmarks = [("cathedral spire", "spire", new(-200f, 140f), new(20f, 20f, 42f)), ("manufactorum chimney tower", "spire", new(200f, 140f), new(20f, 20f, 42f))],
        PlatformTarget = 24, RouteBuffer = 2.5f,
    };

    /// <summary>The front's bands, south to north (lane B's positions 1, 2 and 3).</summary>
    public static readonly AshfallBandProfile[] Front = [Wasteland, Outskirts, Core];

    /// <summary>The share of each band's square (from its north edge) that blends toward the next band's profile.</summary>
    public const float BlendShare = 0.2f;

    /// <summary>The profile the next band north takes (the core blends into itself: beyond it the front is visual only).</summary>
    public AshfallBandProfile Next => Kind switch { AshfallBandKind.Wasteland => Outskirts, AshfallBandKind.Outskirts => Core, _ => Core };

    /// <summary>How far toward the next band a plan point is (0 below the blend's start, 1 at the north edge).</summary>
    public static float BlendAt(float y)
    {
        float start = AshfallMap.Half - 2f * AshfallMap.Half * BlendShare;
        return Math.Clamp((y - start) / (AshfallMap.Half - start), 0f, 1f);
    }
}
