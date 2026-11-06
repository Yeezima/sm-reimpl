using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// Ashfall draft 7's map-wide look (Musou Mode only, the map tfw_war; owner, 2026-11-06: "fires around the map, ashes flying around the sky, dark clouds in the skybox, light poking through", "significant visual overhaul
/// using mapwide visual effects"; docs/musou-ashfall-graybox.md "Draft 7"). Everything is Godot's own features and procedural textures; every value is **ours** (placeholder demonstration, refine register):
/// a storm sky shader (dark clouds, a warm sunset band, the sun breaking through gaps with rays), a low warm sun with long shadows and a cool dim ambient so stone reads grey, ACES tone mapping with a
/// desaturated, contrasty grade, depth fog scattering the sun, volumetric fog for light shafts (Forward+ at medium lighting and up), soft light-shaft beams (every renderer), fires with embers and smoke at the
/// level's fire spots (<see cref="AshfallAtmosphere.FireSpots"/>), tall smoke plumes from the bigger ruins, and ash, rock flakes and embers drifting around the camera. Counts scale with the lighting preset;
/// the Compatibility renderer gets the sky, grade, fires and a thinner ash field (no volumetric fog, no glow). <c>--ashfall-weather plain</c> leaves it out. Every environment and sun value it writes is put
/// back when the node leaves the tree, unless something else has written it since (the next map's lighting).
/// Concept pass (the owner's concept art, 2026-11-06; docs/musou-ashfall-graybox.md "Concept pass"): the shade lifted to a grey stone (the owner: "something is wrong with the shadows": near-black shade under a 26 degree
/// sun), the sun higher (shorter shadows), layered grey haze (height fog), twice the airborne ash and flakes plus far debris chunks and dust banks, nothing large near the camera (every airborne layer fades out close to
/// it), a stronger warm break and shafts behind the keep, and grime on every surface (<see cref="AshfallGrime"/>). <c>--ashfall-concept off</c> (test aid): draft 7's values.
/// </summary>
public partial class AshfallStorm : Node3D
{
    /// <summary>The sun: elevation above the horizon and azimuth (degrees from game north toward east; the stronghold lies north, so the attackers look into the low sun). Ours.</summary>
    public const float SunElevation = 32f, SunAzimuth = -22f;
    /// <summary>Draft 7's sun elevation (<c>--ashfall-concept off</c>).</summary>
    public const float OldSunElevation = 26f;

