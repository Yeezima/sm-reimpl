using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Dreadnought Assault Dreadnought's feel in a Musou match (owner, 2026-11-15: "ensure you get all visual effects, screen shake, sounds"): each action of <see cref="DreadnoughtFeel.Table"/> plays its sounds, its
/// effects and its camera shake. The shake is the data's (<c>camera_effect</c> of <c>sm_dreadnought</c>), felt by the own camera with the data's distance fall-off and scaled by the Camera shake option and Reduce motion
/// (<see cref="DreadnoughtFeel.Mix"/>). Marker points of the effects are body heights (**ours**: the rig's marker bones are not read). Test aid <c>--dn-feel-log</c> prints every play call (sound events cannot be heard in a test run).
/// </summary>
public partial class NetworkView
{
    static readonly bool LogDreadFeel = CmdFlag("dn-feel-log");
    readonly List<(DaShake Shake, double At, float Distance, bool Hit)> _dreadShakes = new();
    /// <summary>The sum of the shakes drawn last frame (metres, degrees), for the tests' log.</summary>
    internal (Num.Vector3 Offset, Num.Vector3 Degrees) DreadShakeNow;

    /// <summary>Per Dreadnought: what the feel needs to see change between frames.</summary>
    sealed class DreadFeelState { public bool Left, WasAir, StimWas, Hurt66, Hurt33, Entered, Rush, RushOn, Sprinting; public float PrevMelee = -1f; public int PrevStep; public bool PrevHeavy; public double AirSince; public readonly List<FxEffect> Lit = new(); public bool Lights; }
    readonly Dictionary<int, DreadFeelState> _dreadFeel = new();

    DreadFeelState FeelOf(int id) { if (!_dreadFeel.TryGetValue(id, out var f)) _dreadFeel[id] = f = new DreadFeelState(); return f; }

    /// <summary>The body height (m above the feet) of a marker point (**ours**), with the side offset in metres (right is positive).</summary>
    static (float Height, float Side, float Forward) DreadMarker(string marker) => marker switch
    {
        "fx_l_foot" => (0.15f, -0.7f, 0f), "fx_r_foot" => (0.15f, 0.7f, 0f), "fx_torso" => (2.4f, 0f, 0f), "fx_cntr" => (1.8f, 0f, 0f), "fx_pelvis" => (1.6f, 0f, 0f),
        "fx_l_shoulder" => (3.0f, -0.9f, 0f), "fx_r_thigh" => (1.2f, 0.5f, 0f), "fx_l_thigh" => (1.2f, -0.5f, 0f), "fx_muzzle_l_hand" => (2.3f, -0.8f, 1.2f),
        _ => (1.8f, 0f, 0f),
    };

