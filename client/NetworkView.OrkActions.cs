using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Ork O7b / O7c (docs/ork-faction-design.md sections 5 and 8): the networked Ork Boy's actions in the boy's own clips. The simulation runs them (the marine's timings for the
/// reactions and the throw, the Ork's own for the choppa, the charge and the hop: Ork O4); this lays the boy's clips over those times, in the order <c>Place</c> uses for the
/// marines (<see cref="OrkActionPose"/>):
/// <list type="bullet">
/// <item><b>Reaction</b> (interrupt, stun, stagger, blasts): <c>BOY_REACTIONS_Coop</c>'s light and heavy hits, front and back, the clip up to its <c>BRANCH_Reaction_End</c> stretched over the
/// marine clip's time the simulation holds the player (<see cref="OrkActionRules.ReactionTime"/>; **ours**). The boy has no side hits or stun-specific set: the choices are
/// <see cref="ReactionTuning.OrkRig"/>'s (ours).</item>
/// <item><b>Charge</b>: <c>BOY_1HM_MEL_Atk_Sprint_01</c> (the choppa's sprint attack, <c>OrkClass.RushClip</c>) at the rush's time; the collision stop plays the same clip on from the hit
/// (<see cref="OrkActionRules.RushStopClipTime"/>; ours: the boy has no stop clip).</item>
/// <item><b>Melee</b>: the choppa's combo and heavy strike clips the simulation's steps were read from (<c>BOY_1HM_MEL_Cmbo_01_*</c>, the 3.5 m variants), at the attack's time; from the step's
/// <c>MoveAt</c>, while the Ork moves, the legs follow the locomotion and the clip stays on the upper body (the marines' hand-off, <see cref="ActionHandoff"/>; the boy's reload mask).</item>
/// <item><b>Dodge hop</b>: <c>BOY_2HR_NAV_Evade4m_L/R_01</c> at the hop's time, the clip played over the simulation's hop duration (<see cref="OrkActionRules.ClipRate"/>: the simulation's hop is the
/// Tactical roll's 1.167 s, the clip 2.3 to 2.6 s: parity, ours); the body is drawn lower by the hop's height so the clip's own lift shows once.</item>
/// <item><b>Throw</b>: <c>BOY_2HR_RNG_GrenadeToss</c>, warped so that the clip's release (<c>Ability_Ranged_Fire</c>, 0.711 s) is the simulation's (0.2 s) and its end the clip's <c>BRANCH_Run</c>
/// (<see cref="OrkActionRules.ThrowClipTime"/>); the stikkbomb leaves the hand at the simulation's release (O6). Moving, the toss goes on the upper body (hand-off).</item>
/// <item><b>Shot</b>: the boy's additive recoil <c>BOY_2HR_RNG_Fire_S_01</c> restarts with each shot seen (the hip-fire code's shot clock, <see cref="HipTrack"/>); the slugga uses the same clip (the boy has no
/// pistol set: ours). The ready posture and the aim pose are the aim run and the idle of O7a (the boy has no relaxed stance: the weapon is always "ready" in the graphs).</item>
/// <item><b>Reload</b>: <c>BOY_2HR_RNG_Reload_S_01</c> over the reload time with the graph's upper-body mask, the M35 machinery on the boy's weapons (NetworkView.Reload.cs); the sounds of its
/// tags are another task's (Ork sounds).</item>
/// <item><b>Death</b>: the boy's death clip for the blow (<see cref="OrkActionRules.DeathKindOf"/>) plays until the graph's <c>Pre-Powered Ragdoll</c> window ends, then the Ork's ragdoll takes the
/// pose over (<see cref="OrkDeathHold"/>; the original's powered ragdoll, which follows the clip with motors for a while, is not modelled).</item>
/// </list>
/// Not in the boy's graphs, so absent: turn in place, the spine twist and the run start. <c>--no-ork-actions</c> (test aid) leaves the Ork on its movement pose as in O7a.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-ork-actions</c> (test aid): the Ork shows only its movement pose, as before O7b / O7c (before / after runs).</summary>
    bool NoOrkActions => _noOrkActions ??= CmdFlag("no-ork-actions");
    bool? _noOrkActions;

    OrkActionPlan? _orkActions;
    readonly Dictionary<string, bool> _orkClipOk = new(StringComparer.OrdinalIgnoreCase);
    int _orkActionFrames, _orkReactionPoses, _orkRushPoses, _orkAttackPoses, _orkHopPoses, _orkThrowPoses, _orkRecoilLayers, _orkHandoffs, _orkDeathHolds;
    string _orkActionNote = "not loaded";

    /// <summary>The Ork's reload data for the M35 machinery (the boy's clip stretched to the reload time); null before the action data is read or with <c>--no-reload</c>.</summary>
    ReloadAnimations? OrkReloadData() => NoReload ? null : _orkActions?.Reload;

    /// <summary>Called when the locomotion worker's bundle is taken (<see cref="OrkPlan"/>): the action plan, and the clips the worker decoded go into the Ork rig's cache.</summary>
    void TakeOrkActions(OrkLocomotionBundle bundle)
    {
        if (NoOrkActions) { _orkActionNote = "switched off (--no-ork-actions)"; return; }
        if (bundle.Actions is null)
        {
            _orkActionNote = "not available: " + (bundle.ActionsFailure ?? "not read");
            _notes.Add("ork actions: " + _orkActionNote + "; Orks keep their movement pose");
            GD.PrintErr("ork actions: " + _orkActionNote);
            return;
        }
        _orkActions = bundle.Actions;
        if (_orks is not null) foreach (var (name, animation) in bundle.ActionAnimations) _orks.Rig.Preload(RigProfiles.OrkBoy.Entry, name, animation);
        var a = bundle.Actions;
        _orkActionNote = $"reactions {string.Join("/", a.Reactions.Select(r => r.Kind))}, reload {a.Reload?.Clips.FirstOrDefault()?.Clip ?? "none"}, recoil {a.GunRecoil?.Clip ?? "none"}, toss release {a.Throw?.ReleaseAt:F2} s, {a.Deaths.Count} death clips, {bundle.ActionAnimations.Count} clips decoded on the worker";
        GD.Print("ork actions: " + _orkActionNote);
        GD.Print($"ork worker: rig read, plan and gaits {bundle.LocomotionMilliseconds:F0} ms, action plan {bundle.ActionsMilliseconds:F0} ms, action clips decoded {bundle.DecodeMilliseconds:F0} ms (off the main thread)");
        foreach (var n in a.Notes) { GD.Print("  ork actions note: " + n); _notes.Add("ork: " + n); }
    }

    /// <summary>Whether the Ork rig has the clip and it decodes (cached per name: the rig's name list is a linear scan).</summary>
    bool OrkClipUsable(MarineAssets assets, string clip)
    {
        if (_orkClipOk.TryGetValue(clip, out bool ok)) return ok;
        ok = OrkHasClip(assets, clip);
        if (!ok) _notes.Add($"ork: clip '{clip}' is not in the Ork rig or does not decode; that action shows the movement pose");
        return _orkClipOk[clip] = ok;
    }

    // ---- the clip choices (also used by the remote smoothing) ----

    /// <summary>The Ork's reaction clip and its clip time for the state, or null when it is not reacting or the plan lacks the clip.</summary>
    (string Clip, float Time)? OrkReactionPose(PlayerState s, SmoothClip? smooth, OrkActionPlan actions, MarineAssets assets)
    {
        if (!s.Alive || _config is null || Reaction.ClipOf(s, _config) is not { } sim || actions.ReactionOf(s.Reaction) is not { } spec) return null;
        var ork = spec.Clip(s.ReactionDirection);
        if (!OrkClipUsable(assets, ork.Animation)) return null;
        float time = smooth is { Kind: ClipKind.Reaction } sr ? sr.Time : Reaction.Time(s, _config);
        return (ork.Animation, OrkActionRules.ReactionTime(time, sim.EndAt, ork.EndAt, ork.Duration));
    }

    /// <summary>The charge's clip and clip time (the collision stop plays on from the hit), or null.</summary>
    (string Clip, float Time)? OrkRushPose(PlayerState s, SmoothClip? smooth, MarineAssets assets)
    {
        if (!s.Alive || _config is null || Loadouts.Rush(_config, s) is not { } spec || Rush.MoveOf(spec, s.Rush) is not { } move || !OrkClipUsable(assets, move.Animation)) return null;
        float length = assets.Animation(move.Animation)!.Animation.Duration;
        float time = smooth is { Kind: ClipKind.Rush } sh ? sh.Time : Rush.Time(s, _config);
        return (move.Animation, OrkActionRules.RushClipTime(s.Rush, spec.ContactUntil, time, move.ClipLength, length));
    }

    /// <summary>The clip of the Ork's current melee step (light combo or heavy strike) of its melee weapon, from the simulation's own steps, or null.</summary>
    string? OrkAttackClip(PlayerState s)
    {
        if (!s.Alive || s.MeleeStep <= 0 || _classes is null || MeleeWargear(s) is not { } wargear) return null;
        var table = s.MeleeHeavy ? _classes.MeleeHeavyAnimationsByWeapon : _classes.MeleeAnimationsByWeapon;
        return table.TryGetValue(wargear, out var clips) && s.MeleeStep <= clips.Count ? clips[s.MeleeStep - 1] : null;
    }

    /// <summary>
    /// Remote smoothing (<see cref="SmoothRemoteClip"/>): which clip an Ork's pose is smoothed for, in the simulation's time: a reaction, else the charge, else a melee swing
    /// (the same order as <see cref="OrkActionPose"/>). Decided from the state alone (the marine clip names the marine checks hold are not the Ork's).
    /// </summary>
    void OrkClipTarget(PlayerState s, ref ClipKind kind, ref long key, ref float target, ref float length)
    {
        if (_config is null) return;
        if (Reaction.ClipOf(s, _config) is { } reaction)
        {
            kind = ClipKind.Reaction; target = Math.Clamp(Reaction.Time(s, _config), 0f, reaction.Duration); length = reaction.Duration;
            key = ClipClock.Key((int)kind, (int)s.Reaction, (int)s.ReactionDirection);
        }
        else if (Loadouts.Rush(_config, s) is { } spec && Rush.MoveOf(spec, s.Rush) is { } move)
        {
            kind = ClipKind.Rush; target = Math.Clamp(Rush.Time(s, _config), 0f, move.ClipLength); length = move.ClipLength;
            key = ClipClock.Key((int)kind, (int)s.Rush);
        }
        else if (OrkAttackClip(s) is not null)
        {
            kind = ClipKind.Attack; target = s.MeleeTime; length = float.MaxValue;
            key = ClipClock.Key((int)kind, s.MeleeStep, s.MeleeHeavy ? 1 : 0, s.MeleeChoice);
        }
    }

    // ---- the pose ----

    /// <summary>The action poses in the marines' order; true when one took the pose (the locomotion is then not placed).</summary>
    bool OrkActionPose(CharacterViewer character, PlayerState s, float? throwTime, bool own, SmoothClip? smooth, OrkLocomotionPlan plan, OrkActionPlan actions, MarineAssets assets)
    {
        if (!s.Alive || _config is null) return false;
        _orkActionFrames++;
        // Reacting: the reaction ends any attack, throw or hop on the ground.
        if (OrkReactionPose(s, smooth, actions, assets) is { } reaction && character.Pose(reaction.Clip, reaction.Time, ClipBlends.Reaction)) { _orkReactionPoses++; if (OrkCaptureDir is not null) OrkNote(s, KindReaction[Math.Clamp((int)s.Reaction, 0, 3)], reaction.Time / MathF.Max(0.01f, actions.ReactionOf(s.Reaction)!.Clip(s.ReactionDirection).EndAt), own); return true; }
        if (OrkRushPose(s, smooth, assets) is { } rush && character.Pose(rush.Clip, rush.Time, ClipBlends.Rush)) { _orkRushPoses++; if (OrkCaptureDir is not null) OrkNote(s, KindCharge, rush.Time / MathF.Max(0.01f, assets.Animation(rush.Clip)!.Animation.Duration), own); return true; }
        if (OrkAttackClip(s) is { } attack && OrkClipUsable(assets, attack) && Combat.MeleeStepOf(_config, s) is { } step)
        {
            float simTime = smooth is { Kind: ClipKind.Attack } sa ? sa.Time : s.MeleeTime;
            float clipLength = assets.Animation(attack)!.Animation.Duration;
            float clipTime = Math.Min(simTime * OrkActionRules.ClipRate(clipLength, step.Duration), clipLength);
            _orkAttackPoses++;
            OrkNote(s, s.MeleeHeavy ? KindHeavy : KindLight[Math.Clamp(s.MeleeStep, 0, 4)], simTime / step.Duration, own);
            if (OrkHandoffAttack(character, s, own, attack, clipTime, simTime, step, plan, actions) || character.Pose(attack, clipTime, ClipBlends.Melee(s.MeleeStep))) return true;
        }
        if (EvadePose(s) is { } hop && Evade.Move(s, _config) is { } move && OrkClipUsable(assets, hop.Clip))
        {
            float clipLength = assets.Animation(hop.Clip)!.Animation.Duration;
            if (character.Pose(hop.Clip, Math.Min(hop.Time * OrkActionRules.ClipRate(clipLength, move.Duration), clipLength), ClipBlends.Evade)) { _orkHopPoses++; OrkNote(s, KindHop, hop.Time / move.Duration, own); return true; }
        }
        if (throwTime is { } tt && actions.Throw is { } toss && OrkClipUsable(assets, toss.Clip))
        {
            float clipTime = OrkActionRules.ThrowClipTime(tt, _config.ThrowRelease, _config.ThrowDuration, toss.ReleaseAt, toss.EndAt, toss.Length);
            _orkThrowPoses++;
            OrkNote(s, KindThrow, tt / _config.ThrowDuration, own);
            if (OrkHandoffThrow(character, s, own, toss.Clip, clipTime, tt, plan, actions) || character.Pose(toss.Clip, clipTime, ClipBlends.Throw)) return true;
        }
        return false;
    }

    /// <summary>The cross-fade of the Ork's locomotion stances: the graphs' 0.1 s while an action clip hands the legs over, else the stance's own.</summary>
    float OrkFade => _fadeOverride > 0f ? _fadeOverride : CharacterViewer.StanceFadeSeconds;

    /// <summary>The locomotion for the legs while an action clip is laid over the upper body.</summary>
    void OrkLocomotionHandedOver(CharacterViewer character, PlayerState s, OrkLocomotionPlan plan, bool own)
    {
        _fadeOverride = ClipBlends.Default;
        try { if (!OrkLocomote(character, s, plan, own)) OrkStand(character, s, plan); }
        finally { _fadeOverride = 0f; }
    }

    /// <summary>A melee swing at the simulation's <paramref name="simTime"/>: true when the locomotion has the legs (the Ork moves past the step's <c>MoveAt</c>) and the clip is on the upper body.</summary>
    bool OrkHandoffAttack(CharacterViewer character, PlayerState s, bool own, string clip, float clipTime, float simTime, MeleeStep step, OrkLocomotionPlan plan, OrkActionPlan actions)
    {
        var t = LocoOf(s.PlayerId);
        if (ActionHandoff.AttackChanged(t.HandStep, t.HandHeavy, t.HandTime, s.MeleeStep, s.MeleeHeavy, s.MeleeTime)) t.AttackHand = false;
        t.HandStep = s.MeleeStep; t.HandHeavy = s.MeleeHeavy; t.HandTime = s.MeleeTime;
        if (NoHandoff || actions.UpperBodyMask is not { } mask) { t.AttackHand = false; return false; }
        float moveAt = _config is { } config ? Combat.MoveAtOf(step, config) : step.MoveAt;   // feel/locks-sprint-stop: the simulation's scaled move gate
        if (!t.AttackHand && ActionHandoff.AttackHandsOver(simTime, moveAt, HandoffMoving(s))) t.AttackHand = true;
        if (!t.AttackHand) return false;
        OrkLocomotionHandedOver(character, s, plan, own);
        character.SetPoseLayer(clip, clipTime, ActionHandoff.TailWeight(step.Duration, simTime), additive: false, mask);
        _orkHandoffs++;
        return true;
    }

    /// <summary>The toss while the Ork moves: the legs follow the locomotion, the toss is on the upper body.</summary>
    bool OrkHandoffThrow(CharacterViewer character, PlayerState s, bool own, string clip, float clipTime, float throwTime, OrkLocomotionPlan plan, OrkActionPlan actions)
    {
        var t = LocoOf(s.PlayerId);
        if (ActionHandoff.ThrowRestarted(t.HandThrowTime, throwTime)) t.ThrowHand = false;
        t.HandThrowTime = throwTime;
        if (NoHandoff || actions.UpperBodyMask is not { } mask) { t.ThrowHand = false; return false; }
        if (!t.ThrowHand && HandoffMoving(s)) t.ThrowHand = true;
        if (!t.ThrowHand) return false;
        OrkLocomotionHandedOver(character, s, plan, own);
        character.SetPoseLayer(clip, clipTime, ActionHandoff.ThrowWeight(_config!.ThrowDuration, throwTime), additive: false, mask);
        _orkHandoffs++;
        return true;
    }

    /// <summary>The recoil of the last shot seen, laid as the boy's additive clip over the pose (the hip-fire code's shot clock).</summary>
    void OrkRecoil(CharacterViewer character, PlayerState s, OrkActionPlan actions)
    {
        if (NoHipFire || !s.Alive || actions.RecoilFor(s.Slot == WeaponSlot.Pistol) is not { } recoil || !_hip.TryGetValue(s.PlayerId, out var t)) return;
        float age = (float)(_connectedFor - t.RecoilAt);
        if (age < 0f || age >= recoil.Length) return;
        if (character.SetPoseLayer(recoil.Clip, age, 1f, recoil.Additive)) { _orkRecoilLayers++; OrkNote(s, KindShot, 0.5f, ReferenceEquals(character, _local)); }
    }

    // ---- death ----

    readonly Dictionary<int, (double Start, OrkDeathClip Clip)> _orkDeaths = new();

    /// <summary>Forgets a death clip under way (the player is alive again or gone).</summary>
    void OrkDeathForget(int id) => _orkDeaths.Remove(id);

    /// <summary>
    /// Called by <see cref="ProcessDeaths"/> before an Ork's ragdoll starts: poses the Ork in its death clip until the graph's <c>Pre-Powered Ragdoll</c> window of the clip ends (0.4 to 0.93 s;
    /// the character is made visible for it, the own one too), and returns true while it does, so the death waits; false at once for anything else, and then the ragdoll starts from the pose
    /// the clip left. The clip is the boy's death state for the blow: from the front falls backwards, from behind forwards, a melee kill by the hit's side (rules in <see cref="OrkActionRules"/>).
    /// </summary>
    bool OrkDeathHold(int id, CharacterViewer character, PlayerState state, bool own)
    {
        if (_orkActions is not { Deaths.Count: > 0 } actions || !IsOrkCharacter(character) || character.Assets is not { } assets) return false;
        if (!_orkDeaths.TryGetValue(id, out var entry))
        {
            var kill = _deathCauses.TryGetValue(id, out var c) && _connectedFor - c.At < 3.0 ? c.Kill : (KillView?)null;
            bool melee = kill is { Cause: KillCause.Melee };
            float along = 0f, right = 0f;
            bool has = kill is { HasDirection: true };
            if (has)
            {
                var d = kill!.Value.Direction;
                along = d.X * -MathF.Sin(state.Yaw) + d.Y * MathF.Cos(state.Yaw);
                right = d.X * MathF.Cos(state.Yaw) + d.Y * MathF.Sin(state.Yaw);
            }
            var kind = OrkActionRules.DeathKindOf(melee, has, along, right);
            if (actions.DeathOf(kind) is not { } clip || !OrkClipUsable(assets, clip.Clip)) return false;
            _orkDeaths[id] = entry = (_connectedFor, clip);
            if (_bot || own) GD.Print($"t {_connectedFor:F1} s: ork P{id} dies: {clip.Clip} for {OrkActionRules.DeathHold(clip):F2} s, then the ragdoll ({kind}{(kill is { } k ? $", {k.Cause}" : ", cause unknown")})");
        }
        float age = (float)(_connectedFor - entry.Start);
        if (age >= OrkActionRules.DeathHold(entry.Clip)) { _orkDeaths.Remove(id); return false; }
        character.Visible = true;
        character.Pose(entry.Clip.Clip, age, 0.167f);   // "Fast Blend" of the graph's death transitions
        _orkDeathHolds++;
        if (OrkCaptureDir is not null) _orkShown[id] = (Engine.GetProcessFrames(), KindDeath, age / MathF.Max(0.01f, OrkActionRules.DeathHold(entry.Clip)), own, character);
        return true;
    }

    // ---- capture (test aid) ----

    readonly Dictionary<int, (ulong Frame, string Kind, float Progress, bool Own, CharacterViewer? Character)> _orkShown = new();
    readonly Dictionary<string, int> _orkCaptured = new();

    /// <summary>The <c>--ork-capture</c> folder (inside the private data folder), read once; null when the aid is off. Nothing of the capture runs, and no note is made, without it.</summary>
    string? OrkCaptureDir
    {
        get
        {
            if (_orkCaptureResolved) return _orkCaptureDir;
            _orkCaptureResolved = true;
            var args = OS.GetCmdlineUserArgs();
            int at = Array.IndexOf(args, "--ork-capture");
            if (at < 0 || at + 1 >= args.Length) return null;
            string dir = System.IO.Path.GetFullPath(args[at + 1]);
            if (!SmFormats.InstallPaths.IsWithinOsRule(dir, GameInstall.DataRoot)) { GD.PrintErr($"ork capture: {dir} is outside {GameInstall.DataRoot}; refused"); return null; }
            return _orkCaptureDir = dir;
        }
    }
    string? _orkCaptureDir;
    bool _orkCaptureResolved;

    // The kinds the capture names (static: no string is made per frame).
    const string KindShot = "shot", KindReload = "reload", KindCharge = "charge", KindHop = "hop", KindThrow = "throw", KindDeath = "death", KindHeavy = "choppa_heavy";
    static readonly string[] KindLight = ["choppa_light0", "choppa_light1", "choppa_light2", "choppa_light3", "choppa_light4"];
    static readonly string[] KindReaction = ["reaction_none", "reaction_interrupt", "reaction_stun", "reaction_stagger"];

    void OrkNote(PlayerState s, string kind, float progress, bool own)
    {
        if (OrkCaptureDir is null) return;
        _orkShown[s.PlayerId] = (Engine.GetProcessFrames(), kind, progress, own, own ? _local : _remotes.GetValueOrDefault(s.PlayerId));
    }

    /// <summary>
    /// <c>--ork-capture &lt;dir&gt;</c> (test aid, windowed client): saves a screenshot the first two times an action (shot, reload, choppa swing, heavy strike, charge, hop, throw, reactions,
    /// death) is half way on the own Ork, or on another Ork close in front of the camera, to a folder inside the private data folder (never the repository or the game folder).
    /// </summary>
    void OrkCaptureFrame()
    {
        if (_orkShown.Count == 0 || OrkCaptureDir is not { } dir || _camera is null || DisplayServer.GetName() == "headless") return;
        ulong frame = Engine.GetProcessFrames();
        foreach (var (id, shown) in _orkShown.ToList())
        {
            if (shown.Frame + 1 < frame || shown.Progress < 0.4f || shown.Character is not { } c || !IsInstanceValid(c)) continue;
            string kind = shown.Kind;
            int count = _orkCaptured.GetValueOrDefault(kind);
            if (count >= 2) continue;
            var head = c.GlobalPosition + Vector3.Up * 1.2f;
            if (!shown.Own && (_camera.GlobalPosition.DistanceTo(head) > 12f || _camera.IsPositionBehind(head))) continue;
            _orkCaptured[kind] = count + 1;
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"ork_{kind}_{(shown.Own ? "own" : "seen")}_{count + 1}.png");
            var err = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print(err == Godot.Error.Ok ? $"ork capture -> {path} (P{id}, {kind}, progress {shown.Progress:F2})" : $"ork capture failed: {err}");
        }
    }

    // ---- info ----

    /// <summary>The bot log's Ork action counters.</summary>
    string OrkActionStats() =>
        $"ork poses: reaction {_orkReactionPoses}, charge {_orkRushPoses}, melee {_orkAttackPoses}, hop {_orkHopPoses}, throw {_orkThrowPoses}, recoil layers {_orkRecoilLayers}, hand-offs {_orkHandoffs}, death holds {_orkDeathHolds}";
}
