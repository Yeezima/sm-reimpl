using System.Numerics;

namespace SmSim;

/// <summary>How a projectile moves: in flight, resting where it struck the level, or stuck to a player.</summary>
public enum ProjectileMotion : byte { Flying = 0, Stuck = 1, Attached = 2 }

/// <summary>
/// A projectile in the world. Server-authoritative: clients draw projectiles from snapshots and do not predict them.
/// <paramref name="Weapon"/> indexes <see cref="SimConfig.Weapons"/>; <paramref name="Stage"/> names the shot as
/// <see cref="Shots"/> does (0 basic, k charge stage k − 1). Units: metres (assumed), seconds.
/// </summary>
public readonly record struct ProjectileState(int Id, int Owner, int Weapon, int Stage, Vector3 Position, Vector3 Velocity)
{
    public uint LaunchTick { get; init; }
    /// <summary>Seconds since launch (one tick per world step, the launch tick included).</summary>
    public float Age { get; init; }
    /// <summary>Distance flown (drives the gravity factor).</summary>
    public float Travelled { get; init; }
    public int Bounces { get; init; }
    public ProjectileMotion Motion { get; init; }
    /// <summary>The player it is stuck to (<see cref="ProjectileMotion.Attached"/>), else -1.</summary>
    public int AttachedTo { get; init; } = -1;
    /// <summary>Offset from the feet of <see cref="AttachedTo"/>.</summary>
    public Vector3 AttachOffset { get; init; }
    /// <summary>The player it last struck without stopping for good (it falls from there); never struck again, else -1.</summary>
    public int Struck { get; init; } = -1;
    /// <summary>Remotely detonated (counts in its owner's <see cref="PlayerState.ProjectilesOut"/> until it is set to detonate).</summary>
    public bool Remote { get; init; }
    /// <summary>Set to detonate remotely (after <see cref="DetonateIn"/>).</summary>
    public bool Scheduled { get; init; }
    /// <summary>Seconds until a scheduled detonation (counted down once per tick; it goes off in the first tick it is at most 0).</summary>
    public float DetonateIn { get; init; }
    /// <summary>A thrown item: <see cref="ProjectileState.Weapon"/> indexes <see cref="SimConfig.Thrown"/> (protocol 8).</summary>
    public bool Thrown { get; init; }
    /// <summary>A rigid body that has touched the ground: its ground fuse (<see cref="ProjectileSpec.GroundLifetime"/>) runs.</summary>
    public bool Landed { get; init; }
    /// <summary>Seconds counted on the ground fuse: 0 when it lands; the landing tick's own check then stores dt, and each later tick adds dt.</summary>
    public float SinceLanding { get; init; }
    /// <summary>
    /// The projectile as launched (M43: the owner's perk variant, e.g. Master-Crafted Wargear's frag or Vengeance Projectiles'
    /// limit); null: the config's by index (<see cref="Shots.ProjectileOf(SimConfig, int, int, bool)"/>). Server only.
    /// </summary>
    public ProjectileSpec? Spec { get; init; }
    /// <summary>The weapon that launched it as its owner carried it (M43: perk variant; direct damage, detonation delays); null: the config's. Server only.</summary>
    public WeaponSpec? Source { get; init; }
}

/// <summary>A thrown item that left a hand: <paramref name="Item"/> indexes <see cref="SimConfig.Thrown"/>.</summary>
public readonly record struct ThrowEvent(uint Tick, int Thrower, int Item, int Projectile);

/// <summary>
/// A lingering blast (<see cref="SplashSpec.Lingers"/>, the melta bomb's first): applied at the detonation and every
/// Period seconds while younger than Lifetime (**interpretation**). <paramref name="Projectile"/> is the detonated projectile's id.
/// </summary>
public readonly record struct LingeringArea(int Projectile, int Owner, int Weapon, bool Thrown, Vector3 Position, SplashSpec Splash)
{
    /// <summary>Seconds since the detonation.</summary>
    public float Age { get; init; }
    /// <summary>Applications so far (the detonation's included).</summary>
    public int Applications { get; init; }

}

