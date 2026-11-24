using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// The weather finish (owner brief 2026-11-23-series-36-weather-finish; client only, presentation only, **ours**, every number an estimate for a playtest; spec/behavior.md "Weather finish"):
/// <list type="bullet">
/// <item><b>Low Fog's ground fog</b>: soft, wide cards drifting on the ground round the player (a ring from <see cref="FogInnerM"/> to <see cref="FogOuterM"/>, their centres 0 to 1.8 m over the street), so the fog lies low across the
/// whole map instead of the old uniform depth haze. Cards fade out nearer than <see cref="FogFadeNearM"/> to <see cref="FogFadeFarM"/>, so a nearby silhouette is never inside a card; they fade under a roof like the other near layers.
/// The fog's base follows the ground under the player down at once and up slowly (<see cref="FogRiseMps"/>), so a balcony stands briefly over the street's fog.</item>
/// <item><b>Preset skies</b>: an overcast dome for Rain and a heavier, darker one for the Thunderstorm: a light procedural cloud texture (one shared seamless noise, drifting with the wind) drawn just in front of the map's own sky
/// and behind every piece of the world, in both renderers. It blends in and out over <see cref="SkyBlendSeconds"/> while the preset's other effects are already full. No cloud simulation; Clear, Low Fog, Ash Squall, Ember Wind and
/// None have no sky of their own (the map's).</item>
/// </list>
/// </summary>
public sealed partial class WeatherRig
{
    /// <summary>Fog cards at the High preset (the others scale them), the fewest at any preset or trim.</summary>
    public const int FogCount = 240, FogFloor = 50;
    /// <summary>The cards' ring round the player (m), a card's size (m), its life (s) and drift (m/s).</summary>
    public const float FogInnerM = 3f, FogOuterM = 38f, FogCardWidthM = 16f, FogCardHeightM = 2.4f, FogLifeSeconds = 14f, FogDriftMin = 0.25f, FogDriftMax = 0.7f;
    /// <summary>The cards' centres lie from the fog's base up to this height (m); the node sits at half of it.</summary>
    public const float FogBandM = 1.0f;
    /// <summary>A card is invisible nearer than <see cref="FogFadeNearM"/> and full from <see cref="FogFadeFarM"/>; the card's alpha at the preset's lull and at its full level.</summary>
    public const float FogFadeNearM = 2f, FogFadeFarM = 10f, FogAlphaLow = 0.13f, FogAlphaFull = 0.2f;
    /// <summary>Repair 1: the share of the scene's own haze colour in a card's colour (a card matches the map's air, not a white patch).</summary>
    public const float FogHazeShare = 0.6f;
    /// <summary>How fast the fog's base climbs after the ground under the player (m/s); it drops at once.</summary>
    public const float FogRiseMps = 0.4f;
    /// <summary>The sky's blend (s for a full change of its cover).</summary>
    public const double SkyBlendSeconds = 4.0;
    /// <summary>The cover (0 to 1) of the sky at a preset's lull and at its full level.</summary>
    public const float RainSkyLow = 0.72f, RainSkyFull = 0.9f, StormSkyLow = 0.85f, StormSkyFull = 1f;

    CpuParticles3D? _fog;
    StandardMaterial3D? _fogMaterial;
    float _fogBase = float.NaN;

    MeshInstance3D? _sky;
    ShaderMaterial? _skyMaterial;
    float _skyCover;
    WeatherId _skyLook = WeatherId.None;
    float _cloudOffsetX, _cloudOffsetZ;

    /// <summary>The ground fog is drawn now.</summary>
    public bool Fogging => _fog is { Emitting: true };
    /// <summary>The fog cards' alpha now (selftest), and the fog's base height (m, world).</summary>
    public float FogAlpha => _fogMaterial?.AlbedoColor.A ?? 0f;
    public float FogBase => _fogBase;
    /// <summary>The preset sky's cover now (0 when none), and the preset its colours belong to.</summary>
    public float SkyCover => _sky is { Visible: true } ? _skyCover : 0f;
    public bool SkyShowing => _skyCover > 0.001f;
    public WeatherId SkyLook => _skyLook;

    /// <summary>The preset skies are drawn only in Forward+ and Mobile (repair 1, the tester's Compatibility capture on Manufactorum: the dome covered indoor walls). In Compatibility the preset keeps its grade, rain and haze; the map's own sky shows, as for MapSky.</summary>
    public static bool SkyDrawn { get; } = RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";

