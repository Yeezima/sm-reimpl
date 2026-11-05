using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// The kit look of Ashfall Citadel (docs/musou-ashfall-graybox.md "Kit look (client)"): the dressing's items drawn with the owner's generated models
/// (<see cref="AshfallKitLibrary"/>), the graybox parts the dressing hides left out, and the rest of the graybox in a stone and ash material (no checker).
/// Draft 5: the collision parts (the dressing's hidden list) are never drawn in the kit look; the ground kinds and the boundary keep seeded-variation textures. Lives in game space (X east, Y north, Z up; the parent converts).
/// Keys: F8 toggles kit look / graybox (collision as plain stone, models hidden); Shift+F8 toggles a translucent collision overlay over the kit look. **Placeholder demonstration.**
/// </summary>
public partial class AshfallKitView : Node3D
{
    /// <summary>The toggle key (F8: F1 overlay, F9 fire test, F10 observer and F11 are taken; F8 was free).</summary>
    public const Key ToggleKey = Key.F8;
    // Shift+F8 (F8 with Shift is bound nowhere else in client/): the kit look with the collision drawn over it.

    /// <summary>
    /// The transform that fits a model into an item's box by the contract (<see cref="AshfallDressItem"/>): the raw bounds on the mapped axes fill <see cref="AshfallDressItem.Size"/>, the
    /// box is centred on the position in X/Y with its bottom at the position's Z, glTF +Y becomes game +Z, then yaw about Z. The mapping is a rotation (no mirror): not swapped,
    /// glTF X to local X and glTF -Z to local Y (the model's front, glTF +Z, faces -Y at yaw 0); swapped, glTF Z to local X and glTF X to local Y. Result: glTF frame to game frame.
    /// </summary>
    public static Transform3D Fit(AshfallDressItem item, AshfallKitModel model)
    {
        var raw = model.RawSize;
        var min = model.RawMin;
        float rx = Math.Max(raw.X, 1e-4f), ry = Math.Max(raw.Y, 1e-4f), rz = Math.Max(raw.Z, 1e-4f);
        Basis m;
        if (!item.SwapXZ)
            m = new Basis(new Vector3(item.Size.X / rx, 0, 0), new Vector3(0, 0, item.Size.Z / ry), new Vector3(0, -item.Size.Y / rz, 0));   // columns: where glTF X, Y, Z go
        else
            m = new Basis(new Vector3(0, item.Size.Y / rx, 0), new Vector3(0, 0, item.Size.Z / ry), new Vector3(item.Size.X / rz, 0, 0));
        var centre = new Vector3((min.X + model.RawMax.X) * 0.5f, min.Y, (min.Z + model.RawMax.Z) * 0.5f);
        var yaw = new Basis(new Vector3(0, 0, 1), item.Yaw);
        var basis = yaw * m;
        var origin = new Vector3(item.Position.X, item.Position.Y, item.Position.Z) - basis * centre;
        return new Transform3D(basis, origin);
    }

    // ours: the stone and ash colours of the remaining graybox parts (kind by kind)
    static Color StoneOf(AshfallKind k) => k switch
    {
        AshfallKind.Ground => new Color(0.36f, 0.33f, 0.30f),
        AshfallKind.Road => new Color(0.24f, 0.23f, 0.22f),
        AshfallKind.Paving => new Color(0.44f, 0.42f, 0.39f),
        AshfallKind.Curb => new Color(0.46f, 0.44f, 0.41f),
        AshfallKind.Wall => new Color(0.40f, 0.38f, 0.36f),
        AshfallKind.Cover => new Color(0.44f, 0.42f, 0.40f),
        AshfallKind.Rubble => new Color(0.34f, 0.31f, 0.28f),
        AshfallKind.Floor => new Color(0.38f, 0.36f, 0.34f),
        AshfallKind.Stair => new Color(0.42f, 0.40f, 0.37f),
        AshfallKind.Perch => new Color(0.40f, 0.38f, 0.36f),
        AshfallKind.Stronghold => new Color(0.30f, 0.26f, 0.25f),
        AshfallKind.Boundary => new Color(0.18f, 0.17f, 0.17f),
        AshfallKind.Backdrop => new Color(0.16f, 0.16f, 0.17f),
        _ => new Color(0.4f, 0.38f, 0.36f),
    };

