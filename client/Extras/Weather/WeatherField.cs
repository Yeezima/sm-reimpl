using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// Rain motion (series 37; client only, presentation only, **ours**): one precipitation layer as a fixed set of drops in a world-anchored field wrapped round the camera (<see cref="WeatherFieldWrap"/>), drawn as one MultiMesh whose
/// vertex shader places each drop (seed in the instance's custom data, the field's drift and box as uniforms). Replaces the CPU-particle emitters of the rain, the grit, the flakes and the embers: those spawned drops over the camera and
/// let them fall, so a camera moving as fast as the drops (a jump-pack fall, a drop off a ledge, a fast slide) outran them and the rain thinned or vanished (measured on mp_tdm_09, series 37). Here every drop is a fixed world
/// point between frames and the box round the camera always holds all of them, at any speed. Nothing is simulated on the CPU (the drift is one vector a frame), so it costs less than the emitters did.
/// <para>Kinds: a streak (the rain's axial billboard, soft across and tapered along its fall) or a dot (a camera-facing soft round fleck; additive for embers, which also twinkle and shade from yellow to deep red).</para>
/// </summary>
public sealed partial class WeatherField : MultiMeshInstance3D
{
    public enum Kind { Streak, Dot, Ember }

    readonly Vector3[] _seeds;
    readonly int[] _group;
    readonly ShaderMaterial _material;
    readonly Kind _kind;
    readonly bool _grouped;
    readonly Vector3[] _drift = new Vector3[WeatherFieldWrap.Groups];
    readonly Vector3[] _driftUniform = new Vector3[WeatherFieldWrap.Groups];
    bool _on;
    float _level, _holeNow, _ringNow = RingOff, _holeTarget, _ringTarget = RingOff, _keep = 1f;

    /// <summary>A ring this far out (m) cuts nothing (the far box's corners are 57 m out); the radius the ring eases from and back to.</summary>
    public const float RingOff = 60f;

    /// <summary>The box round the camera (m, full size) this layer's drops live in.</summary>
    public Vector3 Box { get; }
    /// <summary>The velocity the field moves at (m/s; the fall, or the wind), set by the rig each frame.</summary>
    public Vector3 Velocity { get; set; }
    /// <summary>The material (tint, fall direction) the rig sets.</summary>
    public ShaderMaterial Material => _material;

    /// <summary>
    /// Whether the layer is wanted (the old emitters' switch). Repair: switching it fades every drop in or out over <see cref="WeatherFieldWrap.FadeSeconds"/> (the field keeps moving meanwhile) and the node hides only at 0, so a layer
    /// never pops; the getter is the wish, so the rig's state reads change at once.
    /// </summary>
    public bool Emitting { get => _on; set => _on = value; }
    /// <summary>The fade level now (0 hidden, 1 full).</summary>
    public float Level => _level;
    /// <summary>Fading in or out now (the rig stays while one does).</summary>
    public bool Fading => _level != (_on ? 1f : 0f);

    /// <summary>The drops drawn now (the trim's count; at most the capacity it was built with).</summary>
    public int Amount
    {
        get => Multimesh.VisibleInstanceCount;
        set => Multimesh.VisibleInstanceCount = Math.Clamp(value, 0, Multimesh.InstanceCount);
    }

