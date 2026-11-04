using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

// Pass 3 of the graybox (the owner's sketch 6, 2026-11-05; docs/musou-ashfall-graybox.md "Pass 3"): the stronghold reworked. Everything is ours (placeholder demonstration).
public static partial class AshfallMap
{
    /// <summary>A rampart's parapet: 0.5 m thick, <see cref="ParapetHeight"/> tall (the kit's cover height 1.0..1.2 m).</summary>
    const float ParapetHeight = 1.2f;

    static void Parapet(AshfallBuilder b, float x0, float y0, float x1, float y1, float z, float height = ParapetHeight) => b.Box(Cover, x0, y0, z, x1, y1, z + height);

    // ---------------------------------------------------------------- N centre: the stronghold

    /// <summary>
    /// The ring (4 m thick walls with walkable tops and parapets; ours): the west half (south leg x -72..-12, west leg y 116..220, the north wall) 12 m tall, the east half (south leg x 12..72, east leg to y 220) raised to 18 m;
    /// the gate x -12..12 is the one opening on the ground. The keep stays (64 x 48 m, roof at 18 m, stair up its east face). The stair towers, the keep tower and the twin towers' gateways are built with the towers (<see cref="StrongholdTowers"/>).
    /// </summary>
    static void StrongholdLayout(AshfallBuilder b)
    {
        // West half 12 m, east half 18 m. The north wall runs between the legs at 12 m.
        b.Box(Wall, -72f, 112f, 0f, -12f, 116f, 12f);
        b.Box(Wall, -72f, 116f, 0f, -68f, 220f, 12f);
        b.Box(Wall, -68f, 216f, 0f, 68f, 220f, 12f);
        b.Box(Wall, 12f, 112f, 0f, 72f, 116f, 18f);
        b.Box(Wall, 68f, 116f, 0f, 72f, 220f, 18f);
        // Parapets (Cover, 0.5 x 1.2 m) on both sides of every wall top. They touch edge to edge (gap 0) and stop where a tower stands against the wall (its own wall is the barrier),
        // where the twin tower's gateway opens onto the west rampart (y 144..160) and at the keep bridge's mouth (y 199..205).
        // West rampart, 12 m.
        Parapet(b, -72f, 112f, -71.5f, 144f, 12f); Parapet(b, -72f, 160f, -71.5f, 220f, 12f);
        Parapet(b, -71.5f, 112f, -12f, 112.5f, 12f);
        Parapet(b, -52f, 115.5f, -12f, 116f, 12f);
        Parapet(b, -68.5f, 132f, -68f, 199f, 12f); Parapet(b, -68.5f, 205f, -68f, 216f, 12f);
        Parapet(b, -71.5f, 219.5f, 68f, 220f, 12f);
        Parapet(b, -68f, 216f, 68f, 216.5f, 12f);
        // East rampart, 18 m.
        Parapet(b, 71.5f, 112f, 72f, 220f, 18f);
        Parapet(b, 12f, 112f, 71.5f, 112.5f, 18f);
        Parapet(b, 12f, 115.5f, 52f, 116f, 18f);
        Parapet(b, 68f, 132f, 68.5f, 219.5f, 18f);
        Parapet(b, 68f, 219.5f, 71.5f, 220f, 18f);
        // The bridge from the west rampart to the keep tower: a deck 6 m wide at 12 m (x -68..-48, y 199..205) with parapets, through the tower's gateway (y 200..204).
        b.Box(Floor, -68f, 199f, 11.5f, -48f, 205f, 12f);
        Parapet(b, -68f, 199f, -48f, 199.5f, 12f);
        Parapet(b, -68f, 204.5f, -48f, 205f, 12f);
        // The keep, 64 x 48 m, 18 m tall, a 12 x 10 m entrance (8 m deep) in its south face; solid inside.
        // Pass 2b: the roof (a Floor slab 0.5 m thick on top, 18 m) is reachable on foot by a stair up the keep's east face: three flights of 6 m (4 m wide, 0.25 m risers, 0.5 m treads), landings at 6 and 12 m, a 4 m deck at 18 m.
        b.Box(Stronghold, -32f, 164f, 0f, -6f, 212f, 17.5f);
        b.Box(Stronghold, 6f, 164f, 0f, 32f, 212f, 17.5f);
        b.Box(Stronghold, -6f, 172f, 0f, 6f, 212f, 17.5f);
        b.Box(Stronghold, -6f, 164f, 10f, 6f, 172f, 17.5f);
        b.Box(Floor, -32f, 164f, 17.5f, 32f, 212f, 18f);
        b.Stair(34f, 164f, 0, 1, 4f, 0f, 6f);
        b.Box(AshfallKind.Stair, 32f, 176f, 0f, 36f, 180f, 6f);
        b.Stair(34f, 180f, 0, 1, 4f, 0f, 6f, skip: 24);
        b.Box(AshfallKind.Stair, 32f, 192f, 0f, 36f, 196f, 12f);
        b.Stair(34f, 196f, 0, 1, 4f, 0f, 6f, skip: 48);
        b.Box(AshfallKind.Stair, 32f, 208f, 0f, 36f, 212f, 18f);
        Outerworks(b);
    }

