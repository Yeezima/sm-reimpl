using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// Aim poses in the networked view (protocol 12 carries <see cref="PlayerState.Aiming"/>, the held aim mode, for every
/// player). Normal play shows the original relaxed navigation clips (<c>SM_NAV_Run_F_01</c> / idle, weapon held low); a marine
/// in aim mode shows the weapon raised, with the original aim navigation clips of the weapon class, cross-faded over
/// <see cref="CharacterViewer.StanceFadeSeconds"/> and bent up and down with the view pitch.
/// <para>
/// The rig has aim clips in three classes, named after the original behavior graphs <c>SM_Bolter_NAV</c> (rifles and everything
/// not listed), <c>SM_Pistol_NAV</c> (<c>SM_1HR_NAV_*</c>, one-handed ranged: pistols) and <c>SM_TB_NAV</c> (<c>SM_NAV_TB_*</c>:
/// heavy bolter, plasma cannon, lascannon). Each aim position is a triple of looping clips (aim down / level / aim up) of equal
/// length; the graphs choose among them by the aim pitch, so the pose here is the pitch-weighted blend of the neighbouring two
/// (<see cref="CharacterViewer.Stance"/>). Rifle: the graph's <c>SequencerGenerator03</c> ("Focus": <c>IdleLFocus_S_Aim*</c> standing,
/// <c>WalkFocus_&lt;dir&gt;_Aim*</c> moving, eight directions); the plain <c>SM_NAV_Walk_*_Aim*</c> / <c>Run_*_Aim*</c> clips of the
/// same graph (the stance after firing) are not used. Pistol: <c>1HR_NAV_IdleL_S_Aim*</c> and <c>Walk_&lt;dir&gt;_Aim*</c>. Heavy:
/// <c>TB_Idle_S_Aim*</c> and <c>TB_Walk_&lt;F|B|L|R&gt;_Aim*</c> (diagonals use the nearer of F / B). The simulation's aim speeds
/// are walking speeds, so only the walk and standing clips are used. A marine that stands still while aiming (below
/// <see cref="AimStandBelow"/> m/s, with hysteresis up to <see cref="AimWalkAbove"/>) shows the idle aim pose, feet planted; the
/// "walk on the spot" (<c>WOS</c>) clips step in place and are not used. Moving, the walk clip plays at the rate that matches
/// its planted-foot speed to the movement speed (<see cref="AimWalkSpeed"/>). The clips are whole-body (the playback system
/// has no upper-body layering), so aiming replaces the legs too.
/// </para>
/// Original clips; which of them show when, the pitch range the down/up clips are taken to cover (+-70 degrees: the hand axis
/// of the rifle's Focus clips measures -72 / +72 degrees, and the aim cameras' pitch limits are 5 to 175 of 180 rig degrees)
/// and the fades: **interpretation / placeholder demonstration**.
/// </summary>
public partial class NetworkView
{
    /// <summary>Pitch (radians) at which the aim-down / aim-up clips are shown in full (client choice, see the class comment).</summary>
    const float AimPitchRange = 70f * MathF.PI / 180f;
    /// <summary>Horizontal speed (m/s) above which an aiming marine shows the aim-walk clips, and below which it goes back to the standing aim pose (hysteresis; client choice, user feedback: stand still when not moving).</summary>
    const float AimWalkAbove = 0.5f, AimStandBelow = 0.3f;
    /// <summary>
    /// Ground speed (m/s) at which each class's aim-walk clips are authored: the speed of the planted foot (<c>--clip-stats</c>:
    /// rifle SM_NAV_WalkFocus_* about 2.4, pistol SM_1HR_NAV_Walk_* and heavy SM_NAV_TB_Walk_* about 1.7). The playback rate is the
    /// movement speed over this, limited to 0.5..1.25 so it stays a walk (the pistol and heavy clips, authored for about 1.7 m/s, then slide a little at the simulation's 3 m/s). Measured from the clips; the use is **interpretation**.
    /// </summary>
    static readonly float[] AimWalkSpeed = [2.4f, 1.7f, 1.7f];
    readonly Dictionary<int, bool> _aimMoving = new();   // per player: in the aim-walk state (hysteresis)
    /// <summary>Weapon classes that have aim clips.</summary>
    enum AimClass : byte { Rifle, Pistol, Heavy }

