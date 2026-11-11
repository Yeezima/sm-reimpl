using System;
using Godot;

namespace SmClient;

/// <summary>
/// The autocannon's kick on the body (owner, 2026-11-11; <see cref="SmContent.Extras.Musou.Arsenal.AutocannonKick"/>): <c>RIG_SpineD</c>, the bone the arms and the chest prop (the gun's mount) all hang
/// from, is moved back along the barrel's axis, so the gun and both arms move as one and the hands stay on the gun. Set each frame by the networked view (<see cref="SetAutocannonKick"/>), read by
/// <see cref="AutocannonKickOverlay"/> (called from <c>ExtrasPoseOverlay</c>); a kick not renewed within 0.1 s lapses (a dead or switched character).
/// </summary>
public partial class CharacterViewer
{
    Vector3 _acKickBack;    // world space: the way the gun moves (from the muzzle to the stock), a length in metres
    double _acKickUntil;    // wall clock (s)
    int _acSpineD = -2;

    /// <summary>The kick this frame: <paramref name="back"/> is the offset in world space (the barrel's axis turned round, times the distance).</summary>
    public void SetAutocannonKick(Vector3 back) { _acKickBack = back; _acKickUntil = Time.GetTicksUsec() / 1e6 + 0.1; }

    void AutocannonKickOverlay()
    {
        if (_skeleton is null || _acKickBack == Vector3.Zero || Time.GetTicksUsec() / 1e6 > _acKickUntil) return;
        if (_acSpineD == -2) _acSpineD = _skeleton.FindBone("RIG_SpineD");
        if (_acSpineD < 0) return;
        var inSkeleton = _skeleton.GlobalTransform.Basis.Inverse() * _acKickBack;
        DropBone(_acSpineD, inSkeleton);   // the Musou weight pass's helper: a skeleton-space move turned into the bone's parent frame
        _layersDirty = true;               // applied again next frame, so the kick moves while a held clip shows
    }
}
