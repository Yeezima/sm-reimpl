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
    /// <summary>The largest shake (metres, degrees) since the film last cleared it (test aid).</summary>
    internal (float Offset, float Degrees) DreadShakePeak;

    /// <summary>Per Dreadnought: what the feel needs to see change between frames.</summary>
    sealed class DreadFeelState { public bool Left, WasAir, StimWas, Hurt66, Hurt33, Entered, Rush, RushOn, Sprinting; public float PrevMelee = -1f; public int PrevStep; public bool PrevHeavy; public double AirSince; public float PrevCd, FireRamp; public double LastShot = double.NegativeInfinity; public readonly List<FxEffect> Lit = new(), Pyre = new(); public bool Lights; }
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
                if (action == DreadAction.DeathBlast && e.Name == DreadnoughtFeel.DeathFireEffect) continue;   // the death fire is a lasting one (DreadDeathFire), not a single play
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
        if (DreadMarkerWorld(t, e.Marker) is { } real) { _fx?.Play(e.Name, real, Basis.Identity, e.Scale, priority: SmContent.FxPriority.Special); return; }
        var (h, side, fwd) = DreadMarker(e.Marker);
        var right = new Num.Vector3(MathF.Cos(s.Yaw), MathF.Sin(s.Yaw), 0f); var f = new Num.Vector3(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw), 0f);
        var p = s.Position + new Num.Vector3(0, 0, h) + right * side + f * fwd;
        _fx?.Play(e.Name, ToGodot(p), Basis.Identity, e.Scale, priority: SmContent.FxPriority.Special);
    }

    /// <summary>The shake aimed at the own player: a stomp's or slam's area shake for a camera inside its 10 m.</summary>
    void DreadAreaShake(PlayerState s, bool own)
    {
        if (_client is null) return;
        var me = _client.Predicted.Position;
        float d = new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
        if (d <= DreadnoughtMelee.StompRadius) { _dreadShakes.Add((DreadnoughtFeel.StompArea, _connectedFor, d, true)); if (!own) DreadRumble(DreadnoughtArm.StompAreaHit); }
    }

    /// <summary>Per frame, per living Dreadnought: edges that fire the table (entrance, stomp's foot hit, wall hit, landing, power-up, damage thresholds). Footfalls are fired by the stride (<see cref="DreadnoughtSounds"/>).</summary>
    void DreadnoughtFeelStep(DreadnoughtTrack t, PlayerState s, bool own, bool entering, double since)
    {
        var f = FeelOf(s.PlayerId);
        // the entrance cue plays on ARRIVAL only: a living body in its first 0.3 s of a life (never a corpse: the death fired it 0.02 s after DeathStart, drawing the portal clouds and a purple glow at his feet)
        if (DreadnoughtFeel.EntryDue(s.Alive, t.Corpse, since, f.Entered)) { f.Entered = true; DreadPyreOut(f); DreadAct(DreadAction.Entry, s, t, own); }
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
        if (own) DreadSustainedFire(f);
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
        f.Entered = false; f.Hurt66 = f.Hurt33 = false; f.StimWas = false; DreadLightsOff(f); DreadPyreOut(f);
        DreadAct(DreadAction.DeathStart, s, t, own);
        DreadAct(DreadAction.DeathBlast, s, t, own);
        if (_fx is not null && !Lean && FxActive) { var tt = t; GetTree().CreateTimer(DreadnoughtFeel.DeathFireDelay).Timeout += () => DreadDeathFire(f, tt); }
        if (_client is null) return;
        var me = _client.Predicted.Position;
        float d = own ? 0f : new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
        _dreadShakes.Add((DreadnoughtFeel.DeathBlastLong, _connectedFor + 0.5, d, false));
    }

    /// <summary>
    /// The own cannon's sustained fire (owner 2026-11-17): a round is a rise of the predicted cooldown (the cannon's 0.1 s a shot); while rounds keep coming within <see cref="DreadnoughtFeel.SustainGapSeconds"/> of each other the shake builds up
    /// (<see cref="DreadnoughtFeel.SustainRamp"/>), after the last it falls away. The melta (the pistol slot) never sustains.
    /// </summary>
    void DreadSustainedFire(DreadFeelState f)
    {
        if (_client is null) return;
        var me = _client.Predicted;
        float dt = (float)GetProcessDeltaTime();
        bool cannon = me.Alive && me.Slot != WeaponSlot.Pistol;
        if (cannon && me.Cooldown > f.PrevCd + 0.04f) f.LastShot = _connectedFor;
        f.PrevCd = me.Cooldown;
        bool firing = cannon && _connectedFor - f.LastShot <= DreadnoughtFeel.SustainGapSeconds;
        f.FireRamp = DreadnoughtFeel.SustainRamp(f.FireRamp, firing, dt);
        _dreadSustain = f.FireRamp; _dreadSustainAt = _connectedFor;
    }
    float _dreadSustain;
    double _dreadSustainAt = double.NegativeInfinity;   // the frame's time the ramp was last kept: a Dreadnought that died or left the class stops updating it, and the shake must not stay

    /// <summary>The shakes of this frame for the camera (<see cref="ExtrasCameraShake"/>; the Camera shake option is applied by the caller).</summary>
    partial void ExtrasCameraShake(double delta, ref Vector3 offset, ref Vector3 degrees)
    {
        float sustain = _connectedFor - _dreadSustainAt < 0.2 ? _dreadSustain : 0f;
        if (_dreadShakes.Count == 0 && sustain <= 0f) { DreadShakeNow = default; return; }
        _dreadShakes.RemoveAll(x => _connectedFor - x.At > x.Shake.Total + 0.05);
        var (o, d) = DreadnoughtFeel.Mix(_dreadShakes.Select(x => (x.Shake, (float)(_connectedFor - x.At), x.Distance, x.Hit)), 1f, UiKit.ReduceMotion);
        if (sustain > 0f)
        {
            var (so, sd) = DreadnoughtFeel.CannonSustained.Sample((float)_connectedFor, sustain * DreadnoughtFeel.Scale(1f, UiKit.ReduceMotion));
            o += so; d += sd;
        }
        DreadShakeNow = (o, d); DreadShakePeak = (MathF.Max(DreadShakePeak.Offset, o.Length()), MathF.Max(DreadShakePeak.Degrees, d.Length()));
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
            if (beat == DaArmBeat.Hit && own && !s.MeleeHeavy) DreadArmHitShake(s, steps[s.MeleeStep - 1]);   // a light swing's hit (the stomp and the slam shake at their foot hit, DreadAct)
            if (beat == DaArmBeat.Hit && s.StimLeft > 0f)   // Overcharge: the wave the server releases at a hit (drawn on each hit beat of the power-up; the sim decides whom it moves)
            {
                DreadOverchargeWave(s, t);
                if (own && _client is not null) _dreadShakes.Add((DreadnoughtFeel.OverchargeShake, _connectedFor, 0f, false));
            }
        }
    }

    /// <summary>The own light swing's hit shake: when the client sees at least one Ork in the swing's reach (the server's cleave, <see cref="MusouStrikeTargets"/>), a hit shakes the own camera (owner 2026-11-17).</summary>
    void DreadArmHitShake(PlayerState s, MeleeStep step)
    {
        MusouStrikeTargets(s, step, null);
        _dreadShakes.Add((_musouStrikeTargets.Count == 0 ? DreadnoughtFeel.ArmWhiff : DreadnoughtFeel.ArmHit, _connectedFor, 0f, false));
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} ArmHit shake: {_musouStrikeTargets.Count} struck");
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
            // The hull's blinkers (light_thunderhawk_*) are flat glow discs (non-billboard rings): on the marker's own axes one lay along the hull as a flat pink-red slab behind the head (the film of 2026-11-15), so they face the camera as a lamp's glow does (**ours**).
            bool face = e.Name.StartsWith("light_thunderhawk", StringComparison.Ordinal);
            (Vector3 Position, Basis Axes)? Place()
            {
                if (!IsInstanceValid(sk) || !sk.IsInsideTree()) return null;
                var w = sk.GlobalTransform * sk.GetBoneGlobalPose(bone) * local;
                return (w.Origin, face && _camera is { } cam ? AxesFromY(cam.GlobalPosition - w.Origin) : w.Basis.Orthonormalized());
            }
            if (Place() is not { } at) continue;
            if (_fx.Play(e.Name, at.Position, at.Axes, e.Scale, null, Place, 0f, 0.0, repeat: true, priority: 1) is { } fx) f.Lit.Add(fx); else noPlay++;
        }
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} Lights player {s.PlayerId}: {f.Lit.Count} of {DreadnoughtArm.Lights.Length} playing (marker missing {noMarker}, bone missing {noBone}, effect not played {noPlay})");
    }

    /// <summary>
    /// The death fire the data intends (<c>death_action</c> plays <c>amb_fire</c> 0.85 s in): a looping ambient fire that burns on the wreck for as long as the body lies (<see cref="DreadnoughtCorpseSeconds"/>) and follows the torso and pelvis markers
    /// (**ours**: the data's single play of a looping effect showed once and was gone; the pelvis one is the same effect at 0.8 scale so the wreck reads from the camera). Ends with the corpse or the next life.
    /// </summary>
    void DreadDeathFire(DreadFeelState f, DreadnoughtTrack t)
    {
        if (_fx is null || t.Viewer is not { } v || !IsInstanceValid(v) || !t.Corpse) return;
        foreach (var (marker, scale) in new[] { (DreadnoughtArm.Torso, 0.5f), ("fx_pelvis", 0.8f) })
        {
            var place = DreadMarkerWorld(t, marker);
            if (place is null) continue;
            string mk = marker;
            if (_fx.Play(DreadnoughtFeel.DeathFireEffect, place.Value, Basis.Identity, scale, null, () => DreadMarkerWorld(t, mk) is { } p ? (p, Basis.Identity) : null, 0f, 0.0, repeat: true, priority: SmContent.FxPriority.Special) is { } fire) f.Pyre.Add(fire);
        }
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} DeathFire {f.Pyre.Count} burning");
        var burning = f.Pyre.ToArray();
        GetTree().CreateTimer(DreadnoughtCorpseSeconds - DreadnoughtFeel.DeathFireDelay).Timeout += () => { foreach (var e in burning) e.Life = Math.Min(e.Life, e.Age); };
    }

    void DreadPyreOut(DreadFeelState f) { foreach (var e in f.Pyre) e.Life = Math.Min(e.Life, e.Age); f.Pyre.Clear(); }

    /// <summary>Registers the reach of the Dreadnought's scaled-up effects' lights (<see cref="DreadnoughtFeel.LightReach"/>): the x12 power-up glow and the x3 force field lit the whole view red and purple, the x1.75 muzzle flash flickered it sepia.</summary>
    void DreadRegisterLightCaps()
    {
        if (_fx is null) return;
        foreach (var n in DreadnoughtArm.AllEffectNames()) _fx.LightCaps[n] = DreadnoughtFeel.LightReach;
        foreach (var n in new[] { DreadnoughtFeel.CannonMuzzle.Name, SmContent.Extras.Musou.Arsenal.Arsenal.ChaosMuzzleEffect }) _fx.LightCaps[n] = DreadnoughtFeel.LightReach;
    }

    /// <summary>Overcharge's wave at the hit: the stomp's displace effects at the size of the 3 m wave, on the ground at the hand's place (not at hand height), one set per swing.</summary>
    void DreadOverchargeWave(PlayerState s, DreadnoughtTrack t)
    {
        if (_fx is null || Lean || !FxActive) return;
        var ground = ToGodot(s.Position);
        var hand = DreadMarkerWorld(t, DreadnoughtArm.LeftHand) ?? ToGodot(s.Position + new Num.Vector3(-MathF.Sin(s.Yaw) * 2f, MathF.Cos(s.Yaw) * 2f, 0f));
        var at = new Vector3(hand.X, ground.Y + 0.05f, hand.Z);
        if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} OverchargeWave player {s.PlayerId}: {string.Join(", ", DreadnoughtArm.OverchargeWave.Select(e => $"{e.Name}x{e.Scale}"))} at the ground");
        foreach (var e in DreadnoughtArm.OverchargeWave) _fx.Play(e.Name, at, Basis.Identity, e.Scale, priority: SmContent.FxPriority.Special);
        DreadWaveRing(at);
    }

    const string WaveRingShader = """
        shader_type spatial;
        render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
        uniform float t = 0.0;
        void fragment() {
            vec2 p = UV * 2.0 - 1.0;
            float d = length(p);
            float width = 0.07 + 0.08 * t;
            float ring = smoothstep(width, 0.0, abs(d - t));
            float fill = smoothstep(t, 0.0, d) * 0.18;
            float fade = (1.0 - t) * (1.0 - t);
            ALBEDO = vec3(1.0, 0.78, 0.45) * (ring * 1.8 + fill) * fade;
            ALPHA = clamp(ring + fill, 0.0, 1.0) * fade;
        }
        """;
    static Shader? _waveRingShader;

    /// <summary>
    /// A flat ring that runs out along the ground from the hit to the wave's 3 m radius in 0.4 s (**ours**: the stomp's own effects at that size were too faint in the film of 2026-11-15 to tell from the swing's arcs and blasts), so the wave
    /// reads as its own cue: its edge is where the server's knockback stops (<see cref="DreadnoughtMelee.OverchargeRadius"/>).
    /// </summary>
    void DreadWaveRing(Vector3 at)
    {
        if (_fx is null) return;
        float r = DreadnoughtMelee.OverchargeRadius;
        var mat = new ShaderMaterial { Shader = _waveRingShader ??= new Shader { Code = WaveRingShader } };
        mat.SetShaderParameter("t", 0.04f);
        var ring = new MeshInstance3D { Name = "OverchargeRing", Mesh = new PlaneMesh { Size = new Vector2(2f * r, 2f * r) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _fx.AddChild(ring);
        ring.GlobalPosition = at;
        var tween = ring.CreateTween();
        tween.TweenMethod(Callable.From<float>(v => mat.SetShaderParameter("t", v)), 0.04f, 1f, 0.4);
        tween.TweenCallback(Callable.From(ring.QueueFree));
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
}
