using System;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The capture circle (owner, 2026-11-10: "a black circle around [the banner] on the floor that is drawn and completed as the point is progressing from 0% capture to 100% captured by the player or the orks"):
/// a flat black band on the floor (<see cref="CircleWidth"/> wide, 6 cm up, on render layer 2 like the bodies) drawn over <see cref="TfwMarkerRules.CircleFill"/> of its turn from the ring's +Y point,
/// rebuilt only when the fill moves a step (<see cref="CircleSteps"/>). A mesh, not the decal ring: the decal ring does not show on Ashfall's city ground (found 2026-11-10). The commander's position
/// keeps a full band. Width and steps ours.
/// </summary>
public partial class TfwMarkers
{
    public const int CircleSteps = 120;
    /// <summary>The band's width (m; readable at a distance: about three times the old 0.22 m ring).</summary>
    public const float CircleWidth = 0.7f;

    sealed class ProgressRing
    {
        public float Radius;
        public int Step = -1;
        public ArrayMesh? Arc;
        public MeshInstance3D? ArcNode;
    }

    readonly ProgressRing?[] _progress = new ProgressRing?[WarRules.Positions + 1];

    /// <summary>The arc band for position <paramref name="k"/> (game space, flat, <see cref="CircleWidth"/> wide), rebuilt when its fill moves a step.</summary>
    void ProgressArc(int k, Vector3 ground, float radius, float fill)
    {
        var p = _progress[k] ??= new ProgressRing { Radius = radius };
        if (p.ArcNode is null)
        {
            p.ArcNode = new MeshInstance3D { Name = "CaptureArc" + k, MaterialOverride = Paint(RingBlack, 1f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = 2 };
            AddChild(p.ArcNode);
        }
        p.ArcNode.Visible = true;
        p.ArcNode.Position = ground + new Vector3(0f, 0f, 0.06f);
        int step = (int)MathF.Round(Math.Clamp(fill, 0f, 1f) * CircleSteps);
        if (p.Arc is not null && step == p.Step && MathF.Abs(p.Radius - radius) < 0.01f) return;
        p.Step = step;
        p.Radius = radius;
        p.Arc ??= new ArrayMesh();
        p.Arc.ClearSurfaces();
        int segments = Math.Max(1, step * 128 / CircleSteps);
        if (step == 0) { p.ArcNode.Mesh = p.Arc; return; }
        var verts = new Vector3[(segments + 1) * 2];
        float hw = CircleWidth / 2f, span = MathF.Tau * step / CircleSteps;
        for (int i = 0; i <= segments; i++)
        {
            float a = span * i / segments, sx = MathF.Sin(a), sy = MathF.Cos(a);
            verts[2 * i] = new Vector3(sx * (radius - hw), sy * (radius - hw), 0f);
            verts[2 * i + 1] = new Vector3(sx * (radius + hw), sy * (radius + hw), 0f);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        p.Arc.AddSurfaceFromArrays(Mesh.PrimitiveType.TriangleStrip, arrays);
        p.ArcNode.Mesh = p.Arc;
    }

    void HideArc(int k) { if (k < _progress.Length && _progress[k]?.ArcNode is { } a) a.Visible = false; }
}
