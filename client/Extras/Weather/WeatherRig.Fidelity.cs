using System;
using Godot;

namespace SmClient.Extras.Weather;

/// <summary>
/// Weather fidelity (owner 2026-11-25, decisions/2026-11-25-weather-fidelity.md; client only, presentation only, **ours**, every number an estimate for the Fable gate and a playtest; spec/behavior.md "Weather fidelity"):
/// <list type="bullet">
/// <item>The install's own particle sprites (<see cref="WeatherSprites"/>) replace the procedural textures: the ash grit and flakes draw the original falling-ash sheet, the dust sheets and fog cards its soft volume puffs, the rain mist its
/// wisps, the splashes its spray spikes. Swapped in when the read arrives (a rig built earlier keeps its procedural look until then); <c>--weather-procedural</c> keeps the old look.</item>
/// <item>Every layer's colour follows the map's light at the camera (<see cref="MapLight"/>: the map's sun and ambient as the region blend and the weather's grade leave them, the sun only under open sky): a storm's dim sun
/// dims the rain, the ash and the fog with it, and a dark interior darkens them, so nothing glows against a darkened street. Without map lighting to read, the grade's light alone (<see cref="GradeLight"/>).</item>
/// <item>High preset only, outside the Compatibility renderer and safe mode (<see cref="WeatherField.HighTier"/>): the precipitation fields fade softly where they meet geometry (a depth-buffer soft fade). Medium, Low and the
/// Compatibility renderer keep the rain-motion shader cost (the same instance counts; one texture read in place of the procedural profile).</item>
/// </list>
/// The embers (Ember Wind) are not touched (owner 2026-11-25: "Keep that one as is"; pinned by WeatherEmberPinTests).
/// </summary>
public sealed partial class WeatherRig
{
    /// <summary>The soft fade's depth (m) of the near rain, the far rain, the grit and the flakes on the High tier.</summary>
    public const float RainSoftM = 0.35f, RainFarSoftM = 1.0f, GritSoftM = 0.5f, FlakeSoftM = 0.3f;
    /// <summary>The share of the grade's light the rain follows (the streaks stay a little brighter than the street they cross, as rain does against a dark wall); the ash, dust, fog and mist follow it fully.</summary>
    public const float RainLightShare = 0.5f;
    /// <summary>
    /// Tune 2 (media check of the tune: "On Waste Management the darkened ash grit is now hard to see at all in the shade"): the grit follows <see cref="GritLightShare"/> of the light's change and never drops under
    /// <see cref="GritLightFloor"/>, so in shade it is darker than in sun but still reads as thin streaks (a 3.5 cm streak lit fully by a shade's light vanished against the dark wall). At Waste Management's shaded spawn
    /// (light 0.50) the grit takes 0.70, at Manufactorum's spawn (0.67) 0.80; the review's grit took 0.89 on both.
    /// </summary>
    public const float GritLightShare = 0.6f, GritLightFloor = 0.65f;

    /// <summary>The grit's light factor for a layer light factor <paramref name="light"/>. Pure.</summary>
    public static float GritLight(float light) => Math.Max(GritLightFloor, 1f + (light - 1f) * GritLightShare);
    /// <summary>The volume puff sprite covers less of its card than the procedural puff did (a cloud with gaps, not a filled disc): the dust sheets' and fog cards' alpha times this keeps the squall's and the fog's density.</summary>
    public const float VolumeAlphaGain = 1.8f;
    /// <summary>The bounds of the light factor.</summary>
    public const float LightMin = 0.55f, LightMax = 1.15f;

    int _spriteVersion = -1;
    bool? _tier;

    /// <summary>Whether this rig draws the install's sprites now (the selftest).</summary>
    public bool UsesSprites { get; private set; }
    /// <summary>The High tier is in use now (the selftest, the perf log).</summary>
    public bool HighTier => _tier == true;

    /// <summary>
    /// The light the weather's grade leaves on the street (1 for none): the sun's share under the grade (the weather dims the sun, the ambient stays) times its exposure, so each layer's colour follows the map's grade. Pure on the grade.
    /// </summary>
    public static float GradeLight(SmClient.Customizer.Model.LightingGrade g) =>
        Math.Clamp((0.5f + 0.5f * g.SunEnergy) * g.Exposure * (0.9f + 0.1f * g.Ambient), LightMin, LightMax);

    /// <summary>The light factor from the grade alone (the fallback with no map light to read: the selftest's scene, a view without a sun or environment).</summary>
    float GradeLightNow() => WeatherClientLink.Model is { Has: true } m ? GradeLight(m.Grade) : 1f;

