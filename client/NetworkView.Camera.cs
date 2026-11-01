using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The original third-person camera in the networked view (protocol 12), converged shots and the aim button.
/// <para>
/// Camera (<see cref="RigCamera"/>): the weapon in hand names an exploration and an aim <see cref="CameraSet"/>
/// (<see cref="CameraTuning.For"/>); each names a curve of the original camera rig (<see cref="CameraRig"/>) that gives the
/// camera's and its collision pivot's place relative to the feet for the view's pitch. The camera is the yaw-rotated offset
/// from the feet, pulled in by sphere casts along head to shoulder, to pivot, to camera with a damped pull-in (M40-T6, camera/hop-and-walls: <see cref="CastClear"/>,
/// <see cref="DampFraction"/>; the rig's pivot is an interpretation: the inner bone), and looks along the view direction; exploration and aim are blended over the aim set's <c>blend_in_time</c>
/// (position and field of view; the aim blend follows <see cref="PlayerState.Aiming"/> of the predicted state), the view's
/// pitch is clamped to the sets' lower limit and to 89.9 degrees up (sim/roll-fire-pitch; above a set's upper limit the camera keeps that limit's place), and the mouse look is scaled by the sets' <c>speed_mouse</c> / 100 (aim 0.6, sniper
/// zoom 0.2). Field of view is taken as vertical (interpretation). Relax delay and speed, aim blend speed and distance and
/// aim assist are not implemented. Every weapon but the sniper rifle and lascannon uses the rifle's two camera sets (see <see cref="CameraFor"/>). If the rig or tuning fails to load, the placeholder camera stays (said in the info overlay).
/// </para>
/// <para>
/// Converged shots (<see cref="CenterRayTarget"/>): the ray through the screen centre, starting past the own character, is
/// tested against the map's shot collision and the other players' hit shapes (<see cref="Combat.HitShapes"/>, the
/// simulation's) for the red crosshair. The shot itself is sent along that ray, starting where it crosses the eye's plane
/// (<see cref="PlayerInput.WithShot"/>; the server limits the origin offset and falls back to the eye behind a wall), so it
/// goes where the crosshair is, close targets included. Bots send no offsets.
/// </para>
/// Placeholder demonstration around original camera data; Left Ctrl as the aim key is from the user's knowledge of the original.
/// </summary>
public partial class NetworkView
{
    const float DefaultFov = 65f;
    /// <summary>The exploration camera's distance behind the character as a multiple of the data rig's (ours, owner decision 2026-10-30); <c>--camera-back f</c> (1 = the data's) tests other values.</summary>
    static readonly float CameraBackScale = OS.GetCmdlineUserArgs() is { } a && Array.IndexOf(a, "--camera-back") is int i and >= 0 && i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0f ? f : 1.3f;
    /// <summary><c>--fov deg</c> (test aid, ui/fov-slider): the horizontal field of view to use instead of the saved choice (a direct <c>--connect</c> run has no front end to load it); 0 = the data's look.</summary>
    static readonly float? FovOverride = OS.GetCmdlineUserArgs() is { } fa && Array.IndexOf(fa, "--fov") is int fi and >= 0 && fi + 1 < fa.Length && float.TryParse(fa[fi + 1], System.Globalization.CultureInfo.InvariantCulture, out float fv) ? fv : null;
    const string FallbackWeapon = "sm_mp_ranged_bolter";

    /// <summary><c>--aim-test</c> (test aid): the bot uses the follow camera of a human player and holds aim mode every other 5 s (so screenshots show both cameras).</summary>
    public bool AimTest { get; init; }

    /// <summary><c>--bot-slot pistol|primary</c> (test aid): the bot presses that weapon slot key once, about 3 s after joining.</summary>
    public string? BotSlot { get; init; }

    /// <summary><c>--bot-stand</c> (test aid, with <c>--aim-test</c>): the bot holds aim mode and alternates standing, walking forward and strafing (8 s cycle).</summary>
    public bool BotStand { get; init; }

    /// <summary><c>--old-hop-camera</c> (test aid, camera/hop-and-walls): the evade sets' curves clamped at the sets' lower limit too, for before captures of the hop looking down.</summary>
    static readonly bool OldHopCamera = CmdFlag("old-hop-camera");

