using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Bullets;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// fx/barrel-origin (owner, 2026-10-31: "for all guns the projectiles and weapon effects need to emit from the barrel of the weapon"; client only, no protocol change).
/// The hit-scan bullets, the lascannon beam and the melta cone already started at the weapon's <c>fx_Muzzle</c> and joined the damaging ray (NetworkView.Bullets.cs); the muzzle
/// flash, smoke and shells are placed at their markers. What did not: the visible body of a projectile weapon's shot (the plasma ball, the plasma cannon's, the launcher's grenades).
/// The server launches a projectile from the shot origin (<see cref="Combat.ShotOrigin"/>: on the camera ray, about half a metre beside and below the barrel) and a snapshot carries its
/// position only, so the ball was drawn leaving that point, not the gun.
/// <para>
/// Now: each projectile weapon's shot notes the shot origin and the muzzle as drawn at that moment (own shots: the origin the sent input gave; others': the origin rebuilt from the
/// shooter's camera, <see cref="RemoteShotOrigin"/>). When the projectile first appears in a snapshot it takes the note of the same weapon whose origin is nearest, and is drawn displaced by the
/// vector origin to muzzle while it is within the muzzle's own distance of the origin, the displacement then fading out over <see cref="BulletPaths.ConvergeDistance"/> (3 m, the bullets' value;
/// ours), so that the ball leaves the barrel and falls onto the authoritative path with no snap. The path, the detonation and the damage are the server's, unchanged; the trail effects follow the
/// drawn ball. <c>--old-barrel</c> restores the old drawing. Values are ours (spec "Weapon effects start at the barrel").
/// </para>
/// </summary>
public partial class NetworkView
{
    bool? _oldBarrel;
    /// <summary><c>--old-barrel</c> (test aid): projectiles are drawn at the server's position from the first frame, as before fx/barrel-origin.</summary>
    bool OldBarrel => _oldBarrel ??= CmdFlag("old-barrel");

    readonly record struct BarrelNote(double At, int Shooter, int Weapon, Num.Vector3 Origin, Num.Vector3 Muzzle);
    readonly List<BarrelNote> _barrelNotes = new();
    /// <summary>Per live projectile id: the shot origin and the displacement that takes it to the muzzle (game space).</summary>
    readonly Dictionary<int, (Num.Vector3 Origin, Num.Vector3 Offset)> _barrelProjectiles = new();
    readonly HashSet<int> _barrelChecked = new();
    bool? _barrelProbe;
    /// <summary><c>--barrel-probe</c> (test aid): logs each projectile that found no shot note.</summary>
    bool BarrelProbe => _barrelProbe ??= CmdFlag("barrel-probe");
    int _barrelMatched, _barrelUnmatched;

    const double BarrelNoteSeconds = 3.0;
    const float BarrelMatchReach = 14f;

    /// <summary>Whether any shot of the weapon launches a projectile (a charge weapon's basic shot may be a ray and its charged shot a ball: which one a shot was is not always known when it is seen, so every shot of such a weapon is noted).</summary>
    static bool BarrelCanLaunch(WeaponSpec w) => w.Delivery == DeliveryKind.Projectile || (w.Charge is { } c && c.Stages.Any(s => s.Shot?.Delivery == DeliveryKind.Projectile));

    /// <summary>A projectile weapon's shot was seen: remember where its origin and its muzzle were (game space).</summary>
    void BarrelNoteShot(int shooter, int weapon, Num.Vector3 origin, Vector3 muzzleWorld)
    {
        if (OldBarrel) return;
        for (int i = _barrelNotes.Count - 1; i >= 0; i--) if (_connectedFor - _barrelNotes[i].At > BarrelNoteSeconds) _barrelNotes.RemoveAt(i);
        _barrelNotes.Add(new BarrelNote(_connectedFor, shooter, weapon, origin, ToGame(muzzleWorld)));
        if (_barrelNotes.Count > 64) _barrelNotes.RemoveAt(0);
    }

