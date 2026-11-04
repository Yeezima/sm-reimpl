using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

public static partial class AshfallMap
{
    /// <summary>The boundary wall's height (ours): above the highest standable top (a 5-storey tower's 30 m wall columns) plus a full jump's 17.5 m and a margin.</summary>
    public const float BoundaryHeight = 64f;

    /// <summary>The blocks' raised height above the roads (ours; a kerb under the 0.4 m step).</summary>
    public const float BlockTop = 0.25f;

    /// <summary>A floor overlay's lift (road and paving are drawn 2 cm above the surface they lie on; ours).</summary>
    const float Overlay = 0.02f;

    /// <summary>
    /// The level (deterministic): docs/musou-ashfall-graybox.md in code, every place and size ours (placeholder demonstration). The same parts, in the same order, with the same bits, on every machine.
    /// </summary>
    public static AshfallLevel Build() => BuildWithFeatures().Level;

    /// <summary>The level together with what pass 2 placed (platforms, tall buildings, ruins, walls), for the checks.</summary>
    public static AshfallBuild BuildWithFeatures()
    {
        var b = new AshfallBuilder();
        GroundLayout(b);
        StreetLayout(b);
        BoundaryLayout(b);
        NorthWest(b);
        StrongholdLayout(b);
        NorthEast(b);
        Craters(b);
        Centre(b);
        EastRuins(b);
        SouthWest(b);
        SpawnCompound(b);
        Bunker(b);

        ControlPoint[] points = [new("A", new(-164f, 164f, 3f), 14f, 8f), new("B", new(0f, 0f, BlockTop + Overlay), 16f, 8f), new("C", new(164f, 150f, BlockTop), 14f, 8f)];
        StartArea[] areas =
        [
            new(Team.SpaceMarines, new(0f, -188f, BlockTop), new(64f, 48f, 8f), 0f),
            new(Team.Chaos, new(0f, 138f, BlockTop), new(40f, 36f, 8f), 0f),
        ];
        Vector3 P(float x, float y) => new(x, y, SurfaceAt(x, y));
        Vector3[] spawns =
        [
            areas[0].Center, areas[1].Center, .. points.Select(p => p.Center),
            P(-56f, -56f), P(56f, -56f), P(-56f, 56f), P(56f, 56f),     // the plaza's corners
            P(-186f, -32f), P(164f, 164f), P(180f, -20f), P(-196f, -196f), P(140f, -176f),   // the other blocks' centres (A, B, the stronghold and the compound stand for theirs)
            P(0f, -160f),                                               // the compound's gate
        ];

        foreach (var s in spawns) b.Reserved.Add(new Vector3(s.X, s.Y, 4f));
        foreach (var p in points) b.Reserved.Add(new Vector3(p.Center.X, p.Center.Y, 4f));
        var features = Structures(b, points);
        RubbleLayout(b);
        foreach (var p in points) b.Disc(p.Center.X, p.Center.Y, BlockTop + 0.05f + (p.Name == "B" ? Overlay : 0f), p.Radius);

        // The perimeter road's corners (added after the rubble, so they do not move it): the enemies' graph and the war's bounds are the seeds' box plus 30 m,
        // and without these the east and north strips (x > 210, y > 194) had no nodes and an order there was clamped (review of 2026-11-04).
        Vector3[] corners = [P(-232f, -232f), P(232f, -232f), P(-232f, 232f), P(232f, 232f)];
        return new AshfallBuild(new AshfallLevel(b.Parts, points, areas, [.. spawns, .. corners], HashOf(b.Parts)), features);
    }

