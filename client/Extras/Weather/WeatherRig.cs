using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// The weather's own things in the world (weather W1 and W2; client only, presentation only, **ours**): falling rain around the camera (Rain and the Thunderstorm), drifting ash (Ash Squall), sparse embers (Ember Wind), a
/// faint sky-only glow for thunder (reduced flash by default; the full-flash setting adds a brighter pulse), and their sounds, all made in code (filtered noise: no sample, no original file; the install's own sounds are posted by the
/// networked view). Particles are CPU particles in every renderer, cut to a third in the Compatibility renderer and in safe mode, to a quarter with the lighting preset off. Loudness is capped (spec "Weather (W2)"): rain at most -22 dB,
/// wind at most <see cref="WindLoudestDb"/>, thunder at most <see cref="NearThunderDb"/>, so gunfire, Waaagh, rockets and the beacon stay audible. Built when a look needs it and freed when it ends: no weather leaves a node or a sound behind.
/// <para>Rain is four layers (W2 pass 2, the owner: "thicker and more believable"): a dense near layer, a farther thinner layer, splashes on the ground round the player and a little low mist. Counts at the High preset:
/// <see cref="RainNearCount"/> + <see cref="RainFarCount"/> + <see cref="SplashCount"/> + <see cref="MistCount"/> CPU particles; all of them scale by <see cref="QualityScale"/>.</para>
/// </summary>
public sealed partial class WeatherRig : Node3D
{
    /// <summary>Rain streaks in the near layer (a 24 m box, falling 22 to 28 m/s), in the far layer (an 80 m box, thinner and fainter), ground splashes and mist puffs, each at the High lighting preset; the others scale them.</summary>
    public const int RainNearCount = 1300, RainFarCount = 600, SplashCount = 260, MistCount = 28;
    /// <summary>The loudest the rain hiss gets, in dB, at the peak of Rain.</summary>
    public const float LoudestDb = -22f, SilentDb = -60f;
    /// <summary>The loudest the wind bed gets (dB), the loudest a far and a near thunder gets (dB, procedural; the install's own are posted by the view at their own level offset).</summary>
    public const float WindLoudestDb = -20f, FarThunderDb = -20f, NearThunderDb = -14f;
    /// <summary>Ash flecks at the High preset (the dense emitter; the warning's emitter is a fifth of it), embers at the High preset.</summary>
    public const int AshCount = 800, EmberCount = 70;

    CpuParticles3D? _rain, _rainFar, _splash, _mist;
    StandardMaterial3D? _rainMaterial, _rainFarMaterial, _splashMaterial, _mistMaterial;
    AudioStreamPlayer? _hiss;

    /// <summary>The full-flash setting (the player's option, set by the view): a thunder strike's glow is then the brighter pulse.</summary>
    public bool FullFlash { get; set; }

    public bool Raining => _rain is { Emitting: true };
    public bool Splashing => _splash is { Emitting: true };

    /// <summary>The rain-count scale of the graphics settings in use (0.25 to 1.2).</summary>
    public static float QualityScale()
    {
        if (SafeMode.Active) return 0.33f;
        return MapLightingView.Preset switch
        {
            MapLightingView.LightingPreset.Off => 0.25f,
            MapLightingView.LightingPreset.Low => 0.5f,
            MapLightingView.LightingPreset.High => 1.2f,
            _ => 0.8f,
        };
    }

