using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet.Extras.Musou;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Musou weight and impact pass (owner's brief of 2026-11-10: "overall the game feels floaty, make it feel decisive, powerful, weighty, impactful"; plan approved that day,
/// docs/musou-weight-impact-plan.md). Musou Mode only, client presentation only; Orks are never tracked.
/// <para>MW1, ground contacts: each frame the marines' contacts with the ground (<see cref="MusouWeightRules"/>): the clips' foot tags (through the sound's tag pass,
/// <see cref="ExtrasFootfall"/>) and the bones of the clips without them (<see cref="MusouContactTracker"/>), the own marine and the seven nearest others within 45 m.
/// This frame's contacts are in <see cref="_musouContacts"/> for the puffs, sounds and camera of the later milestones.</para>
/// Switches: <c>--musou-old-weight</c> (the whole pass off), <c>--no-musou-contacts</c> (MW1 and everything built on it off). Test aid: <c>--log-musou-weight</c> (a MUSOU WEIGHT
/// line per contact, and the counts by kind at each <c>--tfw-weight-capture</c> mark).
/// </summary>
public partial class NetworkView
{
    static readonly bool MusouOldWeight = CmdFlag("musou-old-weight");
    static readonly bool NoMusouContacts = CmdFlag("no-musou-contacts");
    static readonly bool LogMusouWeight = CmdFlag("log-musou-weight");

    bool MusouWeightOn => MusouActive && !MusouOldWeight;
    bool MusouContactsOn => MusouWeightOn && !NoMusouContacts;

    readonly Dictionary<int, MusouContactTracker> _musouTrackers = new();
    /// <summary>This frame's ground contacts of the tracked marines (cleared each frame).</summary>
    readonly List<MusouGroundContact> _musouContacts = new();
    readonly List<(int Id, bool Own, string Tag, string? Clip)> _musouFootTags = new();
    readonly List<CharacterViewer.BlendClipState> _musouBlendScratch = new();
    readonly MusouBoneSample[] _musouBones = new MusouBoneSample[MusouWeightRules.Parts];
    readonly List<(float Distance, PlayerState State, CharacterViewer Viewer, bool Own)> _musouMarines = new();
    readonly Dictionary<string, int> _musouContactCounts = new(StringComparer.Ordinal);
    readonly HashSet<int> _musouTrackedNow = new();

    /// <summary>A foot tag the sound's tag pass admitted (one footfall, one post): kept for this frame's contacts.</summary>
    partial void ExtrasFootfall(string tag, PlayerState s, bool own, string? clip)
    {
        if (MusouContactsOn && MusouWeightRules.IsFootTag(tag)) _musouFootTags.Add((s.PlayerId, own, tag, clip));
    }

    /// <summary>MW1: this frame's ground contacts (after the sound, so the foot tags of this frame are in).</summary>
    void MusouWeightFrame(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        _musouContacts.Clear();
        MusouOwnThrownFrame(me);   // MW6: the own grenades always shake (Weight/NetworkView.MusouImpact.cs)
        if (!MusouContactsOn || _client is null || _camera is null) { _musouFootTags.Clear(); return; }
        var camera = _camera.GlobalPosition;
        _musouMarines.Clear();
        if (_local is not null && me.Alive && !IsOrkVoice(me)) _musouMarines.Add((0f, me, _local, true));
        foreach (var s in others)
        {
            if (!s.Alive || IsOrkVoice(s) || !_remotes.TryGetValue(s.PlayerId, out var c) || !c.IsInsideTree()) continue;
            float d = c.GlobalPosition.DistanceTo(camera);
            if (d <= MusouWeightRules.Range) _musouMarines.Add((d, s, c, false));
        }
        _musouMarines.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (_musouMarines.Count > MusouWeightRules.MaxMarines) _musouMarines.RemoveRange(MusouWeightRules.MaxMarines, _musouMarines.Count - MusouWeightRules.MaxMarines);
        double now = _connectedFor;
        _musouTrackedNow.Clear();
        foreach (var (_, s, viewer, own) in _musouMarines)
        {
            if (!viewer.MusouContactBones(_musouBones)) continue;
            _musouTrackedNow.Add(s.PlayerId);
            if (!_musouTrackers.TryGetValue(s.PlayerId, out var tracker)) _musouTrackers[s.PlayerId] = tracker = new MusouContactTracker();
            bool feetFromBones = !viewer.BlendState(_musouBlendScratch, out _) && !ClipHasFootTags(viewer.ClipTime?.Clip);
            // The sim's z is up; its velocity's z is the vertical speed.
            tracker.Step(s.PlayerId, own, _musouBones, s.Grounded, s.Velocity.Z, feetFromBones, now, _musouContacts);
            foreach (var (id, footOwn, tag, clip) in _musouFootTags)
            {
                if (id != s.PlayerId || MusouWeightRules.FromFootTag(tag, clip ?? viewer.ClipTime?.Clip) is not { } f) continue;
                var bone = _musouBones[(int)f.Part];
                _musouContacts.Add(new MusouGroundContact(id, footOwn, f.Part, f.Kind, bone.At with { Y = bone.At.Y - bone.Height }, f.Strength, s.Velocity.Length()));
            }
        }
        _musouFootTags.Clear();
        // A marine no longer tracked (gone, dead, out of range) starts again from its next frame.
        // Its roll state goes too: dying mid-roll gave a roll-end settle and camera dip at the respawn.
        foreach (var (id, tracker) in _musouTrackers) if (!_musouTrackedNow.Contains(id)) { tracker.Reset(); _musouWasRolling.Remove(id); }
        MusouPuffsFrame();   // MW2: the dust and grit (Weight/NetworkView.MusouPuffs.cs)
        MusouSettleFrame();   // MW3: the body's settle (Weight/NetworkView.MusouBody.cs)
        MusouTransitionsFrame();   // MW5: the cross-fade floors (Weight/NetworkView.MusouTransitions.cs)
        MusouContactSoundsFrame();   // MW6: the contacts' sounds (Weight/NetworkView.MusouImpact.cs)
        if (!LogMusouWeight) return;   // the counts and lines are the test aid's only (no per-frame strings without it)
        foreach (var c in _musouContacts)
        {
            string key = $"{c.Kind} {(c.Own ? "own" : "ally")}";
            _musouContactCounts[key] = _musouContactCounts.GetValueOrDefault(key) + 1;
            GD.Print($"MUSOU WEIGHT contact t {now:F3} frame {Engine.GetProcessFrames()} P{c.PlayerId} {(c.Own ? "own" : "ally")} {c.Part} {c.Kind} strength {c.Strength:F2} along {c.GroundSpeed:F1} m/s surface {MusouSurfaceAt(c.At)} clip {ClipOf(c.PlayerId) ?? "-"}");
        }
    }

    string? ClipOf(int id) => (_client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id))?.ClipTime?.Clip;

    /// <summary>Whether a clip carries foot tags (its steps come from them, not from the bones). Without the sound's tag table every clip counts as untagged.</summary>
    bool ClipHasFootTags(string? clip)
    {
        if (clip is null || _soundTriggers is null || !_soundTriggers.ClipTags.TryGetValue(clip, out var tags)) return false;
        foreach (var (tag, _) in tags) if (MusouWeightRules.IsFootTag(tag)) return true;
        return false;
    }

    /// <summary>The level's surface name under a point (Godot space): the sound's surface ray (<see cref="SurfaceUnder"/>), the default key when nothing is under it.</summary>
    string MusouSurfaceAt(Num.Vector3 godot)
    {
        if (_client?.Collision is not { } world) return MaterialEffectTable.DefaultKey;
        var game = new Num.Vector3(godot.X, -godot.Z, godot.Y);
        return world.Weapons.Raycast(game + new Num.Vector3(0, 0, 0.5f), new Num.Vector3(0, 0, -1), 2.5f) is { } hit ? Surfaces.NameOf(world.Weapons.MaterialOf(hit.Triangle)) : MaterialEffectTable.DefaultKey;
    }

    /// <summary>The counts by kind since the last call, for the capture's marks (<c>--log-musou-weight</c>); empties them.</summary>
    string MusouContactCountsText()
    {
        string text = _musouContactCounts.Count == 0 ? "none" : string.Join(", ", _musouContactCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
        text += $"; puffs {_musouPuffsPlayed} effects, {_musouPuffsSkipped} contacts without; contact sounds {_musouContactSounds}; blast jolts kept {_musouJoltsKept}, thinned {_musouJoltsThinned}";
        _musouContactCounts.Clear();
        _musouPuffsPlayed = _musouPuffsSkipped = _musouContactSounds = _musouJoltsKept = _musouJoltsThinned = 0;
        return text;
    }
}
