using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Fx;

namespace SmClient;

/// <summary>
/// M25b: draws every sprite of the original effect systems (rings, beams, streamers) as camera-facing or axis-facing quads out of the
/// original effects atlas (<c>master_fx.dds</c>, rects from <c>master_fx.tpage</c>). All quads of a frame go into two
/// <see cref="MultiMesh"/>es, one additive and one alpha-blended (sorted back to front), whose buffers are rewritten each frame: the quad's
/// transform carries its centre and its two full-size axes, the instance colour carries the effect's colour curve and the custom data the
/// atlas rect. The third basis column carries the texture-U range and addressing mode (the quad has no depth, so it is free).
/// Unshaded, no distortion (materials with <c>AdditiveBlend</c> 2 are not drawn). Rotated atlas rects: <see cref="RotatedClockwise"/>
/// picks which way the sprite was turned (**provisional**; chosen by looking at a known sprite, see the F1 line).
/// M42 fades (**ours**, a deliberate upgrade over the original renderer; owner's decision, docs/changes-from-original.md). The caller says what
/// kind of quad it adds (<see cref="QuadKind"/>); sizes are the quad's "fade size": its short side for a quad more than 3 times longer than wide (god rays: always the long side)
/// (tracers, beams), else its long side. Small sprites (muzzle flashes, sparks, impacts) keep their look; large cards (the map's
/// <c>amb_god_ray_*</c> beams, steam and fog patches) blend into the air:
/// <list type="bullet">
/// <item>Soft particles: the quad fades where it comes close to the scene behind it (depth buffer), over <see cref="SoftK"/> x its fade size
/// (clamped to <see cref="SoftMin"/>..<see cref="SoftMax"/> m), phased in between fade sizes 1 and 2.5 m (smaller sprites: none). Except for god
/// rays the comparison starts <see cref="SoftPush"/> x that distance in front of the quad, so a sprite flush with a wall (impacts and blasts sit
/// 0.02 m off the surface) keeps most of its opacity. Flat rings (ground shockwaves, M41) have none: they lie on the surface by design.</item>
/// <item>Near-camera fade: zero within <see cref="NearZeroK"/> and full beyond <see cref="NearK"/> x its fade size (clamped to
/// <see cref="NearMin"/>..<see cref="NearMax"/> m; quads under <see cref="NearSmallSize"/> m at most <see cref="NearSmallMax"/> m, so the own muzzle flash in aim mode
/// stays; god rays at least 5 m, so zero within 1.5 m; additive glows other than god rays never): a card never fills the screen when the camera
/// passes through it.</item>
/// <item>Edge-on fade: a flat card seen along its plane fades out (|N.V| below <see cref="EdgeSmall"/> for small quads and every flat ring, up to
/// <see cref="EdgeLarge"/> for flat cards of 6 m and more). Never on axis billboards (tracers, beams, streamers: they turn around their axis toward
/// the camera and are seen nearly edge-on from behind the shooter; the data's <c>GodRayFade</c> covers god rays) or camera billboards.</item>
/// <item>End fade: god-ray beams fade at both ends of their plain U range (<see cref="EndFade"/>).</item>
/// </list>
/// The depth read reconstructs view depth with Forward+ (Vulkan) conventions (depth texture value in NDC z directly).
/// </summary>
public sealed partial class FxBatch : Node3D
{
    /// <summary>M42 soft-particle distance per metre of quad size and its clamp (metres). **Ours.**</summary>
    public const float SoftK = 0.3f, SoftMin = 0.05f, SoftMax = 2.5f;
    /// <summary>M42 soft-particle offset toward the camera, as a fraction of the soft distance (not for god rays). **Ours.**</summary>
    public const float SoftPush = 0.75f;