/// <summary>A projectile's detonation: where, how large, and what the detonation dealt (its DetonateDamage and blast; a direct hit on impact is a separate <see cref="HitEvent"/>).</summary>
public readonly record struct DetonationEvent(uint Tick, int Projectile, int Owner, int Weapon, int Stage, Vector3 Position, float Radius,
    int Victims, float ShieldDamage, float HealthDamage, int Kills)
{
    /// <summary>A thrown item's detonation: <see cref="Weapon"/> indexes <see cref="SimConfig.Thrown"/>.</summary>
    public bool Thrown { get; init; }
}

/// <summary>What a hit-scan or cone shot ended on (M25a; for effects: tracer end and impact kind).</summary>
public enum ShotHit : byte
{
    /// <summary>Nothing within range: the shot ends at its range (a cone: at its length).</summary>
    None = 0,
    /// <summary>Level geometry (the weapon collision layer); <see cref="ShotEvent.Surface"/> names its material.</summary>
    World = 1,
    /// <summary>A player's body (any zone but the head).</summary>
    Body = 2,
    /// <summary>A player's head.</summary>
    Head = 3,
}

/// <summary>
/// A shot that left a barrel: <paramref name="Stage"/> as <see cref="Shots"/> names it; the projectile it launched, or 0 for a ray.
/// M25a: a ray or cone shot (<paramref name="Projectile"/> 0) also carries where it went, for effects (tracers, impacts): its
/// <paramref name="Origin"/> (the shot origin the server used), <paramref name="End"/> (the hit point; with nothing hit, the point at
/// the weapon's range along the shot, or at the cone's length), what it hit (<paramref name="Hit"/>), the hit player
/// (<paramref name="Target"/>, 0 none) and, for a world hit, the collision triangle's surface material id
/// (<paramref name="Surface"/>; <see cref="StaticMesh.MaterialOf"/>; -1 unknown). Projectile launches leave these at their defaults.
/// <paramref name="Armoured"/> (protocol 22, hit feedback): for a player hit (Body, Head), the target's armour shield was above 0
/// before the hit (the impact strikes armour; else flesh). The data keys the impact sound's state on the shield the same way
/// (player blueprint: <c>armourshield_broken</c> sets <c>set_state_impact_broken_armor</c>, <c>armourshield_restored</c>
/// <c>set_state_impact_armor</c>); the hit material stays <c>broken_ceramite</c> either way. False for every other hit.
/// </summary>
public readonly record struct ShotEvent(uint Tick, int Shooter, int Weapon, int Stage, int Projectile, Vector3 Origin = default, Vector3 End = default,
    ShotHit Hit = ShotHit.None, int Surface = -1, int Target = 0, bool Armoured = false);

/// <summary>
/// Projectiles (rules: the files' values where they exist, **interpretation** or ours for the rest; spec/behavior.md).
/// <para>
/// Launch: from the eye point hit-scan shots use (no lag compensation), along the aim raised by the projectile's
/// ExtraAimAngle (degrees), at its Speed; the shooter's velocity is not added. Flight, each tick: the velocity loses
/// <see cref="SimConfig.Gravity"/> × the gravity factor for the distance flown (step lookup,
/// <see cref="Combat.GravityFactor"/>) × dt, then the segment it covers is tested against the level's weapon collision (or
/// the flat ground at z = 0 without a level) and the hit volumes of living players at their current positions (the owner
/// only after <see cref="SimConfig.ProjectileOwnerGrace"/>); projectiles are points (CollisionRadius unused).
/// </para>
/// <para>
/// Striking a player: the shot's direct damage (if any); then DetonateOnCollision detonates, Sticky sticks to the player
/// (following it until it dies or leaves), anything else stops and falls. Striking the level: a bounce while fewer than
/// MaxBounces have happened (velocity reflected and scaled by <see cref="SimConfig.ProjectileRestitution"/>; the rest of the
/// tick's motion is dropped), else DetonateOnCollision detonates, else it rests there (sticky or not). Lifetime &gt; 0 ends
/// it after that long (detonating when DetonateWhenLifetimeOver); 0 is no limit. Falling below the level's kill depth
/// removes it.
/// </para>
/// <para>
/// Detonation: the projectile's DetonateDamage on the player it struck or is stuck to, then its splash on every living
/// player (owner included) whose body centre is within the radius and, with LineOfSight, visible from the blast point
/// (level weapon collision), scaled by <see cref="Combat.SplashIntensity"/>, on the body multipliers.
/// </para>
/// <para>
/// Remote detonation (grenade launcher): a request sets the owner's waiting grenades to go off after the weapon's
/// <see cref="WeaponSpec.DetonationDelays"/>, oldest first. Launching more than MaxActive of them detonates the oldest at
/// once; more than <see cref="SimConfig.MaxProjectiles"/> in the world detonates the oldest projectile.
/// </para>
/// </summary>
public sealed partial class World
{
    /// <summary>Ticks a detonation stays in <see cref="RecentDetonations"/> (so snapshots, sent every 1 or 2 ticks, carry it).</summary>
    public const int DetonationMemoryTicks = 3;
    /// <summary>How far a resting or bouncing projectile is kept off the surface it struck, and a blast point off it (m).</summary>
    const float SurfaceOffset = 0.02f, BlastOffset = 0.05f;
    /// <summary>A projectile farther than this from the origin on any axis is removed (ours; the wire carries ±131 km).</summary>
    public const float WorldBound = 100_000f;
    /// <summary>
    /// Upper bound on <see cref="SimConfig.MaxProjectiles"/> (a larger value acts as this): with at most this many live
    /// projectiles plus recent detonations, a free 16-bit id always exists for <see cref="Launch"/>.
    /// </summary>
    public const int MaxProjectilesLimit = 4096;

