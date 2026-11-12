using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmNet;
using SmSim;
using Num = System.Numerics;
using static SmClient.ImpactRules.PlayerHitFxRules;

namespace SmClient;

/// <summary>
/// onhit-range-intensity (owner, 2026-11-13: "any melee damage should also trigger the glows", "charge/bull rush, air slash landing, ground pound, light melee combo, F stun strike moves, any sort
/// of damage should trigger the sparks and glows"; client presentation only): every damage the server CONFIRMS on a player shows the yellow (armour absorbed) or red (health hit) model glow and the
/// yellow or red sparks (<see cref="PlayerHitFxRules.LookFor"/>), not only a gun's. No geometry is guessed (no team, line-of-sight, radius or cone test: an earlier version that did lit allies, the
/// thrower and players behind walls, and was removed). Two confirmed signals exist on the wire:
/// <list type="bullet">
/// <item>the own hit reports (<see cref="OwnHitReportGlow"/>): a melee or splash damage the own player dealt, with its armour and health split, so yellow when armour absorbed some and red when only health
/// was hit (light combo, F stun strikes, kicks, charge, ground pound, jump landing, air slash, the own grenades; a report names the last victim of its tick);</item>
/// <item>the damage mark of every player's record (protocol 71, <see cref="DamageMarkGlows"/>): a counter the server raises on every damage a player takes with the armour and health bits of the last one, so
/// every damage on every player shows in every view with the right colour (light combo, which only flinches, hyper-armoured charges and melee kills included; before the mark, only a started hit reaction was
/// visible and always read yellow).</item>
/// </list>
/// A shot has its own record (<c>FxImpact</c>). One glow per damage event: <see cref="PlayerHitFxRules.DedupePasses"/>, stamped by every glow (<c>CharacterFlashHit</c>). The own player's own body is not drawn, so
/// nothing is added there. <c>--no-player-hit-fx</c> keeps the glows and drops the sparks; <c>--no-damage-glow</c> removes these melee and blast glows.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-damage-glow</c>: melee, charge, pound, slash and grenade damage draws no glow or sparks on its victim (the behaviour before 2026-11-13; shots keep theirs).</summary>
    bool NoDamageGlow => _noDamageGlow ??= CmdFlag("no-damage-glow");
    bool? _noDamageGlow;
    readonly Dictionary<int, double> _damageGlowAt = new();
    int _damageGlows;

    /// <summary>The glow and sparks of one confirmed damage on <paramref name="victim"/> (another player): <paramref name="armourUp"/> null reads as armour up (yellow).</summary>
    void PlayerDamageGlow(Num.Vector3 attackerAt, int victim, Num.Vector3 victimAt, bool? armourUp, DamageKind kind, string source)
    {
        if (NoDamageGlow || _client is null || _config is null || _fx is null || !ShowsOnModel(victim, _client.PlayerId)) return;
        if (_damageGlowAt.TryGetValue(victim, out double last) && !DedupePasses(_connectedFor, last)) return;
        var toward = new Num.Vector3(attackerAt.X - victimAt.X, attackerAt.Y - victimAt.Y, 0f);
        toward = toward.LengthSquared() > 1e-6f ? Num.Vector3.Normalize(toward) : new Num.Vector3(0f, 0f, 1f);
        var at = ToGodot(victimAt + new Num.Vector3(0f, 0f, 1.3f) + toward * 0.3f);
        var dir = -DirToGodot(toward);   // the way the blow travels: toward the victim
        var look = PlayerHitFxRules.LookFor(kind, MatchExtras, PlayerHitFxFlag, PlayerHitBurstFlag);
        bool up = armourUp ?? true;
        CharacterFlashHit(victim, armourUp, at);   // the glow on the model (yellow on armour, red re-flash on none); stamps the dedupe time
        if (look.HasFlag(HitLook.Sparks))
        {
            FxArmourHit(up, at, dir, victim, 0.0);   // the data's own armour-hit glows (red variants when the armour is gone)
            if (up) ArmourSparks(at, dir, 0.0, victim); else RedSparks(at, dir, 0.0);
        }
        _damageGlows++;
        if (LogHitFx) GD.Print($"HITFX damage {kind} ({source}) on P{victim} armour {(armourUp is null ? "unknown (up)" : armourUp.Value ? "up" : "down")} at {at.X:F1},{at.Y:F1},{at.Z:F1} (total {_damageGlows}, t {_connectedFor:F2})");
    }

    /// <summary>The own hit report (<see cref="UpdateHits"/>): a melee or splash damage the server confirmed on the report's last victim shows its glow, yellow when armour took some, red when only health did.</summary>
    void OwnHitReportGlow(HitReport hits)
    {
        if (_client is null || !ReportNeedsGlow(hits.LastMelee, hits.LastSplash, hits.Hits)) return;
        bool? up = ArmourUpOfDamage(hits.ShieldDamage, hits.HealthDamage);
        if (up is null && !hits.LastKilled) return;
        foreach (var victim in _lastOthers)
        {
            if (victim.PlayerId != hits.LastVictim || hits.LastVictim <= 0) continue;
            PlayerDamageGlow(_client.Predicted.Position, victim.PlayerId, victim.Position, up,
                hits.LastMelee ? DamageKind.Melee : DamageKind.Blast, hits.LastMelee ? "own melee report" : "own splash report");
            break;
        }
    }

    readonly Dictionary<int, (byte Count, bool Alive)> _damageMarkSeen = new();
    readonly List<int> _damageMarkGone = new();

    /// <summary>
    /// Protocol 71: every other player's record carries a damage counter and the last damage's shield and health bits (<see cref="PlayerState.DamageCount"/>); a change since the record before is a damage the
    /// server applied (any weapon, melee kind, blast or Musou enemy hit), shown as the glow and sparks, yellow when it took shield and red when only health. Called each frame after the remote shots, so a shot
    /// that already drew its own glow (stamped) is not drawn twice. A corpse and a respawn show nothing (<see cref="PlayerHitFxRules.MarkShowsGlow"/>).
    /// </summary>
    void DamageMarkGlows()
    {
        if (_client is null) return;
        var me = _client.Predicted.Position;
        foreach (var o in _lastOthers)
        {
            bool known = _damageMarkSeen.TryGetValue(o.PlayerId, out var seen);
            _damageMarkSeen[o.PlayerId] = (o.DamageCount, o.Alive);
            if (!MarkShowsGlow(known, seen.Alive, o.Alive, seen.Count, o.DamageCount)) continue;
            if (LogHitFx) GD.Print($"HITFX mark P{o.PlayerId} counter {seen.Count}->{o.DamageCount} shield {o.DamageShield} health {o.DamageHealth} (t {_connectedFor:F2})");
            PlayerDamageGlow(me, o.PlayerId, o.Position, ArmourUpOfMark(o.DamageShield, o.DamageHealth), DamageKind.Melee, "damage mark");
        }
        if (_damageMarkSeen.Count <= _lastOthers.Count + 4) return;
        _damageMarkGone.Clear();
        foreach (var id in _damageMarkSeen.Keys) { bool present = false; foreach (var o in _lastOthers) if (o.PlayerId == id) { present = true; break; } if (!present) _damageMarkGone.Add(id); }
        foreach (var id in _damageMarkGone) _damageMarkSeen.Remove(id);
    }
}
