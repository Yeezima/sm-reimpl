using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Draws a map's own lighting (<see cref="MapLighting"/>, read from <c>l_lights.Layer</c>): the sun, ambient, fog and bloom
/// of its environment regions, and its placed omni and spot lights. Values come from the file; how the engine turns
/// them into pixels is not known, so every conversion below is a **provisional approximation** tuned by eye:
/// <list type="bullet">
/// <item>Sun: direction, colour, power from the file; Godot energy = Power x <see cref="SunEnergyPerPower"/>.</item>
/// <item>Ambient: the two hemisphere colours become a gradient sky that Godot uses for ambient and reflections; saturation as stored.</item>
/// <item>Fog (pass 2 reading): <c>OutscatterColour</c> is a gradient over <c>OutscatterRange</c> metres whose alpha runs from 0 to the
/// far stop's 69/255; read as depth fog that reaches that opacity at the range and stays there (pass 1 read it as exponential fog
/// that keeps growing, which made the far hall hazy). Only the far colour is used.</item>
/// <item>Bloom regions: blended by camera position (<see cref="EnvironmentBlender"/>).</item>
/// <item>Placed lights: range = AttenEnd; Godot's falloff curve differs from the original's (full strength to AttenStart, zero at AttenEnd), so
/// the attenuation exponent is lowered a little; energy = Power x <see cref="OmniEnergyPerPower"/>; far lights fade out
/// (<see cref="FadeBegin"/>); at most <see cref="MaxShadowedLights"/> cast shadows (Manufactorum: none do).</item>
/// </list>
/// </summary>
public static class MapLightingView
{
    /// <summary>
    /// M42 lighting preset (<c>--lighting off|low|medium|high</c>, default medium: it fits unknown GPUs; high is opt-in; the owner's decision to go
    /// beyond the original renderer,
    /// docs/changes-from-original.md). Off = the M40-T7 renderer features alone: no shadows, SSAO, SSIL, volumetric smog or SSR; the look's constants (fx/impact-pass: the remaster by
    /// default: sun angle and colour, tone mapping, exposure, colour grade, sky, materials) still apply, so <c>--lighting off --look current</c> is the M40-T7 look itself (the effect-card
    /// fades in FxBatch apply in every preset; <c>--fx-hard</c> turns them off); every other preset adds the colour grade, a bounce fill and the sun halo; low adds soft sun shadows and contact shading (SSAO); medium adds screen-space indirect light and
    /// volumetric smog with light shafts; high adds screen-space reflections, sun penumbrae (PCSS), a larger shadow map and finer fog. SDFGI is not
    /// used (it hides wall sections on this map; test knob <c>gi.on</c>). <c>--look classic</c> turns all of it off. Every value it sets is **ours** unless its comment names the data.
    /// </summary>
    public enum LightingPreset { Off, Low, Medium, High }

    /// <summary>fx/impact-pass: whether screen-space reflections run: at medium and high only, when the preset's default or the look's <c>ssr.on</c> (<paramref name="wanted"/>) asks; never at low or off.</summary>
    public static bool SsrOn(LightingPreset preset, float wanted) => preset >= LightingPreset.Medium && wanted > 0.5f;

    public static LightingPreset Preset { get; } = ParsePreset();

    static LightingPreset ParsePreset()
    {
        if (RenderQuality.ClassicLook) return LightingPreset.Off;
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--lighting");
        string? text = i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
        switch (text)
        {
            case "off": return LightingPreset.Off;
            case "low": return LightingPreset.Low;
            case "medium": return LightingPreset.Medium;
            case "high": return LightingPreset.High;
            case null or "medium": return LightingPreset.Medium;
            default: GD.PrintErr($"--lighting '{text}' is not known (off, low, medium, high); using medium"); return LightingPreset.Medium;
        }
    }

    public const float SunEnergyPerPower = 0.55f;
    public const float AmbientEnergy = 0.8f;
    public const float OmniEnergyPerPower = 1.2f;
    public const float SpotEnergyPerPower = 1.2f;
    public const float FadeBegin = 70f, FadeLength = 25f;
    public const int MaxShadowedLights = 6;

    /// <summary>The constants a named look sets (<c>--look</c>, <see cref="LookPresets"/>), under the single <c>--look-set</c> overrides (those win).</summary>
    static readonly Dictionary<string, float> Overrides = MergeOverrides();

