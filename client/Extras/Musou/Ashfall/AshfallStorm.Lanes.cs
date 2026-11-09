using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// Draft 10's lane marks (owner, 2026-11-09: "make the lanes to each point from the stronghold, and between each point. there should be clear pathways between them all"; ours, placeholder demonstration; client only,
/// visual only, no collision). The lanes (<see cref="AshfallLanes"/>) are paved like the avenues; here each lane and the paved road from the compound get a worn pale kerb band along both long edges, so the way from a
/// street to a point reads from a distance through the haze. One multimesh (one draw call, no shadows). <c>--ashfall-lane-marks off</c> leaves them out (the before of the perf comparison).
/// </summary>
public partial class AshfallStorm
{
    /// <summary>A kerb band's width and its lift over the block surface (metres; over the road slabs' relief, under the 0.4 m step; ours).</summary>
    public const float LaneMarkWidth = 0.45f, LaneMarkLift = 0.07f;

    /// <summary>The bands' colour: a worn stone, a little lighter than the road slabs under the light pass (0.5 read as fresh paint) (ours).</summary>
    public static readonly Color LaneMarkColour = new(0.31f, 0.30f, 0.29f);

    /// <summary>Test aid <c>--ashfall-lane-marks off</c>: no lane marks (the draft-9 look).</summary>
    public static bool LaneMarksOff
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-lane-marks");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "off";
        }
    }

    /// <summary>The kerb bands of the lanes and of the paved road (Road parts), as one multimesh under <see cref="_world"/>; returns the number of bands.</summary>
    int AddLaneMarks(AshfallLevel level)
    {
        if (LaneMarksOff) return 0;
        var bands = new List<(System.Numerics.Vector2 Centre, float Length, bool AlongX, float Top)>();
        foreach (var part in level.Parts)
        {
            if (part.Kind != AshfallKind.Road || AshfallBox.Of(part) is not { } b) continue;
            bool alongX = MathF.Abs(b.AxisX.X) > 0.5f;   // the box's long side (Length) along world X or Y; the level's roads are square to the grid
            var across = alongX ? new System.Numerics.Vector2(0f, 1f) : new System.Numerics.Vector2(1f, 0f);
            float half = b.Depth / 2f - LaneMarkWidth / 2f;
            foreach (float side in new[] { -1f, 1f })
                bands.Add((new System.Numerics.Vector2(b.Cx, b.Cy) + across * (side * half), b.Length, alongX, b.Z1));
        }
        if (bands.Count == 0) return 0;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh { Size = Vector3.One }, InstanceCount = bands.Count };
        for (int i = 0; i < bands.Count; i++)
        {
            var (c, len, alongX, top) = bands[i];
            // game frame: X east, Y north, Z up -> Godot (G); a box 1 x 1 x 1 scaled to the band (length along the road, width across, 4 cm thick)
            var pos = G(new Vector3(c.X, c.Y, top + LaneMarkLift - 0.02f));
            var scale = alongX ? new Vector3(len, 0.04f, LaneMarkWidth) : new Vector3(LaneMarkWidth, 0.04f, len);
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(scale), pos));
        }
        var mat = new StandardMaterial3D { AlbedoColor = LaneMarkColour, Roughness = 1f, MetallicSpecular = 0.1f };
        _world.AddChild(new MultiMeshInstance3D { Name = "LaneMarks", Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        return bands.Count;
    }
}