    readonly List<ProjectileState> _projectiles = new();
    readonly List<DetonationEvent> _detonations = new();
    readonly List<DetonationEvent> _recentDetonations = new();
    readonly List<ThrowEvent> _throws = new();
    readonly List<LingeringArea> _areas = new();
    int _nextProjectileId = 1;

    /// <summary>Thrown items released during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<ThrowEvent> LastThrows => _throws;
    /// <summary>Lingering blasts in the world (server only; not on the wire).</summary>
    public IReadOnlyList<LingeringArea> Areas => _areas;

    /// <summary>Projectiles in the world, oldest first.</summary>
    public IReadOnlyList<ProjectileState> Projectiles => _projectiles;
    /// <summary>Detonations during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<DetonationEvent> LastDetonations => _detonations;
    /// <summary>Detonations during the last <see cref="DetonationMemoryTicks"/> ticks, oldest first.</summary>
    public IReadOnlyList<DetonationEvent> RecentDetonations => _recentDetonations;

    void BeginProjectileTick()
    {
        _detonations.Clear();
        _throws.Clear();
        _recentDetonations.RemoveAll(d => Tick - d.Tick >= DetonationMemoryTicks);
    }

    int IndexOf(int id)
    {
        for (int i = 0; i < _projectiles.Count; i++) if (_projectiles[i].Id == id) return i;
        return -1;
    }

    ProjectileSpec SpecOf(in ProjectileState p) => p.Spec ?? Shots.ProjectileOf(Config, p.Weapon, p.Stage, p.Thrown)!;

    /// <summary>The weapon that launched a (not thrown) projectile: its owner's variant (M43), else the config's.</summary>
    WeaponSpec WeaponOf(in ProjectileState p) => p.Source ?? Config.Weapons[p.Weapon];

