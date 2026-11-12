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
/// <item>the hit reaction another player's snapshot record starts (<see cref="ReactionDamageGlow"/>, <see cref="PlayerHitFxRules.ReactionStarted"/>): the server's answer to a melee or blast damage that lands
/// on someone else's victim. Its armour state is not on the wire (a snapshot carries no other player's health or shield), so it reads yellow, and a reaction-less damage (hyper armour) shows nothing in
/// another player's view.</item>
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

    /// <summary>A reaction just started on <paramref name="p"/> (another player's snapshot record): the server confirmed a melee or blast damage on him; its glow, unless the own hit report draws it.</summary>
    void ReactionDamageGlow(PlayerState p, int ownId)
    {
        if (_client is null || p.PlayerId == ownId) return;
        var source = _reactionSource.TryGetValue(p.PlayerId, out var known) && _connectedFor - known.At < 0.5 ? known : (Source: 0, At: 0.0, Charge: false);
        if (source.Source == ownId && ownId > 0) return;   // the own strike: the hit report names the armour state and draws it
        var from = p.Position;
        foreach (var o in _lastOthers) if (o.PlayerId == source.Source) { from = o.Position; break; }
        PlayerDamageGlow(from, p.PlayerId, p.Position, null, source.Charge ? DamageKind.Charge : DamageKind.Melee, "reaction");
    }
}