    /// <summary>
    /// feel/jump-pack (ours; the owner, 2026-10-29: "make sure that textures aren't poking through the dust and smoke effects. its jarring visually"): the quads of a
    /// ground effect (<see cref="FxEffect.SoftGround"/>: the pound, the slash, the jump landings and the take-off burst) fade against the scene with no
    /// <see cref="SoftPush"/> (the push left 75% of a card's opacity where the ground or a body cuts it: a hard line), over <see cref="GroundSoftScale"/> x the soft
    /// distance, phased in from sprites of <see cref="GroundSoftFrom"/> m (full at <see cref="GroundSoftFull"/> m) instead of 1 to 2.5 m. Flat rings keep no soft fade.
    /// </summary>
    public const float GroundSoftScale = 1.6f, GroundSoftFrom = 0.25f, GroundSoftFull = 0.8f;
    /// <summary>
    /// fx/no-smoke (ours): a camera-facing card (<see cref="QuadKind.CameraBillboard"/>) is depth-tested at a plane moved toward the camera by <see cref="PullShare"/> of its half size, at most
    /// <see cref="PullMax"/> m and never closer than <see cref="PullKeep"/> m to the camera, and shrunk by the same perspective so it covers the same pixels. A flat card through the cloud's centre let
    /// the floor and its decals in front of that plane draw over the cloud (the owner's playtest of 2026-10-29: "lines on the floor" crisp on top of dense smoke and dust); now what lies inside the
    /// front part of the cloud is covered. Only alpha-blended cards are pulled: an additive card cannot hide what is behind it, and pulled it lit walls in front of a blast (review S1). The cap
    /// (1 m; 3 m in the first cut) bounds the leak: a card whose centre is less than that behind a thin surface (a door, a crate's side) can draw over it. The near-camera fade reads the
    /// card's own depth, not the pulled one, so a pulled card fades exactly as before (review S2). <c>--no-fx-pull</c> (test aid): the cards at their centre (the before picture).
    /// </summary>
    public const float PullShare = 0.5f, PullMax = 1f, PullKeep = 0.5f;
    /// <summary><c>--no-fx-pull</c> (test aid): camera billboards depth-tested at their centre, as before fx/no-smoke.</summary>
    public static bool NoPull { get; set; } = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--no-fx-pull");
    /// <summary>The effect being drawn is a ground effect (set by <see cref="FxPlayer"/> per effect; see <see cref="GroundSoftScale"/>).</summary>
    public bool SoftGround { get; set; }
    /// <summary><c>--no-ground-soft</c> (test aid): ground effects drawn with the M42 fades only (the before picture).</summary>
    public static bool NoGroundSoft { get; set; } = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--no-ground-soft");
    /// <summary>M42 near-camera fade: full opacity beyond <see cref="NearK"/> x the quad's size (clamped), zero at <see cref="NearZeroK"/> of that. **Ours.**</summary>
    public const float NearK = 0.35f, NearMin = 0.25f, NearMax = 10f, NearZeroK = 0.3f;
    /// <summary>M42: the near fade's full-opacity distance for quads under <see cref="NearSmallSize"/> m is at most this (own muzzle flashes and their
    /// glow in aim mode). **Ours.**</summary>
    public const float NearSmallMax = 0.5f, NearSmallSize = 4f;
    /// <summary>M42 edge-on fade: |N.V| at which a small (1 m) and a large (6 m) quad reach full opacity. **Ours.**</summary>
    public const float EdgeSmall = 0.06f, EdgeLarge = 0.45f;
    /// <summary>M42 end fade: a god-ray beam fades over this fraction of its plain U range at both ends, so it has no bright cut-off head in
    /// mid-air. **Ours.**</summary>
    public const float EndFade = 0.2f;

    /// <summary>What a quad is, for the M42 fades (sent to the shader with the U addressing mode: <c>mode + 4 x kind</c>).</summary>
    public enum QuadKind
    {
        /// <summary>Turned to face the camera (camera-facing rings).</summary>
        CameraBillboard = 0,
        /// <summary>Turned around its long axis toward the camera (billboard beams, tracers, streamers): no edge-on fade.</summary>
        AxisBillboard = 1,
        /// <summary>A flat card in a fixed plane (non-billboard beams).</summary>
        FlatCard = 2,
        /// <summary>A flat ring in a fixed plane (non-billboard rings, e.g. ground shockwaves): no soft fade, only the small edge-on fade.</summary>
        FlatRing = 3,
        /// <summary>A beam whose element has the data's <c>GodRayFade</c>: soft fade without offset, end fade, near fade at least 5 m.</summary>
        GodRay = 4,
        /// <summary>A god-ray beam that is also an axis billboard.</summary>
        GodRayAxis = 5,
    }

    /// <summary>Quads per blend mode and frame; further ones are dropped (and counted).</summary>
    public const int Capacity = 3072;
    const int Stride = 12 + 4 + 4;

