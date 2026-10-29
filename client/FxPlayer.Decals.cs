using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Fx;

namespace SmClient;

/// <summary>
/// Decals of the effect player (M25b, reworked in M38). The weapons' hit effects carry their own decal elements (<see cref="FxDecal"/>): a bolter hit on stone leaves a small
/// grey bullet crater, on metal a metal crater, the melta gun a large charred patch with a glow, the lascannon a blue-grey char with a red crater, a hole and a glow, the plasma gun
/// a char and an animated plasma glow, the plasma cannon (through <c>plasma_gun_charged_dmg</c> / <c>plasma_gun_mp_overcharged_dmg</c>) a crater with hot cracks. Sizes, textures,
/// durations and colour fades are the data's; how they are drawn is an interpretation.
/// <list type="bullet">
/// <item>Alpha decals (<c>AdditiveBlend</c> false: craters, char) are Godot <see cref="Decal"/> boxes tinted and faded by the element's colour curve.</item>
/// <item>Additive decals (glows, hot cracks, plasma) are flat unshaded additive quads a centimetre off the surface, tinted and faded per frame by the colour curve (a Godot decal's
/// emission cannot be tinted); a sprite sheet (<c>TileU</c> x <c>TileV</c>, the plasma's 4 x 8) plays its cells over the decal's duration (row by row, interpretation).</item>
/// <item>Decals project only on render layer 1 (<c>Decal.CullMask</c>): characters, their weapons and ragdolls are moved to layer 2 (<see cref="NetworkView"/> <c>SetUpDecalLayers</c>), so craters
/// do not paint players.</item>
/// <item>A decal is placed once and lives on its own clock: it outlives its effect and fades out over its duration; at most <see cref="MaxDecals"/> alpha decals and
/// <see cref="MaxGlowDecals"/> glows exist, and the oldest fade out over <see cref="EvictSeconds"/> instead of popping.</item>
/// </list>
/// </summary>
public sealed partial class FxPlayer
{
    /// <summary>How long a decal pushed out by newer ones takes to fade away (ours).</summary>
    const float EvictSeconds = 0.6f;

    /// <summary>Gap between a glow quad and the surface (m; ours).</summary>
    const float GlowLift = 0.012f;

    sealed class DecalRec
    {
        public required CDecal Element;
        public Decal? Node;
        public MeshInstance3D? Quad;
        public ShaderMaterial? Material;
        public Texture2D? Texture;
        public double Age;
        public float Duration;
        public bool Evicting;
        public double EvictAge;
        public int LastFrame = -1;
        /// <summary>The effect's tint (<see cref="FxEffect.Tint"/>; the Chaos plasma): an additive glow is recoloured to this hue at its own brightness; null: the texture's colours.</summary>
        public Color? Tint;
    }

    readonly List<DecalRec> _decalRecs = new();
    readonly Stack<Decal> _decalPool = new();
    readonly Stack<(MeshInstance3D Quad, ShaderMaterial Material)> _glowPool = new();
    Shader? _glowShader;

    /// <summary>Test aid (<c>--fx-decals</c>): draws only the decals of an effect (its particles, flashes and lights are skipped).</summary>
    public bool DecalsOnly { get; set; }