    /// <summary>The three clips of one aim position (null: not in the rig).</summary>
    sealed record AimTriple(string? Down, string Level, string? Up);

    readonly Dictionary<string, AimTriple> _aimStances = new();
    int _aimClipsInRig;
    string _aimNowText = "";

    static readonly string[] Dirs8 = ["F", "FR", "R", "BR", "B", "BL", "L", "FL"];   // index = round(angle from forward, clockwise, / 45 degrees)
    // Stance keys built once (the per-frame choice must not allocate): [class][0 = standing, 1 + sector = walking toward Dirs8[sector]].
    static readonly string[][] AimKeys = new[] { "rifle", "pistol", "heavy" }.Select(p => new[] { p + ":wos" }.Concat(Dirs8.Select(d => p + ":walk:" + d)).ToArray()).ToArray();
    readonly Dictionary<string, string> _normalKeys = new();

    /// <summary>
    /// Resolves the aim stances against the rig (clips that are missing or cannot be decoded are noted and the stance is left
    /// out, so that class and direction show the normal pose) and logs the result once.
    /// </summary>
    void SetUpAim()
    {
        var names = _assets!.Model.Rig.AnimationNames;
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        _aimClipsInRig = names.Count(n => n.Contains("aim", StringComparison.OrdinalIgnoreCase));
        string? Find(params string[] candidates)
        {
            foreach (var c in candidates)
                if (set.Contains(c) && names.First(n => n.Equals(c, StringComparison.OrdinalIgnoreCase)) is { } exact && _assets.Animation(exact) is not null) return exact;
            return null;
        }
        void Add(string key, string?[] down, string[] level, string?[] up)
        {
            var d = Find(down.Where(x => x is not null).ToArray()!);
            var l = Find(level);
            var u = Find(up.Where(x => x is not null).ToArray()!);
            if (l is null) { _notes.Add($"aim stance {key}: level clip {level[0]} not in the rig; that stance shows the normal pose"); return; }
            if (d is null || u is null) _notes.Add($"aim stance {key}: {(d is null ? "aim-down" : "aim-up")} clip not in the rig; its pitch side shows the level clip");
            _aimStances[key] = new AimTriple(d, l, u);
        }
        // Standing: the idle aim clips (feet planted; measured with --clip-stats: foot travel 0.00 m). The WOS ("walk on the spot")
        // clips step in place (feet lift ~10 cm every 1.33 s) and looked like walking while standing still, so they are not used.
        Add("rifle:wos", ["SM_NAV_IdleLFocus_S_AimDown_01"], ["SM_NAV_IdleLFocus_S_AimForward_01"], ["SM_NAV_IdleLFocus_S_AimUp_01"]);
        Add("pistol:wos", ["SM_1HR_NAV_IdleL_S_AimDown_01"], ["SM_1HR_NAV_IdleL_S_Aim_F_01"], ["SM_1HR_NAV_IdleL_S_AimUp_01"]);
        Add("heavy:wos", ["SM_NAV_TB_Idle_S_AimDown_01"], ["SM_NAV_TB_Idle_S_AimFoward_01"], ["SM_NAV_TB_Idle_S_AimUp_01"]);
        foreach (var d in Dirs8)
        {
            Add($"rifle:walk:{d}", [$"SM_NAV_WalkFocus_{d}_AimDown_01"], [$"SM_NAV_WalkFocus_{d}_Aim_F_01"], [$"SM_NAV_WalkFocus_{d}_AimUp_01"]);
            Add($"pistol:walk:{d}", [$"SM_1HR_NAV_Walk_{d}_AimDown_01", $"SM_1HR_NAV_Walk_{d}_AimD_01"], [$"SM_1HR_NAV_Walk_{d}_AimF_01"],
                [$"SM_1HR_NAV_Walk_{d}_AimUp_01", $"SM_1HR_NAV_Walk_{d}_AimU_01"]);
        }
        foreach (var d in new[] { "F", "R", "B", "L" })
            Add($"heavy:walk:{d}", [$"SM_NAV_TB_Walk_{d}_AimDown_01"], [$"SM_NAV_TB_Walk_{d}_AimForward_01"], [$"SM_NAV_TB_Walk_{d}_AimUp_01"]);

        int Count(string prefix) => _aimStances.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
        float Length(string stance) => _aimStances.TryGetValue(stance, out var t) && _assets.Animation(t.Level) is { } a ? a.Animation.Duration : 0f;
        GD.Print($"  aim poses (original clips; choice: interpretation): rig has {_aimClipsInRig} clips with 'aim' in the name; " +
                 $"stances found: rifle {Count("rifle:")}/9 (standing {_aimStances.GetValueOrDefault("rifle:wos")?.Level ?? "none"}, {Length("rifle:wos"):F2} s; walking SM_NAV_WalkFocus_<dir>_Aim*, {Length("rifle:walk:F"):F2} s), " +
                 $"pistol {Count("pistol:")}/9 (SM_1HR_NAV_*, {Length("pistol:wos"):F2} s / {Length("pistol:walk:F"):F2} s), " +
                 $"heavy {Count("heavy:")}/5 (SM_NAV_TB_*, {Length("heavy:wos"):F2} s / {Length("heavy:walk:F"):F2} s); each a down / level / up triple blended by pitch over +-{AimPitchRange * 180f / MathF.PI:F0} degrees, {CharacterViewer.StanceFadeSeconds:F2} s fades");
    }