    CameraTuning? _cameraTuning;
    CameraRig? _cameraRig;
    string _cameraNote = "camera: placeholder (not loaded)";
    string _cameraInfo = "";
    readonly Dictionary<string, WeaponCamera?> _weaponCameras = new(StringComparer.OrdinalIgnoreCase);
    float _aimBlend;                       // 0 exploration .. 1 aim
    // Weapon switch (fix/camera-zoom-misc, owner item 24): the camera of the new weapon (pistol <-> primary: other rig curve and field of view) is eased in from the last placed one.
    string _switchKey = "";
    System.Numerics.Vector3 _switchCam, _switchPivot, _lastCam, _lastPivot;
    float _switchFov, _lastFov, _switchT = 1f, _switchTime = 0.25f;
    float _evadeBlend;                     // 0 .. 1 toward the evade roll's camera set
    string _evadeCameraNote = "";
    bool _evadeCurveNoted;
    CameraSet? _lastEvadeSet;
    bool _evadeCamLogged;
    /// <summary>The mouse look scale: the camera tuning's speed_mouse / 100 times the player's sensitivity option (ui/mouse-sensitivity; 1 = the original feel).</summary>
    float MouseLook => SmClient.Customizer.Model.MouseSensitivity.Look(_mouseScale) * SmClient.Customizer.Model.LiveOptions.MouseAim(_client?.Predicted.Aiming == true);
    float _mouseScale = 1f;                // speed_mouse / 100 of the current camera
    float _pitchMin = -MaxPitchDown, _pitchMax = MaxPitchUp;
    readonly CameraBoom _rigFraction = new();   // how much of the way from the pivot to the wanted eye the camera is (wall pull-in, smoothed outward)
    bool _rigLogged;
    // Last hit report (TakeHits), for the aimed enemy's health bar.
    int _lastVictim = -1;
    float _lastVictimHealth, _lastVictimShield;
    double _lastHitAt = double.NegativeInfinity;

