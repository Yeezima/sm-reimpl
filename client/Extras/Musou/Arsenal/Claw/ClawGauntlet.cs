using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The lightning claw's frame on the marine's hand (owner, 2026-11-12: "the claws simply extend out from the players hand gauntlet armor ... it is not a glove that is held in the hand ... part of his hand,
/// simply blades extending out of the gauntlet"): the hand bone's own axes read from the rig's rest pose, in the hand bone's space, so a model placed in it is locked to the hand in every pose. The fingers'
/// direction is the mean of the index and ring roots (as the preview stage's stand-in reads it); the back of the hand the side the fingers do not curl toward.
/// </summary>
/// <param name="Bone">The hand bone's name.</param>
/// <param name="Along">Unit, toward the fingertips.</param>
/// <param name="Back">Unit, out of the back of the hand.</param>
/// <param name="Across">Unit, across the hand (Back x Along).</param>
/// <param name="Knuckles">The knuckle line's place: the mean of the finger roots.</param>
/// <param name="Forearm">The elbow-to-hand distance, metres (the rig's rest pose).</param>
public sealed record ClawHandFrame(string Bone, Vector3 Along, Vector3 Back, Vector3 Across, Vector3 Knuckles, float Forearm)
{
    static bool _warned;

    /// <summary>The frame from the rig's rest pose; null when the hand or its finger roots are not found (the claw then keeps the held mount).</summary>
    public static ClawHandFrame? Read(Skeleton3D s)
    {
        int hand = s.FindBone("RIG_R_Hand"), fore = s.FindBone("RIG_R_Forearm"), f1 = s.FindBone("RIG_R_Finger1"), f2 = s.FindBone("RIG_R_Finger2");
        if (hand < 0 || f1 < 0 || f2 < 0) return null;
        var roots = new List<Vector3>();
        var names = new List<string>();
        foreach (int child in s.GetBoneChildren(hand))
        {
            string n = s.GetBoneName(child);
            if (n.StartsWith("RIG_R_Finger", StringComparison.Ordinal) && n.Length == "RIG_R_Finger".Length + 1 && n[^1] != '0') { roots.Add(s.GetBoneRest(child).Origin); names.Add(n); }   // Finger0 is the thumb
        }
        var i1 = s.GetBoneRest(f1).Origin; var i2 = s.GetBoneRest(f2).Origin;
        var along = ((i1 + i2) * 0.5f).Normalized();
        var across = i1 - i2; across = (across - along * across.Dot(along)).Normalized();
        var back = across.Cross(along).Normalized();   // provisional; the curl decides the sign
        var curl = Vector3.Zero;
        foreach (var (root, tip) in new[] { (f1, s.FindBone("RIG_R_Finger11")), (f2, s.FindBone("RIG_R_Finger21")) })
        {
            if (tip < 0) continue;
            var rootRest = s.GetBoneRest(root);
            var run = rootRest.Origin.Normalized();
            var d = (rootRest.Basis * s.GetBoneRest(tip).Origin).Normalized();
            curl += d - run * d.Dot(run);
        }
        // The palm is the side the fingertips curl toward; the back is the other. Without a readable curl the sign is a guess: the claw keeps the old held model instead (logged once).
        if (curl.LengthSquared() <= 1e-6f) { if (!_warned) { _warned = true; GD.Print("claw gauntlet: the finger curl cannot be read (RIG_R_Finger11 / Finger21 missing); the old held model is used"); } return null; }
        if (back.Dot(curl) > 0f) back = -back;
        across = along.Cross(back).Normalized();   // right-handed: Across x Back = Along ... kept consistent below
        var knuckles = roots.Count > 0 ? roots.Aggregate(Vector3.Zero, (a, r) => a + r) / roots.Count : (i1 + i2) * 0.5f;
        float forearm = fore >= 0 ? MathF.Max(0.2f, s.GetBoneRest(hand).Origin.Length()) : 0.3f;
        if (!_warned) GD.Print($"claw gauntlet: hand frame along {along} back {back} across {across}; finger roots {string.Join(", ", names.Zip(roots, (n, r) => $"{n} {r}"))}; knuckles {knuckles}; hand rest origin {s.GetBoneRest(hand).Origin}");
        return new ClawHandFrame(s.GetBoneName(hand), along, back, across, knuckles, forearm);
    }
}

/// <summary>
/// The lightning claw's gauntlet (owner, 2026-11-12: "change the model to match this reference as well: the current power claw model is not good enough"; docs/claw-spear-animations.md "The claw's gauntlet"):
/// an ORIGINAL model built in code, in the hand bone's own space (<see cref="ClawHandFrame"/>), drawn as the weapon rig's held item on the hand bone with no offset, so it never leaves the hand. A back-of-hand
/// housing (a curved dark armour plate in two stepped sections, a transverse knuckle bar, side guards and a wrist power unit: a vented dome in a brass ring), four brass sockets over the knuckles each with a
/// coiled piston behind it and a flared collar, and four long, slightly curved blades fixed in the sockets and running straight out along the line of the fingers and well past them, tapering to points.
/// Dark metal housing, brass sockets, steel blades; the power field's crackle (<see cref="ClawCrackle"/>) lies along each blade. Every size is **ours** (the references are pictures; nothing here is measured from them or
/// copied from them): <see cref="BladeLength"/> 0.62 m from the knuckle line (about twice the 0.31 m forearm; past the knee at rest), the blades 0.038 m wide at the root. Restore the old held model: <c>--old-claw-model</c>.
/// </summary>
public static class ClawGauntlet
{
    /// <summary>Test aid <c>--old-claw-model</c>: the claw's old model (the held gauntlet of <see cref="ClawModel"/>, on the sword's grip) as before 2026-11-12.</summary>
    public static readonly bool Old = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-claw-model") >= 0;

    /// <summary>Metres, ours (state it in the refine register): the blades' length from the knuckle line to the tips.</summary>
    public const float BladeLength = 0.62f;

    /// <summary>Metres, ours: how high above the finger roots' plane the back of the hand's armour plate lies, and the blades' height over it.</summary>
    public const float PlateHeight = 0.050f, BladeRise = 0.050f;

    /// <summary>Metres, ours: how far the blades' tips bend toward the palm over their length.</summary>
    public const float Bend = 0.10f;

    static readonly Dictionary<WeaponAsset, WeaponAsset> Cache = new();

    /// <summary>The attachment of the gauntlet: the sword's, put on the hand bone with no offset (the model is in the hand bone's own space).</summary>
    public static WeaponAttachment OnHand(WeaponAttachment sword, string wargear, ClawHandFrame frame) =>
        sword with { Wargear = wargear, Name = "lightning_claw", HideOnStow = true, Wield = sword.Wield! with { Bone = frame.Bone, BodyLocal = Num.Matrix4x4.Identity, Scale = 1f, ModelAxes = false } };

    /// <summary>The gauntlet's asset, meshes in the sword model's root space so that the rig's mount (the sword's handle, inverted) puts them in the hand bone's space; null when the sword's handle is unusable.</summary>
    public static WeaponAsset? For(WeaponAsset sword, WeaponAttachment attachment, ClawHandFrame frame)
    {
        if (Cache.TryGetValue(sword, out var known)) return known;
        var built = Build(sword, attachment, frame);
        if (built is not null) Cache[sword] = built;
        return built;
    }

    static WeaponAsset? Build(WeaponAsset sword, WeaponAttachment attachment, ClawHandFrame frame)
    {
        if (attachment.Wield is not { } wield || !sword.Model.Tree.Contains(wield.HandleNode)) return null;
        var handle = sword.Model.Tree.ModelSpace(wield.HandleNode);
        if (!Num.Matrix4x4.Invert(handle, out _)) return null;

        var dark = Standard(new Color(0.27f, 0.30f, 0.34f), 0.7f, 0.42f);
        var plate = Standard(new Color(0.38f, 0.42f, 0.47f), 0.65f, 0.38f);
        var brass = Standard(new Color(0.86f, 0.62f, 0.22f), 0.85f, 0.30f);
        var steel = Standard(new Color(0.58f, 0.61f, 0.68f), 0.8f, 0.30f);
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

        // Local frame: x along the fingers from the knuckle line, y across, z out of the back of the hand from the finger roots' plane; then into the hand bone's space; then (as every weapon's meshes are) the model's root.
        float kx = frame.Knuckles.Dot(frame.Along), ky = frame.Knuckles.Dot(frame.Across), kz = frame.Knuckles.Dot(frame.Back);
        Vector3 At(float x, float y, float z)
        {
            var g = frame.Along * (kx + x) + frame.Across * (ky + y) + frame.Back * (kz + z);
            var r = Num.Vector3.Transform(new Num.Vector3(g.X, g.Y, g.Z), handle);
            return new Vector3(r.X, r.Y, r.Z);
        }
        Vector3 L(Vector3 p) => At(p.X, p.Y, p.Z);
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
        // A cone or cylinder from a to b (local coordinates), radii r0 at a and r1 at b, capped at both ends.
        void Cyl(SurfaceTool t, Vector3 a, Vector3 b, float r0, float r1, int sides = 14)
        {
            var d = (b - a).Normalized();
            var u = MathF.Abs(d.Z) < 0.9f ? d.Cross(Vector3.Back).Normalized() : d.Cross(Vector3.Right).Normalized();
            var v = d.Cross(u);
            Vector3 Ring(Vector3 c, float r, int k) { float an = k * MathF.Tau / sides; return c + (u * MathF.Cos(an) + v * MathF.Sin(an)) * r; }
            for (int i = 0; i < sides; i++)
            {
                Quad(t, L(Ring(a, r0, i)), L(Ring(b, r1, i)), L(Ring(b, r1, i + 1)), L(Ring(a, r0, i + 1)));
                Tri(t, L(a), L(Ring(a, r0, i + 1)), L(Ring(a, r0, i)));
                Tri(t, L(b), L(Ring(b, r1, i)), L(Ring(b, r1, i + 1)));
            }
        }

        const float hw = 0.100f;   // half the plate's width at the knuckles
        float baseZ = PlateHeight;
        float[] rows = [-0.069f, -0.023f, 0.023f, 0.069f];

        // The housing's plate: an arched armour shell over the back of the hand, in two stepped sections (the wrist section a little lower, the knuckle section lapping it), the arch following the back's curve.
        void Shell(SurfaceTool t, float x0, float x1, float w0, float w1, float lift)
        {
            const int xs = 5, ys = 12;
            Vector3 Top(float x, float y, float w) { float u = Math.Clamp(y / w, -1f, 1f); return new Vector3(x, y * 0.84f, baseZ + lift + 0.024f + 0.030f * (1f - u * u)); }
            Vector3 Bot(float x, float y, float w) { float u = Math.Clamp(y / w, -1f, 1f); return new Vector3(x, y, baseZ + lift - 0.006f + 0.030f * (1f - u * u)); }
            for (int i = 0; i < xs; i++)
            {
                float xa = x0 + (x1 - x0) * i / xs, xb = x0 + (x1 - x0) * (i + 1) / xs;
                float wa = w0 + (w1 - w0) * i / xs, wb = w0 + (w1 - w0) * (i + 1) / xs;
                for (int j = 0; j < ys; j++)
                {
                    float ta = -1f + 2f * j / ys, tb = -1f + 2f * (j + 1) / ys;
                    Quad(t, L(Top(xa, ta * wa, wa)), L(Top(xb, ta * wb, wb)), L(Top(xb, tb * wb, wb)), L(Top(xa, tb * wa, wa)));
                    Quad(t, L(Bot(xa, ta * wa, wa)), L(Bot(xa, tb * wa, wa)), L(Bot(xb, tb * wb, wb)), L(Bot(xb, ta * wb, wb)));
                }
            }
            for (int j = 0; j < ys; j++)   // the front and rear edges
            {
                float ta = -1f + 2f * j / ys, tb = -1f + 2f * (j + 1) / ys;
                Quad(t, L(Top(x0, ta * w0, w0)), L(Top(x0, tb * w0, w0)), L(Bot(x0, tb * w0, w0)), L(Bot(x0, ta * w0, w0)));
                Quad(t, L(Top(x1, ta * w1, w1)), L(Bot(x1, ta * w1, w1)), L(Bot(x1, tb * w1, w1)), L(Top(x1, tb * w1, w1)));
            }
            for (int i = 0; i < xs; i++)   // the side edges
            {
                float xa = x0 + (x1 - x0) * i / xs, xb = x0 + (x1 - x0) * (i + 1) / xs;
                float wa = w0 + (w1 - w0) * i / xs, wb = w0 + (w1 - w0) * (i + 1) / xs;
                foreach (float sgn in new[] { -1f, 1f })
                    Quad(t, L(Top(xa, sgn * wa, wa)), L(Top(xb, sgn * wb, wb)), L(Bot(xb, sgn * wb, wb)), L(Bot(xa, sgn * wa, wa)));
            }
        }
        Shell(Tool(dark), -0.235f, -0.090f, 0.088f, 0.096f, 0f);
        Shell(Tool(plate), -0.110f, 0.050f, 0.094f, hw, 0.012f);

        // Side guards (the housing's flanks, down over the hand's edges) with brass studs.
        foreach (float sgn in new[] { -1f, 1f })
        {
            Box(Tool(dark), -0.13f, 0.035f, sgn > 0 ? hw - 0.004f : -hw - 0.010f, sgn > 0 ? hw + 0.010f : -hw + 0.004f, baseZ - 0.045f, baseZ + 0.020f);
            foreach (float x in new[] { -0.095f, -0.035f, 0.015f })
                Cyl(Tool(brass), new Vector3(x, sgn * (hw + 0.009f), baseZ - 0.012f), new Vector3(x, sgn * (hw + 0.017f), baseZ - 0.012f), 0.007f, 0.007f, 8);
        }

        // The knuckle bar across the four sockets, brass end caps.
        float barZ = baseZ + 0.038f;
        Cyl(Tool(dark), new Vector3(-0.012f, -hw - 0.004f, barZ - 0.006f), new Vector3(-0.012f, hw + 0.004f, barZ - 0.006f), 0.011f, 0.011f, 10);
        foreach (float sgn in new[] { -1f, 1f })
            Cyl(Tool(brass), new Vector3(-0.012f, sgn * (hw + 0.004f), barZ - 0.006f), new Vector3(-0.012f, sgn * (hw + 0.014f), barZ - 0.006f), 0.0135f, 0.0135f, 10);

        // The four sockets over the knuckles, each a brass barrel with a flared collar the blade comes out of, and a coiled piston behind it.
        float rootZ = baseZ + BladeRise;
        foreach (float y in rows)
        {
            Cyl(Tool(brass), new Vector3(-0.050f, y, rootZ), new Vector3(0.040f, y, rootZ), 0.021f, 0.021f, 14);
            Cyl(Tool(brass), new Vector3(0.040f, y, rootZ), new Vector3(0.066f, y, rootZ), 0.027f, 0.018f, 14);   // the flared collar
            Cyl(Tool(dark), new Vector3(0.066f, y, rootZ), new Vector3(0.071f, y, rootZ), 0.018f, 0.018f, 14);
            Cyl(Tool(brass), new Vector3(-0.052f, y, rootZ), new Vector3(-0.044f, y, rootZ), 0.025f, 0.025f, 14);   // the barrel's rear ring
            Box(Tool(brass), -0.030f, 0.020f, y - 0.023f, y + 0.023f, baseZ + 0.016f, rootZ - 0.008f);   // the barrel's foot on the shell
            // the piston: a rod behind the barrel with a coil of rings
            Cyl(Tool(steel), new Vector3(-0.100f, y, rootZ), new Vector3(-0.052f, y, rootZ), 0.007f, 0.007f, 8);
            for (int k = 0; k < 5; k++)
            {
                float x = -0.098f + k * 0.0095f;
                Cyl(Tool(k % 2 == 0 ? dark : steel), new Vector3(x, y, rootZ), new Vector3(x + 0.0085f, y, rootZ), 0.0135f, 0.0135f, 14);
            }
            Cyl(Tool(brass), new Vector3(-0.108f, y, rootZ), new Vector3(-0.098f, y, rootZ), 0.017f, 0.017f, 10);   // the piston's rear cap
        }

        // The wrist power unit: a vented dome in a brass ring on the plate's wrist end.
        float domeX = -0.178f, domeZ = baseZ + 0.028f;
        Cyl(Tool(brass), new Vector3(domeX, 0f, domeZ - 0.004f), new Vector3(domeX, 0f, domeZ + 0.010f), 0.072f, 0.072f, 20);
        Cyl(Tool(dark), new Vector3(domeX, 0f, domeZ + 0.010f), new Vector3(domeX, 0f, domeZ + 0.040f), 0.064f, 0.056f, 20);
        Cyl(Tool(plate), new Vector3(domeX, 0f, domeZ + 0.040f), new Vector3(domeX, 0f, domeZ + 0.070f), 0.056f, 0.030f, 20);
        Cyl(Tool(brass), new Vector3(domeX, 0f, domeZ + 0.070f), new Vector3(domeX, 0f, domeZ + 0.078f), 0.030f, 0.030f, 12);
        for (int k = 0; k < 8; k++)   // the vents
        {
            float an = k * MathF.Tau / 8f + 0.2f;
            var c = new Vector3(domeX + MathF.Cos(an) * 0.060f, MathF.Sin(an) * 0.060f, domeZ + 0.026f);
            var o = new Vector3(MathF.Cos(an), MathF.Sin(an), 0f) * 0.006f;
            Cyl(Tool(brass), c, c + o, 0.0042f, 0.0042f, 6);
        }
        // the wrist clamp at the rear of the shell: a dark band, and a brass bracer ring around the forearm's top
        Box(Tool(dark), -0.265f, -0.225f, -0.098f, 0.098f, baseZ - 0.015f, baseZ + 0.040f);
        Box(Tool(brass), -0.250f, -0.240f, -0.100f, 0.100f, baseZ - 0.017f, baseZ + 0.042f);

        // Four long, slightly curved steel blades fixed in the sockets: a flat diamond section tapering to a point, parallel along the fingers and curving a little toward the palm.
        const int segments = 14;
        float length = BladeLength;
        float BladeZ(float t) => rootZ - Bend * t * t;
        var crackle = new SurfaceTool();
        crackle.Begin(Mesh.PrimitiveType.Triangles);
        foreach (float y in rows)
        {
            var tool = Tool(steel);
            var centres = new Vector3[segments + 1];
            var thick = new Vector3[segments + 1];
            var side = L(new Vector3(0f, 1f, 0f)) - L(Vector3.Zero);
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float x = -0.020f + (length + 0.020f) * t;
                centres[i] = At(x, y * (1f + 0.30f * t), BladeZ(t));
                var ahead = At(x + 0.01f, y * (1f + 0.30f * (t + 0.01f / length)), BladeZ(MathF.Min(1f, t + 0.01f / length)));
                var tan = (ahead - centres[i]).Normalized();
                thick[i] = side.Normalized().Cross(tan).Normalized();
            }
            var sideDir = side.Normalized();
            for (int i = 0; i < segments; i++)
            {
                float t0 = i / (float)segments, t1 = (i + 1) / (float)segments;
                float W(float t) => 0.019f * (1f - 0.25f * t) * MathF.Pow(MathF.Min(1f, (1f - t) / 0.26f), 0.8f) + 0.0004f;
                float H(float t) => 0.0042f * (1f - 0.45f * t) * MathF.Min(1f, (1f - t) / 0.2f) + 0.0003f;
                Vector3 A(int k, float ws, float hs, float cw, float ch) => centres[k] + sideDir * (ws * cw) + thick[k] * (hs * ch);
                // a flat blade: sharp edges at the sides, bevelled to a thin back (the section's six points)
                float[,] sec = { { 1f, 0f }, { 0.45f, 1f }, { -0.45f, 1f }, { -1f, 0f }, { -0.45f, -1f }, { 0.45f, -1f } };
                var s0 = new Vector3[6]; var s1 = new Vector3[6];
                for (int k = 0; k < 6; k++) { s0[k] = A(i, W(t0), H(t0), sec[k, 0], sec[k, 1]); s1[k] = A(i + 1, W(t1), H(t1), sec[k, 0], sec[k, 1]); }
                for (int k = 0; k < 6; k++) Quad(tool, s0[k], s1[k], s1[(k + 1) % 6], s0[(k + 1) % 6]);
            }
            // the glow: a soft sheen along this blade, in two planes
            const float span = 0.018f;
            for (int plane = 0; plane < 2; plane++)
            {
                var p0 = plane == 0 ? centres[0] - sideDir * span : centres[0] - thick[0] * span;
                var p1 = plane == 0 ? centres[0] + sideDir * span : centres[0] + thick[0] * span;
                const float tipSpan = span * 0.12f;   // the sheen narrows to the blade's point
                var p2 = plane == 0 ? centres[segments] + sideDir * tipSpan : centres[segments] + thick[segments] * tipSpan;
                var p3 = plane == 0 ? centres[segments] - sideDir * tipSpan : centres[segments] - thick[segments] * tipSpan;
                crackle.SetUV(new Vector2(0f, 0f)); crackle.AddVertex(p0); crackle.SetUV(new Vector2(0f, 1f)); crackle.AddVertex(p1); crackle.SetUV(new Vector2(1f, 1f)); crackle.AddVertex(p2);
                crackle.SetUV(new Vector2(0f, 0f)); crackle.AddVertex(p0); crackle.SetUV(new Vector2(1f, 1f)); crackle.AddVertex(p2); crackle.SetUV(new Vector2(1f, 0f)); crackle.AddVertex(p3);
            }
        }

        var mesh = new ArrayMesh();
        int triangles = 0;
        foreach (var (material, tool) in parts)
        {
            tool.GenerateNormals();
            tool.SetMaterial(material);
            tool.Commit(mesh);
        }
        if (ClawCrackle.Demo || ClawCrackle.GlowTest) ClawCrackle.Ensure();
        crackle.SetMaterial(ClawCrackle.Material);
        crackle.Commit(mesh);
        for (int s = 0; s < mesh.GetSurfaceCount(); s++) triangles += mesh.SurfaceGetArrayLen(s) / 3;
        GD.Print($"claw gauntlet: built, {triangles} triangles, {parts.Count} materials, blades {length:F2} m from the knuckle line");
        return new WeaponAsset(sword.Model, mesh, new Dictionary<string, ArrayMesh>(), triangles, parts.Count, 0, 0, null);
    }

    static StandardMaterial3D Standard(Color albedo, float metallic, float roughness) =>
        new() { AlbedoColor = albedo, Metallic = metallic, Roughness = roughness, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
}
