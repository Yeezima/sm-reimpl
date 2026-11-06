using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.ImpactRules;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// pvp-hit-fx (owner, 2026-11-06, classic PvP; three reference images of the original and a short clip): the look of a shot on a player.
/// <list type="number">
/// <item><b>Armour hit</b> (armour left after it): a subtle yellow glow, brightest where the bullet struck (<see cref="CharacterViewer.HitSpotStrength"/>; ours), with the data's own armour-hit effects as before.</item>
/// <item><b>Armour break</b>: the data's break effect <c>sm_armor_break_shoulders</c> (blueprint <c>armourshield_broken</c>, on <c>rig_spined</c>: a red centre glow, a white flash, red shoulder light,
/// sparks; *original asset loaded*), which we did not play before, with the engulfing red body glow of fx/red-glow-blood instead of the red ring of 2026-11-02.</item>
/// <item><b>Hit with no armour</b>: a randomized red-orange burst with dark smoke at the hit point (<see cref="HealthHitBurst"/>, ours, built from the install's sprites), replacing the body re-flash and
/// the red droplet streaks; the weapon's own <c>broken_ceramite</c> effect and <c>sm_armor_dmg_bodyglow_red</c> still play.</item>
/// </list>
/// Musou Mode's gore is untouched (its enemies' hits do not come here). Before / after: <c>--old-hit-glow</c>, <c>--old-break-ring</c>, <c>--old-health-hit</c>. Aids: <c>--log-hit-fx</c>,
/// <c>--pvp-hit-fx-demo armour|break|health|cycle</c> (every <c>--pvp-hit-fx-every</c> s, default 2, a shot on the other player nearest the camera, from the camera's side, through the real impact path).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--old-hit-glow</c>: the even yellow rim flash for an armour hit (no spot at the hit point).</summary>
    bool OldHitGlowFlag => _oldHitGlow ??= CmdFlag("old-hit-glow");
    bool? _oldHitGlow;
    /// <summary><c>--old-break-ring</c>: the armour's break flash in the recharge glow's red ring (2026-11-02), not the engulfing red glow, and without the data's break effect.</summary>
    bool? _oldRegenGlow;
    bool OldRegenGlowFlag => _oldRegenGlow ??= CmdFlag("old-regen-glow");
    bool OldBreakRingFlag => _oldBreakRing ??= CmdFlag("old-break-ring");
    bool? _oldBreakRing;
    /// <summary><c>--old-health-hit</c>: a hit with no armour re-flashes the red body glow and sprays the red streaks (fx/red-glow-blood), no burst.</summary>
    bool OldHealthHitFlag => _oldHealthHit ??= CmdFlag("old-health-hit");
    bool? _oldHealthHit;
    /// <summary><c>--log-hit-fx</c>: a HITFX line per break effect and burst.</summary>
    bool LogHitFx => _logHitFx ??= CmdFlag("log-hit-fx");
    bool? _logHitFx;

    readonly Dictionary<int, (Vector3 Local, double At)> _hitPoints = new();
    readonly Random _burstRandom = new();
    bool _burstBuilt;
    int _bursts, _breakFx;

    /// <summary>The variants of the burst, built once from the loaded effects' own sprites (none when they are missing: then nothing is drawn for it).</summary>
    bool HealthBurstReady()
    {
        if (_fx is null || !_fx.IsReady) return false;
        if (_burstBuilt) return _fx.System(HealthHitBurst.Name(0)) is not null;
        _burstBuilt = true;
        var sources = new[] { "bolter_dmg_body_tough_mp", "plasma_dmg_body_mp", "sm_armor_dmg_bodyglow", "sm_armor_dmg" }.Select(n => _fx.System(n)).OfType<CSystem>().ToList();
        var built = HealthHitBurst.Build(sources);
        if (built.Count == 0) { GD.Print("hit fx: the health-hit burst's sprites are not in the loaded effects; no burst"); return false; }
        _fx.Preload(built.ToDictionary(b => b.Name), new Dictionary<string, byte[]>());
        GD.Print($"hit fx: {built.Count} health-hit burst variants built");
        return true;
    }

    /// <summary>A hit on a body with no armour: one of the burst's variants at the hit point, its axis back toward the shooter, turned at random about it, its size varied a little.</summary>
    void HealthBurstAt(Vector3 end, Vector3 dir, double delay, int victim = -1)
    {
        if (_fx is null || !HealthBurstReady()) return;
        if (_connectedFor - _bloodAt < 0.04) return;   // rapid fire: at most one burst per 40 ms
        _bloodAt = _connectedFor;
        var back = dir.LengthSquared() > 1e-6f ? -dir.Normalized() : Vector3.Up;
        var axes = AxesFromY(back).Rotated(back, (float)(_burstRandom.NextDouble() * Math.Tau));
        float scale = HealthHitBurst.Size * (0.85f + (float)_burstRandom.NextDouble() * 0.35f);
        int variant = _burstRandom.Next(HealthHitBurst.Variants);
        if (_fx.Play(HealthHitBurst.Name(variant), end + back * 0.05f, axes, scale, null, StickToPlayer(victim, end + back * 0.05f, axes), 0f, delay, false, 1) is not null)
        {
            _bursts++;
            if (LogHitFx) GD.Print($"HITFX burst {HealthHitBurst.Name(variant)} at {end.X:F1},{end.Y:F1},{end.Z:F1} scale {scale:F2} (total {_bursts})");
        }
    }

    /// <summary>The data's break effects (<c>armourshield_broken</c>: <c>sm_armor_break_shoulders</c> on <c>rig_spined</c>) on a player whose armour just broke. Not on the own body (the camera is at it).</summary>
    void ArmourBreakFx(int victim)
    {
        if (_fx is null || _catalogue is null || _client is null || victim == _client.PlayerId || OldBreakRingFlag) return;
        foreach (var p in _catalogue.Player.ArmourBroken)
        {
            if (_fx.System(p.Name) is null) continue;
            var follow = p.Marker.Length > 0 ? BodyMarker(victim, p.Marker) ?? BodyMarker(victim, "RIG_SpineD") ?? BodyMarker(victim, "RIG_SpineA") : null;
            if (follow?.Invoke() is not { } place) continue;
            if (_fx.Play(p.Name, place.Position, Basis.Identity, p.Scale <= 0f ? 1f : p.Scale, null, p.Attached ? follow : null, p.Lifetime, 0.0, false, 2) is not null)
            {
                _breakFx++;
                if (LogHitFx) GD.Print($"HITFX break {p.Name} on player {victim} at {place.Position.X:F1},{place.Position.Y:F1},{place.Position.Z:F1} (total {_breakFx})");
            }
        }
    }

    /// <summary>The hit glow's point on a model now (world), while the hit flash can still be seen; null otherwise.</summary>
    Vector3? HitPointOf(int id, CharacterViewer model)
    {
        if (!_hitPoints.TryGetValue(id, out var h)) return null;
        if (_connectedFor - h.At > ArmourFlashTracker.HitSeconds + 0.1) { _hitPoints.Remove(id); return null; }
        return model.ToGlobal(h.Local);
    }

    // ---- --pvp-hit-fx-demo ----

    string? _pvpDemo;
    double _pvpDemoAt = double.NegativeInfinity;
    int _pvpDemoStep;

    /// <summary>The demo: a shot through the real impact path (<see cref="FxImpact"/>) on the other player nearest the camera, at his chest a little off centre, from the camera's side.</summary>
    void PvpHitFxDemo()
    {
        _pvpDemo ??= CmdFlag("pvp-hit-fx-demo") ? (CmdArg("pvp-hit-fx-demo") ?? "cycle") : "";
        if (_pvpDemo.Length == 0 || _catalogue is null || _fx is null || !_fx.IsReady) return;
        double every = double.TryParse(CmdArg("pvp-hit-fx-every"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double e) && e > 0.2 ? e : 2.0;
        if (_connectedFor - _pvpDemoAt < every) return;
        var camera = GetViewport().GetCamera3D();
        if (camera is null) return;
        CharacterViewer? target = null; int id = 0; float best = float.MaxValue;
        foreach (var (pid, c) in _remotes)
        {
            if (!GodotObject.IsInstanceValid(c) || !c.IsInsideTree() || !c.Visible) continue;
            float d = c.GlobalPosition.DistanceSquaredTo(camera.GlobalPosition);
            if (d < best) { best = d; target = c; id = pid; }
        }
        if (target is null) return;
        var w = _catalogue.Weapons.Values.FirstOrDefault(x => x.Wargear.Contains("bolter", StringComparison.OrdinalIgnoreCase) && !x.Wargear.Contains("heavy", StringComparison.OrdinalIgnoreCase)) ?? _catalogue.Weapons.Values.FirstOrDefault();
        if (w is null) return;
        _pvpDemoAt = _connectedFor;
        string kind = _pvpDemo == "cycle" ? (_pvpDemoStep++ % 3) switch { 0 => "armour", 1 => "break", _ => "health" } : _pvpDemo;
        var chest = BodyMarker(id, "RIG_SpineD")?.Invoke()?.Position ?? target.GlobalPosition + Vector3.Up * 1.3f;
        var toCam = camera.GlobalPosition - chest; toCam.Y = 0f;
        toCam = toCam.LengthSquared() > 1e-4f ? toCam.Normalized() : Vector3.Forward;
        var side = toCam.Cross(Vector3.Up).Normalized();
        var point = chest + toCam * 0.28f + side * (float)(_burstRandom.NextDouble() * 0.3 - 0.15) + Vector3.Up * (float)(_burstRandom.NextDouble() * 0.3 - 0.05);
        var dir = (point - camera.GlobalPosition).Normalized();
        switch (kind)
        {
            case "armour":
                _armourFlash.Reset(id);
                FxImpact(w, w.Basic, ShotHit.Body, 0, point, -dir, dir, 0.0, id, true);
                break;
            case "break":
                _armourFlash.Reset(id);
                FxImpact(w, w.Basic, ShotHit.Body, 0, point, -dir, dir, 0.0, id, true);
                CharacterFlashBroke(id);
                break;
            default:
                FxImpact(w, w.Basic, ShotHit.Body, 0, point, -dir, dir, 0.0, id, false);
                break;
        }
        GD.Print($"PVPHITFX demo {kind} on player {id} at {point.X:F2},{point.Y:F2},{point.Z:F2} (t {_connectedFor:F2})");
    }
}
