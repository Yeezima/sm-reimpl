using System.Collections.Generic;
using Godot;
using SmClient.Bullets;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// fx/tracers-impact (owner, 2026-11-07; client only, no simulation, wire or protocol change): the tracer is one straight line from the muzzle to the impact
/// (<see cref="TracerRules"/>), the plasma gun's and pistol's uncharged shot is a blue bolt (<see cref="PlasmaBolt"/>), and a shot that launches a projectile
/// paints no impact until its projectile gets there (<see cref="TracerRules.DrawnAtFire"/>).
/// Test aids: <c>--bent-bullets</c>, <c>--old-plasma-bolt</c>, <c>--old-charged-impact</c> (the looks before), <c>--tracer-probe</c> (a <c>tracer-probe</c> line per shot: the
/// drawn tracer's end against the impact's place, the path's largest distance from the barrel-to-impact line; for a projectile shot, the impacts drawn at fire
/// and, at its detonation, the seconds since the shot and the distance from the ball's last drawn place).
/// </summary>
public partial class NetworkView
{
    bool? _oldPlasmaBolt, _oldChargedImpact, _tracerProbe;
    /// <summary>False with <c>--old-plasma-bolt</c>: the data's tracer colours and sizes, as before fx/tracers-impact.</summary>
    bool PlasmaBoltOn => !(_oldPlasmaBolt ??= CmdFlag("old-plasma-bolt"));
    /// <summary><c>--old-charged-impact</c>: a projectile shot is traced and its impact painted when fired, as before fx/tracers-impact.</summary>
    bool OldChargedImpact => _oldChargedImpact ??= CmdFlag("old-charged-impact");
    bool TracerProbe => _tracerProbe ??= CmdFlag("tracer-probe");
    bool? _botChargeAll;
    /// <summary><c>--bot-charge-all</c> (test aid, fx/tracers-impact): a bot holds every press of a charge weapon to its last firing stage (a real charged shot each time) instead of 4 s in 12.</summary>
    bool BotChargeAll => _botChargeAll ??= CmdFlag("bot-charge-all");

    int _plasmaBolts, _projectileShotsHeld;

    /// <summary>Whether the projectile is an own plasma shot drawn from the newest snapshot (<c>WithOwnPlasmaAhead</c>): its detonation is due when it arrives.</summary>
    bool DrawnAhead(int id) => !OldPlasmaRelease && !OldBarrel && _ownPlasmaIds.Contains(id);

    /// <summary>The bolt's colour for a plasma wargear (Chaos purple, loyalist blue).</summary>
    static Color? PlasmaBoltTint(string wargear)
    {
        var (r, g, b) = PlasmaBolt.Colour(wargear);
        return new Color(r, g, b);
    }

    /// <summary>
    /// fx/tracers-impact: a weapon projectile that went off on a player's body (within <see cref="DirectHitReach"/> of a shown player's axis, between the feet and the head) shows the body hit when
    /// it arrives: the armour-hit glow and the model's flash, as a ray hit does (<c>FxImpact</c>). Before, an own charged shot drew them when fired (traced like a ray) and other players' never did.
    /// The armour state is not on a detonation: unknown, so the armour variant plays (M38's rule).
    /// </summary>
    void DetonationBodyHit(ProjectileView d)
    {
        int victim = -1; float best = DirectHitReach; Num.Vector3 feet = default;
        void Consider(int id, Num.Vector3 at)
        {
            float dz = d.Position.Z - at.Z, horizontal = Num.Vector2.Distance(new(d.Position.X, d.Position.Y), new(at.X, at.Y));
            if (dz < -0.3f || dz > 2.4f || horizontal >= best) return;
            best = horizontal; victim = id; feet = at;
        }
        foreach (var o in _lastOthers) if (o.Alive) Consider(o.PlayerId, o.Position);
        if (_client is { } c && c.Predicted.Alive) Consider(c.PlayerId, c.Predicted.Position);
        if (victim < 0) return;
        var pos = ToGodot(d.Position);
        var toBody = ToGodot(feet with { Z = d.Position.Z }) - pos;
        var dir = toBody.LengthSquared() > 1e-4f ? toBody.Normalized() : Vector3.Down;
        FxArmourHit(true, pos, dir, victim, 0.0);
        CharacterFlashHit(victim, null, pos);
        _detonationBodyHits++;
    }

