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
/// The family is latched when the dash begins (a pistol shot mid-dash does not swap the clip): the hammer's when the marine is in the hammer stance then (<see cref="HammerStance"/>: the
/// hammer graph is the active melee state; with the pistol up the base graph's plain dash plays). Own, remote and spectated players alike (the pose path is shared). <c>--no-extra-transitions</c>
/// restores the plain clips; <c>--log-dash</c> prints each hammer dash's clip.
/// </summary>
public partial class NetworkView
{
    sealed class DashFamilyTrack { public int Ticks; public EvadeDirection Direction; public DashFamily Family; public bool LandLogged; }
    readonly Dictionary<int, DashFamilyTrack> _dashFamilies = new();
    readonly Dictionary<string, bool> _rigHasDashClip = new(StringComparer.OrdinalIgnoreCase);
    static bool? _logDash;
    static bool LogDash => _logDash ??= OS.GetCmdlineUserArgs().Contains("--log-dash");

    /// <summary>The dash clip to pose for <paramref name="clip"/> (the simulation's evade clip): the hammer graph's for a hammer stance dash, else <paramref name="clip"/>.</summary>
    string DashClipFor(PlayerState s, string clip)
    {
        if (DashClipChoice.HammerClip(clip) is null || _config is null) return clip;   // not a dash clip: rolls, hops, the Ork's side hop
        bool restart = !_dashFamilies.TryGetValue(s.PlayerId, out var t) || s.EvadeTicks < t.Ticks || s.EvadeDirection != t.Direction;
        t ??= _dashFamilies[s.PlayerId] = new DashFamilyTrack();
        if (restart)
        {
            t.LandLogged = false;
            t.Family = DashClipChoice.Family(HammerStance(s) ? CarriedAlias(s) : null, NoExtraTransitions);
            if (LogDash && t.Family == DashFamily.Hammer)
                GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} HAMMER DASH {s.EvadeDirection}: {DashClipChoice.Clip(clip, t.Family, DashRigHas)} in place of {clip}");
        }
        t.Ticks = s.EvadeTicks; t.Direction = s.EvadeDirection;
        string posed = DashClipChoice.Clip(clip, t.Family, DashRigHas);
        if (LogDash && posed != clip && s.EvadeLanded && !t.LandLogged) { t.LandLogged = true; GD.Print($"t {_connectedFor:F1} s: P{s.PlayerId} HAMMER DASH landing: {posed}"); }
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
        var idle = new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        if (Evade.Evading(me)) return idle;
        if (me.Jump == JumpPhase.TakeOff) return new PlayerInput(tick, _dashTestStick.X, _dashTestStick.Y, _yaw, InputButtons.None, 0f);   // released: the tap is the dash
        if (_connectedFor < _dashTestNext || !me.Grounded || me.Jump != JumpPhase.None || me.EvadeLock > 0 || me.RushLock > 0 || me.MeleeStep > 0 || me.Throwing || me.JumpHeld) return idle;
        _dashTestNext = _connectedFor + 2.5;
        var (x, y, keys) = DashTestDirs[_dashTestDir++ % DashTestDirs.Length];
        _dashTestStick = (x, y);
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} dash-dirs-test: dash {keys}");
        return new PlayerInput(tick, x, y, _yaw, InputButtons.Jump, 0f);
    }

    bool DashRigHas(string clip) => _rigHasDashClip.TryGetValue(clip, out var has) ? has : _rigHasDashClip[clip] = RigHasClip(clip);
}
