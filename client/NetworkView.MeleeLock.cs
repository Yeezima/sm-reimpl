using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;
using V2 = System.Numerics.Vector2;

namespace SmClient;

/// <summary>
/// feel/melee-lock (protocol 32), the client's side (the simulation is <see cref="MeleeLock"/>, the rules <see cref="MeleePresentation"/>; the owner's
/// knowledge of SM1, 2026-10-29: "they 'stick' to it and line up"; "there should be a bit of 'stickiness' ... everything feels very floaty"):
/// <list type="bullet">
/// <item>Distance clips: an attack shows the blend of its distance variants (<c>…_1m</c>, <c>…_3.5m</c>, <c>…_7m</c>) at the distance its target had when
/// the attack began (own: the lock the client sent; others: the enemy the lock search would take, since locks are not on the wire), the lower
/// variant posed and the upper one laid over it at the blend weight (<see cref="PoseSlots.MeleeVariant"/>).</item>
/// <item>Hit-stop (OFF by default, <c>--hit-stop</c>): a landed hit holds the attacker's attack clip and the victim's reaction clip for a few frames (own hits from the predicted hit event;
/// others' when their hit time passes with someone in reach), plus a small camera shake on own hits.</item>
/// <item>Camera assist: removed in sim/combat-2 (the owner's decision of 2026-10-30: the lock turns the model and the attack, never the camera).</item>
/// </list>
/// Test aids: <c>--no-melee-lock</c> (the client sends no locks), <c>--no-melee-variants</c> (the 3.5 m clip as before); opt-in <c>--hit-stop</c> (hit-stop and hit shakes, off by default since 2026-10-29).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMeleeLock = OS.GetCmdlineUserArgs().Contains("--no-melee-lock");
    /// <summary>Owner's decision 2026-10-29 ("remove the stop hit/screen freeze/shake effect when attacking an enemy in melee"): the hit-stop and the hit shakes (own melee hit shake, the victim's reaction shake) are off; <c>--hit-stop</c> brings them back.</summary>
    static readonly bool HitStopOn = OS.GetCmdlineUserArgs().Contains("--hit-stop");
    static readonly bool NoMeleeVariants = OS.GetCmdlineUserArgs().Contains("--no-melee-variants");

    /// <summary>Per player: the attack being shown (step, heavy, last time seen) and the distance its blend is posed at.</summary>
    sealed class MeleeShow
    {
        public int Step; public bool Heavy; public float Time = -1f; public float StartDistance;
        public int HitStep; public bool HitHeavy; public float LastSeenTime = -1f;   // others' hit-time crossing (MeleeLockFrame)
    }
    readonly Dictionary<int, MeleeShow> _meleeShows = new();
    /// <summary>Hit-stops under way: player id → when it began (<see cref="_connectedFor"/>).</summary>
    readonly Dictionary<int, double> _hitStops = new();
    readonly List<int> _hitStopDone = new();
    int _meleeHitsShown, _meleeLockSwings;

    /// <summary>Once per frame after the own events are drained: own hit-stops and shake, others' hit-stops, the camera assist.</summary>
    void MeleeLockFrame(PlayerState me, double delta)
    {
        if (_client is null || _config is null) return;
        // Own hits: the predicted hit event (the server's hit report comes a round trip later).
        foreach (var (_, events) in _frameOwnEvents)
        {
            if (events.MeleeHitStep <= 0 || !me.Alive) continue;
            int victim = MeleePresentation.Victim(me, _lastOthers, _config, _client.MeleeLockTarget);
            if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} melee hit time (step {events.MeleeHitStep}{(events.MeleeHeavy ? " heavy" : "")}) lock P{_client.MeleeLockTarget} victim {(victim > 0 ? $"P{victim}" : "none")}");
            if (victim <= 0) continue;
            _meleeHitsShown++;
            StartHitStop(_client.PlayerId); StartHitStop(victim);
            if (HitStopOn) FxShake(MeleePresentation.HitShake);
        }
        // Others' hits: their hit time passes with someone in reach in front (presentation guess; the server decides).
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || o.MeleeStep <= 0 || Combat.MeleeStepOf(_config, o) is not { } step) continue;
            var show = ShowOf(o.PlayerId);
            bool same = show.HitStep == o.MeleeStep && show.HitHeavy == o.MeleeHeavy && o.MeleeTime >= show.LastSeenTime;
            float before = same ? show.LastSeenTime : -1f;
            if (before < step.HitTime && o.MeleeTime >= step.HitTime)
            {
                var candidates = new List<PlayerState>(_lastOthers.Count + 1) { me };
                foreach (var p in _lastOthers) if (p.PlayerId != o.PlayerId) candidates.Add(p);
                int victim = MeleePresentation.Victim(o, candidates, _config);
                if (victim > 0) { StartHitStop(o.PlayerId); StartHitStop(victim); }
            }
            (show.HitStep, show.HitHeavy, show.LastSeenTime) = (o.MeleeStep, o.MeleeHeavy, o.MeleeTime);
        }
        // Expired hit-stops.
        _hitStopDone.Clear();
        foreach (var (id, start) in _hitStops) if (!MeleePresentation.Active((float)(_connectedFor - start))) _hitStopDone.Add(id);
        foreach (int id in _hitStopDone) _hitStops.Remove(id);
        // sim/combat-2 (item 32, owner's decision 2026-10-30): no camera assist. The lock turns the character model and the attack's direction (the simulation's
        // MeleeLock.Align on the body's heading), never the view: the camera follows the mouse freely.
    }

    void StartHitStop(int id) { if (HitStopOn) _hitStops[id] = _connectedFor; }

    /// <summary>Seconds the player's attack or reaction clip is held back by a hit-stop now (0 without one).</summary>
    float HitStopHold(int id) => MathF.Max(_hitStops.TryGetValue(id, out double start) ? MeleePresentation.Hold((float)(_connectedFor - start)) : 0f, ChargeStopHold(id));   // anim/melee-interaction: a charge connect's

    MeleeShow ShowOf(int id)
    {
        if (!_meleeShows.TryGetValue(id, out var show)) _meleeShows[id] = show = new MeleeShow();
        return show;
    }

    /// <summary>
    /// The attack's clips to pose at <paramref name="time"/>: the variant below the start distance (posed) and the one above with its weight (laid over it),
    /// each at its own time: the simulation spreads every variant's root motion over the attack's duration, so a clip of another length is posed at the
    /// same fraction of itself (the chainsword's third attack has variants of different lengths). Or the single clip <paramref name="fallback"/> at
    /// <paramref name="time"/> (no reach data, a variant the rig cannot decode, <c>--no-melee-variants</c>). Notes the start distance when a new attack begins.
    /// </summary>
    (string Base, float BaseTime, string? Over, float OverTime, float Weight) AttackVariants(PlayerState s, bool own, string fallback, float time)
    {
        if (NoMeleeVariants || _config is null || _assets is null || Combat.MeleeStepOf(_config, s) is not { Reach: { Variants.Count: > 1 } reach } step) return (fallback, time, null, 0f, 0f);
        var show = ShowOf(s.PlayerId);
        if (show.Step != s.MeleeStep || show.Heavy != s.MeleeHeavy || s.MeleeTime + 1e-4f < show.Time)
        {
            show.Step = s.MeleeStep; show.Heavy = s.MeleeHeavy;
            show.StartDistance = MeleePresentation.StartDistance(reach, step.Duration, s.MeleeTime, TargetDistance(s, own));
            if (own && _bot && _client is not null) { if (_client.MeleeLockTarget > 0) _meleeLockSwings++; }
        }
        show.Time = s.MeleeTime;
        var (low, high, w) = reach.Blend(show.StartDistance);
        if (low is null || ClipLength(low.Clip) is not float lowLength) return (fallback, time, null, 0f, 0f);
        float Scaled(float length) => step.Duration > 1e-4f ? time * length / step.Duration : time;
        if (high is null || w < 0.02f || ClipLength(high.Clip) is not float highLength) return (low.Clip, Scaled(lowLength), null, 0f, 0f);
        if (w > 0.98f) return (high.Clip, Scaled(highLength), null, 0f, 0f);
        return (low.Clip, Scaled(lowLength), high.Clip, Scaled(highLength), w);

        float? ClipLength(string clip) => clip.Length > 0 && _assets.Animation(clip) is { } anim ? anim.Animation.Duration : null;
    }

    /// <summary>The flat distance to the target an attack began on: own, the lock the client sent; others, the enemy the lock search takes along their heading; null: none.</summary>
    float? TargetDistance(PlayerState s, bool own)
    {
        if (_client is null || _config is null) return null;
        if (own)
        {
            if (_client.MeleeLockTarget <= 0) return null;
            foreach (var o in _lastOthers) if (o.PlayerId == _client.MeleeLockTarget && o.Alive) return MeleeLock.Offset(s, o).Length();
            return null;
        }
        var view = new Dictionary<int, PlayerState>();
        foreach (var o in _lastOthers) if (o.PlayerId != s.PlayerId) view[o.PlayerId] = o;
        var me = _client.Predicted;
        if (me.PlayerId > 0) view[me.PlayerId] = me;
        int id = MeleeLock.Acquire(s, MeleeLock.Forward(s.Yaw), view, _config, _client.Collision, _config.MeleeLock.Range, _config.MeleeLock.HalfAngle);
        return id > 0 ? MeleeLock.Offset(s, view[id]).Length() : null;
    }

    /// <summary>Whether the body moves by the attack's own root motion (the clip keeps the legs: no hand-over to the locomotion).</summary>
    bool AttackRootDriven(PlayerState s, MeleeStep step, float time)
    {
        if (_config is null || step.Reach is not { } reach) return false;
        // The blend shown (AttackVariants' start distance for this attack), else the start blend.
        float blend = _meleeShows.TryGetValue(s.PlayerId, out var show) && show.Step == s.MeleeStep && show.Heavy == s.MeleeHeavy
            ? show.StartDistance : Math.Clamp(reach.DefaultDistance, reach.Range.Min, reach.Range.Max);
        return MeleePresentation.RootDriven(step, time, new V2(s.Velocity.X, s.Velocity.Y), s.Yaw, _config.TickSeconds, blend, _config.LungeScale);
    }

    void MeleeLockForget(int id) { _meleeShows.Remove(id); _hitStops.Remove(id); }

    /// <summary>The bot log's melee part: swings with a lock and hits shown.</summary>
    string MeleeLockLogText() => $"melee lock swings {_meleeLockSwings} hit-stops {_meleeHitsShown}";
}