    /// <summary>The displaced (drawn) game-space position of projectile <paramref name="q"/>; the snapshot position when it has no note.</summary>
    Num.Vector3 BarrelDrawn(SmNet.ProjectileView q)
    {
        var at = new Num.Vector3(q.Position.X, q.Position.Y, q.Position.Z);
        if (OldBarrel || q.Thrown) return at;
        if (_barrelChecked.Add(q.Id))
        {
            BarrelNote? best = null;
            float bestD = BarrelMatchReach * BarrelMatchReach;
            foreach (var n in _barrelNotes)
            {
                if (n.Weapon != q.Weapon || _connectedFor - n.At > BarrelNoteSeconds) continue;
                float d = Num.Vector3.DistanceSquared(n.Origin, at);
                if (d < bestD) { bestD = d; best = n; }
            }
            if (best is { } b && (b.Muzzle - b.Origin).Length() < 3f)
            {
                _barrelProjectiles[q.Id] = (b.Origin, b.Muzzle - b.Origin); _barrelMatched++;
                BarrelBackfill(q, b.Muzzle, at);
                if (BarrelProbe) GD.Print($"barrel-probe: projectile #{q.Id} of weapon {q.Weapon} (P{b.Shooter}) first seen {Num.Vector3.Distance(at, b.Muzzle):F2} m from its muzzle (the muzzle is {(b.Muzzle - b.Origin).Length():F2} m from the server's origin, the first snapshot {Num.Vector3.Distance(at, b.Origin):F2} m past it); drawn {Num.Vector3.Distance(BarrelPlaced(q.Id, at), b.Muzzle):F2} m from the muzzle");
            }
            else { _barrelUnmatched++; if (BarrelProbe) GD.Print($"barrel-probe: projectile #{q.Id} weapon {q.Weapon} at {at.X:F1},{at.Y:F1},{at.Z:F1}: no note ({_barrelNotes.Count} notes: {string.Join("; ", _barrelNotes.ConvertAll(n => $"w{n.Weapon} P{n.Shooter} {_connectedFor - n.At:F2}s o {n.Origin.X:F1},{n.Origin.Y:F1},{n.Origin.Z:F1} m {(n.Muzzle - n.Origin).Length():F2}"))})"); }
            if (_barrelChecked.Count > 4096) _barrelChecked.Clear();
        }
        return BarrelPlaced(q.Id, at);
    }

    (double At, Vector3 Muzzle, Vector3 Dir)? _barrelCam;
    bool BarrelShotRecent => _barrelCam is { } c && _connectedFor - c.At < 0.35 && Spectate == "barrel";
    /// <summary><c>--barrel-cam-remote</c>: the barrel camera follows other players' shots only (the own player stands by, <c>--bot-idle</c>).</summary>
    bool BarrelCamRemote => CmdFlag("barrel-cam-remote");
    float BarrelCamSide => FloatArg("barrel-cam-side") ?? 1.3f;
    float BarrelCamAhead => FloatArg("barrel-cam-ahead") ?? 2.2f;

    /// <summary>
    /// <c>--spectate barrel</c> (test aid): for 1.5 s after a shot the camera stands 1.3 m (--barrel-cam-side) to the side of the muzzle it left (level with it), looking along the barrel a couple of metres ahead,
    /// so that a screenshot series shows the flash, the tracer, the beam, the cone or the ball starting at the barrel. Own shots only while the own player is in a fight; others' once seen.
    /// </summary>
    (Vector3 Eye, Vector3 Target)? BarrelView()
    {
        if (_barrelCam is not { } c || _connectedFor - c.At > 1.5) return null;
        var dir = c.Dir.Normalized();
        var side = dir.Cross(Vector3.Up);
        side = side.LengthSquared() < 0.01f ? Vector3.Right : side.Normalized();
        return (c.Muzzle + dir * 0.1f + side * BarrelCamSide + Vector3.Up * 0.12f, c.Muzzle + dir * BarrelCamAhead);
    }

    bool? _barrelTest;
    /// <summary><c>--barrel-test</c> (bot test aid): the bot stands still and fires the weapon in hand in a short burst every 3 s (a charge weapon on its own cycle), aim level, whatever is ahead.</summary>
    bool BarrelTest => _barrelTest ??= CmdFlag("barrel-test");

