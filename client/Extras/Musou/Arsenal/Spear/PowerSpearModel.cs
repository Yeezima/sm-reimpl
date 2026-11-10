using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using SmContent;
using SmSim.Extras.Musou.Arsenal;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The power spear's model (W3; docs/new-weapons-design.md section 6.3; **ours**, *placeholder*, built in code from the owner's reference description: a black shaft about 1.3 times a marine's height,
/// near the top an ornate bronze collar with a ring and a small crossguard, and a long single-edged blade, about a third of the whole length, with an angled chisel tip, glowing with a translucent
/// blue power field, deeper at the base and lighter toward the edge). It is drawn through the thunder hammer's attachment: the hammer's mount (the chest prop of the two-handed weapons) and root space, with the
/// shaft along +X (the hammer's own axis) and the grip about a third of the way up the shaft, so the spear is held where the hammer is. Musou Mode only (<see cref="ExtrasWeaponModel"/> asks only for a Musou
/// rig); every size and colour is ours, to refine against the owner's picture.
/// </summary>
public sealed partial class WeaponRig
{
    /// <summary>Metres, along the shaft (+X, the hammer's own axis) from the grip: the butt, the collar, the blade's base and its tip. The whole spear is 2.85 m.</summary>
    public const float SpearButt = -1.05f, SpearCollar = 0.84f, SpearBladeBase = 0.93f, SpearTip = 1.80f;

    /// <summary>Where along the shaft (metres from the two-handed grip point, the butt toward negative) the right hand closes when the spear is carried one-handed: 0.6 m from the butt.</summary>
    const float OneHandGrip = -0.45f;

    const string BladeShader = """
shader_type spatial;
render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, fog_disabled;
varying float along;
void vertex() { along = UV.x; }
void fragment() {
    // The power field: deeper blue at the base, lighter toward the tip and the edge (COLOR carries the colour, UV.y the distance from the spine to the edge).
    vec3 deep = vec3(0.06, 0.2, 0.95), light = vec3(0.35, 0.68, 1.0);
    vec3 colour = mix(deep, light, clamp(along * 0.7 + UV.y * 0.5, 0.0, 1.0));
    float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float rim = pow(1.0 - facing, 2.0);
    ALBEDO = colour * (0.85 + 1.1 * rim);
    ALPHA = clamp(0.38 + 0.4 * rim + 0.15 * UV.y, 0.0, 0.85);
}
""";

    static readonly ConditionalWeakTable<WeaponAsset, WeaponAsset> SpearAssets = new();

