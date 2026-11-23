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
    StandardMaterial3D? _ashWarnMaterial, _emberMaterial, _glowMaterial;
    ShaderMaterial? _ashMainMaterial;
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
        if (warnOn) _ashWarn ??= BuildFlakes(WeatherTrim.Count(Math.Max(AshWarnFloor, (int)(AshCount * 0.2f * QualityScale())), AshWarnFloor, Trim.Level));
        if (mainOn) _ashMain ??= BuildGrit(Count(AshCount, AshMainFloor));
        float near = WeatherShelterMix.NearAlpha(Shelter);
        bool cover = Shelter >= WeatherShelterMix.NearOffAbove;   // under cover no ash or ember falls near the player (the fade runs first, then the emitters stop)
        ConfigureFlakes(warnOn && !cover, ash, windDir, wind, near);
        ConfigureGrit(mainOn && !cover, ash, windDir, wind, near);

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
            if (windOn)
            {
                _windFade.Cancel();
                // repair 2: the squall's and the storm's wind follow their level like the hiss (4 to 6 dB down at the lull); Ember Wind keeps its W2 law (its look and role are kept)
                float bed = id == WeatherId.EmberWind ? Mathf.Lerp(SilentDb, WindLoudestDb, Math.Clamp(wind, 0f, 1f)) : WeatherPlan.FollowDb(wind, WindLoudestDb, SilentDb);
                _wind.VolumeDb = bed + WeatherShelterMix.WindDb(Shelter);
                if (!_wind.Playing) _wind.Play();
            }
            else if (_wind.Playing)
            {
                _windFade.Start(_wind.VolumeDb);   // repair 2: a 0.5 s fade to silence, then the stop
                _wind.VolumeDb = _windFade.Step(RealDt, out bool done);
                if (done) _wind.Stop();
            }
        }

        // the Ash Squall's warning text (a short cue beside the visible and audible ones; no critical cue is audio alone)
        bool warn = WeatherPlan.Warning(st);
        if (warn && _warnLabel is null) BuildWarning();
        if (_warnLabel is not null) _warnLabel.Visible = warn;

        _fxActive = AshFalling || Embering || (_glow is { Visible: true }) || windOn || warn || (_thunderPlayers > 0);
    }

    int _thunderPlayers;
    readonly System.Collections.Generic.List<(AudioStreamPlayer, float)> _rumbles = new();   // the procedural rumbles still sounding, with their own level (weather fix 5)
    float _thunderFadeDb;

    /// <summary>Weather fix 5: the rumbles sounding now are <paramref name="db"/> below their own level (the fade on a switch to None); 0 restores.</summary>
    public void FadeThunder(float db)
    {
        _thunderFadeDb = db;
        foreach (var (p, b) in _rumbles) if (GodotObject.IsInstanceValid(p)) p.VolumeDb = b + db;
    }

    /// <summary>Weather fix 5: every rumble stops at once and is freed (the end of the fade); the rig may then be freed as usual.</summary>
    public void StopThunder()
    {
        foreach (var (p, _) in _rumbles) if (GodotObject.IsInstanceValid(p)) { p.Stop(); p.QueueFree(); }
        _rumbles.Clear();
        _thunderPlayers = 0;
        _thunderFadeDb = 0f;
    }

    /// <summary>A rumble of thunder from the procedural synth (a far one at <see cref="FarThunderDb"/>, a near one at <see cref="NearThunderDb"/>); rate-limited by the plan (strikes at least 6.5 s apart), and each player frees itself.</summary>
    public void PlayRumble(uint seed, bool near, float extraDb = 0f)
    {
        var bank = near ? (_nearThunder ??= new AudioStreamWav?[2]) : (_farThunder ??= new AudioStreamWav?[3]);
        int k = (int)(seed % (uint)bank.Length);
        var stream = bank[k] ??= WeatherSynth.Thunder(unchecked((uint)(k * 7919 + 13)), near);
        float baseDb = Math.Min(near ? NearThunderDb : FarThunderDb, (near ? NearThunderDb : FarThunderDb) + extraDb) + WeatherShelterMix.ThunderDb(Shelter);
        var player = new AudioStreamPlayer { Name = near ? "ThunderNear" : "ThunderFar", Stream = stream, VolumeDb = baseDb + _thunderFadeDb, Bus = AcquireBus() };
        AddChild(player);
        _thunderPlayers++;
        _rumbles.Add((player, baseDb));
        player.Finished += () => { _thunderPlayers = Math.Max(0, _thunderPlayers - 1); _rumbles.RemoveAll(r => r.Item1 == player); player.QueueFree(); };
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

    /// <summary>Repair 2: the ash colour (a pale grey-ochre), the soft flakes' size (m) and their near fade (m: invisible nearer than the first, full from the second, so no flake at the lens reads as a blurred orb).</summary>
    public const float AshR = 0.62f, AshG = 0.56f, AshB = 0.47f, AshFleckM = 0.16f, AshNearFadeM = 0.6f, AshNearFullM = 2.5f;
    /// <summary>
    /// Repair 2 (the second attempt: 800 then 1300 soft dots drifting through a 32 m box read as a handful of specks on Manufactorum): the squall's body is wind-driven grit, streaks along the wind drawn with the rain's axial streak
    /// shader (each streak the grit's speed times a short shutter, so even a far one is a visible line), emitted <see cref="GritUpwindShare"/> of a life upwind in a narrow street-level band so the whole life is spent crossing the
    /// player's surroundings instead of leaving them. Width and length of a streak (m), the band's half-width, half-height and its middle's height over the camera (m), the life (s).
    /// </summary>
    public const float GritWidthM = 0.035f, GritLengthM = 0.5f, GritHalfWidthM = 11f, GritHalfHeightM = 2.2f, GritMidOverCameraM = -0.2f, GritLifeSeconds = 2.0f, GritUpwindShare = 0.5f;

    CpuParticles3D BuildGrit(int amount)
    {
        _ashMainMaterial = RainMaterial(new Color(AshR, AshG, AshB, 0.6f), 1.0f);
        var p = new CpuParticles3D
        {
            Name = "AshSquall",
            Amount = amount,
            Lifetime = GritLifeSeconds,
            Preprocess = GritLifeSeconds,   // full at once
            LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(GritHalfWidthM, GritHalfHeightM, GritHalfWidthM),
            Direction = new Vector3(1f, -0.04f, 0f),
            Spread = 6f,
            Gravity = new Vector3(0f, -0.5f, 0f),
            InitialVelocityMin = 10f,
            InitialVelocityMax = 16f,
            ScaleAmountMin = 0.6f,
            ScaleAmountMax = 1.4f,
            Mesh = new QuadMesh { Size = new Vector2(GritWidthM, GritLengthM), Material = _ashMainMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-60f, -10f, -60f), new Vector3(120f, 24f, 120f)),
            Emitting = false,
        };
        AddChild(p);
        return p;
    }

    void ConfigureGrit(bool on, float share, Vector3 windDir, float wind, float nearAlpha)
    {
        if (_ashMain is null) return;
        _ashMain.Emitting = on;
        if (!on) return;
        float speed = 8f + 10f * wind;   // a squall's grit outruns the flakes
        var dir = new Vector3(windDir.X, -0.04f, windDir.Z);
        _ashMain.Direction = dir;
        _ashMain.InitialVelocityMin = speed * 0.8f;
        _ashMain.InitialVelocityMax = speed * 1.2f;
        _ashMain.Position = new Vector3(0f, GritMidOverCameraM, 0f) - windDir * (speed * GritLifeSeconds * GritUpwindShare);   // emitted upwind: half way through its life a grain is beside the player
        SetStreak(_ashMainMaterial, dir, new Color(AshR, AshG, AshB), (0.38f + 0.37f * Math.Clamp(share, 0f, 1f)) * nearAlpha);
    }

    /// <summary>The grit streaks' alpha now (selftest).</summary>
    public float GritAlpha => _ashMainMaterial?.GetShaderParameter("tint").AsColor().A ?? 0f;

    CpuParticles3D BuildFlakes(int amount)
    {
        _ashWarnMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(AshR, AshG, AshB, 0.7f),
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = AshNearFadeM,
            DistanceFadeMaxDistance = AshNearFullM,
            AlbedoTexture = SoftDot(),
            DisableFog = false,
        };
        var p = new CpuParticles3D
        {
            Name = "AshWarning",
            Amount = amount,
            Lifetime = 3.0,
            Preprocess = 3.0,
            LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(12f, 3f, 12f),
            Direction = new Vector3(1f, -0.02f, 0f),
            Spread = 10f,
            Gravity = new Vector3(0f, -0.35f, 0f),
            InitialVelocityMin = 4f,
            InitialVelocityMax = 8f,
            ScaleAmountMin = 0.5f,
            ScaleAmountMax = 1.5f,
            Mesh = new QuadMesh { Size = new Vector2(AshFleckM, AshFleckM), Material = _ashWarnMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-50f, -10f, -50f), new Vector3(100f, 24f, 100f)),
            Emitting = false,
        };
        AddChild(p);
        return p;
    }

    void ConfigureFlakes(bool on, float share, Vector3 windDir, float wind, float nearAlpha)
    {
        if (_ashWarn is null) return;
        _ashWarn.Emitting = on;
        if (!on) return;
        float speed = 4f + 8f * wind;
        _ashWarn.Direction = new Vector3(windDir.X, -0.02f, windDir.Z);
        _ashWarn.InitialVelocityMin = speed * 0.7f;
        _ashWarn.InitialVelocityMax = speed * 1.15f;
        _ashWarn.Position = new Vector3(0f, 0.5f, 0f) - windDir * (speed * 1.5f);   // upwind by half a life too
        if (_ashWarnMaterial is not null) _ashWarnMaterial.AlbedoColor = new Color(AshR, AshG, AshB, (0.5f + 0.35f * Math.Clamp(share, 0f, 1f)) * nearAlpha);
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
        _warnLabel = new Label { Text = "ASH SQUALL INTENSIFYING", Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
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