    Texture2D? DecalTexture(string name)
    {
        if (name.Length == 0) return null;
        if (_decalTextures.TryGetValue(name, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            if (_decalBytes.TryGetValue(name, out var dds))
            {
                var image = new Image();
                if (image.LoadDdsFromBuffer(dds) == Error.Ok)
                {
                    if (!image.HasMipmaps()) image.GenerateMipmaps();
                    tex = ImageTexture.CreateFromImage(image);
                }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { tex = null; }
        if (tex is null) _missing.Add("decal:" + name);
        _decalTextures[name] = tex;
        return tex;
    }

    void DrawDecal(FxEffect e, CDecal n, in Ctx c, double age, uint seed)
    {
        if (age < 0 || age > Math.Max(n.Duration, 0.1) + 0.5) return;
        e.PlacedDecals ??= new HashSet<CDecal>();
        if (!e.PlacedDecals.Add(n)) return;   // placed once, then on its own clock
        if (DecalTexture(n.Diffuse) is not { } tex) return;
        float scale = c.S * Jitter(n.EpsScale, seed, 14) * At(n.Scale, 0f, 1f);
        var basis = c.B * new Basis(Vector3.Up, R(seed, 15) * MathF.Tau);
        float w = MathF.Max(0.05f, n.Width * scale), h = MathF.Max(0.05f, n.Height * scale);
        var rec = new DecalRec { Element = n, Age = Math.Max(0.0, age), Duration = MathF.Max(n.Duration, 0.1f), Texture = tex, Tint = e.Tint };
        if (n.Additive)
        {
            while (_decalRecs.Count(r => r.Quad is not null && !r.Evicting) >= MaxGlowDecals) Evict(_decalRecs.First(r => r.Quad is not null && !r.Evicting));
            var (quad, material) = _glowPool.Count > 0 ? _glowPool.Pop() : NewGlow();
            material.SetShaderParameter("tex", tex);
            // A pooled quad may carry the last user's sprite-sheet crop: start from the whole texture (a sheet sets its cell in ApplyDecal).
            material.SetShaderParameter("cell_scale", new Vector2(1f, 1f));
            material.SetShaderParameter("cell_offset", new Vector2(0f, 0f));
            // A pooled quad may carry the last user's recolour: set it (alpha 0 = off) for every glow.
            material.SetShaderParameter("recolour", e.Tint is { } rc ? new Color(rc.R, rc.G, rc.B, 1f) : new Color(0f, 0f, 0f, 0f));
            quad.GlobalTransform = new Transform3D(basis * Basis.FromScale(new Vector3(w, 1f, h)), c.Pos + basis.Y.Normalized() * GlowLift);
            quad.Visible = true;
            rec.Quad = quad; rec.Material = material;
        }
        else
        {
            while (_decalRecs.Count(r => r.Node is not null && !r.Evicting) >= MaxDecals) Evict(_decalRecs.First(r => r.Node is not null && !r.Evicting));
            var node = _decalPool.Count > 0 ? _decalPool.Pop() : NewDecal();
            float depth = Math.Clamp(n.Depth * c.S, 0.3f, Math.Max(0.6f, 0.25f * MathF.Max(w, h)));
            node.Size = new Vector3(w, depth, h);
            node.GlobalTransform = new Transform3D(basis, c.Pos);
            node.TextureAlbedo = tex; node.TextureEmission = null; node.EmissionEnergy = 1f;
            node.Modulate = Colors.White;
            node.Visible = true;
            rec.Node = node;
        }
        _decalRecs.Add(rec);
        ApplyDecal(rec);
    }

    /// <summary>
    /// M41: lays a decal of a texture made in code (the ground pound's ring) on the surface at <paramref name="pos"/> whose normal is the Y column of <paramref name="basis"/>;
    /// <paramref name="width"/> x <paramref name="height"/> m, <paramref name="depth"/> m deep, fading by <paramref name="colour"/> over <paramref name="duration"/> seconds. It shares the
    /// alpha decals' cap and clock (the oldest fade out); like them it projects only on render layer 1.
    /// </summary>
    public void PlaceTextureDecal(Texture2D texture, Vector3 pos, Basis basis, float width, float height, float depth, float duration, FxColourCurve colour)
    {
        if (!IsReady || duration <= 0f) return;
        while (_decalRecs.Count(r => r.Node is not null && !r.Evicting) >= MaxDecals) Evict(_decalRecs.First(r => r.Node is not null && !r.Evicting));
        var element = new CDecal { Kind = FxElementKind.Decal, Colour = colour, Duration = duration };
        var rec = new DecalRec { Element = element, Age = 0.0, Duration = duration, Texture = texture };
        var node = _decalPool.Count > 0 ? _decalPool.Pop() : NewDecal();
        node.Size = new Vector3(width, depth, height);
        node.GlobalTransform = new Transform3D(basis, pos);
        node.TextureAlbedo = texture; node.TextureEmission = null; node.EmissionEnergy = 1f;
        node.Modulate = Colors.White;
        node.Visible = true;
        rec.Node = node;
        _decalRecs.Add(rec);
        ApplyDecal(rec);
    }

    /// <summary>
    /// M44: lays one of the effects package's decal textures (by its diffuse name, e.g. <c>decal_standard_cracked_dif</c>; it must be among the preloaded decals)
    /// as <see cref="PlaceTextureDecal"/> does; false when the texture is not available.
    /// </summary>
    public bool PlaceDataDecal(string diffuse, Vector3 pos, Basis basis, float width, float height, float depth, float duration, FxColourCurve colour)
    {
        if (!IsReady || DecalTexture(diffuse) is not { } tex) return false;
        PlaceTextureDecal(tex, pos, basis, width, height, depth, duration, colour);
        return true;
    }

    Decal NewDecal()
    {
        var d = new Decal { CullMask = 1u, LowerFade = 0.3f, UpperFade = 0.3f, NormalFade = 0.4f, DistanceFadeEnabled = true, DistanceFadeBegin = 35f, DistanceFadeLength = 15f };
        AddChild(d);
        return d;
    }

    const string GlowShader = """
        shader_type spatial;
        render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
        // M38: an additive decal glow (a flat quad just off the surface): the texture's colour, tinted and faded by the effect's colour curve.
        uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable;
        uniform vec4 tint : source_color = vec4(1.0);
        uniform vec2 cell_scale = vec2(1.0);
        uniform vec2 cell_offset = vec2(0.0);
        // The Chaos plasma (weapons task A): alpha 1 recolours the texture to this hue at its own brightness (as FxPlayer.Recolour does the sprites).
        uniform vec4 recolour : source_color = vec4(0.0);
        void fragment() {
            vec3 c = texture(tex, UV * cell_scale + cell_offset).rgb;
            if (recolour.a > 0.5) c = recolour.rgb * max(c.r, max(c.g, c.b));
            ALBEDO = c * tint.rgb * tint.a;
            ALPHA = 1.0;
        }
        """;

    (MeshInstance3D Quad, ShaderMaterial Material) NewGlow()
    {
        _glowShader ??= new Shader { Code = GlowShader };
        // M44: below the sprites (render priority -1, drawn before them in the transparent pass) so dust and smoke in front of a ground glow cover it, and on the
        // effects' render layer so that no other decal projects onto the glow quad.
        var material = new ShaderMaterial { Shader = _glowShader, RenderPriority = -1 };
        var quad = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1f, 1f) }, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, Layers = FxBatch.FxRenderLayer };
        AddChild(quad);
        return (quad, material);
    }

