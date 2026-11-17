using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Grenade effects pass (owner, 2026-11-17; protocol 83): a grenade thrown with Master-Crafted Wargear shows and sounds the <c>_upgraded</c> blueprint's (blast effect scale 1.5 against 0.6, the bigger blast
/// sound), the frag blasts draw their smoke and dust again, and the camera takes the blueprint's own <c>camera_effect</c> (<see cref="GrenadeFx"/>) in place of the placeholder shake. <c>--old-grenade-fx</c>
/// restores the earlier look for all of it (the base blueprint's effects and sounds, no smoke, the placeholder shake).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--old-grenade-fx</c>: the grenades as before the effects pass.</summary>
    static readonly bool OldGrenadeFx = CmdFlag("old-grenade-fx");

    /// <summary>Whether a thrown record draws and sounds the upgraded blueprint (the wire's flag; not with the restore switch).</summary>
    bool GrenadeUpgradedFx(ProjectileView q) => q.Thrown && q.Upgraded && !OldGrenadeFx;

    /// <summary>Whether this blast effect keeps its smoke and dust (the frag grenades' only).</summary>
    static bool GrenadeKeepsSmoke(string effect) => !OldGrenadeFx && GrenadeFx.KeepsSmoke(effect);

    /// <summary>Whether the blast of this thrown record has the data's camera effect (the frag grenades; the krak and the rest keep the earlier feel).</summary>
    bool GrenadeCameraData(ProjectileView d) =>
        !OldGrenadeFx && d.Thrown && _config is not null && d.Weapon >= 0 && d.Weapon < _config.Thrown.Count && GrenadeFx.IsFrag(_config.Thrown[d.Weapon].Name);

    double _grenadeJoltAt = -1;
    float _grenadeJoltStrength;
    /// <summary>Blasts whose camera effect reached the player (test aid and the proof).</summary>
    internal int GrenadeJolts;

    /// <summary>Starts the camera effect of a blast <paramref name="distance"/> metres away (nothing beyond its attenuation).</summary>
    void GrenadeJolt(float distance)
    {
        float strength = GrenadeFx.Strength(distance);
        if (strength <= 0f) return;
        GrenadeJolts++;
        if (strength < _grenadeJoltStrength && _connectedFor - _grenadeJoltAt < GrenadeFx.Duration) return;   // a stronger one is still running
        _grenadeJoltAt = _connectedFor;
        _grenadeJoltStrength = strength;
    }

    /// <summary>The camera offset of the running camera effect (right, up in metres; zero when none).</summary>
    Vector3 GrenadeJoltOffset()
    {
        if (_grenadeJoltAt < 0) return Vector3.Zero;
        var (r, u) = GrenadeFx.Offset((float)(_connectedFor - _grenadeJoltAt), _grenadeJoltStrength);
        return new Vector3(r, u, 0f);
    }

    // ---- --bot-grenade-demo (capture aid for the reference comparison; not original behaviour) ----

    /// <summary>
    /// <c>--bot-grenade-demo &lt;s&gt;</c> (capture aid, grenade-effects proof; with <c>--bot --equipment "frag grenade"</c>, optionally <c>--perks equipment_effectiveness</c>): a standing bot with the human follow camera throws its grenade
    /// <c>&lt;s&gt;</c> seconds after joining, at the pitch <c>--bot-grenade-pitch</c> (radians, default -0.12), and stands there to watch it go off.
    /// </summary>
    double GrenadeDemoAt => _grenadeDemoAt ??= FloatArg("bot-grenade-demo") is { } v ? v : -1.0;
    double? _grenadeDemoAt;
    bool GrenadeDemo => GrenadeDemoAt >= 0.0;
    bool _grenadeDemoThrown;

    PlayerInput GrenadeDemoInput(uint tick)
    {
        _pitch = FloatArg("bot-grenade-pitch") ?? -0.12f;
        bool press = !_grenadeDemoThrown && _connectedFor >= GrenadeDemoAt;
        if (press) { _grenadeDemoThrown = true; GD.Print($"t {_connectedFor:F2} s: grenade demo: throw"); }
        return new PlayerInput(tick, 0f, 0f, _yaw, press ? InputButtons.Grenade : InputButtons.None, _pitch);
    }
}
