using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// feel/charge-directions (protocol 39; the owner's rules of 2026-10-29) in the networked view. The simulation's rules are <see cref="Rush.CanStart"/>
/// (W held at the F press starts the charge, no sprint), <see cref="Rush.StartYaw"/> (W + A / W + D turn it up to <see cref="RushSpec.SideSteer"/>) and
/// <see cref="Rush.DashYaw"/> (the dash attack's lunge goes the stick's way of eight). The body is drawn at the simulation's heading through every charge
/// phase (<c>BodyYawFor</c> snaps it: no turn-in-place follow, so a sideways or backward lunge never slides its forward clip sideways); the camera stays on
/// the player's view. Test aids (with <c>--bot</c>; not original behaviour):
/// <list type="bullet">
/// <item><c>--bot-charge-dirs</c>: the bot's charge (<see cref="BotRushInput"/>) runs with W only (no sprint) for <see cref="ChargeDirsRunUp"/> seconds, turned
/// away from its target (so no melee lock steers it), and presses F with W, W + A, W + D in turn;</item>
/// <item><c>--bot-dash-dirs</c> (with <c>--bot-dash-attack</c>): the dash attack's melee press holds the stick in each of the eight directions in turn;</item>
/// </list>
/// and the own charge's log: the heading against the view at the press and the measured travel when the phase ends, with the prediction corrections.
/// </summary>
public partial class NetworkView
{
    bool BotChargeDirs => _botChargeDirs ??= OS.GetCmdlineUserArgs().Contains("--bot-charge-dirs");
    bool? _botChargeDirs;
    bool BotDashDirs => _botDashDirs ??= OS.GetCmdlineUserArgs().Contains("--bot-dash-dirs");
    bool? _botDashDirs;
    /// <summary>Seconds the <c>--bot-charge-dirs</c> bot runs with W (no sprint) before F (test aid, ours).</summary>
    const float ChargeDirsRunUp = 0.5f;
    static readonly (float X, string Name)[] ChargeDirs = [(0f, "W"), (-1f, "W+A"), (1f, "W+D")];
    static readonly (float X, float Y, string Name)[] DashDirs =
        [(0f, 1f, "W"), (1f, 1f, "W+D"), (1f, 0f, "D"), (1f, -1f, "S+D"), (0f, -1f, "S"), (-1f, -1f, "S+A"), (-1f, 0f, "A"), (-1f, 1f, "W+A")];
    int _botChargeDir, _botDashDir;
    double _botChargeRunSince = -1;

    // ---- the own charge's travel log ----
    RushPhase _travelPhase;
    System.Numerics.Vector3 _travelFrom, _travelLast;
    float _travelView, _travelHeading, _travelLastYaw, _travelPressSpeed;
    string _travelKeys = "";
    int _travelCorrectionsFrom;
    float? _pressView;
    float _pressSpeed;
    string _pressKeys = "";

    /// <summary>A bot press of a charge: the view, keys and speed along the view, for the travel log.</summary>
    void NotePress(float view, string keys, float speed) { _pressView = view; _pressKeys = keys; _pressSpeed = speed; }

    /// <summary>
    /// Each frame (bots only): the own bull rush's and dash attack's lunge: the heading against the view at the press when it starts; when it ends, the
    /// travel from its first to its last charging state (a wall slide shows as another angle) and the prediction corrections sampled during it.
    /// </summary>
    void ChargeTravelFrame(PlayerState me)
    {
        if (!_bot || _config is null || _client is null) return;
        var phase = me.Alive && Rush.Charging(me.Rush) ? me.Rush : RushPhase.None;
        if (phase != RushPhase.None && _travelPhase != phase)
        {
            _travelFrom = me.Position; _travelHeading = me.Yaw; _travelView = _pressView ?? _sampledYaw; _travelKeys = _pressView is null ? "?" : _pressKeys;
            _travelPressSpeed = _pressView is null ? float.NaN : _pressSpeed;
            _travelCorrectionsFrom = _client.CorrectionErrors.Count;
            _pressView = null;
            GD.Print($"t {_connectedFor:F2} s: id {_client.PlayerId} {(phase == RushPhase.Rush ? "charge" : "dash attack")} ({_travelKeys}) starts at {me.Position.X:F1},{me.Position.Y:F1}: heading " +
                     $"{Degrees(_travelHeading - _travelView):F1} deg from the view (moving {_travelPressSpeed:F2} m/s along the view at the press)");
        }
        else if (phase == RushPhase.None && _travelPhase != RushPhase.None)
        {
            var d = new System.Numerics.Vector2(_travelLast.X - _travelFrom.X, _travelLast.Y - _travelFrom.Y);
            float travel = d.LengthSquared() > 1e-4f ? MeleeLock.YawOf(d) : _travelHeading;
            var errors = _client.CorrectionErrors;
            int from = Math.Min(_travelCorrectionsFrom, errors.Count);
            int moved = 0; float worst = 0f;
            for (int k = from; k < errors.Count; k++) { worst = MathF.Max(worst, errors[k]); if (errors[k] > 1e-3f) moved++; }
            GD.Print($"t {_connectedFor:F2} s: id {_client.PlayerId} {(_travelPhase == RushPhase.Rush ? "charge" : "dash attack")} ({_travelKeys}) ended in {me.Rush}: travelled {d.Length():F2} m at " +
                     $"{Degrees(travel - _travelView):F1} deg from the view (heading {Degrees(_travelHeading - _travelView):F1} at the start, {Degrees(_travelLastYaw - _travelView):F1} at the end); " +
                     $"prediction corrections during it: {errors.Count - from} sampled, {moved} above 1 mm, max {worst:F3} m");
        }
        if (phase != RushPhase.None) { _travelLast = me.Position; _travelLastYaw = me.Yaw; }
        _travelPhase = phase;

        static float Degrees(float radians) => MeleeLock.Delta(0f, radians) * 180f / MathF.PI;
    }
}
