using System.Collections.Generic;
using Godot;
using SmClient;
using SmContent;
using SmFormats.Bod;

/// <summary>
/// maps-visual-qa: the engine's sky boxes (<c>seq_NN_sky</c>, shader <c>sky_ycocg_bloomcontrol</c>) drawn as a sky: the <c>YCoCg</c> texture (DXT5, the usual packing: Co in R, Cg in G, a scale in B, the luma Y in alpha;
/// read from the pictures: a sunset with a sun disc comes out) is decoded in an unlit shader, and the box is drawn around the camera, not in the world (the engine does the same; the boxes are 133 m to 1.2 km across
/// and placed away from the play area): the vertex stage keeps the box's own shape and the camera's rotation, drops its translation and puts every vertex on the far plane, so the sky sits behind all geometry. Drawn first in the
/// opaque pass (render priority -128) writing a depth just in front of the far plane (DEPTH), so everything else draws over it and Godot's own sky background, drawn at the far plane, does not paint over it (Forward+ and Mobile,
/// reversed-Z). Not drawn in the Compatibility renderer: there the sky textures are skipped and the haze stays (<c>DrawsSkyTextures</c>). Not the lighting's sky (<c>MapLightingView</c> keeps that for ambient and
/// reflections). Provisional approximation: the <c>BloomControlGrayscale</c> texture (a bloom mask) is not used, and brightness is the texture's own.
/// </summary>
public static class MapSky
{
    const string Code = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, depth_draw_always, fog_disabled, shadows_disabled;
        uniform sampler2D ycocg : filter_linear, repeat_disable;
        uniform vec3 local_centre = vec3(0.0);
        uniform float gain = 1.0;
        uniform float sky_depth = 0.0000001;   // the depth the sky writes: just in front of the far plane (reversed-Z: 1e-7; Compatibility: 1 - 1e-6), so every drawn thing is in front of it and Godot's own background sky (at the far plane) is not drawn over it
        uniform float far_z = 0.0;   // clip z / w of the far plane: 0 with reversed-Z (Forward+, Mobile), just under 1 in the Compatibility renderer
        varying vec3 dir;
        void vertex() {
            vec3 d = mat3(MODEL_MATRIX) * (VERTEX - local_centre);
            vec4 p = PROJECTION_MATRIX * vec4(mat3(VIEW_MATRIX) * d, 1.0);
            POSITION = vec4(p.xy, far_z * p.w, p.w);
            dir = d;   // from the box's centre, interpolated: its elevation is read per pixel
        }
        void fragment() {
            if (dir.y < -0.05 * length(dir)) discard;   // the box's lower half is not drawn: its bottom picture smears (Hab Center's pit showed it through the gaps) and the level covers it in the original
            vec4 t = texture(ycocg, vec2(UV.x, UV.y + 1.0));   // the boxes carry V in -1..0 for a wrapping sampler (V = -1 is the image's top, the zenith; the side's V = 0 is its bottom row): +1 reads it as is. Reading -V (first try) flipped the sky and, clamped, smeared
            float s = t.b * 255.0 / 8.0 + 1.0;
            float co = (t.r - 128.0 / 255.0) / s;
            float cg = (t.g - 128.0 / 255.0) / s;
            vec3 srgb = clamp(vec3(t.a + co - cg, t.a + cg, t.a - co - cg), 0.0, 1.0);
            ALBEDO = pow(srgb, vec3(2.2)) * gain;
            DEPTH = sky_depth;
        }
        """;

    static Shader? _shader;
    static readonly Dictionary<string, Texture2D?> Textures = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>The sky material for <paramref name="bmat"/> (its <c>YCoCg</c> texture), or null when the texture is missing or does not decode. <paramref name="centre"/>: the box's centre in its own (model) space.</summary>
    public static Material? Build(BmatMaterial bmat, IReadOnlyDictionary<string, byte[]> dds, Vector3 centre)
    {
        if (!bmat.Textures.TryGetValue("YCoCg", out var name) || !dds.TryGetValue(name, out var bytes)) return null;
        string key = $"{name}/{bytes.Length}";
        if (!Textures.TryGetValue(key, out var texture))
        {
            var image = new Image();
            Textures[key] = texture = image.LoadDdsFromBuffer(bytes) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
        }
        if (texture is null) return null;
        _shader ??= new Shader { Code = Code };
        var material = new ShaderMaterial { Shader = _shader, RenderPriority = -128 };
        material.SetShaderParameter("ycocg", texture);
        material.SetShaderParameter("local_centre", centre);
        bool compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        material.SetShaderParameter("far_z", MapDecorLists.SkyFarZ(compat));
        material.SetShaderParameter("sky_depth", compat ? 0.999999f : 0.0000001f);
        return material;
    }
}
