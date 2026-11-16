using System;
using System.Runtime.CompilerServices;
using Godot;
using SmClient.Customizer.Model;

namespace SmClient;

/// <summary>
/// The lighting preset of OPTIONS, GRAPHICS (ui/lighting-presets; client only and cosmetic, no simulation or protocol meaning; **ours**): each frame it applies the chosen
/// <see cref="LightingGrade"/> on top of whatever the map's lighting code set on the environment the camera draws with and on the sun (<see cref="MapLightingView"/>, its region
/// blender, the Musou hub's own sky), on every map and in both renderers (it only scales values the renderer already uses; nothing is switched on that the Compatibility renderer lacks
/// but the colour adjustment, which it has). Every touched value keeps its **base** (the value the lighting code last wrote): when a property no longer holds what this view wrote, someone
/// else changed it and that becomes the new base, so the region blender's writes are graded too and nothing compounds. With Faithful Remaster every base is written back and the view stops
/// touching anything; a player who never picks a preset never has a value touched. Test aid <c>--lighting-preset &lt;1-10|name&gt;</c> overrides the saved choice for one run.
/// </summary>
public sealed class LightingGradeView
{
    static readonly LightingPresetChoice? CommandLine = ParseCommandLine();

    static LightingPresetChoice? ParseCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--lighting-preset");
        if (i < 0) return null;
        string? text = i + 1 < args.Length ? args[i + 1] : null;
        var choice = LightingPresets.Parse(text);
        if (choice is null) GD.PrintErr($"--lighting-preset '{text}' is not known (1-10 or a name: {string.Join(", ", LightingPresets.Labels)}); the saved choice is used");
        else GD.Print($"lighting preset: {LightingPresets.Label(choice.Value)} (--lighting-preset)");
        return choice;
    }

    /// <summary>Capture aid <c>--lighting-preset-cycle &lt;seconds&gt;</c>: every preset in turn, that long each, from the first graded frame (with <c>--screenshot-every</c>: one series, all ten looks).</summary>
    static readonly double CycleSeconds = ParseCycle();
    static double ParseCycle()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--lighting-preset-cycle");
        return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 0;
    }
    static ulong _cycleStart;

    /// <summary>The preset in use: the cycle's, the command line's, else the saved option.</summary>
    public static LightingPresetChoice Current
    {
        get
        {
            if (CycleSeconds <= 0) return CommandLine ?? LiveOptions.Current.Lighting;
            if (_cycleStart == 0) _cycleStart = Time.GetTicksMsec();
            return (LightingPresetChoice)((int)((Time.GetTicksMsec() - _cycleStart) / 1000.0 / CycleSeconds) % LightingPresets.Labels.Count);
        }
    }

    struct Slot<T> where T : struct
    {
        public bool Has;
        public T Base, Written;
    }

    sealed class EnvState
    {
        public Slot<float> Exposure, Contrast, Saturation, Brightness, Ambient, FogDensity, VolDensity, Glow, Ssao;
        public Slot<bool> Adjust;
        public Slot<Color> FogColour, VolAlbedo;
    }

    sealed class SunState
    {
        public Slot<float> Energy;
        public Slot<Color> Colour;
    }

    readonly ConditionalWeakTable<Godot.Environment, EnvState> _envs = new();
    readonly ConditionalWeakTable<DirectionalLight3D, SunState> _suns = new();
    LightingPresetChoice _applied = LightingPresetChoice.FaithfulRemaster;
    bool _touched;

    /// <summary>Applies the current preset to <paramref name="environment"/> (the one the camera draws with) and <paramref name="sun"/>; call once a frame after the lighting code's own update.</summary>
    public void Frame(Godot.Environment? environment, DirectionalLight3D? sun)
    {
        var preset = Current;
        if (preset != _applied) { GD.Print($"lighting preset: {LightingPresets.Label(preset)}"); _applied = preset; }
        var g = LightingPresets.For(preset);
        if (g.IsIdentity && !_touched) return;   // never chosen (or already restored): nothing is touched
        if (environment is not null && GodotObject.IsInstanceValid(environment)) Grade(_envs.GetOrCreateValue(environment), environment, g);
        if (sun is not null && GodotObject.IsInstanceValid(sun)) Grade(_suns.GetOrCreateValue(sun), sun, g);
        if (g.IsIdentity) { _envs.Clear(); _suns.Clear(); _touched = false; }   // every base written back
        else _touched = true;
    }

    static void Grade(EnvState s, Godot.Environment e, LightingGrade g)
    {
        Track(ref s.Exposure, e.TonemapExposure, b => b * g.Exposure, v => e.TonemapExposure = v, () => e.TonemapExposure);
        bool adjust = g.Contrast != 1f || g.Saturation != 1f || g.Brightness != 1f;
        Track(ref s.Adjust, e.AdjustmentEnabled, b => b || adjust, v => e.AdjustmentEnabled = v, () => e.AdjustmentEnabled);
        Track(ref s.Contrast, e.AdjustmentContrast, b => b * g.Contrast, v => e.AdjustmentContrast = v, () => e.AdjustmentContrast);
        Track(ref s.Saturation, e.AdjustmentSaturation, b => b * g.Saturation, v => e.AdjustmentSaturation = v, () => e.AdjustmentSaturation);
        Track(ref s.Brightness, e.AdjustmentBrightness, b => b * g.Brightness, v => e.AdjustmentBrightness = v, () => e.AdjustmentBrightness);
        Track(ref s.Ambient, e.AmbientLightEnergy, b => b * g.Ambient, v => e.AmbientLightEnergy = v, () => e.AmbientLightEnergy);
        Track(ref s.FogDensity, e.FogDensity, b => b * g.FogDensity, v => e.FogDensity = v, () => e.FogDensity);
        Track(ref s.VolDensity, e.VolumetricFogDensity, b => b * g.FogDensity, v => e.VolumetricFogDensity = v, () => e.VolumetricFogDensity);
        var tint = new Color(g.FogR, g.FogG, g.FogB);
        Track(ref s.FogColour, e.FogLightColor, b => g.FogTint > 0f ? b.Lerp(tint, g.FogTint) : b, v => e.FogLightColor = v, () => e.FogLightColor);
        Track(ref s.VolAlbedo, e.VolumetricFogAlbedo, b => g.FogTint > 0f ? b.Lerp(tint, g.FogTint) : b, v => e.VolumetricFogAlbedo = v, () => e.VolumetricFogAlbedo);
        Track(ref s.Glow, e.GlowIntensity, b => b * g.Glow, v => e.GlowIntensity = v, () => e.GlowIntensity);
        Track(ref s.Ssao, e.SsaoIntensity, b => b * g.Ssao, v => e.SsaoIntensity = v, () => e.SsaoIntensity);
    }

    static void Grade(SunState s, DirectionalLight3D sun, LightingGrade g)
    {
        Track(ref s.Energy, sun.LightEnergy, b => b * g.SunEnergy, v => sun.LightEnergy = v, () => sun.LightEnergy);
        Track(ref s.Colour, sun.LightColor, b => new Color(b.R * g.SunR, b.G * g.SunG, b.B * g.SunB, b.A), v => sun.LightColor = v, () => sun.LightColor);
    }

    /// <summary>
    /// One value: its base is what was there unless it still holds what was written last (then the remembered base); the graded base is written only when it differs,
    /// and what the engine kept (it may clamp) is remembered as written.
    /// </summary>
    static void Track<T>(ref Slot<T> slot, T current, Func<T, T> grade, Action<T> set, Func<T> get) where T : struct
    {
        var b = slot.Has && current.Equals(slot.Written) ? slot.Base : current;
        var target = grade(b);
        if (!target.Equals(current)) { set(target); current = get(); }
        slot.Base = b; slot.Written = current; slot.Has = true;
    }
}
