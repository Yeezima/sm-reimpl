using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet.Extras.Musou;
using SmSim;

namespace SmClient;

/// <summary>
/// Musou weight pass MW6, camera and sound at contacts (owner 2026-11-10: "Thin the jolts").
/// <list type="bullet">
/// <item>Camera: a detonation shakes and jolts the own camera only within 12 m and at most once in 0.25 s (<see cref="MusouImpactRules.BlastFeels"/>; the baseline measured up to 16 jolts
/// a second from the bolters' blasts within 25 m), so the own impacts stand out; the own roll's end dips the camera 0.4 degrees like a small landing. Switch <c>--no-musou-impact-camera</c>.</item>
/// <item>Sound (install sounds only): a knee, hand, pelvis or shoulder meeting the ground plays the class's heavy footstep (the data's <c>EVT_Left_Foot_Rush</c> / <c>_Right_</c> command, on
/// the surface under it, at the owner's footstep level), so a roll and a fall are heard; a jump-pack dash's landing plays the pack's lightest landing entry (the data's dash lands
/// silently in our build). At most one a marine every 0.15 s, within 25 m, through the sound system's own limits. No gear rattle (2026-10-28 rule). Switch
/// <c>--no-musou-contact-sound</c>.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouImpactCamera = CmdFlag("no-musou-impact-camera"), NoMusouContactSound = CmdFlag("no-musou-contact-sound");
    double _musouLastJolt = double.NegativeInfinity;
    int _musouJoltsKept, _musouJoltsThinned, _musouContactSounds;
    readonly Dictionary<int, double> _musouContactSoundAt = new();

    bool MusouImpactCameraOn => MusouWeightOn && !NoMusouImpactCamera;

    partial void ExtrasBlastFeel(float distance, ref bool feel)
    {
        if (!MusouImpactCameraOn) return;
        feel = MusouImpactRules.BlastFeels(distance, _connectedFor, ref _musouLastJolt);
        if (feel) _musouJoltsKept++; else if (distance < 25f) _musouJoltsThinned++;
    }

    /// <summary>The own roll's end: a small camera dip (from the settle's roll-end detection).</summary>
    void MusouRollEndDip()
    {
        if (MusouImpactCameraOn) CameraKick(MusouImpactRules.RollEndDipDegrees, "musou roll end");
    }

    /// <summary>This frame's contacts' sounds (after the puffs).</summary>
    void MusouContactSoundsFrame()
    {
        if (!MusouWeightOn || NoMusouContactSound || NoSound || _sound is null || _soundTriggers is null || _camera is null || _config is null) return;
        var camera = _camera.GlobalPosition;
        foreach (var c in _musouContacts)
        {
            bool touch = c.Kind == MusouContactKind.Touch && !MusouWeightRules.IsFoot(c.Part) && c.Strength >= MusouImpactRules.TouchSoundStrength;
            bool dashLanding = c.Kind == MusouContactKind.Landing && c.Part == MusouContactPart.LeftFoot;
            if (!touch && !dashLanding) continue;
            if (camera.DistanceTo(new Vector3(c.At.X, c.At.Y, c.At.Z)) > MusouImpactRules.SoundRange) continue;
            if (_musouContactSoundAt.TryGetValue(c.PlayerId, out double at) && _connectedFor - at < MusouImpactRules.SoundEvery) continue;
            if (MusouTrackedState(c.PlayerId) is not { } state || !state.Alive) continue;
            var t = Track(c.PlayerId, c.Own);
            SurfaceUnder(t, state);
            bool played = false;
            if (touch)
            {
                string tag = c.Part is MusouContactPart.LeftKnee or MusouContactPart.LeftHand or MusouContactPart.LeftShoulder ? "EVT_Left_Foot_Rush" : "EVT_Right_Foot_Rush";
                foreach (var file in PlayerFiles(state))
                    foreach (var b in file.Tag(tag))
                        if (b.IsFootstep && PostBinding(b, t.Emitter, c.Own, "musou contact") is not null) played = true;
            }
            else if (Evade.Evading(state) && state.EvadeLanded && Loadouts.JumpPack(_config, state) is { } pack && _soundTriggers.File(pack.Name) is { } packFile)
            {
                foreach (var b in LandingSounds(packFile, MusouImpactRules.DashLandingDrop))
                    if (PostBinding(b, t.Emitter, c.Own, "musou dash landing") is not null) played = true;
            }
            if (!played) continue;
            _musouContactSoundAt[c.PlayerId] = _connectedFor;
            _musouContactSounds++;
            if (LogMusouWeight) GD.Print($"MUSOU WEIGHT sound t {_connectedFor:F3} P{c.PlayerId} {c.Part} {c.Kind} ({(touch ? "heavy footstep" : "dash landing")})");
        }
    }

    /// <summary>The state of a marine tracked this frame (<see cref="_musouMarines"/>), or null.</summary>
    PlayerState? MusouTrackedState(int id)
    {
        foreach (var (_, s, _, _) in _musouMarines) if (s.PlayerId == id) return s;
        return null;
    }
}
