using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// The W2 half of the rig (client only, presentation only, **ours**): the Ash Squall's drifting ash (a sparse warning emitter in the buildup, a dense one at the peak, both leaning on the wind), Ember Wind's sparse
/// embers, the Thunderstorm's sky-only glow (a pale blue-white film on a half dome behind the world, additive, at most <see cref="WeatherPlan.GlowMaxAlpha"/>, pulsed by <see cref="WeatherPlan.CloudGlow"/>; with the full-flash setting up to <see cref="WeatherPlan.FullGlowMaxAlpha"/>; the screen's own lift is in the lighting grade), the procedural wind bed, thunder rumbles and the Ash Squall's short warning text.
/// </summary>
public sealed partial class WeatherRig
{
    CpuParticles3D? _ashWarn, _ashMain, _embers;
    StandardMaterial3D? _ashWarnMaterial, _ashMainMaterial, _emberMaterial, _glowMaterial;
    MeshInstance3D? _glow;
    AudioStreamPlayer? _wind;
    CanvasLayer? _warnLayer;
    Label? _warnLabel;
    AudioStreamWav?[]? _farThunder, _nearThunder;
    bool _fxActive;

    /// <summary>The sky glow's current alpha (0 when none), for the selftest and the log.</summary>
    public float GlowAlpha => _glowMaterial?.AlbedoColor.A ?? 0f;
    public bool AshFalling => (_ashMain is { Emitting: true }) || (_ashWarn is { Emitting: true });
    public bool Embering => _embers is { Emitting: true };
    public bool WindBlowing => _wind is { Playing: true };
    public bool WarningShown => _warnLabel is { Visible: true };
    public bool FxActive => _fxActive;

