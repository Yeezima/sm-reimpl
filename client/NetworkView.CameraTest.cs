using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// camera/hop-and-walls capture aid <c>--camera-test hop|wall|corner|pillar</c> (with <c>--bot</c>; not original behaviour): the bot has the human follow camera, its view
/// pitch is <c>--camera-test-pitch</c> (radians, default -1.4 for hop, -0.1 for the rest; the sets' lower limit still applies) and it runs a fixed script:
/// <list type="bullet">
/// <item><c>hop</c>: Space for 0.1 s every 3 s, S and W in turn: backwards and forwards evades (a Devastator or Havoc with <c>--class</c>: the hop).</item>
/// <item><c>wall</c>: backs up until a wall stops it, stands, walks 2 m out, backs in again.</item>
/// <item><c>corner</c>: backs up to a wall, turns the view 90 degrees, backs up to the next one, then turns the view left and right.</item>
/// <item><c>pillar</c>: finds the nearest narrow obstacle by a ray scan at chest height, walks up to it, stands with it behind and strafes left and right across it.</item>
/// </list>
/// A <c>CAMTEST</c> line twice a second and a <c>CAMSUM</c> line every 5 s: the camera's distance from the head, how far the character's chest lies off the view axis, the camera's
/// largest step per frame not explained by the rig's own move, how often the pull-in changed its mind, and how far the crosshair's world point lay from the uncollided rig's.
/// </summary>
public partial class NetworkView
{
    static readonly string? CameraTestMode = CmdArg("camera-test");
    static readonly float CameraTestPitchArg = CmdFloat("camera-test-pitch");
    static bool CameraTest => CameraTestMode is not null;
    float CameraTestPitch => float.IsFinite(CameraTestPitchArg) ? CameraTestPitchArg : CameraTestMode == "hop" ? -1.4f : -0.1f;

    double _ctStart = -1, _ctLogAt, _ctSumAt, _ctBlockAt, _ctPhaseStart;
    int _ctPhase, _ctCycle = -1, _ctPressTicks;
    float _ctYaw, _ctSide = 1f;
    System.Numerics.Vector3 _ctLast, _ctTarget;
    bool _ctHasTarget;

    PlayerInput? CameraTestInput(PlayerState me, uint tick)
    {
        if (!CameraTest || !me.Alive) { _ctStart = -1; return null; }
        if (_ctStart < 0) { _ctStart = _connectedFor + 1.5; _ctYaw = _yaw; _ctPhase = 0; _ctHasTarget = false; _ctBlockAt = _ctStart; _ctLast = me.Position; }
        double t = _connectedFor - _ctStart;
        float x = 0f, y = 0f;
        var buttons = InputButtons.None;
        if (t >= 0)
        {
            // Blocked: a stick held but no ground covered over the last half second.
            bool blocked = false;
            if (_connectedFor - _ctBlockAt >= 0.5)
            {
                blocked = System.Numerics.Vector3.Distance(me.Position, _ctLast) < 0.15f;
                _ctLast = me.Position; _ctBlockAt = _connectedFor;
            }
            switch (CameraTestMode)
            {
                case "hop":
                    {
                        int cycle = (int)(t / 3.0);   // by the cycle, not a time window: a slow capture frame must not skip the press
                        if (cycle != _ctCycle) { _ctCycle = cycle; _ctPressTicks = 3; }
                        if (_ctPressTicks > 0) { _ctPressTicks--; y = cycle % 2 == 0 ? -1f : 1f; buttons = InputButtons.Jump; }   // back, then forward again so the back hops stay off the wall
                    }
                    break;
                case "wall":
                    if (_ctPhase == 0) { y = -1f; if (blocked) CtNext(); }
                    else if (_ctPhase == 1) { if (CtElapsed(2.0)) CtNext(); }
                    else if (_ctPhase == 2) { y = 1f; if (CtElapsed(0.9)) CtNext(); }
                    else if (CtElapsed(0.8)) _ctPhase = 0;
                    break;
                case "corner":
                    if (_ctPhase == 0) { y = -1f; if (blocked) { _ctYaw += MathF.PI / 2f; CtNext(); } }
                    else if (_ctPhase == 1) { y = -1f; if (blocked) CtNext(); }
                    else _ctYaw += MathF.Sin((float)(t * 0.8)) * 0.012f;
                    break;
                case "pillar":
                    if (!_ctHasTarget)
                    {
                        _ctHasTarget = CtFindPillar(me, out _ctTarget); _ctPhase = 0;
                        if (!_ctHasTarget) { _pitch = CameraTestPitch; return null; }   // the normal bot walks on until one is near
                        GD.Print($"t {_connectedFor:F2} s: CAMTEST pillar found at {_ctTarget.X:F1},{_ctTarget.Y:F1}, from {me.Position.X:F1},{me.Position.Y:F1}");
                    }
                    if (_ctHasTarget)
                    {
                        var to = _ctTarget - me.Position; to.Z = 0f;
                        if (_ctPhase == 0)
                        {
                            _ctYaw = MathF.Atan2(-to.X, to.Y);
                            y = 1f;
                            if (to.Length() < 2.2f || blocked)
                            {
                                _ctYaw += MathF.PI; CtNext();
                                GD.Print($"t {_connectedFor:F2} s: CAMTEST at the pillar {_ctTarget.X:F1},{_ctTarget.Y:F1}, {to.Length():F1} m from it");
                            }
                        }
                        else
                        {
                            x = _ctSide;
                            if (CtElapsed(1.6)) { _ctSide = -_ctSide; CtNext(); }
                        }
                    }
                    break;
            }
        }
        _yaw = _ctYaw; _pitch = CameraTestPitch;
        return new PlayerInput(tick, x, y, _yaw, buttons, _pitch);
    }

