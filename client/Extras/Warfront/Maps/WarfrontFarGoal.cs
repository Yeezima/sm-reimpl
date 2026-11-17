using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// The advancing goal (slice W5d; owner requirement: the far spire and the manufactorum stacks are the goal the advance moves toward). <see cref="AshfallFrontScenery"/> puts the spire a constant <see cref="AshfallFrontScenery.SpireNorth"/> m north of
/// the band's centre on every band, so the goal never came closer. The pilot's composed world puts it beyond the urban core, the LAST band (docs/musou-ashfall-graybox.md "Front pilot": the spire 1150 m north of the core's centre), and the scenery
/// draws the lane's bands one <see cref="AshfallFrontScenery.NeighbourOffset"/> m apart along the avenue. So the spire stands at the pilot's distance beyond the last band and, from band k, that plus the bands still to cross:
/// <c>SpireNorth + (BandCount - 1 - band) x NeighbourOffset</c> = 2190, 1670, 1150 m for nodes 1, 2 and 3. Client scenery only (no collision, never hashed): the scenery's own spire and stacks nodes are moved north by the difference, and a far ground is added
/// under them where the scenery's ground (<see cref="AshfallFrontScenery.Reach"/>) ends. Everything is ours (placeholder demonstration, refine register).
/// </summary>
public static class WarfrontFarGoal
{
    /// <summary>The spire's distance north of band <paramref name="band"/>'s centre (m).</summary>
    public static float SpireDistance(int band) => AshfallFrontScenery.SpireNorth + (AshfallFront.BandCount - 1 - band) * AshfallFrontScenery.NeighbourOffset;

    /// <summary>How far the scenery's own spire (built at <see cref="AshfallFrontScenery.SpireNorth"/>) moves north for band <paramref name="band"/>.</summary>
    public static float Shift(int band) => SpireDistance(band) - AshfallFrontScenery.SpireNorth;

    /// <summary>Moves the scenery's spire and stacks and adds the far ground; returns a one-line note for the log.</summary>
    public static string Apply(Node3D scenery, int band)
    {
        float shift = Shift(band);
        string note = $"spire {SpireDistance(band):F0} m north (pilot: {AshfallFrontScenery.SpireNorth:F0} m beyond the last band, plus {shift:F0} m of bands to cross)";
        if (shift <= 0f) return note;
        // game space (Z up): +Y is north; the nodes are under the band node, in that frame
        var moved = new List<string>();
        foreach (string name in new[] { "CathedralSpire", "ManufactorumStacks" })
        {
            if (scenery.GetNodeOrNull<Node3D>(name) is { } node) { node.Position += new Vector3(0f, shift, 0f); moved.Add(name); }
            else GD.Print($"warfront: far goal: the scenery has no node named \"{name}\" (Musou's AshfallFrontScenery renamed it?); it is not moved");
        }
        note += $"; moved {(moved.Count == 0 ? "nothing" : string.Join(" and ", moved))}";
        float x0 = AshfallFrontScenery.Reach;
        float[] xs = [-x0, -950f, -750f, -580f, -440f, -340f, -280f, -AshfallFrontScenery.LineHalf, AshfallFrontScenery.LineHalf, 280f, 340f, 440f, 580f, 750f, 950f, x0];
        float y0 = AshfallFrontScenery.Reach, y1 = SpireDistance(band) + 400f;
        var ground = new List<V3>();
        for (int i = 0; i + 1 < xs.Length; i++)
        {
            float a = xs[i], b = xs[i + 1];
            V3 p0 = new(a, y0, AshfallFrontScenery.Height(a)), p1 = new(b, y0, AshfallFrontScenery.Height(b)), p2 = new(b, y1, AshfallFrontScenery.Height(b)), p3 = new(a, y1, AshfallFrontScenery.Height(a));
            ground.AddRange([p0, p1, p2, p0, p2, p3]);
        }
        scenery.AddChild(new MeshInstance3D
        {
            Name = "FarGround", Mesh = AshfallView.MeshOf([.. ground]),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.47f, 0.44f, 0.41f), Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return note;
    }
}
