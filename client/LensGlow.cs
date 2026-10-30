using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent.Customization;

namespace SmClient;

/// <summary>
/// fx/character-visuals (owner, 2026-10-30, item 17: "eye lenses of all Marines and Chaos glow in the colour assigned in the customizer"; **ours**: the install has an eye-glow effect only for
/// some Chaos weapons, see <see cref="EyeGlow"/>): a soft glow at each eye of a Space Marine or Chaos character, in the look's lens colour (the colour brought to full brightness, as
/// <see cref="EyeGlow.TintOf"/>) or, with no lens colour chosen, the lenses' own red (<see cref="DefaultColour"/>). Two additive camera-facing sprites per eye (a halo and a small hot core)
/// on a node under the head bone, so the animation carries them and hiding the character hides them. The eye places are found on the helmet itself: the
/// centre of each lens rectangle of its diffuse texture (<see cref="LensRegions"/>) mapped through the mesh's texture coordinates to a point on the model (<see cref="SpotsFor"/>), lifted a
/// little off the surface; a helmet without lens rectangles (two Chaos berserk helmets) gets the rig's own <c>fx_lefteye_csm</c> / <c>fx_righteye_csm</c> places (<see cref="LeftEye"/>,
/// <see cref="RightEye"/>, head-bone space). The same node serves the match and the customizer's previews.
/// </summary>
public sealed class LensGlow
{
    /// <summary>The eye places in head-bone space (the rig body markers <c>fx_lefteye_csm</c> and <c>fx_righteye_csm</c> on <c>RIG_Head</c>; numbers only).</summary>
    public static readonly Vector3 LeftEye = new(0.144f, 0.137f, -0.049f), RightEye = new(0.144f, 0.137f, 0.048f);
    public const string HeadBone = "RIG_Head";
    /// <summary>The glow without a lens colour (ours, estimates): a Space Marine's the lenses' own red; a Chaos Marine's the blue of the data's eye effects (<see cref="EyeGlow"/>: (33, 110, 255) for the bolters and melee weapons).</summary>
    public static readonly Color DefaultColour = new(1f, 0.16f, 0.07f), DefaultChaosColour = new(0.13f, 0.43f, 1f);
    /// <summary>Sprite sizes in metres, halo and core (ours).</summary>
    public const float HaloSize = 0.055f, CoreSize = 0.016f;
    /// <summary>How far a glow sits off the helmet's surface along its normal (m, ours).</summary>
    public const float Lift = 0.012f;

    /// <summary>Test aid <c>--no-lens-glow</c> or <c>SM_NO_LENS_GLOW=1</c> (before / after).</summary>
    public static readonly bool Off = System.Environment.GetEnvironmentVariable("SM_NO_LENS_GLOW") == "1" || OS.GetCmdlineUserArgs().Contains("--no-lens-glow");

