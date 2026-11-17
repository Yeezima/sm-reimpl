using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;

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
        var scratch = Build(s.Look);   // a skeleton of the rig to measure on; freed again
        try
        {
            if (s.FlinchBones is { } bones && s.Look.Clips[MusouLooks.Idle] is { } idle)
            {
                set = MusouOrkReactClips.Build(s.Look, scratch.Skeleton, bones, () => Pose(scratch, idle, 0f), s.Look.Scale);
            }
        }
        catch (Exception ex) { GD.PushWarning($"musou react clips {s.Look.Name}: {ex.Message}"); }
        finally { scratch.Root.QueueFree(); }
        _react[s.Look] = set;
        if (LogReact)
        {
            if (set is null) GD.Print($"MUSOU REACT {s.Look.Name}: no reaction clips (procedural flinch)");
            else
            {
                GD.Print($"MUSOU REACT {s.Look.Name}: {set.Count} clips, procedural head travel at size 1 {set.ReferenceLength:F3} (skeleton units)");
                for (int i = 0; i < set.Count; i++) { var (name, dir, len) = set.Describe(i); GD.Print($"MUSOU REACT   {name}: leans ({dir.X:F2}, {dir.Y:F2}), head travel {len:F3} at weight 1"); }
            }
        }
        return set;
    }
}