    static ImageTexture? _stone;

    /// <summary>A seamless 128 px noise (ash and grit) the triplanar stone material tiles every 4 m.</summary>
    static ImageTexture Stone()
    {
        if (_stone is not null) return _stone;
        var noise = new FastNoiseLite { Frequency = 0.045f, FractalOctaves = 4, Seed = 7 };
        var img = noise.GetSeamlessImage(128, 128);
        img.Convert(Image.Format.Rgb8);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float v = 0.78f + 0.22f * img.GetPixel(x, y).R;
                img.SetPixel(x, y, new Color(v, v, v));
            }
        img.GenerateMipmaps();
        return _stone = ImageTexture.CreateFromImage(img);
    }

    static ImageTexture? _terrain, _masonry;

    /// <summary>
    /// A seamless 256 px ash and stone texture (ours): big soft patches of dark ash and pale dust, fine grit and scattered pale stone flecks; triplanar, one tile every 8 m. Not flat, not a checker.
    /// </summary>
    static ImageTexture Terrain()
    {
        if (_terrain is not null) return _terrain;
        var big = new FastNoiseLite { Frequency = 0.012f, FractalOctaves = 4, Seed = 11 }.GetSeamlessImage(256, 256);
        var mid = new FastNoiseLite { Frequency = 0.05f, FractalOctaves = 3, Seed = 23 }.GetSeamlessImage(256, 256);
        var grit = new FastNoiseLite { Frequency = 0.35f, FractalOctaves = 2, Seed = 5 }.GetSeamlessImage(256, 256);
        var img = Image.CreateEmpty(256, 256, false, Image.Format.Rgb8);
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float b = big.GetPixel(x, y).R, m = mid.GetPixel(x, y).R, g = grit.GetPixel(x, y).R;
                float v = 0.58f + 0.34f * (b - 0.5f) + 0.24f * (m - 0.5f) + 0.20f * (g - 0.5f);
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                if (h % 97 == 0) v += 0.18f;      // a pale stone fleck
                else if (h % 61 == 0) v -= 0.14f; // a dark pit
                v = Math.Clamp(v, 0.18f, 1f);
                img.SetPixel(x, y, new Color(v * 1.01f, v, v * 0.97f));   // a hint warm: ash, not blue-grey
            }
        img.GenerateMipmaps();
        return _terrain = ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// A seamless 256 px ashlar masonry (ours): courses 1 m high and blocks 4 m long over a 4 m x 4 m tile (triplanar, one tile every 4 m), joints stepped half a block on every other course, each course its own tone and grit, mortar lines dark.
    /// </summary>
    static ImageTexture Masonry()
    {
        if (_masonry is not null) return _masonry;
        var grit = new FastNoiseLite { Frequency = 0.12f, FractalOctaves = 3, Seed = 31 }.GetSeamlessImage(256, 256);
        var img = Image.CreateEmpty(256, 256, false, Image.Format.Rgb8);
        for (int y = 0; y < 256; y++)
        {
            int course = y / 64, row = y % 64;
            for (int x = 0; x < 256; x++)
            {
                int xx = (x + (course % 2) * 128) % 256;   // the joint sits at 0 on even courses, at half a block on odd ones
                uint bh = (uint)(course * 40503 + 12345) * 2654435761u; bh ^= bh >> 15;
                float tone = 0.50f + 0.14f * ((bh % 100) / 100f - 0.5f) * 2f + (course % 2 == 0 ? 0.02f : -0.02f);
                float g = grit.GetPixel(x, y).R;
                float v = tone + 0.20f * (g - 0.5f);
                if (xx < 2 || row < 2) v *= 0.45f;   // the mortar line
                else if (xx < 4 || xx > 252 || row < 4 || row > 61) v *= 0.8f;   // a soft bevel
                img.SetPixel(x, y, new Color(v * 1.02f, v, v * 0.95f));
            }
        }
        img.GenerateMipmaps();
        return _masonry = ImageTexture.CreateFromImage(img);
    }

    static StandardMaterial3D Triplanar(Texture2D texture, Color tint, float perMetre) => new()
    {
        AlbedoColor = tint, AlbedoTexture = texture,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = new Vector3(perMetre, perMetre, perMetre),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 1f,
    };

    // ---- Draft 5: seeded surface variation (ours; subtle, flat, no geometry added) ------------------------------------------------------------------------------
    // Ground kinds: a hash of (8 m cell, seed) gives a tone and a grit value per cell, blended smoothly between cell centres (value noise, tone +-4 %); no per-cell turn, no visible cell edge.
    // Boundary: per 4 m x 1 m block (the masonry's own courses and joints) a hash picks one of three block faces (plain, darker and grittier, lighter with a small chip at the edge); joints stay.
    // Plain spatial shaders (integer hash, textureGrad, no Forward+ feature), so the Compatibility renderer runs them too. `--ashfall-variation off` draws the draft-3 materials instead.
    const string GroundShader = """
shader_type spatial;
render_mode cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform int seed = 1;
uniform float tile = 8.0;
varying vec3 wpos;
varying vec3 wnor;
uint cellhash(int a, int b, int s) {
	uint h = uint(a) * 73856093u ^ uint(b) * 19349663u ^ uint(s) * 83492791u;
	h ^= h >> 13u; h *= 1540483477u; h ^= h >> 15u; h *= 2654435761u; h ^= h >> 13u;
	return h;
}
float cellval(int a, int b, int s) { return float(cellhash(a, b, s) & 1023u) / 1023.0; }
void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnor = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}
void fragment() {
	vec3 an = abs(wnor);
	vec2 p;
	if (an.y >= an.x && an.y >= an.z) p = wpos.xz;
	else if (an.x >= an.z) p = wpos.zy;
	else p = wpos.xy;
	// Smooth value noise over the 8 m cells (bilinear between the cell values, smoothstep-eased): no cell edge shows. Tone about +-4 %, grit contrast about +-12 %.
	vec2 g = wpos.xz / tile - 0.5;
	ivec2 i0 = ivec2(floor(g));
	vec2 f = g - vec2(i0);
	f = f * f * (3.0 - 2.0 * f);
	float t0 = mix(cellval(i0.x, i0.y, seed), cellval(i0.x + 1, i0.y, seed), f.x);
	float t1 = mix(cellval(i0.x, i0.y + 1, seed), cellval(i0.x + 1, i0.y + 1, seed), f.x);
	float tn = mix(t0, t1, f.y);
	float r0 = mix(cellval(i0.x, i0.y, seed + 7919), cellval(i0.x + 1, i0.y, seed + 7919), f.x);
	float r1 = mix(cellval(i0.x, i0.y + 1, seed + 7919), cellval(i0.x + 1, i0.y + 1, seed + 7919), f.x);
	float gn = mix(r0, r1, f.y);
	vec3 c = texture(tex, p / tile).rgb;
	c = ((c - 0.58) * (1.0 + 0.24 * (gn - 0.5)) + 0.58) * (1.0 + 0.08 * (tn - 0.5));
	ALBEDO = c * tint.rgb;
	ROUGHNESS = 1.0;
}
""";

    const string MasonryShader = """
shader_type spatial;
render_mode cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform int seed = 1;
uniform float tile = 4.0;
varying vec3 wpos;
varying vec3 wnor;
uint blockhash(int a, int b, int c, int s) {
	uint h = uint(a) * 73856093u ^ uint(b) * 19349663u ^ uint(c) * 83492791u ^ uint(s) * 2654435761u;
	h ^= h >> 13u; h *= 1540483477u; h ^= h >> 15u; h *= 2654435761u; h ^= h >> 13u;
	return h;
}
void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnor = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}
void fragment() {
	vec3 an = abs(wnor);
	vec3 c;
	if (an.y > an.x && an.y > an.z) {
		c = texture(tex, wpos.xz * 0.25).rgb;
	} else {
		bool xface = an.x > an.z;
		float u = xface ? wpos.z : wpos.x;
		float plane = xface ? wpos.x : wpos.z;
		c = texture(tex, vec2(u * 0.25, -wpos.y * 0.25)).rgb;
		int course = int(floor(-wpos.y));
		float odd = float(course & 1);
		float s = u + 2.0 * odd;
		float bidx = floor(s / 4.0);
		float bu = s - bidx * 4.0;
		float bv = fract(-wpos.y);
		uint h = blockhash(int(bidx), course, int(floor(plane)) * 2 + (xface ? 1 : 0), seed);
		int variant = int(h % 3u);
		float edge = min(min(bu, 4.0 - bu), min(bv, 1.0 - bv));
		float inner = smoothstep(0.06, 0.12, edge);
		float tone = 1.0;
		float grit = 1.0;
		if (variant == 1) { tone = 0.90; grit = 1.25; }
		else if (variant == 2) { tone = 1.07; grit = 0.85; }
		vec3 d = ((c - 0.5) * grit + 0.5) * tone;
		if (variant == 2) {
			vec2 centre = vec2(((h >> 4u) & 1u) == 0u ? 0.35 : 3.65, ((h >> 5u) & 1u) == 0u ? 0.82 : 0.18);
			vec2 r = (vec2(bu, bv) - centre) / vec2(0.40, 0.20);
			float ragged = 1.0 + 0.35 * (c.r - 0.5);
			if (dot(r, r) < ragged * ragged) d *= 0.80;
		}
		c = mix(c, d, inner);
	}
	ALBEDO = c * tint.rgb;
	ROUGHNESS = 1.0;
}
""";

    static Shader? _groundShader, _masonryShader;
    static bool _variationOn = true;

    static ShaderMaterial Variant(string code, ref Shader? cache, Texture2D texture, Color tint, int seed, float tile)
    {
        cache ??= new Shader { Code = code };
        var m = new ShaderMaterial { Shader = cache };
        m.SetShaderParameter("tex", texture);
        m.SetShaderParameter("tint", tint);
        m.SetShaderParameter("seed", seed);
        m.SetShaderParameter("tile", tile);
        return m;
    }

    /// <summary>The level's variation seed (<c>AshfallMap.VariationSeed</c>); <c>--ashfall-seed N</c> overrides (test aid).</summary>
    public static int VariationSeed()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--ashfall-seed");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var asked)) return asked;
        return (int)AshfallMap.VariationSeed;
    }

    /// <summary>What the last build and load measured (see the log line).</summary>
    public static string Report { get; private set; } = "";

    readonly AshfallKitLibrary _library = new();
    readonly AshfallDressing _dressing;
    readonly Node3D? _graybox;
    readonly Dictionary<string, List<int>> _itemsByAsset = new();
    readonly AshfallLevel _level;
    readonly HashSet<string> _failedLogged = [];
    Node3D? _overlay;
    bool _overlayOn;
    readonly Node3D _items = new() { Name = "Items" };
    readonly Node3D _remainder = new() { Name = "Remainder" };
    readonly HashSet<int> _drawnItems = [];
    int _shown, _shownInstances, _failedItems;
    long _startMs;
    bool _reported, _harmonised;
    bool _kit = true;

    /// <summary>True while the kit look is the one drawn.</summary>
    public bool Kit => _kit;
    public AshfallKitLibrary Library => _library;
    public int ItemsShown => _shown;
    public int InstancesShown => _shownInstances;
    public int ItemsFailed => _failedItems;

    public AshfallKitView(AshfallLevel level, AshfallDressing dressing, Node3D? graybox)
    {
        Name = "KitLook";
        _level = level;
        _dressing = dressing;
        _graybox = graybox;
        AddChild(_remainder);
        AddChild(_items);
        foreach (var part in dressing.HiddenParts) _hiddenAll.Add(part);
        BuildRemainder(level);
        ApplyArguments();
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            string id = dressing.Items[i].Asset;
            if (!_itemsByAsset.TryGetValue(id, out var list)) _itemsByAsset[id] = list = [];
            list.Add(i);
        }
        _startMs = (long)Time.GetTicksMsec();
        _library.Request(_itemsByAsset.Keys);
    }

    /// <summary>The camera-facing options: <c>--ashfall-tex N</c>, <c>--ashfall-lods off</c> and <c>--ashfall-shadows off</c> (measurement aids).</summary>
    bool _shadows = true;
    /// <summary>Mesh LOD threshold (pixels of error) while the kit look is drawn; the engine default is 1. Measured: 1 costs about 20 ms of GPU time on the dense map, 4 about 3 to 5 ms (ours; <c>--ashfall-lod-threshold</c>).</summary>
    public float LodThreshold { get; set; } = 4f;
    float _baseLod = 1f;
    bool _lodKept;
    void ApplyArguments()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Value(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Value("ashfall-tex") is { } t && int.TryParse(t, out var tex)) _library.TextureMax = tex;
        if (Value("ashfall-lods") == "off") _library.GenerateLods = false;
        if (Value("ashfall-shadows") == "off") _shadows = false;
        if (Value("ashfall-lod-threshold") is { } lt && float.TryParse(lt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var th) && th >= 0.1f) LodThreshold = th;
    }

    void BuildRemainder(AshfallLevel level)
    {
        var hidden = _hiddenAll;
        // Draft 5: the collision parts (the dressing's hidden list) are never drawn in the kit look. The ground kinds (Ground, Road, Paving, Curb) are drawn as ash and stone terrain and the boundary as a flat
        // masonry wall, both with the seeded variation; any other part the dressing does not hide keeps the stone material.
        int seed = VariationSeed();
        var args = OS.GetCmdlineUserArgs();
        int vi = Array.IndexOf(args, "--ashfall-variation");
        if (vi >= 0 && vi + 1 < args.Length && args[vi + 1] == "off") _variationOn = false;
        GD.Print($"ashfall kit: surface variation {(_variationOn ? "on" : "off")}, seed {seed}");
        foreach (var group in level.Parts.Select((p, i) => (Part: p, Index: i)).Where(x => x.Part.Kind != AshfallKind.Objective && !hidden.Contains(x.Index)).GroupBy(x => x.Part.Kind))
        {
            Material material;
            try
            {
                material = group.Key switch
                {
                    AshfallKind.Ground => Ground(new Color(0.62f, 0.59f, 0.56f), seed),
                    AshfallKind.Curb => Ground(new Color(0.70f, 0.67f, 0.63f), seed),
                    AshfallKind.Road => Ground(new Color(0.52f, 0.50f, 0.48f), seed),
                    AshfallKind.Paving => Ground(new Color(0.95f, 0.92f, 0.86f), seed),
                    AshfallKind.Boundary => _variationOn ? Variant(MasonryShader, ref _masonryShader, Masonry(), new Color(0.62f, 0.58f, 0.55f), seed, 4f) : Triplanar(Masonry(), new Color(0.62f, 0.58f, 0.55f), 0.25f),
                    _ => Triplanar(Stone(), StoneOf(group.Key), 0.25f),
                };
            }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
            {
                GD.Print($"ashfall kit: the variation material failed ({e.GetType().Name}: {e.Message}); the plain triplanar material is used");
                material = Triplanar(Terrain(), new Color(0.6f, 0.58f, 0.55f), 0.125f);
            }
            var corners = group.SelectMany(x => x.Part.Corners).ToArray();
            if (corners.Length > 0)
                _remainder.AddChild(new MeshInstance3D { Name = group.Key.ToString(), Mesh = AshfallView.MeshOf(corners), MaterialOverride = material });
        }
    }

    static Material Ground(Color tint, int seed) => _variationOn
        ? Variant(GroundShader, ref _groundShader, Terrain(), tint, seed, 8f)
        : Triplanar(Terrain(), tint, 0.125f);

    /// <summary>Every part of the dressing's hidden list (all collision that is not ground: never drawn in the kit look).</summary>
    readonly HashSet<int> _hiddenAll = [];

    // ---- the collision overlay (Shift+F8) ----------------------------------------------------------------------------------------------------------------

    /// <summary>Builds the overlay once: the hidden parts as one unshaded translucent mesh (about 25 % alpha) and their outlines (coplanar triangle seams left out).</summary>
    void BuildOverlay()
    {
        _overlay = new Node3D { Name = "CollisionOverlay", Visible = false };
        var parts = _hiddenAll.OrderBy(x => x).Where(i => i >= 0 && i < _level.Parts.Count).Select(i => _level.Parts[i].Corners).ToArray();
        var corners = parts.SelectMany(c => c).ToArray();
        if (corners.Length > 0)
        {
            _overlay.AddChild(new MeshInstance3D
            {
                Name = "Fill", Mesh = AshfallView.MeshOf(corners), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.25f, 0.75f, 1f, 0.25f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled, DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                },
            });
            var edges = OutlineOf(parts);
            if (edges is not null)
                _overlay.AddChild(new MeshInstance3D
                {
                    Name = "Edges", Mesh = edges, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.55f, 0.9f, 1f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
                });
        }
        AddChild(_overlay);
    }

    /// <summary>Line mesh of the triangle edges of each part, dropping an edge shared by two coplanar triangles of the same part (the diagonal of a quad).</summary>
    static ArrayMesh? OutlineOf(IEnumerable<System.Numerics.Vector3[]> parts)
    {
        var lines = new List<Vector3>();
        static (long, long, long) Q(System.Numerics.Vector3 v) => ((long)MathF.Round(v.X * 100f), (long)MathF.Round(v.Y * 100f), (long)MathF.Round(v.Z * 100f));
        foreach (var tris in parts)
        {
            var seen = new Dictionary<((long, long, long), (long, long, long)), (System.Numerics.Vector3 N, int Count, System.Numerics.Vector3 A, System.Numerics.Vector3 B, bool Coplanar)>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(tris[t + 1] - tris[t], tris[t + 2] - tris[t]));
                if (!float.IsFinite(n.X)) continue;
                for (int e = 0; e < 3; e++)
                {
                    var a = tris[t + e]; var b = tris[t + (e + 1) % 3];
                    var qa = Q(a); var qb = Q(b);
                    var key = qa.CompareTo(qb) <= 0 ? (qa, qb) : (qb, qa);
                    if (seen.TryGetValue(key, out var old)) seen[key] = (old.N, old.Count + 1, old.A, old.B, old.Coplanar || MathF.Abs(System.Numerics.Vector3.Dot(old.N, n)) > 0.9999f);
                    else seen[key] = (n, 1, a, b, false);
                }
            }
            foreach (var v in seen.Values)
                if (!(v.Count == 2 && v.Coplanar)) { lines.Add(new(v.A.X, v.A.Y, v.A.Z)); lines.Add(new(v.B.X, v.B.Y, v.B.Z)); }
        }
        if (lines.Count == 0) return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = lines.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    /// <summary>True while the translucent collision overlay is drawn over the kit look.</summary>
    public bool Overlay => _overlayOn && _kit;

    /// <summary>Shows or hides the collision overlay over the kit look (turning it on from the graybox look switches to the kit look first). Logged.</summary>
    public void SetOverlay(bool on)
    {
        if (on && !_kit) SetKit(true);
        _overlayOn = on;
        if (on && _overlay is null) BuildOverlay();
        if (_overlay is not null) _overlay.Visible = on;
        GD.Print($"ashfall: collision overlay {(on ? "on" : "off")} ({_hiddenAll.Count} parts)");
    }

    /// <summary>Switches the look: the kit (this node, the graybox view's objective markers only) or the graybox (the whole graybox view). Logged.</summary>
    public void SetKit(bool kit)
    {
        _kit = kit;
        Visible = kit;
        if (IsInsideTree())
        {
            var viewport = GetViewport();
            if (!_lodKept) { _baseLod = viewport.MeshLodThreshold; _lodKept = true; }
            viewport.MeshLodThreshold = kit ? LodThreshold : _baseLod;
        }
        if (_graybox is not null)
            foreach (var child in _graybox.GetChildren().OfType<Node3D>())
                child.Visible = !kit || child.Name.ToString().StartsWith("Objective", StringComparison.Ordinal) || child.Name.ToString().StartsWith("Letter", StringComparison.Ordinal);
        GD.Print($"ashfall: look {(kit ? "kit" : "graybox")}");
    }

    /// <summary>The viewport is shared with every later map (classic PvP too): leave it with the threshold it had before the kit look changed it.</summary>
    public override void _ExitTree()
    {
        if (_lodKept && IsInsideTree()) GetViewport().MeshLodThreshold = _baseLod;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: ToggleKey } k)
        {
            if (k.ShiftPressed) SetOverlay(!(_kit && _overlayOn));   // Shift+F8: the kit look with the collision drawn over it
            else SetKit(!_kit);                                       // F8: kit look <-> graybox (the collision drawn as plain stone, models hidden)
        }
    }

    public override void _Process(double delta)
    {
        long budgetEnd = (long)Time.GetTicksUsec() + 4000;
        bool libraryDone = _library.Pending == 0;   // read before draining: a model finishing in between is then still placed before the report
        foreach (var id in _library.TakeReady()) _toPlace.Enqueue(id);
        // Models that arrived but did not fit this frame's budget wait for the next frame (TakeReady drains the library's queue).
        while (_toPlace.Count > 0 && (long)Time.GetTicksUsec() <= budgetEnd) Instantiate(_toPlace.Dequeue());
        if (!_harmonised && libraryDone && _library.Loaded > 0) { _harmonised = true; Harmonise(); }
        if (!_reported && libraryDone && _toPlace.Count == 0 && _library.Loaded + _library.Failed > 0)
        {
            _reported = true;
            var assets = _library.All.Where(a => a.Ok).ToList();
            Report = $"{_library.Loaded} models loaded, {_library.Failed} failed, wall {_library.WallMs:F0} ms (sum of per-model {_library.All.Sum(a => a.Ms):F0} ms), " +
                     $"{assets.Sum(a => a.Triangles):N0} triangles at full detail, textures ~{assets.Sum(a => a.TextureBytes) / 1048576.0:F0} MiB, items shown {_shown} ({_shownInstances} mesh instances), items without a model {_failedItems}";
            GD.Print("ashfall kit: " + Report);
        }
    }

    /// <summary>
    /// Material harmonisation (ours): the generated models differ in how pale their base colour is (manifest: AC-009, AC-012, AC-022 and above all AC-028 "brighter than neighbouring assets"). Once every model is
    /// loaded, each model's measured base-colour luminance is scaled toward the median of the loaded set (factor clamped to 0.6..1.1, multiplied into its materials' albedo colour). The factors are logged once.
    /// </summary>
    void Harmonise()
    {
        var ok = _library.All.Where(a => a.Ok && a.Luma > 0.01f).ToList();
        if (ok.Count < 3) return;
        var sorted = ok.Select(a => a.Luma).OrderBy(x => x).ToArray();
        float median = sorted[sorted.Length / 2];
        var done = new HashSet<BaseMaterial3D>();
        var log = new List<string>();
        foreach (var a in ok.OrderBy(a => a.Id))
        {
            float f = Math.Clamp(median / a.Luma, 0.6f, 1.1f);
            if (MathF.Abs(f - 1f) < 0.02f) continue;
            foreach (var piece in a.Pieces)
                for (int s = 0; s < piece.Mesh.GetSurfaceCount(); s++)
                    if (piece.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D m && done.Add(m))
                        m.AlbedoColor = new Color(m.AlbedoColor.R * f, m.AlbedoColor.G * f, m.AlbedoColor.B * f, m.AlbedoColor.A);
            log.Add($"{a.Id} {a.Luma:F2}x{f:F2}");
        }
        GD.Print($"ashfall kit: albedo harmonised toward median luminance {median:F2}: {(log.Count == 0 ? "no change" : string.Join(", ", log))}");
    }

    // Mesh instances of one asset are created once its model arrives; the pieces share the cached meshes.
    readonly Queue<string> _toPlace = new();

    void Instantiate(string id)
    {
        var asset = _library.Get(id);
        var model = AshfallKit.Models.FirstOrDefault(m => m.Id == id);
        if (!_itemsByAsset.TryGetValue(id, out var indexes)) return;
        if ((asset is not { Ok: true } || model is null) && _failedLogged.Add(id))
            GD.Print($"ashfall kit: model {id} did not load ({asset?.Error ?? (model is null ? "not in the manifest" : "no result")}); nothing is shown for it in the kit look");
        foreach (int i in indexes)
        {
            var item = _dressing.Items[i];
            if (asset is not { Ok: true } || model is null) { _failedItems++; continue; }
            var fit = Fit(item, model);
            foreach (var piece in asset.Pieces)
            {
                _items.AddChild(new MeshInstance3D
                {
                    Mesh = piece.Mesh, Transform = fit * piece.Local,
                    CastShadow = _shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                });
                _shownInstances++;
            }
            _shown++;
        }
    }
}
