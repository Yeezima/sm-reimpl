using System;
using Godot;

namespace SmClient.Extras.Weather;

/// <summary>
/// Weather fidelity (owner 2026-11-25, decisions/2026-11-25-weather-fidelity.md; client only, presentation only, **ours**, every number an estimate for the Fable gate and a playtest; spec/behavior.md "Weather fidelity"):
/// <list type="bullet">
/// <item>The install's own particle sprites (<see cref="WeatherSprites"/>) replace the procedural textures: the ash grit and flakes draw the original falling-ash sheet, the dust sheets and fog cards its soft volume puffs, the rain mist its
/// wisps, the splashes its spray spikes. Swapped in when the read arrives (a rig built earlier keeps its procedural look until then); <c>--weather-procedural</c> keeps the old look.</item>
/// <item>Every layer's colour follows the map's light under the weather's grade (<see cref="GradeLight"/>): a storm's dim sun dims the rain, the ash and the fog with it, so nothing glows against a darkened street.</item>
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

    /// <summary>The light factor now (the weather model's grade with the full flash, or 1).</summary>
    float LightNow() => WeatherClientLink.Model is { Has: true } m ? GradeLight(m.Grade) : 1f;

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
            Sprite(_dustMaterial, _dust, WeatherSprites.Role.Volume, FogPuff(), angle: 0f);
            UsesSprites = ash is not null || WeatherSprites.Of(WeatherSprites.Role.Volume) is not null;
        }
        float light = old ? 1f : LightNow(), rainLight = 1f + (light - 1f) * RainLightShare;
        Scale(_rainMaterial, rainLight); Scale(_rainFarMaterial, rainLight);
        Scale(_ashMainMaterial, rainLight); Scale(_ashWarnMaterial, light);
        float volumeGain = WeatherSprites.Of(WeatherSprites.Role.Volume) is null ? 1f : VolumeAlphaGain;
        Scale(_splashMaterial, rainLight); Scale(_mistMaterial, light); Scale(_fogMaterial, light, volumeGain); Scale(_dustMaterial, light, volumeGain);
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
