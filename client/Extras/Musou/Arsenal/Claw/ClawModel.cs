using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Arsenal;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// W2, the lightning claw's model, built in code (docs/new-weapons-design.md section 5, "Look"; the owner's reference images, described by the lead: a bulky armoured steel gauntlet on the
/// right forearm with pistons and brass bands on the back of the hand and the wrist, and four long curved steel talons from the knuckles, each 1.9 times the forearm, tapering to
/// points). Nothing is loaded for it but the power sword's own grip (its handle node and mount are the claw's: the claw is held where the sword is), so it is **ours** (a placeholder shape
/// from words, to be corrected against the owner's pictures). The geometry is made once per sword model in the handle's own frame and baked into the model's root space, as every weapon's
/// meshes are. A faint blue crackle (<see cref="ClawCrackle"/>) plays along the talons while a claw strike is shown.
/// </summary>
public static class ClawModel
{
    /// <summary>Metres (the model's own scale): the forearm's length the talons are sized from, and the talons' share of it (the owner: longer, bigger).</summary>
    public const float Forearm = 0.30f, TalonShare = 1.9f;

    /// <summary>Which side of the grip's blade axis the talons curl to: +1 or -1 along the sword's thin axis (a flip for the look; the first still decides it).</summary>
    public const float CurlSide = 1f;

    /// <summary>Test aid <c>--claw-flip</c>: the talons curl the other way.</summary>
    static readonly bool Flip = Array.IndexOf(OS.GetCmdlineUserArgs(), "--claw-flip") >= 0;

    static readonly Dictionary<WeaponAsset, WeaponAsset> Cache = new();

    /// <summary>Whether the claw's mesh was made on this sword model already (the forearm's direction, which only the rig's rest pose decides, is read once with it).</summary>
    public static bool Known(WeaponAsset sword) => Cache.ContainsKey(sword);

    /// <summary>The claw's asset on the grip of <paramref name="sword"/> (the power sword's held attachment and its asset); null when the sword's mesh cannot be measured.</summary>
    public static WeaponAsset? For(WeaponAsset sword, WeaponAttachment attachment, Num.Vector3? forearmRoot = null)
    {
        if (Cache.TryGetValue(sword, out var known)) return known;
        var built = Build(sword, attachment, forearmRoot);
        if (built is not null) Cache[sword] = built;
        return built;
    }