    /// <summary>Which way the atlas stores rotated sprites (flag bit 0 of the rect): true = turned 90 degrees clockwise, false = counter-clockwise. **Provisional**: chosen so that the bolt tracer streak's bright head leads along its travel (both turns are 180 degrees apart).</summary>
    public static bool RotatedClockwise { get; set; } = true;

    /// <summary>M42: the soft-particle, near-camera and edge-on fades (on by default; <c>--fx-hard</c> draws the cards without them, for comparison).</summary>
    public static bool Fades { get; set; } = !Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--fx-hard");

    struct Quad
    {
        public Vector3 C, X, Y, Info;
        public Color Colour;
        public Vector4 Rect;
        public float Depth;
    }

    readonly Quad[] _add = new Quad[Capacity], _alpha = new Quad[Capacity];
    readonly int[] _order = new int[Capacity];
    readonly float[] _keys = new float[Capacity];
    readonly float[] _bufAdd = new float[Capacity * Stride], _bufAlpha = new float[Capacity * Stride];
    int _nAdd, _nAlpha;
    MultiMesh? _mmAdd, _mmAlpha;
    ShaderMaterial? _matAdd, _matAlpha;
    /// <summary>M44: the render layer of the effects' geometry (sprites, glow quads, casings): 2, which no decal projects onto (decals: layer 1 only).</summary>
    public const uint FxRenderLayer = 2;
    Vector3 _camPos;
    FxTexturePage? _page;

    public Texture2D? Atlas { get; private set; }
    /// <summary>Quads dropped this frame because a buffer was full.</summary>
    public int DroppedLastFrame { get; private set; }
    public int AdditiveLastFrame { get; private set; }
    public int AlphaLastFrame { get; private set; }
    public int DroppedTotal { get; private set; }
    int _dropped;

