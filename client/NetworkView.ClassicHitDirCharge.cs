using System;
using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmSim;
using static SmClient.ImpactRules.ClassicHitDirRules;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// classic-hit-effects, the bull-rush charge connect (owner 2026-11-24, question card "Yes, spray charges too"; client only): a charge's confirmed damage (the victim's damage mark with an
/// enemy in his rush or its collision stop beside him) draws the directional sparks or spray along the charger's velocity at contact, by the same armour and blood rules and profiles as
/// melee (sparks in both Classic profiles, blood in Extended only), one effect per victim and charge (<see cref="ClassicHitDirRules.ChargeKey"/>). The charge's data impact effects on the
/// victim (its body_smash container, the fallback impact and the directional burst: the same kind of effect) give way when the directional look draws; they are held for
/// <see cref="ChargeFxWait"/> s for the mark and play as before when it draws nothing (Faithful's health hit) or no mark comes. The hit-stop, camera kick and shake, the stun and the
/// sounds are untouched.
/// </summary>
public partial class NetworkView
{
    /// <summary>How long a charge's data impact effects wait for the damage mark that may replace them (s; ours).</summary>
    const double ChargeFxWait = 0.4;

    sealed class ChargePending { public PlayerState Charger; public int Victim; public Num.Vector3 Contact, Along; public bool ArmourUp; public double At; }
    readonly List<ChargePending> _chargePending = new();
    readonly Dictionary<int, (int Charger, double At)> _chargeDrawn = new();
    /// <summary>Per player: in the rush last frame, rushes started (the charge key), the last rush velocity (game space).</summary>
    readonly Dictionary<int, (bool Rushing, int Count, Num.Vector3 Velocity)> _rushTrack = new();
    int _hitDirCharges;

    /// <summary>Each frame, every player: the rush's start (a new charge) and its velocity while it runs (the contact's direction).</summary>
    void TrackRush(PlayerState s)
    {
        var r = _rushTrack.GetValueOrDefault(s.PlayerId);
        bool rushing = s.Alive && s.Rush == RushPhase.Rush;
        if (rushing && !r.Rushing) r.Count++;
        if (rushing) { var v = new Num.Vector3(s.Velocity.X, s.Velocity.Y, 0f); if (v.Length() > 1f) r.Velocity = v; }
        r.Rushing = rushing;
        _rushTrack[s.PlayerId] = r;
    }

    /// <summary>
    /// From <c>ChargeImpact</c>: null to play the charge's impact effects as before (no directional look: Musou Mode, Warfront); 0 when the directional look already drew this contact or
    /// the effects wait for its mark.
    /// </summary>
    int? ClassicChargeImpactFx(PlayerState charger, PlayerState victim, Num.Vector3 contact, Num.Vector3 along, bool armourUp)
    {
        if (HitDirLook == DirLook.None || (_client is { } me && victim.PlayerId == me.PlayerId)) return null;   // the own body draws no directional look: its impact as before
        if (_chargeDrawn.TryGetValue(victim.PlayerId, out var d) && d.Charger == charger.PlayerId && _connectedFor - d.At <= ChargeFxWait + 0.2) return 0;
        _chargePending.Add(new ChargePending { Charger = charger, Victim = victim.PlayerId, Contact = contact, Along = along, ArmourUp = armourUp, At = _connectedFor });
        return 0;
    }

    /// <summary>Per frame: held charge effects whose mark did not come play now (as before, a little late).</summary>
    void ChargeFxFrame()
    {
        for (int i = _chargePending.Count - 1; i >= 0; i--)
        {
            var p = _chargePending[i];
            if (_connectedFor - p.At < ChargeFxWait) continue;
            _chargePending.RemoveAt(i);
            ChargeImpactFxPlay(p.Charger, p.Contact, p.Along, p.ArmourUp);
        }
        if (_chargeDrawn.Count > 32) _chargeDrawn.Clear();
    }