    /// <summary>The cover a preset's sky aims at for <paramref name="intensity"/> (0 to 1; 0 for a preset with no sky of its own). Pure.</summary>
    public static float SkyTarget(WeatherId id, float intensity)
    {
        if (!SkyDrawn) return 0f;   // repair 1: no dome in the Compatibility renderer (its far-plane depth trick is not reliable there; the dome drew over geometry)
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        float k = Math.Clamp((i - WeatherClock.ActiveFloor) / (1f - WeatherClock.ActiveFloor), 0f, 1f);
        return id switch
        {
            WeatherId.Rain when i > 0f => RainSkyLow + (RainSkyFull - RainSkyLow) * k,
            WeatherId.Thunderstorm when i > 0f => StormSkyLow + (StormSkyFull - StormSkyLow) * k,
            _ => 0f,
        };
    }

    void UpdateFog(WeatherState st, float i, Vector3 cameraAt, float? groundY)
    {
        bool on = st.Id == WeatherId.LowFog && i > 0.02f;
        if (on) EnsureFog();
        if (_fog is null) return;
        bool cover = Shelter >= WeatherShelterMix.NearOffAbove;
        _fog.Emitting = on && !cover;
        if (!on) return;
        float ground = groundY ?? cameraAt.Y - 1.8f;
        float dt = (float)Math.Clamp(GetProcessDeltaTime(), 0.0, 0.25);
        if (float.IsNaN(_fogBase) || ground < _fogBase || Math.Abs(ground - _fogBase) > 30f) _fogBase = ground;
        else _fogBase = Math.Min(ground, _fogBase + FogRiseMps * dt);
        _fog.GlobalPosition = new Vector3(cameraAt.X, _fogBase + FogBandM * 0.5f, cameraAt.Z);
        float yaw = WeatherPlan.WindYaw(st.Id);
        _fog.Direction = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        if (_fogMaterial is not null)
        {
            float k = Math.Clamp((i - WeatherClock.ActiveFloor) / (1f - WeatherClock.ActiveFloor), 0f, 1f);
            var haze = SceneHaze();
            var tint = new Color(0.62f, 0.65f, 0.69f).Lerp(new Color(Math.Clamp(haze.R, 0.25f, 1f), Math.Clamp(haze.G, 0.25f, 1f), Math.Clamp(haze.B, 0.25f, 1f)), FogHazeShare);
            _fogMaterial.AlbedoColor = new Color(tint.R, tint.G, tint.B, (FogAlphaLow + (FogAlphaFull - FogAlphaLow) * k) * WeatherShelterMix.NearAlpha(Shelter));
        }
    }