    static Texture2D? _gradient;
    static Texture2D Gradient()
    {
        if (_gradient is not null) return _gradient;
        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 1f, 1f));
        g.SetColor(1, new Color(1f, 1f, 1f, 0f));
        g.AddPoint(0.35f, new Color(1f, 1f, 1f, 0.45f));
        return _gradient = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64 };
    }

    /// <summary>One eye: the bone it follows and the place in that bone's space.</summary>
    public readonly record struct Spot(string Bone, Vector3 Local);

    static readonly Dictionary<string, Spot[]> SpotCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The eye places of a helmet piece (cached by its package): the centre of each lens rectangle found on the mesh's surface through its texture coordinates, in the space of the
    /// bone the surface is skinned to; the rig's eye markers on the head bone without rectangles or when the mesh does not give them.
    /// </summary>
    public static Spot[] SpotsFor(AssetPiece piece)
    {
        if (SpotCache.TryGetValue(piece.Name, out var known)) return known;
        var found = new List<Spot>();
        foreach (var rect in LensRegions.For(piece.Name))
        {
            var (u0, v0, u1, v1) = LensRegions.Uv(rect);
            if (Locate(piece, new Vector2((u0 + u1) * 0.5f, (v0 + v1) * 0.5f)) is { } spot) found.Add(spot);
        }
        if (found.Count == 0) found.AddRange([new Spot(HeadBone, LeftEye), new Spot(HeadBone, RightEye)]);
        if (System.Environment.GetEnvironmentVariable("SM_LENS_DEBUG") == "1") GD.Print($"lens glow: {piece.Name}: {string.Join("; ", found.Select(f => $"{f.Bone} {f.Local.X:F3},{f.Local.Y:F3},{f.Local.Z:F3}"))}");
        return SpotCache[piece.Name] = found.ToArray();
    }

    /// <summary>The point of the mesh whose texture coordinates are <paramref name="uv"/> (the triangle that holds it, else the nearest one), as a place in its bone's space; null without geometry.</summary>
    static Spot? Locate(AssetPiece piece, Vector2 uv)
    {
        float best = float.MaxValue;
        Spot? result = null;
        for (int surface = 0; surface < piece.Mesh.GetSurfaceCount(); surface++)
        {
            var arrays = piece.Mesh.SurfaceGetArrays(surface);
            if (arrays.Count <= (int)Mesh.ArrayType.Index || arrays[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil || arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil) continue;
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(uvs[i].X - MathF.Floor(uvs[i].X), uvs[i].Y - MathF.Floor(uvs[i].Y));   // the shader wraps them (fract): the meshes store V below 0
            var normals = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? [] : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil ? [] : arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.Nil ? [] : arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            int perVertex = vertices.Length > 0 && bones.Length % vertices.Length == 0 && bones.Length > 0 ? bones.Length / vertices.Length : 0;
            var index = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int triangles = (index?.Length ?? vertices.Length) / 3;
            for (int t = 0; t < triangles; t++)
            {
                int a = index?[t * 3] ?? t * 3, b = index?[t * 3 + 1] ?? t * 3 + 1, c = index?[t * 3 + 2] ?? t * 3 + 2;
                if (a >= uvs.Length || b >= uvs.Length || c >= uvs.Length) continue;
                var (wa, wb, wc) = Barycentric(uv, uvs[a], uvs[b], uvs[c]);
                float outside = wa >= 0f && wb >= 0f && wc >= 0f ? 0f : Math.Min(SegmentDistance(uv, uvs[a], uvs[b]), Math.Min(SegmentDistance(uv, uvs[b], uvs[c]), SegmentDistance(uv, uvs[c], uvs[a])));   // 0 inside; the distance in the texture otherwise
                if (outside >= best) continue;
                best = outside;
                var p = vertices[a] * wa + vertices[b] * wb + vertices[c] * wc;
                var n = normals.Length > c ? (normals[a] * wa + normals[b] * wb + normals[c] * wc).Normalized() : Vector3.Zero;
                int strongest = wa >= wb && wa >= wc ? a : wb >= wc ? b : c;
                int bind = 0;
                if (perVertex > 0 && weights.Length >= (strongest + 1) * perVertex)
                {
                    float top = -1f;
                    for (int k = 0; k < perVertex; k++) if (weights[strongest * perVertex + k] > top) { top = weights[strongest * perVertex + k]; bind = bones[strongest * perVertex + k]; }
                }
                if (bind < 0 || bind >= piece.Skin.GetBindCount()) bind = 0;
                string name = piece.Skin.GetBindName(bind);
                var pose = piece.Skin.GetBindPose(bind);
                result = new Spot(name.Length > 0 ? name : HeadBone, pose * (p + n * Lift));
            }
        }
        if (System.Environment.GetEnvironmentVariable("SM_LENS_DEBUG") == "1") GD.Print($"lens glow: uv {uv.X:F3},{uv.Y:F3} -> distance outside the nearest triangle {best:F4}");
        return result;
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float l = ab.LengthSquared();
        float t = l < 1e-12f ? 0f : Math.Clamp((p - a).Dot(ab) / l, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    static (float, float, float) Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = b - a; var v1 = c - a; var v2 = p - a;
        float d = v0.X * v1.Y - v1.X * v0.Y;
        if (Math.Abs(d) < 1e-12f) return (-1f, -1f, -1f);
        float wb = (v2.X * v1.Y - v1.X * v2.Y) / d, wc = (v0.X * v2.Y - v2.X * v0.Y) / d;
        return (1f - wb - wc, wb, wc);
    }

    readonly List<BoneAttachment3D> _nodes = new();   // directly under the skeleton (an attachment finds its skeleton only as its parent)
    bool _built, _shown;
    Skeleton3D? _skeleton;
    string _helmet = "";
    readonly StandardMaterial3D _halo = Make(), _core = Make();
    Color _colour = new(-1f, 0f, 0f);

    static StandardMaterial3D Make() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        AlbedoTexture = Gradient(),
        DisableFog = true,
        DisableReceiveShadows = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Whether the glow nodes exist on a character now (test aid).</summary>
    public bool Attached => _nodes.Count > 0 && GodotObject.IsInstanceValid(_nodes[0]) && _nodes[0].IsInsideTree();
    /// <summary>The colour the glow is drawn in now.</summary>
    public Color Colour => _colour;

    /// <summary>The colour for a look: the lens colour at full brightness, else the race's default (<see cref="DefaultColour"/>, <see cref="DefaultChaosColour"/>).</summary>
    public static Color ColourFor((byte R, byte G, byte B)? lens, bool chaos) => EyeGlow.TintOf(lens) ?? (chaos ? DefaultChaosColour : DefaultColour);

    /// <summary>
    /// Per frame: puts the glow on <paramref name="character"/> (built on first use, and again when its skeleton or helmet changed), shows or hides it, and sets its colour. A no-op
    /// without a skeleton or when switched off.
    /// </summary>
    public void Update(CharacterViewer character, Color colour, bool show)
    {
        if (Off || character.Skeleton is not { } skeleton) { Hide(); return; }
        AssetPiece? helmet = null;
        if (character.Assets is { } assets) foreach (var piece in assets.Pieces) if (piece.Name.Contains("_helmet", StringComparison.OrdinalIgnoreCase)) { helmet = piece; break; }
        bool valid = _built && ReferenceEquals(_skeleton, skeleton) && _helmet == (helmet?.Name ?? "") && _nodes.Count > 0 && GodotObject.IsInstanceValid(_nodes[0]) && _nodes[0].GetParent() == skeleton;
        if (!valid && !Build(skeleton, helmet)) return;
        if (_shown != show) { _shown = show; foreach (var n in _nodes) n.Visible = show; }
        if (!show || colour == _colour) return;
        _colour = colour;
        _halo.AlbedoColor = new Color(colour.R, colour.G, colour.B, 0.5f);
        _core.AlbedoColor = new Color(Mathf.Lerp(colour.R, 1f, 0.55f), Mathf.Lerp(colour.G, 1f, 0.55f), Mathf.Lerp(colour.B, 1f, 0.55f), 0.7f);
    }

    /// <summary>Hides the glow (the character is dead or switched off).</summary>
    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        foreach (var n in _nodes) if (GodotObject.IsInstanceValid(n)) n.Visible = false;
    }

    bool Build(Skeleton3D skeleton, AssetPiece? helmet)
    {
        foreach (var n in _nodes) if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _nodes.Clear();
        _skeleton = skeleton;
        _helmet = helmet?.Name ?? "";
        _built = true;
        _shown = false;
        var spots = helmet is null ? [new Spot(HeadBone, LeftEye), new Spot(HeadBone, RightEye)] : SpotsFor(helmet);
        var attachments = new Dictionary<string, BoneAttachment3D>(StringComparer.OrdinalIgnoreCase);
        foreach (var spot in spots)
        {
            if (skeleton.FindBone(spot.Bone) < 0) continue;
            if (!attachments.TryGetValue(spot.Bone, out var attach))
            {
                attachments[spot.Bone] = attach = new BoneAttachment3D { Name = "LensGlow_" + spot.Bone, Visible = false };
                skeleton.AddChild(attach);
                attach.BoneName = spot.Bone;
                _nodes.Add(attach);
            }
            attach.AddChild(Quad("Halo", _halo, HaloSize, spot.Local));
            attach.AddChild(Quad("Core", _core, CoreSize, spot.Local));
        }
        _colour = new Color(-1f, 0f, 0f);   // the colours are set again
        return _nodes.Count > 0;
    }

    static MeshInstance3D Quad(string name, Material material, float size, Vector3 at) => new()
    {
        Name = name, Mesh = new QuadMesh { Size = new Vector2(size, size) }, MaterialOverride = material, Position = at,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
    };
}