    void UpdateFx(WeatherState st, float i, float wind)
    {
        var id = st.Id;
        float yaw = WeatherPlan.WindYaw(id);
        var windDir = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));

        // ash (Ash Squall): the warning emitter sprinkles from the buildup, the dense one is the crosswind
        float ash = WeatherPlan.AshShare(st);
        bool warnOn = id == WeatherId.AshSquall && ash > 0.005f, mainOn = ash > 0.12f;   // the sparse emitter runs through the whole squall; the dense one adds to it at the peak
        if (warnOn) _ashWarn ??= BuildAsh("AshWarning", WeatherTrim.Count(Math.Max(AshWarnFloor, (int)(AshCount * 0.2f * QualityScale())), AshWarnFloor, Trim.Level), out _ashWarnMaterial);
        if (mainOn) _ashMain ??= BuildAsh("AshSquall", Count(AshCount, AshMainFloor), out _ashMainMaterial);
        float near = WeatherShelterMix.NearAlpha(Shelter);
        bool cover = Shelter >= WeatherShelterMix.NearOffAbove;   // under cover no ash or ember falls near the player (the fade runs first, then the emitters stop)
        ConfigureAsh(_ashWarn, _ashWarnMaterial, warnOn && !cover, Math.Clamp(ash * 6f, 0f, 1f), windDir, wind, near);
        ConfigureAsh(_ashMain, _ashMainMaterial, mainOn && !cover, ash, windDir, wind, near);

        // embers (Ember Wind, preview): sparse, lit by themselves, riding the wind and rising
        float ember = WeatherPlan.EmberDensity(id, i);
        bool embersOn = ember > 0.02f;
        if (embersOn) _embers ??= BuildEmbers();
        if (_embers is not null)
        {
            _embers.Emitting = embersOn && !cover;
            if (embersOn)
            {
                float speed = 2.5f + 4f * wind;
                _embers.Direction = windDir + Vector3.Up * 0.25f;
                _embers.InitialVelocityMin = speed * 0.8f;
                _embers.InitialVelocityMax = speed * 1.2f;
                if (_emberMaterial is not null) _emberMaterial.AlbedoColor = new Color(1f, 0.62f, 0.25f, (0.55f + 0.45f * ember) * near);
            }
        }

        // thunder's cloud glow: a pale film on the sky only (a half dome behind the world); never the screen
        float glowAlpha = 0f;
        if (id == WeatherId.Thunderstorm)
        {
            glowAlpha = WeatherPlan.GlowMaxAlpha * WeatherPlan.CloudGlow(st.Elapsed, st.Span);   // reduced: faint and slow
            if (FullFlash) glowAlpha = Math.Max(glowAlpha, WeatherPlan.FullGlowMaxAlpha * WeatherPlan.FullFlash(st.Elapsed, st.Span));   // full: the brighter, quicker pulse
        }
        if (glowAlpha > 0.0005f) EnsureGlow();
        if (_glow is not null)
        {
            _glow.Visible = glowAlpha > 0.0005f;
            if (_glowMaterial is not null) _glowMaterial.AlbedoColor = new Color(0.66f, 0.76f, 1f, glowAlpha);
        }

        // wind bed
        bool windOn = wind > 0.02f;
        if (windOn && _wind is null) { _wind = new AudioStreamPlayer { Name = "WindBed", Stream = WeatherSynth.Wind(), VolumeDb = SilentDb, Bus = AcquireBus() }; AddChild(_wind); }
        if (_wind is not null)
        {
            _wind.VolumeDb = windOn ? Mathf.Lerp(SilentDb, WindLoudestDb, Math.Clamp(wind, 0f, 1f)) + WeatherShelterMix.WindDb(Shelter) : SilentDb;
            if (windOn && !_wind.Playing) _wind.Play();
            else if (!windOn && _wind.Playing) _wind.Stop();
        }

        // the Ash Squall's warning text (a short cue beside the visible and audible ones; no critical cue is audio alone)
        bool warn = WeatherPlan.Warning(st);
        if (warn && _warnLabel is null) BuildWarning();
        if (_warnLabel is not null) _warnLabel.Visible = warn;

        _fxActive = AshFalling || Embering || (_glow is { Visible: true }) || windOn || warn || (_thunderPlayers > 0);
    }

    int _thunderPlayers;

    /// <summary>A rumble of thunder from the procedural synth (a far one at <see cref="FarThunderDb"/>, a near one at <see cref="NearThunderDb"/>); rate-limited by the plan (strikes at least 6.5 s apart), and each player frees itself.</summary>
    public void PlayRumble(uint seed, bool near, float extraDb = 0f)
    {
        var bank = near ? (_nearThunder ??= new AudioStreamWav?[2]) : (_farThunder ??= new AudioStreamWav?[3]);
        int k = (int)(seed % (uint)bank.Length);
        var stream = bank[k] ??= WeatherSynth.Thunder(unchecked((uint)(k * 7919 + 13)), near);
        var player = new AudioStreamPlayer { Name = near ? "ThunderNear" : "ThunderFar", Stream = stream, VolumeDb = Math.Min(near ? NearThunderDb : FarThunderDb, (near ? NearThunderDb : FarThunderDb) + extraDb) + WeatherShelterMix.ThunderDb(Shelter), Bus = AcquireBus() };
        AddChild(player);
        _thunderPlayers++;
        player.Finished += () => { _thunderPlayers = Math.Max(0, _thunderPlayers - 1); player.QueueFree(); };
        player.Play();
    }

    // ---- builders ----

    static GradientTexture2D? _dot;

    /// <summary>A soft round dot (white to transparent, radial), so flecks and embers are round, not square quads. Shared.</summary>
    static GradientTexture2D SoftDot()
    {
        if (_dot is not null) return _dot;
        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 1f, 1f));
        g.SetColor(1, new Color(1f, 1f, 1f, 0f));
        g.AddPoint(0.5f, new Color(1f, 1f, 1f, 0.9f));   // a solid core, a soft rim
        return _dot = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f), Width = 32, Height = 32 };
    }

    CpuParticles3D BuildAsh(string name, int amount, out StandardMaterial3D material)
    {
        material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.30f, 0.26f, 0.22f, 0.5f),
            AlbedoTexture = SoftDot(),
            DisableFog = false,
        };
        var p = new CpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = 3.0,
            Preprocess = 3.0,
            LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(20f, 4f, 20f),
            Direction = new Vector3(1f, -0.02f, 0f),
            Spread = 8f,
            Gravity = new Vector3(0f, -0.35f, 0f),
            InitialVelocityMin = 5f,
            InitialVelocityMax = 9f,
            ScaleAmountMin = 0.6f,
            ScaleAmountMax = 1.6f,
            Mesh = new QuadMesh { Size = new Vector2(0.14f, 0.14f), Material = material },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0f, 1.5f, 0f),
            VisibilityAabb = new Aabb(new Vector3(-40f, -10f, -40f), new Vector3(80f, 24f, 80f)),
            Emitting = false,
        };
        AddChild(p);
        return p;
    }

    void ConfigureAsh(CpuParticles3D? p, StandardMaterial3D? m, bool on, float share, Vector3 windDir, float wind, float nearAlpha = 1f)
    {
        if (p is null) return;
        p.Emitting = on;
        if (!on) return;
        p.Direction = new Vector3(windDir.X, -0.02f, windDir.Z);
        float speed = 4f + 10f * wind;
        p.InitialVelocityMin = speed * 0.7f;
        p.InitialVelocityMax = speed * 1.15f;
        if (m is not null) m.AlbedoColor = new Color(0.30f, 0.26f, 0.22f, (0.30f + 0.50f * Math.Clamp(share, 0f, 1f)) * nearAlpha);
    }

    CpuParticles3D BuildEmbers()
    {
        _emberMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(1f, 0.62f, 0.25f, 0.8f),
            AlbedoTexture = SoftDot(),
            VertexColorUseAsAlbedo = true,
            DisableFog = true,
        };
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 0.85f, 0.45f, 1f));
        ramp.SetColor(1, new Color(0.9f, 0.18f, 0.04f, 0f));
        var p = new CpuParticles3D
        {
            Name = "Embers",
            Amount = Count(EmberCount, EmberFloor),   // sparse on purpose: about 4 in 100 m3 at High
            Lifetime = 4.0,
            Preprocess = 4.0,
            LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(22f, 5f, 22f),
            Direction = new Vector3(1f, 0.25f, 0f),
            Spread = 18f,
            Gravity = new Vector3(0f, 0.25f, 0f),
            InitialVelocityMin = 3f,
            InitialVelocityMax = 5f,
            ScaleAmountMin = 0.5f,
            ScaleAmountMax = 1.4f,
            ColorRamp = ramp,
            Mesh = new QuadMesh { Size = new Vector2(0.16f, 0.16f), Material = _emberMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0f, 1.5f, 0f),
            VisibilityAabb = new Aabb(new Vector3(-40f, -10f, -40f), new Vector3(80f, 24f, 80f)),
            Emitting = false,
        };
        AddChild(p);
        return p;
    }

    void EnsureGlow()
    {
        if (_glow is not null) return;
        _glowMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Front,   // seen from inside
            AlbedoColor = new Color(0.66f, 0.76f, 1f, 0f),
            DisableFog = true,
            DisableReceiveShadows = true,
        };
        _glow = new MeshInstance3D
        {
            Name = "SkyGlow",
            Mesh = new SphereMesh { Radius = 120f, Height = 120f, IsHemisphere = true, RadialSegments = 24, Rings = 8, Material = _glowMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            ExtraCullMargin = 200f,
        };
        AddChild(_glow);
    }

    void BuildWarning()
    {
        _warnLayer = new CanvasLayer { Name = "WeatherWarning", Layer = 6 };
        _warnLabel = new Label { Text = "ASH SQUALL APPROACHING", Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        _warnLabel.AddThemeColorOverride("font_color", new Color(1f, 0.78f, 0.45f));
        _warnLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        _warnLabel.AddThemeConstantOverride("outline_size", 4);
        _warnLabel.AddThemeFontSizeOverride("font_size", 22);
        _warnLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _warnLabel.OffsetTop = 70f;
        _warnLabel.OffsetLeft = -300f;
        _warnLabel.OffsetRight = 300f;
        _warnLayer.AddChild(_warnLabel);
        AddChild(_warnLayer);
    }
}
