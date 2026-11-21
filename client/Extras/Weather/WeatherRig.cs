using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// The weather's own things in the world (weather W1 and W2; client only, presentation only, **ours**): falling rain around the camera (Rain and the Thunderstorm), drifting ash (Ash Squall), sparse embers (Ember Wind), a
/// faint sky-only glow for thunder (reduced flash: never the screen's exposure), and their sounds, all made in code (filtered noise: no sample, no original file; the install's own sounds are posted by the networked view).
/// Particles are CPU particles in every renderer, cut to a third in the Compatibility renderer and in safe mode, to a quarter with the lighting preset off. Loudness is capped (spec "Weather (W2)"): rain at most -22 dB,
/// wind at most <see cref="WindLoudestDb"/>, thunder at most <see cref="NearThunderDb"/>, so gunfire, Waaagh, rockets and the beacon stay audible. Built when a look needs it and freed when it ends: no weather leaves a node or a sound behind.
/// </summary>
public sealed partial class WeatherRig : Node3D
{
    /// <summary>Rain particles at the High lighting preset; the others scale it.</summary>
    public const int RainCount = 1000;
    /// <summary>The loudest the rain hiss gets, in dB, at the peak of Rain.</summary>
    public const float LoudestDb = -22f, SilentDb = -60f;
    /// <summary>The loudest the wind bed gets (dB), the loudest a far and a near thunder gets (dB, procedural; the install's own are posted by the view at their own level offset).</summary>
    public const float WindLoudestDb = -20f, FarThunderDb = -20f, NearThunderDb = -14f;
    /// <summary>Ash flecks at the High preset (the dense emitter; the warning's emitter is a fifth of it), embers at the High preset.</summary>
    public const int AshCount = 800, EmberCount = 70;

    CpuParticles3D? _rain;
    StandardMaterial3D? _rainMaterial;
    AudioStreamPlayer? _hiss;

    public bool Raining => _rain is { Emitting: true };

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

    /// <summary>Once a frame: follows the camera and shows what the preset at its phase and intensity needs (rain, ash, embers, the sky glow, the wind); anything not needed stops at once.</summary>
    public void Update(WeatherState st, float intensity, Vector3 cameraAt)
    {
        GlobalPosition = cameraAt;
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        float wind = WeatherPlan.Wind(st.Id, i, st.Elapsed);
        float rainShare = WeatherPlan.RainShare(st.Id, i);
        bool rain = rainShare > 0.02f;
        if (rain) EnsureRain();
        if (_rain is not null)
        {
            _rain.Emitting = rain;
            if (_rainMaterial is not null) _rainMaterial.AlbedoColor = new Color(0.72f, 0.78f, 0.88f, 0.10f + 0.34f * rainShare);   // the streaks' strength follows the phase
            float slant = 0.10f + (st.Id == WeatherId.Thunderstorm ? 0.30f * wind : 0f);   // the wind leans the rain
            float yaw = WeatherPlan.WindYaw(st.Id);
            _rain.Direction = new Vector3(MathF.Cos(yaw) * slant, -1f, MathF.Sin(yaw) * slant);
        }
        if (_hiss is not null || rain)
        {
            if (_hiss is null) EnsureRain();
            float db = rain ? Mathf.Lerp(SilentDb, LoudestDb, rainShare) : SilentDb;
            _hiss!.VolumeDb = db;
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
            _rainMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(0.72f, 0.78f, 0.88f, 0.2f),
                DisableFog = false,
            };
            _rain = new CpuParticles3D
            {
                Name = "Rain",
                Amount = Math.Max(60, (int)(RainCount * QualityScale())),
                Lifetime = 1.0,
                Preprocess = 1.0,
                LocalCoords = false,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(18f, 0.2f, 18f),
                Direction = new Vector3(0.10f, -1f, 0.04f),
                Spread = 1.5f,
                Gravity = Vector3.Zero,
                InitialVelocityMin = 24f,
                InitialVelocityMax = 28f,
                Mesh = new QuadMesh { Size = new Vector2(0.012f, 0.6f), Material = _rainMaterial },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0f, 14f, 0f),
                VisibilityAabb = new Aabb(new Vector3(-24f, -30f, -24f), new Vector3(48f, 40f, 48f)),
            };
            AddChild(_rain);
        }
        if (_hiss is null)
        {
            _hiss = new AudioStreamPlayer { Name = "RainHiss", Stream = SynthesizedHiss(), VolumeDb = SilentDb, Bus = "Master" };
            AddChild(_hiss);
        }
    }

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
