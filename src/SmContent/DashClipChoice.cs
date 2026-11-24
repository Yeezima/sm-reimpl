using System.Collections.Concurrent;

namespace SmContent;

/// <summary>The jump-pack dash's clip family: the plain set of <c>SM_Base_MP</c> or the thunder hammer's of <c>MP_SM_MEL_ThunderHammer</c> (<see cref="EvadeTuning.HammerGraph"/>).</summary>
public enum DashFamily { Plain, Hammer }

/// <summary>
/// Install transitions, pass 2 batch 2 (2026-11-25; engine-free, tested): the clips a jump-pack marine's air dash shows. <see cref="EvadeTuning"/> reads the dash from one graph (the
/// plain <c>SM_Base_MP</c> set, which the simulation keeps); a hammer or daemon maul in the marine's hands picks the hammer graph's own flight and landing clips, of the same lengths and
/// landing branch times, so the client poses them at the simulation's times with no stretch. The choice reads the melee wargear by its animation alias (<see cref="MeleeFallChoice.IsHammer"/>;
/// Musou's spear counts as the hammer, as for the falls). The switch is <c>--no-extra-transitions</c>. Which stance counts: <see cref="DashFamilyLatch"/>.
/// </summary>
public static class DashClipChoice
{
    static readonly ConcurrentDictionary<string, string?> HammerClips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The dash family of a wargear (already aliased): Hammer for the thunder hammer and the daemon maul; Plain otherwise, without a wargear, or whenever <paramref name="extraTransitionsOff"/>.</summary>
    public static DashFamily Family(string? aliasedWargear, bool extraTransitionsOff) =>
        !extraTransitionsOff && MeleeFallChoice.IsHammer(aliasedWargear) ? DashFamily.Hammer : DashFamily.Plain;

    /// <summary>
    /// The family's name for a plain dash clip the simulation plays: <c>SM_JMP_Dash_</c> + F/B/L/R → <c>SM_JMP_TH_Dash_</c> + the same letter; <c>SM_JMP_Dash_Landing_Shallow_</c> + B/L/R
    /// → <c>SM_JMP_TH_Dash_Landing_</c> + the same letter, and the forward landing (<c>_01</c> or <c>_F</c>) → <c>..._F</c>. Null for any other clip (a roll, a hop; a diagonal plays the
    /// forward or back clip and so maps through it). Cached per name (the pose asks every frame).
    /// </summary>
    public static string? HammerClip(string clip) => HammerClips.GetOrAdd(clip, MapHammerClip);

    static string? MapHammerClip(string clip)
    {
        if (clip.StartsWith(EvadeTuning.DashLandingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = clip[EvadeTuning.DashLandingPrefix.Length..];
            string? letter = suffix.ToUpperInvariant() switch { "01" or "F" => "F", "B" => "B", "L" => "L", "R" => "R", _ => null };
            return letter is null ? null : EvadeTuning.HammerDashLandingPrefix + letter;
        }
        if (clip.StartsWith(EvadeTuning.DashClipPrefix, StringComparison.OrdinalIgnoreCase) && clip.Length == EvadeTuning.DashClipPrefix.Length + 1
            && "FBLR".Contains(char.ToUpperInvariant(clip[^1])))
            return EvadeTuning.HammerDashClipPrefix + char.ToUpperInvariant(clip[^1]);
        return null;
    }

    /// <summary>
    /// The clip to pose for the simulation's dash clip <paramref name="clip"/>: the hammer's when the family is <see cref="DashFamily.Hammer"/> and <paramref name="rigHas"/> finds it;
    /// else <paramref name="clip"/> itself.
    /// </summary>
    public static string Clip(string clip, DashFamily family, Func<string, bool> rigHas) =>
        family == DashFamily.Hammer && HammerClip(clip) is { } hammer && rigHas(hammer) ? hammer : clip;
}

/// <summary>
/// Which family a player's dash keeps (engine-free, tested). An evading tick and the take-off tick before it both lower the weapon in the simulation, so the stance is read on the
/// player's last frame that was neither evading nor taking off (<see cref="Ground"/>: the hammer stance's aliased wargear, or null with the pistol up or another weapon), and latched when
/// the dash is first posed (<see cref="During"/>). It is never
/// re-read during the flight, at the landing (where the simulation restarts the evade's tick count) or across a chained dash; it clears when the evade ends (the next <see cref="Ground"/>)
/// and when the player leaves (<see cref="Forget"/>).
/// </summary>
public sealed class DashFamilyLatch
{
    readonly Dictionary<int, string?> _stance = new();
    readonly Dictionary<int, DashFamily> _latched = new();

    /// <summary>
    /// A frame on which the player is not evading: drop any latch and remember its stance (<paramref name="hammerStanceAlias"/>), except on a jump-pack take-off frame
    /// (<paramref name="takingOff"/>). The press tick of a ground tap starts the take-off and already lowers the weapon in the simulation (<c>jump.Started</c>; the dash itself
    /// begins a tick later), so that frame would read the hammer stance whatever was up; the stance from before the press is kept instead.
    /// </summary>
    public void Ground(int playerId, string? hammerStanceAlias, bool takingOff = false)
    {
        if (!takingOff) _stance[playerId] = hammerStanceAlias;
        _latched.Remove(playerId);
    }

    /// <summary>
    /// A frame inside a dash: the latched family, taken on the first such frame from the stance remembered before it (<paramref name="fallbackAlias"/> when none was seen, as for a
    /// player first seen mid-dash).
    /// </summary>
    public DashFamily During(int playerId, string? fallbackAlias, bool extraTransitionsOff, out bool latchedNow)
    {
        latchedNow = false;
        if (_latched.TryGetValue(playerId, out var family)) return family;
        family = DashClipChoice.Family(_stance.TryGetValue(playerId, out var stance) ? stance : fallbackAlias, extraTransitionsOff);
        _latched[playerId] = family;
        latchedNow = true;
        return family;
    }

    /// <summary>The player left: forget its stance and latch.</summary>
    public void Forget(int playerId)
    {
        _stance.Remove(playerId);
        _latched.Remove(playerId);
    }

    /// <summary>Whether a latch is held (tests).</summary>
    public bool IsLatched(int playerId) => _latched.ContainsKey(playerId);
}