    /// <summary>Plays an action of the table at a Dreadnought: its sounds, its effects (delayed ones after their delay) and its camera shake for the own camera.</summary>
    void DreadAct(DreadAction action, PlayerState s, DreadnoughtTrack t, bool own, bool hit = false)
    {
        var feel = DreadnoughtFeel.For(action);
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} {action} ({feel.Trigger}) player {s.PlayerId}: sounds [{string.Join(", ", feel.Sounds)}] effects [{string.Join(", ", feel.Effects.Select(e => $"{e.Name}x{e.Scale}@{e.Marker}+{e.DelaySeconds}"))}] shake {feel.Shake?.Name ?? "none"}");
        if (_sound is not null && !Lean) foreach (var snd in feel.Sounds) _sound.Post(snd, t.Emitter, footstep: action <= DreadAction.SprintFootRight);
        if (_fx is not null && !Lean && FxActive)
            foreach (var e in feel.Effects)
            {
                if (e.DelaySeconds <= 0f) { DreadPlayEffect(e, s, t); continue; }
                var cap = e; var st = s;
                GetTree().CreateTimer(e.DelaySeconds).Timeout += () => DreadPlayEffect(cap, st, t);
            }
        if (own && action is DreadAction.FootLeft or DreadAction.SprintFootLeft) DreadRumble(DreadnoughtArm.FootLeft);
        if (own && action is DreadAction.FootRight or DreadAction.SprintFootRight) DreadRumble(DreadnoughtArm.FootRight);
        if (own && action is DreadAction.StompLeft or DreadAction.StompRight) DreadRumble(DreadnoughtArm.StompSelf);
        if (action is DreadAction.SprintFootLeft or DreadAction.SprintFootRight) DreadPlayEffects(DreadnoughtArm.EffortSprint, s, t);
        if (feel.Shake is { } shake && _client is not null)
        {
            var me = _client.Predicted.Position;
            float d = own ? 0f : new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
            if (shake.Gain(d) > 0f || hit) _dreadShakes.Add((shake, _connectedFor, d, hit));
        }
    }

    /// <summary>A marker point of a Dreadnought's model in Godot world space (the object tree's marker on its bone), or null.</summary>
    Vector3? DreadMarkerWorld(DreadnoughtTrack t, string marker)
    {
        if (t.Viewer is not { } v || !IsInstanceValid(v) || v.Skeleton is not { } sk || _dreadLooks is null || !_dreadLooks.Markers.TryGetValue(marker, out var m)) return null;
        int bone = sk.FindBone(m.Bone);
        return bone < 0 ? null : (sk.GlobalTransform * sk.GetBoneGlobalPose(bone) * MarineAssets.ToGodot(m.Local)).Origin;
    }

    void DreadPlayEffects(DaEffect[] list, PlayerState s, DreadnoughtTrack t) { if (_fx is null || Lean || !FxActive) return; foreach (var e in list) DreadPlayEffect(e, s, t); }

    void DreadPlayEffect(DaEffect e, PlayerState s, DreadnoughtTrack t)
    {
        if (DreadMarkerWorld(t, e.Marker) is { } real) { _fx?.Play(e.Name, real, Basis.Identity, e.Scale); return; }
        var (h, side, fwd) = DreadMarker(e.Marker);
        var right = new Num.Vector3(MathF.Cos(s.Yaw), MathF.Sin(s.Yaw), 0f); var f = new Num.Vector3(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw), 0f);
        var p = s.Position + new Num.Vector3(0, 0, h) + right * side + f * fwd;
        _fx?.Play(e.Name, ToGodot(p), Basis.Identity, e.Scale);
    }

    /// <summary>The shake aimed at the own player: a stomp's or slam's area shake for a camera inside its 10 m.</summary>
    void DreadAreaShake(PlayerState s, bool own)
    {
        if (_client is null) return;
        var me = _client.Predicted.Position;
        float d = new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
        if (d <= DreadnoughtMelee.AreaRadius) { _dreadShakes.Add((DreadnoughtFeel.StompArea, _connectedFor, d, true)); if (!own) DreadRumble(DreadnoughtArm.StompAreaHit); }
    }

    /// <summary>Per frame, per living Dreadnought: edges that fire the table (entrance, stomp's foot hit, wall hit, landing, power-up, damage thresholds). Footfalls are fired by the stride (<see cref="DreadnoughtSounds"/>).</summary>
    void DreadnoughtFeelStep(DreadnoughtTrack t, PlayerState s, bool own, bool entering, double since)
    {
        var f = FeelOf(s.PlayerId);
        if (since < 0.3 && !f.Entered) { f.Entered = true; DreadAct(DreadAction.Entry, s, t, own); }
        if (since > 0.5) f.Entered = false;
        if (entering) return;
        // the arm's foot hit (the stomp and the slam): when the strike's time passes the step's hit time
        if (s.MeleeStep > 0 && s.MeleeHeavy)
        {
            var steps = DreadnoughtMelee.Heavy();
            if (s.MeleeStep <= steps.Count)
            {
                float hit = steps[s.MeleeStep - 1].HitTime;
                if (f.PrevStep == s.MeleeStep && f.PrevMelee < hit && s.MeleeTime >= hit)
                {
                    f.Left = !f.Left;
                    DreadAct(f.Left ? DreadAction.StompLeft : DreadAction.StompRight, s, t, own);
                    DreadAreaShake(s, own);
                }
            }
        }
        DreadArmBeats(f, s, t, own);
        f.PrevStep = s.MeleeStep; f.PrevHeavy = s.MeleeHeavy; f.PrevMelee = s.MeleeTime;
        DreadLights(f, s, t);
        float spd = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y); bool sprinting = spd > DreadnoughtRules.RunSpeed + 1.5f;
        if (sprinting && !f.Sprinting) DreadPlayEffects(DreadnoughtArm.EffortStart, s, t);
        f.Sprinting = sprinting;
        bool rush = s.Rush == RushPhase.Collision;   // the charge's wall stop
        if (rush && !f.Rush) DreadAct(DreadAction.HitWall, s, t, own);
        if (s.Rush == RushPhase.Rush && !f.RushOn) { f.RushOn = true; DreadCue(DaArmKey.Rush, DaArmBeat.Start, s, t); } else if (s.Rush != RushPhase.Rush) f.RushOn = false;
        f.Rush = rush;
        if (!s.Grounded) { if (!f.WasAir) f.AirSince = _connectedFor; f.WasAir = true; }
        else if (f.WasAir) { f.WasAir = false; double air = _connectedFor - f.AirSince; if (air > 0.25) DreadAct(air > 0.8 ? DreadAction.LandHeavy : DreadAction.LandLight, s, t, own); }
        // the power-up. The time left is not on the wire (Stims.Carry): the drawn snapshot state has 0, so the own player reads the prediction (the film log's 2 s end was this); others' power-ups show no start or end effect.
        bool stim = own && _client is not null && _client.Predicted.StimLeft > 0f;
        if (stim && !f.StimWas) DreadAct(DreadAction.Buff, s, t, own);
        if (!stim && f.StimWas) DreadAct(DreadAction.BuffEnd, s, t, own);
        f.StimWas = stim;
        if (_config is { } cfg)   // damage states (data health_actions at 66 % and 33 % of his health)
        {
            float frac = s.Health / MathF.Max(1f, cfg.For(s.Class).HealthMax);
            if (frac < 0.66f && !f.Hurt66) { f.Hurt66 = true; DreadAct(DreadAction.Hurt66, s, t, own); }
            if (frac < 0.33f && !f.Hurt33) { f.Hurt33 = true; DreadAct(DreadAction.Hurt33, s, t, own); DreadAct(DreadAction.ArmourBreak, s, t, own); }
            if (frac >= 0.66f) { f.Hurt66 = false; f.Hurt33 = false; } else if (frac >= 0.33f) f.Hurt33 = false;
        }
    }

    /// <summary>His death (the alive to dead edge): the body fall, the delayed blasts and the shake; the wreck is the held death clip.</summary>
    void DreadnoughtFeelDeath(DreadnoughtTrack t, PlayerState s, bool own)
    {
        var f = FeelOf(s.PlayerId);
        f.Entered = false; f.Hurt66 = f.Hurt33 = false; f.StimWas = false; DreadLightsOff(f);
        DreadAct(DreadAction.DeathStart, s, t, own);
        DreadAct(DreadAction.DeathBlast, s, t, own);
        if (_client is null) return;
        var me = _client.Predicted.Position;
        float d = own ? 0f : new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
        _dreadShakes.Add((DreadnoughtFeel.DeathBlastLong, _connectedFor + 0.5, d, false));
    }

    /// <summary>The shakes of this frame for the camera (<see cref="ExtrasCameraShake"/>; the Camera shake option is applied by the caller).</summary>
    partial void ExtrasCameraShake(double delta, ref Vector3 offset, ref Vector3 degrees)
    {
        if (_dreadShakes.Count == 0) { DreadShakeNow = default; return; }
        _dreadShakes.RemoveAll(x => _connectedFor - x.At > x.Shake.Total + 0.05);
        var (o, d) = DreadnoughtFeel.Mix(_dreadShakes.Select(x => (x.Shake, (float)(_connectedFor - x.At), x.Distance, x.Hit)), 1f, UiKit.ReduceMotion);
        DreadShakeNow = (o, d);
        offset += new Vector3(o.X, o.Y, o.Z);
        degrees += new Vector3(d.X, d.Y, d.Z);
    }

    /// <summary>Plays one cue of the arm (its sounds and effects) for the Dreadnought <paramref name="s"/>.</summary>
    void DreadCue(DaArmKey key, DaArmBeat beat, PlayerState s, DreadnoughtTrack t)
    {
        if (DreadnoughtArm.Cue(key, beat) is not { } cue) return;
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} Arm {key}/{beat} player {s.PlayerId}: sounds [{string.Join(", ", cue.Sounds)}] effects [{string.Join(", ", cue.Effects.Select(e => $"{e.Name}x{e.Scale}@{e.Marker}"))}]");
        if (_sound is not null && !Lean) foreach (var snd in cue.Sounds) _sound.Post(snd, t.Emitter);
        DreadPlayEffects(cue.Effects, s, t);
    }

    /// <summary>The arm's beats by the strike's time (the sim's, as the clip clock runs it): start when the strike begins, antic and swing before the hit, the hit; the own player feels the hit as the data's rumble.</summary>
    void DreadArmBeats(DreadFeelState f, PlayerState s, DreadnoughtTrack t, bool own)
    {
        if (s.MeleeStep <= 0) return;
        bool fresh = f.PrevStep != s.MeleeStep || f.PrevHeavy != s.MeleeHeavy || f.PrevMelee > s.MeleeTime + 0.01f;
        var steps = s.MeleeHeavy ? DreadnoughtMelee.Heavy() : DreadnoughtMelee.Light();
        if (s.MeleeStep > steps.Count) return;
        var key = DreadnoughtArm.KeyOf(s.MeleeHeavy, s.MeleeStep);
        float hit = steps[s.MeleeStep - 1].HitTime, prev = fresh ? -1f : f.PrevMelee;
        foreach (var beat in new[] { DaArmBeat.Start, DaArmBeat.Antic, DaArmBeat.Swing, DaArmBeat.Hit })
        {
            float at = DreadnoughtArm.BeatTime(beat, hit);
            if (prev >= at || s.MeleeTime < at) continue;
            DreadCue(key, beat, s, t);
            if (beat == DaArmBeat.Start) DreadPlayEffects(DreadnoughtArm.EffortBig, s, t);
            if (beat == DaArmBeat.Hit && own) DreadRumble(DreadnoughtArm.ArmHit);
        }
    }

    /// <summary>The lights and eyes of his lifetime action, once per life, on the model's markers (following the bones); ended when he dies.</summary>
    void DreadLights(DreadFeelState f, PlayerState s, DreadnoughtTrack t)
    {
        if (f.Lights || _fx is null || Lean || !FxActive || t.Viewer is not { } v || _dreadLooks is null || _dreadLooks.Markers.Count == 0) return;
        f.Lights = true;
        int noMarker = 0, noBone = 0, noPlay = 0;
        foreach (var e in DreadnoughtArm.Lights)
        {
            if (!_dreadLooks.Markers.TryGetValue(e.Marker, out var m)) { noMarker++; continue; }
            var sk = v.Skeleton; if (sk is null) continue;
            int bone = sk.FindBone(m.Bone); if (bone < 0) { noBone++; continue; }
            var local = MarineAssets.ToGodot(m.Local);
            (Vector3 Position, Basis Axes)? Place()
            {
                if (!IsInstanceValid(sk) || !sk.IsInsideTree()) return null;
                var w = sk.GlobalTransform * sk.GetBoneGlobalPose(bone) * local;
                return (w.Origin, w.Basis.Orthonormalized());
            }
            if (Place() is not { } at) continue;
            if (_fx.Play(e.Name, at.Position, at.Axes, e.Scale, null, Place, 0f, 0.0, repeat: true, priority: 1) is { } fx) f.Lit.Add(fx); else noPlay++;
        }
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} Lights player {s.PlayerId}: {f.Lit.Count} of {DreadnoughtArm.Lights.Length} playing (marker missing {noMarker}, bone missing {noBone}, effect not played {noPlay})");
    }

    void DreadLightsOff(DreadFeelState f) { foreach (var e in f.Lit) e.Life = Math.Min(e.Life, e.Age); f.Lit.Clear(); f.Lights = false; }

    /// <summary>The own pad's vibration (<c>controller_rumble</c>): the data's two motors for its seconds, scaled by the Camera shake option and off with Reduce motion (the game has no vibration option); logged with <c>--dn-feel-log</c>.</summary>
    void DreadRumble(DaRumble r)
    {
        float k = DreadnoughtArm.RumbleScale(SmClient.Customizer.Model.LiveOptions.CameraShake, UiKit.ReduceMotion);
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} Rumble small {r.Small * k:F2} large {r.Large * k:F2} for {r.Seconds:F2} s");
        if (k <= 0f || Lean) return;
        foreach (int pad in Input.GetConnectedJoypads()) Input.StartJoyVibration(pad, r.Small * k, r.Large * k, r.Seconds);
    }

    /// <summary>One cannon shot's effects: the muzzle flash (autocannon_muzzle x1.75) and the casing (shell_casing x3) at their markers, the tracer trail (chaos_kraken_trail) from the muzzle along the aim.</summary>
    void DreadCannonShot(DreadnoughtTrack t, PlayerState s)
    {
        if (_fx is null || Lean || !FxActive) return;
        float cp = MathF.Cos(s.Pitch);
        var fwd = new Num.Vector3(-MathF.Sin(s.Yaw) * cp, MathF.Cos(s.Yaw) * cp, MathF.Sin(s.Pitch));
        var gf = new Vector3(fwd.X, fwd.Z, -fwd.Y);
        var muzzle = DreadMarkerWorld(t, DreadnoughtFeel.CannonMuzzle.Marker) ?? ToGodot(s.Position + new Num.Vector3(0, 0, 2.3f) + fwd * 2.2f);
        _fx.Play(DreadnoughtFeel.CannonMuzzle.Name, muzzle, AxesFromY(gf), DreadnoughtFeel.CannonMuzzle.Scale);
        var casing = DreadMarkerWorld(t, DreadnoughtFeel.CannonCasing.Marker) ?? muzzle;
        _fx.Play(DreadnoughtFeel.CannonCasing.Name, casing, Basis.Identity, DreadnoughtFeel.CannonCasing.Scale);
        _fx.Play(DreadnoughtFeel.CannonTrail, muzzle, AxesFromY(gf), 1f, muzzle + gf * DreadnoughtFeel.TracerMetres);
        if (LogDreadFeel && DreadMarkerWorld(t, DreadnoughtFeel.CannonMuzzle.Marker) is { } mk) GD.Print($"DNFEEL muzzle marker offset from the feet (godot x,y up,z): {mk - ToGodot(s.Position)}, aim {gf}, found casing marker {DreadMarkerWorld(t, DreadnoughtFeel.CannonCasing.Marker) is not null}");
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} CannonShot {DreadnoughtFeel.CannonMuzzle.Name}x{DreadnoughtFeel.CannonMuzzle.Scale}, {DreadnoughtFeel.CannonCasing.Name}x{DreadnoughtFeel.CannonCasing.Scale}, {DreadnoughtFeel.CannonTrail}");
    }

    /// <summary>One melta shot's effects: the wargear's two cylinder blasts (<c>meltagun_cylinder_blast_3</c> and <c>_4</c>; Chaos' <c>csm_</c> copies) at the left hand's muzzle marker (the melta arm) along the aim. The catalogue has no bindings for <c>dread_melta</c> ("no effect data" in the log), so nothing else played before.</summary>
    void DreadMeltaShot(DreadnoughtTrack t, PlayerState s)
    {
        if (_fx is null || Lean || !FxActive) return;
        float cp = MathF.Cos(s.Pitch);
        var fwd = new Num.Vector3(-MathF.Sin(s.Yaw) * cp, MathF.Cos(s.Yaw) * cp, MathF.Sin(s.Pitch));
        var gf = new Vector3(fwd.X, fwd.Z, -fwd.Y);
        var at = DreadMarkerWorld(t, DreadnoughtArm.LeftHand) ?? ToGodot(s.Position + new Num.Vector3(0, 0, 2.3f) + fwd * 2.2f);
        foreach (var name in DreadChaos ? DreadnoughtFeel.ChaosMeltaEffects : DreadnoughtFeel.MeltaEffects) _fx.Play(name, at, AxesFromY(gf), 1f);
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} MeltaShot {string.Join(", ", DreadChaos ? DreadnoughtFeel.ChaosMeltaEffects : DreadnoughtFeel.MeltaEffects)}");
    }
}
