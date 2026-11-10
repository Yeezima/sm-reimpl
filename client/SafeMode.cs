using System;
using Godot;

namespace SmClient;

/// <summary>
/// hotfix/old-gpu: the "old GPU / safe mode". Godot has no DirectX 9, so an old card (a GTX 960M laptop) runs the Compatibility renderer (OpenGL 3.3, ANGLE as a
/// second try; <c>project.godot</c> lets Godot fall back to it on its own when Vulkan or D3D12 cannot start, the launcher's checkbox forces it with
/// <c>--rendering-method gl_compatibility</c>). Safe mode is on when the renderer in use is the Compatibility one, or with <c>--safe-mode</c>. It means: lighting
/// low (unless <c>--lighting</c> says otherwise), anti-aliasing off, and every effect the Compatibility renderer lacks (volumetric fog, SSR, SSIL, SSAO, SDFGI,
/// glow, SMAA/TAA) is not asked for. Only the look of a few effects changes; no gameplay value does. The default look for everyone else is unchanged.
/// </summary>
public static class SafeMode
{
    /// <summary>The renderer in use is Godot's Compatibility one (no Forward+ features).</summary>
    public static bool Compatibility { get; } = DetectCompatibility();

    /// <summary><c>--safe-mode</c> on the client's command line (the launcher's checkbox adds it).</summary>
    public static bool Flag { get; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--safe-mode") >= 0;

    /// <summary>Safe mode: the Compatibility renderer or <c>--safe-mode</c>.</summary>
    public static bool Active => Compatibility || Flag;

    /// <summary>
    /// polish pass P1 (2026-11-10): in safe mode added content's Ashfall grime is folded into the kit's materials (FAST) whatever the Graphics option says, the overlay pass being what the
    /// Compatibility renderer pays most for; <c>--ashfall-grime-exact-safe</c> keeps the option's choice. A core-side flag so the Options view needs nothing from <c>Extras/</c>
    /// (the faithful build has no <c>AshfallGrime</c>); <c>AshfallGrime.FoldedForSafeMode</c> reads it.
    /// </summary>
    public static bool GrimeFoldedInSafeMode { get; } = Active && Array.IndexOf(OS.GetCmdlineUserArgs(), "--ashfall-grime-exact-safe") < 0;

    static bool DetectCompatibility()
    {
        try { return string.Equals(RenderingServer.GetCurrentRenderingMethod(), "gl_compatibility", StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>One clear line for the log: the renderer, its driver and the mode (also the line the launcher's tester guide points at).</summary>
    public static string Describe()
    {
        string method = "unknown", driver = "unknown";
        try { method = RenderingServer.GetCurrentRenderingMethod(); driver = RenderingServer.GetCurrentRenderingDriverName(); } catch (Exception) { }
        string mode = !Active ? "normal mode (Forward+ look, lighting per --lighting, default medium)"
            : "SAFE MODE on (" + (Compatibility ? "Compatibility renderer" : "--safe-mode") + "): lighting low, anti-aliasing off"
              + (Compatibility ? ", volumetric fog / SSR / SSIL / SSAO / SDFGI / glow not available" : "");
        return $"renderer: {method} ({driver}); {mode}";
    }

    /// <summary>Lighting forced by safe mode (an explicit <c>--lighting</c> wins, except that Compatibility never goes above low).</summary>
    public static MapLightingView.LightingPreset Limit(MapLightingView.LightingPreset chosen, bool explicitLighting)
    {
        if (!Active) return chosen;
        if (!explicitLighting) return MapLightingView.LightingPreset.Low;
        return Compatibility && chosen > MapLightingView.LightingPreset.Low ? MapLightingView.LightingPreset.Low : chosen;
    }
}