    PlayerInput BarrelTestInput(uint tick)
    {
        var me = _client!.Predicted;
        if (CmdArg("barrel-slot") is { Length: > 0 } slot && _connectedFor is >= 3.0 and < 3.2)
            return new PlayerInput(tick, 0f, 0f, _yaw, slot.StartsWith("pi", StringComparison.OrdinalIgnoreCase) ? InputButtons.SlotPistol : InputButtons.SlotPrimary, _pitch);
        bool charge = Loadouts.InHand(_config!, me)?.Charge is not null;
        bool window = _connectedFor % 3.0 < 0.3;
        var buttons = (charge || window) && me.Alive ? BotWeaponButtons(me, tick, null) : InputButtons.None;
        return new PlayerInput(tick, 0f, 0f, _yaw, buttons, _pitch);
    }

    /// <summary>
    /// A fast projectile is first drawn some metres out (the first snapshot that holds it, shown after the interpolation delay): a bright streak is laid from the muzzle to where it first shows (the
    /// pooled streak of the bullets' muzzle leg, tinted for plasma), so that it is seen to leave the barrel. Not for a ball first seen within <see cref="BarrelBackfillMin"/> of the muzzle.
    /// </summary>
    void BarrelBackfill(SmNet.ProjectileView q, Num.Vector3 muzzle, Num.Vector3 firstSeen)
    {
        var to = BarrelPlaced(q.Id, firstSeen);
        if (Num.Vector3.Distance(muzzle, to) < BarrelBackfillMin) return;
        Color? tint = IsPlasmaShot(q.Weapon, q.Thrown) ? RaceTint(ProjectileWargear(q), "plasma") ?? new Color(0.5f, 0.75f, 1f) : null;
        StreakAdd(ToGodot(muzzle), ToGodot(to), ToGodot(to), 400f, tint);   // Godot world space, like the bullets' streaks
    }

    const float BarrelBackfillMin = 0.6f;

    /// <summary>The drawn position of a matched projectile at snapshot position <paramref name="at"/> (game space).</summary>
    Num.Vector3 BarrelPlaced(int id, Num.Vector3 at)
    {
        if (!_barrelProjectiles.TryGetValue(id, out var o)) return at;
        float along = (at - o.Origin).Length(), reach = o.Offset.Length();
        return at + o.Offset * Math.Clamp(1f - MathF.Max(0f, along - reach) / BulletPaths.ConvergeDistance, 0f, 1f);
    }

    /// <summary>
    /// Another player's projectile shot, seen as its projectile first appears next to that player (the server sends shot records for rays only): plays the weapon's muzzle effects at its
    /// muzzle as an own shot does, and notes the origin and the muzzle for <see cref="BarrelDrawn"/>. Before, other players' plasma cannon, launcher and charged plasma shots showed no muzzle
    /// flash, smoke or shells at all. About 30 to 90 ms after the shot (the first snapshot that holds the projectile, shown after the interpolation delay).
    /// </summary>
    void FxProjectileLaunch(PlayerState shooter, SmNet.ProjectileView q)
    {
        if (OldBarrel || _fx is null || _config is null || q.Weapon < 0 || q.Weapon >= _config.Weapons.Count || !FxReady() || !FxActive) return;
        try
        {
            var spec = _config.Weapons[q.Weapon];
            if (FxWeapon(spec.Name) is not { } w || FxPlace(shooter.PlayerId, w.MuzzleMarker) is not { } muzzle) return;
            var at = ToGodot(q.Position);
            var dir = at - muzzle.Position;
            dir = dir.LengthSquared() > 1e-4f ? dir.Normalized() : muzzle.Axes.Y;
            BarrelNoteShot(shooter.PlayerId, q.Weapon, RemoteShotOrigin(shooter), muzzle.Position);
            FxShot(shooter.PlayerId, spec, w, q.Weapon, q.Stage, at, ShotHit.None, -1, -dir, dir, Combat.EyePosition(shooter, _config), own: false);
            _barrelLaunchFx++;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (_fxNoted.Add("fault:launch:" + ex.GetType().Name)) { GD.PrintErr($"projectile launch effects failed: {ex}"); _notes.Add($"projectile launch effects: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    int _barrelLaunchFx;

    void BarrelForget(int id) => _barrelProjectiles.Remove(id);

    string BarrelStats() => $"barrel origin: {_barrelMatched} projectiles drawn from the muzzle, {_barrelUnmatched} unmatched, {_barrelLaunchFx} others' launches given their muzzle effects";
}
