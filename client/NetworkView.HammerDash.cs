using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// Install transitions, pass 2 batch 2 (2026-11-25; client only, no simulation, wire or protocol change): a jump-pack marine with the thunder hammer or the daemon maul in both hands dashes
/// with the hammer graph's clips (<c>MP_SM_MEL_ThunderHammer</c> "Thunderhammer Jumppack Evade" <c>SM_JMP_TH_Dash_B/F/L/R</c>, "Jump TH Dash Land" <c>SM_JMP_TH_Dash_Landing_B/F/L/R</c>), in
/// place of the base graph's plain ones the simulation reads (<see cref="DashClipChoice"/>). Same lengths and landing branch times, so the clip is posed at the simulation's time, unstretched.
/// Which family: <see cref="DashFamilyLatch"/>, from the stance before the press. The stance is read on the player's last frame that is neither evading nor taking off (the ground tap's
/// press tick already lowers the weapon, the evading ticks keep it down): the hammer stance (<see cref="HammerStance"/>, the hammer graph active) gives the hammer's clips, the pistol up
/// the base graph's plain dash. Take-off frames: the own record's <c>JumpPhase.TakeOff</c>; a remote or spectated player's slim record has no take-off phase, so
/// <see cref="DashFamilyLatch.SlimTakingOff"/> reads it from the jump and ground flags. A dropped snapshot or a slow frame keeps the stance last read. The family holds for the flight and
/// the landing and across a chained dash (a quick re-press at the landing keeps the first dash's stance), and clears when the evade ends (<see cref="TrackEvade"/>) and when the player
/// leaves. Own, remote and spectated players get the same family. <c>--no-extra-transitions</c> restores the plain clips; <c>--log-dash</c> prints each dash's family (own and seen)
/// and each change of a jump-pack player's dash stance.
/// </summary>
public partial class NetworkView
{
    readonly DashFamilyLatch _dashLatch = new();
    readonly HashSet<int> _dashLandLogged = new();
    readonly Dictionary<int, DashFamily> _dashStanceLogged = new();   // --log-dash: the stance family last logged per player
    readonly Dictionary<string, bool> _rigHasDashClip = new(StringComparer.OrdinalIgnoreCase);
    static bool? _logDash;
    static bool LogDash => _logDash ??= OS.GetCmdlineUserArgs().Contains("--log-dash");

    /// <summary>The player's stance alias for the dash: the aliased melee wargear in the hammer stance, else null.</summary>
    string? DashStanceAlias(PlayerState s) => HammerStance(s) ? CarriedAlias(s) : null;