    /// <summary>Tune: the map's sun and environment, set by the view each frame (null: the grade's light alone).</summary>
    public DirectionalLight3D? MapSun { get; set; }
    public Godot.Environment? MapEnvironment { get; set; }

    /// <summary>
    /// Tune (review: "ash grit reads as near-white sleet" on Waste Management): the light a particle at the camera receives, read from the map's own lighting as the frame draws it (the sun and environment after the region blend
    /// and the weather's grade): the ambient (the sky's hemisphere colours, or the ambient colour) times its energy times <see cref="MapAmbientGain"/> (the sky, the haze's in-scatter and the bounce that light drifting ash in
    /// shade), plus <see cref="MapSunGain"/> of the sun times the share of the air round the camera the sun reaches (<see cref="SunSeen"/>: rays toward the sun through the collision world; in a street's shade or under a roof
    /// none), times the exposure, bounded. Calibrated on the Ash Squall at full: about 0.66 at Manufactorum's spawn 1 (a fifth of its air sunlit), 0.86 in its full sun, 0.51 in Waste Management's shade (the old grade light
    /// was 0.78 everywhere, and the grit took only half of it). Shade and dark interiors give dark grit; the storm's dim sun dims it too.
    /// </summary>
    public const float MapAmbientGain = 2.1f, MapSunGain = 0.22f, MapLightMin = 0.45f, MapLightMax = 1.15f;
    /// <summary>The sun rays' reach (m): an occluder farther than this does not shade the camera's air.</summary>
    public const float SunRayReachM = 80f;

    /// <summary>The share of the air round the camera the map's sun reaches (0 to 1; set by the view from its sun rays, 1 when nothing sets it).</summary>
    public float SunSeen { get; set; } = 1f;
    /// <summary>How fast the light factor follows a change (per second, exponential): a region border or a roof edge eases in instead of stepping.</summary>
    public const float MapLightRate = 2.5f;

    /// <summary>The light factor of an ambient level, a sun level, an exposure and the share of the camera's air the sun reaches (0 to 1). Pure.</summary>
    public static float MapLight(float ambient, float sun, float exposure, float sunSeen)
    {
        float open = Math.Clamp(float.IsFinite(sunSeen) ? sunSeen : 1f, 0f, 1f);
        float v = (MapAmbientGain * Math.Max(ambient, 0f) + MapSunGain * Math.Max(sun, 0f) * open) * Math.Max(exposure, 0f);
        return Math.Clamp(float.IsFinite(v) ? v : 1f, MapLightMin, MapLightMax);
    }

