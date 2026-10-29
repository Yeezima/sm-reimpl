using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// Animation pass 3 (the owner, 2026-10-29: "when hopping and sprinting a devastator/havok needs to keep his weapon in his hands"): the heavy classes' hop and sprint show the clips of the
/// weapon in hand, as the base graph picks them. The simulation's heavy evade is read from the heavy bolter's set (<c>SM_EVD_TB_Evade_*</c>, M21), which every heavy class used for the pose
/// too; <c>SM_Base_MP</c>'s "PlasmaCannon Evade" (and the lascannon's graph) play <c>SM_EVD_LC_Evade_*</c> (0.967 s, the same length and branch times), whose arms hold the cannon. A
/// lascannon or plasma cannon in hand now shows the LC clip of the same direction (the body's motion is the simulation's, unchanged); the heavy bolter and the bolt pistol keep the TB clip
/// ("BoltPistol - Heavy Evade" plays the TB set too). The sprint's per-weapon clips: <c>NetworkView.Locomotion.cs</c> (<c>SprintFamilyOf</c>). <c>--no-p3</c>: the TB clip for every heavy weapon.
/// </summary>
public partial class NetworkView
{
    const string HeavyHopPrefix = "SM_EVD_TB_Evade_", CannonHopPrefix = "SM_EVD_LC_Evade_";
    readonly Dictionary<string, string?> _cannonHops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hop clip to pose for <paramref name="clip"/> (the simulation's evade clip): the cannon's own with a lascannon or plasma cannon in hand, else the clip itself.</summary>
    string HopClipFor(PlayerState s, string clip)
    {
        if (CharacterViewer.NoP3 || _config is null || !clip.StartsWith(HeavyHopPrefix, StringComparison.OrdinalIgnoreCase)) return clip;
        if (Loadouts.InHand(_config, s)?.Name is not { } weapon || LocomotionAnimations.SprintFamilyOf(weapon) != SprintFamily.Cannon) return clip;
        if (!_cannonHops.TryGetValue(clip, out var cannon))
        {
            string candidate = CannonHopPrefix + clip[HeavyHopPrefix.Length..];
            _cannonHops[clip] = cannon = RigHasClip(candidate) ? candidate : null;
        }
        return cannon ?? clip;
    }
}
