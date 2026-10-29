using System;
using System.Linq;
using Godot;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Weapon spread on the client (weapons/spread; the owner's decision 2026-10-28; rules in <see cref="Spread"/>):
/// <list type="bullet">
/// <item>The own shot's picture (tracer, impact, decal; <c>TraceOwn</c>) is turned by the predicted shot's scatter
/// (<see cref="PlayerState.SpreadYaw"/>/<see cref="PlayerState.SpreadPitch"/>), through the server's own maths
/// (<see cref="Combat.ShotDirection(PlayerInput, SimConfig, PlayerState)"/>), so it lands where the server's shot lands.</item>
/// <item>The crosshair's four ticks move out by the next shot's horizontal half-width projected on the screen (<see cref="Hud.Frame.SpreadRadius"/>).
/// Evidence that the original reticule changed size: <c>Hud.gfx</c> carries the names <c>min_aim</c> / <c>max_aim</c> next to its
/// reticules (names only; the movie's script is not read); how it grew is ours.</item>
/// <item>Handling (the data's <c>climb</c> and <c>sway</c> blocks of the same set, **interpretation**): each own shot kicks the view (and so the
/// aim, which is the view) up by <see cref="HandlingSet.TemporaryClimbPerShot"/> (temporary, at most <see cref="HandlingSet.MaxTemporaryClimb"/>,
/// reached at <see cref="HandlingSet.ClimbRate"/> °/s and recovered at <see cref="HandlingSet.ClimbRecoveryRate"/> °/s) plus
/// <see cref="HandlingSet.PermanentClimbPerShot"/> (kept), and sideways by a random temporary sway between the per-shot minimum and maximum
/// (signed; at most <see cref="HandlingSet.MaxTemporarySway"/>, <see cref="HandlingSet.SwayRate"/> / <see cref="HandlingSet.SwayRecoveryRate"/>) plus a
/// random permanent one. Lascannon Stabilizers' handling sets act here. Humans only (a bot sets its view itself); <c>--no-handling</c> switches it
/// off. The <c>temporary_sway_increase_per_shot</c> / <c>_decrease_per_second</c> / <c>_decrease_delay</c> fields are not used (meaning not established).
/// With handling on, the placeholder camera shake of an own shot (<c>FxKick</c>) is not added.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    bool? _noHandling;
    /// <summary><c>--no-handling</c>: no climb or sway kick on the own view.</summary>
    bool NoHandling => _noHandling ??= OS.GetCmdlineUserArgs().Contains("--no-handling");

    /// <summary>Whether own shots kick the view (a human player in a live match).</summary>
    bool HandlingActive => !_bot && !NoHandling && _demoPlayback is null;

    float _climbTarget, _climbShown, _climbPermanentLeft, _swayTarget, _swayShown, _swayPermanentLeft;
    int _handlingKicks;
    readonly Random _handlingRandom = new(0x5EED);

    /// <summary>The own shot's direction for its picture: <paramref name="wanted"/> (the camera ray, or the aim) with the predicted scatter, as the server turns it.</summary>
    Num.Vector3 OwnSpreadDirection(PlayerState me, Num.Vector3 wanted, SimConfig c)
    {
        if (me.SpreadYaw == 0f && me.SpreadPitch == 0f) return wanted;
        var input = new PlayerInput(0, 0f, 0f, me.Yaw, InputButtons.None, me.Pitch).WithShotDirection(wanted);
        return Combat.ShotDirection(input, c, me);
    }

    /// <summary>
    /// The crosshair ticks' extra distance (the HUD's 1080-high virtual units): the next shot's horizontal half-width at the camera's vertical
    /// field of view (0 without spread).
    /// </summary>
    float SpreadPixels(PlayerState me, SimConfig c)
    {
        if (_camera is null || !me.Alive) return 0f;
        var (h, _) = Spread.HalfWidths(Loadouts.InHand(c, me), me, 0, c.MoveSpeedScale);
        if (h <= 0f) return 0f;
        float half = Mathf.DegToRad(Math.Clamp(_camera.Fov, 5f, 170f)) * 0.5f;
        return 540f * MathF.Tan(Mathf.DegToRad(h)) / MathF.Tan(half);
    }

    /// <summary>Kicks the view for this frame's own shots and moves the kick on (call once per frame after <c>HipFireBegin</c>).</summary>
    void HandlingFrame(PlayerState me, double delta)
    {
        if (!HandlingActive || _config is null) return;
        if (!me.Alive) { _climbTarget = _climbShown = _climbPermanentLeft = _swayTarget = _swayShown = _swayPermanentLeft = 0f; return; }
        var weapon = Loadouts.InHand(_config, me);
        HandlingSet? last = null;
        foreach (var (_, ev) in _frameOwnEvents)
        {
            if (!ev.Fired || Spread.SetFor(weapon, me, ev.ShotStage, _config.MoveSpeedScale)?.Handling is not { } hs) continue;
            last = hs;
            _handlingKicks++;
            _climbTarget = MathF.Min(MathF.Max(hs.MaxTemporaryClimb, 0f), _climbTarget + MathF.Max(0f, hs.TemporaryClimbPerShot));
            _climbPermanentLeft += MathF.Max(0f, hs.PermanentClimbPerShot);
            float side = _handlingRandom.Next(2) == 0 ? -1f : 1f;
            float kick = Mathf.Lerp(hs.MinTemporarySwayPerShot, hs.MaxTemporarySwayPerShot, (float)_handlingRandom.NextDouble());
            float maxSway = MathF.Max(0f, hs.MaxTemporarySway);
            _swayTarget = Math.Clamp(_swayTarget + side * MathF.Max(0f, kick), -maxSway, maxSway);
            _swayPermanentLeft += (_handlingRandom.Next(2) == 0 ? -1f : 1f)
                * MathF.Max(0f, Mathf.Lerp(hs.MinPermanentSwayPerShot, hs.MaxPermanentSwayPerShot, (float)_handlingRandom.NextDouble()));
        }
        var set = last ?? Spread.SetFor(weapon, me, 0, _config.MoveSpeedScale)?.Handling ?? HandlingSet.None;
        float dt = (float)Math.Clamp(delta, 0.0, 0.25);
        // Temporary parts: the target recovers toward 0, the shown kick follows the target at the rate.
        _climbTarget = Toward(_climbTarget, 0f, MathF.Max(0f, set.ClimbRecoveryRate) * dt);
        _swayTarget = Toward(_swayTarget, 0f, MathF.Max(0f, set.SwayRecoveryRate) * dt);
        float climbRate = set.ClimbRate > 0f ? set.ClimbRate : 50f, swayRate = set.SwayRate > 0f ? set.SwayRate : 25f;
        float climb = Toward(_climbShown, _climbTarget, climbRate * dt), sway = Toward(_swayShown, _swayTarget, swayRate * dt);
        float permanentClimb = MathF.Min(_climbPermanentLeft, climbRate * dt);
        float permanentSway = Math.Clamp(_swayPermanentLeft, -swayRate * dt, swayRate * dt);
        _climbPermanentLeft -= permanentClimb; _swayPermanentLeft -= permanentSway;
        float dPitch = climb - _climbShown + permanentClimb, dYaw = sway - _swayShown + permanentSway;
        _climbShown = climb; _swayShown = sway;
        if (dPitch == 0f && dYaw == 0f) return;
        _pitch = Math.Clamp(_pitch + Mathf.DegToRad(dPitch), _pitchMin, _pitchMax);
        _yaw = (_yaw + Mathf.DegToRad(dYaw)) % (2 * MathF.PI);
    }

    static float Toward(float value, float target, float step) => value < target ? MathF.Min(target, value + step) : MathF.Max(target, value - step);

    float? _spreadWallYaw;
    string? _spreadWallMode;
    bool _spreadWallRead;

    /// <summary>
    /// <c>--spread-wall [hip|aim]</c> (test aid, with <c>--bot</c>; add <c>--aim-test</c> for the player's camera; <c>--spread-turn</c>, <c>--spread-pitch</c> in degrees): the bot turns to face away from its spawn
    /// facing (the spawn room's back wall), stands still and holds the trigger (aim mode with <c>aim</c>), so the impacts draw the scatter pattern on
    /// the wall. Null when the flag is absent or the bot is not alive.
    /// </summary>
    PlayerInput? SpreadWallInput(uint tick)
    {
        if (!_spreadWallRead)
        {
            _spreadWallRead = true;
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--spread-wall");
            _spreadWallMode = i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : "hip";
        }
        if (_spreadWallMode is null || _client is null) return null;
        var me = _client.Predicted;
        if (!me.Alive) { _spreadWallYaw = null; return null; }
        // --spread-turn <deg> (default 180: the wall behind the spawn facing) and --spread-pitch <deg> (default 0; e.g. -9 for the floor 10 m ahead).
        _spreadWallYaw ??= me.Yaw + Mathf.DegToRad(FloatArg("spread-turn") ?? 180f);
        _yaw = _spreadWallYaw.Value; _pitch = Mathf.DegToRad(FloatArg("spread-pitch") ?? 0f);
        var buttons = InputButtons.Fire | (_spreadWallMode == "aim" ? InputButtons.Aim : InputButtons.None);
        return new PlayerInput(tick, 0f, 0f, _yaw, buttons, _pitch);
    }
}
