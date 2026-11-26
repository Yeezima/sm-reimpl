using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmClient.Extras.Warfront.Chaos.Finale;

/// <summary>
/// The Chaos Lord's telegraphs in the duel (Chaos Warfront C5c; tag 51 kind 17; code-made, ours): the warp leap's landing marker, a red-violet ground ring at the target that shrinks from
/// <see cref="ChaosDuelLook.LeapRingStartScale"/> x <see cref="WarfrontDuelRules.LeapRadius"/> to the landing's 4 m over the marker's seconds over a faint disc of the landing's size, and the bolt's muzzle cue, a hot
/// flash at the Lord's chest. The slam and the charge are already drawn by Musou Mode's boss telegraphs (the Warboss's ring and lane, <c>MusouTelegraphs</c>), so nothing more is drawn for them here.
/// A child of the game space: its positions are the sim's (z up). It goes with the map.
/// </summary>
public sealed partial class LordTelegraphFx : Node3D
{
    sealed class Mark
    {
        public MeshInstance3D Ring = null!, Disc = null!;
        public ChaosDuelTold Told;
    }

    sealed class Flash
    {
        public MeshInstance3D Ball = null!;
        public ChaosDuelTold Told;
    }

    readonly List<Mark> _marks = new();
    readonly List<Flash> _flashes = new();
    static ArrayMesh? _ring, _disc;
    static SphereMesh? _ball;

    /// <summary>The clock the telegraphs' arrival times are on (the view's connection seconds).</summary>
    public Func<double>? Now;

    /// <summary>A new telegraph (its first copy).</summary>
    public void Add(in ChaosDuelTold told)
    {
        switch (told.Told.Ability)
        {
            case WarfrontLordTelegraph.Leap:
            {
                var colour = new Color(ChaosDuelLook.LeapColour.R, ChaosDuelLook.LeapColour.G, ChaosDuelLook.LeapColour.B);
                var m = new Mark
                {
                    Told = told,
                    Ring = Instance(_ring ??= Annulus(0.9f, 1f, 64), colour, 0.9f),
                    Disc = Instance(_disc ??= Annulus(0f, 1f, 48), colour, 0.18f),
                };
                var at = told.Told.Position;
                m.Ring.Position = m.Disc.Position = new Vector3(at.X, at.Y, at.Z + 0.06f);
                m.Disc.Scale = new Vector3(WarfrontDuelRules.LeapRadius, WarfrontDuelRules.LeapRadius, 1f);
                _marks.Add(m);
                break;
            }
            case WarfrontLordTelegraph.Bolt:
            {
                var colour = new Color(ChaosDuelLook.MuzzleColour.R, ChaosDuelLook.MuzzleColour.G, ChaosDuelLook.MuzzleColour.B);
                var f = new Flash { Told = told, Ball = Instance(_ball ??= new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 }, colour, 0.95f) };
                var at = told.Told.Position;
                f.Ball.Position = new Vector3(at.X, at.Y, at.Z);
                f.Ball.Scale = Vector3.One * ChaosDuelLook.MuzzleRadius;
                _flashes.Add(f);
                break;
            }
        }
    }

    MeshInstance3D Instance(Mesh mesh, Color colour, float alpha)
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = false,
            AlbedoColor = new Color(colour, alpha),
            RenderPriority = 5,
        };
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(mi);
        return mi;
    }

    /// <summary>A flat ring (or a disc for <paramref name="inner"/> 0) in the sim's ground plane (x, y; z up), radius 1.</summary>
    static ArrayMesh Annulus(float inner, float outer, int segments)
    {
        var verts = new List<Vector3>();
        for (int k = 0; k < segments; k++)
        {
            float a0 = Mathf.Tau * k / segments, a1 = Mathf.Tau * (k + 1) / segments;
            var o0 = new Vector3(MathF.Cos(a0) * outer, MathF.Sin(a0) * outer, 0f);
            var o1 = new Vector3(MathF.Cos(a1) * outer, MathF.Sin(a1) * outer, 0f);
            var i0 = new Vector3(MathF.Cos(a0) * inner, MathF.Sin(a0) * inner, 0f);
            var i1 = new Vector3(MathF.Cos(a1) * inner, MathF.Sin(a1) * inner, 0f);
            verts.Add(i0); verts.Add(o0); verts.Add(o1);
            if (inner > 0f) { verts.Add(i0); verts.Add(o1); verts.Add(i1); }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public override void _Process(double delta)
    {
        double now = Now?.Invoke() ?? 0.0;
        for (int k = _marks.Count - 1; k >= 0; k--)
        {
            var m = _marks[k];
            if (m.Told.Over(now)) { m.Ring.QueueFree(); m.Disc.QueueFree(); _marks.RemoveAt(k); continue; }
            float p = m.Told.Progress(now);
            float r = WarfrontDuelRules.LeapRadius * (ChaosDuelLook.LeapRingStartScale + (1f - ChaosDuelLook.LeapRingStartScale) * p);
            m.Ring.Scale = new Vector3(r, r, 1f);
        }
        for (int k = _flashes.Count - 1; k >= 0; k--)
        {
            var f = _flashes[k];
            if (f.Told.Over(now)) { f.Ball.QueueFree(); _flashes.RemoveAt(k); continue; }
            float p = f.Told.Progress(now);
            f.Ball.Scale = Vector3.One * ChaosDuelLook.MuzzleRadius * (1f + 0.6f * p);
            if (f.Ball.MaterialOverride is StandardMaterial3D mat) mat.AlbedoColor = new Color(mat.AlbedoColor, 0.95f * (1f - p));
        }
    }

    /// <summary>Every mark goes (the duel ended or the map went).</summary>
    public void ClearAll()
    {
        foreach (var m in _marks) { m.Ring.QueueFree(); m.Disc.QueueFree(); }
        foreach (var f in _flashes) f.Ball.QueueFree();
        _marks.Clear();
        _flashes.Clear();
    }

    /// <summary>The number of marks and flashes showing (the capture aid's log).</summary>
    public int Showing => _marks.Count + _flashes.Count;
}
