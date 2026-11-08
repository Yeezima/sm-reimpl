using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace SmClient;

/// <summary>Anti-aliasing choice (<c>--aa</c>).</summary>
public enum AaMode
{
    /// <summary>No anti-aliasing at all (what the client did before M40-T7).</summary>
    Off,
    /// <summary>MSAA 4x on geometry and alpha-to-coverage cut-outs.</summary>
    Msaa,
    /// <summary>MSAA 4x plus SMAA on the resolved image (default).</summary>
    MsaaSmaa,
    /// <summary>Temporal AA (smoother, may ghost on fast motion), no MSAA.</summary>
    Taa,
}

/// <summary>
/// Render quality switches (M40-T7, client only). <c>--aa off|msaa|msaa+smaa|taa</c> picks the anti-aliasing (default
/// <c>msaa+smaa</c>); the screen-space roughness limiter (specular anti-aliasing on every material, normal-map shimmer included) stays on in all
/// modes but <c>off</c>. <c>--look classic</c> restores the pre-T7 lighting constants for before/after comparison; <c>--look sm1|remaster|moody|forge</c>
/// applies a named lighting look (<see cref="LookPresets"/>, a set of constants under <c>--look-set</c>); fx/impact-pass (the owner's choice, 2026-10-29): the
/// remaster is the default, <c>--look current</c> gives the look that was the default before it (no preset); <c>--look-set k=v,k=v</c>
/// overrides single lighting constants (<see cref="Look"/>, test aid). All values are **ours** (a choice, not data).
/// </summary>
public static class RenderQuality
{
    static readonly Dictionary<string, string?> Args = ParseArgs(OS.GetCmdlineUserArgs());

    public static AaMode Aa { get; private set; } = ParseAa(Args.GetValueOrDefault("aa"));

    static readonly AaMode StartingAa = Aa;

    /// <summary><c>--aa</c> was given: it wins over the Options menu.</summary>
    public static bool AaFromCommandLine => Args.ContainsKey("aa");

    /// <summary>What the Options menu offers on this renderer (the Compatibility renderer has MSAA, but neither SMAA nor TAA).</summary>
    public static Customizer.Model.AaChoice[] Choices => SafeMode.Compatibility
        ? [Customizer.Model.AaChoice.Default, Customizer.Model.AaChoice.Off, Customizer.Model.AaChoice.Msaa]
        : [Customizer.Model.AaChoice.Default, Customizer.Model.AaChoice.Off, Customizer.Model.AaChoice.Msaa, Customizer.Model.AaChoice.MsaaSmaa, Customizer.Model.AaChoice.Taa];

    public static string ChoiceName(Customizer.Model.AaChoice choice) => choice switch
    {
        Customizer.Model.AaChoice.Off => "OFF", Customizer.Model.AaChoice.Msaa => "MSAA 4X", Customizer.Model.AaChoice.MsaaSmaa => "MSAA 4X + SMAA", Customizer.Model.AaChoice.Taa => "TAA", _ => "DEFAULT",
    };

    /// <summary>The Options menu's choice (ui/menu-rebuild); false when <c>--aa</c> holds it. A caller applies it with <see cref="Apply"/>; cut-out materials follow it when they are next made.</summary>
    public static bool Choose(Customizer.Model.AaChoice choice)
    {
        if (AaFromCommandLine) return false;
        Aa = choice switch
        {
            Customizer.Model.AaChoice.Off => AaMode.Off, Customizer.Model.AaChoice.Msaa => AaMode.Msaa, Customizer.Model.AaChoice.MsaaSmaa => AaMode.MsaaSmaa, Customizer.Model.AaChoice.Taa => AaMode.Taa, _ => StartingAa,
        };
        return true;
    }

    /// <summary>True when the cut-out materials should use alpha-to-coverage (needs MSAA, which FSR 2 replaces).</summary>
    public static bool AlphaToCoverage => Aa is AaMode.Msaa or AaMode.MsaaSmaa && !Fsr2;

    /// <summary>
    /// The upscaler (musou/perf-pass, ours; the owner's brief: FSR 2 in the graphics settings, default off). <c>--fsr off|native|quality|balanced|performance</c> (test aid) wins over the Options menu.
    /// FSR 2 is Godot's own (Forward+ only): on the Compatibility renderer, or any other, the choice is kept but nothing changes (<see cref="Fsr2Supported"/>).
    /// </summary>
    public static Customizer.Model.UpscaleChoice Upscale { get; private set; } = ParseUpscale(Args.GetValueOrDefault("fsr"));