    static Dictionary<string, float> MergeOverrides()
    {
        var merged = new Dictionary<string, float>(LookPresets.For(RenderQuality.LookPreset), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in RenderQuality.ParseOverrides(RenderQuality.LookOverrides)) merged[key] = value;
        return merged;
    }
    static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase);
    static bool _overridesReported;

    /// <summary>
    /// A lighting constant: <paramref name="classic"/> (the look before M40-T7, <c>--look classic</c>) or <paramref name="t7"/>, unless
    /// <c>--look-set key=value</c> names it (test aid). Every value passed here is a tuning choice (**ours**) unless its comment says it comes from the data.
    /// </summary>
    public static float V(string key, float classic, float t7)
    {
        lock (KnownKeys) KnownKeys.Add(key);   // material builds may run on worker threads
        return Overrides.TryGetValue(key, out var v) ? v : RenderQuality.ClassicLook ? classic : t7;
    }

    /// <summary>An M42 constant (**ours**): <paramref name="value"/> unless <c>--look-set</c> names <paramref name="key"/>.</summary>
    static float O(string key, float value) => V(key, value, value);

    /// <summary>Logs once which <c>--look-set</c> keys were used and which no lighting constant asked for (typos).</summary>
    static void ReportOverrides()
    {
        if (_overridesReported || Overrides.Count == 0) return;
        _overridesReported = true;
        if (RenderQuality.LookPreset is { } look) GD.Print($"--look {look}: {LookPresets.Descriptions.GetValueOrDefault(look, "unknown")}");
        lock (KnownKeys) ReportOverridesLocked();
    }

    static void ReportOverridesLocked()
    {
        var used = Overrides.Keys.Where(KnownKeys.Contains).ToList();
        var unknown = Overrides.Keys.Where(k => !KnownKeys.Contains(k)).ToList();
        GD.Print($"--look / --look-set: applied {(used.Count > 0 ? string.Join(", ", used.Select(k => $"{k}={Overrides[k]:0.###}")) : "none")}"
                 + (unknown.Count > 0 ? $"; not a lighting constant (ignored): {string.Join(", ", unknown)}" : ""));
    }

    /// <summary>The sky shader (M40-T7): the radiance cubemap (ambient light and reflections) is the data's hemisphere gradient; the background is a haze gradient. **Ours.**</summary>
    const string SkyShaderCode = """
        shader_type sky;
        uniform vec3 amb_upper;
        uniform vec3 amb_lower;
        uniform vec3 sky_top;
        uniform vec3 sky_horizon;
        uniform vec3 sky_ground;
        uniform float sky_curve = 0.5;
        uniform vec3 sun_halo = vec3(0.0);
        uniform float sun_halo_power = 6.0;
        uniform vec3 sun_core = vec3(0.0);
        uniform vec3 bounce = vec3(0.0);
        void sky() {
            float h = EYEDIR.y;
            if (AT_CUBEMAP_PASS) {
                // M42: plus the sunlight bounced off the ground and walls (strongest from below, some from the sides): warm fill in the shade.
                COLOR = mix(amb_lower + bounce, amb_upper + bounce * 0.25, smoothstep(-0.6, 0.6, h));
            } else {
                vec3 c = h >= 0.0 ? mix(sky_horizon, sky_top, pow(clamp(h, 0.0, 1.0), sky_curve))
                                  : mix(sky_horizon, sky_ground, smoothstep(0.0, 0.3, -h));
                // M42: the sun's glow through the smog (a wide halo and a soft core; no disc): the light comes from somewhere.
                if (LIGHT0_ENABLED && h > -0.1) {
                    float d = max(dot(EYEDIR, LIGHT0_DIRECTION), 0.0);
                    c += sun_halo * pow(d, sun_halo_power) + sun_core * pow(d, 48.0);
                }
                COLOR = c;
            }
        }
        """;

    /// <summary>
    /// Sets the sun and environment (through <paramref name="blender"/>, which the caller updates with the camera position) and
    /// returns the lights node (game space, Z up) to add under the map; null when the map has no lighting data.
    /// </summary>
    public static Node3D? Apply(MapLighting? lighting, DirectionalLight3D sun, Godot.Environment environment, List<string> notes, out string summary,
                                out EnvironmentBlender? blender)
    {
        summary = "lighting: provisional approximation (no l_lights data)";
        blender = null;
        if (lighting is null) return null;
        // If anything below throws, the caller keeps the provisional look: every stored property of the environment and every sun property
        // this file sets go back to their values from before, and the renderer-wide settings (shadow atlas, soft-shadow filter, SSAO / SSIL
        // and fog quality) are only applied after success.
        var environmentBefore = (Godot.Environment)environment.Duplicate();
        var sunBefore = SunSettings.Of(sun);
        PendingGlobal.Clear();
        try
        {
            var placed = ApplyCore(lighting, sun, environment, notes, out summary, out blender);
            foreach (var apply in PendingGlobal) apply();
            return placed;
        }
        catch
        {
            foreach (var property in environmentBefore.GetPropertyList())
            {
                var usage = (PropertyUsageFlags)(int)property["usage"];
                if ((usage & PropertyUsageFlags.Storage) == 0) continue;
                var name = (StringName)(string)property["name"];
                environment.Set(name, environmentBefore.Get(name));
            }
            sunBefore.Restore(sun);
            if (blender?.FogVolume is { } orphan && orphan.GetParent() is null) orphan.Free();   // built but never added to the tree
            blender = null;
            throw;
        }
        finally { PendingGlobal.Clear(); }
    }

    /// <summary>Renderer-wide settings collected while applying a map's lighting; run only when everything else succeeded.</summary>
    static readonly List<Action> PendingGlobal = new();

    /// <summary>The sun properties this file sets, to put back when applying fails.</summary>
    readonly record struct SunSettings(Basis Basis, Color Colour, float Energy, float MaxDistance, DirectionalLight3D.ShadowMode Mode, float Split1,
                                       float Split2, float Split3, bool BlendSplits, float FadeStart, float Blur, float AngularDistance, float FogEnergy)
    {
        public static SunSettings Of(DirectionalLight3D s) => new(s.Basis, s.LightColor, s.LightEnergy, s.DirectionalShadowMaxDistance, s.DirectionalShadowMode,
            s.DirectionalShadowSplit1, s.DirectionalShadowSplit2, s.DirectionalShadowSplit3, s.DirectionalShadowBlendSplits, s.DirectionalShadowFadeStart,
            s.ShadowBlur, s.LightAngularDistance, s.LightVolumetricFogEnergy);

        public void Restore(DirectionalLight3D s)
        {
            s.Basis = Basis; s.LightColor = Colour; s.LightEnergy = Energy; s.DirectionalShadowMaxDistance = MaxDistance; s.DirectionalShadowMode = Mode;
            s.DirectionalShadowSplit1 = Split1; s.DirectionalShadowSplit2 = Split2; s.DirectionalShadowSplit3 = Split3; s.DirectionalShadowBlendSplits = BlendSplits;
            s.DirectionalShadowFadeStart = FadeStart; s.ShadowBlur = Blur; s.LightAngularDistance = AngularDistance; s.LightVolumetricFogEnergy = FogEnergy;
        }
    }

    static Node3D? ApplyCore(MapLighting lighting, DirectionalLight3D sun, Godot.Environment environment, List<string> notes, out string summary,
                             out EnvironmentBlender? blender)
    {
        summary = "lighting: provisional approximation (no l_lights data)";
        blender = null;
        var region = lighting.Global;
        var sunData = lighting.Sun ?? region?.Sun;
        if (sunData is not null)
        {
            // Game space (Z up) to Godot (Y up): x -> x, y -> -z, z -> y.
            var d = new Vector3(sunData.Direction.X, sunData.Direction.Z, -sunData.Direction.Y);
            if (d.LengthSquared() < 1e-6f) d = Vector3.Down;
            d = SunDirection(d.Normalized());
            sun.Basis = Basis.LookingAt(d.Normalized(), MathF.Abs(d.Normalized().Y) > 0.99f ? Vector3.Forward : Vector3.Up);   // a vertical sun needs another up vector
            sun.DirectionalShadowMaxDistance = MathF.Min(sunData.ShadowDistance, V("sun.shadow", 220f, 400f));   // T7: the data's FarSunDistance (400)
            if (Preset != LightingPreset.Off) ApplySunShadows(sun, sunData);
        }
        if (region is not null)
        {
            blender = new EnvironmentBlender(lighting, region, sunData, sun, environment);
            blender.Update(region.Centre);
        }

        var root = new Node3D { Name = "PlacedLights" };
        int omni = 0, spot = 0, shadowed = 0;
        // The placed lights' energy and range (look knobs lights.mul, lights.range: factors on the data's Power and AttenEnd) and Godot's attenuation exponent.
        float lightMul = O("lights.mul", 1f), lightRange = O("lights.range", 1f), lightAtten = O("lights.atten", 0.8f);
        try
        {
        foreach (var light in lighting.Lights)
        {
            float range = MathF.Max(light.AttenEnd, 0.1f);   // a zero range would turn the light off or divide by zero
            bool shadows = light.CastShadows && shadowed < MaxShadowedLights;
            if (shadows) shadowed++;
            var color = ToColor(light.Colour);
            var position = new Vector3(light.Position.X, light.Position.Y, light.Position.Z);
            Light3D node;
            if (light.Kind == MapLightKind.Omni)
            {
                node = new OmniLight3D { OmniRange = range * lightRange, OmniAttenuation = lightAtten, LightEnergy = light.Power * OmniEnergyPerPower * lightMul };
                omni++;
            }
            else
            {
                node = new SpotLight3D
                {
                    SpotRange = range * lightRange, SpotAngle = Math.Clamp(light.FovDegrees * 0.5f, 5f, 89f), SpotAttenuation = lightAtten,
                    LightEnergy = light.Power * SpotEnergyPerPower * lightMul,
                };
                spot++;
            }
            node.Name = $"{(light.Kind == MapLightKind.Omni ? "Omni" : "Spot")}_{light.Name}";
            node.LightColor = color;
            // M42: placed lamps glow in the volumetric fog (medium and high; ours).
            node.LightVolumetricFogEnergy = VolumetricOn ? O("vf.lights", 0.35f) : 0f;
            node.ShadowEnabled = shadows;
            node.DistanceFadeEnabled = true;
            node.DistanceFadeBegin = FadeBegin;
            node.DistanceFadeLength = FadeLength;
            node.DistanceFadeShadow = FadeBegin * 0.5f;
            var forward = new Vector3(light.Direction.X, light.Direction.Y, light.Direction.Z);
            var up = MathF.Abs(forward.Z) > 0.99f ? Vector3.Right : Vector3.Back;   // game-space Z is up
            root.AddChild(node);
            node.Transform = new Transform3D(Basis.LookingAt(forward, up), position);
        }
        }
        catch { root.Free(); throw; }   // not in the tree yet: free it (and the lights built so far) here
        if (blender?.FogVolume is { } fogVolume) root.AddChild(fogVolume);
        ReportOverrides();
        summary = $"lighting ({PresetNote}{(RenderQuality.LookPreset is { } look ? $", look {look}" : "")}): l_lights data (sun {sunData?.Power:0.#}, {omni} omni + {spot} spot lights, {shadowed} shadowed, {lighting.Environments.Count} environment regions), conversions provisional";
        if (region is null) notes.Add("l_lights has no environment region; the provisional ambient and fog stay");
        return root;
    }

    /// <summary>
    /// The sun's travel direction (Godot space) after the look's <c>sun.elev</c> (degrees above the horizon; the data's (-45, 45, 0) rotation gives 30)
    /// and <c>sun.yaw</c> (degrees turned about the vertical from the data's azimuth). Both absent: the data's direction unchanged.
    /// </summary>
    static Vector3 SunDirection(Vector3 data)
    {
        float dataElev = MathF.Asin(Math.Clamp(-data.Y, -1f, 1f)) * 180f / MathF.PI;
        float elev = O("sun.elev", dataElev), yaw = O("sun.yaw", 0f);
        if (MathF.Abs(elev - dataElev) < 1e-3f && MathF.Abs(yaw) < 1e-3f) return data;
        var flat = new Vector3(data.X, 0f, data.Z);
        flat = flat.LengthSquared() < 1e-6f ? Vector3.Forward : flat.Normalized();
        flat = flat.Rotated(Vector3.Up, yaw * MathF.PI / 180f);
        float e = Math.Clamp(elev, 1f, 89f) * MathF.PI / 180f;
        return (flat * MathF.Cos(e) + Vector3.Down * MathF.Sin(e)).Normalized();
    }

    /// <summary>The sun's colour (data) times the look's per-channel factors <c>sun.cr</c>, <c>sun.cg</c>, <c>sun.cb</c> (1 = the data's colour).</summary>
    static Color SunTint(Color c) => new(c.R * O("sun.cr", 1f), c.G * O("sun.cg", 1f), c.B * O("sun.cb", 1f));

    /// <summary>Volumetric smog on (medium and high by default; a look's <c>vf.on</c>).</summary>
    static bool VolumetricOn => O("vf.on", Preset >= LightingPreset.Medium ? 1f : 0f) > 0.5f;

    /// <summary>What the preset turned on (for the log and the F1 line).</summary>
    public static string PresetNote => Preset switch
    {
        LightingPreset.Off => RenderQuality.ClassicLook ? "classic look" : "M40-T7 look, M42 off",
        LightingPreset.Low => "M42 low: soft sun shadows, SSAO",
        LightingPreset.Medium => "M42 medium: soft sun shadows, SSAO, SSIL, volumetric fog",
        _ => "M42 high: soft sun shadows with penumbrae, SSAO, SSIL, volumetric fog, SSR",
    };

    /// <summary>
    /// M42 sun shadows: the data's 4 splits (<c>ShadowSplits</c>) over its 400 m (<c>FarSunDistance</c>), split distances weighted toward the
    /// camera, blended cascades, a larger shadow map and softer filtering, and an angular size (softer penumbrae further from the caster; PCSS
    /// on high). Values **ours**.
    /// </summary>
    static void ApplySunShadows(DirectionalLight3D sun, SunLight data)
    {
        sun.DirectionalShadowMode = data.ShadowSplits switch
        {
            <= 1 => DirectionalLight3D.ShadowMode.Orthogonal,
            2 => DirectionalLight3D.ShadowMode.Parallel2Splits,
            _ => DirectionalLight3D.ShadowMode.Parallel4Splits,
        };
        sun.DirectionalShadowSplit1 = O("sh.s1", 0.05f);
        sun.DirectionalShadowSplit2 = O("sh.s2", 0.15f);
        sun.DirectionalShadowSplit3 = O("sh.s3", 0.4f);
        sun.DirectionalShadowBlendSplits = Preset >= LightingPreset.Medium;
        sun.DirectionalShadowFadeStart = 0.85f;
        sun.ShadowBlur = O("sh.blur", 1.2f);
        sun.LightAngularDistance = Preset == LightingPreset.High ? O("sun.angle", 0.6f) : 0f;
        int atlas = Preset == LightingPreset.High ? 8192 : 4096;
        var filter = Preset switch
        {
            LightingPreset.High => RenderingServer.ShadowQuality.SoftHigh,
            LightingPreset.Medium => RenderingServer.ShadowQuality.SoftMedium,
            _ => RenderingServer.ShadowQuality.SoftLow,
        };
        PendingGlobal.Add(() => { RenderingServer.DirectionalShadowAtlasSetSize(atlas, true); RenderingServer.DirectionalSoftShadowFilterSetQuality(filter); });
        sun.LightVolumetricFogEnergy = O("vf.sun", 1.0f);
    }

    /// <summary>
    /// Blends the environment regions by camera position (game space): the global region applies everywhere; inside another region
    /// (a box, <see cref="MapEnvironment.Contains"/>) its values take over smoothly over <see cref="Feather"/> metres from the box
    /// faces; overlaps apply in priority order. Blended: sun power and colour, ambient colours, fog, bloom. The original's own blend
    /// (each description has a <c>BlendDuration</c> of 0 to 3 s, so it fades by time) is not reproduced: **provisional approximation**.
    /// </summary>
    public sealed class EnvironmentBlender
    {
        public const float Feather = 8f;
        readonly MapEnvironment _global;
        readonly SunLight? _sunData;
        readonly DirectionalLight3D _sun;
        readonly Godot.Environment _environment;
        readonly ProceduralSkyMaterial? _sky;
        readonly ShaderMaterial? _skyShader;   // T7 look
        readonly bool _classic = RenderQuality.ClassicLook;
        Params? _last;
        FogMaterial? _fogMaterial;

        /// <summary>M42 (medium and high): the low-lying smog as a fog volume (game space; the caller adds it under the lights node), or null.</summary>
        public FogVolume? FogVolume { get; private set; }

        readonly MapEnvironment[] _others;
        readonly Params[] _otherParams;
        readonly float[] _weights;
        readonly Params _globalParams;

        /// <summary>The regions with a nonzero weight at the last position, with their weights ("" when only the global one applies).</summary>
        public string Active => string.Join(", ", _others.Select((e, i) => (e, w: _weights[i])).Where(x => x.w > 0f).Select(x => $"{x.e.Name} {x.w:0.00}"));

        static bool Near(Params a, Params b)
        {
            const float eps = 0.002f;
            static float D(Num.Vector3 x, Num.Vector3 y) => MathF.Max(MathF.Abs(x.X - y.X), MathF.Max(MathF.Abs(x.Y - y.Y), MathF.Abs(x.Z - y.Z)));
            return MathF.Abs(a.SunPower - b.SunPower) < eps * 10 && D(a.SunColour, b.SunColour) < eps && D(a.Upper, b.Upper) < eps && D(a.Lower, b.Lower) < eps
                   && MathF.Abs(a.FogFar.X - b.FogFar.X) + MathF.Abs(a.FogFar.Y - b.FogFar.Y) + MathF.Abs(a.FogFar.Z - b.FogFar.Z) + MathF.Abs(a.FogFar.W - b.FogFar.W)
                      + MathF.Abs(a.FogNear.X - b.FogNear.X) + MathF.Abs(a.FogNear.Y - b.FogNear.Y) + MathF.Abs(a.FogNear.Z - b.FogNear.Z) < eps
                   && MathF.Abs(a.FogRange - b.FogRange) < 0.5f && MathF.Abs(a.Bloom - b.Bloom) < eps * 10 && MathF.Abs(a.HdrMax - b.HdrMax) < eps
                   && MathF.Abs(a.SsaoMultiplier - b.SsaoMultiplier) < eps;
        }

        record struct Params(float SunPower, Num.Vector3 SunColour, Num.Vector3 Upper, Num.Vector3 Lower, Num.Vector4 FogNear, Num.Vector4 FogFar, float FogRange,
                             float Bloom, float HdrMin, float HdrMax, float SsaoMultiplier);

        public EnvironmentBlender(MapLighting lighting, MapEnvironment global, SunLight? sunData, DirectionalLight3D sun, Godot.Environment environment)
        {
            _global = global; _sunData = sunData; _sun = sun; _environment = environment;
            if (global.Ambient is { } ambient)
            {
                if (_classic) { _sky = new ProceduralSkyMaterial { SkyCurve = 0.25f, GroundCurve = 0.25f }; environment.Sky = new Sky { SkyMaterial = _sky }; }
                else
                {
                    _skyShader = new ShaderMaterial { Shader = new Shader { Code = SkyShaderCode } };
                    // M42: the radiance map (ambient and reflections) is a smooth hemisphere gradient: 128 px is plenty, and incremental updates
                    // spread its re-filtering over frames when an environment region's colours blend in (no hitch at region borders).
                    environment.Sky = new Sky
                    {
                        SkyMaterial = _skyShader,
                        RadianceSize = Sky.RadianceSizeEnum.Size128,
                        ProcessMode = Sky.ProcessModeEnum.Incremental,
                    };
                    environment.BackgroundMode = Godot.Environment.BGMode.Sky;
                }
                environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
                environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
                environment.AmbientLightSkyContribution = 1f;
                environment.AmbientLightEnergy = V("amb.energy", AmbientEnergy, 1f);
                environment.SsaoEnabled = !ambient.SsaoDisabled;
            }
            if (global.Fog is { Range: > 0f }) { environment.FogEnabled = true; environment.FogSkyAffect = V("fog.sky", 0.5f, 0.35f); }
            if (global.Hdr is not null) { environment.GlowEnabled = true; environment.GlowBloom = V("glow.bloom", 0.05f, 0.05f); }
            if (!_classic) ApplyGradeOnce(environment, global.Hdr);
            if (!_classic && Preset != LightingPreset.Off) ApplyOverhaul(environment, global);
            _globalParams = For(global);
            _others = lighting.Environments.Where(e => !ReferenceEquals(e, global)).OrderBy(e => e.Priority).ToArray();
            _otherParams = _others.Select(For).ToArray();
            _weights = new float[_others.Length];
        }

        Params For(MapEnvironment e)
        {
            var sun = e.Sun ?? _global.Sun ?? _sunData;
            var ambient = e.Ambient ?? _global.Ambient;
            var fog = e.Fog ?? _global.Fog;
            var hdr = e.Hdr ?? _global.Hdr;
            return new Params(
                sun?.Power ?? 1f, sun?.Colour ?? Num.Vector3.One,
                ambient is null ? Num.Vector3.One * 0.5f : Saturate(ambient.Upper, ambient.Saturation),
                ambient is null ? Num.Vector3.One * 0.3f : Saturate(ambient.Lower, ambient.Saturation),
                fog?.NearColour ?? Num.Vector4.Zero, fog?.FarColour ?? Num.Vector4.Zero, fog?.Range ?? 0f, hdr?.BloomIntensity ?? 1f,
                hdr?.BrightThresholdMin ?? 0.4f, hdr?.BrightThresholdMax ?? 0.8f, ambient?.SsaoMultiplier ?? 1f);
        }

        static Params Lerp(Params a, Params b, float t) => new(
            a.SunPower + (b.SunPower - a.SunPower) * t, Num.Vector3.Lerp(a.SunColour, b.SunColour, t),
            Num.Vector3.Lerp(a.Upper, b.Upper, t), Num.Vector3.Lerp(a.Lower, b.Lower, t),
            Num.Vector4.Lerp(a.FogNear, b.FogNear, t), Num.Vector4.Lerp(a.FogFar, b.FogFar, t), a.FogRange + (b.FogRange - a.FogRange) * t,
            a.Bloom + (b.Bloom - a.Bloom) * t, a.HdrMin + (b.HdrMin - a.HdrMin) * t, a.HdrMax + (b.HdrMax - a.HdrMax) * t, a.SsaoMultiplier + (b.SsaoMultiplier - a.SsaoMultiplier) * t);

        /// <summary>Weight 0 outside the box, rising smoothly to 1 over <see cref="Feather"/> metres inside it.</summary>
        public static float Weight(MapEnvironment e, Num.Vector3 p) => e.Weight(p, Feather);

        /// <summary>Re-evaluates the regions at a camera position (game space) and updates the sun, sky, fog and bloom if anything changed.</summary>
        public void Update(Num.Vector3 gamePosition)
        {
            var p = _globalParams;
            for (int i = 0; i < _others.Length; i++)
            {
                float w = Weight(_others[i], gamePosition);
                _weights[i] = w;
                if (w > 0f) p = Lerp(p, _otherParams[i], w);
            }
            // Re-setting sky colours makes Godot rebuild the radiance map: skip changes too small to see.
            if (_last is { } last && Near(last, p)) return;
            _last = p;
            if (_sunData is not null) { _sun.LightColor = SunTint(ToColor(p.SunColour)); _sun.LightEnergy = p.SunPower * SunEnergyPerPower * O("sun.mul", 1f); }
            if (_sky is not null)
            {
                var mid = (p.Upper + p.Lower) * 0.5f;
                _sky.SkyTopColor = ToColor(p.Upper); _sky.SkyHorizonColor = ToColor(mid);
                _sky.GroundBottomColor = ToColor(p.Lower); _sky.GroundHorizonColor = ToColor(mid);
                _environment.SsaoIntensity = 2f * p.SsaoMultiplier;
            }
            if (_skyShader is not null)
            {
                // Hemisphere ambient: the data's colours. How the engine reads them (sRGB or linear) is unknown: amb.gamma 1 = as linear, 2.2 = as sRGB.
                float gamma = V("amb.gamma", 1f, 2.2f), sat = V("amb.sat", 1f, 0.25f), mul = V("amb.mul", 1f, 1f);
                Vector3 Amb(Num.Vector3 c)
                {
                    var d = Saturate(c, sat);
                    return new Vector3(MathF.Pow(MathF.Max(d.X, 0f), gamma), MathF.Pow(MathF.Max(d.Y, 0f), gamma), MathF.Pow(MathF.Max(d.Z, 0f), gamma)) * mul;
                }
                _skyShader.SetShaderParameter("amb_upper", Amb(p.Upper));
                _skyShader.SetShaderParameter("amb_lower", Amb(p.Lower));
                // Background haze (ours): the fog's colour at the horizon (the original's sky is a texture, not decoded), darker overhead with a neutral grey.
                var haze = SatOf(FogTint(p), O("sky.sat", 1f));   // a look's sky.sat: the haze colour's saturation in the sky (1 = the data's)
                float top = V("sky.top", 0f, 0.28f), ground = V("sky.ground", 0f, 0.5f), curve = V("sky.curve", 0f, 0.45f);
                float hl = 0.2126f * haze.X + 0.7152f * haze.Y + 0.0722f * haze.Z;
                var neutral = new Vector3(hl, hl, hl);
                _skyShader.SetShaderParameter("sky_horizon", ToLinear(haze) * V("sky.horizon", 0f, 1.5f));
                _skyShader.SetShaderParameter("sky_top", ToLinear(neutral * 0.6f + haze * 0.4f) * top);
                _skyShader.SetShaderParameter("sky_ground", ToLinear(haze) * ground);
                _skyShader.SetShaderParameter("sky_curve", curve);
                if (Preset != LightingPreset.Off)
                {
                    // M42: the sun's colour (data) as a halo in the haze around its direction (strength ours).
                    var sunLin = ToLinear(new Vector3(p.SunColour.X, p.SunColour.Y, p.SunColour.Z));
                    _skyShader.SetShaderParameter("sun_halo", sunLin * O("sky.halo", 0.35f));
                    _skyShader.SetShaderParameter("sun_halo_power", O("sky.halo.pow", 5f));
                    _skyShader.SetShaderParameter("sun_core", sunLin * O("sky.core", 1.2f));
                    // Bounce fill (ours): the sun's linear colour x its Godot energy x an average ground reflectance and a share of the hemisphere.
                    _skyShader.SetShaderParameter("bounce", sunLin * (p.SunPower * SunEnergyPerPower * O("sun.mul", 1f)) * O("amb.bounce", 0.05f));
                }
                _environment.SsaoIntensity = V("ssao", 2f, 2f) * p.SsaoMultiplier;
            }
            if (p.FogRange > 0f)
            {
                // The gradient's alpha runs from 0 (near stop) to the far stop's alpha over OutscatterRange metres, then stays:
                // depth fog with that alpha as its largest opacity. The colour is the far stop's (the near stop's is not used).
                _environment.FogMode = Godot.Environment.FogModeEnum.Depth;
                _environment.FogDepthBegin = 0f;
                _environment.FogDepthEnd = p.FogRange * O("fog.end", 1f);
                _environment.FogDepthCurve = 1f;
                _environment.FogDensity = Math.Clamp(p.FogFar.W * V("fog.k", 1f, 1f), 0.0f, 0.95f);
                if (_classic)
                {
                    _environment.FogLightColor = new Color(p.FogFar.X, p.FogFar.Y, p.FogFar.Z);
                    _environment.BackgroundColor = new Color(p.FogFar.X, p.FogFar.Y, p.FogFar.Z) * 0.5f;
                }
                else
                {
                    // M42 fix: Godot converts FogLightColor from sRGB itself, so the data colour goes in as stored (T7 raised it to 2.2 first,
                    // which squared the conversion and darkened the fog). fog.gamma stays as a test knob.
                    var f = SatOf(FogTint(p), O("fog.sat", 1f)); float g = V("fog.gamma", 1f, 1f);
                    _environment.FogLightColor = new Color(MathF.Pow(MathF.Max(f.X, 0f), g), MathF.Pow(MathF.Max(f.Y, 0f), g), MathF.Pow(MathF.Max(f.Z, 0f), g));
                    _environment.FogLightEnergy = O("fog.energy", 1f);
                    if (_fogMaterial is not null)
                    {
                        var albedo = FogAlbedo(f);
                        _environment.VolumetricFogAlbedo = albedo;
                        _fogMaterial.Albedo = albedo;
                    }
                }
            }
            if (_classic)
            {
                _environment.GlowHdrThreshold = Math.Max(0.6f, p.HdrMax + 0.3f);
                _environment.GlowIntensity = 0.35f * p.Bloom;
            }
            else
            {
                // Bloom from the data's HDRDesc (interpretation): BrightThresholdMin is Godot's HDR threshold, the range up to BrightThresholdMax
                // sets how hard it knees (scale), BloomIntensity scales the intensity.
                _environment.GlowHdrThreshold = V("glow.thr", 0f, p.HdrMin);
                _environment.GlowHdrScale = V("glow.scale", 2f, 1f + 0.5f / MathF.Max(p.HdrMax - p.HdrMin, 0.1f));
                _environment.GlowIntensity = V("glow.int", 0f, 0.6f) * p.Bloom;
            }
        }

        /// <summary>
        /// The fog's colour for Godot's single-colour fog (interpretation): the data's gradient runs from the near stop (alpha 0) to the far stop (alpha
        /// 69/255) over the range, so the fog's own contribution (alpha x colour) is weighted toward the far end; the alpha-weighted mean colour
        /// of a linear alpha ramp is the gradient at 2/3 of the way.
        /// </summary>
        static Vector3 FogTint(Params p)
        {
            float t = Math.Clamp(V("fog.t", 0.667f, 0.55f), 0f, 1f);
            var c = Num.Vector4.Lerp(p.FogNear, p.FogFar, t);
            return new Vector3(c.X, c.Y, c.Z);
        }

        /// <summary>The volumetric smog's albedo: the data's fog colour with part of its saturation (<c>vf.sat</c>, ours): lit by the warm sun it turns warm
        /// by itself, and in the shade it should not tint the blacks.</summary>
        static Color FogAlbedo(Vector3 fog)
        {
            float sat = O("vf.sat", 0.2f), l = 0.2126f * fog.X + 0.7152f * fog.Y + 0.0722f * fog.Z;
            var c = new Vector3(l, l, l) + (fog - new Vector3(l, l, l)) * sat;
            return new Color(c.X, c.Y, c.Z);
        }

        /// <summary><paramref name="c"/> with its saturation scaled by <paramref name="s"/> about its luminance, clamped at 0 (1 = unchanged).</summary>
        static Vector3 SatOf(Vector3 c, float s)
        {
            if (MathF.Abs(s - 1f) < 1e-4f) return c;
            float l = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
            var r = new Vector3(l, l, l) + (c - new Vector3(l, l, l)) * s;
            return new Vector3(MathF.Max(r.X, 0f), MathF.Max(r.Y, 0f), MathF.Max(r.Z, 0f));
        }

        static Vector3 ToLinear(Vector3 srgb) => new(MathF.Pow(srgb.X, 2.2f), MathF.Pow(srgb.Y, 2.2f), MathF.Pow(srgb.Z, 2.2f));

        /// <summary>
        /// Tone mapping and colour grade (T7, ours): AgX or filmic, exposure, white point and a contrast / saturation adjustment (test knobs <c>tm</c>
        /// 0 linear 1 reinhard 2 filmic 3 aces 4 agx, <c>tm.exp</c>, <c>tm.white</c>, <c>adj.con</c>, <c>adj.sat</c>), and the bloom's level weights from
        /// <c>BloomPersistence</c> (interpretation: each wider level counts persistence x 2 times the one before).
        /// </summary>
        void ApplyGradeOnce(Godot.Environment environment, HdrDesc? hdr)
        {
            int tm = (int)V("tm", 0f, 4f);
            environment.TonemapMode = tm switch
            {
                0 => Godot.Environment.ToneMapper.Linear, 1 => Godot.Environment.ToneMapper.Reinhardt, 2 => Godot.Environment.ToneMapper.Filmic,
                3 => Godot.Environment.ToneMapper.Aces, _ => Godot.Environment.ToneMapper.Agx,
            };
            environment.TonemapExposure = V("tm.exp", 1.15f, 1.25f);
            environment.TonemapWhite = V("tm.white", 1f, 1f);
            float con = V("adj.con", 1f, 1.08f), satur = V("adj.sat", 1f, 1f), bright = O("adj.bri", 1f);
            if (MathF.Abs(con - 1f) > 1e-3f || MathF.Abs(satur - 1f) > 1e-3f || MathF.Abs(bright - 1f) > 1e-3f)
            {
                environment.AdjustmentEnabled = true; environment.AdjustmentContrast = con; environment.AdjustmentSaturation = satur; environment.AdjustmentBrightness = bright;
            }
            environment.GlowBlendMode = (int)O("glow.mode", 1f) switch
            {
                0 => Godot.Environment.GlowBlendModeEnum.Additive, 2 => Godot.Environment.GlowBlendModeEnum.Softlight,
                3 => Godot.Environment.GlowBlendModeEnum.Replace, 4 => Godot.Environment.GlowBlendModeEnum.Mix, _ => Godot.Environment.GlowBlendModeEnum.Screen,
            };
            float persistence = hdr?.BloomPersistence ?? 0.25f;
            for (int i = 0; i < 7; i++) environment.SetGlowLevel(i, Math.Min(1f, MathF.Pow(Math.Clamp(persistence * 2f, 0.1f, 1f), i)) * V("glow.lv", 1f, 1f));
        }

        /// <summary>
        /// M42 colour grade (**ours**): a per-channel curve after the tone mapping (Godot's 1D colour-correction LUT) for the imperial forge look:
        /// a deeper toe (<c>grade.toe</c>: the darkest tones pressed down, real blacks), shadows pulled toward a cool neutral (<c>grade.cool</c>, so the warm
        /// smog does not tint them) and highlights a little warmer (<c>grade.warm</c>). Values are offsets in 0..1 output units.
        /// </summary>
        static void ApplyGrade(Godot.Environment env)
        {
            float toe = O("grade.toe", 0.03f), cool = O("grade.cool", 0.045f), warm = O("grade.warm", 0.02f);
            // Control points: input level -> (r, g, b) output. Shadows (0.05-0.3) lose red and gain blue; highlights (0.6-0.9) the reverse.
            (float x, float r, float g, float b)[] pts =
            {
                (0f, 0f, 0f, 0f),
                (0.06f, 0.06f - toe - cool * 0.5f, 0.06f - toe - cool * 0.15f, 0.06f - toe + cool * 0.3f),
                (0.18f, 0.18f - cool, 0.18f - cool * 0.3f, 0.18f + cool * 0.6f),
                (0.4f, 0.4f - cool * 0.4f, 0.4f, 0.4f + cool * 0.2f),
                (0.7f, 0.7f + warm, 0.7f + warm * 0.3f, 0.7f - warm * 0.8f),
                (0.9f, 0.9f + warm * 0.5f, 0.9f + warm * 0.2f, 0.9f - warm * 0.5f),
                (1f, 1f, 1f, 1f),
            };
            var gradient = new Gradient();
            gradient.Offsets = pts.Select(p => p.x).ToArray();
            gradient.Colors = pts.Select(p => new Color(Math.Clamp(p.r, 0f, 1f), Math.Clamp(p.g, 0f, 1f), Math.Clamp(p.b, 0f, 1f))).ToArray();
            env.AdjustmentEnabled = true;
            env.AdjustmentColorCorrection = new GradientTexture1D { Gradient = gradient, Width = 256, UseHdr = false };
        }

        /// <summary>
        /// M42 overhaul (**ours**; a deliberate upgrade over the original renderer, owner's decision 2026-10-28): contact shading (SSAO, and on
        /// medium+ screen-space indirect light), reflections on glossy floors and metal (SSR, high),
        /// and the map's smog as volumetric fog (medium+) that the sun and the lamps light: shafts through the roof openings. The fog's colour is the
        /// data's (<c>OutscatterColour</c>), its thickness ours (the data's depth fog stays as the far haze). Knobs: <c>--look-set</c>.
        /// </summary>
        void ApplyOverhaul(Godot.Environment env, MapEnvironment global)
        {
            var q = Preset;
            ApplyGrade(env);
            // Contact shading: SSAO (the data's AmbientDesc enables it and scales it: SsaoMultiplier) with a wider radius and a little effect on direct light.
            env.SsaoRadius = O("ssao.radius", 1.4f);
            env.SsaoPower = O("ssao.power", 1.6f);
            env.SsaoDetail = O("ssao.detail", 0.6f);
            env.SsaoHorizon = 0.06f;
            env.SsaoSharpness = 0.98f;
            env.SsaoLightAffect = O("ssao.light", 0.15f);
            env.SsaoAOChannelAffect = O("ssao.channel", 0.3f);
            PendingGlobal.Add(() => RenderingServer.EnvironmentSetSsaoQuality(
                q == LightingPreset.High ? RenderingServer.EnvironmentSsaoQuality.High : RenderingServer.EnvironmentSsaoQuality.Medium, q != LightingPreset.High, 0.5f, 2, 50f, 300f));
            if (O("ssil.on", q >= LightingPreset.Medium ? 1f : 0f) > 0.5f)
            {
                env.SsilEnabled = true;
                env.SsilRadius = O("ssil.radius", 6f);
                env.SsilIntensity = O("ssil.int", 2f);
                env.SsilSharpness = 0.98f;
                env.SsilNormalRejection = 1f;
                PendingGlobal.Add(() => RenderingServer.EnvironmentSetSsilQuality(
                    q == LightingPreset.High ? RenderingServer.EnvironmentSsilQuality.High : RenderingServer.EnvironmentSsilQuality.Medium, q != LightingPreset.High, 0.5f, 4, 50f, 300f));
            }
            if (q == LightingPreset.High)
            {
                env.SdfgiEnabled = O("gi.on", 0f) > 0.5f;   // off by default: on Manufactorum it hides whole walls (Godot 4.7.2; see the spec row), test knob only
                env.SdfgiCascades = (int)O("gi.cascades", 5f);
                env.SdfgiMinCellSize = O("gi.cell", 0.25f);
                env.SdfgiUseOcclusion = true;
                env.SdfgiReadSkyLight = true;
                env.SdfgiBounceFeedback = O("gi.bounce", 0.5f);
                env.SdfgiEnergy = O("gi.energy", 1.0f);
                env.SdfgiNormalBias = 1.1f;
                env.SdfgiProbeBias = 1.1f;
                env.SdfgiYScale = Godot.Environment.SdfgiyScale.Scale75Percent;
                if (env.SdfgiEnabled)
                    PendingGlobal.Add(() =>
                    {
                        RenderingServer.EnvironmentSetSdfgiRayCount(RenderingServer.EnvironmentSdfgiRayCount.Count64);
                        RenderingServer.EnvironmentSetSdfgiFramesToConverge(RenderingServer.EnvironmentSdfgiFramesToConverge.In20Frames);
                        RenderingServer.EnvironmentSetSdfgiFramesToUpdateLight(RenderingServer.EnvironmentSdfgiFramesToUpdateLight.In8Frames);
                    });

            }
            // SSR: high by default; a look can turn it on (ssr.on) at medium and high only, never at low (fx/impact-pass review: the remaster default must not cost older hardware).
            if (SsrOn(q, O("ssr.on", q == LightingPreset.High ? 1f : 0f)))
            {
                env.SsrEnabled = true;
                env.SsrMaxSteps = (int)O("ssr.steps", 56f);
                env.SsrFadeIn = 0.15f;
                env.SsrFadeOut = 2f;
                env.SsrDepthTolerance = O("ssr.depth", 0.25f);
            }
            if (VolumetricOn && global.Fog is { } fog)
            {
                var albedo = FogAlbedo(FogTint(For(global)));
                env.VolumetricFogEnabled = true;
                // vf.haze (ours, 1): one knob for the smog's thickness (both densities below), if the owner finds it too strong.
                float haze = Math.Max(0f, O("vf.haze", 1f));
                env.VolumetricFogDensity = O("vf.density", 0.002f) * haze;   // a thin even haze everywhere ...
                env.VolumetricFogAlbedo = albedo;
                env.VolumetricFogEmissionEnergy = 0f;
                env.VolumetricFogAnisotropy = O("vf.aniso", 0.45f);   // forward scattering: bright toward the sun, shafts readable
                env.VolumetricFogLength = O("vf.length", 120f);       // beyond that the data's depth fog carries the haze
                env.VolumetricFogDetailSpread = 2f;
                env.VolumetricFogGIInject = q == LightingPreset.High ? O("vf.gi", 0.6f) : 0f;
                env.VolumetricFogAmbientInject = O("vf.ambient", 0.15f);
                env.VolumetricFogSkyAffect = O("vf.sky", 0.1f);
                env.VolumetricFogTemporalReprojectionEnabled = true;
                env.VolumetricFogTemporalReprojectionAmount = 0.9f;
                PendingGlobal.Add(() =>
                {
                    RenderingServer.EnvironmentSetVolumetricFogVolumeSize(q == LightingPreset.High ? 160 : 96, q == LightingPreset.High ? 96 : 64);
                    RenderingServer.EnvironmentSetVolumetricFogFilterActive(true);
                });
                // ... and thicker smog close to the ground, thinning with height. Interpretation: the data's FogDesc GroundHeight (0 m) and GroundRange
                // (18 m) give where it starts and the height over which it halves (Godot's falloff is exp2(-falloff x height above the volume's origin)).
                float range = fog.GroundRange > 0.5f ? fog.GroundRange : 16f;
                _fogMaterial = new FogMaterial
                {
                    Density = O("vf.ground", 0.007f) * haze, Albedo = albedo, HeightFalloff = O("vf.falloff", 1f / range), EdgeFade = 0.1f,
                };
                var c = global.Centre; var size = global.Size;
                FogVolume = new FogVolume
                {
                    Name = "GroundSmog", Shape = RenderingServer.FogVolumeShape.Box, Material = _fogMaterial,
                    Size = new Vector3(size.X, size.Y, O("vf.height", 120f)),
                    Position = new Vector3(c.X, c.Y, fog.GroundHeight + O("vf.base", 2f)),   // game space: the falloff starts at the origin's height
                };
            }
        }
    }

    static Num.Vector3 Saturate(Num.Vector3 c, float saturation)
    {
        float luma = 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;
        return new Num.Vector3(luma) + (c - new Num.Vector3(luma)) * saturation;
    }

    static Color ToColor(Num.Vector3 c) => new(c.X, c.Y, c.Z);

    /// <summary>Frame-time log: mean and worst frame over each period, plus the renderer's own CPU/GPU time when it reports one.</summary>
    public sealed class FrameStats
    {
        readonly Viewport _viewport;
        double _sum, _worst, _elapsed, _gpuSum, _gpuWorst, _cpuSum;
        int _frames;
        public double LastMeanMs { get; private set; }
        public double LastWorstMs { get; private set; }

        public FrameStats(Viewport viewport)
        {
            _viewport = viewport;
            RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
        }

        /// <summary>Call every frame; prints a line each <paramref name="period"/> seconds.</summary>
        public void Add(double delta, double period = 5.0)
        {
            var rid = _viewport.GetViewportRid();
            // fx/no-smoke: the renderer's own times summed every frame (the engine reports the last measured frame), so the line gives their mean and worst.
            double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid), cpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
            _gpuSum += gpu; _gpuWorst = Math.Max(_gpuWorst, gpu); _cpuSum += cpu;
            _sum += delta; _worst = Math.Max(_worst, delta); _frames++; _elapsed += delta;
            if (_elapsed < period) return;
            LastMeanMs = _sum / _frames * 1000.0; LastWorstMs = _worst * 1000.0;
            GD.Print($"frame time: mean {LastMeanMs:0.0} ms ({1000.0 / LastMeanMs:0} fps), worst {LastWorstMs:0.0} ms over {_frames} frames; render cpu {cpu:0.0} ms gpu {gpu:0.0} ms (last frame); " +
                     $"mean render cpu {_cpuSum / _frames:0.00} ms, gpu {_gpuSum / _frames:0.00} ms (worst {_gpuWorst:0.0})");
            _sum = _worst = _elapsed = _gpuSum = _gpuWorst = _cpuSum = 0; _frames = 0;
        }
    }
}