    void CtNext() { _ctPhase++; _ctPhaseStart = _connectedFor; }
    bool CtElapsed(double s) => _connectedFor - _ctPhaseStart >= s;

    /// <summary>The nearest narrow obstacle around the player: a ray fan at chest height where a run of 2 to 6 rays (of 180) ends well in front of its neighbours.</summary>
    bool CtFindPillar(PlayerState me, out System.Numerics.Vector3 found)
    {
        found = default;
        if (_client?.Collision is not { } world) return false;
        const int N = 180;
        Span<float> d = stackalloc float[N];
        var eye = me.Position + new System.Numerics.Vector3(0f, 0f, 1.4f);
        for (int i = 0; i < N; i++)
        {
            float a = i * MathF.Tau / N;
            d[i] = world.Character.Raycast(eye, new System.Numerics.Vector3(MathF.Cos(a), MathF.Sin(a), 0f), 40f)?.Distance ?? 40f;
        }
        float best = float.MaxValue;
        for (int i = 0; i < N; i++)
            for (int w = 2; w <= 6; w++)
            {
                float near = 0f;
                for (int k = 0; k < w; k++) near = MathF.Max(near, d[(i + k) % N]);
                float side = MathF.Min(d[(i + N - 2) % N], d[(i + w + 1) % N]);
                if (near < 2.5f || near > 10f || side < near + 2f || near >= best) continue;
                float a = (i + (w - 1) * 0.5f) * MathF.Tau / N;
                best = near;
                found = me.Position + new System.Numerics.Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * near;
            }
        return best < float.MaxValue;
    }

    // ---- the measurements ----
    float _ctMaxOff, _ctMaxStep, _ctMinDist = float.MaxValue, _ctMaxDist, _ctMaxAimShift, _ctPrevDist = -1f, _ctPrevSign;
    Vector3 _ctPrevPos, _ctPrevWanted;
    bool _ctPrevValid;
    int _ctReversals, _ctFrames;

    /// <summary>Per frame from <see cref="RigCamera"/> (with <c>--camera-test</c>); see the class comment. <paramref name="aimShift"/>: metres between the crosshair's world point and the uncollided rig's.</summary>
    void CameraTestNote(PlayerState me, Vector3 position, Vector3 wanted, Vector3 look, float aimShift)
    {
        if (!CameraTest || _config is null || _gameSpace is null) return;
        var feet = _gameSpace.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        var chest = feet + Vector3.Up * 1.3f;
        float off = Mathf.RadToDeg((chest - position).Normalized().AngleTo(look));
        float dist = position.DistanceTo(feet + Vector3.Up * _config.EyeHeight);
        _ctFrames++;
        _ctMaxOff = MathF.Max(_ctMaxOff, off);
        _ctMinDist = MathF.Min(_ctMinDist, dist); _ctMaxDist = MathF.Max(_ctMaxDist, dist);
        _ctMaxAimShift = MathF.Max(_ctMaxAimShift, aimShift);
        if (_ctPrevValid)
        {
            _ctMaxStep = MathF.Max(_ctMaxStep, ((position - _ctPrevPos) - (wanted - _ctPrevWanted)).Length());
            float change = dist - _ctPrevDist;
            if (MathF.Abs(change) > 0.01f)
            {
                float sign = MathF.Sign(change);
                if (_ctPrevSign != 0f && sign != _ctPrevSign) _ctReversals++;
                _ctPrevSign = sign;
            }
        }
        _ctPrevPos = position; _ctPrevWanted = wanted; _ctPrevDist = dist; _ctPrevValid = true;
        if (_connectedFor - _ctLogAt >= 0.5)
        {
            _ctLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F2} s: CAMTEST {CameraTestMode} pitch {_pitch:F2} evading {Evade.Evading(me)} head {dist:F2} m, chest {off:F1} deg off axis, cam up {position.Y - feet.Y:F2}, hidden {_ownHidden}, aim shift {aimShift:F2} m, at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1}");
        }
        if (_connectedFor - _ctSumAt >= 5.0)
        {
            _ctSumAt = _connectedFor;
            GD.Print($"t {_connectedFor:F2} s: CAMSUM {CameraTestMode} {_ctFrames} frames: chest off axis at most {_ctMaxOff:F1} deg, collision step at most {_ctMaxStep:F3} m, head distance {_ctMinDist:F2}..{_ctMaxDist:F2} m, {_ctReversals} reversals, aim shift at most {_ctMaxAimShift:F2} m");
            _ctMaxOff = _ctMaxStep = _ctMaxDist = _ctMaxAimShift = 0f; _ctMinDist = float.MaxValue; _ctReversals = 0; _ctFrames = 0;
        }
    }
}