    /// <summary>
    /// Once a frame: follows the camera and shows what the preset at its phase and intensity needs (rain, ash, embers, the sky glow, the wind); anything not needed stops at once.
    /// <paramref name="groundY"/>: the height of the ground under the player (splashes and mist lie there); null takes 1.8 m under the camera (an estimate: the camera is the head's height, higher in third person).
    /// </summary>
    public void Update(WeatherState st, float intensity, Vector3 cameraAt, float? groundY = null)
    {
        GlobalPosition = cameraAt;
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        float wind = WeatherPlan.Wind(st.Id, i, st.Elapsed);
        float rainShare = WeatherPlan.RainShare(st.Id, i);
        bool rain = rainShare > 0.02f;
        if (rain) EnsureRain();
        if (_rain is not null)
        {
            float yaw = WeatherPlan.WindYaw(st.Id);
            float slant = 0.08f + (st.Id == WeatherId.Thunderstorm ? 0.35f * wind : 0.05f);   // the wind leans the rain
            var dir = new Vector3(MathF.Cos(yaw) * slant, -1f, MathF.Sin(yaw) * slant);
            _rain.Emitting = rain; _rain.Direction = dir;
            if (_rainFar is not null) { _rainFar.Emitting = rain; _rainFar.Direction = dir; }
            // the streaks' strength follows the phase; a heavier fall draws a little brighter and thicker
            if (_rainMaterial is not null) _rainMaterial.AlbedoColor = new Color(0.74f, 0.80f, 0.90f, 0.16f + 0.42f * rainShare);
            if (_rainFarMaterial is not null) _rainFarMaterial.AlbedoColor = new Color(0.70f, 0.76f, 0.86f, 0.08f + 0.24f * rainShare);
            float ground = groundY ?? cameraAt.Y - 1.8f;
            if (_splash is not null)
            {
                _splash.Emitting = rain;
                _splash.GlobalPosition = new Vector3(cameraAt.X, ground + 0.03f, cameraAt.Z);
                if (_splashMaterial is not null) _splashMaterial.AlbedoColor = new Color(0.85f, 0.9f, 1f, 0.20f + 0.35f * rainShare);
            }
            if (_mist is not null)
            {
                _mist.Emitting = rain;
                _mist.GlobalPosition = new Vector3(cameraAt.X, ground + 0.5f, cameraAt.Z);
                _mist.Direction = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
                if (_mistMaterial is not null) _mistMaterial.AlbedoColor = new Color(0.62f, 0.68f, 0.76f, 0.03f + 0.06f * rainShare);
            }
        }
        if (_hiss is not null)
        {
            float db = rain ? Mathf.Lerp(SilentDb, LoudestDb, rainShare) : SilentDb;
            _hiss.VolumeDb = db;
            if (rain && !_hiss.Playing) _hiss.Play();
            else if (!rain && _hiss.Playing) _hiss.Stop();
        }
        UpdateFx(st, i, wind);
    }

    /// <summary>Anything of the rig is showing or sounding (the view keeps the rig while this holds).</summary>
    public bool Active => Raining || FxActive;