    /// <summary><c>--fsr</c> was given: it wins over the Options menu.</summary>
    public static bool UpscaleFromCommandLine => Args.ContainsKey("fsr");

    /// <summary>Godot's FSR 2 runs on this renderer: Forward+ only (not Mobile, not Compatibility).</summary>
    public static bool Fsr2Supported { get; } = !SafeMode.Compatibility && RenderingServer.GetCurrentRenderingMethod() == "forward_plus";

    /// <summary>FSR 2 is on: chosen and supported. It is then the anti-aliasing too (a temporal one): MSAA, SMAA and TAA stay off, whatever the anti-aliasing choice says.</summary>
    public static bool Fsr2 => Upscale != Customizer.Model.UpscaleChoice.Off && Fsr2Supported;

    static Customizer.Model.UpscaleChoice ParseUpscale(string? text)
    {
        if (text is null) return Customizer.Model.UpscaleChoice.Off;
        if (Customizer.Model.Upscaling.Parse(text) is { } c) return c;
        GD.PrintErr($"--fsr '{text}' is not known (off, native, quality, balanced, performance); FSR 2 stays off");
        return Customizer.Model.UpscaleChoice.Off;
    }

    /// <summary>Test aid (<c>--perf-ab fsr</c>): the upscaler whatever holds it; a caller applies it with <see cref="Apply"/>.</summary>
    public static void ForceUpscale(Customizer.Model.UpscaleChoice choice) => Upscale = choice;

    /// <summary>The Options menu's upscaler; false when <c>--fsr</c> holds it. A caller applies it with <see cref="Apply"/>.</summary>
    public static bool ChooseUpscale(Customizer.Model.UpscaleChoice choice)
    {
        if (UpscaleFromCommandLine) return false;
        Upscale = choice;
        return true;
    }

    /// <summary>
    /// The 3D picture's scaling on the game's window: FSR 2 at its preset's scale when on, else plain scaling at <paramref name="renderScale"/> (the Options menu's render scale; 1 is native). The HUD and the
    /// menus are 2D and stay at the window's resolution either way. A sub-viewport (the customizer's preview) is left as it is.
    /// </summary>
    /// <summary>The Options menu's render scale (set by <c>OptionsHost</c>; 1 until then): plain scaling's scale while FSR 2 is off.</summary>
    public static float RenderScale { get; set; } = 1f;

    public static void ApplyScaling(Viewport viewport, float renderScale)
    {
        if (viewport is not Window) return;
        if (Fsr2)
        {
            viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Fsr2;
            viewport.Scaling3DScale = Customizer.Model.Upscaling.Scale(Upscale) ?? 1f;
        }
        else
        {
            viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
            viewport.Scaling3DScale = renderScale;
        }
    }