    /// <summary>Loads the camera tuning and rig from the install; a failure keeps the placeholder camera and says so.</summary>
    void LoadCameraRig()
    {
        try
        {
            _cameraTuning = CameraTuning.Load(_gameDir);
            _cameraRig = CameraRig.Load(_gameDir);
            _cameraNote = $"camera: original rig ({_cameraRig.CurveNames.Count} curves) and camera sets ({_cameraTuning.Sets.Count}, {_cameraTuning.Weapons.Count} weapons) from the install; blending, pitch range and mouse speed applied, relax and aim assist not implemented; every weapon but the sniper rifle and lascannon uses the rifle's two camera sets (user's knowledge of the original, not the wargear data)";
            foreach (var n in _cameraTuning.Notes.Take(5)) GD.Print("  camera note: " + n);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException))
        {
            _cameraTuning = null; _cameraRig = null;
            _cameraNote = "camera: placeholder (the original rig failed to load: " + ex.Message + ")";
            GD.PrintErr("camera rig: " + ex.Message);
        }
        GD.Print("  " + _cameraNote);
    }

    /// <summary>
    /// The camera data of the weapon in hand (a missing weapon or unknown name: the bolter's). Every weapon except the sniper
    /// rifle and the lascannon uses the rifle's camera sets (exploration <c>exploration_default_mp</c>, aim <c>aim_sm_rifle_mp</c>:
    /// field of view 40 / 20, the rifle rig curves), so unzoomed and aimed views are the same for the bolter, the pistols, the
    /// heavy weapons and the rest. The wargear data gives the pistols (<c>exploration_pistol</c>, <c>aim_sm_pistol_mp</c>, fov 25),
    /// the heavy bolter, plasma cannon, grenade launcher and melta their own sets; this is **the user's knowledge of the original**
    /// (a playtest finding), not the data read. The sniper rifle (<c>*_sniper_rifle</c>, "stalker bolter") and lascannon
    /// (<c>*_las_cannon</c>) keep their own sets (zoom fov 15). The look-speed tuning stays per weapon: it is the same for the
    /// bolter and the pistols (aim 60), and differs only for the heavy weapons and the storm bolter.
    /// </summary>
    WeaponCamera? CameraFor(PlayerState me)
    {
        string name = Loadouts.InHand(_config!, me)?.Name ?? FallbackWeapon;
        if (name == "default") name = FallbackWeapon;
        if (!_weaponCameras.TryGetValue(name, out var wc))
        {
            wc = _cameraTuning!.For(name);
            bool own = name.EndsWith("_sniper_rifle", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_las_cannon", StringComparison.OrdinalIgnoreCase)
                       || name.Contains("lascannon", StringComparison.OrdinalIgnoreCase);
            if (wc is not null && !own && _cameraTuning.For(FallbackWeapon) is { } rifle)
                wc = wc with { Exploration = rifle.Exploration, Aim = rifle.Aim, ZoomFovs = rifle.ZoomFovs,
                               Evidence = $"{rifle.Exploration.Name} / {rifle.Aim.Name} (the rifle's sets for every weapon but the sniper rifle and lascannon: user's knowledge of the original; wargear {name} names {wc.Exploration.Name} / {wc.Aim.Name}); input {wc.ExplorationInput.Name} / {wc.AimInput.Name} from the wargear" };
            _weaponCameras[name] = wc;
        }
        return wc;
    }

    /// <summary>Whether the follow camera is the rig's: human views (and the bot's with <c>--aim-test</c>), not the spectator cameras.</summary>
    bool RigCameraWanted => Spectate is null && (!_bot || AimTest || SlashTest && float.IsFinite(SlashViewPitch) || StompMoveTest || SwingTest || CarryTest || CameraTest) && _cameraRig is not null && _cameraTuning is not null;   // anim/air-slash-character: --slash-view-pitch gives the slash test the human camera; anim/stomp-to-move: --stomp-move-test too; anim/relic-bolter-idle: --carry-test too

    static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;

    /// <summary>
    /// Places the camera by the original rig (see the class comment); false when this frame's curves are missing (the caller
    /// then uses the placeholder camera). Also updates the pitch range and mouse scale.
    /// </summary>
    bool RigCamera(PlayerState me, float delta)
    {
        _cameraPlacedValid = false;   // feel/controls: set again by NoteCameraPlaced when the rig places the camera this frame
        if (_camera is null || _gameSpace is null || _config is null || _cameraRig is null || CameraFor(me) is not { } wc) return false;
        var ex = wc.Exploration; var aim = wc.Aim;
        // Blend between the sets; the weight follows the predicted aim mode (so it matches the simulation).
        float target = me.Alive && me.Aiming ? 1f : 0f;
        float time = Finite(target > _aimBlend ? aim.BlendInTime : ex.BlendInTime, 0.2f);
        _aimBlend = Mathf.MoveToward(_aimBlend, target, time > 0.01f ? delta / time : 1f);
        float w = _aimBlend * _aimBlend * (3f - 2f * _aimBlend);

        // Pitch range of the blended sets (file degrees -> simulation radians), and the mouse speed. sim/roll-fire-pitch (the owner 2026-11-01: "all classes
        // should be able to zoom directly upwards into the sky"): the sets' upper limits (exploration 70, rifle aim 85, heavy 55 degrees) no longer bound the
        // view, which goes up to MaxPitchUp (89.9 degrees) in hip fire and aim mode alike; above a set's limit the camera keeps the rig's place for that
        // limit (lookupPitch) and only turns. The lower limit is the sets' as before.
        _pitchMin = MathF.Max(-MaxPitchDown, Mathf.Lerp(CameraRig.SimRadians(ex.PitchMinimum), CameraRig.SimRadians(aim.PitchMinimum), w));
        float setsMax = MathF.Min(MaxPitchDown, Mathf.Lerp(CameraRig.SimRadians(ex.PitchMaximum), CameraRig.SimRadians(aim.PitchMaximum), w));
        _pitchMax = MaxPitchUp;
        _mouseScale = Mathf.Lerp(Finite(wc.ExplorationInput.SpeedMouse, 100f), Finite(wc.AimInput.SpeedMouse, 100f), w) / 100f;
        _pitch = Math.Clamp(_pitch, _pitchMin, _pitchMax);
        float pitch = Math.Clamp(_pitch, _pitchMin, _pitchMax);
        float lookupPitch = MathF.Min(pitch, MathF.Max(setsMax, _pitchMin));   // the rig curves' place: as before up to the sets' old limit

        if (_cameraRig.OffsetAtPitch(ex.AnimationCurve, lookupPitch) is not { } o0 || _cameraRig.OffsetAtPitch(aim.AnimationCurve, lookupPitch) is not { } o1) return false;
        if (!_rigLogged)
        {
            _rigLogged = true;
            // Diagnostic: does the rig's outer-bone elevation (LookPitch) equal the view pitch?
            string looks = string.Join(", ", new[] { -0.6f, 0f, 0.6f }.Select(p => _cameraRig.OffsetAtPitch(ex.AnimationCurve, p) is { } o ? $"pitch {p:F2} -> look {o.LookPitch:F2}" : "none"));
            GD.Print($"camera back: data {o0.Back:F2} m exploration / {o1.Back:F2} m aim at pitch {pitch:F2}; exploration used x{CameraBackScale:F2} = {o0.Back * CameraBackScale:F2} m");
            GD.Print($"camera rig: {ex.Name} / {aim.Name} for {wc.Weapon}; {wc.Evidence}; exploration LookPitch vs view pitch: {looks}");
        }
        // ui/hud-camera-2 (owner, 2026-10-30: "too close to the character"): the exploration camera sits CameraBackScale times as far behind the feet as the data's rig puts it (ours; the aim set is unchanged).
        var explore = o0.Camera; explore.Y *= CameraBackScale;
        var cam = System.Numerics.Vector3.Lerp(explore, o1.Camera, w);
        var pivot = System.Numerics.Vector3.Lerp(o0.Pivot, o1.Pivot, w);
        // The scoped weapons (stalker bolter, lascannon): the wargear's zoom_levels fov_1 is the zoomed view (spec "Zoom levels"), not the aim set's field_of_view; the rest of the weapons use the set's.
        float aimFov = wc.ZoomFovs is { Count: > 0 } zooms ? zooms[_padZoom % zooms.Count] : aim.FieldOfView;   // R3 on the pad steps through the levels (NetworkView.Gamepad.cs)
        // ui/fov-slider (ours): the player's wider field of view scales the exploration and aim cameras; a scope's zoom level stays exactly the data's.
        var viewSize = GetViewport().GetVisibleRect().Size;
        float fovScale = FovMath.Scale(FovOverride ?? FovMath.Selected, FovMath.Aspect(viewSize.X, viewSize.Y));
        bool scoped = wc.ZoomFovs is { Count: > 0 };
        float fov = Mathf.Lerp(FovMath.Apply(Finite(ex.FieldOfView, DefaultFov), fovScale), scoped ? Finite(aimFov, DefaultFov) : FovMath.Apply(Finite(aimFov, DefaultFov), fovScale), w);

        // Evade roll (M21): blend to the roll's camera set (the ability's camera_curve, e.g. evade_default_mp) over its blend-in
        // time (back out over the exploration set's). Position from the set's rig curve (pitch clamped to the set's range for the
        // lookup; the view's pitch itself is not limited); a set whose curve the rig lacks changes only the field of view.
        var evadeSet = me.Alive && Evade.Evading(me) && Loadouts.Evade(_config, me) is { Camera.Length: > 0 } evadeSpec
            && _cameraTuning!.Sets.TryGetValue(evadeSpec.Camera, out var found) ? found : null;
        float evadeTarget = evadeSet is null ? 0f : 1f;
        float evadeTime = Finite(evadeSet is not null ? evadeSet.BlendInTime : ex.BlendInTime, 0.2f);
        _evadeBlend = Mathf.MoveToward(_evadeBlend, evadeTarget, evadeTime > 0.01f ? delta / evadeTime : 1f);
        if (_evadeBlend > 0f)
        {
            var set = evadeSet ?? _lastEvadeSet;
            if (set is not null)
            {
                _lastEvadeSet = set;
                float we = _evadeBlend * _evadeBlend * (3f - 2f * _evadeBlend);
                float evadeLookup = OldHopCamera ? Math.Clamp(pitch, CameraRig.SimRadians(set.PitchMinimum), CameraRig.SimRadians(set.PitchMaximum)) : CameraRig.SetPlacePitch(pitch, set);   // camera/hop-and-walls: the curve follows the view down
                if (_cameraRig.OffsetAtPitch(set.AnimationCurve, evadeLookup) is { } oe)
                {
                    cam = System.Numerics.Vector3.Lerp(cam, oe.Camera, we);
                    pivot = System.Numerics.Vector3.Lerp(pivot, oe.Pivot, we);
                    _evadeCameraNote = $"{set.Name} blend {we:F2} (rig curve {set.AnimationCurve})";
                }
                else
                {
                    _evadeCameraNote = $"{set.Name} blend {we:F2}: curve '{set.AnimationCurve}' not in the camera rig, exploration offset kept, field of view only";
                    if (!_evadeCurveNoted) { _evadeCurveNoted = true; _notes.Add(_evadeCameraNote); GD.Print("  camera note: " + _evadeCameraNote); }
                }
                fov = Mathf.Lerp(fov, float.IsFinite(set.FieldOfView) ? FovMath.Apply(set.FieldOfView, fovScale) : fov, we);
                if (_bot && !_evadeCamLogged && we > 0.5f)
                {
                    _evadeCamLogged = true;
                    GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} evade camera: {_evadeCameraNote}; fov {fov:F0}; camera {cam.X:F2},{-cam.Y:F2} back,{cam.Z:F2} up (blend-in {set.BlendInTime:F2} s)");
                }
            }
        }
        else { _lastEvadeSet = null; _evadeCamLogged = false; }

        // Weapon switch: when the weapon's camera sets change (bolt pistol <-> primary), the rig offsets and the field of view blend from the last placed camera to the new one over the new
        // exploration set's blend_in_time (data: 0.25 s for exploration_default_mp and exploration_pistol), smoothstep, instead of jumping.
        string switchKey = $"{ex.Name}/{aim.Name}/{aimFov}";
        if (_switchKey.Length > 0 && switchKey != _switchKey && me.Alive && _cameraPlaced)
        {
            _switchCam = _lastCam; _switchPivot = _lastPivot; _switchFov = _lastFov; _switchT = 0f;
            _switchTime = Finite(ex.BlendInTime, 0.25f);
        }
        _switchKey = switchKey;
        if (_switchT < 1f)
        {
            _switchT = _switchTime > 0.01f ? MathF.Min(1f, _switchT + delta / _switchTime) : 1f;
            float ws = _switchT * _switchT * (3f - 2f * _switchT);
            cam = System.Numerics.Vector3.Lerp(_switchCam, cam, ws);
            pivot = System.Numerics.Vector3.Lerp(_switchPivot, pivot, ws);
            fov = Mathf.Lerp(_switchFov, fov, ws);
        }
        _lastCam = cam; _lastPivot = pivot; _lastFov = fov;   // what was placed (a switch during a blend continues from here)

        // camera/right-shoulder-fov (ours, owner 2026-11-02): a wider field of view draws the character nearer the crosshair, so the camera and its pivot sit further right (FovMath.CameraShift).
        // The view stays parallel, so the crosshair's ray moves with the camera and the converged shot (Converged) with it. A scope's zoom is not widened: no shift there.
        // The rig's place changes with the pitch and the evade curves, so the shift gives way where the ray would pass the server's shot origin limit.
        float fovShift = FovMath.LimitShift(FovMath.CameraShift(fovScale) * (scoped ? 1f - w : 1f), cam.X, cam.Y * MathF.Sin(pitch) - (cam.Z - _config.EyeHeight) * MathF.Cos(pitch), _config.MaxShotOriginOffset - FovMath.OriginMargin);
        cam.X += fovShift; pivot.X += fovShift;

        // Character frame (X right, Y forward, Z up) rotated by the view's yaw about the feet, into Godot space.
        float sin = MathF.Sin(_yaw), cos = MathF.Cos(_yaw);
        Vector3 ToWorld(System.Numerics.Vector3 v)
        {
            var g = me.Position + new System.Numerics.Vector3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);
            return _gameSpace.Transform * new Vector3(g.X, g.Y, g.Z);
        }
        var wanted = ToWorld(cam);
        CamProbeNote("rig", wanted);
        var pivotWorld = ToWorld(pivot);
        // Walls (M40-T6): a sphere cast along the rig's two legs, head to pivot (a wall beside the player would put the pivot
        // inside it) and from that pivot to the camera. Each leg's place is a fraction that is pulled in fast but continuously
        // and grows back out slowly, clamped by a hard cast of the near plane's size (NetworkView.Spectate.cs).
        // The wall casts start from the simulation's position (M36: me is the drawn one, up to a tick behind).
        // feel/controls: from the drawn head (the camera is placed from the drawn position, which after an early tick can be two ticks behind
        // the simulation's: 0.67 m sprinting), unless the way from the simulation's head to it is blocked (a drawn position across a corner).
        long castsFrom = CameraTest ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var simHead = _gameSpace.Transform * new Vector3(_simPosition.X, _simPosition.Y, _simPosition.Z + _config.EyeHeight);
        var drawnHead = _gameSpace.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z + _config.EyeHeight);
        var head = NoDrawnCameraCast || CastClear(simHead, drawnHead, CameraSoftRadius) < simHead.DistanceTo(drawnHead) - 1e-3f ? simHead : drawnHead;
        if (!_cameraPlaced) CamProbeCut();
        // Lookahead: the same two legs cast for where the player will be in 0.12 s and 0.25 s (its velocity, stopped by walls) and
        // looking where the view is turning, so a wall corner that is about to cross the camera's line starts the pull-in early
        // instead of meeting the hard cast all at once.
        // camera/hop-and-walls: a shoulder leg first. The rig's pivot is beside, above and behind the head; a single leg from the head to it shrank all three together, so a wall behind
        // the character took the camera into the head and across the view (0.07 m from it, the world sliding sideways and down). Now the side and height come first (head to shoulder) and
        // only the way back meets the wall behind. The shoulder is the pivot's place without its distance along the facing.
        // Review pass: the height comes before the side (a pillar at the right shoulder took the height too). camera/right-shoulder-fov (owner 2026-11-02): the camera stays on the right shoulder, a blocked side only pulls it in.
        var shoulderRig = new System.Numerics.Vector3(pivot.X, 0f, pivot.Z);
        var legStart = head;
        if (!OldCameraCollision)
        {
            if (!_cameraPlaced) _rigFocus = 0f;   // a respawn or first frame: nothing carries over
            var heightRig = new System.Numerics.Vector3(0f, 0f, pivot.Z);
            var raised = DampFraction(_rigFractionU, _cameraPlaced, head, head + (ToWorld(heightRig) - drawnHead), delta);
            var sideRight = ToWorld(shoulderRig) - ToWorld(heightRig);
            // No lookahead on these short legs: a head foreseen against a wall starts inside the soft radius, and its leg read 0 for the frames of every wall contact.
            legStart = DampFraction(_rigFractionS, _cameraPlaced, raised, raised + sideRight, delta);
        }
        int ahead = 0;
        float yawStep = _rigYawInit ? Mathf.Wrap(_yaw - _rigYaw, -MathF.PI, MathF.PI) : 0f;
        _rigYaw = _yaw; _rigYawInit = true;
        _rigYawRate = Mathf.Lerp(_rigYawRate, delta > 1e-4f ? Math.Clamp(yawStep / delta, -4f, 4f) : 0f, 1f - MathF.Exp(-15f * delta));
        if (_client?.Collision is not null && me.Alive)
        {
            var velocity = new Vector3(me.Velocity.X, me.Velocity.Y, me.Velocity.Z);
            foreach (float t in RigLookAheadSeconds)
            {
                var step = _gameSpace.Basis * (velocity * t);
                float stepLength = step.Length();
                if (stepLength > 0.05f) step *= CastClear(head, head + step, CameraSoftRadius) / stepLength;
                float turned = _yaw + _rigYawRate * t;
                float s2 = MathF.Sin(turned), c2 = MathF.Cos(turned);
                Vector3 AtAhead(System.Numerics.Vector3 v)
                {
                    var g = me.Position + new System.Numerics.Vector3(v.X * c2 - v.Y * s2, v.X * s2 + v.Y * c2, v.Z);
                    return _gameSpace.Transform * new Vector3(g.X, g.Y, g.Z) + step;
                }
                _aheadHead[ahead] = head + step; _aheadShoulder[ahead] = AtAhead(shoulderRig); _aheadPivot[ahead] = AtAhead(pivot); _aheadEye[ahead] = AtAhead(cam);
                ahead++;
            }
        }
        if (!OldCameraCollision)
            for (int i = 0; i < ahead; i++) _aheadHead[i] += (_aheadShoulder[i] - _aheadHead[i]) * _rigFractionS.Fraction;
        _aheadFrom.Clear(); _aheadTo.Clear();
        for (int i = 0; i < ahead; i++) { _aheadFrom.Add(_aheadHead[i]); _aheadTo.Add(_aheadPivot[i]); }
        pivotWorld = DampFraction(_rigFractionA, _cameraPlaced, legStart, pivotWorld, delta, _aheadFrom, _aheadTo);
        _aheadFrom.Clear(); _aheadTo.Clear();
        for (int i = 0; i < ahead; i++)
        {
            _aheadFrom.Add(_aheadHead[i] + (_aheadPivot[i] - _aheadHead[i]) * _rigFractionA.Fraction);
            _aheadTo.Add(_aheadEye[i]);
        }
        var damped = DampFraction(_rigFraction, _cameraPlaced, pivotWorld, wanted, delta, _aheadFrom, _aheadTo);
        var position = Depenetrate(ClearOfBodies(damped, me, includeOwn: false, pivotWorld, delta));   // an enemy in melee range is not in the lens
        _cameraPlaced = true;

        var aimGame = Combat.AimDirection(_yaw, pitch);
        var look = (_gameSpace.Basis * new Vector3(aimGame.X, aimGame.Y, aimGame.Z)).Normalized();
        // camera/hop-and-walls: the crosshair stays on the world point the uncollided rig camera looks at while the boom is short (CameraBoom.StableLook, NetworkView.CameraAim.cs).
        var focusPoint = OldCameraCollision && !CameraTest ? position : RigFocusPoint(wanted, look, head, delta);   // the extra cast only where it is used
        if (!OldCameraCollision) look = StableLook(wanted, position, look);
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt(look, Vector3.Up), position);
        if (CameraTest) CameraTestCost(System.Diagnostics.Stopwatch.GetTimestamp() - castsFrom);
        CameraTestNote(me, position, wanted, look, (focusPoint - position).Cross(look).Length());   // --camera-test (NetworkView.CameraTest.cs)
        NoteCameraPlaced(_yaw, pitch, me.Position);   // feel/controls: the view the converged shot's ray is turned from (FreshCenterRay)
        _camera.Fov = fov;
        var feet = _gameSpace.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        _cameraOffset = position - feet;
        // Too close to the head (the sniper zoom, a wall behind): hide the whole own character, pack effects included (NetworkView.CameraHide.cs).
        if (_local is not null) UpdateOwnHidden(position.DistanceTo(feet + Vector3.Up * _config.EyeHeight));
        _cameraInfo = (_evadeBlend > 0f ? $"evade {_evadeCameraNote}; " : "") + $"{ex.Name} / {aim.Name} blend {w:F2} fov {fov:F0} mouse x{_mouseScale:F2} pitch {_pitchMin:F2}..{_pitchMax:F2} camera {cam.X:F2},{-cam.Y:F2} back,{cam.Z:F2} up";
        return true;
    }

    /// <summary>
    /// Where the ray through the screen centre ends (game space) and the player it hits first, if any: the ray starts
    /// past the own character (at the head's plane across the ray), stops at the first shot-collision surface and at the
    /// first hit shape (<see cref="Combat.HitShapes"/>) of another living player within the weapon's range, and without a hit
    /// ends at that range.
    /// </summary>
    (System.Numerics.Vector3 Point, PlayerState? Hit, System.Numerics.Vector3 Start, System.Numerics.Vector3 Direction) CenterRayTarget(PlayerState me, SimConfig c,
        (System.Numerics.Vector3 Origin, System.Numerics.Vector3 Direction)? ray = null)
    {
        var inverse = _gameSpace!.Transform.AffineInverse();
        var o3 = inverse * _camera!.GlobalPosition;
        var d3 = (inverse.Basis * (-_camera.GlobalBasis.Z)).Normalized();
        var o = new System.Numerics.Vector3(o3.X, o3.Y, o3.Z);
        var d = new System.Numerics.Vector3(d3.X, d3.Y, d3.Z);
        if (ray is { } fresh) { o = fresh.Origin; d = fresh.Direction; }   // feel/controls: the ray at the view of this moment (FreshCenterRay)
        float t0 = MathF.Max(0f, System.Numerics.Vector3.Dot(Combat.EyePosition(me, c) - o, d));
        var start = o + d * t0;
        var inHand = Loadouts.InHand(c, me);
        float range = inHand?.MaxRange is { } r and > 0f ? r : 100f;
        // M39: with a piercing weapon the crosshair names the first player the shot would damage, through props too (NetworkView.Pierce.cs).
        if (inHand is { Delivery: DeliveryKind.Raycast } held && Pierces.Of(held, 0, upgrade => Pierces.HasUpgrade(c, me, upgrade)) is { } pierce)
        {
            var picture = PictureShot(start, d, range, pierce, c);
            float shown = picture.FirstVictim is null ? picture.EndDistance
                : Combat.HitShapes(start, d, picture.FirstVictim.Value.Position, HitBodyAsDrawn(picture.FirstVictim.Value), range, picture.FirstVictim.Value.Yaw).Distance is var fd && fd <= range ? fd : picture.EndDistance;
            return (start + d * shown, picture.FirstVictim, start, d);
        }
        float best = range;
        PlayerState? hit = null;
        if (_client?.Collision is { } world && world.Weapons.Raycast(start, d, range) is { } wall) best = MathF.Min(best, wall.Distance);
        foreach (var other in _lastOthers)
        {
            if (!other.Alive) continue;
            var (zone, distance) = Combat.HitShapes(start, d, other.Position, HitBodyAsDrawn(other), best, other.Yaw);   // fix/hitboxes: as drawn
            if (zone != HitZone.None && distance < best) { best = distance; hit = other; }
        }
        return (start + d * best, hit, start, d);
    }

    /// <summary>The input with its shot aimed at the point under the screen-centre crosshair (human players only; see the class comment).</summary>
    PlayerInput Converged(PlayerInput input)
    {
        if ((_bot && !BulletProbe) || _camera is null || _gameSpace is null || _config is null || _client is null || _client.Status != ClientStatus.Connected) return input;
        var me = _client.Predicted;
        if (!me.Alive) return input;
        // The shot leaves along the camera ray, from where that ray crosses the eye's plane (the server keeps the origin within
        // 1.5 m of the eye and falls back to the eye if the way to it is blocked), so close targets are hit too.
        // feel/controls: the ray turned to the view this input carries (the camera was placed last frame, before this frame's mouse movement).
        var (_, _, start, direction) = CenterRayTarget(me, _config, FreshCenterRay());
        LatencyProbeShot(direction);   // feel/controls test aid (--latency-probe)
        return input.WithShot(start, direction, Combat.EyePosition(me, _config));
    }

    double _convergenceLogAt = double.NegativeInfinity;

    /// <summary>
    /// <c>--aim-test</c> diagnostic: every 2 s prints what a human's shot would be from the bot's input: the point under the
    /// crosshair, its distance from the camera and the resulting shot offsets (the bot itself sends none).
    /// </summary>
    void LogConvergence(PlayerInput input)
    {
        if (_camera is null || _gameSpace is null || _config is null || _client is null || _connectedFor - _convergenceLogAt < 2.0) return;
        var me = _client.Predicted;
        if (!me.Alive) return;
        _convergenceLogAt = _connectedFor;
        var (point, hit, start, direction) = CenterRayTarget(me, _config);
        var shot = input.WithShot(start, direction, Combat.EyePosition(me, _config));
        var cam = _gameSpace.Transform.AffineInverse() * _camera.GlobalPosition;
        float distance = System.Numerics.Vector3.Distance(point, new System.Numerics.Vector3(cam.X, cam.Y, cam.Z));
        GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} converged shot (not sent): point {point.X:F1},{point.Y:F1},{point.Z:F1} {distance:F1} m from the camera" +
                 $"{(hit is { } h ? $" on P{h.PlayerId}" : "")}, offsets yaw {shot.ShotYawOffset * 57.2958f:F1} deg pitch {shot.ShotPitchOffset * 57.2958f:F1} deg, origin offset {shot.ShotOrigin.Length():F2} m; aiming {me.Aiming}; camera {_cameraInfo}");
    }

    /// <summary>Per hit report of the local player (<see cref="GameClient.TakeHits"/>): the hit marker and the target's health for the bar.</summary>
    void UpdateHits()
    {
        if (_client?.TakeHits() is not { } hits || _config is null) return;
        _hudView?.ShowHitMarker(hits.Kills > 0 || hits.LastKilled, hits.LastHead);
        BotNoteHits(hits);   // M44a: the bot's hit statistics
        FlinchNoteOwnHit(hits);   // animation pass 2: the victim flinches
        SoundHits(hits);   // M26b
        _lastVictim = hits.LastVictim; _lastVictimHealth = hits.VictimHealth; _lastVictimShield = hits.VictimShield;
        _lastHitAt = _connectedFor;
        if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} hit report: {hits.Hits} hit(s), shield {hits.ShieldDamage:F1} health {hits.HealthDamage:F1}, kills {hits.Kills}, last P{hits.LastVictim}{(hits.LastHead ? " head" : "")}{(hits.LastKilled ? " killed" : "")}{(hits.LastMelee ? " melee" : "")}{(hits.LastSplash ? " splash" : "")}, victim {hits.VictimHealth:F0}/{hits.VictimShield:F0}");
    }
}

public partial class NetworkView
{
    /// <summary>The evade part of the F1 text: the own class's roll and counts.</summary>
    string EvadeInfo()
    {
        if (_client?.Status != ClientStatus.Connected || _config is null) return "";
        var me = _client.Predicted;
        return Loadouts.Evade(_config, me) is { } spec
            ? $"Own roll: {spec.Name} (camera {spec.Camera}{(Evade.Evading(me) ? $", now {me.EvadeDirection} {Evade.Time(me, _config):F2} s" : "")}); rolls {_evadesOwn}, seen {_evadesSeen}; Space rolls (sideways with A / D)."
            : "This class has no roll (Space is its jump pack).";
    }
}