    void EnsureRain()
    {
        if (_rain is null)
        {
            float q = QualityScale();
            _rainMaterial = RainMaterial(new Color(0.74f, 0.80f, 0.90f, 0.2f));
            _rain = new CpuParticles3D
            {
                Name = "Rain",
                Amount = Math.Max(120, (int)(RainNearCount * q)),
                Lifetime = 0.9,
                Preprocess = 1.0,
                LocalCoords = false,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(12f, 0.2f, 12f),
                Direction = new Vector3(0.10f, -1f, 0.04f),
                Spread = 2.0f,
                Gravity = Vector3.Zero,
                InitialVelocityMin = 22f,
                InitialVelocityMax = 28f,
                ScaleAmountMin = 0.7f,   // some variance in length and thickness
                ScaleAmountMax = 1.3f,
                Mesh = new QuadMesh { Size = new Vector2(0.014f, 0.8f), Material = _rainMaterial },   // a streak is the speed times a 0.03 s shutter
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0f, 13f, 0f),
                VisibilityAabb = new Aabb(new Vector3(-16f, -30f, -16f), new Vector3(32f, 40f, 32f)),
            };
            AddChild(_rain);
            _rainFarMaterial = RainMaterial(new Color(0.70f, 0.76f, 0.86f, 0.1f));
            _rainFar = new CpuParticles3D
            {
                Name = "RainFar",
                Amount = Math.Max(60, (int)(RainFarCount * q)),
                Lifetime = 1.2,
                Preprocess = 1.2,
                LocalCoords = false,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(40f, 0.2f, 40f),
                Direction = new Vector3(0.10f, -1f, 0.04f),
                Spread = 3.0f,
                Gravity = Vector3.Zero,
                InitialVelocityMin = 20f,
                InitialVelocityMax = 26f,
                ScaleAmountMin = 0.7f,
                ScaleAmountMax = 1.4f,
                Mesh = new QuadMesh { Size = new Vector2(0.01f, 0.6f), Material = _rainFarMaterial },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0f, 16f, 0f),
                VisibilityAabb = new Aabb(new Vector3(-48f, -34f, -48f), new Vector3(96f, 48f, 96f)),
            };
            AddChild(_rainFar);
            _splashMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(0.85f, 0.9f, 1f, 0.4f),
                AlbedoTexture = SoftDot(),
                VertexColorUseAsAlbedo = true,
            };
            var splashRamp = new Gradient();
            splashRamp.SetColor(0, new Color(1f, 1f, 1f, 1f));
            splashRamp.SetColor(1, new Color(1f, 1f, 1f, 0f));
            _splash = new CpuParticles3D
            {
                Name = "Splash",
                Amount = Math.Max(60, (int)(SplashCount * q)),   // each lives 0.3 s: the rate is about 870 a second at High
                Lifetime = 0.3,
                Preprocess = 0.3,
                LocalCoords = false,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(7f, 0.01f, 7f),
                Direction = Vector3.Up,
                Spread = 35f,
                Gravity = new Vector3(0f, -6f, 0f),
                InitialVelocityMin = 0.5f,
                InitialVelocityMax = 1.2f,
                ScaleAmountMin = 0.6f,
                ScaleAmountMax = 1.2f,
                ColorRamp = splashRamp,
                Mesh = new QuadMesh { Size = new Vector2(0.07f, 0.07f), Material = _splashMaterial },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityAabb = new Aabb(new Vector3(-9f, -1f, -9f), new Vector3(18f, 4f, 18f)),
            };
            AddChild(_splash);
            _mistMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(0.62f, 0.68f, 0.76f, 0.08f),
                AlbedoTexture = SoftDot(),
                VertexColorUseAsAlbedo = true,
            };
            var mistRamp = new Gradient();
            mistRamp.SetColor(0, new Color(1f, 1f, 1f, 0f));
            mistRamp.SetColor(1, new Color(1f, 1f, 1f, 0f));
            mistRamp.AddPoint(0.5f, new Color(1f, 1f, 1f, 1f));   // fades in and out
            _mist = new CpuParticles3D
            {
                Name = "RainMist",
                Amount = Math.Max(10, (int)(MistCount * q)),
                Lifetime = 5.0,
                Preprocess = 5.0,
                LocalCoords = false,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(14f, 0.3f, 14f),
                Direction = new Vector3(1f, 0f, 0f),
                Spread = 20f,
                Gravity = Vector3.Zero,
                InitialVelocityMin = 0.6f,
                InitialVelocityMax = 1.2f,
                ScaleAmountMin = 0.8f,
                ScaleAmountMax = 1.4f,
                ColorRamp = mistRamp,
                Mesh = new QuadMesh { Size = new Vector2(3.0f, 3.0f), Material = _mistMaterial },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityAabb = new Aabb(new Vector3(-20f, -2f, -20f), new Vector3(40f, 6f, 40f)),
            };
            AddChild(_mist);
        }
        if (_hiss is null)
        {
            _hiss = new AudioStreamPlayer { Name = "RainHiss", Stream = SynthesizedHiss(), VolumeDb = SilentDb, Bus = "Master" };
            AddChild(_hiss);
        }
    }

    static StandardMaterial3D RainMaterial(Color color) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        BillboardKeepScale = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = color,
        DisableFog = false,
    };

    /// <summary>Three seconds of filtered noise as a looping 16-bit mono stream: a steady hiss, brighter than a rumble, made in code (no sample).</summary>
    static AudioStreamWav SynthesizedHiss()
    {
        const int rate = 22050, samples = rate * 3;
        var data = new byte[samples * 2];
        var rng = new Random(0x5AF3);
        float low = 0f, high = 0f, prev = 0f;
        for (int i = 0; i < samples; i++)
        {
            float white = (float)(rng.NextDouble() * 2.0 - 1.0);
            low += 0.30f * (white - low);          // a gentle low-pass: no harsh top
            high = 0.96f * (high + low - prev);    // a high-pass: no thump
            prev = low;
            short s = (short)Math.Clamp((int)(high * 9000f), short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data, LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = samples };
    }
}