    /// <summary>A frame on which the player is not evading (<see cref="TrackEvade"/>): remember the stance, drop the latch.</summary>
    void DashGround(PlayerState s, bool own)
    {
        // A take-off frame (from the ground tap's press tick to its release) has the weapon already lowered: keep the stance from before the press (DashFamilyLatch.Ground).
        // The own (full) record carries the take-off phase; others' slim records read every jumping phase as the ascent (DashFamilyLatch.SlimTakingOff).
        bool takingOff = own
            ? s.Jump == JumpPhase.TakeOff
            : _dashLatch.SlimTakingOff(s.PlayerId, JumpPack.Jumping(s.Jump), s.Grounded, _connectedFor, (_config is null ? null : Loadouts.JumpPack(_config, s))?.TakeOffDelay ?? 0.2f);
        string? stance = _config is null ? null : DashStanceAlias(s);
        _dashLatch.Ground(s.PlayerId, stance, takingOff);
        if (LogDash && !takingOff && s.Alive && _config is not null && Loadouts.JumpPack(_config, s) is not null)
        {
            var family = DashClipChoice.Family(stance, NoExtraTransitions);
            if (!_dashStanceLogged.TryGetValue(s.PlayerId, out var last) || last != family)
            {
                _dashStanceLogged[s.PlayerId] = family;
                GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} ({(own ? "own" : "seen")}) dash stance now {family}");
            }
        }
        _dashLandLogged.Remove(s.PlayerId);
    }

    /// <summary>The player left: forget its dash stance and latch.</summary>
    void DashForget(int playerId) { _dashLatch.Forget(playerId); _dashLandLogged.Remove(playerId); _dashStanceLogged.Remove(playerId); }

    /// <summary>The dash clip to pose for <paramref name="clip"/> (the simulation's evade clip): the hammer graph's for a dash begun in the hammer stance, else <paramref name="clip"/>.</summary>
    string DashClipFor(PlayerState s, string clip)
    {
        if (DashClipChoice.HammerClip(clip) is null || _config is null) return clip;   // not a dash clip: rolls, hops, the Ork's side hop
        var family = _dashLatch.During(s.PlayerId, DashStanceAlias(s), NoExtraTransitions, out bool latchedNow);
        string posed = DashClipChoice.Clip(clip, family, DashRigHas);
        if (LogDash && latchedNow) GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} ({(_client is not null && s.PlayerId == _client.PlayerId ? "own" : "seen")}) DASH family {family} {s.EvadeDirection}: {posed}");
        if (LogDash && posed != clip)
        {
            if (latchedNow) GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} HAMMER DASH {s.EvadeDirection}: {posed} in place of {clip}");
            if (s.EvadeLanded && _dashLandLogged.Add(s.PlayerId)) GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} HAMMER DASH landing: {posed}");
        }
        return posed;
    }

    // ---- --dash-dirs-test (capture aid with --bot; not original behaviour) ----
    static bool? _dashDirsTest;
    static bool DashDirsTest => _dashDirsTest ??= OS.GetCmdlineUserArgs().Contains("--dash-dirs-test");
    static readonly (float X, float Y, string Name)[] DashTestDirs = [(0f, 1f, "W"), (1f, 0f, "D"), (-1f, 0f, "A"), (0f, -1f, "S")];
    int _dashTestDir;
    double _dashTestNext = 5.0;
    (float X, float Y) _dashTestStick;

    /// <summary>
    /// <c>--dash-dirs-test</c>: a jump-pack bot stands at its spawn with its view fixed and, every 2.5 s while on the ground and free, taps Jump with the stick forward, right, left and back in
    /// turn (the dash, landing included; the four dashes trace a path back to the start, so the back dash runs over ground already crossed); no stick during the dash. Null for a class without a jump pack or without the switch (the bot's other behaviour).
    /// </summary>
    PlayerInput? DashDirsTestInput(PlayerState me, uint tick)
    {
        if (!DashDirsTest || _config is null || Loadouts.JumpPack(_config, me) is null || !me.Alive) return null;
        // --dash-aim: every other dash (the 1st, 3rd, ...) is pressed with Aim held from 1 s before the press until the dash starts (the pistol up before the press: the plain dash).
        bool aimThis = DashAimTest && _dashTestDir % 2 == 0;
        var aim = aimThis && _connectedFor >= _dashTestNext - 1.0 ? InputButtons.Aim : InputButtons.None;
        var idle = new PlayerInput(tick, 0f, 0f, _yaw, Evade.Evading(me) ? InputButtons.None : aim, 0f);
        if (Evade.Evading(me)) return idle;
        if (me.Jump == JumpPhase.TakeOff) return new PlayerInput(tick, _dashTestStick.X, _dashTestStick.Y, _yaw, _dashTestAimed ? InputButtons.Aim : InputButtons.None, 0f);   // released: the tap is the dash
        if (_connectedFor < _dashTestNext || !me.Grounded || me.Jump != JumpPhase.None || me.EvadeLock > 0 || me.RushLock > 0 || me.MeleeStep > 0 || me.Throwing || me.JumpHeld) return idle;
        _dashTestNext = _connectedFor + 2.5;
        _dashTestAimed = aimThis;
        var (x, y, keys) = DashTestDirs[_dashTestDir++ % DashTestDirs.Length];
        _dashTestStick = (x, y);
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} dash-dirs-test: dash {keys}{(aimThis ? " (aiming: pistol up)" : "")}");
        return new PlayerInput(tick, x, y, _yaw, InputButtons.Jump | aim, 0f);
    }
    static bool? _dashAimTest;
    static bool DashAimTest => _dashAimTest ??= OS.GetCmdlineUserArgs().Contains("--dash-aim");
    bool _dashTestAimed;

    bool DashRigHas(string clip) => _rigHasDashClip.TryGetValue(clip, out var has) ? has : _rigHasDashClip[clip] = RigHasClip(clip);
}
