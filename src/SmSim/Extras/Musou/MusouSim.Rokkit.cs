using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>A rocket in flight (expansion S2): its serial (the client's handle), the Rokkit Boy who fired it, where it is, its velocity and the tick it was fired on.</summary>
public readonly record struct MusouRocket(ushort Serial, ushort Shooter, Vector3 Position, Vector3 Velocity, uint Fired);

/// <summary>A rocket that burst this tick, for the World to apply as an Ork blast (<see cref="MusouRokkitRules.Blast"/>).</summary>
public readonly record struct MusouRocketBlast(ushort Serial, ushort Shooter, Vector3 Point);

/// <summary>
/// The Rokkit Boy's rocket (expansion S2; <see cref="MusouRokkitRules"/>): a slow projectile, unlike the Weirdboy's bolt (which bursts where it was aimed). Each tick it moves along its path and
/// bursts at the first thing it meets: a solid of the collision world (the weapons mesh), an Ork (any but the one who fired it: <see cref="Raycast(Vector3, Vector3, float, out float, out bool, ReadOnlySpan{ushort})"/>,
/// the shots' bodies), or a living player's chest; else after <see cref="MusouRokkitRules.RocketMaxSeconds"/> where it is. The World applies each burst (<see cref="TakeRocketBlasts"/>).
/// Its own hit roll is gone: a rocket hits what it hits.
/// </summary>
public sealed partial class MusouSim
{
    readonly List<MusouRocket> _rockets = new();
    readonly List<MusouRocketBlast> _rocketBlasts = new();
    ushort _rocketSerial;

    /// <summary>The rockets in flight now (in the order they were fired).</summary>
    public IReadOnlyList<MusouRocket> Rockets => _rockets;

    /// <summary>The rockets that burst since the last call, moved into <paramref name="into"/>.</summary>
    public void TakeRocketBlasts(List<MusouRocketBlast> into) { into.AddRange(_rocketBlasts); _rocketBlasts.Clear(); }

    /// <summary>
    /// Fires a rocket from Ork <paramref name="shooter"/>'s launcher (or <paramref name="from"/> when given) at <paramref name="aim"/>; its serial (0: none, an unknown shooter or no direction).
    /// Used by the Rokkit Boy's burst (<see cref="Fire"/>) and by tests.
    /// </summary>
    public ushort LaunchRocket(ushort shooter, Vector3 aim, Vector3? from = null)
    {
        int i = shooter - 1;
        if (i < 0 || i >= Capacity || !_used[i]) return 0;
        var flat = new Vector2(aim.X - _position[i].X, aim.Y - _position[i].Y);
        var forward = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : Forward(_yaw[i]);
        var muzzle = from ?? _position[i] + new Vector3(forward * MusouRokkitRules.MuzzleForward, MusouRokkitRules.MuzzleHeight);
        var way = aim - muzzle;
        float length = way.Length();
        if (!(length > 1e-3f) || !float.IsFinite(length)) return 0;
        if (++_rocketSerial == 0) _rocketSerial = 1;
        _rockets.Add(new MusouRocket(_rocketSerial, shooter, muzzle, way / length * MusouRokkitRules.RocketSpeed, Tick));
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, shooter, _rocketSerial, MusouRokkitRules.FireCue, aim));
        return _rocketSerial;
    }

    /// <summary>A Rokkit Boy's round (from <see cref="Fire"/>): a rocket at the target's chest (none outside play).</summary>
    void FireRocket(int i, MusouTarget target, bool fight)
    {
        if (!fight) return;
        LaunchRocket((ushort)(i + 1), target.Position + new Vector3(0f, 0f, MusouRokkitRules.ChestHeight));
    }

    /// <summary>Every rocket's move this tick (from <see cref="Step"/>, before the enemies move): it bursts at the first solid, Ork or player chest on its way, or at the end of its life.</summary>
    /// <summary>Expansion S4: where along a rocket's step it enters a shield dome from outside (infinite: none); set by the World in a Musou Mode battle.</summary>
    public Func<Vector3, Vector3, float, float>? RocketDome { get; set; }

    void StepRockets(ReadOnlySpan<MusouTarget> targets, CollisionWorld? collision)
    {
        Span<ushort> past = stackalloc ushort[1];
        int max = Ticks(MusouRokkitRules.RocketMaxSeconds);
        for (int r = 0; r < _rockets.Count; r++)
        {
            var rocket = _rockets[r];
            var step = rocket.Velocity * Dt;
            float length = step.Length();
            var dir = length > 1e-6f ? step / length : Vector3.UnitZ;
            float hit = float.PositiveInfinity;
            if (collision?.Weapons.Raycast(rocket.Position, dir, length) is { } wall) hit = wall.Distance;
            if (RocketDome?.Invoke(rocket.Position, dir, length) is float dome && dome < hit) hit = dome;   // expansion S4: a shield dome stops it from outside
            past[0] = rocket.Shooter;
            if (Raycast(rocket.Position, dir, MathF.Min(length, hit), out float ork, out _, past) != 0) hit = MathF.Min(hit, ork);
            foreach (var t in targets)
            {
                if (!t.Alive || t.Goal || t.Id < 0) continue;   // Guard squads: the ground under them stops it
                var chest = t.Position + new Vector3(0f, 0f, MusouRokkitRules.ChestHeight);
                if (RaySphere(rocket.Position, dir, chest, MusouRokkitRules.BodyRadius) is { } at && at <= length) hit = MathF.Min(hit, at);
            }
            if (float.IsFinite(hit))
            {
                Burst(r--, rocket, rocket.Position + dir * MathF.Max(0f, hit - 0.05f));
                continue;
            }
            var moved = rocket with { Position = rocket.Position + step };
            if (Tick - rocket.Fired >= (uint)max) { Burst(r--, moved, moved.Position); continue; }
            _rockets[r] = moved;
        }
    }

    void Burst(int index, MusouRocket rocket, Vector3 point)
    {
        _rockets.RemoveAt(index);
        _rocketBlasts.Add(new MusouRocketBlast(rocket.Serial, rocket.Shooter, point));
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, rocket.Shooter, rocket.Serial, MusouRokkitRules.BlastCue, point));
    }
}