    /// <summary>Repair: the share of the drawn drops shown (the flakes' emitter share); the last drops fade out over a soft edge in the shader instead of popping when the share moves.</summary>
    public float Keep
    {
        get => _keep;
        set { _keep = Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f); SetKeep(); }
    }

    /// <summary>The keep share's soft edge (drops): a tenth of the drawn drops, at least 8.</summary>
    float KeepSoft => Math.Max(8f, 0.1f * Amount);

    float _keepSent = float.NaN, _keepSoftSent = float.NaN;

    /// <summary>The keep cut in drops (<see cref="WeatherFieldWrap.KeepCut"/>): the soft edge centred on the share (repair 2), sliding up by half an edge over the last soft / amount of share so it is continuous through share 1 (repair 3).</summary>
    void SetKeep()
    {
        float soft = KeepSoft, keep = WeatherFieldWrap.KeepCut(_keep, Amount, soft);   // repair 3: continuous through share 1 (at 1 every drop is full)
        if (keep != _keepSent) { _material.SetShaderParameter(PKeep, keep); _keepSent = keep; }
        if (soft != _keepSoftSent) { _material.SetShaderParameter(PKeepSoft, soft); _keepSoftSent = soft; }
    }

    // repair 2: the shader parameters' names, made once (a string key allocates a StringName per call)
    static readonly StringName PKeep = "keep", PKeepSoft = "keep_soft", PFade = "fade", PHole = "hole", PRing = "ring";
    static readonly StringName[] PDrift = { "drift0", "drift1", "drift2", "drift3" };   // repair 3: one vec3 a group (no array marshalled each frame)

    /// <summary>For the engine only (a node type needs a parameterless constructor); the rig always uses the full one.</summary>
    public WeatherField() : this("WeatherField", Kind.Streak, 1, 0, Vector3.One, Vector2.One, 1f, 1f, 1f, 0f, 0) { }

    /// <param name="capacity">Drops allocated (the most it can show); <paramref name="amount"/> are shown at first.</param>
    /// <param name="size">The quad (width, length) for a streak, (diameter, diameter) for a dot (m).</param>
    /// <param name="scaleMin">Per-drop size variance.</param>
    /// <param name="grouped">Repair: the drops move in <see cref="WeatherFieldWrap.Groups"/> drift groups (the flakes and embers); otherwise all move as one (the rain and grit, which fall at one speed).</param>
    public WeatherField(string name, Kind kind, int capacity, int amount, Vector3 box, Vector2 size, float scaleMin, float scaleMax, float nearFadeM, float nearFullM, int seed, bool grouped = false)
    {
        Name = name;
        Box = box;
        _kind = kind;
        _grouped = grouped;
        _material = new ShaderMaterial { Shader = ShaderFor(kind) };
        _material.SetShaderParameter("box", box);
        _material.SetShaderParameter("near_fade", nearFadeM);
        _material.SetShaderParameter("near_full", nearFullM);
        var mesh = new QuadMesh { Size = size, Material = _material };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = mesh,
            InstanceCount = Math.Max(1, capacity),
        };
        _seeds = new Vector3[mm.InstanceCount];
        _group = new int[mm.InstanceCount];
        var rng = new Random(seed);
        for (int i = 0; i < mm.InstanceCount; i++)
        {
            var s = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            _seeds[i] = s;
            float scale = scaleMin + (scaleMax - scaleMin) * (float)rng.NextDouble();
            int g = grouped ? i % WeatherFieldWrap.Groups : 0;   // the seeds are random, so every group is a uniform share of the box
            _group[i] = g;
            mm.SetInstanceTransform(i, Transform3D.Identity);
            mm.SetInstanceCustomData(i, new Color(s.X, s.Y, s.Z, scale + 10f * g));   // the group rides in the tens of the scale (scales are under 10)
        }
        mm.CustomAabb = new Aabb(-box * 0.5f - Vector3.One * 2f, box + Vector3.One * 4f);   // the shader moves every drop within the box round this node
        mm.VisibleInstanceCount = Math.Clamp(amount, 0, mm.InstanceCount);
        Multimesh = mm;
        CastShadow = ShadowCastingSetting.Off;
        Visible = false;
        _material.SetShaderParameter("fade", 0f);
        _material.SetShaderParameter("hole", 0f);
        _material.SetShaderParameter("ring", RingOff);
        _material.SetShaderParameter("keep", 1e9f);
        _material.SetShaderParameter("keep_soft", 8f);
    }

    /// <summary>
    /// One frame of <paramref name="dt"/> real seconds, every frame the rig lives: the field (each drift group) moves by its velocity (kept inside one box), the switch's fade and the hole's and ring's radii ease, and the node
    /// shows only while its fade is above 0.
    /// </summary>
    public void Advance(double dt)
    {
        var box = new System.Numerics.Vector3(Box.X, Box.Y, Box.Z);
        var v = new System.Numerics.Vector3(Velocity.X, Velocity.Y, Velocity.Z);
        int groups = _grouped ? WeatherFieldWrap.Groups : 1;
        for (int g = 0; g < groups; g++)
        {
            var d0 = _drift[g];
            var d = WeatherFieldWrap.Advance(new System.Numerics.Vector3(d0.X, d0.Y, d0.Z), _grouped ? WeatherFieldWrap.GroupVelocity(v, g) : v, dt, box);
            _drift[g] = new Vector3(d.X, d.Y, d.Z);
            _driftUniform[g] = _drift[g];
        }
        _level = WeatherFieldWrap.Ease(_level, _on ? 1f : 0f, 1f, dt);
        if (_holeNow != _holeTarget || _ringNow != _ringTarget)
        {
            _holeNow = WeatherFieldWrap.Ease(_holeNow, _holeTarget, HoleSpan, dt);
            _ringNow = WeatherFieldWrap.Ease(_ringNow, _ringTarget, RingOff - FarRingNear, dt);
        }
        Visible = _level > 0f;
        if (!Visible) return;   // repair 2: a hidden field sets nothing (its drift still moves, so it comes back where it would be)
        for (int g = 0; g < (_grouped ? WeatherFieldWrap.Groups : 1); g++) _material.SetShaderParameter(PDrift[g], _driftUniform[g]);
        if (_level != _fadeSent) { _material.SetShaderParameter(PFade, _level); _fadeSent = _level; }
        if (_holeNow != _holeSent) { _material.SetShaderParameter(PHole, _holeNow); _holeSent = _holeNow; }
        if (_ringNow != _ringSent) { _material.SetShaderParameter(PRing, _ringNow); _ringSent = _ringNow; }
        SetKeep();
    }

    float _fadeSent = 0f, _holeSent = 0f, _ringSent = RingOff;

    /// <summary>Weather fidelity: the install's sprite this field draws (null: the procedural profile), and the shader tier (High: the depth soft fade over <paramref name="softM"/> m). Not for the embers (kept as they were).</summary>
    public void UseSprite(WeatherSprites.Sprite? sprite)
    {
        if (_kind == Kind.Ember) return;
        _material.SetShaderParameter("use_sprite", sprite is null ? 0f : 1f);
        if (sprite is null) return;
        _material.SetShaderParameter("sprite", sprite.Texture);
        _material.SetShaderParameter("tiles", new Vector2(sprite.TilesX, sprite.TilesY));
    }

    /// <summary>Whether the field draws the High tier's shader now.</summary>
    public bool High { get; private set; }
    /// <summary>Whether the field draws an install sprite now.</summary>
    public bool Sprited => _material.GetShaderParameter("use_sprite").AsSingle() > 0.5f;

    public void SetTier(bool high, float softM)
    {
        if (_kind == Kind.Ember || (high == High && _material.Shader is not null && _tierSet)) return;
        _tierSet = true;
        High = high;
        _material.Shader = ShaderFor(_kind, high);
        _material.SetShaderParameter("soft_m", softM);
    }

    bool _tierSet;

    /// <summary>Test seam (selftest): ends the switch's fade and the radii's eases at once.</summary>
    public void Settle()
    {
        _level = _on ? 1f : 0f; _holeNow = _holeTarget; _ringNow = _ringTarget;
        Advance(0);
    }

    /// <summary>
    /// The far hole's soft edge (m): a drop fades out over the 2 m inside the hole's radius, so the rig asks for W4's dry 14 m plus this (repair 2, the lead's decision: no far drop within 14 m, as before), and the eases' paces (m):
    /// the hole's full radius and the ring's near radius, so each takes <see cref="WeatherFieldWrap.FadeSeconds"/>.
    /// </summary>
    public const float HoleSoftM = 2f;
    const float HoleSpan = 16f, FarRingNear = 44f;

    /// <summary>
    /// The far rain's dry ring under cover: no drop within <paramref name="holeM"/> of the centre horizontally nor beyond <paramref name="ringM"/>; a hole of 0 with the ring at <see cref="RingOff"/> keeps the whole box. Repair: the
    /// radii ease to these over <see cref="WeatherFieldWrap.FadeSeconds"/> instead of switching, so the streaks round the player fade out and back in.
    /// </summary>
    public void SetHole(float holeM, float ringM)
    {
        _holeTarget = Math.Max(0f, holeM);
        _ringTarget = ringM;
    }

    /// <summary>The hole's target radius (m; 0 in the open) and its radius now.</summary>
    public float HoleM => _holeTarget;
    public float HoleNowM => _holeNow;

    /// <summary>
    /// Test aid (the density log, the selftest): the drops within <paramref name="radius"/> of <paramref name="at"/> COMPUTED by the shader's arithmetic mirrored in C# (<see cref="WeatherFieldWrap"/>; not read back from the GPU), not faded
    /// out at a face, in the hole or past the keep share. That the shader draws them is checked by eye in windowed stills.
    /// </summary>
    public int CountWithin(Vector3 at, float radius)
    {
        if (!Visible) return 0;
        var c = GlobalPosition;
        var centre = new System.Numerics.Vector3(c.X, c.Y, c.Z);
        var box = new System.Numerics.Vector3(Box.X, Box.Y, Box.Z);
        var p = new System.Numerics.Vector3(at.X, at.Y, at.Z);
        int n = 0, shown = Amount;
        float cut = WeatherFieldWrap.KeepCut(_keep, Amount, KeepSoft), soft = KeepSoft;
        float r2 = radius * radius;
        for (int i = 0; i < shown; i++)
        {
            var dg = _drift[_group[i]];
            var w = WeatherFieldWrap.Place(new System.Numerics.Vector3(_seeds[i].X, _seeds[i].Y, _seeds[i].Z), box, new System.Numerics.Vector3(dg.X, dg.Y, dg.Z), centre);
            if (System.Numerics.Vector3.DistanceSquared(w, p) > r2) continue;
            if (WeatherFieldWrap.EdgeFade(w, centre, box) * WeatherFieldWrap.HoleFade(w, centre, _holeNow, _ringNow) * WeatherFieldWrap.KeepAlpha(i, cut, soft) < 0.5f) continue;
            n++;
        }
        return n;
    }

    // ---- the shaders: the arithmetic of WeatherFieldWrap.Place, EdgeFade and HoleFade per vertex ----

    static Shader? _streak, _dot, _ember, _streakHigh, _dotHigh;

    static Shader ShaderFor(Kind kind, bool high = false) => kind switch
    {
        Kind.Streak => high ? _streakHigh ??= new Shader { Code = TexCode(StreakRender, StreakVertex, StreakTexFragment, true) } : _streak ??= new Shader { Code = TexCode(StreakRender, StreakVertex, StreakTexFragment, false) },
        Kind.Dot => high ? _dotHigh ??= new Shader { Code = TexCode(DotRender, DotVertex, DotTexFragment, true) } : _dot ??= new Shader { Code = TexCode(DotRender, DotVertex, DotTexFragment, false) },
        _ => _ember ??= new Shader { Code = Code(EmberRender, DotVertex, EmberFragment) },
    };

    /// <summary>Test seam (selftest): the shader source a kind compiles (the ember's is pinned unchanged by WeatherEmberPinTests).</summary>
    public static string ShaderCode(Kind kind, bool high = false) => ShaderFor(kind, high).Code;

    /// <summary>
    /// Weather fidelity (owner 2026-11-25): the rain's and the ash's shader is the rain-motion template (<see cref="Code"/>, kept as it was for the embers) plus the install's own sprite (<see cref="WeatherSprites"/>; <c>use_sprite</c> 0
    /// keeps the procedural profile) and, on the High preset outside the Compatibility renderer, a soft-particle fade against the depth buffer, so a drop or a flake meeting a wall, the ground or a body fades over <c>soft_m</c> instead
    /// of cutting a line. The same placement, fades and keep cut as <see cref="Code"/>.
    /// </summary>
    static string TexCode(string render, string vertexTail, string fragment, bool high)
    {
        const string Extra = """
            uniform sampler2D sprite : source_color, filter_linear_mipmap, repeat_disable;
            uniform float use_sprite = 0.0;
            uniform vec2 tiles = vec2(1.0);
            uniform float soft_m = 0.5;

            """;
        const string HighExtra = """
            uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
            float soft_fade(vec2 suv, float fz, mat4 inv_proj) {
                float scene = texture(depth_tex, suv).r;
                vec4 p = inv_proj * vec4(suv * 2.0 - 1.0, scene, 1.0);
                vec4 q = inv_proj * vec4(suv * 2.0 - 1.0, fz, 1.0);
                return clamp(((-p.z / p.w) - (-q.z / q.w)) / soft_m, 0.0, 1.0);
            }

            """;
        const string LowExtra = """
            float soft_fade(vec2 suv, float fz, mat4 inv_proj) { return 1.0; }

            """;
        string code = Code(render, vertexTail, fragment);
        const string At = "void vertex() {";
        return code.Replace(At, Extra + (high ? HighExtra : LowExtra) + At);
    }

    /// <summary>The shader tier a field uses: High (the depth soft fade) on the High preset outside the Compatibility renderer and safe mode; everything else stays at the rain-motion cost.</summary>
    public static bool HighTier => !SafeMode.Active && MapLightingView.Preset == MapLightingView.LightingPreset.High && RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";

    static string Code(string render, string vertexTail, string fragment) => $$"""
        shader_type spatial;
        {{render}}
        uniform vec4 tint : source_color = vec4(0.74, 0.80, 0.90, 0.3);
        uniform vec3 fall_dir = vec3(0.0, -1.0, 0.0);
        uniform vec3 box = vec3(24.0);
        uniform vec3 drift0;
        uniform vec3 drift1;
        uniform vec3 drift2;
        uniform vec3 drift3;
        uniform float fade = 1.0;
        uniform float keep = 1e9;
        uniform float keep_soft = 8.0;
        uniform float near_fade = 1.2;
        uniform float near_full = 0.0;
        uniform float hole = 0.0;
        uniform float ring = 0.0;
        varying float v_fade;
        varying float v_hash;
        float axis_fade(float d, float size) {
            float q = abs(d) / (size * 0.5);
            return 1.0 - smoothstep(1.0 - {{WeatherFieldWrap.EdgeShare.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)}}, 1.0, q);
        }
        void vertex() {
            vec3 centre = MODEL_MATRIX[3].xyz;
            vec3 lo = centre - box * 0.5;
            vec3 seed = INSTANCE_CUSTOM.xyz;
            float grp = floor(INSTANCE_CUSTOM.w / 10.0);
            float sc = INSTANCE_CUSTOM.w - grp * 10.0;
            vec3 drift = grp < 0.5 ? drift0 : (grp < 1.5 ? drift1 : (grp < 2.5 ? drift2 : drift3));
            vec3 rel = mod(seed * box + drift - lo, box);
            vec3 c = lo + rel;
            vec3 d = c - centre;
            float r = length(d.xz);
            float edge = axis_fade(d.x, box.x) * axis_fade(d.y, box.y) * axis_fade(d.z, box.z) * smoothstep(hole - 2.0, hole, r) * (1.0 - smoothstep(ring - 2.0, ring, r));
            edge *= fade * clamp((keep + 0.5 * keep_soft - float(INSTANCE_ID)) / keep_soft, 0.0, 1.0);
            v_hash = fract(sin(dot(seed, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
            vec3 to_cam = CAMERA_POSITION_WORLD - c;
            float dist = max(length(to_cam), 1e-4);
            float near = near_full > near_fade ? smoothstep(near_fade, near_full, dist) : smoothstep(0.35, 0.35 + near_fade, dist);
            v_fade = edge * near;
            {{vertexTail}}
            POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(world, 1.0));
        }
        void fragment() {
            {{fragment}}
        }
        """;

    const string StreakRender = "render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;";
    const string DotRender = "render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;";
    const string EmberRender = "render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;";

    /// <summary>The axial billboard of the weather finish: the quad turned about the fall direction to face the camera.</summary>
    const string StreakVertex = """
            vec3 axis = normalize(fall_dir);
            vec3 side = cross(axis, to_cam / dist);
            float sl = length(side);
            side = sl > 1e-3 ? side / sl : vec3(1.0, 0.0, 0.0);
            vec3 world = c + side * (VERTEX.x * sc) - axis * (VERTEX.y * sc);
        """;
    /// <summary>
    /// The streak (weather fidelity: with the install's sprite). The procedural soft profile stays the envelope (soft across, tapered along: no hard sprite edge); the sprite (a cell picked per drop) breaks it up across and along
    /// (the rain's drops beam: a run of droplets smeared by the shutter; the ash: a flake smeared along the wind) and shades it a little; <c>soft_fade</c> softens it against geometry on High. Without a sprite: the rain-motion streak.
    /// </summary>
    const string StreakTexFragment = """
            float x = (UV.x - 0.5) * 2.0;
            float across = exp(-x * x * 3.5);
            float along = mix(0.25, 1.0, smoothstep(0.0, 0.85, UV.y)) * (1.0 - smoothstep(0.9, 1.0, UV.y)) * smoothstep(0.0, 0.08, UV.y);
            float shade = 1.0;
            if (use_sprite > 0.5) {
                float n = tiles.x * tiles.y;
                float cell = floor(fract(v_hash * 7.13) * n);
                vec2 cuv = (vec2(mod(cell, tiles.x), floor(cell / tiles.x)) + clamp(UV, vec2(0.02), vec2(0.98))) / tiles;
                vec4 t = texture(sprite, cuv);
                float lum = clamp(dot(t.rgb, vec3(0.3333)), 0.0, 1.0);
                across *= mix(0.35, 1.0, clamp(t.a * 1.4, 0.0, 1.0));
                shade = mix(0.85, 1.12, lum);
            }
            ALBEDO = tint.rgb * shade;
            ALPHA = clamp(tint.a * across * along * v_fade * soft_fade(SCREEN_UV, FRAGCOORD.z, INV_PROJECTION_MATRIX), 0.0, 1.0);
        """;

    /// <summary>A camera-facing quad (the old particle billboard).</summary>
    const string DotVertex = """
            vec3 world = c + (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * sc;
        """;
    /// <summary>
    /// The flake (weather fidelity: with the install's ash sprite): a cell of the sheet per flake, turning slowly in the view plane and tumbling (its width breathing with a slow cosine: a flat flake seen edge-on), its own shading
    /// kept a little; the quad's rim fades so no sprite edge shows; <c>soft_fade</c> on High. Without a sprite: the soft round dot of the old SoftDot texture (a solid core, 1 to 0.9 at half the radius, a soft rim to 0).
    /// </summary>
    const string DotTexFragment = """
            float r = length(UV - vec2(0.5)) * 2.0;
            float prof = r < 0.5 ? mix(1.0, 0.9, r / 0.5) : mix(0.9, 0.0, clamp((r - 0.5) / 0.5, 0.0, 1.0));
            float shade = 1.0;
            if (use_sprite > 0.5) {
                float rate = (0.5 + 1.2 * fract(v_hash * 3.71)) * (fract(v_hash * 11.3) < 0.5 ? -1.0 : 1.0);
                float ang = v_hash * 6.2832 + TIME * rate;
                float flip = max(0.3, abs(cos(TIME * (0.9 + fract(v_hash * 5.17)) + v_hash * 17.0)));
                vec2 q = mat2(vec2(cos(ang), sin(ang)), vec2(-sin(ang), cos(ang))) * (UV - vec2(0.5)) * 1.6;   // the scrap fills the middle 0.6 of the quad: a flake, not a sheet of paper
                q.x /= flip;
                q += vec2(0.5);
                float inside = step(0.0, q.x) * step(q.x, 1.0) * step(0.0, q.y) * step(q.y, 1.0);
                float n = tiles.x * tiles.y;
                float cell = floor(fract(v_hash * 7.13) * n);
                vec2 cuv = (vec2(mod(cell, tiles.x), floor(cell / tiles.x)) + clamp(q, vec2(0.02), vec2(0.98))) / tiles;
                vec4 t = texture(sprite, cuv);
                prof = inside * t.a * (1.0 - smoothstep(0.8, 1.0, r)) * mix(0.6, 1.0, flip);
                shade = mix(0.6, 1.0, clamp(dot(t.rgb, vec3(0.3333)), 0.0, 1.0));
            }
            ALBEDO = tint.rgb * shade;
            ALPHA = clamp(tint.a * prof * v_fade * soft_fade(SCREEN_UV, FRAGCOORD.z, INV_PROJECTION_MATRIX), 0.0, 1.0);
        """;
    /// <summary>Embers: the old life ramp (pale yellow to deep red, fading) run as a slow per-ember cycle (about 4 s, the old life), faded in at its start so an ember never pops.</summary>
    const string EmberFragment = """
            float r = length(UV - vec2(0.5)) * 2.0;
            float prof = r < 0.5 ? mix(1.0, 0.9, r / 0.5) : mix(0.9, 0.0, clamp((r - 0.5) / 0.5, 0.0, 1.0));
            float life = fract(v_hash * 7.31 + TIME * 0.25);
            vec3 ramp = mix(vec3(1.0, 0.85, 0.45), vec3(0.9, 0.18, 0.04), life);
            float a = smoothstep(0.0, 0.15, life) * (1.0 - life);
            ALBEDO = tint.rgb * ramp;
            ALPHA = clamp(tint.a * prof * a * v_fade, 0.0, 1.0);
        """;
}