    /// <summary><c>--look classic</c>: the lighting constants from before M40-T7.</summary>
    public static bool ClassicLook { get; } = string.Equals(Args.GetValueOrDefault("look"), "classic", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>--look-set</c> text (test aid), or null.</summary>
    public static string? LookOverrides { get; } = Args.GetValueOrDefault("look-set");

    /// <summary>
    /// <c>--look &lt;name&gt;</c>: the named lighting look (<see cref="LookPresets"/>) in use, or null for none (<c>classic</c>, or <c>current</c>: the T7/M42 constants
    /// alone, the default before fx/impact-pass). Without <c>--look</c>: <see cref="LookPresets.Default"/> (the remaster; the owner's choice, 2026-10-29).
    /// </summary>
    public static string? LookPreset { get; } = PresetOf(Args.GetValueOrDefault("look"));

    /// <summary>The look a <c>--look</c> value names (null: no preset); no value: <see cref="LookPresets.Default"/>.</summary>
    public static string? PresetOf(string? look) => ImpactRules.LookChoice.PresetOf(look);   // engine-free, tested

    static AaMode ParseAa(string? text)
    {
        var mode = ParseAaText(text);
        // hotfix/old-gpu: safe mode turns anti-aliasing off unless --aa says otherwise; the Compatibility renderer has MSAA but neither SMAA nor TAA.
        if (SafeMode.Active && text is null) return AaMode.Off;
        if (SafeMode.Compatibility) return mode switch { AaMode.MsaaSmaa => AaMode.Msaa, AaMode.Taa => AaMode.Off, _ => mode };
        return mode;
    }

    static AaMode ParseAaText(string? text)
    {
        switch (text?.ToLowerInvariant())
        {
            case "off" or "none": return AaMode.Off;
            case "msaa": return AaMode.Msaa;
            case "taa": return AaMode.Taa;
            case null or "msaa+smaa" or "smaa": return AaMode.MsaaSmaa;
            default:
                GD.PrintErr($"--aa '{text}' is not known (off, msaa, msaa+smaa, taa); using msaa+smaa");
                return AaMode.MsaaSmaa;
        }
    }

    /// <summary>Applies the anti-aliasing mode to a viewport and returns a one-line description for the log.</summary>
    public static string Apply(Viewport viewport)
    {
        // musou/perf-pass: FSR 2 is a temporal anti-aliasing of its own; MSAA, SMAA or TAA on top would only cost (TAA is redundant, Godot's docs), so they stay off while it is on.
        bool fsr = Fsr2 && viewport is Window;
        bool msaa = !fsr && Aa is AaMode.Msaa or AaMode.MsaaSmaa;
        bool smaa = !fsr && Aa == AaMode.MsaaSmaa, taa = !fsr && Aa == AaMode.Taa;
        viewport.Msaa3D = msaa ? Viewport.Msaa.Msaa4X : Viewport.Msaa.Disabled;
        viewport.ScreenSpaceAA = smaa ? Viewport.ScreenSpaceAAEnum.Smaa : Viewport.ScreenSpaceAAEnum.Disabled;
        viewport.UseTaa = taa;
        ApplyScaling(viewport, RenderScale);
        // Specular anti-aliasing: Godot's screen-space roughness limiter raises the roughness where the normal map or the geometry
        // changes faster than a pixel (armour, painted and map materials alike).
        RenderingServer.ScreenSpaceRoughnessLimiterSetActive(Aa != AaMode.Off || fsr, 0.25f, 0.18f);
        return $"aa: {(fsr ? $"FSR 2 ({Customizer.Model.Upscaling.Name(Upscale)}, scale {viewport.Scaling3DScale:0.##}) in place of {Aa}" : Aa.ToString())} (MSAA {(msaa ? "4x" : "off")}, SMAA {(smaa ? "on" : "off")}, TAA {(taa ? "on" : "off")}, "
               + $"alpha-to-coverage {(AlphaToCoverage ? "on" : "off")}, roughness limiter {(Aa != AaMode.Off || fsr ? "on" : "off")})"
               + (Upscale != Customizer.Model.UpscaleChoice.Off && !Fsr2Supported ? "; FSR 2 chosen but this renderer has none (Forward+ only): plain scaling" : "");
    }

    /// <summary>Cut-out standard materials use alpha-to-coverage with MSAA (smooth edges on grates and fences).</summary>
    public static void ApplyCutOut(BaseMaterial3D material)
    {
        if (!AlphaToCoverage) return;
        material.AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
        material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
        material.RenderPriority = CutOutPriority;
    }

    /// <summary>Render-mode text a cut-out shader adds for alpha-to-coverage (empty when off).</summary>
    public static string CutOutRenderMode => AlphaToCoverage ? ", alpha_to_coverage, depth_draw_always" : "";

    /// <summary>
    /// fix/ground-lines-over-smoke (the owner, 2026-11-02; ours): an alpha-to-coverage cut-out is drawn among the transparent effects, so a floor overlay such as Manufactorum's strips
    /// could land on top of smoke, blast and blood clouds. Cut-outs go first in that pass and write depth, so every effect draws after them and is still hidden behind a fence.
    /// </summary>
    public const int CutOutPriority = -100;

    /// <summary>Extra fragment lines for alpha-to-coverage on a cut-out shader (the antialiasing edge scales with the texture's texels).</summary>
    public static string CutOutShaderLines(string texture) => AlphaToCoverage
        ? $"\n    ALPHA_ANTIALIASING_EDGE = 0.3;\n    ALPHA_TEXTURE_COORDINATE = UV * vec2(textureSize({texture}, 0));" : "";

    static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
            result[key] = value;
        }
        return result;
    }

    /// <summary>Parses "k=v,k=v" into floats (invalid entries are skipped).</summary>
    public static Dictionary<string, float> ParseOverrides(string? text)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && float.TryParse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v))
                result[part[..eq]] = v;
            else GD.PrintErr($"--look-set: '{part}' is not key=number; ignored");
        }
        return result;
    }
}