    /// <summary>The charger of a confirmed damage on <paramref name="o"/>: a held charge impact on him, else the nearest enemy in his rush or collision stop within <see cref="ChargeReach"/>.</summary>
    (PlayerState Charger, Num.Vector3 Along, ChargePending? Held)? ChargeOf(PlayerState o)
    {
        ChargePending? held = null;
        foreach (var p in _chargePending) if (p.Victim == o.PlayerId && _connectedFor - p.At <= ChargeFxWait) { held = p; break; }
        PlayerState? best = null; float bestD = ChargeReach;
        void Look(PlayerState p)
        {
            if (!p.Alive || p.PlayerId == o.PlayerId || p.Team == o.Team || !IsChargeContact(p.Rush)) return;
            if (held is not null && p.PlayerId != held.Charger.PlayerId) return;
            var d = p.Position - o.Position; d.Z = 0f;
            float l = d.Length();
            if (l < bestD) { bestD = l; best = p; }
        }
        if (_client is { } c && c.Predicted.PlayerId == c.PlayerId && c.PlayerId != o.PlayerId) Look(c.Predicted);
        foreach (var p in _lastOthers) Look(p);
        var charger = best ?? held?.Charger;
        if (charger is not { } ch) return null;
        var bearing = new Num.Vector3(o.Position.X - ch.Position.X, o.Position.Y - ch.Position.Y, 0f);
        var along = ChargeDirection(_rushTrack.GetValueOrDefault(ch.PlayerId).Velocity, bearing, ch.Yaw);
        return (ch, along, held);
    }

    /// <summary>A charge's confirmed damage on <paramref name="o"/>: true when a charger was found (the caller leaves out the old fans as for a paired melee hit).</summary>
    bool ClassicChargeMark(PlayerState o, bool shield, bool health, bool lethal)
    {
        if (ChargeOf(o) is not { } found) return false;
        var (charger, along, held) = found;
        if (held is not null) _chargePending.Remove(held);
        int key = _rushTrack.GetValueOrDefault(charger.PlayerId).Count;
        if (!_contactLedger.First(o.PlayerId, charger.PlayerId, ChargeKey(key), _connectedFor)) return true;   // the same charge again: one effect per victim and charge
        bool before = _armourFlash.ArmourUp(o.PlayerId, _connectedFor) ?? true;
        var visual = Classify(shield, health, ShieldLeftAfterMark(shield, health, before));
        // thrown on from the far side of his body along the charge: from the charger's side the streaks ran into the victim's own body and were hidden (evidence capture, 2026-11-24)
        var contactGame = o.Position + Num.Vector3.Normalize(along) * BodySurface + new Num.Vector3(0f, 0f, 1.25f);
        bool drawn = DrawCharge(o.PlayerId, charger.PlayerId, ToGodot(contactGame), DirToGodot(Num.Vector3.Normalize(along)).Normalized(), visual, lethal);
        if (drawn) _chargeDrawn[o.PlayerId] = (charger.PlayerId, _connectedFor);
        else if (held is not null) ChargeImpactFxPlay(held.Charger, held.Contact, held.Along, held.ArmourUp);   // nothing new drawn (Faithful's health hit): the old impact, as before
        _hitDirCharges++;
        if (LogHitDir) HitDirPrint($"HITDIR charge P{charger.PlayerId}->P{o.PlayerId} {visual}{(lethal ? " lethal" : "")} along {along.X:F2},{along.Y:F2} (key {key}{(held is null ? "" : ", impact held")}) shield {shield} health {health} armour-before {before} drawn {drawn} (t {_connectedFor:F2})");
        return true;
    }

    /// <summary>The charge's sparks and (Extended) spray: thrown straight along the charge from the contact; true when anything was drawn.</summary>
    bool DrawCharge(int victim, int charger, Vector3 contact, Vector3 dir, HitVisual visual, bool lethal)
    {
        var look = HitDirLook;
        bool any = false;
        NoteHitDirFocus(contact, dir);
        if (HasSparks(visual) && look.HasFlag(DirLook.MeleeSparks)) { HitSpray().SpawnMelee(contact, ReadOnlySpan<(double, Vector3)>.Empty, null, dir, new ClassicHitSpray.Look(true, 1f, ChargeSpread * 0.8f), 0.0); any = true; }
        bool drawn = false;
        if (HasBlood(visual))
            ExtrasClassicBlood(new ClassicContact(victim, charger, contact, dir, visual, false, false, lethal, false, ChargeSpread, BloodAmount(false, lethal), 0.0, null, 0, null), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        return any || drawn;
    }
}