    /// <summary>How far from a player's vertical axis a detonation counts as on the body, metres (ours: the capsule's 0.45 m radius plus a little).</summary>
    const float DirectHitReach = 0.6f;
    int _detonationBodyHits;

    // ---- --tracer-probe ----

    (Vector3 From, Vector3? Via, Vector3 To)? _probeTracer;
    Vector3? _probeImpact;
    bool _probeImpactAny;
    readonly Dictionary<int, (double At, string Weapon)> _probeOwnProjectileShots = new();
    readonly Dictionary<int, Vector3> _probeBallLast = new();
    readonly Dictionary<int, double> _probeBallLastAt = new();
    double _probeProjectileShotAt = double.NegativeInfinity;
    string _probeProjectileWeapon = "";
    int _probeProjectileWeaponIndex = -1, _probeProjectileStage = -1;
    readonly HashSet<int> _probeBallSeen = new();
    int _probeImpactsAtFire;

    /// <summary>From <see cref="PlayTracer"/> / <see cref="PlayFallbackBullet"/>: the tracer as drawn (the effect's own origin and target when one played).</summary>
    void TracerProbeNote(FxEffect? e, Vector3 from, Vector3 end, Vector3? via = null)
    {
        if (!TracerProbe || e is null) return;
        _probeTracer = (e.Origin, via, e.Target ?? end);
    }

    void TracerProbeNote(bool played, Vector3 from, Vector3 end, Vector3? via = null)
    {
        if (!TracerProbe || !played) return;
        _probeTracer = (from, via, end);
    }

    /// <summary>From <c>FxImpact</c>: where its first effect was placed.</summary>
    void TracerProbeImpact(Vector3 position)
    {
        if (TracerProbe && _probeImpact is null) _probeImpact = position;
    }

    /// <summary>After a fired shot's tracer and impact (<c>FxShot</c>): one line comparing them.</summary>
    void TracerProbeShot(int shooter, WeaponSpec spec, Vector3 end, Vector3 normal, float travel, bool tracered, bool projectileShot, ShotHit hit)
    {
        if (!TracerProbe) { _probeTracer = null; _probeImpact = null; _probeImpactAny = false; return; }
        if (projectileShot)
        {
            if (_probeImpactAny) _probeImpactsAtFire++;
            GD.Print($"tracer-probe projectile shot P{shooter} {spec.Name} at fire: hit {hit}, impact effects {(_probeImpactAny ? $"yes{(_probeImpact is null ? " (armour hit, sparks, flash)" : " (hit container)")}, {travel:F3} s after the shot" : "no")}, tracer {(_probeTracer is null ? "none" : "drawn")} (impacts painted at fire so far {_probeImpactsAtFire})");
        }
        if (_probeTracer is { } t)
        {
            var path = (N(t.From), t.Via is { } v ? N(v) : (Num.Vector3?)null, N(t.To));
            float gap = TracerRules.EndGap(path, N(end));
            float impactGap = _probeImpact is { } ip ? ip.DistanceTo(t.To) : float.NaN;
            float off = TracerRules.MaxOffLine(path, N(t.From), N(end));
            GD.Print($"tracer-probe shot P{shooter} {spec.Name}: tracer {t.From.DistanceTo(t.To):F2} m{(t.Via is null ? " straight" : " bent")}, end to the decal point {gap:F3} m (to the first impact effect, lifted 2 cm off the surface, {impactGap:F3} m), " +
                     $"largest distance from the barrel-to-impact line {off:F3} m, impact delay {travel:F3} s{(tracered ? "" : " (fallback streak)")}");
        }
        _probeTracer = null; _probeImpact = null; _probeImpactAny = false;
    }

