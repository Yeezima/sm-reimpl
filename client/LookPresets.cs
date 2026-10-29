using System;
using System.Collections.Generic;
using System.Linq;

namespace SmClient;

/// <summary>
/// Lighting mockups (<c>--look &lt;name&gt;</c>; the owner's request of 2026-10-29: "give me mockup previews of what the lighting system will make the game look
/// like before you commit to doing a lot of work on it"). Each look is a named set of the lighting constants that <c>--look-set</c> can override
/// (<see cref="MapLightingView.V"/>, <c>MapMaterials</c>): runtime parameters only (environment, sky, fog and volumetric fog, tone mapping, exposure,
/// bloom, grade, screen-space effects, the sun's angle, colour and energy, the placed lights' energy and range, the materials' roughness and specular).
/// <c>--look-set</c> still wins over a look's value. fx/impact-pass (the owner's choice, 2026-10-29: "make the remaster look the default"): the remaster
/// (<see cref="Default"/>) applies without <c>--look</c>; <c>--look current</c> (or <c>none</c>) gives the constants alone, the default look before it.
/// Every value here is **ours** (a mockup choice, picked by eye on matched views) unless its comment names the data; the map's own sun colour, light colours,
/// fog colour and ambient colours stay the data's in every look (scaled or saturated, never replaced).
/// The looks do not switch the costly features: volumetric smog and SSIL follow <c>--lighting</c> (on at the default medium, off at low, so
/// <c>--lighting low --look forge</c> is the cheap variant); only the remaster turns on SSR, and only at medium (on at high anyway; never at low or off:
/// <see cref="MapLightingView.SsrOn"/>, fx/impact-pass review). Captures: medium.
/// </summary>
public static class LookPresets
{
    /// <summary>The looks and a one-line description of each (for the log and the help text).</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sm1"] = "SM1 haze: the data's sun angle, warm orange-brown smog, strong sun shafts, dark interiors with warm practicals",
        ["remaster"] = "remaster: cleaner air, higher sun, higher contrast, glossier metal, screen-space reflections",
        ["moody"] = "moody: a low sun behind the cathedral, long shadows, cold shade, lamps that carry the interiors",
        ["forge"] = "forge (pick): the remaster's higher sun with the SM1 haze and shafts, readable shade and interiors",
    };

    static readonly Dictionary<string, Dictionary<string, float>> Sets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Faithful to SM1's warm industrial haze: the data's sun angle (30 degrees) and colour, pushed warmer; volumetric smog warmer than the default
        // (the data's fog colour at more of its saturation), shafts stronger; the depth haze and the sky more saturated toward the data's amber;
        // interiors dark, lit by the placed lamps (data colours: Sun_Bounce warm, Cool_Fill cool, Low_Fire orange) brighter and glowing in the smog.
        ["sm1"] = new()
        {
            ["vf.haze"] = 1.2f, ["vf.sat"] = 0.9f, ["vf.aniso"] = 0.65f, ["vf.sun"] = 1.8f, ["vf.lights"] = 0.9f, ["vf.ambient"] = 0.05f,
            ["fog.k"] = 1f, ["fog.sat"] = 1.25f, ["sun.mul"] = 1.1f, ["sun.cg"] = 0.95f, ["sun.cb"] = 0.85f,
            ["amb.energy"] = 0.95f, ["amb.sat"] = 0.4f, ["amb.bounce"] = 0.08f, ["amb.gamma"] = 1.9f,
            ["lights.mul"] = 1.6f, ["lights.range"] = 1.1f,
            ["tm"] = 4, ["tm.exp"] = 1.3f, ["adj.con"] = 1.18f, ["adj.sat"] = 1.12f,
            ["grade.toe"] = 0.045f, ["grade.cool"] = 0.015f, ["grade.warm"] = 0.045f,
            ["glow.int"] = 0.8f, ["sky.halo"] = 0.55f, ["sky.horizon"] = 1.35f, ["sky.sat"] = 1.6f, ["sky.top"] = 0.22f,
        },
        // Cleaner and higher contrast: a higher sun (38 degrees, the data's azimuth) so the yards are sunlit, thin haze, AgX with more contrast and
        // saturation, cooler shade, glossier metal and screen-space reflections.
        ["remaster"] = new()
        {
            ["vf.haze"] = 0.45f, ["vf.sat"] = 0.3f, ["vf.aniso"] = 0.5f, ["vf.sun"] = 1.2f,
            ["fog.k"] = 0.55f, ["sun.mul"] = 1.3f, ["sun.elev"] = 38f, ["amb.energy"] = 1.1f, ["amb.sat"] = 0.35f, ["amb.bounce"] = 0.07f, ["amb.gamma"] = 1.7f,
            ["lights.mul"] = 1.6f,
            ["tm"] = 4, ["tm.exp"] = 1.2f, ["adj.con"] = 1.22f, ["adj.sat"] = 1.15f,
            ["grade.toe"] = 0.025f, ["grade.cool"] = 0.04f, ["grade.warm"] = 0.025f,
            ["glow.int"] = 0.45f, ["mat.spec"] = 1.15f, ["mat.rough"] = 0.9f, ["sky.sat"] = 1.2f,
            ["ssr.on"] = 1,
        },
        // Darker and moodier: a low sun (18 degrees) turned 45 degrees to come from behind the cathedral (north), long shadows, cold shade, the
        // lamps carry the interiors, a cool grade.
        ["moody"] = new()
        {
            ["vf.haze"] = 1.3f, ["vf.sat"] = 0.6f, ["vf.aniso"] = 0.75f, ["vf.sun"] = 2.4f, ["vf.lights"] = 1.2f,
            ["fog.k"] = 1.1f, ["sun.mul"] = 1.1f, ["sun.elev"] = 18f, ["sun.yaw"] = 45f, ["sun.cg"] = 0.9f, ["sun.cb"] = 0.7f,
            ["amb.energy"] = 0.85f, ["amb.sat"] = 0.5f, ["amb.gamma"] = 1.75f,
            ["lights.mul"] = 2.2f, ["lights.range"] = 1.2f,
            ["tm"] = 4, ["tm.exp"] = 1.45f, ["adj.con"] = 1.15f, ["adj.sat"] = 0.95f,
            ["grade.toe"] = 0.035f, ["grade.cool"] = 0.08f, ["grade.warm"] = 0.05f,
            ["glow.int"] = 0.9f, ["sky.top"] = 0.15f, ["sky.horizon"] = 1.3f, ["sky.halo"] = 0.9f, ["sky.sat"] = 1.3f,
        },
        // The pick: the remaster's higher sun so the play space is sunlit, SM1's warm haze and shafts at a lighter density, readable shade and interiors.
        ["forge"] = new()
        {
            ["vf.haze"] = 0.9f, ["vf.sat"] = 0.8f, ["vf.aniso"] = 0.6f, ["vf.sun"] = 1.6f, ["vf.lights"] = 0.8f, ["vf.ambient"] = 0.08f,
            ["fog.k"] = 0.85f, ["fog.sat"] = 1.3f, ["sun.mul"] = 1.3f, ["sun.elev"] = 38f, ["sun.cb"] = 0.9f,
            ["amb.energy"] = 1.05f, ["amb.sat"] = 0.4f, ["amb.bounce"] = 0.08f, ["amb.gamma"] = 1.7f,
            ["lights.mul"] = 1.6f, ["lights.range"] = 1.15f,
            ["tm"] = 4, ["tm.exp"] = 1.2f, ["adj.con"] = 1.18f, ["adj.sat"] = 1.12f,
            ["grade.toe"] = 0.04f, ["grade.cool"] = 0.03f, ["grade.warm"] = 0.04f,
            ["glow.int"] = 0.65f, ["mat.spec"] = 1.1f, ["sky.halo"] = 0.5f, ["sky.sat"] = 1.4f, ["sky.horizon"] = 1.4f,
        },
    };

    public static IEnumerable<string> Names => Sets.Keys;

    /// <summary>fx/impact-pass: the look used without <c>--look</c> (the owner's choice of 2026-10-29).</summary>
    public const string Default = ImpactRules.LookChoice.Default;

    /// <summary>fx/impact-pass: <c>--look</c> values that name no preset: <c>current</c> (the default look before the remaster became it), <c>none</c>, and <c>classic</c> (the pre-T7 constants).</summary>
    public static bool IsNone(string name) => ImpactRules.LookChoice.IsNone(name);

    /// <summary>The constants of look <paramref name="name"/>; empty for null (no preset), the <see cref="Default"/> look's with an error for an unknown name.</summary>
    public static IReadOnlyDictionary<string, float> For(string? name)
    {
        if (name is null) return new Dictionary<string, float>();
        if (Sets.TryGetValue(name, out var set)) return set;
        Godot.GD.PrintErr($"--look '{name}' is not known ({string.Join(", ", Sets.Keys.Prepend("current").Prepend("classic"))}); the default look ({Default}) is used");
        return Sets[Default];
    }
}