    static WeaponAsset? Build(WeaponAsset sword, WeaponAttachment attachment, Num.Vector3? forearmRoot)
    {
        if (sword.Mesh is not { } swordMesh || attachment.Wield is not { } wield || !sword.Model.Tree.Contains(wield.HandleNode)) return null;
        var handle = sword.Model.Tree.ModelSpace(wield.HandleNode);
        if (!Num.Matrix4x4.Invert(handle, out var inverse)) return null;
        // The sword's extent in the grip's own frame: the longest axis is the blade's, then the width, then the thickness.
        var low = new Num.Vector3(float.MaxValue); var high = new Num.Vector3(float.MinValue);
        for (int s = 0; s < swordMesh.GetSurfaceCount(); s++)
            foreach (var v in swordMesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
            {
                var p = Num.Vector3.Transform(new Num.Vector3(v.X, v.Y, v.Z), inverse);
                low = Num.Vector3.Min(low, p); high = Num.Vector3.Max(high, p);
            }
        if (low.X > high.X) return null;
        var reach = new[] { Math.Max(Math.Abs(low.X), Math.Abs(high.X)), Math.Max(Math.Abs(low.Y), Math.Abs(high.Y)), Math.Max(Math.Abs(low.Z), Math.Abs(high.Z)) };
        var order = new[] { 0, 1, 2 }.OrderByDescending(a => high[a] - low[a]).ToArray();
        int bladeAxis = order[0], flatAxis = order[1], thinAxis = order[2];
        float bladeSign = Math.Abs(high[bladeAxis]) >= Math.Abs(low[bladeAxis]) ? 1f : -1f;
        Num.Vector3 Axis(int a, float sign) => a switch { 0 => Num.Vector3.UnitX * sign, 1 => Num.Vector3.UnitY * sign, _ => Num.Vector3.UnitZ * sign };
        var b = Axis(bladeAxis, bladeSign);   // toward the fingers (the sword's blade, until the forearm is known)
        var w = Axis(flatAxis, 1f);           // across the hand
        var c = Axis(thinAxis, CurlSide);     // the way the talons curl
        if (forearmRoot is { } fa && fa.LengthSquared() > 1e-8f)
        {
            // The forearm's own direction (elbow to hand) in the grip's frame: the gauntlet wears it and the talons continue it.
            b = Num.Vector3.Normalize(Num.Vector3.TransformNormal(fa, inverse));
            var thin = Axis(thinAxis, CurlSide);
            c = thin - Num.Vector3.Dot(thin, b) * b;
            if (c.LengthSquared() < 1e-4f) { thin = Axis(flatAxis, 1f); c = thin - Num.Vector3.Dot(thin, b) * b; }
            c = Num.Vector3.Normalize(c) * (Flip ? -1f : 1f);
            w = Num.Vector3.Normalize(Num.Vector3.Cross(b, c));
        }
        GD.Print($"lightning claw: sword grip frame blade axis {bladeAxis}{(bladeSign > 0 ? "+" : "-")}, flat {flatAxis}, thin {thinAxis}; extent {high - low}");

        var steel = Standard(new Color(0.70f, 0.73f, 0.80f), 0.8f, 0.30f);
        var dark = Standard(new Color(0.26f, 0.28f, 0.33f), 0.6f, 0.4f);
        var brass = Standard(new Color(1.0f, 0.74f, 0.26f), 0.7f, 0.3f);
        var parts = new List<(Material Material, SurfaceTool Tool)>();
        SurfaceTool Tool(Material m)
        {
            var existing = parts.FirstOrDefault(p => p.Material == m);
            if (existing.Tool is not null) return existing.Tool;
            var t = new SurfaceTool();
            t.Begin(Mesh.PrimitiveType.Triangles);
            parts.Add((m, t));
            return t;
        }
        // local (x along the fingers, y across, z the curl side) -> the model's root space
        Vector3 At(float x, float y, float z)
        {
            var g = b * x + w * y + c * z;
            var r = Num.Vector3.Transform(g, handle);
            return new Vector3(r.X, r.Y, r.Z);
        }
        void Tri(SurfaceTool t, Vector3 p, Vector3 q, Vector3 r) { t.AddVertex(p); t.AddVertex(q); t.AddVertex(r); }
        void Quad(SurfaceTool t, Vector3 p, Vector3 q, Vector3 r, Vector3 s) { Tri(t, p, q, r); Tri(t, p, r, s); }
        void Box(SurfaceTool t, float x0, float x1, float y0, float y1, float z0, float z1)
        {
            var p = new Vector3[8];
            for (int i = 0; i < 8; i++) p[i] = At((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
            Quad(t, p[0], p[1], p[3], p[2]); Quad(t, p[4], p[6], p[7], p[5]);
            Quad(t, p[0], p[4], p[5], p[1]); Quad(t, p[2], p[3], p[7], p[6]);
            Quad(t, p[0], p[2], p[6], p[4]); Quad(t, p[1], p[5], p[7], p[3]);
        }
        // a tube along x: radii r0 at x0 and r1 at x1, centred on (y, z)
        void Tube(SurfaceTool t, float x0, float x1, float r0, float r1, float y, float z, int sides = 12)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
                Quad(t, At(x0, y + MathF.Cos(a0) * r0, z + MathF.Sin(a0) * r0), At(x1, y + MathF.Cos(a0) * r1, z + MathF.Sin(a0) * r1),
                    At(x1, y + MathF.Cos(a1) * r1, z + MathF.Sin(a1) * r1), At(x0, y + MathF.Cos(a1) * r0, z + MathF.Sin(a1) * r0));
                Tri(t, At(x0, y, z), At(x0, y + MathF.Cos(a1) * r0, z + MathF.Sin(a1) * r0), At(x0, y + MathF.Cos(a0) * r0, z + MathF.Sin(a0) * r0));
                Tri(t, At(x1, y, z), At(x1, y + MathF.Cos(a0) * r1, z + MathF.Sin(a0) * r1), At(x1, y + MathF.Cos(a1) * r1, z + MathF.Sin(a1) * r1));
            }
        }

        // The gauntlet wears the armour's forearm (the axis x runs along it, the hand at 0): a cuff over the lower forearm that is a little wider than the armour's bracer, a knuckle block over the
        // hand, three brass bands and two pistons on the back of the hand.
        Tube(Tool(steel), -0.30f, -0.03f, 0.100f, 0.082f, 0f, 0f, 16);
        Tube(Tool(dark), -0.27f, -0.05f, 0.090f, 0.072f, 0f, 0f, 16);
        Box(Tool(steel), -0.06f, 0.11f, -0.075f, 0.075f, -0.058f, 0.068f);
        Box(Tool(dark), -0.02f, 0.13f, -0.068f, 0.068f, -0.046f, -0.038f);
        foreach (float x in new[] { -0.28f, -0.17f, -0.07f })
            Tube(Tool(brass), x, x + 0.034f, 0.104f - 0.12f * (x + 0.28f) * 0.5f, 0.104f - 0.12f * (x + 0.28f) * 0.5f, 0f, 0f, 18);
        foreach (float y in new[] { -0.042f, 0.042f })
        {
            Tube(Tool(brass), -0.22f, -0.02f, 0.014f, 0.014f, y, 0.100f, 8);
            Tube(Tool(steel), -0.02f, 0.10f, 0.022f, 0.022f, y, 0.092f, 8);
        }
        Box(Tool(brass), -0.04f, 0.04f, -0.06f, 0.06f, 0.066f, 0.082f);

        // Four long curved steel talons from the knuckles, each over the forearm's length (the owner: "longer, bigger"): a broad flat blade whose width and thickness taper to a point, curling toward the thin axis.
        float length = Forearm * TalonShare;
        const int segments = 12;
        float[] rows = [-0.066f, -0.022f, 0.022f, 0.066f];
        float Curl(float t) => 0.012f - 0.26f * t * t;
        foreach (float y in rows)
        {
            var tool = Tool(steel);
            var centres = new Vector3[segments + 1];
            var sideDir = new Vector3[segments + 1];
            var thickDir = new Vector3[segments + 1];
            float spread = y * 0.5f;   // the outer talons splay a little
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                centres[i] = At(0.12f + length * t, y + spread * t, Curl(t));
                float dx = length, dz = -0.52f * t;
                var tangent = (b * dx + c * dz) / MathF.Sqrt(dx * dx + dz * dz);
                var tan = new Vector3(tangent.X, tangent.Y, tangent.Z);
                var side = Num.Vector3.TransformNormal(w, handle); var sideV = new Vector3(side.X, side.Y, side.Z).Normalized();
                sideDir[i] = sideV;
                thickDir[i] = sideV.Cross(tan).Normalized();
            }
            for (int i = 0; i < segments; i++)
            {
                float t0 = i / (float)segments, t1 = (i + 1) / (float)segments;
                float w0 = 0.021f * MathF.Pow(1f - t0, 0.7f) + 0.0010f, w1 = 0.021f * MathF.Pow(1f - t1, 0.7f) + 0.0004f;
                float h0 = 0.0085f * (1f - t0) + 0.0008f, h1 = 0.0085f * (1f - t1) + 0.0003f;
                Vector3 A(int k, float ws, float hs, float cw, float ch) => centres[k] + sideDir[k] * (ws * cw) + thickDir[k] * (hs * ch);
                var s0 = new[] { A(i, w0, h0, 1, 0), A(i, w0, h0, 0, 1), A(i, w0, h0, -1, 0), A(i, w0, h0, 0, -1) };
                var s1 = new[] { A(i + 1, w1, h1, 1, 0), A(i + 1, w1, h1, 0, 1), A(i + 1, w1, h1, -1, 0), A(i + 1, w1, h1, 0, -1) };
                for (int k = 0; k < 4; k++) Quad(tool, s0[k], s1[k], s1[(k + 1) % 4], s0[(k + 1) % 4]);
            }
        }

        // The glow: along each talon a soft blue sheen that is always faintly there and brightens with a jagged crackle while a claw strike is on (ClawCrackle), in two planes.
        var crackle = new SurfaceTool();
        crackle.Begin(Mesh.PrimitiveType.Triangles);
        foreach (float y in rows)
            for (int plane = 0; plane < 2; plane++)
            {
                const float span = 0.032f;
                float ye = y + y * 0.5f, ze = Curl(1f);
                var p0 = plane == 0 ? At(0.12f, y - span, Curl(0f)) : At(0.12f, y, Curl(0f) - span);
                var p1 = plane == 0 ? At(0.12f, y + span, Curl(0f)) : At(0.12f, y, Curl(0f) + span);
                var p2 = plane == 0 ? At(0.12f + length, ye + span, ze) : At(0.12f + length, ye, ze + span);
                var p3 = plane == 0 ? At(0.12f + length, ye - span, ze) : At(0.12f + length, ye, ze - span);
                crackle.SetUV(new Vector2(0f, 0f)); crackle.AddVertex(p0); crackle.SetUV(new Vector2(0f, 1f)); crackle.AddVertex(p1); crackle.SetUV(new Vector2(1f, 1f)); crackle.AddVertex(p2);
                crackle.SetUV(new Vector2(0f, 0f)); crackle.AddVertex(p0); crackle.SetUV(new Vector2(1f, 1f)); crackle.AddVertex(p2); crackle.SetUV(new Vector2(1f, 0f)); crackle.AddVertex(p3);
            }

        var mesh = new ArrayMesh();
        int triangles = 0;
        foreach (var (material, tool) in parts)
        {
            tool.GenerateNormals();
            tool.SetMaterial(material);
            tool.Commit(mesh);
        }
        if (ClawCrackle.Demo) ClawCrackle.Ensure();
        crackle.SetMaterial(ClawCrackle.Material);
        crackle.Commit(mesh);
        for (int s = 0; s < mesh.GetSurfaceCount(); s++) triangles += mesh.SurfaceGetArrayLen(s) / 3;
        var model = sword.Model;
        return new WeaponAsset(model, mesh, new Dictionary<string, ArrayMesh>(), triangles, parts.Count, 0, 0, null);
    }

    static StandardMaterial3D Standard(Color albedo, float metallic, float roughness) =>
        new() { AlbedoColor = albedo, Metallic = metallic, Roughness = roughness, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
}
