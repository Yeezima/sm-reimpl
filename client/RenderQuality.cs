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

    /// <summary>True when the cut-out materials should use alpha-to-coverage (needs MSAA).</summary>
    public static bool AlphaToCoverage => Aa is AaMode.Msaa or AaMode.MsaaSmaa;

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
        bool msaa = Aa is AaMode.Msaa or AaMode.MsaaSmaa;
        viewport.Msaa3D = msaa ? Viewport.Msaa.Msaa4X : Viewport.Msaa.Disabled;
        viewport.ScreenSpaceAA = Aa == AaMode.MsaaSmaa ? Viewport.ScreenSpaceAAEnum.Smaa : Viewport.ScreenSpaceAAEnum.Disabled;
        viewport.UseTaa = Aa == AaMode.Taa;
        // Specular anti-aliasing: Godot's screen-space roughness limiter raises the roughness where the normal map or the geometry
        // changes faster than a pixel (armour, painted and map materials alike).
        RenderingServer.ScreenSpaceRoughnessLimiterSetActive(Aa != AaMode.Off, 0.25f, 0.18f);
        return $"aa: {Aa} (MSAA {(msaa ? "4x" : "off")}, SMAA {(Aa == AaMode.MsaaSmaa ? "on" : "off")}, TAA {(Aa == AaMode.Taa ? "on" : "off")}, "
               + $"alpha-to-coverage {(AlphaToCoverage ? "on" : "off")}, roughness limiter {(Aa != AaMode.Off ? "on" : "off")})";
    }

    /// <summary>Cut-out standard materials use alpha-to-coverage with MSAA (smooth edges on grates and fences).</summary>
    public static void ApplyCutOut(BaseMaterial3D material)
    {
        if (AlphaToCoverage) material.AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
    }

    /// <summary>Render-mode text a cut-out shader adds for alpha-to-coverage (empty when off).</summary>
    public static string CutOutRenderMode => AlphaToCoverage ? ", alpha_to_coverage" : "";

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
