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
/// of damage should trigger the sparks and glows"; client presentation only): every damage a player takes shows the yellow (armour) or red (health) model glow and the yellow or red sparks
/// (<see cref="PlayerHitFxRules.LookFor"/>), not only a gun's. Until now only shots did; a melee strike drew the power weapons' flash and nothing on the victim. The sources wired here, in every
/// player's view, are the melee strikes at their hit time (light combo, F stun strikes, kicks, heavy strikes, stomps: the presented victim, or every enemy in a stomp's blast radius), the charge
/// and dash-attack connect, the jump pack's ground pound and landing blast (every enemy within its radius), the air slash's cone, and a thrown grenade's blast (every player within the blast
/// radius, a presentation estimate: the detonation does not say whom it hurt). The armour state of a victim of someone else's melee is not known, so it reads yellow; the own strikes use the last hit
/// report's shield (<see cref="OwnHitArmoured"/>). The own player's own body is not drawn, so nothing is added there. <c>--no-player-hit-fx</c> keeps the glows and drops the sparks.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-damage-glow</c>: melee, charge, pound, slash and grenade damage draws no glow or sparks on its victim (the behaviour before 2026-11-13; shots keep theirs).</summary>
    bool NoDamageGlow => _noDamageGlow ??= CmdFlag("no-damage-glow");
    bool? _noDamageGlow;
    readonly Dictionary<int, double> _damageGlowAt = new();
    int _damageGlows;

    /// <summary>The glow and sparks of one damage on <paramref name="victim"/> (another player), at <paramref name="point"/> (null: his chest toward the attacker).</summary>
    void PlayerDamageGlow(Num.Vector3 attackerAt, int victim, Num.Vector3 victimAt, Num.Vector3? point, bool? armourUp, DamageKind kind, string source)
    {
        if (NoDamageGlow || victim <= 0 || _client is null || victim == _client.PlayerId || _config is null || _fx is null) return;
        if (_damageGlowAt.TryGetValue(victim, out double last) && _connectedFor - last < 0.12) return;   // one set per victim per 0.12 s (a multi-hit swing, a charge and its melee step)
        _damageGlowAt[victim] = _connectedFor;
        var chest = victimAt + new Num.Vector3(0f, 0f, 1.3f);
        var toward = new Num.Vector3(attackerAt.X - victimAt.X, attackerAt.Y - victimAt.Y, 0f);
        toward = toward.LengthSquared() > 1e-6f ? Num.Vector3.Normalize(toward) : new Num.Vector3(0f, 0f, 1f);
        var at = ToGodot(point ?? chest + toward * 0.3f);
        var dir = -DirToGodot(toward);   // the way the blow travels: toward the victim
        var look = PlayerHitFxRules.LookFor(kind, MatchExtras, PlayerHitFxFlag, PlayerHitBurstFlag);
        bool up = armourUp ?? true;
        CharacterFlashHit(victim, armourUp, at);   // the glow on the model (yellow on armour, red re-flash on none)
        if (look.HasFlag(HitLook.Sparks))
        {
            FxArmourHit(up, at, dir, victim, 0.0);   // the data's own armour-hit glows (red variants when the armour is gone)
            if (up) ArmourSparks(at, dir, 0.0, victim); else RedSparks(at, dir, 0.0);
        }
        _damageGlows++;
        if (LogHitFx) GD.Print($"HITFX damage {kind} ({source}) on P{victim} armour {(armourUp is null ? "unknown (up)" : armourUp.Value ? "up" : "down")} at {at.X:F1},{at.Y:F1},{at.Z:F1} (total {_damageGlows})");
    }

    /// <summary>Every enemy player of <paramref name="attacker"/> within <paramref name="radius"/> (m, flat) and a body's height of its position.</summary>
    void PlayerDamageGlowsInRadius(in PlayerState attacker, float radius, DamageKind kind, string source, bool own)
    {
        if (radius <= 0f || _client is null) return;
        foreach (var o in _lastOthers)
        {
            if (!MeleeLock.IsEnemy(attacker, o) || System.MathF.Abs(o.Position.Z - attacker.Position.Z) > 2.5f) continue;
            if (new Num.Vector2(o.Position.X - attacker.Position.X, o.Position.Y - attacker.Position.Y).Length() > radius) continue;
            PlayerDamageGlow(attacker.Position, o.PlayerId, o.Position, null, own ? OwnHitArmoured(o.PlayerId) : null, kind, source);
        }
    }

    /// <summary>A melee strike's hit time passed for <paramref name="s"/>: the presented victim (or every enemy in a stomp's radius) shows the glow.</summary>
    void MeleeDamageGlows(in PlayerState s, bool own, MeleeStep step, List<PlayerState> all)
    {
        if (step.Stomp is { Radius: > 0f } blast) { PlayerDamageGlowsInRadius(s, blast.Radius, DamageKind.Pound, "stomp", own); return; }
        int victim = MeleePresentation.Victim(s, all, _config!, own ? _client!.MeleeLockTarget : 0);
        if (victim <= 0) return;
        foreach (var o in all) if (o.PlayerId == victim) { PlayerDamageGlow(s.Position, victim, o.Position, null, own ? OwnHitArmoured(victim) : null, DamageKind.Melee, "melee strike"); break; }
    }

    /// <summary>The air slash's cone (melee range and cone of the attacker's heading) resolved: every enemy in it shows the glow.</summary>
    void SlashDamageGlows(in PlayerState s, bool own)
    {
        if (_config is null) return;
        var forward = MeleeLock.Forward(s.Yaw);
        float minCos = System.MathF.Cos(_config.MeleeConeDegrees * System.MathF.PI / 180f);
        foreach (var o in _lastOthers)
        {
            if (!MeleeLock.IsEnemy(s, o)) continue;
            var flat = MeleeLock.Offset(s, o); float d = flat.Length();
            if (d > _config.MeleeRange + 0.5f || (d > 1e-4f && Num.Vector2.Dot(flat / d, forward) < minCos)) continue;
            PlayerDamageGlow(s.Position, o.PlayerId, o.Position, null, own ? OwnHitArmoured(o.PlayerId) : null, DamageKind.Slash, "air slash");
        }
    }

    /// <summary>A thrown grenade went off: every player within its blast radius shows the glow (an estimate: the detonation does not say whom it hurt).</summary>
    void ThrownBlastGlows(ProjectileView d)
    {
        if (_config is null || d.Weapon < 0 || d.Weapon >= _config.Thrown.Count) return;
        var spec = _config.Thrown[d.Weapon];
        if (spec.IsBlindGrenade) return;
        float radius = 0f;
        foreach (var sp in spec.Projectile.AllSplashes) if (!sp.BlindOnly && sp.Radius > radius) radius = sp.Radius;
        if (radius <= 0f) return;
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || System.MathF.Abs(o.Position.Z - d.Position.Z) > 3f) continue;
            if (new Num.Vector2(o.Position.X - d.Position.X, o.Position.Y - d.Position.Y).Length() > radius) continue;
            PlayerDamageGlow(d.Position, o.PlayerId, o.Position, null, null, DamageKind.Blast, "thrown blast");
        }
    }
}