    void Evict(DecalRec r)
    {
        r.Evicting = true; r.EvictAge = 0.0;
    }

    void Retire(DecalRec r)
    {
        if (r.Node is { } node) { node.Visible = false; _decalPool.Push(node); }
        if (r.Quad is { } quad && r.Material is { } material) { quad.Visible = false; _glowPool.Push((quad, material)); }
    }

    void ClearDecals()
    {
        foreach (var r in _decalRecs) Retire(r);
        _decalRecs.Clear();
    }

    /// <summary>The decal's brightness and tint now: the element's colour curve over its duration (alpha; after the end 0), times the eviction fade.</summary>
    void ApplyDecal(DecalRec r)
    {
        var n = r.Element;
        float t = (float)Math.Clamp(r.Age / r.Duration, 0.0, 1.0);
        var colour = n.Colour.IsEmpty ? new System.Numerics.Vector4(1f, 1f, 1f, 1f) : n.Colour.Evaluate(t);
        float a = colour.W;
        if (r.Age > r.Duration) a = 0f;
        if (r.Evicting) a *= 1f - (float)Math.Clamp(r.EvictAge / EvictSeconds, 0.0, 1.0);
        a = Math.Clamp(a, 0f, 1f);
        if (r.Node is { } node) node.Modulate = new Color(colour.X, colour.Y, colour.Z, a);
        else if (r.Material is { } material)
        {
            material.SetShaderParameter("tint", new Color(colour.X, colour.Y, colour.Z, a));
            int cells = n.TileU * n.TileV;
            if (cells > 1)
            {
                int frame = Math.Min(cells - 1, (int)(t * cells));
                if (frame != r.LastFrame)
                {
                    r.LastFrame = frame;
                    material.SetShaderParameter("cell_scale", new Vector2(1f / n.TileU, 1f / n.TileV));
                    material.SetShaderParameter("cell_offset", new Vector2(frame % n.TileU / (float)n.TileU, frame / n.TileU / (float)n.TileV));
                }
            }
        }
    }

    void FadeDecals(double dt)
    {
        for (int i = _decalRecs.Count - 1; i >= 0; i--)
        {
            var r = _decalRecs[i];
            r.Age += dt;
            if (r.Evicting) r.EvictAge += dt;
            if (r.Age > r.Duration || (r.Evicting && r.EvictAge >= EvictSeconds)) { Retire(r); _decalRecs.RemoveAt(i); continue; }
            ApplyDecal(r);
        }
    }
}