    /// <summary>
    /// The two outerworks south of the south wall (items 6 and 7 of sketch 6; ours). West: a narrow terraced bastion x -68..-20, y 102..108 (6 m deep) of three terraces at 2, 4 and 6 m, each 16 m long, reached by a box-step stair
    /// from the ground at its west end and a stair between terraces, with 1.0 m parapets on both long sides; it stands 4 m from the wall and its top is 6 m under the rampart, so it gives no foot path onto the wall. East: an outer wall
    /// running south-east from the gate's east side in two steps, 6 m tall (x 20..36, y 104..108 then x 36..56, y 100..104), no stair (a jump pack only reaches its top). Both stay 8 m from the gate's approach (x -12..12) and clear of the avenue's 8 m lane (y 88..96).
    /// </summary>
    static void Outerworks(AshfallBuilder b)
    {
        float[] tops = [2f, 4f, 6f];
        for (int i = 0; i < 3; i++)
        {
            float x0 = -68f + 16f * i, x1 = x0 + 16f, top = tops[i];
            b.Box(Wall, x0, 102f, 0f, x1, 108f, top - 0.5f);
            b.Box(Floor, x0, 102f, top - 0.5f, x1, 108f, top);
            Parapet(b, x0, 102f, x1, 102.5f, top, 1f);
            Parapet(b, x0, 107.5f, x1, 108f, top, 1f);
        }
        Parapet(b, -20.5f, 102.5f, -20f, 107.5f, 6f, 1f);
        b.Stair(-72f, 105f, 1, 0, 4f, 0f, 2f);
        b.Stair(-56f, 105f, 1, 0, 4f, 2f, 2f);
        b.Stair(-40f, 105f, 1, 0, 4f, 4f, 2f);
        b.Box(Wall, 20f, 104f, 0f, 36f, 108f, 6f);
        b.Box(Wall, 36f, 100f, 0f, 56f, 104f, 6f);
    }

    /// <summary>
    /// The stronghold's towers (pass 3): a stair tower at each of the two inner corners (south-west, 3 storeys: floors at 6 and 12 m, a gateway at 12 m onto the west rampart; south-east, 4 storeys: floors to 18 m, a gateway at 18 m onto the east rampart),
    /// both inside the compound (the stair rises from the courtyard) and with no door in a wall that stands against the ring; and the keep tower (5 storeys, 30 m, 16 x 16 m, against the keep's west face at its north end) with a gateway at 12 m for the bridge.
    /// </summary>
    static void StrongholdTowers(AshfallPlacer p)
    {
        TowerDef[] list =
        [
            // SW: rot 2 (u runs west, v south): the left wall (east face) and the front wall (north face) face the courtyard, the right wall (west face) stands against the west leg and holds the gateway (column 1 = y 124..128).
            new("Stair tower SW", -68f, 116f, 16, 16, 2, 3, true, RightGateCol: 1, RightGateStorey: 2),
            // SE: rot 3 (u runs south, v east): the left wall (north face) and the front wall (west face) face the courtyard, the back wall (east face) stands against the east leg and holds the gateway (column 1 = y 124..128).
            new("Stair tower SE", 52f, 116f, 16, 16, 3, 4, true, BackGateCol: 1, BackGateStorey: 3),
            // The keep tower: rot 3, the front wall (west face, x -48) holds the gateway at 12 m (column 2 = y 200..204); the east face stands against the keep.
            new("Keep tower", -48f, 196f, 16, 16, 3, 5, true, FrontGateCol: 2, FrontGateStorey: 2, Roof: true),
        ];
        uint seed = 8000;
        foreach (var t in list)
        {
            var f = AshfallFrame.At(p.B, t.Rot, 0f, 0f, t.W, t.D, t.X, t.Y, out var world);
            seed += 101;
            var floors = Tower(f, t, seed);
            p.Claims.Add(world);
            p.Towers.Add(new AshfallTower(t.Name, world, t.N, t.Core, floors, 0f));
            switch (t.Name)
            {
                case "Stair tower SW":
                    p.Points["sw stair foot"] = new Vector3(-51f, 126f, BlockTop);
                    p.Points["sw tower top"] = floors[1];
                    p.Points["rampart west gate"] = new Vector3(-70f, 126f, 12f);
                    break;
                case "Stair tower SE":
                    p.Points["se stair foot"] = new Vector3(51f, 126f, BlockTop);
                    p.Points["se tower top"] = floors[2];
                    p.Points["rampart east gate"] = new Vector3(70f, 126f, 18f);
                    break;
                default:
                    p.Points["keep tower gateway"] = new Vector3(-46f, 202f, 12f);
                    p.Points["keep tower top"] = floors[3];
                    p.Points["keep bridge west"] = new Vector3(-66f, 202f, 12f);
                    p.Points["keep bridge middle"] = new Vector3(-58f, 202f, 12f);
                    p.Points["keep bridge east"] = new Vector3(-50f, 202f, 12f);
                    break;
            }
        }
        p.Claims.Add(new Vector4(-68f, 199f, -48f, 205f));
        p.Points["rampart west"] = new Vector3(-70f, 170f, 12f);
        p.Points["rampart south west"] = new Vector3(-40f, 114f, 12f);
        p.Points["rampart north"] = new Vector3(0f, 218f, 12f);
        p.Points["rampart east"] = new Vector3(70f, 170f, 18f);
        p.Points["rampart south east"] = new Vector3(40f, 114f, 18f);
        p.Points["terrace 1"] = new Vector3(-62f, 105f, 2f);
        p.Points["terrace 2"] = new Vector3(-44f, 105f, 4f);
        p.Points["terrace 3"] = new Vector3(-28f, 105f, 6f);
        p.Points["terrace foot"] = new Vector3(-74f, 105f, 0.25f);
    }
}
