using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// A stunned boss drawn dizzy (owner, 2026-11-14; <see cref="MusouDizzyRules"/>): it stands on its idle clip where it faced, wobbles, and a ring of stars circles above its head until the stun ends. The stars are
/// small yellow billboards (**ours**: no original equivalent), pooled per boss and hidden the frame it is no longer dizzy. Used by <see cref="MusouCrowd.Frame"/>.
/// </summary>
public partial class MusouCrowd
{
    /// <summary>Test aid (<c>--musou-old-boss-stun</c>): a stunned boss plays its stagger clip as before 2026-11-14 (the Warboss's turns the body round), with no stars.</summary>
    public static bool OldBossStun { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-boss-stun") >= 0;

    sealed class Ring { public Node3D Root = null!; public MeshInstance3D[] Stars = null!; public bool Used; }
    readonly Dictionary<int, Ring> _rings = new();
    readonly List<int> _ringGone = new();
    readonly Dictionary<MusouLooks.Look, float> _heights = new();
    static ArrayMesh? _starMesh;
    static StandardMaterial3D? _starMaterial;
    /// <summary>Boss rings drawn this frame (the tests and the capture aid).</summary>
    public int DizzyShown { get; private set; }

    /// <summary>The Orks drawn dizzy this frame: where, how tall, and seconds into the stun.</summary>
    readonly List<(int Id, MusouEnemyType Type, Vector3 At, float Height, float Seconds)> _dizzy = new();

    static bool IsDizzy(in MusouShown e) => !OldBossStun && MusouDizzyRules.Dizzy(e.Type, e.State);

    /// <summary>A look's model height at its scale (metres, game Z; measured once).</summary>
    float HeightOfLook(MusouLooks.Look? look)
    {
        if (look is null) return 3f;
        if (_heights.TryGetValue(look, out float known)) return known;
        float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
        foreach (var piece in look.Assets.Pieces) { var box = piece.Mesh.GetAabb(); top = MathF.Max(top, box.End.Z); bottom = MathF.Min(bottom, box.Position.Z); }
        float height = top > bottom ? (top - bottom) * look.Scale : 3f;
        _heights[look] = height;
        return height;
    }

    /// <summary>The lean of a dizzy boss's body (<see cref="Item.Turn"/>): about its feet, toward a direction that goes round.</summary>
    static Quaternion Lean(float seconds)
    {
        var (angle, toward) = MusouDizzyRules.Sway(seconds);
        var axis = new Vector3(-MathF.Sin(toward), MathF.Cos(toward), 0f);   // horizontal, a quarter turn from the lean's direction: the body tips toward `toward`
        return new Quaternion(axis, angle);
    }

    void NoteDizzy(in MusouShown e, Vector3 at, float seconds) => _dizzy.Add((e.Id, e.Type, at, HeightOfLook(_looks.For(e.Type)), seconds));

    /// <summary>After the frame's members: places this frame's rings and hides the rest.</summary>
    void UpdateRings()
    {
        DizzyShown = _dizzy.Count;
        foreach (var r in _rings.Values) r.Used = false;
        foreach (var (id, type, at, height, seconds) in _dizzy)
        {
            if (!_rings.TryGetValue(id, out var ring)) _rings[id] = ring = BuildRing();
            ring.Used = true;
            ring.Root.Visible = true;
            ring.Root.Position = at + new Vector3(0f, 0f, height * MusouDizzyRules.StarHeight(type));
            float radius = height * MusouDizzyRules.StarOrbitRadius;
            for (int k = 0; k < ring.Stars.Length; k++)
            {
                float a = MusouDizzyRules.StarAngle(k, seconds);
                ring.Stars[k].Position = new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, MathF.Sin(a * 2f + k) * radius * 0.08f);   // a slight bob so the ring is not flat
                float size = height * MusouDizzyRules.StarSize;
                ring.Stars[k].Scale = new Vector3(size, size, size);
            }
        }
        _dizzy.Clear();
        _ringGone.Clear();
        foreach (var (id, r) in _rings) if (!r.Used) { r.Root.Visible = false; if (!_seen.Contains(id)) _ringGone.Add(id); }
        foreach (var id in _ringGone) { _rings[id].Root.QueueFree(); _rings.Remove(id); }
    }

    Ring BuildRing()
    {
        _starMesh ??= BuildStarMesh();
        _starMaterial ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.9f, 0.25f), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, DisableReceiveShadows = true,
        };
        var root = new Node3D { Name = "DizzyRing" };
        var stars = new MeshInstance3D[MusouDizzyRules.StarCount];
        for (int k = 0; k < stars.Length; k++)
        {
            stars[k] = new MeshInstance3D { Mesh = _starMesh, MaterialOverride = _starMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            root.AddChild(stars[k]);
        }
        AddChild(root);
        return new Ring { Root = root, Stars = stars };
    }

    /// <summary>A five-pointed star of outer radius 1 in the XY plane (a triangle fan; the billboard turns it to the camera).</summary>
    static ArrayMesh BuildStarMesh()
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        const int points = 5;
        for (int i = 0; i < points * 2; i++)
        {
            float a0 = MathF.PI / 2f + MathF.Tau * i / (points * 2), a1 = MathF.PI / 2f + MathF.Tau * (i + 1) / (points * 2);
            float r0 = i % 2 == 0 ? 1f : 0.42f, r1 = i % 2 == 0 ? 0.42f : 1f;
            verts.Add(Vector3.Zero); verts.Add(new Vector3(MathF.Cos(a0) * r0, MathF.Sin(a0) * r0, 0f)); verts.Add(new Vector3(MathF.Cos(a1) * r1, MathF.Sin(a1) * r1, 0f));
            for (int k = 0; k < 3; k++) normals.Add(Vector3.Back);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