    void EnsureFog()
    {
        if (_fog is not null) return;
        bool compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        _fogMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.66f, 0.69f, 0.73f, FogAlphaFull),
            AlbedoTexture = FogPuff(),
            VertexColorUseAsAlbedo = true,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = FogFadeNearM,
            DistanceFadeMaxDistance = FogFadeFarM,
            ProximityFadeEnabled = !compat,   // a soft line where a card meets the ground or a wall (the depth texture; the Compatibility renderer goes without)
            ProximityFadeDistance = 3f,
            DisableFog = false,
        };
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 1f, 1f, 0f));
        ramp.SetColor(1, new Color(1f, 1f, 1f, 0f));
        ramp.AddPoint(0.25f, new Color(1f, 1f, 1f, 1f));
        ramp.AddPoint(0.75f, new Color(1f, 1f, 1f, 1f));
        _fog = new CpuParticles3D
        {
            Name = "LowFog",
            Amount = Count(FogCount, FogFloor),
            Lifetime = FogLifeSeconds,
            Preprocess = FogLifeSeconds,   // full at once: no ramp from nothing
            LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up,
            EmissionRingHeight = FogBandM,
            EmissionRingRadius = FogOuterM,
            EmissionRingInnerRadius = FogInnerM,
            Direction = new Vector3(1f, 0f, 0f),
            Spread = 25f,
            Gravity = Vector3.Zero,
            InitialVelocityMin = FogDriftMin,
            InitialVelocityMax = FogDriftMax,
            ScaleAmountMin = 0.7f,
            ScaleAmountMax = 1.3f,
            ColorRamp = ramp,
            Mesh = new QuadMesh { Size = new Vector2(FogCardWidthM, FogCardHeightM), Material = _fogMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-50f, -6f, -50f), new Vector3(100f, 12f, 100f)),
            Emitting = false,
        };
        AddChild(_fog);
    }

    static GradientTexture2D? _puff;

    /// <summary>A very soft round puff (a near-Gaussian falloff, no plateau or rim; repair 1): many overlapping wide cards then read as one continuous low layer, not separate patches.</summary>
    static GradientTexture2D FogPuff()
    {
        if (_puff is not null) return _puff;
        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 1f, 1f));
        g.SetColor(1, new Color(1f, 1f, 1f, 0f));
        g.AddPoint(0.25f, new Color(1f, 1f, 1f, 0.72f));
        g.AddPoint(0.5f, new Color(1f, 1f, 1f, 0.34f));
        g.AddPoint(0.75f, new Color(1f, 1f, 1f, 0.09f));
        return _puff = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f), Width = 64, Height = 64 };
    }

    // ---- repair 1: the Ash Squall's blowing dust ----

    /// <summary>Dust sheets at the High preset and the fewest at any preset or trim; their ring (m), size (m), life (s); the near fade (m); alpha at the lull and at full.</summary>
    public const int DustCount = 130, DustFloor = 30;
    public const float DustHalfWidthM = 26f, DustBandM = 4f, DustCardWidthM = 12f, DustCardHeightM = 5f, DustLifeSeconds = 5f;
    public const float DustFadeNearM = 2.5f, DustFadeFarM = 9f, DustAlphaLow = 0.18f, DustAlphaFull = 0.34f;

    CpuParticles3D? _dust;
    StandardMaterial3D? _dustMaterial;

    /// <summary>The squall's dust is blowing now; its alpha (selftest).</summary>
    public bool Dusting => _dust is { Emitting: true };
    public float DustAlpha => _dustMaterial?.AlbedoColor.A ?? 0f;

    /// <summary>
    /// The Ash Squall's wind-driven dust (repair 1: at intensity 1 the squall read as a faint tint): wide soft ochre sheets in a ring round the player, 0 to 4 m over the ground, racing along the wind at (6 + 10 x wind) m/s,
    /// so the street-level air visibly moves and long sightlines shorten while near cover and enemies stay clear (the sheets fade out nearer than 3 to 12 m). Presentation only; none under a roof.
    /// </summary>
    void UpdateDust(WeatherState st, float i, float wind, Vector3 cameraAt, float? groundY)
    {
        bool on = st.Id == WeatherId.AshSquall && i > 0.02f;
        if (on) EnsureDust();
        if (_dust is null) return;
        bool cover = Shelter >= WeatherShelterMix.NearOffAbove;
        _dust.Emitting = on && !cover;
        if (!on) return;
        float ground = groundY ?? cameraAt.Y - 1.8f;
        float yaw = WeatherPlan.WindYaw(st.Id);
        var windDir = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        float speed = 6f + 10f * wind;
        _dust.GlobalPosition = new Vector3(cameraAt.X, ground + DustBandM * 0.5f, cameraAt.Z) - windDir * (speed * DustLifeSeconds * 0.5f);   // repair 2: upwind by half a life, so each sheet spends its life crossing the player's surroundings
        _dust.Direction = windDir;
        _dust.InitialVelocityMin = speed * 0.8f;
        _dust.InitialVelocityMax = speed * 1.2f;
        if (_dustMaterial is not null)
        {
            float k = Math.Clamp((i - WeatherClock.ActiveFloor) / (1f - WeatherClock.ActiveFloor), 0f, 1f);
            var haze = SceneHaze();
            var tint = new Color(0.80f, 0.70f, 0.55f).Lerp(new Color(Math.Clamp(haze.R, 0.25f, 1f), Math.Clamp(haze.G, 0.25f, 1f), Math.Clamp(haze.B, 0.25f, 1f)), 0.15f);   // repair 2: paler than the amber grade, so it reads over it
            _dustMaterial.AlbedoColor = new Color(tint.R, tint.G, tint.B, (DustAlphaLow + (DustAlphaFull - DustAlphaLow) * k) * WeatherShelterMix.NearAlpha(Shelter));
        }
    }

    void EnsureDust()
    {
        if (_dust is not null) return;
        bool compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        _dustMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.80f, 0.70f, 0.55f, DustAlphaFull),
            AlbedoTexture = FogPuff(),
            VertexColorUseAsAlbedo = true,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = DustFadeNearM,
            DistanceFadeMaxDistance = DustFadeFarM,
            ProximityFadeEnabled = !compat,
            ProximityFadeDistance = 2f,
            DisableFog = true,   // repair 2: the squall's own thick haze (fog x2.8) washed the sheets into the fog colour; they are the haze's moving part
        };
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 1f, 1f, 0f));
        ramp.SetColor(1, new Color(1f, 1f, 1f, 0f));
        ramp.AddPoint(0.3f, new Color(1f, 1f, 1f, 1f));
        ramp.AddPoint(0.7f, new Color(1f, 1f, 1f, 1f));
        _dust = new CpuParticles3D
        {
            Name = "AshDust",
            Amount = Count(DustCount, DustFloor),
            Lifetime = DustLifeSeconds,
            Preprocess = DustLifeSeconds,   // full at once
            LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(DustHalfWidthM, DustBandM * 0.5f, DustHalfWidthM),
            Direction = new Vector3(1f, 0f, 0f),
            Spread = 6f,
            Gravity = Vector3.Zero,
            InitialVelocityMin = 6f,
            InitialVelocityMax = 10f,
            ScaleAmountMin = 0.7f,
            ScaleAmountMax = 1.4f,
            ColorRamp = ramp,
            Mesh = new QuadMesh { Size = new Vector2(DustCardWidthM, DustCardHeightM), Material = _dustMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-90f, -6f, -90f), new Vector3(180f, 16f, 180f)),
            Emitting = false,
        };
        AddChild(_dust);
    }

    // ---- the preset sky ----

    void UpdateSky(WeatherState st, float i, float wind)
    {
        float target = SkyTarget(st.Id, i);
        if (target > 0f) { _skyLook = st.Id; EnsureSky(); }
        float dt = (float)Math.Clamp(GetProcessDeltaTime(), 0.0, 0.25);
        float step = (float)(dt / SkyBlendSeconds);
        _skyCover = _skyCover < target ? Math.Min(target, _skyCover + step) : Math.Max(target, _skyCover - step);
        if (_sky is null) return;
        _sky.Visible = _skyCover > 0.001f;
        if (_skyMaterial is null || !_sky.Visible) return;
        float yaw = WeatherPlan.WindYaw(_skyLook);
        float drift = (0.004f + 0.01f * wind) * dt;   // the cloud texture drifts with the wind, in texture units a second
        _cloudOffsetX = (_cloudOffsetX + MathF.Cos(yaw) * drift) % 1f;
        _cloudOffsetZ = (_cloudOffsetZ + MathF.Sin(yaw) * drift) % 1f;
        bool storm = _skyLook == WeatherId.Thunderstorm;
        _skyMaterial.SetShaderParameter("cover", _skyCover);
        _skyMaterial.SetShaderParameter("dark", storm ? new Color(0.13f, 0.15f, 0.19f) : new Color(0.36f, 0.39f, 0.44f));
        _skyMaterial.SetShaderParameter("light", storm ? new Color(0.30f, 0.32f, 0.38f) : new Color(0.60f, 0.63f, 0.68f));
        _skyMaterial.SetShaderParameter("offset", new Vector2(_cloudOffsetX, _cloudOffsetZ));
    }

    static Shader? _skyShader;
    static NoiseTexture2D? _clouds;

    void EnsureSky()
    {
        if (_sky is not null) return;
        _skyShader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
                uniform sampler2D clouds : filter_linear_mipmap, repeat_enable;
                uniform float cover = 0.0;
                uniform vec3 dark : source_color = vec3(0.36, 0.39, 0.44);
                uniform vec3 light : source_color = vec3(0.60, 0.63, 0.68);
                uniform vec2 offset = vec2(0.0);
                uniform float sky_z = 0.0000004;   // in front of the map's sky box (1e-7) and Godot's background, behind every piece of the world (reversed-Z: Forward+ and Mobile only; never built in Compatibility, repair 1)
                varying vec3 dir;
                void vertex() {
                    dir = VERTEX;
                    vec4 p = PROJECTION_MATRIX * vec4(mat3(VIEW_MATRIX) * VERTEX, 1.0);
                    POSITION = vec4(p.xy, sky_z * p.w, p.w);
                }
                void fragment() {
                    vec3 d = normalize(dir);
                    if (d.y < -0.08) discard;
                    vec2 uv = d.xz / (max(d.y, 0.0) + 0.18) * 0.22 + offset;
                    float n = texture(clouds, uv).r;
                    float n2 = texture(clouds, uv * 2.3 + vec2(0.37, 0.71)).r;
                    float c = clamp(n * 0.7 + n2 * 0.3, 0.0, 1.0);
                    ALBEDO = mix(dark, light, smoothstep(0.25, 0.85, c)) * mix(0.85, 1.0, smoothstep(0.0, 0.5, d.y));
                    float horizon = smoothstep(-0.08, 0.10, d.y);
                    ALPHA = clamp(cover * mix(0.78, 1.0, c) * mix(0.55, 1.0, horizon) * horizon, 0.0, 1.0);
                }
                """,
        };
        _clouds ??= new NoiseTexture2D
        {
            Width = 256, Height = 256, Seamless = true, GenerateMipmaps = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.012f, FractalOctaves = 4, Seed = 0x5C1 },
        };
        _skyMaterial = new ShaderMaterial { Shader = _skyShader, RenderPriority = -100 };   // drawn before the other weather layers (the thunder glow, the rain) so they lie over it
        _skyMaterial.SetShaderParameter("clouds", _clouds);
        _skyMaterial.SetShaderParameter("sky_z", 0.0000004f);   // reversed-Z (Forward+, Mobile); never built in Compatibility (SkyDrawn)
        _sky = new MeshInstance3D
        {
            Name = "WeatherSky",
            Mesh = new SphereMesh { Radius = 5f, Height = 10f, RadialSegments = 24, Rings = 12, Material = _skyMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_sky);
    }
}