    /// <summary>
    /// Which concept pass applies: 2 the concept light pass (default; brighter field, the warm break behind the keep, the ruined boundary top), 1 the first concept pass (<c>--ashfall-concept v1</c>),
    /// 0 draft 7's values (<c>--ashfall-concept off</c>). Test aids.
    /// </summary>
    public static int ConceptLevel
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-concept");
            string? v = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            return v switch { "off" => 0, "v1" => 1, _ => 2 };
        }
    }

    /// <summary>Whether a concept pass's values apply (default); <c>--ashfall-concept off</c> keeps draft 7's.</summary>
    public static bool Concept => ConceptLevel >= 1;

    /// <summary>The concept light pass (owner, 2026-11-06: "Musou currently feels too dark"; his concept art as the target): the values of <see cref="LightPass"/>.</summary>
    public static bool LightPassOn => ConceptLevel >= 2;

    /// <summary>
    /// The concept light pass's values (ours, placeholder demonstration; refine register "Ashfall concept light pass"). The warm break in the clouds sits low behind the keep (its elevation, degrees; the sun itself
    /// stays at <see cref="SunElevation"/> for the shadows' length, hidden in the cloud); a cool fill from the attackers' side lifts what the sun leaves in shade (backlit players and Orks); the boundary is drawn
    /// to a ruined top about <see cref="BoundaryTop"/> m high (its collision keeps the full 64 m), so the bright horizon shows over it from the field.
    /// </summary>
    public static class LightPass
    {
        public const float BreakElevation = 12f, FillElevation = 40f, FillAzimuthFromSun = 205f, FillEnergy = 0.42f, SunEnergy = 1.35f, Exposure = 0.9f, Saturation = 0.68f, Contrast = 1.16f;
        public const float AmbientEnergy = 1.05f, BoundaryTop = 13f;
        /// <summary>The Compatibility renderer (no grime overlay on the models, its sky drawn darker): exposure and sun a step lower, the sky lifted.</summary>
        public const float CompatExposure = 0.8f, CompatSunEnergy = 0.85f, CompatAmbientEnergy = 0.75f, CompatSkyGain = 0.95f;
        public static readonly Color Sun = new(1.0f, 0.85f, 0.68f), Fill = new(0.70f, 0.76f, 0.86f), Ambient = new(0.50f, 0.52f, 0.56f), Fog = new(0.33f, 0.34f, 0.355f), VolumetricAlbedo = new(0.55f, 0.55f, 0.56f);
    }

    /// <summary>Wind (game frame, metres per second): ash and smoke drift toward the east-north-east. Ours.</summary>
    static readonly Vector3 WindGame = new(2.4f, 0.9f, 0f);

    static readonly Basis GameToGodot = new(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));
    static Vector3 G(Vector3 game) => GameToGodot * game;
    static Vector3 G(System.Numerics.Vector3 game) => G(new Vector3(game.X, game.Y, game.Z));

    readonly List<(GodotObject Obj, StringName Prop, Variant Base, Variant Written)> _writes = [];
    readonly List<(OmniLight3D Light, float Energy, float Phase)> _lights = [];
    Node3D _world = null!;
    Node3D? _follow;
    float _quality;
    double _time;

    /// <summary>One line for the log: what was built.</summary>
    public string Report { get; private set; } = "";

    /// <summary>Whether the command line leaves the storm look out (<c>--ashfall-weather plain</c>).</summary>
    public static bool Off
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-weather");
            string? v = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            if (v is not (null or "storm" or "plain")) GD.Print($"ashfall: --ashfall-weather '{v}' is not storm or plain; storm is used");
            return v == "plain";
        }
    }

    /// <summary>Builds the look under <paramref name="parent"/> (a node in game space, freed with the map) and grades <paramref name="sun"/> and <paramref name="env"/>.</summary>
    public static AshfallStorm Attach(Node3D parent, AshfallLevel level, AshfallDressing dressing, DirectionalLight3D sun, Godot.Environment env)
    {
        var storm = new AshfallStorm { Name = "AshfallStorm" };
        parent.AddChild(storm);
        storm.Build(level, dressing, sun, env);
        GD.Print("ashfall storm: " + storm.Report);
        return storm;
    }

    static float QualityScale()
    {
        if (SafeMode.Compatibility) return 0.35f;
        return MapLightingView.Preset switch
        {
            MapLightingView.LightingPreset.Off => 0.35f,
            MapLightingView.LightingPreset.Low => 0.55f,
            MapLightingView.LightingPreset.High => 1.2f,
            _ => 1f,
        };
    }

    void Write(GodotObject obj, StringName prop, Variant value)
    {
        var b = obj.Get(prop);
        obj.Set(prop, value);
        _writes.Add((obj, prop, b, obj.Get(prop)));
    }

    public override void _ExitTree()
    {
        // Put back what this look wrote, unless someone else wrote it since (the next map's lighting, applied before this node is freed).
        for (int i = _writes.Count - 1; i >= 0; i--)
        {
            var (obj, prop, b, written) = _writes[i];
            if (!IsInstanceValid(obj)) continue;
            if (Same(obj.Get(prop), written)) obj.Set(prop, b);
        }
        _writes.Clear();
    }

    static bool Same(Variant a, Variant b)
    {
        if (a.VariantType != b.VariantType) return false;
        return a.VariantType switch
        {
            Variant.Type.Nil => true,
            Variant.Type.Object => a.AsGodotObject() == b.AsGodotObject(),
            _ => Equals(a.Obj, b.Obj),
        };
    }

    void Build(AshfallLevel level, AshfallDressing dressing, DirectionalLight3D sun, Godot.Environment env)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _quality = QualityScale();
        bool compat = SafeMode.Compatibility;
        bool volumetric = !compat && MapLightingView.Preset >= MapLightingView.LightingPreset.Medium;
        // Children in Godot's own frame (Y up): the parent is game space (Z up).
        _world = new Node3D { Name = "World", Basis = GameToGodot.Inverse() };
        AddChild(_world);

        // ---- the sun: low, warm, long shadows; the light shafts in the volumetric fog come from it.
        bool concept = Concept, light = LightPassOn;
        float el = Mathf.DegToRad(concept ? SunElevation : OldSunElevation), az = Mathf.DegToRad(SunAzimuth);
        var toSunGame = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Cos(az) * MathF.Cos(el), MathF.Sin(el));
        var toSun = G(toSunGame).Normalized();
        Write(sun, Node3D.PropertyName.Basis, (sun.GetParentNode3D()?.GlobalBasis.Inverse() ?? Basis.Identity) * Basis.LookingAt(-toSun, Vector3.Up));
        Write(sun, Light3D.PropertyName.LightColor, light ? LightPass.Sun : new Color(1.0f, 0.84f, 0.68f));
        Write(sun, Light3D.PropertyName.LightEnergy, light ? (compat ? LightPass.CompatSunEnergy : LightPass.SunEnergy) : concept ? 1.15f : 1.4f);
        Write(sun, Light3D.PropertyName.ShadowEnabled, true);
        Write(sun, DirectionalLight3D.PropertyName.DirectionalShadowMaxDistance, 220f);
        Write(sun, Light3D.PropertyName.ShadowBlur, concept ? 2.0f : 1.4f);
        Write(sun, Light3D.PropertyName.LightVolumetricFogEnergy, volumetric ? (light ? 0.6f : concept ? 0.55f : 1.1f) : 0f);
        Write(sun, DirectionalLight3D.PropertyName.SkyMode, (int)DirectionalLight3D.SkyModeEnum.LightAndSky);

        // ---- the sky: the storm shader (in every renderer).
        var sky = new Sky
        {
            SkyMaterial = light ? (PanoSkyMaterial(az, compat) ?? BreakSkyMaterial(az, compat)) : SkyMaterialFor(concept),
            RadianceSize = Sky.RadianceSizeEnum.Size64,
            ProcessMode = Sky.ProcessModeEnum.Incremental,
        };
        Write(env, Godot.Environment.PropertyName.BackgroundMode, (int)Godot.Environment.BGMode.Sky);
        Write(env, Godot.Environment.PropertyName.Sky, sky);
        Write(env, Godot.Environment.PropertyName.AmbientLightSource, (int)Godot.Environment.AmbientSource.Color);
        // cool grey: the stone takes its grey from the textures, the warmth from the sun. Concept pass: the shade a readable grey (was 0.42/0.44/0.50 at 0.6: near black under the grade).
        Write(env, Godot.Environment.PropertyName.AmbientLightColor, light ? LightPass.Ambient : concept ? new Color(0.49f, 0.50f, 0.52f) : new Color(0.42f, 0.44f, 0.50f));
        Write(env, Godot.Environment.PropertyName.AmbientLightEnergy, light ? (compat ? LightPass.CompatAmbientEnergy : LightPass.AmbientEnergy) : concept ? 0.95f : 0.6f);
        Write(env, Godot.Environment.PropertyName.ReflectedLightSource, (int)Godot.Environment.ReflectionSource.Sky);
        // ---- grade: ACES, a touch under-exposed, contrast up, colour down (warm light, cold shade).
        Write(env, Godot.Environment.PropertyName.TonemapMode, (int)Godot.Environment.ToneMapper.Aces);
        Write(env, Godot.Environment.PropertyName.TonemapExposure, light ? (compat ? LightPass.CompatExposure : LightPass.Exposure) : concept ? 0.78f : 0.8f);
        Write(env, Godot.Environment.PropertyName.TonemapWhite, 6f);
        Write(env, Godot.Environment.PropertyName.AdjustmentEnabled, true);
        Write(env, Godot.Environment.PropertyName.AdjustmentBrightness, light ? 1f : concept ? 0.97f : 0.92f);
        Write(env, Godot.Environment.PropertyName.AdjustmentContrast, light ? LightPass.Contrast : concept ? 1.08f : 1.2f);
        Write(env, Godot.Environment.PropertyName.AdjustmentSaturation, light ? LightPass.Saturation : concept ? 0.55f : 0.7f);
        // ---- depth fog: dark smoke-grey, scattering the sun, thinning into the sky.
        Write(env, Godot.Environment.PropertyName.FogEnabled, true);
        Write(env, Godot.Environment.PropertyName.FogMode, (int)Godot.Environment.FogModeEnum.Exponential);
        Write(env, Godot.Environment.PropertyName.FogLightColor, light ? LightPass.Fog : concept ? new Color(0.21f, 0.21f, 0.22f) : new Color(0.13f, 0.13f, 0.14f));
        Write(env, Godot.Environment.PropertyName.FogLightEnergy, 1f);
        Write(env, Godot.Environment.PropertyName.FogSunScatter, light ? 0.05f : concept ? 0.05f : 0.12f);   // concept pass: the haze toward the sun warm grey, not mustard
        Write(env, Godot.Environment.PropertyName.FogDensity, concept ? 0.0036f : 0.0032f);
        if (concept)
        {
            // layered grey haze: thicker low down, between the ruins (Godot's height fog; ours)
            Write(env, Godot.Environment.PropertyName.FogHeight, 7f);
            Write(env, Godot.Environment.PropertyName.FogHeightDensity, 0.035f);
        }
        Write(env, Godot.Environment.PropertyName.FogAerialPerspective, light ? 0.3f : 0.15f);   // light pass: far ruins take the sky's colour behind them (the warm break shows through the haze)
        Write(env, Godot.Environment.PropertyName.FogSkyAffect, 0.1f);
        if (volumetric)
        {
            // Light poking through: shafts where the ruins and the tower cut the low sun.
            Write(env, Godot.Environment.PropertyName.VolumetricFogEnabled, true);
            Write(env, Godot.Environment.PropertyName.VolumetricFogDensity, 0.0035f);
            Write(env, Godot.Environment.PropertyName.VolumetricFogAlbedo, light ? LightPass.VolumetricAlbedo : new Color(0.36f, 0.36f, 0.37f));
            Write(env, Godot.Environment.PropertyName.VolumetricFogEmission, new Color(0f, 0f, 0f));
            Write(env, Godot.Environment.PropertyName.VolumetricFogAnisotropy, light ? 0.45f : 0.6f);   // light pass: less of a warm veil toward the high sun
            Write(env, Godot.Environment.PropertyName.VolumetricFogLength, 120f);
            Write(env, Godot.Environment.PropertyName.VolumetricFogDetailSpread, 2f);
            Write(env, Godot.Environment.PropertyName.VolumetricFogAmbientInject, light ? 0.35f : 0.1f);   // light pass: the fog in the shade a grey, not a dark veil
            Write(env, Godot.Environment.PropertyName.VolumetricFogSkyAffect, 0f);
            Write(env, Godot.Environment.PropertyName.VolumetricFogTemporalReprojectionEnabled, true);
        }
        if (!compat)
        {
            // Fires and embers bloom.
            Write(env, Godot.Environment.PropertyName.GlowEnabled, true);
            Write(env, Godot.Environment.PropertyName.GlowIntensity, 0.7f);
            Write(env, Godot.Environment.PropertyName.GlowBloom, 0.03f);
            Write(env, Godot.Environment.PropertyName.GlowHdrThreshold, 1.0f);
            Write(env, Godot.Environment.PropertyName.GlowBlendMode, (int)Godot.Environment.GlowBlendModeEnum.Softlight);
        }

        // ---- fires, embers and smoke at the level's spots.
        var spots = AshfallAtmosphere.FireSpots(level, dressing);
        foreach (var s in spots) AddFire(s, compat);

        // ---- light shafts out of the cloud gaps (soft beams, every renderer).
        int shafts = AddShafts(toSun);

        // ---- light pass: a cool fill from the attackers' side (no shadows, not in the sky) and the boundary's ruined top.
        string lightNote;
        if (light)
        {
            float faz = az + Mathf.DegToRad(LightPass.FillAzimuthFromSun), fel = Mathf.DegToRad(LightPass.FillElevation);
            var toFill = G(new Vector3(MathF.Sin(faz) * MathF.Cos(fel), MathF.Cos(faz) * MathF.Cos(fel), MathF.Sin(fel))).Normalized();
            _world.AddChild(new DirectionalLight3D
            {
                Name = "Fill", LightColor = LightPass.Fill, LightEnergy = LightPass.FillEnergy, ShadowEnabled = false, LightVolumetricFogEnergy = 0f, LightSpecular = 0.2f,
                SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly, Basis = Basis.LookingAt(-toFill, Vector3.Up),
            });
            int cut = CutBoundary(GetParent()?.GetParent(), LightPass.BoundaryTop);
            TintGround(GetParent()?.GetParent());
            AshfallGrime.OverlayMaterial().SetShaderParameter("wash", AshfallGrime.LightPassWash);
            lightNote = $"light pass on (fill x{LightPass.FillEnergy:0.##}, break {LightPass.BreakElevation:0} deg, boundary drawn to ~{LightPass.BoundaryTop:0} m on {cut} material(s))";
        }
        else lightNote = $"light pass off (--ashfall-concept {(concept ? "v1" : "off")})";

        // ---- ash, rock flakes and embers around the camera.
        _follow = new Node3D { Name = "AroundCamera" };
        _world.AddChild(_follow);
        float airborne = concept ? AirborneScale : 1f;
        int ash = (int)(2200 * _quality * airborne), rocks = (int)(140 * _quality * airborne), sparks = (int)(180 * _quality);
        int debris = concept ? (int)(160 * _quality) : 0, dust = concept ? (int)(70 * _quality) : 0;
        _follow.AddChild(AshField("Ash", ash, new Vector3(45, 22, 45), AshMaterial(concept ? NearFadeSmall : 0f), new QuadMesh { Size = new Vector2(0.10f, 0.07f) }, 0.6f, 1.6f, 10f));
        _follow.AddChild(AshField("Flakes", rocks, new Vector3(55, 28, 55), RockMaterial(concept ? NearFadeFlakes : 0f), new QuadMesh { Size = new Vector2(0.28f, 0.2f) }, 0.5f, 1.4f, 9f));
        _follow.AddChild(AshField("Embers", sparks, new Vector3(40, 16, 40), EmberMaterial(1.5f), new QuadMesh { Size = new Vector2(0.06f, 0.06f) }, 0.4f, 1.1f, 7f, rise: 0.7f));
        if (debris > 0)   // tumbling dark chunks seen at a distance (the concept's), never near the camera
            _follow.AddChild(AshField("Debris", debris, new Vector3(80, 34, 80), RockMaterial(NearFadeDebris, 0.09f), new QuadMesh { Size = new Vector2(0.9f, 0.7f) }, 0.5f, 1.3f, 12f, rise: -0.25f));
        if (dust > 0)     // soft banks of blown dust between the ruins, far only (overdraw: few, large, faint)
            _follow.AddChild(AshField("Dust", dust, new Vector3(95, 6, 95), DustMaterial(), new QuadMesh { Size = new Vector2(14f, 7f) }, 0.7f, 1.5f, 18f, rise: 0.05f, spin: 12f));

        int grimed = concept ? AshfallGrime.Apply(GetParent()?.GetParent(), _quality, compat) : 0;
        Report = $"{spots.Count(s => s.Plume)} smoke plumes, {spots.Count(s => !s.Plume)} fires ({_lights.Count} fire lights), {shafts} light shafts, ash {ash} + flakes {rocks} + embers {sparks} + debris {debris} + dust banks {dust} around the camera; grime on {grimed} ground materials{(AshfallGrime.Overlay(_quality, compat) ? " and every kit model (overlay)" : "")}; concept pass {(concept ? "on" : "off")}, {lightNote}; "
               + $"quality x{_quality:0.##} ({(compat ? "Compatibility renderer: no volumetric fog, no glow" : $"lighting {MapLightingView.Preset}, volumetric shafts {(volumetric ? "on" : "off")}")}); built in {watch.ElapsedMilliseconds} ms";
    }

    bool _logged;

    public override void _Process(double delta)
    {
        _time += delta;
        if (!_logged && _time > 1.0 && GetViewport()?.GetCamera3D() is { Environment: null } c0)   // on the field (aboard the Stormbird the camera has the hub's own environment)
        {
            // The values the camera draws with once every layer has run (the OPTIONS lighting preset grades on top of this look each frame): evidence for the look pass.
            _logged = true;
            var e = c0.GetWorld3D()?.Environment;
            if (e is not null)
                GD.Print($"ashfall storm: applied environment: exposure {e.TonemapExposure:0.###}, brightness {e.AdjustmentBrightness:0.##}, contrast {e.AdjustmentContrast:0.##}, saturation {e.AdjustmentSaturation:0.##}, ambient {e.AmbientLightColor} x{e.AmbientLightEnergy:0.##}, "
                         + $"fog {e.FogLightColor} density {e.FogDensity:0.####}, volumetric {(e.VolumetricFogEnabled ? $"on {e.VolumetricFogDensity:0.####}" : "off")}, glow {(e.GlowEnabled ? $"{e.GlowIntensity:0.##}" : "off")}, ssao {(e.SsaoEnabled ? "on" : "off")}, lighting preset {LightingGradeView.Current}");
        }
        var cam = GetViewport()?.GetCamera3D();
        if (_follow is not null && cam is not null)
        {
            _follow.GlobalPosition = cam.GlobalPosition;
            _follow.Visible = cam.Environment is null;   // aboard the Stormbird (its own environment) no ash falls
        }
        foreach (var (light, energy, phase) in _lights)
        {
            float t = (float)_time + phase;
            float f = 0.78f + 0.12f * MathF.Sin(t * 9.1f) + 0.07f * MathF.Sin(t * 23.3f + 1.3f) + 0.05f * MathF.Sin(t * 4.2f + 2.1f);
            light.LightEnergy = energy * f;
        }
    }

    // ---------------------------------------------------------------------------------------------------- fires

    void AddFire(AshfallFireSpot s, bool compat)
    {
        var at = G(s.Position);
        var node = new Node3D { Name = s.Plume ? "Plume" : "Fire", Position = at };
        _world.AddChild(node);
        float k = s.Size;
        // Flames
        node.AddChild(Emitter("Flames", Math.Max(8, (int)(26 * k * Math.Min(1f, _quality + 0.3f))), 0.9f, FlameProcess(k), FlameMaterial(), new QuadMesh { Size = new Vector2(1.1f, 2.0f) }, s.Plume ? 400f : 160f, new Aabb(new Vector3(-3, -1, -3) * k, new Vector3(6, 7, 6) * k)));
        // Embers rising from it
        node.AddChild(Emitter("Embers", Math.Max(6, (int)(18 * k * _quality)), 2.6f, EmberProcess(k), EmberMaterial(3f), new QuadMesh { Size = new Vector2(0.07f, 0.07f) }, 120f, new Aabb(new Vector3(-6, -1, -6) * k, new Vector3(12, 14, 12) * k)));
        // Smoke: a short dark column, or a tall plume seen across the map
        if (s.Plume)
            node.AddChild(Emitter("Smoke", Math.Max(16, (int)(44 * _quality)), 16f, SmokeProcess(k, 4.2f, 2.2f, 16f), SmokeMaterial(0.07f, compat), new QuadMesh { Size = new Vector2(1f, 1f) }, 900f, new Aabb(new Vector3(-60, -2, -60), new Vector3(120, 110, 120))));
        else
            node.AddChild(Emitter("Smoke", Math.Max(6, (int)(12 * _quality)), 6f, SmokeProcess(k, 2.0f, 0.7f, 5f), SmokeMaterial(0.10f, compat), new QuadMesh { Size = new Vector2(1f, 1f) }, 260f, new Aabb(new Vector3(-20, -1, -20), new Vector3(40, 30, 40))));
        // Light (no shadows; fades with distance)
        var light = new OmniLight3D
        {
            Name = "Light", Position = new Vector3(0, 1.1f * k, 0), LightColor = new Color(1f, 0.52f, 0.2f), LightEnergy = 2.2f * k, OmniRange = 9f * k, ShadowEnabled = false,
            DistanceFadeEnabled = true, DistanceFadeBegin = 70f, DistanceFadeLength = 30f, LightVolumetricFogEnergy = 0f,
        };
        if (!compat || _lights.Count < 12) { node.AddChild(light); _lights.Add((light, light.LightEnergy, s.Position.X * 0.37f + s.Position.Y * 0.11f)); }
        else light.Free();
    }

    static GpuParticles3D Emitter(string name, int amount, float lifetime, ParticleProcessMaterial process, Material material, Mesh mesh, float range, Aabb aabb)
    {
        if (mesh is PrimitiveMesh pm) pm.Material = material;
        return new GpuParticles3D
        {
            Name = name, Amount = amount, Lifetime = lifetime, Preprocess = lifetime, Randomness = 0.4f, LocalCoords = false, ProcessMaterial = process, DrawPass1 = mesh,
            VisibilityAabb = aabb, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityRangeEnd = range, VisibilityRangeEndMargin = range * 0.1f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self, FixedFps = 30, Interpolate = true,
        };
    }

    static CurveTexture Curve(params (float X, float Y)[] points)
    {
        var c = new Curve { MinValue = 0f, MaxValue = Math.Max(1f, points.Max(p => p.Y)) };
        foreach (var (x, y) in points) c.AddPoint(new Vector2(x, y));
        return new CurveTexture { Curve = c };
    }

    static GradientTexture1D Ramp(params (float At, Color Colour)[] stops)
    {
        var g = new Gradient();
        g.Offsets = stops.Select(s => s.At).ToArray();
        g.Colors = stops.Select(s => s.Colour).ToArray();
        return new GradientTexture1D { Gradient = g };
    }

    static ParticleProcessMaterial FlameProcess(float k) => new()
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.55f * k,
        Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = 0.8f * k, InitialVelocityMax = 1.8f * k, Gravity = new Vector3(0, 1.4f, 0),
        ScaleMin = 0.7f * k, ScaleMax = 1.3f * k, ScaleCurve = Curve((0f, 0.5f), (0.2f, 1f), (1f, 0.2f)),
        AngleMin = -20f, AngleMax = 20f,
        ColorRamp = Ramp((0f, new Color(1.9f, 1.05f, 0.35f, 0.55f)), (0.3f, new Color(1.7f, 0.6f, 0.12f, 0.5f)), (0.7f, new Color(0.7f, 0.15f, 0.03f, 0.3f)), (1f, new Color(0.15f, 0.03f, 0.01f, 0f))),
    };

    static ParticleProcessMaterial EmberProcess(float k) => new()
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.8f * k,
        Direction = Vector3.Up, Spread = 30f, InitialVelocityMin = 1.5f, InitialVelocityMax = 3.6f, Gravity = new Vector3(WindGame.X * 0.3f, 0.6f, -WindGame.Y * 0.3f),
        TurbulenceEnabled = true, TurbulenceNoiseStrength = 2.2f, TurbulenceNoiseScale = 3f, TurbulenceInfluenceMin = 0.05f, TurbulenceInfluenceMax = 0.15f,
        ScaleMin = 0.6f, ScaleMax = 1.4f, ScaleCurve = Curve((0f, 1f), (1f, 0.2f)),
        ColorRamp = Ramp((0f, new Color(4f, 1.8f, 0.5f, 1f)), (0.6f, new Color(2.4f, 0.7f, 0.15f, 1f)), (1f, new Color(0.6f, 0.1f, 0.02f, 0f))),
    };

    static ParticleProcessMaterial SmokeProcess(float k, float rise, float startScale, float endScale) => new()
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.8f * k,
        Direction = (Vector3.Up * rise + G(WindGame) * 0.25f).Normalized(), Spread = 6f, InitialVelocityMin = rise * 0.8f, InitialVelocityMax = rise * 1.2f, Gravity = G(WindGame) * 0.04f + new Vector3(0, 0.1f, 0),
        DampingMin = 0.05f, DampingMax = 0.15f,
        ScaleMin = startScale * k, ScaleMax = startScale * 1.4f * k, ScaleCurve = Curve((0f, 0.25f), (1f, endScale / startScale * 0.5f)),
        AngleMin = 0f, AngleMax = 360f, AngularVelocityMin = -8f, AngularVelocityMax = 8f,
        ColorRamp = Ramp((0f, new Color(1f, 1f, 1f, 0f)), (0.06f, new Color(1f, 1f, 1f, 0.95f)), (0.5f, new Color(1.3f, 1.3f, 1.3f, 0.7f)), (1f, new Color(1.8f, 1.8f, 1.8f, 0f))),
    };

    static Texture2D? _soft, _flake;

    /// <summary>A soft round blob (procedural).</summary>
    static Texture2D Soft()
    {
        if (_soft is not null) return _soft;
        var g = new Gradient();
        g.Offsets = [0f, 0.45f, 1f];
        g.Colors = [new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.45f), new Color(1, 1, 1, 0)];
        return _soft = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64 };
    }

    /// <summary>A jagged flake (procedural): an irregular polygon with a soft rim.</summary>
    static Texture2D Flake()
    {
        if (_flake is not null) return _flake;
        const int N = 32;
        var img = Image.CreateEmpty(N, N, true, Image.Format.Rgba8);
        Span<float> r = stackalloc float[7];
        for (int i = 0; i < r.Length; i++) r[i] = 0.55f + 0.4f * ((i * 37 % 11) / 10f);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f;
                float ang = (MathF.Atan2(dy, dx) / MathF.Tau + 1f) % 1f * r.Length;
                int a = (int)ang % r.Length; float t = ang - MathF.Floor(ang);
                float edge = r[a] * (1 - t) + r[(a + 1) % r.Length] * t;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float alpha = Math.Clamp((edge - d) * 8f, 0f, 1f);
                img.SetPixel(x, y, new Color(1, 1, 1, alpha));
            }
        img.GenerateMipmaps();
        return _flake = ImageTexture.CreateFromImage(img);
    }

    static StandardMaterial3D Particle(Texture2D tex, Color colour, bool additive, bool shaded = false) => new()
    {
        ShadingMode = shaded ? BaseMaterial3D.ShadingModeEnum.PerVertex : BaseMaterial3D.ShadingModeEnum.Unshaded,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
        VertexColorUseAsAlbedo = true, AlbedoTexture = tex, AlbedoColor = colour,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled, DisableReceiveShadows = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableFog = additive,
    };

    static StandardMaterial3D FlameMaterial() => Particle(Soft(), new Color(1, 1, 1, 1), additive: true);

    static StandardMaterial3D EmberMaterial(float glow) => Particle(Soft(), new Color(glow, glow, glow, 1), additive: true);

    static StandardMaterial3D SmokeMaterial(float grey, bool compat)
    {
        // unshaded: a dark smoke-grey that reads against the sky and the stone alike
        return Particle(Soft(), new Color(grey, grey * 0.96f, grey * 0.92f, 0.85f), additive: false);
    }

    /// <summary>Twice draft 7's ash and flakes (the owner: "roughly double the amount and visual presence of airborne dirt and dust"; ours).</summary>
    public const float AirborneScale = 2f;
    /// <summary>Metres from the camera inside which each airborne layer is invisible (it fades in over the next as many again; ours): nothing large drifts across the aim.</summary>
    public const float NearFadeSmall = 1.2f, NearFadeFlakes = 3f, NearFadeDebris = 14f, NearFadeDust = 22f;

    static StandardMaterial3D NearFaded(StandardMaterial3D m, float near)
    {
        if (near <= 0f) return m;
        m.DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha;
        m.DistanceFadeMinDistance = near;
        m.DistanceFadeMaxDistance = near * 2f;
        return m;
    }

    static StandardMaterial3D AshMaterial(float near = 0f) => NearFaded(Particle(Flake(), new Color(0.30f, 0.29f, 0.28f, 0.9f), additive: false), near);

    static StandardMaterial3D RockMaterial(float near = 0f, float grey = 0.07f) => NearFaded(Particle(Flake(), new Color(grey, grey * 0.93f, grey * 0.86f, 1f), additive: false), near);

    static StandardMaterial3D DustMaterial() => NearFaded(Particle(Soft(), new Color(0.34f, 0.31f, 0.28f, 0.10f), additive: false), NearFadeDust);

    /// <summary>A field of drifting particles around the camera (world coordinates; the emitter follows the camera, the flakes stay where they are).</summary>
    static GpuParticles3D AshField(string name, int amount, Vector3 extents, Material material, QuadMesh mesh, float scaleMin, float scaleMax, float lifetime, float rise = -0.4f, float spin = 160f)
    {
        var wind = G(WindGame);
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = extents,
            Direction = wind.Normalized(), Spread = 25f, InitialVelocityMin = wind.Length() * 0.6f, InitialVelocityMax = wind.Length() * 1.4f,
            Gravity = new Vector3(0, rise, 0),
            TurbulenceEnabled = true, TurbulenceNoiseStrength = 1.6f, TurbulenceNoiseScale = 6f, TurbulenceInfluenceMin = 0.04f, TurbulenceInfluenceMax = 0.12f,
            ScaleMin = scaleMin, ScaleMax = scaleMax, AngleMin = 0f, AngleMax = 360f, AngularVelocityMin = -spin, AngularVelocityMax = spin,
            ColorRamp = Ramp((0f, new Color(1, 1, 1, 0)), (0.1f, new Color(1, 1, 1, 1)), (0.85f, new Color(1, 1, 1, 1)), (1f, new Color(1, 1, 1, 0))),
        };
        mesh.Material = material;
        return new GpuParticles3D
        {
            Name = name, Amount = Math.Max(1, amount), Lifetime = lifetime, Preprocess = lifetime, Randomness = 0.3f, LocalCoords = false, ProcessMaterial = process, DrawPass1 = mesh,
            VisibilityAabb = new Aabb(-extents - new Vector3(30, 20, 30), (extents + new Vector3(30, 20, 30)) * 2f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            FixedFps = 30, Interpolate = true,
        };
    }

    // ---------------------------------------------------------------------------------------------------- light shafts

    /// <summary>
    /// Soft beams out of the clouds toward the ground, along the light from the sun's side of the sky, over the far half of the map (deterministic). Additive, depth-faded, fading toward the ends and as the camera nears one.
    /// </summary>
    int AddShafts(Vector3 toSun)
    {
        var shader = new Shader { Code = ShaftShader };
        // the beams fall along the light, a little steeper than the sun (they come out of gaps in a cloud deck at about 160 m)
        var dir = (-toSun + new Vector3(0, -0.55f, 0)).Normalized();
        var spots = new (float X, float Y, float R)[] { (-40, 150, 9), (30, 175, 12), (90, 120, 8), (-120, 110, 10), (150, 190, 11), (-10, 60, 7), (-180, 180, 9) };
        int n = 0;
        foreach (var (x, y, r) in spots)
        {
            float len = 210f;
            var ground = G(new Vector3(x, y, 0f));
            var mid = ground - dir * (len / 2f);
            var mesh = new CylinderMesh { TopRadius = r * 1.35f, BottomRadius = r, Height = len, RadialSegments = 16, Rings = 1, CapTop = false, CapBottom = false };
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("strength", (Concept ? 0.045f : 0.03f) * (0.8f + 0.4f * ((n * 7 % 5) / 4f)));
            mat.SetShaderParameter("seed", n * 3.7f);
            mesh.Material = mat;
            var inst = new MeshInstance3D { Name = "Shaft" + n, Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            // the cylinder's axis (Y) along the beam
            var up = -dir;
            var side = up.Cross(Vector3.Forward).LengthSquared() > 1e-4f ? up.Cross(Vector3.Forward).Normalized() : Vector3.Right;
            var fwd = side.Cross(up).Normalized();
            inst.Transform = new Transform3D(new Basis(side, up, fwd), mid);
            _world.AddChild(inst);
            n++;
        }
        return n;
    }

    const string ShaftShader = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, cull_back, shadows_disabled, fog_disabled;
        uniform float strength = 0.08;
        uniform float seed = 0.0;
        uniform sampler2D depth_tex : hint_depth_texture, filter_linear;
        varying float along;
        varying vec3 wpos;
        void vertex() {
            along = UV.y;   // 0 at the top (the clouds), 1 at the ground
            wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }
        float n1(float x) { return fract(sin(x * 12.9898) * 43758.5453); }
        float vnoise(float x) { float i = floor(x); float f = fract(x); f = f * f * (3.0 - 2.0 * f); return mix(n1(i), n1(i + 1.0), f); }
        void fragment() {
            float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
            float edge = pow(facing, 2.5);                          // soft sides
            float ends = smoothstep(0.0, 0.35, along) * smoothstep(1.0, 0.75, along);
            float streak = 0.55 + 0.45 * vnoise(UV.x * 18.0 + seed + TIME * 0.05);
            float drift = 0.75 + 0.25 * sin(TIME * 0.21 + seed);
            float d = distance(CAMERA_POSITION_WORLD, wpos);
            float near_fade = smoothstep(6.0, 40.0, d);
            float scene = texture(depth_tex, SCREEN_UV).r;
            vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene, 1.0);
            vec4 view = INV_PROJECTION_MATRIX * ndc; view.xyz /= view.w;
            float soft = clamp((VERTEX.z - view.z) / 6.0, 0.0, 1.0);   // fades where the beam meets the ground or a wall
            ALBEDO = vec3(1.0, 0.78, 0.52);
            ALPHA = 1.0;
            ALBEDO *= strength * edge * ends * streak * drift * near_fade * soft;
        }
        """;

    // ---------------------------------------------------------------------------------------------------- the sky

    /// <summary>The storm sky; the concept pass makes the clouds charcoal (their lit edges and the horizon band less brown, ours).</summary>
    static ShaderMaterial SkyMaterialFor(bool concept)
    {
        var m = new ShaderMaterial { Shader = new Shader { Code = SkyShader } };
        if (concept)
        {
            m.SetShaderParameter("cloud_dark", new Vector3(0.028f, 0.029f, 0.032f));
            m.SetShaderParameter("cloud_lit", new Vector3(0.17f, 0.155f, 0.145f));
            m.SetShaderParameter("horizon", new Vector3(0.20f, 0.16f, 0.12f));
            m.SetShaderParameter("cover", 0.6f);
        }
        return m;
    }

    const string SkyShader = """
        shader_type sky;
        uniform vec3 zenith = vec3(0.022, 0.024, 0.03);
        uniform vec3 cloud_dark = vec3(0.035, 0.036, 0.042);
        uniform vec3 cloud_lit = vec3(0.30, 0.22, 0.16);
        uniform vec3 horizon = vec3(0.24, 0.17, 0.12);
        uniform vec3 glow = vec3(0.95, 0.48, 0.20);
        uniform float cover = 0.56;
        uniform float speed = 0.006;

        float h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
        float vn(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
        }
        float fbm(vec2 p) {
            float v = 0.0; float a = 0.5;
            for (int i = 0; i < 6; i++) { v += a * vn(p); p = p * 2.03 + vec2(1.7, 9.2); a *= 0.5; }
            return v;
        }
        void sky() {
            vec3 d = normalize(EYEDIR);
            vec3 s = normalize(LIGHT0_DIRECTION);
            float h = d.y;
            float sd = max(dot(d, s), 0.0);
            vec2 dh = normalize(d.xz + vec2(1e-5)); vec2 sh = normalize(s.xz + vec2(1e-5));
            float az = max(dot(dh, sh), 0.0);   // facing the sun's side of the horizon
            // behind the clouds: a dark zenith, a warm band at the horizon, brightest under the sun
            float band = exp(-max(h - 0.1, 0.0) * 5.0) * (0.2 + 0.8 * pow(az, 3.0));
            vec3 col = mix(horizon * 0.55, zenith, smoothstep(0.0, 0.45, h));
            col += glow * band * 0.4;
            col += glow * pow(sd, 16.0) * 0.7 + vec3(1.4, 1.05, 0.75) * pow(sd, 1200.0) * 3.0;
            // the cloud deck: projected on a plane, drifting; thinner just above the horizon so the band shows under it
            float t = TIME * speed;
            vec2 uv = d.xz / (max(h, 0.0) + 0.18) * 0.9;
            float n = fbm(uv * 1.3 + vec2(t, t * 0.4));
            float m = fbm(uv * 3.1 - vec2(t * 1.7, 0.0));
            float dens = smoothstep(cover - 0.18, cover + 0.22, n * 0.75 + m * 0.35);
            dens *= mix(0.35, 1.0, smoothstep(0.08, 0.3, h));   // thinner low down, where the warm band shows under the deck
            dens = max(dens, smoothstep(0.35, 0.9, h) * 0.85);   // heavy overhead
            // cloud colour: dark bellies, edges and thin parts lit warm toward the sun
            float thin = 1.0 - smoothstep(0.35, 0.95, dens);
            vec3 ccol = mix(cloud_dark, cloud_lit, clamp(pow(sd, 6.0) * 1.3 * thin + band * 0.25 * pow(az, 2.0), 0.0, 1.0));
            ccol += glow * pow(sd, 10.0) * thin * 0.6;
            col = mix(col, ccol, dens);
            // rays through the gaps around the sun
            vec3 a1 = normalize(cross(s, vec3(0.0, 1.0, 0.0)));
            vec3 a2 = cross(a1, s);
            float ang = atan(dot(d, a2), dot(d, a1));
            float rays = vn(vec2(ang * 9.0, 0.0)) * vn(vec2(ang * 23.0 + 3.0, 1.0));
            col += glow * rays * pow(sd, 6.0) * (1.0 - dens * 0.6) * 0.35 * step(-0.02, h);
            // below the horizon: smoke haze
            col = mix(col, horizon * 0.35, smoothstep(0.0, -0.08, h));
            COLOR = col;
        }
        """;

    // ---------------------------------------------------------------------------------------------------- the light pass: the break sky and the boundary's top

    /// <summary>Sets the kit boundary's drawn top (<see cref="AshfallKitView"/>'s masonry shader, <c>top_cut</c>) under <paramref name="root"/>; returns how many materials took it. Drawing only: the collision is unchanged.</summary>
    static int CutBoundary(Node? root, float top)
    {
        if (root?.FindChild("KitLook", true, false) is not Node kit || kit.GetNodeOrNull("Remainder/Boundary") is not MeshInstance3D { MaterialOverride: ShaderMaterial sm }
            || sm.Shader?.Code.Contains("uniform float top_cut") != true) return 0;
        sm.SetShaderParameter("top_cut", top);
        sm.SetShaderParameter("desat", 0.7f);   // the concept's cool grey wall, not the masonry texture's brown (ours)
        sm.SetShaderParameter("bounce", new Vector3(0.22f, 0.23f, 0.25f));   // a 64 m wall under open sky: its shaded faces a hazy grey, not near black (ours)
        return 1;
    }

    /// <summary>The ground kinds' and the boundary's tints under the light pass (ours): a cool mid grey stone like the concept's flagstones, not draft 7's cream and brown (a shader uniform of our own terrain material; nothing on disk changes).</summary>
    static readonly (string Kind, Color Tint)[] GroundTints = [("Paving", new Color(0.66f, 0.665f, 0.68f)), ("Curb", new Color(0.58f, 0.585f, 0.60f)), ("Ground", new Color(0.52f, 0.515f, 0.51f)), ("Road", new Color(0.46f, 0.46f, 0.47f)), ("Boundary", new Color(0.66f, 0.665f, 0.68f))];

    static void TintGround(Node? root)
    {
        if (root?.FindChild("KitLook", true, false) is not Node kit) return;
        foreach (var (kind, tint) in GroundTints)
            if (kit.GetNodeOrNull("Remainder/" + kind) is MeshInstance3D { MaterialOverride: ShaderMaterial sm } && sm.Shader?.Code.Contains("uniform vec4 tint") == true)
                sm.SetShaderParameter("tint", tint);
    }

    /// <summary>Test aid <c>--ashfall-sky pano|procedural</c> (default pano): the generated panorama sky or the procedural break sky.</summary>
    public static bool PanoSkyWanted
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-sky");
            return !(i >= 0 && i + 1 < args.Length && args[i + 1] == "procedural");
        }
    }

    static ImageTexture? _panoTexture;
    static bool _panoTried;

    /// <summary>The generated storm sky panorama (ours, our own generated image; read only from the kit library), or null when off, missing or unreadable (the caller falls back to the procedural sky).</summary>
    static ImageTexture? PanoTexture()
    {
        if (_panoTexture != null || _panoTried) return _panoTexture;
        _panoTried = true;
        string path = System.IO.Path.Combine(AshfallKitLibrary.Root, AshfallKit.SkyFile.Replace('/', System.IO.Path.DirectorySeparatorChar));
        try
        {
            if (!System.IO.File.Exists(path)) { GD.Print($"ashfall storm: sky image not found ({AshfallKit.SkyFile}); using the procedural sky"); return null; }
            var img = Image.LoadFromFile(path);
            if (img == null || img.IsEmpty()) { GD.Print("ashfall storm: sky image failed to load; using the procedural sky"); return null; }
            img.GenerateMipmaps();
            _panoTexture = ImageTexture.CreateFromImage(img);
        }
        catch (Exception e) { GD.Print($"ashfall storm: sky image failed to load ({e.Message}); using the procedural sky"); }
        return _panoTexture;
    }

    /// <summary>The panorama sky: the generated image with its warm break turned to the sun's azimuth, the far-ruin skyline drawn over the horizon. Null when the image is unavailable.</summary>
    static ShaderMaterial? PanoSkyMaterial(float sunAzimuth, bool compat)
    {
        if (!PanoSkyWanted) return null;
        var tex = PanoTexture();
        if (tex == null) return null;
        // The Compatibility renderer's sky pass crashes on screen-space derivatives (hidden-desktop OpenGL, 2026-11-06): there a plain texture() call is used (the mip seam is one pixel wide and the sky is lifted anyway).
        string code = compat ? PanoSkyShader.Replace("textureGrad(pano, vec2(u, v), du, vec2(dFdx(v), dFdy(v)))", "texture(pano, vec2(u, v))").Replace("vec2 gu = vec2(dFdx(u), dFdy(u)); vec2 gu2 = vec2(dFdx(u2), dFdy(u2));", "vec2 gu = vec2(0.0); vec2 gu2 = vec2(0.0);") : PanoSkyShader;
        var m = new ShaderMaterial { Shader = new Shader { Code = code } };
        m.SetShaderParameter("pano", tex);
        var dir = G(new Vector3(MathF.Sin(sunAzimuth), MathF.Cos(sunAzimuth), 0f));
        m.SetShaderParameter("yaw", MathF.Atan2(dir.X, dir.Z));
        // Compatibility: the image is already a lit photograph, so no gamma lift (the procedural sky's 1.8 washed it out) and a gentler break boost (no glow there).
        if (compat) { m.SetShaderParameter("gain", 0.9f); m.SetShaderParameter("lift", 1.05f); m.SetShaderParameter("hdr_boost", 0.5f); }
        return m;
    }

    /// <summary>The light pass's sky: charcoal storm clouds over a grey haze, a warm break low on the sun's side (behind the keep) with lit cloud edges and rays, far ruins fading into the haze on the horizon.</summary>
    static ShaderMaterial BreakSkyMaterial(float sunAzimuth, bool compat)
    {
        var m = new ShaderMaterial { Shader = new Shader { Code = BreakSkyShader } };
        if (compat) { m.SetShaderParameter("gain", LightPass.CompatSkyGain); m.SetShaderParameter("lift", 1.8f); }
        float bel = Mathf.DegToRad(LightPass.BreakElevation);
        m.SetShaderParameter("break_dir", G(new Vector3(MathF.Sin(sunAzimuth) * MathF.Cos(bel), MathF.Cos(sunAzimuth) * MathF.Cos(bel), MathF.Sin(bel))).Normalized());
        return m;
    }

    /// <summary>The break sky (ours). Every value is a placeholder demonstration; the far ruins are a procedural skyline (no model), so they cost nothing in any renderer.</summary>
    public const string BreakSkyShader = """
        shader_type sky;
        uniform vec3 zenith = vec3(0.026, 0.028, 0.032);
        uniform vec3 cloud_dark = vec3(0.030, 0.032, 0.036);
        uniform vec3 cloud_grey = vec3(0.115, 0.118, 0.126);
        uniform vec3 cloud_lit = vec3(1.2, 0.74, 0.36);
        uniform vec3 haze = vec3(0.30, 0.30, 0.31);
        uniform vec3 glow = vec3(1.0, 0.66, 0.32);
        uniform vec3 break_dir = vec3(0.0, 0.2, -1.0);
        uniform float cover = 0.44;
        uniform float speed = 0.004;
        uniform float strength = 1.0;
        uniform float gain = 1.0;   // the Compatibility renderer draws the sky darker: it is lifted (gain and a gamma curve)
        uniform float lift = 1.0;

        float h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
        float vn(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
        }
        float fbm(vec2 p) {
            float v = 0.0; float a = 0.5;
            for (int i = 0; i < 5; i++) { v += a * vn(p); p = p * 2.03 + vec2(1.7, 9.2); a *= 0.5; }
            return v;
        }
        // a skyline of ruins (elevation in radians) along the azimuth phi: blocks of uneven height, gaps, now and then a spire
        float skyline(float phi, float cells, float base, float tall, float seed) {
            float k = phi * cells;
            float c = floor(k);
            float on = step(0.28, h21(vec2(c, seed)));
            float hgt = base + tall * h21(vec2(c, seed + 1.0)) * on;
            float f = fract(k);
            hgt += tall * 0.35 * step(0.5, fract(f * 4.0)) * on * step(0.5, h21(vec2(c, seed + 2.0)));   // battlements
            hgt += tall * 0.9 * step(0.88, h21(vec2(c, seed + 3.0))) * step(abs(f - 0.5), 0.12);          // a spire
            return hgt;
        }
        void sky() {
            vec3 d = normalize(EYEDIR);
            vec3 b = normalize(break_dir);
            float h = d.y;
            vec2 dh = normalize(d.xz + vec2(1e-5)); vec2 bh = normalize(b.xz + vec2(1e-5));
            float ha = acos(clamp(dot(dh, bh), -1.0, 1.0));   // across from the break (radians)
            float el = asin(clamp(h, -1.0, 1.0));
            float va = el - asin(clamp(b.y, -1.0, 1.0));       // above or below it
            float wide = exp(-ha * ha / 0.2 - va * va / 0.04);
            float open = exp(-ha * ha / 0.06 - va * va / 0.016);
            float core = exp(-ha * ha / 0.008 - va * va / 0.0035);
            // behind the clouds: grey haze at the horizon, a dark zenith, the warm break
            vec3 col = mix(haze, zenith, smoothstep(0.02, 0.5, h));
            col += glow * (wide * 0.16 + open * 0.9 + core * 2.2) * strength;
            col += glow * 0.14 * exp(-max(h, 0.0) * 10.0) * pow(max(dot(dh, bh), 0.0), 2.0) * strength;
            // the cloud deck: billowing (domain-warped), drifting, heavy overhead, open around the break
            float t = TIME * speed;
            vec2 uv = d.xz / (max(h, 0.0) + 0.1) * 0.8;
            vec2 w = vec2(fbm(uv * 0.6 + vec2(t, 0.0)), fbm(uv * 0.6 + vec2(5.2, 1.3) - vec2(0.0, t)));
            float n = fbm(uv * 1.1 + w * 1.5 + vec2(t, t * 0.3));
            float fine = fbm(uv * 4.3 - vec2(t * 2.0, 0.0));
            float body = n * 0.8 + fine * 0.3;
            float dens = smoothstep(cover - 0.16, cover + 0.18, body);
            dens = max(dens, smoothstep(0.18, 0.7, h) * 0.96);
            dens *= 1.0 - 0.92 * open;
            dens *= 0.7 + 0.3 * smoothstep(0.0, 0.1, h);   // low cloud thins into the haze
            float thick = smoothstep(0.4, 1.0, body + 0.1);
            vec3 ccol = mix(cloud_grey, cloud_dark, thick) * (0.75 + 0.5 * fine);
            ccol += cloud_lit * (1.0 - thick) * (wide * 0.5 + open) * 0.6 * strength + glow * open * 0.3 * strength;   // edges lit gold near the break
            col = mix(col, ccol, clamp(dens, 0.0, 1.0));
            // rays fanning out of the break
            vec3 a1 = normalize(cross(b, vec3(0.0, 1.0, 0.0)));
            vec3 a2 = cross(a1, b);
            float ang = atan(dot(d, a2), dot(d, a1));
            float rays = vn(vec2(ang * 11.0, 0.0)) * vn(vec2(ang * 27.0 + 3.0, 1.0));
            col += glow * rays * (open * 0.5 + wide * 0.12) * (1.0 - dens * 0.8) * 0.4 * smoothstep(-0.05, 0.05, va) * strength;
            // far ruins on the horizon, two layers fading into the haze (lit from behind where the break is)
            float phi = atan(d.x, d.z);
            float farA = skyline(phi, 70.0, 0.012, 0.05, 3.0);
            float farB = skyline(phi + 0.37, 38.0, 0.006, 0.035, 11.0);
            vec3 colA = mix(col * 0.8, haze * 0.7, 0.35);   // a silhouette a little darker than the sky behind it
            vec3 colB = mix(col * 0.66, haze * 0.58, 0.35);
            col = mix(col, colA, smoothstep(farA + 0.0015, farA - 0.0015, el));
            col = mix(col, colB, smoothstep(farB + 0.0015, farB - 0.0015, el));
            // below the horizon: the haze over far ground
            col = mix(col, haze * 0.6 + glow * wide * 0.12 * strength, smoothstep(0.0, -0.06, h));
            COLOR = pow(max(col * gain, vec3(0.0)), vec3(1.0 / lift));
        }
        """;

    /// <summary>The panorama sky (ours): the generated image (u = 360 degrees of azimuth, horizon at 90 percent of its height, zenith on top); the bright break is boosted over 1.0 to glow and bloom.</summary>
    public const string PanoSkyShader = """
        shader_type sky;
        uniform sampler2D pano : source_color, filter_linear_mipmap, repeat_enable;
        uniform float horizon_v = 0.90;
        uniform float yaw = 0.0;
        uniform float gain = 1.0;   // the Compatibility renderer draws the sky darker: it is lifted (gain and a gamma curve)
        uniform float lift = 1.0;
        uniform float hdr_boost = 2.2;
        uniform vec3 haze = vec3(0.30, 0.30, 0.31);
        uniform float break_strength = 1.0;

        float h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
        // a skyline of ruins (elevation in radians) along the azimuth phi: blocks of uneven height, gaps, now and then a spire
        float skyline(float phi, float cells, float base, float tall, float seed) {
            float k = phi * cells;
            float c = floor(k);
            float on = step(0.28, h21(vec2(c, seed)));
            float hgt = base + tall * h21(vec2(c, seed + 1.0)) * on;
            float f = fract(k);
            hgt += tall * 0.35 * step(0.5, fract(f * 4.0)) * on * step(0.5, h21(vec2(c, seed + 2.0)));   // battlements
            hgt += tall * 0.9 * step(0.88, h21(vec2(c, seed + 3.0))) * step(abs(f - 0.5), 0.12);          // a spire
            return hgt;
        }
        void sky() {
            vec3 d = normalize(EYEDIR);
            float phi = atan(d.x, d.z);
            float u = fract(0.5 + (phi - yaw) / (2.0 * PI));
            float el = asin(clamp(d.y, -1.0, 1.0));
            float v = el >= 0.0 ? horizon_v * (1.0 - el / (PI / 2.0)) : horizon_v + 0.02;
            // the atan wrap gives a one-pixel mip seam: derivatives from whichever of u and the half-turned u is continuous here
            float u2 = fract(u + 0.5);
            vec2 gu = vec2(dFdx(u), dFdy(u)); vec2 gu2 = vec2(dFdx(u2), dFdy(u2));
            vec2 du = dot(gu, gu) < dot(gu2, gu2) ? gu : gu2;
            vec3 col = textureGrad(pano, vec2(u, v), du, vec2(dFdx(v), dFdy(v))).rgb;
            float lum = dot(col, vec3(0.2126, 0.7152, 0.0722));
            col *= 1.0 + hdr_boost * break_strength * smoothstep(0.45, 0.95, lum);
            // far ruins on the horizon, two layers fading into the haze
            float farA = skyline(phi, 70.0, 0.012, 0.05, 3.0);
            float farB = skyline(phi + 0.37, 38.0, 0.006, 0.035, 11.0);
            vec3 colA = mix(col * 0.8, haze * 0.7, 0.35);
            vec3 colB = mix(col * 0.66, haze * 0.58, 0.35);
            col = mix(col, colA, smoothstep(farA + 0.0015, farA - 0.0015, el));
            col = mix(col, colB, smoothstep(farB + 0.0015, farB - 0.0015, el));
            // below the horizon: the haze over far ground
            col = mix(col, haze * 0.6, smoothstep(0.0, -0.06, d.y));
            COLOR = pow(max(col * gain, vec3(0.0)), vec3(1.0 / lift));
        }
        """;
}