    static readonly StringName PAmbUpper = "amb_upper", PAmbLower = "amb_lower";
    static float Lum(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
    static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>The map's ambient level, sun level and exposure now; false with no environment to read.</summary>
    bool MapLevels(out float ambient, out float sun, out float exposure)
    {
        ambient = sun = 0f; exposure = 1f;
        var env = MapEnvironment;
        if (env is null || !GodotObject.IsInstanceValid(env)) return false;
        float amb = 0.4f;
        if (env.AmbientLightSource == Godot.Environment.AmbientSource.Color) amb = Lum(env.AmbientLightColor);
        else if (env.Sky?.SkyMaterial is ShaderMaterial sm && sm.GetShaderParameter(PAmbUpper).VariantType == Variant.Type.Vector3)
            amb = 0.5f * (Lum(sm.GetShaderParameter(PAmbUpper).AsVector3()) + Lum(sm.GetShaderParameter(PAmbLower).AsVector3()));
        else if (env.Sky?.SkyMaterial is ProceduralSkyMaterial ps) amb = 0.5f * (Lum(ps.SkyTopColor) + Lum(ps.GroundBottomColor));
        ambient = amb * env.AmbientLightEnergy;
        if (MapSun is { } s && GodotObject.IsInstanceValid(s) && s.Visible) sun = Lum(s.LightColor) * s.LightEnergy;
        exposure = env.TonemapExposure;
        return true;
    }

    float _mapLight = float.NaN;
    /// <summary>The map's levels last read (the log): ambient, sun, exposure.</summary>
    public (float Ambient, float Sun, float Exposure) MapLevelsNow { get; private set; }

    /// <summary>The light factor now: the map's light at the camera (eased), or the grade's alone with no map lighting to read.</summary>
    float LightNow()
    {
        if (!MapLevels(out float amb, out float sun, out float exp)) { _mapLight = float.NaN; return GradeLightNow(); }
        MapLevelsNow = (amb, sun, exp);
        float target = MapLight(amb, sun, exp, Math.Min(SunSeen, 1f - Shelter));
        float dt = (float)Math.Clamp(GetProcessDeltaTime(), 0.0, 0.25);
        _mapLight = float.IsNaN(_mapLight) ? target : _mapLight + (target - _mapLight) * (1f - MathF.Exp(-MapLightRate * dt));
        return _mapLight;
    }

    /// <summary>A colour scaled by the light (alpha kept).</summary>
    static Color Lit(Color c, float light) => new(c.R * light, c.G * light, c.B * light, c.A);

    /// <summary>Once a frame, after the layers took their colours: the sprites (when they arrive or a layer is new), the shader tier, the light.</summary>
    void ApplyFidelity()
    {
        bool old = WeatherSprites.Procedural;   // --weather-procedural: the rain-motion look whole (no sprite, no tier, no light)
        bool high = !old && WeatherField.HighTier;
        int version = WeatherSprites.Procedural ? -2 : WeatherSprites.Version;
        bool fresh = version != _spriteVersion || high != _tier || _layersSeen != LayerMask();
        if (fresh)
        {
            _spriteVersion = version; _tier = high; _layersSeen = LayerMask();
            var ash = WeatherSprites.Of(WeatherSprites.Role.Ash);
            _rain?.SetTier(high, RainSoftM); _rainFar?.SetTier(high, RainFarSoftM); _ashMain?.SetTier(high, GritSoftM); _ashWarn?.SetTier(high, FlakeSoftM);
            _rain?.SetVary(!old); _rainFar?.SetVary(!old); _ashMain?.SetVary(!old);
            _rain?.UseSprite(null); _rainFar?.UseSprite(null);   // the rain keeps its procedural streak (WeatherSprites: no rain sprite in the install)
            _ashMain?.UseSprite(ash); _ashWarn?.UseSprite(ash);
            Sprite(_splashMaterial, _splash, WeatherSprites.Role.Splash, SoftDot(), angle: 25f);
            SplashShape(_splash, WeatherSprites.Of(WeatherSprites.Role.Splash) is not null);
            Sprite(_mistMaterial, _mist, WeatherSprites.Role.Wisp, SoftDot(), angle: 180f);
            Sprite(_fogMaterial, _fog, WeatherSprites.Role.Volume, FogPuff(), angle: 0f);
            FogShape(_fog, _fogMaterial, WeatherSprites.Of(WeatherSprites.Role.Volume) is not null);
            Sprite(_dustMaterial, _dust, WeatherSprites.Role.Volume, FogPuff(), angle: 0f);
            UsesSprites = ash is not null || WeatherSprites.Of(WeatherSprites.Role.Volume) is not null;
        }
        float light = old ? 1f : LightNow(), rainLight = 1f + (light - 1f) * RainLightShare;
        Scale(_rainMaterial, rainLight); Scale(_rainFarMaterial, rainLight);
        Scale(_ashMainMaterial, GritLight(light)); Scale(_ashWarnMaterial, light);   // tune (review: grit read as near-white sleet), tune 2 (then too dark to see in shade): most of the light's change, with a floor
        float volumeGain = WeatherSprites.Of(WeatherSprites.Role.Volume) is null ? 1f : VolumeAlphaGain;
        Scale(_splashMaterial, rainLight); Scale(_mistMaterial, light); Scale(_fogMaterial, light, WeatherSprites.Of(WeatherSprites.Role.Volume) is null ? 1f : FogSpriteAlphaGain); Scale(_dustMaterial, light, volumeGain);
        LightFactor = light;
    }

    /// <summary>The light factor applied this frame (the selftest, the stills' log).</summary>
    public float LightFactor { get; private set; } = 1f;

    int _layersSeen;
    int LayerMask() => (_rain is null ? 0 : 1) | (_ashMain is null ? 0 : 2) | (_ashWarn is null ? 0 : 4) | (_fog is null ? 0 : 8) | (_dust is null ? 0 : 16) | (_splash is null ? 0 : 32);

    /// <summary>The colour last written per material: a layer the rig did not recolour this frame (switched off and fading with its last look) keeps it, so the light is never applied twice.</summary>
    readonly System.Collections.Generic.Dictionary<Material, Color> _lit = new();

    /// <summary>The rig set each layer's colour from its base tint this frame (or left it, while it fades); scaled here by the light, alpha kept.</summary>
    void Scale(Material? m, float light, float alphaGain = 1f)
    {
        if (m is null) return;
        var c = m is ShaderMaterial sm ? sm.GetShaderParameter(PTint).AsColor() : ((StandardMaterial3D)m).AlbedoColor;
        if (_lit.TryGetValue(m, out var written) && written == c) return;   // not recoloured this frame: already lit
        var lit = Lit(c, light);
        lit.A = Math.Clamp(lit.A * alphaGain, 0f, 1f);
        if (m is ShaderMaterial s) s.SetShaderParameter(PTint, lit); else ((StandardMaterial3D)m).AlbedoColor = lit;
        _lit[m] = lit;
    }

    /// <summary>
    /// Tune (review: Low Fog's cards showed fine horizontal striations across the crates): with the install's volume puff a fog card is <see cref="FogSpriteWidthM"/> x <see cref="FogSpriteHeightM"/> (about 3 to 1, not the
    /// procedural puff's 16 x 2.4 m, which stretched the puff's detail into bands), it fades over a deeper soft line where it meets the ground or a wall, and its alpha is <see cref="FogSpriteAlphaGain"/> times the card's
    /// (in place of the dust sheets' <see cref="VolumeAlphaGain"/>), so many soft overlapping clouds read as one low layer.
    /// </summary>
    public const float FogSpriteWidthM = 9f, FogSpriteHeightM = 3.2f, FogSpriteSoftM = 4.5f, FogSpriteAlphaGain = 1.8f;

    static void FogShape(CpuParticles3D? p, StandardMaterial3D? m, bool sprite)
    {
        if (p is null || m is null || p.Mesh is not QuadMesh q) return;
        q.Size = sprite ? new Vector2(FogSpriteWidthM, FogSpriteHeightM) : new Vector2(FogCardWidthM, FogCardHeightM);
        m.ProximityFadeDistance = sprite ? FogSpriteSoftM : 3f;
    }

    /// <summary>The splash's size (m, width and height of a spike) with the install's sprite; its base sits on the ground and it grows over its life as the original's splash beam does (0 to its length).</summary>
    public const float SplashSpikeWidthM = 0.16f, SplashSpikeHeightM = 0.22f;

    /// <summary>
    /// The splashes with the install's spray spike: a spike rising from where the drop hit (the quad's base on the ground), growing from 0.3 to full over its 0.3 s life and fading, barely moving; without the sprite, the rain-motion
    /// droplets (a 7 cm soft dot thrown up 0.5 to 1.2 m/s under 6 m/s2).
    /// </summary>
    static void SplashShape(CpuParticles3D? p, bool sprite)
    {
        if (p is null || p.Mesh is not QuadMesh q) return;
        if (sprite)
        {
            q.Size = new Vector2(SplashSpikeWidthM, SplashSpikeHeightM);
            q.CenterOffset = new Vector3(0f, SplashSpikeHeightM * 0.5f, 0f);
            p.InitialVelocityMin = 0f; p.InitialVelocityMax = 0.15f; p.Gravity = Vector3.Zero;
            var grow = new Curve();
            grow.AddPoint(new Vector2(0f, 0.3f)); grow.AddPoint(new Vector2(0.4f, 1f)); grow.AddPoint(new Vector2(1f, 1f));
            p.ScaleAmountCurve = grow;
        }
        else
        {
            q.Size = new Vector2(0.07f, 0.07f); q.CenterOffset = Vector3.Zero;
            p.InitialVelocityMin = 0.5f; p.InitialVelocityMax = 1.2f; p.Gravity = new Vector3(0f, -6f, 0f);
            p.ScaleAmountCurve = null;
        }
    }

    /// <summary>A CPU layer's texture: the role's sprite (its sheet as particle frames, a random frame per particle, turned up to <paramref name="angle"/> degrees either way) or the procedural one.</summary>
    static void Sprite(StandardMaterial3D? m, CpuParticles3D? p, WeatherSprites.Role role, Texture2D fallback, float angle)
    {
        if (m is null || p is null) return;
        var s = WeatherSprites.Of(role);
        m.AlbedoTexture = s?.Texture ?? fallback;
        m.ParticlesAnimHFrames = s?.TilesX ?? 1;
        m.ParticlesAnimVFrames = s?.TilesY ?? 1;
        m.ParticlesAnimLoop = false;
        p.AnimOffsetMin = 0f;
        p.AnimOffsetMax = s is null ? 0f : 1f;
        p.AnimSpeedMin = p.AnimSpeedMax = 0f;
        p.AngleMin = s is null ? 0f : -angle;
        p.AngleMax = s is null ? 0f : angle;
    }
}
