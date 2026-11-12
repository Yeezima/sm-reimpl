using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Godot;

namespace SmClient;

/// <summary>
/// The claw's housing wears the marine's own gauntlet armour (owner, 2026-11-12: "match the space marine armor in game and ensure it integrates and appears as one with the hand armor gauntlet of the player
/// model"). At run time, from the wearer's own look (<see cref="MarineAssets.Pieces"/>): the right arm's armour surface is a painted material (<see cref="MapMaterials.MakePainted"/>: the install's diffuse, paint
/// mask, normal, gloss and light-property textures, the player's right-arm colours as uniforms). This makes a copy of that material whose shader is the same code with the UV read through a window
/// (<c>uv_window</c>: the middle of the arm surface's own UV island, mirrored so it never leaves it) and a <c>darken</c> factor, shares every texture and colour with the source, and so the housing's boxes are
/// drawn with the same albedo, paint, dirt, normal and roughness as the gauntlet it sits on, in the same customizer colours. Nothing is copied from the install into the repository or the cache; the copies live
/// as long as the source material. Where no arm piece is found (the placeholder look, a single-set path) the housing keeps <see cref="ClawPowerField.HousingPlate"/>'s procedural worn grey.
/// </summary>
public static class ClawArmourLook
{
    /// <summary>The window's half-size, as a share of the arm surface's UV extent (ours: the housing is about as big as the bracer, the texture is laid at a little less than the armour's own density).</summary>
    const float WindowShare = 0.32f;

    /// <summary>How much darker the housing's inner parts (the wrist section, the side guards, the knuckle bar) are than its plate (ours).</summary>
    const float DarkShare = 0.75f;

    public sealed record Look(ShaderMaterial Plate, ShaderMaterial Dark, string Source);

    static readonly ConditionalWeakTable<ShaderMaterial, Look> Looks = new();
    static readonly Dictionary<string, Shader> Shaders = new(StringComparer.Ordinal);
    static readonly HashSet<ShaderMaterial> Failed = new();
    static bool _logged;

    /// <summary>The housing's materials for a wearer, or null when the look has no right-arm armour with a painted surface.</summary>
    public static Look? For(MarineAssets? assets)
    {
        if (assets is null) return null;
        AssetPiece? arm = null;
        foreach (var piece in assets.Pieces)
            if (piece.Name.EndsWith("_armr", StringComparison.OrdinalIgnoreCase)) { arm = piece; break; }
        if (arm is null) return null;
        int best = -1, bestCount = 0;
        for (int i = 0; i < arm.Mesh.GetSurfaceCount() && i < arm.Overrides.Count; i++)
            if (arm.Overrides[i] is ShaderMaterial && arm.Mesh.SurfaceGetArrayLen(i) > bestCount) { best = i; bestCount = arm.Mesh.SurfaceGetArrayLen(i); }
        if (best < 0 || arm.Overrides[best] is not ShaderMaterial source || Failed.Contains(source)) return null;
        if (Looks.TryGetValue(source, out var known)) return known;
        var look = Make(arm, best, source);
        if (look is null) { Failed.Add(source); return null; }
        Looks.Add(source, look);
        if (!_logged) { _logged = true; GD.Print($"claw armour look: housing wears {arm.Name} surface {best} ({source.Shader?.GetShaderUniformList().Count} uniforms), window {look.Plate.GetShaderParameter(ClawPowerField.UvWindow)}"); }
        return look;
    }

    static Look? Make(AssetPiece arm, int surface, ShaderMaterial source)
    {
        if (source.Shader is not { } shader) return null;
        string code = shader.Code;
        int fragment = code.IndexOf("void fragment() {", StringComparison.Ordinal);
        if (fragment < 0 || !code.Contains("ALBEDO = albedo;", StringComparison.Ordinal)) return null;
        // The middle of the arm surface's UV island, from its vertices (the median and the 10th to 90th percentile).
        var arrays = arm.Mesh.SurfaceGetArrays(surface);
        if (arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array() is not { Length: > 3 } uvs) return null;
        var us = new float[uvs.Length]; var vs = new float[uvs.Length];
        for (int i = 0; i < uvs.Length; i++) { us[i] = uvs[i].X; vs[i] = uvs[i].Y; }
        Array.Sort(us); Array.Sort(vs);
        float Pct(float[] a, float p) => a[Math.Clamp((int)(p * (a.Length - 1)), 0, a.Length - 1)];
        var window = new Vector4(Pct(us, 0.5f), Pct(vs, 0.5f), MathF.Max(0.004f, (Pct(us, 0.9f) - Pct(us, 0.1f)) * WindowShare), MathF.Max(0.004f, (Pct(vs, 0.9f) - Pct(vs, 0.1f)) * WindowShare));
        string body = code[fragment..];
        body = Regex.Replace(body, @"\bUV\b", "UVW");   // UV2 (the pad decals) is left alone
        body = body.Replace("ALBEDO = albedo;", "ALBEDO = albedo * darken;", StringComparison.Ordinal);
        body = body.Replace("void fragment() {", "void fragment() {\n    vec2 UVW = uv_window.xy + vec2(tri(UV.x), tri(UV.y)) * uv_window.zw;", StringComparison.Ordinal);
        string patched = code[..fragment] + "uniform vec4 uv_window = vec4(0.5, 0.5, 0.1, 0.1);\nuniform float darken = 1.0;\nfloat tri(float x) { return abs(fract(x * 0.5) * 2.0 - 1.0) * 2.0 - 1.0; }\n" + body;
        if (!Shaders.TryGetValue(patched, out var windowed)) Shaders[patched] = windowed = new Shader { Code = patched };
        return new Look(Copy(source, windowed, window, 1f), Copy(source, windowed, window, DarkShare), arm.Name);
    }

    static ShaderMaterial Copy(ShaderMaterial source, Shader shader, Vector4 window, float darken)
    {
        var m = new ShaderMaterial { Shader = shader, RenderPriority = source.RenderPriority };
        foreach (var info in source.Shader!.GetShaderUniformList())
        {
            var name = info.AsGodotDictionary()["name"].AsStringName();
            var value = source.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil) m.SetShaderParameter(name, value);
        }
        m.SetShaderParameter(ClawPowerField.UvWindow, window);
        m.SetShaderParameter(ClawPowerField.Darken, darken);
        return m;
    }
}