    /// <summary>
    /// Launches from the input's shot origin (<see cref="Combat.ShotOrigin"/>) along its shot direction (<see cref="Combat.ShotAim"/>;
    /// protocol 12), raised by the projectile's ExtraAimAngle. <paramref name="source"/>: the weapon as the owner carries it (M43).
    /// </summary>
    void Launch(int owner, PlayerState state, PlayerInput input, int weapon, int stage, ProjectileSpec spec, bool thrown = false, WeaponSpec? source = null)
    {
        while (_projectiles.Count > 0 && _projectiles.Count >= Math.Clamp(Config.MaxProjectiles, 1, MaxProjectilesLimit)) Detonate(_projectiles[0].Id, _projectiles[0].Position, -1, HitZone.None);
        var origin = Combat.ShotOrigin(state, input, Config, Collision);
        var view = input with { Yaw = state.Yaw, Pitch = state.Pitch };
        var (shotYaw, shotPitch) = thrown ? Combat.ShotAim(view, Config) : Combat.ShotAim(view, Config, state);   // weapons/spread: a fired projectile scatters, a throw does not
        var direction = Combat.AimDirection(shotYaw, shotPitch + spec.ExtraAimAngle * (MathF.PI / 180f));
        // Ids travel modulo 65536: skip one a live projectile or a detonation still on the wire shares.
        while (_projectiles.Any(q => (q.Id & 0xFFFF) == (_nextProjectileId & 0xFFFF)) || _recentDetonations.Any(d => (d.Projectile & 0xFFFF) == (_nextProjectileId & 0xFFFF)))
            _nextProjectileId++;
        var p = new ProjectileState(_nextProjectileId++, owner, weapon, stage, origin, direction * spec.Speed)
        {
            LaunchTick = Tick, Thrown = thrown, Remote = !thrown && stage == 0 && Shots.RemoteDetonated(source ?? Config.Weapons[weapon]),
            Spec = spec, Source = thrown ? null : source,
        };
        _projectiles.Add(p);
        if (thrown) _throws.Add(new ThrowEvent(Tick, owner, weapon, p.Id));
        else _shots.Add(new ShotEvent(Tick, owner, weapon, stage, p.Id));
        if (!p.Remote) return;
        // More waiting grenades than the launcher allows: the oldest goes off now (interpretation).
        while (true)
        {
            var waiting = _projectiles.Where(q => q.Owner == owner && q.Weapon == weapon && q.Remote && !q.Scheduled).ToList();
            if (waiting.Count <= spec.MaxActive) break;
            Detonate(waiting[0].Id, waiting[0].Position, -1, HitZone.None);
        }
    }

    /// <summary>The owner's waiting remote projectiles go off after the weapon's delays, oldest first.</summary>
    void DetonateRemotely(int owner)
    {
        int k = 0;
        for (int i = 0; i < _projectiles.Count; i++)
        {
            var p = _projectiles[i];
            if (p.Owner != owner || !p.Remote || p.Scheduled) continue;
            var delays = WeaponOf(p).DetonationDelays;
            float delay = delays.Count == 0 ? 0f : MathF.Max(0f, delays[Math.Min(k, delays.Count - 1)]);
            _projectiles[i] = p with { Scheduled = true, DetonateIn = delay };
            k++;
        }
    }

    void StepProjectiles()
    {
        float dt = Config.TickSeconds;
        foreach (int id in _projectiles.Select(p => p.Id).ToList())
        {
            int i = IndexOf(id);
            if (i < 0) continue;
            var p = _projectiles[i];
            var spec = SpecOf(p);
            float ageBefore = p.Age;
            p = p with { Age = p.Age + dt };
            if (p.Motion == ProjectileMotion.Attached)
                p = _players.TryGetValue(p.AttachedTo, out var host) && host.Alive
                    ? p with { Position = host.Position + p.AttachOffset }
                    : p with { Motion = ProjectileMotion.Stuck, AttachedTo = -1 };   // its host died or left: it stays there
            _projectiles[i] = p;
            if (p.Motion == ProjectileMotion.Flying && !Fly(i, spec, ageBefore)) continue;   // detonated on impact
            i = IndexOf(id);
            p = _projectiles[i];
            if (spec.Lifetime > 0f && p.Age >= spec.Lifetime - 1e-5f)
            {
                if (spec.DetonateWhenLifetimeOver) Detonate(id, p.Position, -1, HitZone.None);
                else _projectiles.RemoveAt(i);
                continue;
            }
            // The ground fuse of a landed rigid body (the frag grenade's ground_lifetime).
            if (p.Landed && spec.GroundLifetime > 0f)
            {
                if (p.SinceLanding + 1e-5f >= spec.GroundLifetime) { Detonate(id, p.Position, -1, HitZone.None); continue; }
                p = p with { SinceLanding = p.SinceLanding + dt };
            }
            if (p.Scheduled)
            {
                if (p.DetonateIn <= 1e-6f) { Detonate(id, p.Position, -1, HitZone.None); continue; }
                p = p with { DetonateIn = p.DetonateIn - dt };
            }
            if ((Collision is not null && p.Position.Z < Collision.Character.Min.Z - Config.KillDepth)
                || !(MathF.Abs(p.Position.X) <= WorldBound && MathF.Abs(p.Position.Y) <= WorldBound && MathF.Abs(p.Position.Z) <= WorldBound))
            { _projectiles.RemoveAt(i); continue; }
            _projectiles[i] = p;
        }
    }

