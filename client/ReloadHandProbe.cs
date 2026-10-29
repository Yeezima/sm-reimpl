using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// When the hand brings the new magazine back (M35). The reload clips' <c>Load_Clip</c> sound tags are sound designers' moments; in the
/// bolter and the stalker bolter the hand is still 0.5 to 0.9 m from the weapon then (bolter: tag 0.30, hand arrives about 0.35; stalker: tag 0.21, arrives about 0.28).
/// The magazine is drawn back in the weapon when the hand has arrived: this poses the reload clip on a scratch copy of the rig at 100 steps and follows the
/// distance from each hand bone to the weapon's magazine (placed from the weapon's own mount). The hand that travels furthest is the one that
/// changes the magazine (left for rifles, right for pistols); "arrived" is the first step after its furthest point where it is within
/// <see cref="ArriveWithin"/> m of its closest distance. **Interpretation** of the animation (evidence: the measured distances, spec/behavior.md M35).
/// The legs and <c>RIG_SpineA</c> of the rifle's mask come from the movement in the game; here every bone follows the clip alone (a small error in the chest's tilt).
/// </summary>
public static class ReloadHandProbe
{
    public const float ArriveWithin = 0.15f;
    const int Steps = 100;

    /// <param name="propBone">The rig bone the weapon hangs on (<c>RIG_Prop_M_Chest</c>, <c>RIG_Prop_LHand</c>).</param>
    /// <param name="magazineInBone">The magazine's centre in <paramref name="propBone"/>'s space.</param>
    /// <returns>The fraction of the clip at which the hand is back at the magazine, the hand's name, the closest and furthest distances (m); null when the clip or bones are missing.</returns>
    public static (float Arrive, float Leave, string Hand, float Closest, float Furthest)? Measure(Node host, MarineAssets assets, string clip, string propBone, Vector3 magazineInBone)
    {
        if (assets.Animation(clip) is not { } animation) return null;
        var rig = assets.Model.Rig.Skeleton;
        var skeleton = new Skeleton3D();
        host.AddChild(skeleton);   // bone poses are only kept up to date inside the tree
        try
        {
            for (int i = 0; i < rig.BoneNames.Count; i++)
            {
                skeleton.AddBone(rig.BoneNames[i]);
                if (rig.Parents[i] >= 0) skeleton.SetBoneParent(i, rig.Parents[i]);
                skeleton.SetBoneRest(i, MarineAssets.ToGodot(rig.ReferencePose[i]));
            }
            skeleton.ResetBonePoses();
            int prop = skeleton.FindBone(propBone);
            var hands = new[] { "RIG_L_Hand", "RIG_R_Hand" }.Select(skeleton.FindBone).Where(b => b >= 0).ToArray();
            if (prop < 0 || hands.Length == 0) return null;
            var distance = new float[hands.Length][];
            for (int h = 0; h < hands.Length; h++) distance[h] = new float[Steps + 1];
            for (int s = 0; s <= Steps; s++)
            {
                var local = animation.Animation.Sample(Math.Clamp(s / (float)Steps * animation.Animation.Duration, 0f, animation.Animation.Duration));
                for (int t = 0; t < local.Length; t++)
                {
                    int bone = animation.TrackToBone[t];
                    if ((uint)bone >= (uint)skeleton.GetBoneCount()) continue;
                    skeleton.SetBonePosePosition(bone, new Vector3(local[t].Translation.X, local[t].Translation.Y, local[t].Translation.Z));
                    skeleton.SetBonePoseRotation(bone, new Quaternion(local[t].Rotation.X, local[t].Rotation.Y, local[t].Rotation.Z, local[t].Rotation.W).Normalized());
                    skeleton.SetBonePoseScale(bone, new Vector3(local[t].Scale.X, local[t].Scale.Y, local[t].Scale.Z));
                }
                var magazine = skeleton.GetBoneGlobalPose(prop) * magazineInBone;
                for (int h = 0; h < hands.Length; h++) distance[h][s] = skeleton.GetBoneGlobalPose(hands[h]).Origin.DistanceTo(magazine);
            }
            int best = 0;
            float bestRange = -1f;
            for (int h = 0; h < hands.Length; h++)
            {
                float range = distance[h].Max() - distance[h].Min();
                if (range > bestRange) { bestRange = range; best = h; }
            }
            var d = distance[best];
            float closest = d.Min(), furthest = d.Max();
            float threshold = closest + ArriveWithin;
            int farthest = Array.IndexOf(d, furthest);
            int arrive = Enumerable.Range(farthest, Steps - farthest + 1).FirstOrDefault(s => d[s] <= threshold, -1);
            int leave = Enumerable.Range(0, farthest + 1).LastOrDefault(s => d[s] <= threshold, -1);
            if (arrive < 0 || leave < 0 || closest > 0.45f) return null;   // the hand never comes to the magazine (the pistol clip: the gun hand holds the weapon, the other hand stays at the side)
            return (arrive / (float)Steps, leave / (float)Steps, rig.BoneNames[hands[best]], closest, furthest);
        }
        finally { host.RemoveChild(skeleton); skeleton.Free(); }
    }
}

/// <summary>The measured hand timing per reload clip and weapon model, each measured once (<see cref="ReloadHandProbe.Measure"/>).</summary>
public sealed class ReloadHandTimes
{
    readonly Dictionary<string, (float Arrive, float Leave, string Hand, float Closest, float Furthest)?> _cache = new(StringComparer.OrdinalIgnoreCase);
    public int Measured => _cache.Count;

    /// <summary>The timing for the weapon in <paramref name="rig"/>'s hand and its reload clip; null without a magazine mount or when it cannot be measured.</summary>
    public (float Arrive, float Leave, string Hand, float Closest, float Furthest)? For(Node host, MarineAssets assets, WeaponRig rig, string clip)
    {
        if (!rig.TryMagazineMount(out string bone, out var centre, out string model)) return null;
        string key = clip + "|" + model;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ReloadHandProbe.Measure(host, assets, clip, bone, centre);
        _cache[key] = result;
        GD.Print($"reload hand timing measured in {watch.Elapsed.TotalMilliseconds:F1} ms (once per clip and weapon model, at the first reload)");
        if (result is { } r) GD.Print($"reload hand timing {key}: {r.Hand} leaves the magazine at {r.Leave:F2} of the clip and is back at {r.Arrive:F2} (closest {r.Closest:F2} m, furthest {r.Furthest:F2} m)");
        else GD.Print($"reload hand timing {key}: not measurable; the load tag is used");
        return result;
    }
}
