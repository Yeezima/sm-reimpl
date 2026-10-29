using System.Collections.Generic;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The picture of a piercing shot (M39): the own shot's tracer and impact (<c>TraceOwn</c>) and the crosshair's enemy colour
/// (<c>CenterRayTarget</c>) follow the simulation's rules (<see cref="CollisionWorld.TraceShot"/>, <see cref="Pierces"/>,
/// <c>World.ResolveShot</c>) against the players as last drawn: the ray passes players while the weapon's points cover them,
/// passes the props it can pierce, and past a prop reaches only as far as the exit cone. The server's answer decides the
/// damage; this is only what the shooter sees.
/// </summary>
public partial class NetworkView
{
    /// <param name="EndDistance">Where the picture's ray ends: the player it stops in, the farthest player it damages, the surface it stops at, or the range.</param>
    /// <param name="EndVictim">The player at the end, if any.</param>
    /// <param name="FirstVictim">The first player the shot would damage (the crosshair's colour), if any.</param>
    readonly record struct ShotPicture(float EndDistance, PlayerState? EndVictim, HitZone EndZone, RayHit? Surface, PlayerState? FirstVictim);

    ShotPicture PictureShot(Num.Vector3 start, Num.Vector3 dir, float range, PierceSpec? pierce, SimConfig c)
    {
        var world = _client?.Collision;
        var trace = world?.TraceShot(start, dir, range, pierce);
        float blocked = trace?.First?.Distance ?? range;
        var along = new List<(float Distance, PlayerState Player, HitZone Zone)>();
        foreach (var other in _lastOthers)
        {
            if (!other.Alive) continue;
            var (zone, distance) = Combat.HitShapes(start, dir, other.Position, HitBodyAsDrawn(other), blocked, other.Yaw);   // fix/hitboxes: as drawn
            if (zone != HitZone.None && distance < blocked) along.Add((distance, other, zone));
        }
        along.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Player.PlayerId.CompareTo(b.Player.PlayerId));
        PlayerState? firstVictim = along.Count > 0 ? along[0].Player : null;
        float points = pierce?.Points ?? 0f;
        (float Distance, PlayerState Player, HitZone Zone)? last = null;
        foreach (var a in along)
        {
            last = a;
            float cost = Pierces.PlayerCost(pierce, a.Zone);
            if (pierce is null || !pierce.CanPierce(points, cost)) return new ShotPicture(a.Distance, a.Player, a.Zone, null, firstVictim);
            points -= cost;
        }
        RayHit? stop = trace?.First;
        if (world is not null && trace is { Pierced.Count: > 0 } t && pierce is not null)
        {
            if (points < pierce.Points) t = world.TraceShot(start, dir, range, pierce with { Points = points });
            stop = t.Stop;
            if (pierce.Exit is { } cone)
                foreach (var p in t.Pierced)
                {
                    if (!p.IntoOpen) continue;
                    float reach = System.MathF.Min(cone.Length, range - p.Exit);
                    var exit = start + dir * p.Exit;
                    (float Distance, PlayerState Player, HitZone Zone)? behind = null;
                    foreach (var other in _lastOthers)
                    {
                        if (!other.Alive) continue;
                        var (zone, distance) = Combat.HitShapes(exit, dir, other.Position, HitBodyAsDrawn(other), reach, other.Yaw);   // fix/hitboxes: as drawn
                        if (zone != HitZone.None && (behind is null || distance < behind.Value.Distance)) behind = (distance, other, zone);
                    }
                    if (behind is { } b)
                        return new ShotPicture(p.Exit + b.Distance, b.Player, b.Zone, null, firstVictim ?? b.Player);
                }
        }
        if (last is { } l) return new ShotPicture(l.Distance, l.Player, l.Zone, null, firstVictim);
        return stop is { } s ? new ShotPicture(s.Distance, null, HitZone.None, s, null) : new ShotPicture(range, null, HitZone.None, null, null);
    }
}