    /// <summary>One tick of flight for the projectile at <paramref name="i"/>; false when it detonated.</summary>
    bool Fly(int i, ProjectileSpec spec, float ageBefore)
    {
        float dt = Config.TickSeconds;
        var p = _projectiles[i];
        // A rigid body falls with full gravity once it has touched anything (its gravity table is for the flight from the hand; ours).
        float gravityFactor = spec.Rigid is not null && (p.Bounces > 0 || p.Landed || p.Struck >= 0) ? 1f : Combat.GravityFactor(spec.Gravity, p.Travelled);
        var velocity = p.Velocity - new Vector3(0, 0, Config.Gravity * gravityFactor * dt);
        var segment = velocity * dt;
        float length = segment.Length();
        if (!(length > 1e-7f)) { _projectiles[i] = p with { Velocity = velocity }; return true; }
        var direction = segment / length;
        var wall = RaycastLevel(p.Position, direction, length);
        float best = wall?.Distance ?? float.MaxValue;
        int target = -1; var zone = HitZone.None; var targetDisplacement = Vector3.Zero;
        _history.TryGetValue(Tick - 1, out var before);
        foreach (var (id, player) in _players)
        {
            if (!player.Alive || id == p.Struck || (id == p.Owner && ageBefore < (spec.Rigid?.DelayBeforeCollision ?? Config.ProjectileOwnerGrace) - 1e-5f)) continue;
            // In the target's frame: players moved before projectiles this tick, so the projectile's motion relative to
            // the target is its segment minus the target's displacement, from the start shifted by that displacement.
            var displacement = before is not null && before.TryGetValue(id, out var then) && then.Alive ? player.Position - then.Position : Vector3.Zero;
            var start = p.Position + displacement;
            // The target stepped onto it: inside its actual hit shapes (not for the owner, whose shapes every projectile starts in).
            var body = BodyOf(id, player, _flightNow);   // Ork O4: the target's own volumes; fix/hitboxes: as drawn in a jump
            if (id != p.Owner && Combat.InsideHitShapes(start, player.Position, body, player.Yaw) is var inside and not HitZone.None)
            {
                if (0f < best) { best = 0f; target = id; zone = inside; targetDisplacement = displacement; }
                continue;
            }
            var relative = segment - displacement;
            float relativeLength = relative.Length();
            if (!(relativeLength > 1e-7f)) continue;
            // The owner keeps the hit-scan exclusion (projectiles start inside it); others are tested against the shapes alone,
            // so a start between the cylinder and the shapes (beside the head, over a shoulder) is no blind spot.
            var (z, d) = id == p.Owner
                ? Combat.HitTest(start, relative / relativeLength, player.Position, body, relativeLength, player.Yaw)
                : Combat.HitShapes(start, relative / relativeLength, player.Position, body, relativeLength, player.Yaw);
            if (z == HitZone.None) continue;
            float along = d / relativeLength * length;   // the same fraction of the tick along the projectile's own path
            if (along < best) { best = along; target = id; zone = z; targetDisplacement = displacement; }
        }
        p = p with { Velocity = velocity };
        if (target >= 0)
        {
            var point = p.Position + direction * best;
            p = p with { Position = point, Travelled = p.Travelled + best };
            _projectiles[i] = p;
            if (!p.Thrown && Shots.DirectDamageOf(WeaponOf(p), p.Stage) is { } direct)
                ApplyHit(p.Owner, target, direct, zone, p.Travelled, 1f, p.Id, false, weapon: p.Weapon, direction: direction);
            if (spec.DetonateOnCollision) { Detonate(p.Id, point, target, zone); return false; }
            if (spec.Rigid is { } body && !spec.Sticky && _players.TryGetValue(target, out var struck))
            {
                // A rigid body glances off the player (entity reductions) and never strikes that player again; not a bounce.
                var away = point - (struck.Position - targetDisplacement * (1f - best / length));
                var normal = new Vector3(away.X, away.Y, 0f);
                if (normal.LengthSquared() <= 1e-8f) normal = new Vector3(-direction.X, -direction.Y, 0f);
                normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                var reflected = Keep(velocity - 2f * Vector3.Dot(velocity, normal) * normal, body.EntityHorizontalReduction, body.EntityVerticalReduction);
                _projectiles[i] = p with { Position = point + normal * SurfaceOffset, Velocity = reflected, Struck = target };
                return true;
            }
            _projectiles[i] = spec.Sticky && _players.TryGetValue(target, out var host) && host.Alive
                // The offset from where the host was at the moment of impact (the same fraction of its move this tick).
                ? p with { Motion = ProjectileMotion.Attached, AttachedTo = target, Velocity = Vector3.Zero,
                    AttachOffset = point - (host.Position - targetDisplacement * (1f - best / length)) }
                : p with { Velocity = Vector3.Zero, Struck = target };   // stops and falls, never striking that player again
            return true;
        }
        if (wall is { } hit)
        {
            var travelled = p.Travelled + hit.Distance;
            if (spec.Rigid is { } rigid) return RigidContact(i, p with { Velocity = velocity, Travelled = travelled }, spec, rigid, hit);
            if (p.Bounces < spec.MaxBounces)
            {
                var reflected = (velocity - 2f * Vector3.Dot(velocity, hit.Normal) * hit.Normal) * Config.ProjectileRestitution;
                _projectiles[i] = p with { Position = hit.Point + hit.Normal * SurfaceOffset, Velocity = reflected, Travelled = travelled, Bounces = p.Bounces + 1 };
                return true;
            }
            if (spec.DetonateOnCollision) { Detonate(p.Id, hit.Point + hit.Normal * BlastOffset, -1, HitZone.None); return false; }
            _projectiles[i] = p with { Position = hit.Point + hit.Normal * SurfaceOffset, Velocity = Vector3.Zero, Travelled = travelled, Motion = ProjectileMotion.Stuck };
            return true;
        }
        _projectiles[i] = p with { Position = p.Position + segment, Travelled = p.Travelled + length };
        return true;
    }