    /// <summary>A tracer held back until the pistol is up (<c>NetworkView.PistolTracer.cs</c>), played after its shot's <see cref="TracerProbeShot"/>: its own line, so its note is not read as the next shot's.</summary>
    void TracerProbeDeferred(int shooter, string wargear, Vector3 end)
    {
        if (!TracerProbe) return;
        if (_probeTracer is { } t)
        {
            var path = (N(t.From), t.Via is { } v ? N(v) : (Num.Vector3?)null, N(t.To));
            GD.Print($"tracer-probe shot P{shooter} {wargear} (tracer after the pistol's raise): tracer {t.From.DistanceTo(t.To):F2} m{(t.Via is null ? " straight" : " bent")}, end to the decal point {TracerRules.EndGap(path, N(end)):F3} m, " +
                     $"largest distance from the barrel-to-impact line {TracerRules.MaxOffLine(path, N(t.From), N(end)):F3} m");
        }
        _probeTracer = null;
    }

    /// <summary>A projectile shot held back from impacting at fire (own: remembered for its detonation's line).</summary>
    void TracerProbeProjectileShot(int shooter, WeaponSpec spec, int stage)
    {
        if (!TracerProbe) return;
        if (shooter != _client?.PlayerId) return;
        _probeProjectileShotAt = _connectedFor; _probeProjectileWeapon = spec.Name + (stage > 0 ? $" stage {stage}" : "");
        _probeProjectileStage = stage; _probeProjectileWeaponIndex = -1;
        for (int i = 0; _config is not null && i < _config.Weapons.Count; i++) if (ReferenceEquals(_config.Weapons[i], spec)) { _probeProjectileWeaponIndex = i; break; }
        GD.Print($"tracer-probe projectile shot P{shooter} {_probeProjectileWeapon}: {(OldChargedImpact ? "traced and impacted at fire (--old-charged-impact)" : "no tracer or impact at fire")}");
    }

    /// <summary>Each frame for a projectile in flight: its drawn place (for the detonation's line); an own projectile within 0.3 s of an own projectile shot is that shot's.</summary>
    void TracerProbeBall(ProjectileView q, Vector3 position)
    {
        if (!TracerProbe) return;
        _probeBallLast[q.Id] = position; _probeBallLastAt[q.Id] = _connectedFor;
        if (_probeBallSeen.Add(q.Id) && !q.Thrown && q.Weapon == _probeProjectileWeaponIndex && q.Stage == _probeProjectileStage && _connectedFor - _probeProjectileShotAt < 0.3)
            _probeOwnProjectileShots[q.Id] = (_probeProjectileShotAt, _probeProjectileWeapon);
    }

    /// <summary>At a detonation's effects (<c>FxDetonation</c>): for an own projectile shot, how long after the shot and how far from the ball's last drawn place.</summary>
    void TracerProbeDetonation(ProjectileView d, Vector3 position)
    {
        if (!TracerProbe) return;
        string ball = _probeBallLast.TryGetValue(d.Id, out var last) ? $"{last.DistanceTo(position):F3} m from the ball's last drawn place, drawn last {_connectedFor - _probeBallLastAt.GetValueOrDefault(d.Id):F3} s before" : "ball never drawn";
        if (_probeOwnProjectileShots.Remove(d.Id, out var shot))
            GD.Print($"tracer-probe detonation of own {shot.Weapon} #{d.Id}: impact effects {_connectedFor - shot.At:F3} s after the shot, {ball}; impacts painted at fire so far {_probeImpactsAtFire}");
        _probeBallLast.Remove(d.Id); _probeBallLastAt.Remove(d.Id);
    }
}
