using System;
using Godot;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// The autocannon's recoil (owner, 2026-11-11): in place of the heavy bolter's additive clip, the gun and both arms move a little straight back along the barrel and return (<see cref="AutocannonKick"/>,
/// <see cref="CharacterViewer.SetAutocannonKick"/>). Pinned hook <c>ExtrasHipRecoil</c> of <c>NetworkView.HipFire.cs</c>. Test aid <c>--old-autocannon-recoil</c>: the heavy bolter's clip, as before.
/// </summary>
public partial class NetworkView
{
    static readonly bool OldAutocannonRecoil = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-autocannon-recoil") >= 0;

    partial void ExtrasHipRecoil(CharacterViewer character, PlayerState s, HipTrack t, ref bool handled)
    {
        if (OldAutocannonRecoil || !s.Alive || _config is null || Loadouts.InHand(_config, s) is not { } held || !held.Name.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) return;
        handled = true;
        float metres = AutocannonKick.Metres((float)(_connectedFor - t.RecoilAt));
        if (metres <= 0f || character.Weapons is not { } rig || !rig.TryMuzzle(out _, out var direction)) return;
        character.SetAutocannonKick(-direction.Normalized() * metres);
    }
}
