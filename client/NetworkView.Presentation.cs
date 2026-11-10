using System;
using System.Collections.Generic;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// W0b (Musou's arsenal; lead 2026-11-10): the two hooks through which a weapon only added content carries borrows a data weapon's presentation. Every name of the install is its own, and both
/// hooks do nothing without added content, so a Classic or faithful match draws and sounds as before.
/// <list type="bullet">
/// <item><see cref="PresentationSources"/> (<see cref="ExtrasPresentationSources"/>): the install files a weapon takes its effects, sounds and name rules from. The effect and sound catalogues are
/// read with those files and copy their entries under the weapon's name (<see cref="PresentationSourceMap"/>; <see cref="EffectCatalogue.WithSources"/>, <see cref="SoundTriggers.WithSources"/>);
/// the name rules (the power flash, the hit synth, the sword's glow and flare, the idle hum) read <see cref="PresentedAs"/>.</item>
/// <item><see cref="MoveClipSpeed"/> (<see cref="ExtrasMoveClipSpeed"/>): how many times faster than their clips the charge, the dash attack, the ground pound's impact and get-up and the air
/// slash's strike and get-up are posed for the player's melee weapon (the claw's 1.25, the spear's 0.85); presentation only, the simulation's timings stay as they are.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The install files <paramref name="wargear"/> takes its presentation from, in order (the first that has any is used), or null: its own.</summary>
    IReadOnlyList<string>? PresentationSources(string wargear)
    {
        IReadOnlyList<string>? sources = null;
        ExtrasPresentationSources(wargear, ref sources);   // EXTRAS-HOOK added content (Musou's arsenal): the claw shows the power sword, the spear the hammer, the autocannon its install twin
        return sources is { Count: > 0 } ? sources : null;
    }

    /// <summary>The name the presentation's name rules read for <paramref name="wargear"/>: its first source, or the name itself (every name of the install).</summary>
    string? PresentedAs(string? wargear) => wargear is not null && PresentationSources(wargear) is { } sources ? sources[0] : wargear;

    /// <summary>
    /// The sources of every wargear file the match's config uses (<see cref="SoundTriggers.WargearNames"/>) that has any, for the catalogues' loads; empty without added content, so the effect
    /// and sound catalogues are read and kept exactly as before.
    /// </summary>
    Dictionary<string, IReadOnlyList<string>> PresentationSourceMap()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (_config is null) return map;
        foreach (var name in SoundTriggers.WargearNames(_config))
            if (PresentationSources(name) is { } sources) map[name] = sources;
        return map;
    }

    /// <summary>
    /// Added content: the install files whose presentation an added weapon shows (null without added content or for a name of its own); asked from the main thread and while the
    /// catalogues load.
    /// </summary>
    partial void ExtrasPresentationSources(string wargear, ref IReadOnlyList<string>? sources);

    /// <summary>
    /// How many times faster than their clips the moves of <paramref name="s"/>'s melee weapon that its strike map does not time are posed (the charge, the dash attack, the pound's impact
    /// and get-up, the air slash's strike and get-up): 1 for every weapon of the data. The strikes themselves are scaled by <see cref="MeleeClipSpeed"/> and the simulation's step durations.
    /// </summary>
    float MoveClipSpeed(PlayerState s)
    {
        if (_config is null || MeleeWargear(s) is not { } wargear) return 1f;
        float rate = 1f;
        ExtrasMoveClipSpeed(wargear, ref rate);   // EXTRAS-HOOK added content (Musou's arsenal): the claw's 1.25 and the spear's 0.85 reach the pack's moves and the charge
        return float.IsFinite(rate) && rate > 0f ? rate : 1f;
    }

    /// <summary>Added content: the clip rate of the charge's and the jump pack's moves for a melee weapon; leaves <paramref name="rate"/> at 1 without added content.</summary>
    partial void ExtrasMoveClipSpeed(string wargear, ref float rate);
}