    /// <summary>
    /// A rigid body striking the level (rules **interpretation**; spec/behavior.md): ground is a surface whose normal is within
    /// GroundAngle of up; touching ground starts the ground fuse. While fewer than MaxBounces contacts have happened, the
    /// velocity is reflected about the normal and its horizontal and vertical parts scaled by the "reductions" (the fraction
    /// kept; ground: the ground values; wall: the wall maps at the angle between the incoming direction and the wall's surface,
    /// 0° grazing to 90° head-on, linear between points). A contact after
    /// that, with StopAtMaxBounce, stops the body: on ground it rests, at a wall it loses its velocity and falls. Without
    /// StopAtMaxBounce it detonates (DetonateOnCollision) or rests. False when it detonated.
    /// </summary>
    bool RigidContact(int i, ProjectileState p, ProjectileSpec spec, RigidSpec rigid, RayHit hit)
    {
        var velocity = p.Velocity;
        bool ground = hit.Normal.Z >= MathF.Cos(rigid.GroundAngle * (MathF.PI / 180f)) - 1e-6f;
        if (ground && !p.Landed) p = p with { Landed = true, SinceLanding = 0f };
        var at = hit.Point + hit.Normal * SurfaceOffset;
        if (p.Bounces < spec.MaxBounces)
        {
            // fix/grenades (the owner's change; RigidSpec.GroundMaterial): on ground the body's own restitution and friction first, when on.
            var reflected = ground ? rigid.Bounce(velocity, hit.Normal) : velocity - 2f * Vector3.Dot(velocity, hit.Normal) * hit.Normal;
            float speed = velocity.Length();
            // Angle from the surface: 90° head-on, 0° grazing.
            float angle = speed > 1e-6f ? MathF.Asin(Math.Clamp(MathF.Abs(Vector3.Dot(velocity / speed, hit.Normal)), 0f, 1f)) * (180f / MathF.PI) : 90f;
            reflected = ground
                ? Keep(reflected, rigid.GroundHorizontalReduction, rigid.GroundVerticalReduction)
                : Keep(reflected, Combat.PointValue(rigid.WallHorizontalReduction, angle), Combat.PointValue(rigid.WallVerticalReduction, angle));
            _projectiles[i] = p with { Position = at, Velocity = reflected, Bounces = p.Bounces + 1 };
            return true;
        }
        if (!spec.StopAtMaxBounce && spec.DetonateOnCollision) { Detonate(p.Id, hit.Point + hit.Normal * BlastOffset, -1, HitZone.None); return false; }
        // A stop after the last bounce starts the ground fuse even at a wall (a crevice steeper than ground_angle would
        // otherwise hold it until its lifetime; interpretation).
        if (spec.StopAtMaxBounce && !p.Landed) p = p with { Landed = true, SinceLanding = 0f };

        _projectiles[i] = ground || !spec.StopAtMaxBounce
            ? p with { Position = at, Velocity = Vector3.Zero, Motion = ProjectileMotion.Stuck }
            : p with { Position = at, Velocity = Vector3.Zero };   // stopped at a wall: it falls from there
        return true;
    }

