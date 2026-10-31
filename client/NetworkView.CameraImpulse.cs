using System;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// feel/weight-impact (the owner, 2026-11-01: floaty actions; client only, no simulation, wire or config-hash change): the weight pass. The numbers are <see cref="WeightFeel"/>.
/// <list type="bullet">
/// <item><b>Camera impulses</b> (<see cref="CameraImpulseNow"/>, read once a frame by <c>FxApplyShake</c>, on the shaken copy of the camera, so neither the aim nor <c>NetworkView.Camera.cs</c>
/// sees them): a dip on landings and the own ground pound, a jolt from explosions, a kick up per heavy-weapon shot, a tiny dip on a Devastator's or Havoc's footfall. The pitch only.</item>
/// <item><b>Landing settle</b> (<c>NetworkView.Fall.cs</c>): a fall below the hard clip's speed shows a share of the data's landing crouch.</item>
/// <item><b>Recoil</b> (<c>NetworkView.HipFire.cs</c>, <c>NetworkView.Brace.cs</c>) and <b>flinch</b> (<c>NetworkView.UpperBody.cs</c>): the data's additive clips laid stronger.</item>
/// <item><b>Strike and reaction entries</b> (<see cref="PoseSharp"/>): the entry fade's floor back to 0.125 s for them; the graphs' blends and every exit stay.</item>
/// </list>
/// No melee hit-stop and no melee hit shake: the owner removed them on 2026-10-29 (opt-in <c>--hit-stop</c> as before). <c>--old-feel</c> (test aid) restores the presentation before this
/// pass; one part each: <c>--old-camera-impulse</c>, <c>--old-landing-settle</c>, <c>--old-recoil-weight</c>, <c>--old-flinch-weight</c>, <c>--old-strike-entry</c>, <c>--old-heavy-footfall</c>.
/// </summary>
public partial class NetworkView
{
    static bool FeelArg(string flag) { var args = OS.GetCmdlineUserArgs(); return args.Contains("--old-feel") || args.Contains(flag); }

    static readonly bool OldCameraImpulse = FeelArg("--old-camera-impulse"), OldLandingSettle = FeelArg("--old-landing-settle"), OldRecoilWeight = FeelArg("--old-recoil-weight"),
        OldFlinchWeight = FeelArg("--old-flinch-weight"), OldStrikeEntry = FeelArg("--old-strike-entry"), OldHeavyFootfall = FeelArg("--old-heavy-footfall");

    /// <summary><c>--log-weight-feel</c> (test aid): prints each impulse and settle.</summary>
    static readonly bool LogWeightFeel = OS.GetCmdlineUserArgs().Contains("--log-weight-feel");

    readonly CameraImpulse _cameraImpulse = new();

    /// <summary>A kick of the own camera; positive dips the view, negative throws it up (as <see cref="Punch"/>).</summary>
    void CameraKick(float degrees, string why)
    {
        if (OldCameraImpulse || degrees == 0f) return;
        _cameraImpulse.Add(degrees);
        if (LogWeightFeel) GD.Print($"t {_connectedFor:F2} s: weight feel: camera {degrees:+0.00;-0.00} deg ({why})");
    }

    /// <summary>The impulses' pitch this frame (radians), added to the camera's punch by <c>FxApplyShake</c>.</summary>
    float CameraImpulseNow(double delta) => OldCameraImpulse ? 0f : _cameraImpulse.Step((float)delta) * MathF.PI / 180f;

    void HeavyWeaponKick(PlayerState me, WeaponSpec weapon, int stage) => CameraKick(-WeightFeel.CameraRecoilDegrees(weapon.Name, stage, me.Aiming), "shot");

    /// <summary>The body's recoil clip's weight for the weapon in hand.</summary>
    float RecoilWeight(PlayerState s) =>
        OldRecoilWeight || _config is null || Loadouts.InHand(_config, s) is not { } weapon ? 1f : WeightFeel.RecoilScale(weapon.Name);

    /// <summary>Whether a player is a Devastator or a Havoc.</summary>
    bool HeavyClass(PlayerState s) =>
        _config is not null && Loadouts.ClassOf(_config, s.Class)?.Name is { } name && (name.Contains("devastator", StringComparison.OrdinalIgnoreCase) || name.Contains("havoc", StringComparison.OrdinalIgnoreCase));

    /// <summary>A footfall of the own Devastator or Havoc on the move: the sound is the data's and is not touched.</summary>
    void HeavyFootfall(PlayerState s)
    {
        if (OldHeavyFootfall || !s.Grounded || !HeavyClass(s)) return;
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        if (speed < 1f) return;
        CameraKick(speed > 7f ? WeightFeel.FootfallSprintDegrees : WeightFeel.FootfallRunDegrees, "heavy footfall");
    }

    /// <summary>A strike's or a hit reaction's clip with the shorter entry floor; <paramref name="sharp"/> false keeps the floor (the stomp).</summary>
    bool PoseSharp(CharacterViewer character, string clip, float time, float blend, bool sharp = true)
    {
        character.EntryFloorOnce = OldStrikeEntry || !sharp ? 0f : WeightFeel.StrikeEntryFloor;
        bool posed = character.Pose(clip, time, blend);
        character.EntryFloorOnce = 0f;
        return posed;
    }
}
