using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// The weather's own things in the world (weather W1 and W2; client only, presentation only, **ours**): falling rain around the camera (Rain and the Thunderstorm), drifting ash (Ash Squall), sparse embers (Ember Wind), a
/// faint sky-only glow for thunder (reduced flash by default; the full-flash setting adds a brighter pulse), and their sounds, all made in code (filtered noise: no sample, no original file; the install's own sounds are posted by the
/// networked view). Particles are CPU particles in every renderer, cut to a third in the Compatibility renderer and in safe mode, to a quarter with the lighting preset off. Loudness is capped (spec "Weather (W2)"): rain at most -22 dB,
/// wind at most <see cref="WindLoudestDb"/>, thunder at most <see cref="NearThunderDb"/>, so gunfire, Waaagh, rockets and the beacon stay audible. Built when a look needs it and freed when it ends: no weather leaves a node or a sound behind (the weather bus goes with the last rig).
/// <para>W4: <see cref="SetShelter"/> (the view's upward rays, smoothed by <see cref="WeatherShelter"/>) fades the near layers out under cover and keeps the far layer only outside a hole round the player, and slides the weather bus's low-pass to the muffled indoor mix (<see cref="WeatherShelterMix"/>); <see cref="StepTrim"/> (<see cref="WeatherTrim"/>) quietly lowers the particle counts when frames run long.</para>
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
    ShaderMaterial? _rainMaterial, _rainFarMaterial;
    StandardMaterial3D? _splashMaterial, _mistMaterial;
    AudioStreamPlayer? _hiss;

    /// <summary>The full-flash setting (the player's option, set by the view): a thunder strike's glow is then the brighter pulse.</summary>
    public bool FullFlash { get; set; }

    /// <summary>The shelter level now (0 open, 1 under cover) and whether the cover is low (a tunnel); set by the view.</summary>
    public float Shelter { get; private set; }
    public bool ShelterTunnel { get; private set; }
    /// <summary>The auto-trim (weather W4): the level and its counts; <see cref="TrimChanged"/> fires with (level, total particles) when it steps.</summary>
    public WeatherTrim Trim { get; } = new();
    public event Action<int, int>? TrimChanged;
    bool _farHole;

    /// <summary>The shelter of this frame; the next <see cref="Update"/> draws and mixes it.</summary>
    public void SetShelter(float level, bool tunnel)
    {
        Shelter = float.IsFinite(level) ? Math.Clamp(level, 0f, 1f) : 0f;
        ShelterTunnel = tunnel;
    }

    /// <summary>One frame of <paramref name="dt"/> seconds for the auto-trim; the layers' counts follow its level.</summary>
    public void StepTrim(double dt)
    {
        if (--_capCountdown <= 0) { _capCountdown = 60; Trim.ExpectedFrameMs = ExplainedFrameMs(); }
        if (Trim.Frame(dt)) { ApplyTrim(); TrimChanged?.Invoke(Trim.Level, ParticleTotal); }
    }

    int _capCountdown;

    /// <summary>The frame length (ms) the engine's own frame cap or the display's vsync interval explains (the longer of the two; 0 when neither limits), so the trim does not read a cap as weather cost.</summary>
    public static double ExplainedFrameMs()
    {
        double ms = Engine.MaxFps > 0 ? 1000.0 / Engine.MaxFps : 0.0;
        try
        {
            if (DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled)
            {
                float hz = DisplayServer.ScreenGetRefreshRate();
                if (hz > 1f) ms = Math.Max(ms, 1000.0 / hz);
            }
        }
        catch (Exception) { /* headless: no display server window */ }
        return ms;
    }

    /// <summary>The particles of every layer that exists now (at the current trim).</summary>
    public int ParticleTotal => (_rain?.Amount ?? 0) + (_rainFar?.Amount ?? 0) + (_splash?.Amount ?? 0) + (_mist?.Amount ?? 0) + (_ashWarn?.Amount ?? 0) + (_ashMain?.Amount ?? 0) + (_embers?.Amount ?? 0) + (_fog?.Amount ?? 0);

    /// <summary>A layer's count at the High-preset base <paramref name="baseCount"/>: the graphics preset's scale, then the trim, never below the floor.</summary>
    int Count(int baseCount, int floor) => WeatherTrim.Count(Math.Max(floor, (int)(baseCount * QualityScale())), floor, Trim.Level);

    void ApplyTrim()
    {
        Set(_rain, RainNearCount, NearFloor); Set(_rainFar, RainFarCount, FarFloor); Set(_splash, SplashCount, SplashFloor); Set(_mist, MistCount, MistFloor);
        Set(_ashWarn, (int)(AshCount * 0.2f), AshWarnFloor, scale: false); Set(_ashMain, AshCount, AshMainFloor); Set(_embers, EmberCount, EmberFloor); Set(_fog, FogCount, FogFloor);
        void Set(CpuParticles3D? p, int baseCount, int floor, bool scale = true)
        {
            if (p is null) return;
            int n = scale ? Count(baseCount, floor) : WeatherTrim.Count(Math.Max(floor, (int)(baseCount * QualityScale())), floor, Trim.Level);
            if (p.Amount != n) p.Amount = n;
        }
    }

    /// <summary>The floors of each layer's count (the smallest it ever gets, at any preset and any trim).</summary>
    public const int NearFloor = 120, FarFloor = 60, SplashFloor = 60, MistFloor = 10, AshWarnFloor = 20, AshMainFloor = 60, EmberFloor = 24;

    public bool Raining => _rain is { Emitting: true } || (_rainFar is { Emitting: true } && Shelter >= WeatherShelterMix.NearOffAbove);
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
        TrackVelocity(cameraAt);
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        float wind = WeatherPlan.Wind(st.Id, i, st.Elapsed);
        float rainShare = WeatherPlan.RainShare(st.Id, i);
        bool rain = rainShare > 0.02f;
        float near = WeatherShelterMix.NearAlpha(Shelter), far = WeatherShelterMix.FarAlpha(Shelter);
        bool nearOn = rain && Shelter < WeatherShelterMix.NearOffAbove;   // under cover the near layers have stopped; the far layer keeps raining outside its hole
        if (rain) EnsureRain();
        MixShelter();
        if (_rain is not null)
        {
            float yaw = WeatherPlan.WindYaw(st.Id);
            float slant = 0.08f + (st.Id == WeatherId.Thunderstorm ? 0.35f * wind : 0.05f);   // the wind leans the rain
            var dir = new Vector3(MathF.Cos(yaw) * slant, -1f, MathF.Sin(yaw) * slant);
            _rain.Emitting = nearOn; _rain.Direction = dir;
            // the weather finish: the emitters lead the camera by its ground velocity times the time a drop takes to fall to eye level, so a fast sideways move never outruns the rain (no dry gap)
            var lead = LeadVelocity;
            _rain.Position = new Vector3(lead.X * NearLeadSeconds, NearHeightM, lead.Z * NearLeadSeconds);
            if (_rainFar is not null)
            {
                _rainFar.Emitting = rain; _rainFar.Direction = dir; FarHole();
                _rainFar.Position = _farHole ? new Vector3(0f, FarHeightM, 0f) : new Vector3(lead.X * FarLeadSeconds, FarHeightM, lead.Z * FarLeadSeconds);   // the ring under cover stays round the player
            }
            // the streaks' strength follows the phase; a heavier fall draws a little brighter; the colour leans toward the scene's own haze so the rain belongs to the map's light
            var haze = SceneHaze();
            SetStreak(_rainMaterial, dir, RainTint(new Color(0.74f, 0.80f, 0.90f), haze), (0.16f + 0.42f * rainShare) * near);
            SetStreak(_rainFarMaterial, dir, RainTint(new Color(0.70f, 0.76f, 0.86f), haze), (0.08f + 0.24f * rainShare) * far);
            float ground = groundY ?? cameraAt.Y - 1.8f;
            if (_splash is not null)
            {
                _splash.Emitting = nearOn;
                _splash.GlobalPosition = new Vector3(cameraAt.X + lead.X * SplashLeadSeconds, ground + 0.03f, cameraAt.Z + lead.Z * SplashLeadSeconds);
                if (_splashMaterial is not null) _splashMaterial.AlbedoColor = new Color(0.85f, 0.9f, 1f, (0.20f + 0.35f * rainShare) * near);
            }
            if (_mist is not null)
            {
                _mist.Emitting = nearOn;
                _mist.GlobalPosition = new Vector3(cameraAt.X, ground + 0.5f, cameraAt.Z);
                _mist.Direction = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
                if (_mistMaterial is not null) _mistMaterial.AlbedoColor = new Color(0.62f, 0.68f, 0.76f, (0.03f + 0.06f * rainShare) * near);
            }
        }
        if (_hiss is not null)
        {
            float db = rain ? WeatherPlan.FollowDb(rainShare, LoudestDb, SilentDb) + WeatherShelterMix.RainDb(Shelter) : SilentDb;   // the weather finish: the hiss's amplitude follows the rain's share
            _hiss.VolumeDb = db;
            if (rain && !_hiss.Playing) _hiss.Play();
            else if (!rain && _hiss.Playing) _hiss.Stop();
        }
        UpdateFx(st, i, wind);
        UpdateFog(st, i, cameraAt, groundY);
        UpdateSky(st, i, wind);
    }

    /// <summary>Anything of the rig is showing or sounding (the view keeps the rig while this holds).</summary>
    public bool Active => Raining || FxActive || Fogging || SkyShowing;

    /// <summary>The far layer rains in a ring (a hole round the player) under cover and in its full box in the open; flips with a gap so it does not chatter.</summary>
    void FarHole()
    {
        if (_rainFar is null) return;
        bool hole = _farHole ? Shelter > 0.2f : Shelter > WeatherShelterMix.HoleAbove;
        if (hole == _farHole) return;
        _farHole = hole;
        if (hole)
        {
            _rainFar.EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring;
            _rainFar.EmissionRingAxis = Vector3.Up;
            _rainFar.EmissionRingHeight = 0.4f;
            _rainFar.EmissionRingRadius = 44f;
            _rainFar.EmissionRingInnerRadius = WeatherShelterMix.FarHoleRadiusM;
        }
        else _rainFar.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
    }

    /// <summary>Whether the far layer is raining in its ring (selftest).</summary>
    public bool FarHoleOn => _farHole;

    // ---- the weather bus: one low-pass bus for the rig's own sounds (shared by every rig, removed with the last) ----

    public const string BusName = "WeatherMix";
    static int _busUsers;
    static AudioEffectLowPassFilter? _lowPass;
    static float _cutoffNow = WeatherShelterMix.OpenHz;
    bool _busHeld;

    /// <summary>The bus's name; makes it (a low-pass, sending to Master) when this rig first needs it. Falls back to Master when the audio server has no such bus to make.</summary>
    string AcquireBus()
    {
        if (!_busHeld) { _busHeld = true; _busUsers++; }
        int index = AudioServer.GetBusIndex(BusName);
        if (index < 0)
        {
            AudioServer.AddBus();
            index = AudioServer.BusCount - 1;
            AudioServer.SetBusName(index, BusName);
            AudioServer.SetBusSend(index, "Master");
            _lowPass = new AudioEffectLowPassFilter { CutoffHz = WeatherShelterMix.OpenHz, Resonance = 0.4f, Db = AudioEffectFilter.FilterDB.Filter12Db };
            AudioServer.AddBusEffect(index, _lowPass);
            _cutoffNow = WeatherShelterMix.OpenHz;
        }
        return AudioServer.GetBusIndex(BusName) >= 0 ? BusName : "Master";
    }

    /// <summary>The bus's low-pass follows the shelter level (set only when it moved by more than 1 %).</summary>
    void MixShelter()
    {
        if (!_busHeld) return;
        float hz = WeatherShelterMix.CutoffHz(Shelter);
        if (_lowPass is not null && Math.Abs(hz - _cutoffNow) > _cutoffNow * 0.01f) { _lowPass.CutoffHz = hz; _cutoffNow = hz; }
    }

    /// <summary>The weather bus's cutoff now (Hz), for the selftest and the log.</summary>
    public float BusCutoffHz => _busHeld && _lowPass is not null ? _lowPass.CutoffHz : WeatherShelterMix.OpenHz;

    public override void _ExitTree()
    {
        if (!_busHeld) return;
        _busHeld = false;
        if (--_busUsers <= 0)
        {
            _busUsers = 0;
            int index = AudioServer.GetBusIndex(BusName);
            if (index >= 0) AudioServer.RemoveBus(index);
            _lowPass = null;
        }
    }

    void EnsureRain()
    {
        if (_rain is null)
        {
            _rainMaterial = RainMaterial(new Color(0.74f, 0.80f, 0.90f, 0.2f), 1.2f);
            _rain = new CpuParticles3D
            {
                Name = "Rain",
                Amount = Count(RainNearCount, NearFloor),
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
                Mesh = new QuadMesh { Size = new Vector2(StreakWidthM, 0.8f), Material = _rainMaterial },   // a streak is the speed times a 0.03 s shutter; the soft profile across it is the shader's
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0f, NearHeightM, 0f),
                VisibilityAabb = new Aabb(new Vector3(-40f, -30f, -40f), new Vector3(80f, 40f, 80f)),   // wide enough for the drops a fast move leaves behind the led emitter
            };
            AddChild(_rain);
            _rainFarMaterial = RainMaterial(new Color(0.70f, 0.76f, 0.86f, 0.1f), 3f);
            _rainFar = new CpuParticles3D
            {
                Name = "RainFar",
                Amount = Count(RainFarCount, FarFloor),
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
                Mesh = new QuadMesh { Size = new Vector2(StreakFarWidthM, 0.6f), Material = _rainFarMaterial },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0f, FarHeightM, 0f),
                VisibilityAabb = new Aabb(new Vector3(-72f, -34f, -72f), new Vector3(144f, 48f, 144f)),
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
                Amount = Count(SplashCount, SplashFloor),   // each lives 0.3 s: the rate is about 870 a second at High
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
                VisibilityAabb = new Aabb(new Vector3(-16f, -1f, -16f), new Vector3(32f, 4f, 32f)),
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
                Amount = Count(MistCount, MistFloor),
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
            _hiss = new AudioStreamPlayer { Name = "RainHiss", Stream = SynthesizedHiss(), VolumeDb = SilentDb, Bus = AcquireBus() };
            AddChild(_hiss);
        }
    }

    // ---- the weather finish: rain coverage at speed and the streaks' look ----

    /// <summary>The near and far emitters' heights over the camera (m), and the seconds a drop takes from them to eye level (height over the mean fall speed: 13 m at 25 m/s, 16 m at 23 m/s); the splashes' lead is half their 0.3 s life.</summary>
    public const float NearHeightM = 13f, FarHeightM = 16f;
    public const float NearLeadSeconds = 0.52f, FarLeadSeconds = 0.70f, SplashLeadSeconds = 0.15f;
    /// <summary>The lead's speed cap (m/s; above any player move: a jump-pack boost, a fall), the smoothing of the measured camera velocity (s); a step longer than <see cref="TeleportM"/> and than 1.5 times the cap over the frame (a respawn, a camera cut) resets it.</summary>
    public const float LeadMaxSpeed = 30f, LeadSmoothSeconds = 0.12f, TeleportM = 3f;
    /// <summary>The streak quads' widths (m): the shader's soft profile across them makes the drawn core about half of it.</summary>
    public const float StreakWidthM = 0.02f, StreakFarWidthM = 0.014f;

    Vector3 _lastCamera, _velocity;
    ulong _lastUsec;
    bool _leadForced;

    /// <summary>The camera's smoothed ground velocity (m/s, x and z; y is 0), capped at <see cref="LeadMaxSpeed"/>.</summary>
    public Vector3 LeadVelocity => _velocity;

    void TrackVelocity(Vector3 at)
    {
        ulong now = Time.GetTicksUsec();
        if (_leadForced) { _lastCamera = at; _lastUsec = now; return; }
        double dt = _lastUsec == 0 ? 0 : (now - _lastUsec) / 1e6;
        if (dt > 0 && dt < 0.5)
        {
            var step = at - _lastCamera; step.Y = 0f;
            if (step.Length() > Math.Max(TeleportM, LeadMaxSpeed * 1.5f * (float)dt)) _velocity = Vector3.Zero;   // faster than any move (a respawn, a camera cut): no lead
            else
            {
                var v = step / (float)dt;
                float k = 1f - MathF.Exp(-(float)dt / LeadSmoothSeconds);
                _velocity = _velocity.Lerp(v, k);
                if (_velocity.Length() > LeadMaxSpeed) _velocity = _velocity.Normalized() * LeadMaxSpeed;
            }
        }
        else if (dt >= 0.5) _velocity = Vector3.Zero;   // a stall: start again
        _lastCamera = at; _lastUsec = now;
    }

    /// <summary>Test seam (selftest, stills): hold the lead as if the camera moved at <paramref name="v"/> m/s; null measures again.</summary>
    public void ForceLeadVelocity(Vector3? v)
    {
        _leadForced = v is not null;
        _velocity = v is { } f ? new Vector3(f.X, 0f, f.Z) : Vector3.Zero;
        if (_velocity.Length() > LeadMaxSpeed) _velocity = _velocity.Normalized() * LeadMaxSpeed;
    }

    /// <summary>The near emitter's place relative to the camera (selftest).</summary>
    public Vector3 RainEmitterOffset => _rain?.Position ?? Vector3.Zero;

    Color _haze = new(0.6f, 0.65f, 0.72f);
    double _hazeClock = 1.0;

    /// <summary>The scene's haze colour (the environment's fog colour), read at most twice a second; a soft grey when there is none.</summary>
    Color SceneHaze()
    {
        _hazeClock += GetProcessDeltaTime();
        if (_hazeClock < 0.5) return _haze;
        _hazeClock = 0;
        var vp = IsInsideTree() ? GetViewport() : null;
        var env = vp?.GetCamera3D()?.Environment ?? vp?.World3D?.Environment;
        if (env is not null && env.FogEnabled) _haze = env.FogLightColor;
        return _haze;
    }

    /// <summary>The streak colour: the rain's own cool grey moved a third of the way to the scene's haze, so it sits in the map's light instead of over it.</summary>
    static Color RainTint(Color own, Color haze) => own.Lerp(new Color(Math.Clamp(haze.R, 0.2f, 1f), Math.Clamp(haze.G, 0.2f, 1f), Math.Clamp(haze.B, 0.2f, 1f)), 0.33f);

    static void SetStreak(ShaderMaterial? m, Vector3 fall, Color tint, float alpha)
    {
        if (m is null) return;
        m.SetShaderParameter("fall_dir", fall);
        m.SetShaderParameter("tint", new Color(tint.R, tint.G, tint.B, Math.Clamp(alpha, 0f, 1f)));
    }

    /// <summary>The near streaks' alpha now (selftest).</summary>
    public float RainAlpha => _rainMaterial?.GetShaderParameter("tint").AsColor().A ?? 0f;

    static Shader? _streakShader;

    /// <summary>
    /// The rain streak (the weather finish): a quad turned about the fall direction to face the camera (an axial billboard), so a streak always lies along the fall and leans with the wind whatever the camera's pitch
    /// (the full billboard before tilted with the view); a soft Gaussian profile across it and a taper along it (a faint tail, a brighter head, a short soft tip) instead of a flat rectangle; drops nearer than about
    /// <paramref name="nearFadeM"/> fade out (no smear across the lens); the scene's fog still applies, so far rain sinks into the haze. Unshaded, no depth write, both renderers.
    /// </summary>
    static ShaderMaterial RainMaterial(Color color, float nearFadeM)
    {
        _streakShader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
                uniform vec4 tint : source_color = vec4(0.74, 0.80, 0.90, 0.3);
                uniform vec3 fall_dir = vec3(0.0, -1.0, 0.0);
                uniform float near_fade = 1.2;
                varying float v_fade;
                void vertex() {
                    vec3 centre = MODEL_MATRIX[3].xyz;
                    float sc = length(MODEL_MATRIX[1].xyz);
                    vec3 axis = normalize(fall_dir);
                    vec3 to_cam = CAMERA_POSITION_WORLD - centre;
                    float dist = max(length(to_cam), 1e-4);
                    vec3 side = cross(axis, to_cam / dist);
                    float sl = length(side);
                    side = sl > 1e-3 ? side / sl : vec3(1.0, 0.0, 0.0);
                    vec3 world = centre + side * (VERTEX.x * sc) - axis * (VERTEX.y * sc);
                    POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(world, 1.0));
                    v_fade = smoothstep(0.35, 0.35 + near_fade, dist);
                }
                void fragment() {
                    float x = (UV.x - 0.5) * 2.0;
                    float across = exp(-x * x * 3.5);
                    float along = mix(0.25, 1.0, smoothstep(0.0, 0.85, UV.y)) * (1.0 - smoothstep(0.9, 1.0, UV.y)) * smoothstep(0.0, 0.08, UV.y);
                    ALBEDO = tint.rgb * COLOR.rgb;
                    ALPHA = clamp(tint.a * across * along * v_fade * COLOR.a, 0.0, 1.0);
                }
                """,
        };
        var m = new ShaderMaterial { Shader = _streakShader };
        m.SetShaderParameter("tint", color);
        m.SetShaderParameter("near_fade", nearFadeM);
        return m;
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