    /// <summary>A velocity with its horizontal part scaled by <paramref name="horizontal"/> and its vertical part by <paramref name="vertical"/> (each clamped to [0, 1]).</summary>
    static Vector3 Keep(Vector3 v, float horizontal, float vertical)
    {
        float h = Math.Clamp(horizontal, 0f, 1f), z = Math.Clamp(vertical, 0f, 1f);
        return new Vector3(v.X * h, v.Y * h, v.Z * z);
    }

    /// <summary>The level's weapon collision along a segment, or the flat ground (z = 0, normal up) without a level.</summary>
    RayHit? RaycastLevel(Vector3 origin, Vector3 direction, float length)
    {
        if (Collision is not null) return Collision.Weapons.Raycast(origin, direction, length);
        if (direction.Z >= 0f) return origin.Z < 0f ? new RayHit(0f, origin with { Z = 0f }, Vector3.UnitZ, -1) : null;
        float t = MathF.Max(0f, origin.Z / -direction.Z);
        return t <= length ? new RayHit(t, origin + direction * t, Vector3.UnitZ, -1) : null;
    }

    /// <summary>Whether the level blocks the line between two points (never without a level).</summary>
    bool Blocked(Vector3 from, Vector3 to)
    {
        if (Collision is null) return false;
        var line = to - from;
        float length = line.Length();
        return length > 1e-4f && Collision.Weapons.Raycast(from, line / length, length) is { } hit && hit.Distance < length - 1e-3f;
    }

