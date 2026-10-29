using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// feel/camera-hitstop (the owner's playtest, 2026-10-29: "when i back up into a wall the camera gets really close to the character and i can see his jump
/// pack thrusters really close up to the camera. the entire character should go invisible during this time"): while the camera is pulled in closer to the own
/// head than <see cref="HideDistanceNow"/> the **whole** own character is hidden: the model with the armour, the weapons in hand and stowed, the worn backpack and
/// jump pack (children of the character, hidden with it), and the jump pack's thrust, idle glow and one-shot effects (<see cref="FxEffect.Hidden"/>: effects are
/// drawn from the effect player, not under the character, so hiding the model alone left them, and the pack's glow filled the lens). The Chaos eye glow already follows the
/// character's visibility. Own muzzle flashes, casings and tracers stay (they are ahead of the camera and the shooter's feedback). Hysteresis, not a fade (a hide
/// with a dither or alpha fade over the painted shader and the effects is not cheap): the character is shown again at 1.15 x the distance.
/// <para>
/// The distance is ours (the original's camera collision is not decoded: the camera rig data has no collision offset): the exploration camera sits 3.5 m
/// from the head and the aim camera 2.0 m (measured with <c>--camera-hide-log</c>), so the limit sits well inside the shoulder camera and far enough that
/// the pack (about 0.6 m deep behind the head) is never closer to the lens than about 0.8 m while shown (a camera held 1.29 m from the head by a wall showed the
/// pauldron filling the lens with the old 1.0 m limit). <c>--camera-hide-distance &lt;m&gt;</c> overrides it; <c>--old-camera-hide</c> restores the M19 rule (1.0 m, the
/// model only, the pack effects stay) for before / after captures.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>Ours (tagged in the refine register): the camera-to-head distance in metres below which the whole own character is hidden (<see cref="SmNet.CameraHideRules"/>). Before 2026-10-29: 1.0 (model only).</summary>
    const float CharacterHideDistance = SmNet.CameraHideRules.HideDistance;
    const float CharacterShowFactor = SmNet.CameraHideRules.ShowFactor;

    static readonly bool OldCameraHide = CmdFlag("old-camera-hide");
    static readonly float HideDistanceOverride = CmdFloat("camera-hide-distance");

    /// <summary>Whether the own character is hidden because the camera is too close (read by the effects that belong to it).</summary>
    bool _ownHidden;
    /// <summary>The own jump pack's effects (live ones), so they can be hidden with the character.</summary>
    readonly List<FxEffect> _ownPackFx = new();

    float HideDistanceNow => OldCameraHide ? CameraHideDistance : float.IsFinite(HideDistanceOverride) && HideDistanceOverride > 0f ? HideDistanceOverride : CharacterHideDistance;

    /// <summary>Hides or shows the own character for a camera <paramref name="closest"/> metres from the head (hysteresis on the current state).</summary>
    void UpdateOwnHidden(float closest)
    {
        if (_local is null) return;
        float limit = HideDistanceNow;
        bool hidden = SmNet.CameraHideRules.Hidden(_ownHidden, closest, limit);
        if (_bot && (BotBackup || HideLog) && _connectedFor - _hideLogAt >= 0.25) { _hideLogAt = _connectedFor; GD.Print($"t {_connectedFor:F2} s: camera {closest:F2} m from the head (hide below {limit:F2}, back out at {limit * CharacterShowFactor:F2}): own character {(hidden ? "HIDDEN" : "shown")}; pack effects hidden {_ownPackFx.Count(e => e.Hidden)} of {_ownPackFx.Count}"); }
        SetOwnHidden(hidden);
    }
    double _hideLogAt = double.NegativeInfinity;

    /// <summary>Shows or hides the whole own character (model and pack effects).</summary>
    void SetOwnHidden(bool hidden)
    {
        _ownHidden = hidden;
        if (_local is not null) _local.Visible = !hidden;
        OwnPackFxHide();
    }

    /// <summary>Sets <see cref="FxEffect.Hidden"/> on the own pack's effects (and forgets the ended ones).</summary>
    void OwnPackFxHide()
    {
        bool hide = _ownHidden && !OldCameraHide;
        for (int i = _ownPackFx.Count - 1; i >= 0; i--)
        {
            var e = _ownPackFx[i];
            if (e.Dead) { _ownPackFx.RemoveAt(i); continue; }
            e.Hidden = hide;
        }
    }

    /// <summary><c>--bot-backup</c> (test aid, with <c>--aim-test</c>): the bot stands still for 7 s, then walks backwards (see <c>--bot-backup-yaw</c>) until it stands against a wall (captures of the camera pulled in).</summary>
    static readonly bool BotBackup = CmdFlag("bot-backup");
    /// <summary><c>--camera-hide-log</c> (test aid, bots): prints the camera-to-head distance and the hide state four times a second.</summary>
    static readonly bool HideLog = CmdFlag("camera-hide-log");

    /// <summary><c>--bot-backup-yaw &lt;degrees&gt;</c>: turns the bot's view by this much at 5 s, so it backs into the wall at an angle (the camera then stops at other distances).</summary>
    static readonly float BotBackupYaw = CmdFloat("bot-backup-yaw");
    bool _backupTurned;

    PlayerInput BotBackupInput(uint tick)
    {
        if (!_backupTurned && _connectedFor >= 5.0)
        {
            _backupTurned = true;
            if (float.IsFinite(BotBackupYaw)) _yaw += BotBackupYaw * MathF.PI / 180f;
        }
        float y = _connectedFor >= 7.0 ? -1f : 0f;
        return new PlayerInput(tick, 0f, y, _yaw, InputButtons.None, _pitch);
    }

    static float CmdFloat(string name)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
    }
}
