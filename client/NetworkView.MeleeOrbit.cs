using System;
using Godot;
using SmClient.Customizer.Model;
using SmSim;

namespace SmClient;

/// <summary>
/// camera/melee-orbit (owner 2026-11-25: "After a melee attack the player should be in melee mode and able to orbit his camera fully around him until his next ranged
/// weapon shot puts him back in the default locked camera state"; decision melee-camera-orbit). Client only: no protocol, wire or simulation change.
/// <para>
/// The state: <see cref="MeleeMode"/>, stepped once per input tick (<see cref="StepMeleeMode"/>) and shared with the field-of-view widening (NetworkView.MeleeFov.cs).
/// </para>
/// <para>
/// The yaw split while in melee mode (a human's keyboard and mouse or pad input, <see cref="MeleeOrbitInput"/>, before the converged shot is added): <c>_yaw</c> is the
/// camera's yaw only (the mouse and right stick orbit it all the way round; pitch as before), and the input's yaw is the body's (<see cref="MeleeOrbitSteer"/>): the
/// camera-relative keys/stick give the direction to run, sent as the yaw with the stick as pure forward, so the server (and the prediction, the same input) runs the
/// marine facing his run. Idle stick: the body holds. A strike with no key held goes the way the camera looks. The tick that leaves the mode (a shot, aim, reload)
/// sends the camera's yaw, so that shot leaves along the crosshair; the drawn body turns to it smoothly (<see cref="MeleeOrbitFrame"/>).
/// </para>
/// Which yaw each consumer takes (audit): the camera placement, the rig's yaw rate, the aim direction, <c>NoteCameraPlaced</c>/<c>FreshCenterRay</c>, the crosshair
/// target and the converged shot ray all take the camera's <c>_yaw</c> (NetworkView.Camera.cs, NetworkView.Controls.cs); the simulation's movement, the melee lock's
/// search, the attack and evade direction and the charge's start take the input's yaw, the body's (so they go along the run, or the camera's look for a strike with no
/// key); the grenade throw and a jump-pack jump and its flight (and the ground pound's aim) send the camera's yaw and the plain stick (<see cref="OrbitSuspended"/>), so
/// they go where the crosshair is, as before; the own drawn model takes <see cref="DisplayYaw"/> (the body's, smoothed); bots, demos and test aids are unchanged;
/// spectators and other players draw the body as sent (<see cref="PlayerState.Yaw"/>). <c>--old-melee-camera</c> restores the locked camera (no orbit);
/// <c>--log-melee-mode</c> logs the mode; <c>--melee-orbit-demo</c> is the scripted proof.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--old-melee-camera</c> (restore switch, camera/melee-orbit): the camera stays locked to the body in melee mode (no orbit); the widening keeps its own switch.</summary>
    static readonly bool OldMeleeCamera = CmdFlag("old-melee-camera");
    /// <summary><c>--melee-orbit-demo</c> (test aid, human client): a scripted strike, a standing 360 degree orbit, a 360 degree orbit while running, a shot, then further strikes with a throw and a reload.</summary>
    static readonly bool MeleeOrbitDemo = CmdFlag("melee-orbit-demo");
    /// <summary><c>--log-melee-mode</c> (test aid): a line for every change of the melee mode, and with <c>--melee-orbit-demo</c> every 0.5 s.</summary>
    static readonly bool LogMeleeMode = CmdFlag("log-melee-mode") || MeleeOrbitDemo;

    /// <summary>Presentation, ours: how fast the drawn own body chases the simulation's heading in melee mode and turns back to the view after it (degrees a second).</summary>
    const float MeleeDrawTurnDegreesPerSecond = 1440f;

    readonly MeleeMode _meleeMode = new();
    readonly MeleeOrbitSteer _meleeSteer = new();
    MeleeMode.Change _meleeModeLast;
    uint _meleeModeTick;
    bool _meleeModeStepped;
    InputButtons _meleeOrbitPrevButtons;
    bool _meleeDrawn;           // the own model draws _meleeDrawnYaw (melee mode, or turning back after it)
    float _meleeDrawnYaw;
    double _meleeLogAt;

    /// <summary>Hook for added content: a body with no melee mode, so no field-of-view widening and no camera orbit (Musou: the Dreadnought).</summary>
    partial void ExtrasMeleeModeExcluded(PlayerState me, ref bool excluded);

    /// <summary>A strike with a melee weapon (not the combat knife) is in progress (<see cref="MeleeMode.EntersMeleeMode"/>).</summary>
    bool MeleeWeaponStrike(PlayerState me) => _config is not null && me.MeleeStep > 0 && MeleeMode.EntersMeleeMode(MeleeWargear(me));

    /// <summary>The camera orbits freely now (a human's own view in melee mode).</summary>
    bool MeleeOrbiting => !OldMeleeCamera && !_bot && _meleeMode.Active;

    /// <summary>Steps the shared melee mode once for this tick's input (repeat calls in the same tick do nothing).</summary>
    MeleeMode.Change StepMeleeMode(PlayerState me, PlayerInput input)
    {
        if (_meleeModeStepped && _meleeModeTick == input.Tick) return MeleeMode.Change.None;
        _meleeModeStepped = true; _meleeModeTick = input.Tick;
        bool excluded = false;
        ExtrasMeleeModeExcluded(me, ref excluded);   // EXTRAS-HOOK: the Dreadnought has no melee mode
        var b = input.Buttons;
        var change = _meleeMode.Step(new MeleeMode.Inputs(me.Alive && !OwnDead, MeleeWeaponStrike(me), (b & InputButtons.Fire) != 0, (b & InputButtons.Aim) != 0,
            (b & InputButtons.Reload) != 0, excluded));
        if (change != MeleeMode.Change.None)
        {
            _meleeModeLast = change;
            if (LogMeleeMode) GD.Print($"MELEEMODE t {_connectedFor:F2} s: {change} active {_meleeMode.Active} camera yaw {Deg(_yaw):F1} body yaw {Deg(me.Yaw):F1} melee step {me.MeleeStep} weapon {MeleeWargear(me) ?? "-"}{(OldMeleeCamera ? " (--old-melee-camera: no orbit)" : "")}");
        }
        return change;
    }

    static float Deg(float radians) => MeleeOrbitSteer.Wrap(radians) * 180f / MathF.PI;

    /// <summary>
    /// A human's live input (keyboard and mouse, or pad) before the converged shot: steps the melee mode and, while it is on, sends the body's yaw and the stick as forward
    /// (<see cref="MeleeOrbitSteer"/>). Off, leaving it this tick, dead, or with <c>--old-melee-camera</c>: the input as it was (the camera's yaw).
    /// </summary>
    PlayerInput MeleeOrbitInput(PlayerInput input)
    {
        if (_client is null || _config is null) return input;
        var me = _client.Predicted;
        StepMeleeMode(me, input);
        var b = input.Buttons;
        var prev = _meleeOrbitPrevButtons;   // read before it is replaced: Press compares with the last tick's buttons
        _meleeOrbitPrevButtons = b;
        bool Press(InputButtons x) => (b & x) != 0 && (prev & x) == 0;
        if (!MeleeOrbiting || !me.Alive) { _meleeSteer.Reset(); return input; }
        bool strike = Press(InputButtons.Melee) || Press(InputButtons.MeleeHeavy);
        if (OrbitSuspended(me, Press(InputButtons.Grenade), Press(InputButtons.Jump), strike)) { _meleeSteer.Reset(); return input; }
        var r = _meleeSteer.Step(_yaw, me.Yaw, input.MoveX, input.MoveY, snap: strike || Press(InputButtons.Jump), strikePress: strike, _config.TickSeconds);
        return input with { Yaw = r.Yaw, MoveX = r.MoveX, MoveY = r.MoveY };
    }

    /// <summary>
    /// The ticks that send the camera's yaw and the plain stick although in melee mode: a grenade press and the throw (it goes along the crosshair), a jump-pack jump's
    /// press and its whole flight (the take-off, the steering and the ground pound's aim follow the view; the pound's landing mark is drawn from it); and a strike string
    /// latched to a side or back (<see cref="PlayerState.MeleeDirection"/>, the directional melee of a strike begun with a side key outside melee mode, normally the one
    /// that entered it): the simulation faces that string along the view turned by its direction, so it keeps the camera's frame (the same world direction) until it ends
    /// or a strike press starts a forward one along the keys.
    /// </summary>
    bool OrbitSuspended(PlayerState me, bool grenadePress, bool jumpPress, bool strikePress) =>
        grenadePress || me.Throwing || me.Jump != JumpPhase.None || (jumpPress && Loadouts.JumpPack(_config!, me) is not null)
        || (me.MeleeStep > 0 && me.MeleeDirection != EvadeDirection.Forward && !strikePress);

    /// <summary><c>--melee-orbit-demo</c>: the scripted input through the human path (<see cref="MeleeOrbitInput"/>, then the converged shot) with a log line every 0.5 s.</summary>
    PlayerInput MeleeOrbitDemoSample(uint tick)
    {
        var raw = MeleeOrbitDemoInput(tick);
        var sent = MeleeOrbitInput(raw);
        if (_client is not null) MeleeOrbitLog(_client.Predicted, raw, sent);
        return Converged(sent);
    }

    void MeleeOrbitLog(PlayerState me, PlayerInput raw, PlayerInput sent)
    {
        if (!MeleeOrbitDemo || (_connectedFor - _meleeLogAt < 0.5 && !(me.MeleeStep > 0 && _meleeMode.Active))) return;   // every tick of a strike in melee mode
        _meleeLogAt = _connectedFor;
        float speed = new System.Numerics.Vector2(me.Velocity.X, me.Velocity.Y).Length();
        float heading = speed > 0.5f ? Deg(MathF.Atan2(-me.Velocity.X, me.Velocity.Y)) : float.NaN;
        GD.Print($"MELEEORBIT t {_connectedFor:F2} s: melee mode {_meleeMode.Active} camera yaw {Deg(_yaw):F1} sent yaw {Deg(sent.Yaw):F1} body yaw {Deg(me.Yaw):F1} keys {raw.MoveX:F0},{raw.MoveY:F0} sent stick {sent.MoveX:F2},{sent.MoveY:F2} melee step {me.MeleeStep} buttons {sent.Buttons} speed {speed:F2} m/s run heading {(float.IsNaN(heading) ? "-" : heading.ToString("F1"))}");
    }

    /// <summary>
    /// Once per frame before the own model is placed: in melee mode the drawn body chases the simulation's heading (not the camera) at
    /// <see cref="MeleeDrawTurnDegreesPerSecond"/>; after the mode it turns back to the view's at the same rate, then hands over to the usual drawing.
    /// </summary>
    void MeleeOrbitFrame(PlayerState me, float delta)
    {
        float normal = DisplayYawLocked(me);
        if (OldMeleeCamera || _bot || !me.Alive || OwnDead) { _meleeDrawn = false; return; }
        bool active = _meleeMode.Active;
        if (!active && !_meleeDrawn) return;
        if (!_meleeDrawn) _meleeDrawnYaw = normal;
        float target = active ? me.Yaw : normal;
        float step = MeleeDrawTurnDegreesPerSecond * MathF.PI / 180f * MathF.Max(0f, delta);
        float diff = MeleeOrbitSteer.Wrap(target - _meleeDrawnYaw);
        _meleeDrawnYaw = MathF.Abs(diff) <= step ? target : _meleeDrawnYaw + MathF.Sign(diff) * step;
        _meleeDrawn = active || MathF.Abs(diff) > step;
    }

    // ---- --melee-orbit-demo (test aid) ----
    double _orbitDemoStart = -1;
    float _orbitDemoYaw;

    /// <summary>
    /// The scripted input of <c>--melee-orbit-demo</c> (time from 3 s after joining): 0.0 strike (no key); 1.5-5.5 the camera orbits 360 degrees standing (the body
    /// holds); 6.0-10.0 another 360 degrees with W held (the marine runs a circle, facing his run); 10.5 a shot (back to the locked camera, the body turns to the view);
    /// 12.0 strike with D held (to the camera's right); 13.0 a grenade (stays); 14.5 a reload (leaves); 16.0 strike, 17.0 aim (leaves); 19.0 strike, then 20.0 a
    /// 180 degree camera turn and a strike with no key (it goes the camera's way); 22.0 a shot. Then it repeats.
    /// </summary>
    PlayerInput MeleeOrbitDemoInput(uint tick)
    {
        if (_orbitDemoStart < 0) { _orbitDemoStart = _connectedFor + 3.0; _orbitDemoYaw = _yaw; }
        double t = _connectedFor - _orbitDemoStart;
        if (t >= 24.0) { _orbitDemoStart += 24.0; t -= 24.0; _orbitDemoYaw = _yaw; }
        const float Orbit = 2f * MathF.PI / 4f;   // 90 degrees a second
        float x = 0f, y = 0f;
        var b = InputButtons.None;
        if (t < 0) return new PlayerInput(tick, 0f, 0f, _yaw, b, 0f);
        if (t < 1.5) { _yaw = _orbitDemoYaw; }
        else if (t < 5.5) _yaw = _orbitDemoYaw + Orbit * (float)(t - 1.5);
        else if (t < 6.0) _yaw = _orbitDemoYaw;
        else if (t < 10.0) { _yaw = _orbitDemoYaw + Orbit * (float)(t - 6.0); y = 1f; }
        else _yaw = t is >= 20.0 and < 23.0 ? _orbitDemoYaw + MathF.PI : _orbitDemoYaw;
        _pitch = 0f;
        if (t is >= 0.0 and < 0.1 or >= 16.0 and < 16.1 or >= 19.0 and < 19.1 or >= 20.5 and < 20.6) b |= InputButtons.Melee;
        if (t is >= 12.0 and < 12.1) { b |= InputButtons.Melee; x = 1f; }
        if (t is >= 11.9 and < 12.6) x = 1f;
        if (t is >= 10.5 and < 10.8 or >= 22.0 and < 22.3) b |= InputButtons.Fire;   // 0.3 s: a frame hitch must not skip the press
        if (t is >= 13.0 and < 13.2) b |= InputButtons.Grenade;
        if (t is >= 14.5 and < 14.75) b |= InputButtons.Reload;
        if (t is >= 17.0 and < 17.5) b |= InputButtons.Aim;
        return new PlayerInput(tick, x, y, _yaw, b, _pitch);
    }
}