    /// <summary>
    /// W3: the spear's model. Carried in the right hand alone (the power sword's hand mount: the shaft where a sword's blade points, the grip at the hand) and swung in both hands (the thunder hammer's
    /// chest-prop mount, <see cref="WeaponAsset.SwingWield"/>); the rig changes mount with the strike (owner, 2026-11-10: "Right hand carries, both swing"). Without the sword's model it is the hammer's mount always.
    /// </summary>
    partial void PowerSpearModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset)
    {
        if (!ArsenalRules.PowerSpear.Equals(wargear, StringComparison.OrdinalIgnoreCase)) return;
        if (assets.Catalogue.Held("sm_mp_melee_2hand_thunder_hammer") is not { } hammer || assets.Get(hammer) is not { } hammerAsset) return;   // no hammer model, no spear
        WeaponMount? hand = null;
        if (assets.Catalogue.Held("sm_mp_melee_powersword") is { Wield: { Bone: not null } swordMount } sword && assets.Get(sword) is { } swordAsset) hand = HandMount(swordMount, swordAsset.Model);
        attachment = hand is null ? hammer : hammer with { Wield = hand };
        asset = SpearAssets.GetValue(hammerAsset, h => new WeaponAsset(h.Model, BuildSpear(), new Dictionary<string, ArrayMesh>(), 0, 0, 0, 0) { SwingWield = hand is null ? null : hammer.Wield });
    }

    /// <summary>
    /// The one-handed carry: a mount on the power sword's hand bone that puts the spear's own frame (the hammer's root space: the shaft along +X, the grip at the origin) where the sword sits in the hand
    /// (its blade axis the shaft's, its blade's flat side the spear's across axis). The handle node is left out on purpose, so the mesh is taken as already in that frame.
    /// </summary>
    static WeaponMount? HandMount(WeaponMount sword, WeaponModel model)
    {
        if (model.Locator(sword.HandleNode) is not { } grip) return null;
        var toBone = model.RootToBone(sword);
        var along = Num.Vector3.TransformNormal(Num.Vector3.UnitY, toBone);     // the sword's blade runs along its model's +Y
        var across = Num.Vector3.TransformNormal(Num.Vector3.UnitX, toBone);
        if (along.LengthSquared() < 1e-8f) return null;
        var x = Num.Vector3.Normalize(along);
        across -= x * Num.Vector3.Dot(across, x);
        if (across.LengthSquared() < 1e-8f) across = Num.Vector3.Cross(x, Num.Vector3.UnitZ);
        var y = Num.Vector3.Normalize(across);
        var z = Num.Vector3.Cross(x, y);
        var at = Num.Vector3.Transform(grip.Position, toBone) - x * OneHandGrip;   // the fist closes on the shaft this far (metres) along it from the spear's own grip point, so the butt hangs 0.6 m below the hand
        var frame = new Num.Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, at.X, at.Y, at.Z, 1f);
        return new WeaponMount("", sword.BodyNode, sword.Bone, frame, 1f);
    }

    // ---------------------------------------------------------------- the mesh

    /// <summary>The spear as one mesh in the hammer's root space (Z up, shaft along +X): surfaces black shaft, bronze fittings, the blade's dark core and its translucent field.</summary>
    public static ArrayMesh BuildSpear()
    {
        var mesh = new ArrayMesh();
        var steel = Material(new Color(0.025f, 0.025f, 0.03f), 0.85f, 0.38f);
        var bronze = Material(new Color(0.72f, 0.48f, 0.18f), 0.95f, 0.32f);

        // The black shaft with a small bronze butt cap and two bronze bands, an octagonal section.
        var shaft = new Builder();
        shaft.Cylinder(SpearButt, SpearCollar, 0.021f, 8);
        Surface(mesh, shaft, steel);

        var fittings = new Builder();
        fittings.Cylinder(SpearButt - 0.025f, SpearButt + 0.07f, 0.027f, 8);             // the butt cap
        fittings.Cylinder(-0.12f, -0.07f, 0.026f, 8);                                       // a grip band
        fittings.Cylinder(0.50f, 0.55f, 0.026f, 8);                                         // a second band
        fittings.Cylinder(SpearCollar - 0.05f, SpearBladeBase + 0.02f, 0.042f, 10);         // the collar
        fittings.Cylinder(SpearCollar - 0.11f, SpearCollar - 0.075f, 0.036f, 10);           // the collar's lower step
        fittings.Torus(SpearCollar - 0.06f, 0.075f, 0.011f, 14, 6);                          // the ring around the shaft below the collar
        // A small crossguard bracket: two short prongs either side of the blade's base (the blade lies in the XY plane).
        fittings.Box(new Vector3(SpearBladeBase - 0.03f, -0.115f, -0.012f), new Vector3(SpearBladeBase + 0.05f, 0.115f, 0.012f));
        fittings.Box(new Vector3(SpearBladeBase - 0.03f, -0.115f, -0.012f), new Vector3(SpearBladeBase + 0.11f, -0.095f, 0.012f));
        fittings.Box(new Vector3(SpearBladeBase - 0.03f, 0.095f, -0.012f), new Vector3(SpearBladeBase + 0.11f, 0.115f, 0.012f));
        Surface(mesh, fittings, bronze);

        // The blade: single-edged. In the XY plane the spine runs along -Y, the edge along +Y; the tip is angled (a chisel): the spine side runs out to the tip, the edge side stops short.
        var core = new Builder();
        core.Extrude(BladeOutline(0f), 0.0075f);
        Surface(mesh, core, Material(new Color(0.05f, 0.12f, 0.45f), 0.3f, 0.3f, new Color(0.15f, 0.4f, 1f), 1.6f));
        var field = new Builder(uv: true);
        field.Extrude(BladeOutline(0.012f), 0.018f);
        Surface(mesh, field, new ShaderMaterial { Shader = new Shader { Code = BladeShader }, RenderPriority = 1 });
        return mesh;
    }

    /// <summary>The blade's outline in the XY plane (x along the shaft, y across), grown by <paramref name="grow"/> metres all round.</summary>
    static Vector2[] BladeOutline(float grow) =>
    [
        new(SpearBladeBase - grow * 0.5f, -0.045f - grow),   // base, spine side
        new(SpearBladeBase - grow * 0.5f, 0.080f + grow),    // base, edge side
        new(SpearTip - 0.115f + grow, 0.080f + grow * 0.5f), // the edge ends short of the tip
        new(SpearTip + grow * 1.5f, -0.025f),                // the chisel tip, on the spine side
        new(SpearTip - 0.035f + grow, -0.045f - grow),       // the spine, a short fall behind the tip
    ];

    static StandardMaterial3D Material(Color albedo, float metallic, float roughness, Color? emission = null, float energy = 1f)
    {
        var m = new StandardMaterial3D { AlbedoColor = albedo, Metallic = metallic, Roughness = roughness, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        if (emission is { } e) { m.EmissionEnabled = true; m.Emission = e; m.EmissionEnergyMultiplier = energy; }
        return m;
    }

    static void Surface(ArrayMesh mesh, Builder b, Material material)
    {
        if (b.Vertices.Count == 0) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = b.Vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = b.Normals.ToArray();
        if (b.Uvs.Count == b.Vertices.Count) arrays[(int)Mesh.ArrayType.TexUV] = b.Uvs.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    /// <summary>Flat-shaded triangles for the spear's solids (the shapes are few and small).</summary>
    sealed class Builder
    {
        public readonly List<Vector3> Vertices = new(), Normals = new();
        public readonly List<Vector2> Uvs = new();
        readonly bool _uv;
        public Builder(bool uv = false) { _uv = uv; }

        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector2 ua = default, Vector2 ub = default, Vector2 uc = default)
        {
            var n = (b - a).Cross(c - a);
            n = n.LengthSquared() > 1e-12f ? n.Normalized() : Vector3.Up;
            Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
            Normals.Add(n); Normals.Add(n); Normals.Add(n);
            if (_uv) { Uvs.Add(ua); Uvs.Add(ub); Uvs.Add(uc); }
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Tri(a, b, c); Tri(a, c, d); }

        /// <summary>A prism along X from <paramref name="x0"/> to <paramref name="x1"/> with <paramref name="sides"/> sides.</summary>
        public void Cylinder(float x0, float x1, float radius, int sides)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = MathF.Tau * i / sides, a1 = MathF.Tau * (i + 1) / sides;
                Vector3 P(float x, float a) => new(x, MathF.Cos(a) * radius, MathF.Sin(a) * radius);
                Quad(P(x0, a0), P(x1, a0), P(x1, a1), P(x0, a1));
                Tri(P(x1, a0), new Vector3(x1, 0f, 0f), P(x1, a1));
                Tri(P(x0, a1), new Vector3(x0, 0f, 0f), P(x0, a0));
            }
        }

        /// <summary>A ring about the X axis at <paramref name="x"/> (major radius <paramref name="major"/>, tube <paramref name="minor"/>).</summary>
        public void Torus(float x, float major, float minor, int around, int tube)
        {
            Vector3 P(int i, int j)
            {
                float a = MathF.Tau * i / around, t = MathF.Tau * j / tube, r = major + minor * MathF.Cos(t);
                return new Vector3(x + minor * MathF.Sin(t), MathF.Cos(a) * r, MathF.Sin(a) * r);
            }
            for (int i = 0; i < around; i++)
                for (int j = 0; j < tube; j++) Quad(P(i, j), P(i + 1, j), P(i + 1, j + 1), P(i, j + 1));
        }

        public void Box(Vector3 min, Vector3 max)
        {
            Vector3 V(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(V(0, 0, 0), V(0, 1, 0), V(0, 1, 1), V(0, 0, 1)); Quad(V(1, 0, 0), V(1, 0, 1), V(1, 1, 1), V(1, 1, 0));
            Quad(V(0, 0, 0), V(0, 0, 1), V(1, 0, 1), V(1, 0, 0)); Quad(V(0, 1, 0), V(1, 1, 0), V(1, 1, 1), V(0, 1, 1));
            Quad(V(0, 0, 0), V(1, 0, 0), V(1, 1, 0), V(0, 1, 0)); Quad(V(0, 0, 1), V(0, 1, 1), V(1, 1, 1), V(1, 0, 1));
        }

        /// <summary>A flat plate of the outline (XY, in order) with a lens section: its middle ridge stands <paramref name="half"/> either side of the plane, the outline's edge in the plane.</summary>
        public void Extrude(Vector2[] outline, float half)
        {
            var centre = Vector2.Zero;
            foreach (var p in outline) centre += p;
            centre /= outline.Length;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in outline) { minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X); minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y); }
            Vector2 Uv(Vector2 p) => new((p.X - minX) / (maxX - minX), (p.Y - minY) / (maxY - minY));
            for (int i = 0; i < outline.Length; i++)
            {
                var a = outline[i]; var b = outline[(i + 1) % outline.Length];
                // A ridge point per vertex: the outline pulled to the middle, raised: a bevel all round.
                var ma = a + (centre - a) * 0.55f; var mb = b + (centre - b) * 0.55f;
                foreach (float s in new[] { 1f, -1f })
                {
                    var A = new Vector3(a.X, a.Y, 0f); var B = new Vector3(b.X, b.Y, 0f);
                    var MA = new Vector3(ma.X, ma.Y, s * half); var MB = new Vector3(mb.X, mb.Y, s * half);
                    var C = new Vector3(centre.X, centre.Y, s * half);
                    if (s > 0) { Tri(A, B, MB, Uv(a), Uv(b), Uv(mb)); Tri(A, MB, MA, Uv(a), Uv(mb), Uv(ma)); Tri(C, MA, MB, Uv(centre), Uv(ma), Uv(mb)); }
                    else { Tri(B, A, MA, Uv(b), Uv(a), Uv(ma)); Tri(B, MA, MB, Uv(b), Uv(ma), Uv(mb)); Tri(C, MB, MA, Uv(centre), Uv(mb), Uv(ma)); }
                }
            }
        }
    }
}
