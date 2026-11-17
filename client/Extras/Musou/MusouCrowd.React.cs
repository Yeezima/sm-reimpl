using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Ork reaction clips (owner 2026-11-17, "Use reaction clips"; <see cref="MusouOrkReactRules"/>, <see cref="MusouOrkReactClips"/>): a skinned Ork's hit reaction is the Boy rig's own additive flinch clip,
/// chosen by the hit's direction and laid on the upper body at the flinch's size, in place of the procedural flinch of 2026-11-13. Facing, path, feet and attack phase are the Ork's clip's.
/// The baked far tier keeps its whole-body lean and jolt (the owner's near/far split of 2026-11-13). <c>--musou-procedural-flinch</c> restores the procedural flinch.
/// </summary>
public partial class MusouCrowd
{
    /// <summary>Test aid and switch (<c>--musou-procedural-flinch</c>): the procedural flinch of 2026-11-13 (torso, pelvis, knees, feet planted) instead of the rig's reaction clips.</summary>
    public static bool ProceduralFlinch { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-procedural-flinch") >= 0;

    readonly Dictionary<MusouLooks.Look, MusouOrkReactClips?> _react = new();
    public static readonly bool LogReact = Array.IndexOf(OS.GetCmdlineUserArgs(), "--log-musou-react") >= 0;

    protected override void PoseFlinch(Skinned s, in Item it, Vector3 kick)
    {
        if (!ProceduralFlinch && ReactOf(s) is { } set)
        {
            set.Apply(s.Skeleton, kick, NoOrkVariety ? 0 : it.Key, s.Look.Scale);
            return;
        }
        base.PoseFlinch(s, it, kick);
    }

    /// <summary>The look's measured reaction clips (built the first time one of its Orks flinches near; null: the rig has none, the procedural flinch stays).</summary>
    MusouOrkReactClips? ReactOf(Skinned s)
    {
        if (_react.TryGetValue(s.Look, out var known)) return known;
        MusouOrkReactClips? set = null;
        var scratch = BuildScratch(s.Look);   // a bone-only skeleton of the rig to measure on (no meshes; in the tree hidden for a moment)
        try
        {
            if (s.FlinchBones is { } bones && s.Look.Clips[MusouLooks.Idle] is { } idle)
            {
                set = MusouOrkReactClips.Build(s.Look, scratch.Skeleton, bones, () => Pose(scratch, idle, 0f), s.Look.Scale);
            }
        }
        catch (Exception ex) { GD.PushWarning($"musou react clips {s.Look.Name}: {ex.Message}"); }
        finally { scratch.Root.Free(); }
        _react[s.Look] = set;
        if (LogReact)
        {
            MusouOrkReactClips.Measure = true;
            if (set is null) GD.Print($"MUSOU REACT {s.Look.Name}: no reaction clips (procedural flinch)");
            else
            {
                GD.Print($"MUSOU REACT {s.Look.Name}: {set.Count} clips, procedural head travel at size 1 {set.ReferenceLength:F3} (skeleton units)");
                for (int i = 0; i < set.Count; i++) { var (name, dir, len) = set.Describe(i); GD.Print($"MUSOU REACT   {name}: leans ({dir.X:F2}, {dir.Y:F2}), head travel {len:F3} at weight 1"); }
            }
        }
        return set;
    }

    /// <summary>A skeleton of the look's rig with no meshes: what the clips are measured on.</summary>
    Skinned BuildScratch(MusouLooks.Look look)
    {
        var skeleton = look.Assets.Model.Rig.Skeleton;
        var sk = new Skeleton3D();
        var rest = new Transform3D[skeleton.BoneNames.Count];
        for (int i = 0; i < rest.Length; i++)
        {
            sk.AddBone(skeleton.BoneNames[i]);
            if (skeleton.Parents[i] >= 0) sk.SetBoneParent(i, skeleton.Parents[i]);
            rest[i] = MarineAssets.ToGodot(skeleton.ReferencePose[i]);
            sk.SetBoneRest(i, rest[i]);
        }
        sk.ResetBonePoses();
        var root = new Node3D { Visible = false };   // a Skeleton3D computes its global poses only inside the tree (seen: off it every measure read zero)
        root.AddChild(sk);
        AddChild(root);
        return new Skinned { Root = root, Skeleton = sk, Look = look, Rest = rest };
    }

    /// <summary>Pre-warm (owner 2026-11-17, the nearest 100 play the clips): once a Boy look is baked, hidden skinned members are built into the pool one a frame, up to half of the clip tier for each of the two common looks, and the look's reaction clips are measured with the first, so a promotion mid-fight builds nothing. <c>--musou-no-prewarm</c> leaves it off.</summary>
    void WarmPool()
    {
        if (NoPrewarm || ProceduralFlinch || NoOrkFlinch) return;
        int target = ClipCount() / 2;
        if (target <= 0) return;
        foreach (var type in new[] { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy })
        {
            if (_looks.For(type) is not { } look || look.Vat is null) continue;
            for (int k = 0; k < 1; k++)
            {
                var s = Prewarm(look, target);
                if (s is null) break;
                if (!_react.ContainsKey(look)) { s.FlinchBones = MusouOrkFlinchPose.Resolve(s.Skeleton); s.FlinchResolved = true; if (s.FlinchBones is not null) ReactOf(s); }
            }
        }
    }
    public static bool NoPrewarm { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-no-prewarm") >= 0;
}