    /// <summary>The aim class of the weapon in hand, by the original wargear name (the camera table's grouping: pistols, the heavy weapons, the rest); null without a ranged weapon.</summary>
    AimClass? AimClassOf(PlayerState s)
    {
        if (Loadouts.InHand(_config!, s)?.Name is not { } name) return null;
        if (name.Contains("pistol", StringComparison.OrdinalIgnoreCase)) return AimClass.Pistol;
        if (name.Contains("heavy_bolter", StringComparison.OrdinalIgnoreCase) || name.Contains("plasma_cannon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("las_cannon", StringComparison.OrdinalIgnoreCase) || name.Contains("lascannon", StringComparison.OrdinalIgnoreCase)) return AimClass.Heavy;
        return AimClass.Rifle;
    }

    /// <summary>
    /// The aim stance for a marine in aim mode: the key, clips and pitch position for <see cref="CharacterViewer.Stance"/>, or
    /// null when it is not aiming (or has no stance for its weapon class / direction). Standing below <see cref="AimStandBelow"/> (see the class comment);
    /// moving, the direction of its velocity against its facing (eight sectors; the heavy class has four).
    /// </summary>
    (string Key, string? Low, string Mid, string? High, float PitchT, float Rate)? AimStance(PlayerState s)
    {
        if (!s.Alive || !s.Aiming || AimClassOf(s) is not { } cls) { _aimMoving.Remove(s.PlayerId); return null; }
        var keys = AimKeys[(int)cls];
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        bool moving = _aimMoving.GetValueOrDefault(s.PlayerId);
        moving = moving ? speed > AimStandBelow : speed > AimWalkAbove;
        _aimMoving[s.PlayerId] = moving;
        int slot = 0;
        float rate = 1f;
        if (moving)
        {
            rate = Math.Clamp(speed / AimWalkSpeed[(int)cls], 0.5f, 1.25f);
            float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
            float forward = s.Velocity.X * fx + s.Velocity.Y * fy, right = s.Velocity.X * fy - s.Velocity.Y * fx;
            int sector = cls == AimClass.Heavy
                ? (MathF.Abs(forward) >= MathF.Abs(right) ? (forward >= 0f ? 0 : 4) : (right >= 0f ? 2 : 6))
                : ((int)MathF.Round(MathF.Atan2(right, forward) / (MathF.PI / 4f)) % 8 + 8) % 8;
            slot = 1 + sector;
        }
        string key = keys[slot];
        if (!_aimStances.TryGetValue(key, out var t)) { key = keys[0]; if (!_aimStances.TryGetValue(key, out t)) return null; }
        return (key, t.Down, t.Level, t.Up, Math.Clamp(s.Pitch / AimPitchRange, -1f, 1f), key == keys[0] ? 1f : rate);
    }

    /// <summary>
    /// The pose of a marine not under melee, jump or throw: the aim stance when it is aiming, else the ready posture (M27), else the sprint or
    /// the relaxed run of its movement direction above <see cref="RunThreshold"/> (M36, <c>NetworkView.Locomotion.cs</c>) or idle, as a stance so
    /// aiming fades in and out of it. Falls back to the forward run clip or a plain looping clip when the data or stances cannot be used.
    /// </summary>
    void PlaceLocomotionCore(CharacterViewer character, PlayerState s, bool own)
    {
        if (own && HipBlendActive) return;   // the hip-fire blend probe drives this character (--hip-probe)
        _standFade = 0f;
        s = s with { Yaw = BodyYawOf(s) };   // M40-T5 (NetworkView.TurnInPlace.cs): the poses compare the velocity with the shown body's heading
        var hip = HipOf(s.PlayerId);   // M27 (NetworkView.HipFire.cs): ready posture after a shot, recoil per shot
        if (AimStance(s) is { } aim && character.Stance(aim.Key, aim.Low, aim.Mid, aim.High, aim.PitchT, aim.Rate))
        {
            hip.Kind = PoseKind.Aim;
            HipRecoil(character, s, hip);
            // The text is for the F1 overlay only (built when it is shown, for the own player).
            if (own) _aimNowText = _showInfo ? $"{aim.Key} pitch {s.Pitch * 180f / MathF.PI:F0} deg, speed {MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y):F1} m/s, clip rate {aim.Rate:F2} ({aim.Mid})" : "";
            return;
        }
        if (RunStartPlace(character, s, hip, own)) { if (own) _aimNowText = ""; HipRecoil(character, s, hip); return; }   // M40-T5 (NetworkView.RunStart.cs): the start clip, driven by the distance travelled
        if (!NoHipFire && HipData() is not null && HipReady(s) && HipStancePlace(character, s, hip))
        {
            if (own) _aimNowText = "";
            HipRecoil(character, s, hip);
            return;
        }
        if (own) _aimNowText = _showInfo && s.Aiming && s.Alive ? "aim mode, but no aim clips for this weapon: normal pose" : "";
        // M36 (NetworkView.Locomotion.cs): the sprint loop, or the relaxed run of the movement direction (backpedal and strafes included).
        if (LocoPlace(character, s, hip, own)) { HipRecoil(character, s, hip); return; }
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        // M40-T5: standing in a posture with turn data: the aim-offset idle, the turn clip on the legs (NetworkView.TurnInPlace.cs).
        float standFade = _standFade > 0f && _fadeOverride <= 0f ? _standFade : HipRelaxFade(s, hip);   // qc/reference-pass: the stop blend when the run's blend let go this frame
        _standFade = 0f;
        if (speed <= RunThreshold && TurnStandPlace(character, s, hip, ready: false, standFade)) { HipRecoil(character, s, hip); return; }
        string clip = speed > RunThreshold || IdleClipOf(s) is not { } idleClip ? CharacterViewer.DefaultAnimation : idleClip;   // animation pass 3: the pistol's relaxed idle (NetworkView.SprintEdges.cs)
        if (!_normalKeys.TryGetValue(clip, out var normalKey)) _normalKeys[clip] = normalKey = "normal:" + clip;
        if (_aimStances.Count == 0 || !character.Stance(normalKey, null, clip, null, 0f, 1f, HipRelaxFade(s, hip))) character.Play(clip);
        hip.Kind = PoseKind.Normal;
        HipRecoil(character, s, hip);
    }

    /// <summary>The F1 info line on aim poses.</summary>
    string AimInfoLine() =>
        $"Aim poses: original clips (rifle SM_NAV_*Focus*, pistol SM_1HR_NAV_*, heavy SM_NAV_TB_*; standing = idle aim clips with the feet planted, aim-walk clips at the movement speed only above 0.5 m/s; {_aimStances.Count} stances, each a down / level / up triple blended by pitch over +-{AimPitchRange * 180f / MathF.PI:F0} degrees, {CharacterViewer.StanceFadeSeconds:F2} s fades, whole-body); choice: interpretation." +
        (_aimNowText.Length > 0 ? " Now: " + _aimNowText : " Now: off (hold Left Ctrl).");
}