    void Detonate(int id, Vector3 point, int struck, HitZone zone)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        var p = _projectiles[i];
        _projectiles.RemoveAt(i);
        // Stuck to a player: the blast is where the host is now (it may have moved since the projectile was last updated).
        if (p.Motion == ProjectileMotion.Attached && _players.TryGetValue(p.AttachedTo, out var host) && host.Alive) point = host.Position + p.AttachOffset;
        var spec = SpecOf(p);
        int first = _hits.Count;
        int direct = struck >= 0 ? struck : p.Motion == ProjectileMotion.Attached ? p.AttachedTo : -1;
        if (spec.DetonateDamage is { } blastDirect && direct >= 0)
            ApplyHit(p.Owner, direct, blastDirect, zone == HitZone.None ? HitZone.Body : zone, 0f, 1f, p.Id, false, weapon: p.Weapon, thrownItem: p.Thrown,
                direction: BlastPush(point, direct, p.Velocity));
        // Every damaging blast in file order; a lingering one is applied now and stays (StepAreas).
        foreach (var splash in spec.AllSplashes)
        {
            ApplyBlast(p.Owner, p.Id, point, splash, weapon: p.Weapon, thrownItem: p.Thrown);
            if (splash.Lingers) _areas.Add(new LingeringArea(p.Id, p.Owner, p.Weapon, p.Thrown, point, splash) { Applications = 1 });
        }
        var dealt = _hits.Skip(first).ToList();
        var e = new DetonationEvent(Tick, p.Id, p.Owner, p.Weapon, p.Stage, point, spec.AllSplashes.Select(x => x.Radius).DefaultIfEmpty(0f).Max(),
            dealt.Select(h => h.Target).Distinct().Count(), dealt.Sum(h => h.ShieldDamage), dealt.Sum(h => h.HealthDamage), dealt.Count(h => h.Killed)) { Thrown = p.Thrown };
        _detonations.Add(e);
        _recentDetonations.Add(e);
    }

    /// <summary>
    /// A blast on every living player (the owner too unless the splash has IgnoreSelf) whose body centre is within the radius and, with LineOfSight, visible
    /// from the blast point, scaled by <see cref="Combat.SplashIntensity"/>, on the body multipliers. A blind grenade's blast
    /// (<see cref="SplashSpec.BlindOnly"/>) only blinds (<see cref="BlindBlast"/>).
    /// </summary>
    /// <param name="filtered">Honour the splash's <see cref="SplashSpec.TargetFilter"/> (M43, perk blasts: <see cref="BlastReaches"/>).</param>
    /// <param name="perk">The perk whose blast this is (M43; index into <see cref="SimConfig.Perks"/>, -1 none): recorded in the hits.</param>
    void ApplyBlast(int owner, int projectile, Vector3 point, SplashSpec splash, bool landing = false, int weapon = -1, bool thrownItem = false, bool melee = false,
        bool filtered = false, int perk = -1)
    {
        if (splash.BlindOnly) { BlindBlast(owner, projectile, point, splash); return; }   // M37: the blind grenade
        foreach (var target in _players.Keys.ToList())
        {
            var player = _players[target];
            if (!player.Alive || (splash.IgnoreSelf && target == owner)) continue;
            if (filtered && !BlastReaches(owner, target, splash)) continue;
            var centre = player.Position + new Vector3(0, 0, ChestOf(player));
            float distance = Vector3.Distance(centre, point);
            if (distance > splash.Radius) continue;
            if (splash.LineOfSight && Blocked(point, centre)) continue;
            float intensity = Combat.SplashIntensity(splash.FallOff, distance);
            ApplyHit(owner, target, splash.Damage, HitZone.Body, distance, intensity, projectile, true, landing: landing, weapon: weapon, thrownItem: thrownItem,
                direction: BlastPush(point, target, Vector3.Zero), melee: melee, perk: perk);
            BlastEffects(owner, target, splash, point, intensity);   // reactions and blindness (M28)
        }
    }

    /// <summary>
    /// A blast's push on a player (M23, for the ragdoll; ours): from the blast point to the player's body centre; when they
    /// coincide, along <paramref name="fallback"/> (a projectile's velocity), else straight up.
    /// </summary>
    Vector3 BlastPush(Vector3 point, int target, Vector3 fallback)
    {
        var away = _players.TryGetValue(target, out var player) ? player.Position + new Vector3(0, 0, ChestOf(player)) - point : Vector3.Zero;
        if (away.LengthSquared() > 1e-8f) return away;
        return fallback.LengthSquared() > 1e-8f ? fallback : Vector3.UnitZ;
    }

    /// <summary>One tick of every lingering blast: it ends when its age reaches Lifetime, else it applies whenever its period comes round.</summary>
    void StepAreas()
    {
        float dt = Config.TickSeconds;
        for (int i = 0; i < _areas.Count; i++)
        {
            var a = _areas[i];
            a = a with { Age = a.Age + dt };
            if (a.Age + 1e-4f >= a.Splash.Lifetime) { _areas.RemoveAt(i--); continue; }
            // Application k is due at k × Period (counted, so float sums do not drift by a tick).
            if (a.Age + 1e-4f >= a.Applications * a.Splash.Period)
            {
                ApplyBlast(a.Owner, a.Projectile, a.Position, a.Splash, weapon: a.Weapon, thrownItem: a.Thrown);
                a = a with { Applications = a.Applications + 1 };
            }
            _areas[i] = a;
        }
    }

    /// <summary>Each player's <see cref="PlayerState.ProjectilesOut"/>: its remote projectiles not yet set to detonate.</summary>
    void CountProjectilesOut()
    {
        foreach (var id in _players.Keys.ToList())
        {
            int count = 0;
            foreach (var p in _projectiles) if (p.Owner == id && p.Remote && !p.Scheduled) count++;
            if (_players[id].ProjectilesOut != count) _players[id] = _players[id] with { ProjectilesOut = count };
        }
    }

    void RemoveProjectilesOf(int playerId)
    {
        _projectiles.RemoveAll(p => p.Owner == playerId);
        _areas.RemoveAll(a => a.Owner == playerId);

        for (int i = 0; i < _projectiles.Count; i++)
            if (_projectiles[i].AttachedTo == playerId) _projectiles[i] = _projectiles[i] with { Motion = ProjectileMotion.Stuck, AttachedTo = -1 };
    }
}
