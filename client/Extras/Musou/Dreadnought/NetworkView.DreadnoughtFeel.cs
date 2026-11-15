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
    sealed class DreadFeelState { public bool Left, WasAir, StimWas, Hurt66, Hurt33, Entered, Rush; public float PrevMelee; public int PrevStep; public double AirSince; }
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
                if (e.DelaySeconds <= 0f) { DreadPlayEffect(e, s); continue; }
                var cap = e; var st = s;
                GetTree().CreateTimer(e.DelaySeconds).Timeout += () => DreadPlayEffect(cap, st);
            }
        if (feel.Shake is { } shake && _client is not null)
        {
            var me = _client.Predicted.Position;
            float d = own ? 0f : new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
            if (shake.Gain(d) > 0f || hit) _dreadShakes.Add((shake, _connectedFor, d, hit));
        }
    }

    void DreadPlayEffect(DaEffect e, PlayerState s)
    {
        var (h, side, fwd) = DreadMarker(e.Marker);
        var right = new Num.Vector3(MathF.Cos(s.Yaw), MathF.Sin(s.Yaw), 0f); var f = new Num.Vector3(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw), 0f);
        var p = s.Position + new Num.Vector3(0, 0, h) + right * side + f * fwd;
        _fx?.Play(e.Name, ToGodot(p), Basis.Identity, e.Scale);
    }

    /// <summary>The shake aimed at the own player: a stomp's or slam's area shake for a camera inside its 10 m.</summary>
    void DreadAreaShake(PlayerState s)
    {
        if (_client is null) return;
        var me = _client.Predicted.Position;
        float d = new Num.Vector2(s.Position.X - me.X, s.Position.Y - me.Y).Length();
        if (d <= DreadnoughtMelee.AreaRadius) _dreadShakes.Add((DreadnoughtFeel.StompArea, _connectedFor, d, true));
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
                    DreadAreaShake(s);
                }
            }
        }
        f.PrevStep = s.MeleeStep; f.PrevMelee = s.MeleeTime;
        bool rush = s.Rush == RushPhase.Collision;   // the charge's wall stop
        if (rush && !f.Rush) DreadAct(DreadAction.HitWall, s, t, own);
        f.Rush = rush;
        if (!s.Grounded) { if (!f.WasAir) f.AirSince = _connectedFor; f.WasAir = true; }
        else if (f.WasAir) { f.WasAir = false; double air = _connectedFor - f.AirSince; if (air > 0.25) DreadAct(air > 0.8 ? DreadAction.LandHeavy : DreadAction.LandLight, s, t, own); }
        bool stim = s.StimLeft > 0f;   // the power-up
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
        f.Entered = false; f.Hurt66 = f.Hurt33 = false; f.StimWas = false;
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
}