    /// <summary>The level's hash: FNV-1a over the solid parts' corner bits in order, salted with <see cref="Version"/>.</summary>
    static ulong HashOf(IReadOnlyList<AshfallPart> parts)
    {
        ulong h = Geometry.Fnv(Geometry.FnvOffset, Version);
        foreach (var part in parts)
        {
            if (!part.Solid) continue;
            foreach (var v in part.Corners)
            {
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.X));
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Y));
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Z));
            }
        }
        return h;
    }

    // ---------------------------------------------------------------- the grid

    static readonly (float Lo, float Hi)[] Spans = [(-224f, -104f), (-80f, 80f), (104f, 224f)];

    static bool InBlock(float x, float y, out bool crater)
    {
        crater = false;
        foreach (var (xl, xh) in Spans)
            foreach (var (yl, yh) in Spans)
                if (x >= xl && x <= xh && y >= yl && y <= yh) { crater = xl < -200f && yl > -100f && yh < 100f; return true; }
        return false;
    }

    /// <summary>The ground's height at (<paramref name="x"/>, <paramref name="y"/>): 0 on the roads, <see cref="BlockTop"/> on a block, the crater field's heightfield on the west-middle block.</summary>
    public static float SurfaceAt(float x, float y) =>
        !InBlock(x, y, out bool crater) ? 0f : crater ? CraterHeight(x, y) : BlockTop;

    // ---------------------------------------------------------------- ground, streets, boundary

    static void GroundLayout(AshfallBuilder b)
    {
        // The slab under everything, open where the crater field's heightfield replaces it (west-middle block).
        b.Box(Ground, -Half, -Half, -2f, Half, -80f, 0f);
        b.Box(Ground, -Half, 80f, -2f, Half, Half, 0f);
        b.Box(Ground, -Half, -80f, -2f, -224f, 80f, 0f);
        b.Box(Ground, -104f, -80f, -2f, Half, 80f, 0f);
        foreach (var (xl, xh) in Spans)
            foreach (var (yl, yh) in Spans)
                if (!(xl == -224f && yl == -80f)) b.Box(Curb, xl, yl, 0f, xh, yh, BlockTop);
        b.Heightfield(Ground, -224f, -80f, -104f, 80f, 2f, 0f, CraterHeight);
    }

    static void StreetLayout(AshfallBuilder b)
    {
        // Paved road from the compound's north gate to the plaza (12 m wide, flush: drawn as road), then the plaza's paving.
        b.Box(Road, -6f, -160f, BlockTop, 6f, -104f, BlockTop + Overlay);
        b.Box(Road, -6f, -104f, 0f, 6f, -80f, Overlay);
        b.Box(Road, -6f, -80f, BlockTop, 6f, -60f, BlockTop + Overlay);
        b.Box(Paving, -60f, -60f, BlockTop, 60f, 60f, BlockTop + Overlay);
    }

    static void BoundaryLayout(AshfallBuilder b)
    {
        // A ruined skyline wall, 4 m thick, BoundaryHeight (64 m) tall, outside the square.
        const float O = Half + 4f, T = BoundaryHeight;
        b.Box(Boundary, -O, -O, -2f, O, -Half, T);
        b.Box(Boundary, -O, Half, -2f, O, O, T);
        b.Box(Boundary, -O, -Half, -2f, -Half, Half, T);
        b.Box(Boundary, Half, -Half, -2f, O, Half, T);
        // Backdrop (drawn, solid, unreachable): cathedral blocks 20 x 20 x 40..70 m along the west and east edges and the north corners.
        float[] heights = [40f, 55f, 70f, 48f, 62f, 40f];
        for (int i = 0; i < 6; i++)
        {
            float y = -200f + 80f * i, h = heights[i];
            b.Box(Backdrop, -270f, y - 10f, -2f, -250f, y + 10f, h);
            b.Box(Backdrop, 250f, y - 10f, -2f, 270f, y + 10f, heights[5 - i]);
        }
        foreach (float x in new[] { -200f, -160f, 160f, 200f })
            b.Box(Backdrop, x - 10f, 250f, -2f, x + 10f, 270f, x is -160f or 200f ? 62f : 48f);
    }

    // ---------------------------------------------------------------- NW: objective A

    static void NorthWest(AshfallBuilder b)
    {
        // Plinth 16 x 16 x 3 m centred on A, a box-step stair 8 m wide up its south face, a broken obelisk (jump-only perch) to 15 m.
        b.Box(Floor, -172f, 156f, 0f, -156f, 172f, 3f);
        b.Stair(-164f, 150f, 0, 1, 8f, 0f, 3f);    // south
        b.Stair(-164f, 178f, 0, -1, 8f, 0f, 3f);   // north (pass 2: stairs on all four sides, owner's note)
        b.Stair(-178f, 164f, 1, 0, 8f, 0f, 3f);    // west
        b.Stair(-150f, 164f, -1, 0, 8f, 0f, 3f);   // east
        b.Box(Perch, -166f, 166f, 3f, -162f, 170f, 15f);
        // Two low walls (1.2 m) at 28..40 m from A, cover outside the zone.
        b.Box(Cover, -136f, 175.5f, BlockTop, -128f, 176.5f, BlockTop + 1.2f);
        b.Box(Cover, -180f, 127.5f, BlockTop, -172f, 128.5f, BlockTop + 1.2f);
    }

    // ---------------------------------------------------------------- N centre: the stronghold

    static void StrongholdLayout(AshfallBuilder b)
    {
        // Two L walls, 4 m thick, 12 m tall, walkable tops; the gate between them is open (24 m).
        b.Box(Wall, -72f, 112f, 0f, -12f, 116f, 12f);
        b.Box(Wall, -72f, 116f, 0f, -68f, 196f, 12f);
        b.Box(Wall, 12f, 112f, 0f, 72f, 116f, 12f);
        b.Box(Wall, 68f, 116f, 0f, 72f, 196f, 12f);
        // One ramp of box steps up the inner face of each L wall to its top (6 m wide, 48 steps).
        b.Stair(-65.25f, 116f, 0, 1, 6.5f, 0f, 12f);   // x -68.25..-62.25: into the wall on one side, between grid lines on the other
        b.Stair(65.25f, 116f, 0, 1, 6.5f, 0f, 12f);
        // The keep, 64 x 48 m, 18 m tall, a 12 x 10 m entrance (8 m deep) in its south face; solid inside.
        b.Box(Stronghold, -32f, 164f, 0f, -6f, 212f, 18f);
        b.Box(Stronghold, 6f, 164f, 0f, 32f, 212f, 18f);
        b.Box(Stronghold, -6f, 172f, 0f, 6f, 212f, 18f);
        b.Box(Stronghold, -6f, 164f, 10f, 6f, 172f, 18f);
    }

    // ---------------------------------------------------------------- NE: objective C, the chapel ruin

    static void NorthEast(AshfallBuilder b)
    {
        // North facade 24 m wide and 18 m tall with two breaches 4 x 6 m at 6 m; east and west walls 12 m tall with ground breaches 4 x 4.5 m.
        b.HoleWall(Wall, true, 152f, 176f, 187f, 188f, 0f, 18f, (155f, 159f, 6f, 12f), (169f, 173f, 6f, 12f));
        b.HoleWall(Wall, false, 172f, 188f, 152f, 153f, 0f, 12f, (178f, 182f, 0f, 4.5f));
        b.HoleWall(Wall, false, 172f, 188f, 175f, 176f, 0f, 12f, (178f, 182f, 0f, 4.5f));
        // First floor at 6 m over the north half and the stair to it: two flights of 3 m and a landing (4 m wide), then a connector slab.
        b.Box(Floor, 153f, 180f, 5.5f, 175f, 187f, 6f);
        b.Stair(154f, 175f, 1, 0, 4f, 0f, 3f);
        b.Box(AshfallKind.Stair, 160f, 173f, 0f, 164f, 177f, 3f);
        b.Stair(164f, 175f, 1, 0, 4f, 0f, 3f, skip: 12);
        b.Box(Floor, 170f, 172f, 5.5f, 175f, 180f, 6f);
        // Roof slab at 12 m over the west third (jump-only).
        b.Box(Perch, 153f, 172f, 11.5f, 161f, 187f, 12f);
        // Low walls at 28..40 m from C.
        b.Box(Cover, 160f, 117.5f, BlockTop, 168f, 118.5f, BlockTop + 1.2f);
        b.Box(Cover, 199.5f, 146f, BlockTop, 200.5f, 154f, BlockTop + 1.2f);
    }

    // ---------------------------------------------------------------- W middle: the crater field

    /// <summary>The craters (centre x, centre y, radius; ours): radius 6..14 m, depth 1.5..2.5 m, rim 0.5 m, slopes under 35 degrees, none overlapping.</summary>
    static readonly (float X, float Y, float R)[] CraterList =
    [
        (-200f, 55f, 10f), (-165f, 60f, 8f), (-130f, 50f, 12f), (-185f, 20f, 6f), (-150f, 25f, 9f), (-205f, -10f, 12f),
        (-165f, -15f, 14f), (-125f, -5f, 7f), (-195f, -50f, 8f), (-160f, -55f, 10f), (-125f, -52f, 6f), (-208f, 25f, 6f),
    ];

    static float Smooth(float t) => t * t * (3f - 2f * t);

    /// <summary>The crater field's height: <see cref="BlockTop"/> plus the bowl (depth 1.5..2.5 m by radius) and its 0.5 m rim (polynomial profile: no trigonometry).</summary>
    static float CraterHeight(float x, float y)
    {
        foreach (var (cx, cy, r) in CraterList)
        {
            float dx = x - cx, dy = y - cy, d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= r * 1.4f) continue;
            float depth = 1.5f + (r - 6f) / 8f, t = d / r;
            return BlockTop + (t < 1f ? -depth + (depth + 0.5f) * Smooth(t) : 0.5f * (1f - Smooth((t - 1f) / 0.4f)));
        }
        return BlockTop;
    }

    static bool Flat(float x, float y, float r) =>
        SurfaceAt(x, y) == BlockTop && SurfaceAt(x - r, y - r) == BlockTop && SurfaceAt(x + r, y + r) == BlockTop && SurfaceAt(x - r, y + r) == BlockTop && SurfaceAt(x + r, y - r) == BlockTop;

    static void Craters(AshfallBuilder b)
    {
        // Low walls (1.2 m) and collapsed-wall pieces (2.5 m) between the craters, on flat ground only.
        (float X, float Y, int Yaw)[] low = [(-176f, 40f, 0), (-140f, 0f, 6), (-112f, -32f, 0), (-212f, -34f, 6), (-140f, -72f, 0), (-110f, 40f, 6)];
        foreach (var (x, y, yaw) in low)
            if (Flat(x, y, 5f)) b.Oriented(Cover, x, y, BlockTop, BlockTop + 1.2f, 8f, 1f, yaw);
        (float X, float Y, int Yaw)[] collapsed = [(-176f, 5f, 3), (-145f, -35f, 9), (-186f, 40f, 3), (-115f, 18f, 0), (-115f, -70f, 15)];
        foreach (var (x, y, yaw) in collapsed)
            if (Flat(x, y, 3f)) b.Oriented(Rubble, x, y, BlockTop, BlockTop + 2.5f, 4f, 1f, yaw);
    }

    // ---------------------------------------------------------------- centre: plaza, small monument, arch

    static void Centre(AshfallBuilder b)
    {
        // Pass 2: the fallen statue at B is removed (owner's note); B's plaza stays.
        // The small monument on the west avenue: plinth 10 x 10 x 2 m, steps on its east side, an obelisk 3 x 3 m to 12 m (jump-only perch).
        b.Box(Floor, -97f, -5f, 0f, -87f, 5f, 2f);
        b.Stair(-83f, 0f, -1, 0, 6f, 0f, 2f);
        b.Box(Perch, -93.5f, -1.5f, 2f, -90.5f, 1.5f, 12f);
        // The triumphal arch on the east avenue: piers to 10 m, a lintel 10..14 m with a walkable top (jump-only perch), a passage 16 m wide.
        b.Box(Wall, 80f, -3f, 0f, 84f, 3f, 10f);
        b.Box(Wall, 100f, -3f, 0f, 104f, 3f, 10f);
        b.Box(Perch, 80f, -3f, 10f, 104f, 3f, 14f);
    }

    // ---------------------------------------------------------------- E middle: ruin cluster

    static void EastRuins(AshfallBuilder b)
    {
        // Large two-storey ruin 24 x 24 m at (164, 10): walls 1 m thick, 12 m, breaches on three sides (ground 4 x 4.5, upper 2 x 2.5 at a 7 m sill).
        b.HoleWall(Wall, false, -2f, 22f, 152f, 153f, 0f, 12f, (8f, 12f, 0f, 4.5f), (16f, 18f, 7f, 9.5f));
        b.HoleWall(Wall, false, -2f, 22f, 175f, 176f, 0f, 12f, (8f, 12f, 0f, 4.5f), (4f, 6f, 7f, 9.5f));
        b.HoleWall(Wall, true, 152f, 176f, 21f, 22f, 0f, 12f, (160f, 164f, 0f, 4.5f), (168f, 170f, 7f, 9.5f));
        b.Box(Wall, 152f, -2f, 0f, 176f, -1f, 12f);
        // First floor at 6 m over the east half, a straight stair (4 m wide, 24 steps) along the south wall, a roof at 12 m over the north-east quarter (jump-only).
        b.Box(Floor, 166f, -1f, 5.5f, 175f, 21f, 6f);
        b.Stair(154f, 1f, 1, 0, 4f, 0f, 6f);
        b.Box(Perch, 164f, 10f, 11.5f, 175f, 21f, 12f);
        // Small ruin 12 x 12 x 12 m at (136, -48): one stair, outside along the south wall, to a floor at 6 m through a doorway whose sill is that high.
        b.HoleWall(Wall, true, 130f, 142f, -54f, -53f, 0f, 12f, (137f, 141f, 6f, 9.5f), (132f, 136f, 0f, 4.5f));
        b.HoleWall(Wall, true, 130f, 142f, -43f, -42f, 0f, 12f);
        b.HoleWall(Wall, false, -53f, -43f, 130f, 131f, 0f, 12f, (-50f, -46f, 0f, 4.5f));
        b.HoleWall(Wall, false, -53f, -43f, 141f, 142f, 0f, 12f);
        b.Box(Floor, 131f, -53f, 5.5f, 141f, -43f, 6f);
        b.Stair(129f, -56f, 1, 0, 4f, 0f, 6f);
    }

    // ---------------------------------------------------------------- SW: the artillery wreck

    static void SouthWest(AshfallBuilder b)
    {
        // Gun platform 12 x 12 x 3 m, box steps on its north side, a barrel 4 x 22 x 3 m pitched 20 degrees up to the north-east (a walkable ramp to a perch at about 10 m).
        b.Box(Floor, -170f, -170f, 0f, -158f, -158f, 3f);
        b.Stair(-164f, -152f, 0, -1, 6f, 0f, 3f);
        b.Pitched(Floor, new Vector3(-162f, -162f, 3f), 3, 0.3420201433256687f, 0.9396926207859084f, 22f, 4f, 3f);
        // Wall fragments.
        b.Box(Cover, -196f, -150f, BlockTop, -188f, -149f, BlockTop + 1.2f);
        b.Box(Cover, -150f, -196f, BlockTop, -149f, -188f, BlockTop + 1.2f);
    }

    // ---------------------------------------------------------------- S centre: the spawn compound

    static void SpawnCompound(AshfallBuilder b)
    {
        // Low ruined walls 2.5 m tall, 1 m thick, a 16 m gate in the north wall; the Space Marine start area stands inside.
        b.Box(Wall, -36f, -216f, 0f, 36f, -215f, 2.5f);
        b.Box(Wall, -36f, -215f, 0f, -35f, -161f, 2.5f);
        b.Box(Wall, 35f, -215f, 0f, 36f, -161f, 2.5f);
        b.Box(Wall, -36f, -161f, 0f, -8f, -160f, 2.5f);
        b.Box(Wall, 8f, -161f, 0f, 36f, -160f, 2.5f);
        // Short wall fragments (1.2 m) flanking the road north.
        foreach (float x in new[] { -10f, 9f })
        {
            b.Box(Cover, x, -130f, BlockTop, x + 1f, -122f, BlockTop + 1.2f);
            b.Box(Cover, x, -116f, BlockTop, x + 1f, -108f, BlockTop + 1.2f);
        }
    }

    // ---------------------------------------------------------------- SE: the bunker ruin

    static void Bunker(AshfallBuilder b)
    {
        // Bunker 24 x 20 m: walls 3 m tall on three sides, a roofed part 12 x 20 m (roof at 6 m, walkable) reached by box steps on its west side.
        b.Box(Wall, 152f, -174f, 0f, 176f, -173f, 3f);
        b.Box(Wall, 152f, -155f, 0f, 176f, -154f, 3f);
        b.Box(Wall, 175f, -173f, 0f, 176f, -155f, 3f);
        b.Box(Floor, 164f, -174f, 5.5f, 176f, -154f, 6f);
        b.Stair(152f, -164f, 1, 0, 6f, 0f, 6f);
    }

    // ---------------------------------------------------------------- rubble

    static void RubbleLayout(AshfallBuilder b)
    {
        float Surf(float x, float y) => SurfaceAt(x, y);
        uint seed = 1;
        void C(float x, float y, int n, float spread, int tall) => b.Cluster(seed++, x, y, n, spread, tall, Surf);
        // Around A and C (28..40 m out), B's six piles (2.5 m tall), then cover through the rest of the city.
        C(-134f, 140f, 8, 6f, 2); C(-190f, 190f, 8, 6f, 2);
        C(132f, 150f, 8, 6f, 2); C(190f, 124f, 8, 6f, 2); C(196f, 170f, 8, 6f, 2);
        foreach (var (x, y) in new[] { (34.5f, 20f), (0f, 40f), (-34.5f, 20f), (-34.5f, -20f), (14f, -42f), (34.5f, -20f) }) C(x, y, 5, 4f, 4);
        C(-190f, -140f, 8, 6f, 2); C(-140f, -200f, 8, 6f, 2); C(-210f, -190f, 6, 5f, 2);
        C(-60f, -130f, 6, 6f, 2); C(60f, -130f, 6, 6f, 2); C(-60f, -195f, 6, 5f, 2); C(60f, -195f, 6, 5f, 2);
        C(130f, -190f, 7, 6f, 2); C(200f, -130f, 7, 6f, 2); C(190f, -190f, 6, 5f, 2);
        C(-100f, -40f, 6, 5f, 2); C(100f, 40f, 6, 5f, 2); C(100f, -40f, 5, 5f, 2); C(-100f, 40f, 5, 5f, 2);
        C(-100f, 190f, 6, 6f, 2); C(100f, 190f, 6, 6f, 2); C(-92f, 130f, 5, 5f, 2); C(92f, 130f, 5, 5f, 2);
        C(200f, 40f, 7, 6f, 2); C(130f, 40f, 6, 6f, 2); C(200f, -60f, 6, 6f, 2); C(130f, -10f, 5, 5f, 2);
        C(-50f, 60f, 5, 5f, 2); C(50f, -60f, 5, 5f, 2);
        // Pass 2: more rubble in the owner's red-marked zones (south loop, crater field south-west, the avenues' stretches, the east side, the north avenue).
        foreach (var (x, y) in new[] { (-200f, -110f), (-150f, -112f), (-60f, -112f), (60f, -112f), (150f, -112f), (205f, -112f), (-200f, -70f), (-150f, -72f), (-70f, -70f), (70f, -70f), (150f, -70f), (210f, -72f),
            (-210f, -60f), (-180f, -30f), (-190f, -75f), (-70f, -50f), (-80f, -35f), (110f, -50f), (120f, -30f), (110f, -65f), (205f, -45f), (215f, -20f), (200f, -5f),
            (-170f, 95f), (-130f, 95f), (-110f, 110f), (80f, 95f), (110f, 108f), (140f, 95f) })
            C(x, y, 5, 5f, 2);
    }
}