    const string ShaderBody = @"
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, fog_disabled, skip_vertex_transform, BLENDMODE;
uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform bool rot_cw = false;
uniform bool fades = true;
uniform bool additive = false;
uniform vec4 soft = vec4(0.3, 0.05, 2.5, 0.75);
uniform vec4 near_fade = vec4(0.35, 0.25, 10.0, 0.3);
uniform vec2 near_small = vec2(0.5, 4.0);
uniform vec2 edge = vec2(0.06, 0.45);
uniform float end_fade = 0.2;
uniform vec3 ground = vec3(1.6, 0.25, 0.8);
// fx/no-smoke: camera billboards are depth-tested at a plane pulled toward the camera (share of the half size, at most metres, kept from the camera; 0 = off).
uniform vec3 pull = vec3(0.5, 1.0, 0.5);
varying flat vec4 v_rect;
varying flat vec3 v_info;
varying flat vec3 v_normal;
varying flat float v_size;
// fx/no-smoke: the view depth of the card where it really is (before the pull); the near-camera fade reads it, so a pulled card fades as before.
varying float v_orig_d;
void vertex() {
    v_rect = INSTANCE_CUSTOM;
    v_info = MODEL_MATRIX[2].xyz;
    vec3 ax = MODEL_MATRIX[0].xyz;
    vec3 ay = MODEL_MATRIX[1].xyz;
    vec3 c = cross(ax, ay);
    v_normal = dot(c, c) > 1e-12 ? normalize(c) : vec3(0.0);
    float la = length(ax), lb = length(ay);
    // Fade size: the short side of a long thin quad (tracers, beams), else the long side. God rays (kind 4, 5) keep the long side, as reviewed.
    float vcode = floor(MODEL_MATRIX[2].z + 0.5);
    if (vcode > 31.5) vcode -= 32.0;   // feel/jump-pack: the ground-soft flag (below)
    bool god_ray = floor(vcode / 4.0 + 0.01) > 3.5;
    v_size = !god_ray && max(la, lb) > 3.0 * min(la, lb) ? min(la, lb) : max(la, lb);
    // The quad's vertex in the world (the Z column is data and the quad's z is 0, so it adds nothing).
    vec3 centre = MODEL_MATRIX[3].xyz;
    vec3 wp = centre + ax * VERTEX.x + ay * VERTEX.y;
    v_orig_d = -(VIEW_MATRIX * vec4(wp, 1.0)).z;
    // fx/no-smoke (ours): a camera-facing card is a flat plane through its centre, so the floor and its decals between the camera and that plane were drawn over
    // the card (lines on the floor showing through the smoke). The card is moved toward the camera by a share of its half size (at most pull.y m, never closer than
    // pull.z m to the camera) and shrunk by the same perspective, so it covers the same pixels but is depth-tested nearer: what lies inside the cloud is covered.
    if (floor(vcode / 4.0 + 0.01) < 0.5 && pull.x > 0.0) {
        vec3 to_cam = CAMERA_POSITION_WORLD - centre;
        float d = length(to_cam);
        float p = clamp(min(pull.x * 0.5 * max(la, lb), pull.y), 0.0, max(d - pull.z, 0.0));
        if (p > 0.0 && d > 1e-4) wp = centre + to_cam * (p / d) + (wp - centre) * ((d - p) / d);
    }
    VERTEX = (VIEW_MATRIX * vec4(wp, 1.0)).xyz;
    NORMAL = vec3(0.0, 0.0, 1.0);
}
vec2 sprite_to_atlas(vec2 s, vec4 r) {
    if (r.z >= 0.0) return r.xy + s * r.zw;
    vec2 q = rot_cw ? vec2(1.0 - s.y, s.x) : vec2(s.y, 1.0 - s.x);
    return r.xy + q * vec2(-r.z, r.w);
}
void fragment() {
    float u_raw = mix(v_info.x, v_info.y, UV.x);
    float code = floor(v_info.z + 0.5);
    // feel/jump-pack: +32 marks a ground effect's quad (pound, slash, landing dust): soft without the push, from smaller sprites up, over a longer distance,
    // so the ground, props and bodies never cut a hard line through its dust and smoke.
    bool ground_soft = code > 31.5;
    if (ground_soft) code -= 32.0;
    float mode = mod(code, 4.0);
    float kind = floor(code / 4.0 + 0.01);
    bool god = kind > 3.5;
    bool axis_bb = (kind > 0.5 && kind < 1.5) || kind > 4.5;
    bool flat_ring = kind > 2.5 && kind < 3.5;
    bool flat_card = (kind > 1.5 && kind < 3.5) || (god && !axis_bb);
    if (mode > 1.5 && (u_raw < 0.0 || u_raw > 1.0)) discard;
    vec2 a0 = sprite_to_atlas(vec2(u_raw, UV.y), v_rect);
    vec2 dx = dFdx(a0);
    vec2 dy = dFdy(a0);
    float u = (mode > 0.5 && mode < 1.5) ? fract(u_raw) : u_raw;
    vec4 t = textureGrad(atlas, sprite_to_atlas(vec2(u, UV.y), v_rect), dx, dy);
    float fade = 1.0;
    if (fades) {
        float frag_d = -VERTEX.z;
        if (!flat_ring) {
            // Forward+ (Vulkan): the depth texture holds NDC z directly; unproject to view space.
            float scene = texture(depth_tex, SCREEN_UV).r;
            vec4 p = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene, 1.0);
            float scene_d = -p.z / p.w;
            float soft_d = clamp(v_size * soft.x * (ground_soft ? ground.x : 1.0), soft.y, soft.z);
            float push = (god || ground_soft) ? 0.0 : soft.w * soft_d;
            float s = clamp((scene_d - frag_d + push) / soft_d, 0.0, 1.0);
            fade *= mix(1.0, s, god ? 1.0 : ground_soft ? smoothstep(ground.y, ground.z, v_size) : smoothstep(1.0, 2.5, v_size));
        }
        float full = clamp(v_size * near_fade.x, near_fade.y, near_fade.z);
        if (v_size < near_small.y) full = min(full, near_small.x);   // small sprites: the own muzzle flash in aim mode stays
        if (god) full = max(full, 5.0);                   // god rays: clear of the camera's first 1.5 m whatever their size
        // Additive glows (muzzle and blast light) never get it: they brighten, they do not cover the view with a slab.
        // fx/no-smoke: at the card's own depth, not the pulled one (the soft term above keeps the pulled plane).
        if (god || !additive) fade *= smoothstep(full * near_fade.w, full, v_orig_d);
        if (flat_card && dot(v_normal, v_normal) > 0.5) {
            vec3 n = normalize((VIEW_MATRIX * vec4(v_normal, 0.0)).xyz);
            float ndv = abs(dot(n, normalize(-VERTEX)));
            float lim = flat_ring ? edge.x : mix(edge.x, edge.y, smoothstep(1.0, 6.0, v_size));
            fade *= smoothstep(0.0, lim, ndv);
        }
        if (god && mode < 0.5) fade *= smoothstep(0.0, end_fade, u_raw) * (1.0 - smoothstep(1.0 - end_fade, 1.0, u_raw));
    }
    ALBEDO = t.rgb * COLOR.rgb;
    ALPHA = t.a * COLOR.a * fade;
}
";

    /// <summary>Loads the atlas texture (raw DDS bytes) and builds both multimeshes. Returns false (and says why) when the atlas cannot be decoded.</summary>
    public bool Initialize(FxTexturePage page, byte[]? atlasDds, Image? decoded, out string error)
    {
        _page = page;
        var image = decoded;
        if (image is null)
        {
            image = new Image();
            var err = atlasDds is null ? Error.FileNotFound : image.LoadDdsFromBuffer(atlasDds);
            if (err != Error.Ok) { error = $"atlas DDS: {err}"; return false; }
        }
        Atlas = ImageTexture.CreateFromImage(image);
        _matAdd = Material("blend_add");
        _matAlpha = Material("blend_mix");
        var quad = new QuadMesh { Size = Vector2.One };
        _mmAdd = NewMultiMesh(quad);
        _mmAlpha = NewMultiMesh(quad);
        var huge = new Aabb(new Vector3(-5000, -5000, -5000), new Vector3(10000, 10000, 10000));
        // M44: on render layer 2 (with the characters): the effects' decals (Godot Decals, cull mask 1) must not project onto the sprites, or a crack, crater
        // or pound ring under a dust cloud is painted onto the cloud's cards and shows as flat lines in front of the dust (the owner's playtest, 2026-10-28).
        AddChild(new MultiMeshInstance3D { Name = "AdditiveSprites", Multimesh = _mmAdd, MaterialOverride = _matAdd, CustomAabb = huge, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = FxRenderLayer });
        AddChild(new MultiMeshInstance3D { Name = "AlphaSprites", Multimesh = _mmAlpha, MaterialOverride = _matAlpha, CustomAabb = huge, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = FxRenderLayer });
        error = "";
        return true;
    }

    ShaderMaterial Material(string blend)
    {
        var shader = new Shader { Code = ShaderBody.Replace("BLENDMODE", blend) };
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("atlas", Atlas!);
        m.SetShaderParameter("rot_cw", RotatedClockwise);
        m.SetShaderParameter("fades", Fades);
        m.SetShaderParameter("additive", blend == "blend_add");
        m.SetShaderParameter("soft", new Vector4(SoftK, SoftMin, SoftMax, SoftPush));
        m.SetShaderParameter("near_small", new Vector2(NearSmallMax, NearSmallSize));
        m.SetShaderParameter("near_fade", new Vector4(NearK, NearMin, NearMax, NearZeroK));
        m.SetShaderParameter("edge", new Vector2(EdgeSmall, EdgeLarge));
        m.SetShaderParameter("end_fade", EndFade);
        m.SetShaderParameter("ground", new Vector3(GroundSoftScale, GroundSoftFrom, GroundSoftFull));
        // fx/no-smoke review S1: the additive material is never pulled.
        m.SetShaderParameter("pull", new Vector3(NoPull || blend == "blend_add" ? 0f : PullShare, PullMax, PullKeep));
        return m;
    }

    static MultiMesh NewMultiMesh(Mesh mesh) => new()
    {
        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
        UseColors = true, UseCustomData = true,
        Mesh = mesh, InstanceCount = Capacity, VisibleInstanceCount = 0,
    };

    /// <summary>Starts a frame: forgets last frame's quads.</summary>
    public void Begin(Vector3 cameraPosition)
    {
        _camPos = cameraPosition;
        _nAdd = _nAlpha = 0; _dropped = 0;
    }

    /// <summary>The atlas rect (in the shader's encoding: negative width = stored rotated) of cell <paramref name="cell"/> of the material's sprite sheet.</summary>
    public bool Sprite(FxMaterial material, int cell, out Vector4 rect)
    {
        rect = default;
        if (_page?.Find(material) is not { } r) return false;
        int tx = Math.Max(1, (int)MathF.Round(material.TileX)), ty = Math.Max(1, (int)MathF.Round(material.TileY));
        int count = tx * ty;
        cell = ((cell % count) + count) % count;
        int cx = cell % tx, cy = cell / tx;
        if (!r.Rotated)
            rect = new Vector4(r.X + r.Width * cx / tx, r.Y + r.Height * cy / ty, r.Width / tx, r.Height / ty);
        else if (RotatedClockwise)   // sprite (u, v) -> atlas (1 - v, u): sprite columns run down the atlas, rows run right to left
            rect = new Vector4(r.X + r.Width * (1f - (cy + 1f) / ty), r.Y + r.Height * cx / tx, -(r.Width / ty), r.Height / tx);
        else                          // sprite (u, v) -> atlas (v, 1 - u)
            rect = new Vector4(r.X + r.Width * cy / ty, r.Y + r.Height * (1f - (cx + 1f) / tx), -(r.Width / ty), r.Height / tx);
        return true;
    }

    /// <summary>
    /// Adds a quad: <paramref name="x"/> and <paramref name="y"/> are its full-size axes (the sprite's U runs along <paramref name="x"/>, V down
    /// along <paramref name="y"/>'s negative), <paramref name="u0"/>..<paramref name="u1"/> the sprite U range across the quad, <paramref name="mode"/>
    /// 0 plain, 1 wrap (repeat), 2 clamp (nothing outside 0..1), <paramref name="kind"/> what the quad is (for the M42 fades).
    /// </summary>
    public bool Add(bool additive, Vector3 centre, Vector3 x, Vector3 y, Color colour, Vector4 rect, float u0 = 0f, float u1 = 1f, int mode = 0,
                    QuadKind kind = QuadKind.CameraBillboard)
    {
        if (colour.A <= 0.003f) return true;
        mode = Math.Clamp(mode, 0, 3) + 4 * (int)kind + (SoftGround && Fades && !NoGroundSoft ? 32 : 0);
        if (additive)
        {
            if (_nAdd >= Capacity) { _dropped++; return false; }
            _add[_nAdd++] = new Quad { C = centre, X = x, Y = y, Colour = colour, Rect = rect, Info = new Vector3(u0, u1, mode) };
        }
        else
        {
            if (_nAlpha >= Capacity) { _dropped++; return false; }
            _alpha[_nAlpha++] = new Quad { C = centre, X = x, Y = y, Colour = colour, Rect = rect, Info = new Vector3(u0, u1, mode), Depth = (centre - _camPos).LengthSquared() };
        }
        return true;
    }

    public bool HasRoom => _nAdd < Capacity - 8 && _nAlpha < Capacity - 8;

    /// <summary>Ends the frame: writes the buffers.</summary>
    public void End()
    {
        DroppedLastFrame = _dropped; DroppedTotal += _dropped;
        AdditiveLastFrame = _nAdd; AlphaLastFrame = _nAlpha;
        if (_mmAdd is null || _mmAlpha is null) return;
        Fill(_mmAdd, _bufAdd, _add, _nAdd, sort: false);
        Fill(_mmAlpha, _bufAlpha, _alpha, _nAlpha, sort: true);
    }

    void Fill(MultiMesh mm, float[] buf, Quad[] quads, int n, bool sort)
    {
        if (n == 0) { mm.VisibleInstanceCount = 0; return; }
        if (sort)
        {
            for (int i = 0; i < n; i++) { _order[i] = i; _keys[i] = -quads[i].Depth; }
            Array.Sort(_keys, _order, 0, n);   // far first
        }
        for (int i = 0; i < n; i++)
        {
            ref readonly var q = ref quads[sort ? _order[i] : i];
            // Z column: the data (U range and mode); it multiplies a z of 0, so the quad stays flat.
            int o = i * Stride;
            buf[o + 0] = q.X.X; buf[o + 1] = q.Y.X; buf[o + 2] = q.Info.X; buf[o + 3] = q.C.X;
            buf[o + 4] = q.X.Y; buf[o + 5] = q.Y.Y; buf[o + 6] = q.Info.Y; buf[o + 7] = q.C.Y;
            buf[o + 8] = q.X.Z; buf[o + 9] = q.Y.Z; buf[o + 10] = q.Info.Z; buf[o + 11] = q.C.Z;
            buf[o + 12] = q.Colour.R; buf[o + 13] = q.Colour.G; buf[o + 14] = q.Colour.B; buf[o + 15] = q.Colour.A;
            buf[o + 16] = q.Rect.X; buf[o + 17] = q.Rect.Y; buf[o + 18] = q.Rect.Z; buf[o + 19] = q.Rect.W;
        }
        mm.Buffer = buf;
        mm.VisibleInstanceCount = n;
    }
}
