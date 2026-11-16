using System.Collections.Generic;
using Godot;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The crowd's part of the Musou guns (<see cref="MusouGunModels"/>, <c>NetworkView.MusouGuns.cs</c>): where a ranged Ork's muzzle is now, and his aiming stance while he fires. The simulation's
/// shooter stands still through a burst but its state stays "seek", which drew him running on the spot; for <see cref="MusouOrkGunRules.AimHoldSeconds"/> after a round he plays his look's idle,
/// the boy's two-handed aiming idle (ours).
/// </summary>
public partial class MusouCrowd
{
    readonly Dictionary<int, double> _firedAt = new();   // per id: when his last round was seen (s, the engine's clock)
    readonly List<int> _firedOld = new();

    static double GunNow => Time.GetTicksUsec() / 1e6;

    /// <summary>A round of this Ork's was seen.</summary>
    public void Fired(int id)
    {
        double now = GunNow;
        _firedAt[id] = now;
        if (_firedAt.Count <= 512) return;
        _firedOld.Clear();
        foreach (var (key, at) in _firedAt) if (now - at > MusouOrkGunRules.AimHoldSeconds) _firedOld.Add(key);
        foreach (int key in _firedOld) _firedAt.Remove(key);
    }

    /// <summary>The clip a member plays: <paramref name="clip"/>, or the aiming stance of an armed Ork who fired a moment ago and is not doing anything else.</summary>
    int GunClip(in MusouShown e, int clip) =>
        clip == MusouLooks.Run && e.State == MusouEnemyState.Seek && _firedAt.TryGetValue(e.Id, out double at) && GunNow - at < MusouOrkGunRules.AimHoldSeconds
        && _looks.Guns?.Of(_looks.For(e.Type)) is not null ? MusouLooks.Idle : clip;

    /// <summary>Where an armed Ork's muzzle is now (game space): on his skinned skeleton's gun bone when he is drawn skinned, else from his place and facing in the aiming stance. Null: no gun.</summary>
    public Vector3? MuzzleOf(in MusouShown e)
    {
        var look = _looks.For(e.Type);
        if (_looks.Guns?.Of(look) is not { } gun) return null;
        if (SkinnedOf(e.Id) is { } s && ReferenceEquals(s.Look, look) && gun.MuzzleBone >= 0)
            return (s.Root.Transform * s.Skeleton.GetBoneGlobalPose(gun.MuzzleBone) * gun.MuzzleOnBone).Origin;
        return new Vector3(e.Position.X, e.Position.Y, e.Position.Z) + new Basis(new Vector3(0, 0, 1), e.Yaw) * (gun.Muzzle * look!.Scale);
    }
}
