using System.Numerics;

namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// anim/melee-interaction (protocol 48, no wire change; the owner's request of 2026-10-31: "charges don't consistently lock the target and charge deliverer
    /// together when it connects"): the charge's contact also counts the other player's blocking body and locks the two together (<see cref="ChargeLock"/>).
    /// False (test aid <c>--no-charge-lock</c> on the server and every client): the M29 contact and stop as before. Hashed only when false.
    /// </summary>
    public bool ChargeLock { get; init; } = true;
}

/// <summary>A charge's contact this tick (<see cref="ChargeLock.Find"/>): the player reached, where it was met (its seen feet or its blocking body's) and how far along the move (0..1).</summary>
public readonly record struct ChargeContact(int Target, Vector3 Reference, float T);

/// <summary>
/// anim/melee-interaction: the charge connect made consistent (rules **ours**, spec/behavior.md "Charge connect: the lock"). The owner (2026-10-31): "when a player charges a
/// target, the target is stunned and plays the stun animation. the player delivering the charge ... should play the recoil animation like he charged into a wall ... charges don't
/// consistently lock the target and charge deliverer together when it connects".
/// <para>
/// <b>Why it was inconsistent</b> (found by reading the paths and measured in <c>ChargeLockTests</c>): (1) the contact tested only the other players as the charger's client saw them
/// (rewound to its view tick), while the capsule that stopped the charger's move was the newer body picture (sim/player-collision, carried on by velocity): a player running at the
/// charger blocked it up to a metre short of the rewound picture's reach (2 × 0.45 + 0.3 m), so the charger pressed against him without a contact, slid along him in its rush clip and often never
/// connected; (2) on a contact the charger stopped wherever its tick's move ended, facing its start heading, and the victim stayed at its own (current) position: the two stood 0.9 to
/// 2.5 m apart at any angle within 45 degrees, differently each time; (3) the own client never predicted the contact (the world resolves it), so the own charger ran on in its rush clip
/// for a round trip and then jumped into the collision stop part-way through.
/// </para>
/// <para>
/// <b>The lock</b>: a contact counts the seen picture or the blocking body (whichever the move reaches first); the charger keeps its position and turns to face the point it met
/// (<see cref="Direction"/>: from its move's closest approach toward it, within the contact half-angle of its heading) and goes into the data's collision stop (the wall-hit recoil,
/// <c>SM_NAV_Bullrush2Idle_F_Collision_01</c>); a victim the stun takes is moved to stand exactly <see cref="LockDistance"/> in front of it along that line (<see cref="Pull"/>: swept
/// through the level, at most <see cref="MaxPull"/>), so the charger's shoulder meets the victim's chest on every connect, for every viewer. The client predicts the stop and the turn
/// exactly (the same view and bodies: <c>GameClient</c>), so the own recoil starts on the contact tick.
/// </para>
/// </summary>
public static class ChargeLock
{
    /// <summary>Ours: the farthest the victim is moved to the lock spot (metres; a round trip of lag at a sprint is about 2.2 m).</summary>
    public const float MaxPull = 2.5f;

    /// <summary>Ours: the distance between the two at the lock (feet to feet, metres): the capsules touching (2 × the capsule radius, 0.9 m), the shoulder at the chest.</summary>
    public static float LockDistance(SimConfig c) => 2f * c.CapsuleRadius;

    /// <summary>
    /// The first player a charge's move this tick (<paramref name="from"/> to <paramref name="to"/>, heading <paramref name="yaw"/>) reaches (<see cref="Rush.Contact"/>): each living
    /// player of <paramref name="seen"/> (the attacker's rewound view) at its seen feet, and with <see cref="SimConfig.ChargeLock"/> also at its blocking body's feet in
    /// <paramref name="bodies"/> (the picture the move collided with); the earlier of the two counts. Visible as before: a clear line from the eye to the chest of the point met
    /// through weapon collision. Ties go to the lower id. <paramref name="aliveNow"/>: the server's check that the player is still alive now (null: not checked).
    /// </summary>
    public static ChargeContact? Find(int attacker, Vector3 from, Vector3 to, float yaw, RushSpec spec, SimConfig c, IReadOnlyDictionary<int, PlayerState> seen,
        BodyView? bodies, CollisionWorld? world, Func<int, bool>? aliveNow = null)
    {
        var eye = to + new Vector3(0, 0, c.EyeHeight);
        int target = -1; float first = float.MaxValue; var reference = Vector3.Zero;
        foreach (var (id, then) in seen)
        {
            if (id == attacker || !then.Alive || (aliveNow is not null && !aliveNow(id))) continue;
            float? t = Rush.Contact(from, to, yaw, then.Position, spec, c);
            var at = then.Position;
            if (c.ChargeLock && BodyOf(bodies, id) is { } body && Rush.Contact(from, to, yaw, body.Feet, spec, c) is float tb && (t is not float ts || tb < ts)) { t = tb; at = body.Feet; }
            if (t is not float tt || tt > first || (tt == first && id > target)) continue;
            if (world is not null)
            {
                var toChest = at + new Vector3(0, 0, HitVolumes.CentreHeight(c.For(then.Class))) - eye;
                float length = toChest.Length();
                if (length > 1e-4f && world.Weapons.Raycast(eye, toChest / length, length) is { } wall && wall.Distance < length) continue;   // behind a wall
            }
            first = tt; target = id; reference = at;
        }
        return target < 0 ? null : new ChargeContact(target, reference, first);
    }

    static PlayerBody? BodyOf(BodyView? bodies, int id)
    {
        if (bodies is null) return null;
        foreach (var b in bodies.Bodies) if (b.Id == id) return b;
        return null;
    }

    /// <summary>
    /// The line of the lock (unit, horizontal): from the point of the move closest to the contact (<see cref="ChargeContact.T"/>) toward the point met, turned back to within the
    /// contact half-angle of the heading; the heading itself when the two coincide.
    /// </summary>
    public static Vector2 Direction(Vector3 from, Vector3 to, float yaw, ChargeContact contact, RushSpec spec)
    {
        var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var a = new Vector2(from.X, from.Y); var b = new Vector2(to.X, to.Y);
        var closest = a + (b - a) * Math.Clamp(contact.T, 0f, 1f);
        var d = new Vector2(contact.Reference.X, contact.Reference.Y) - closest;
        float length = d.Length();
        if (!(length > 1e-3f) || !float.IsFinite(length)) return forward;
        d /= length;
        float max = Math.Clamp(float.IsFinite(spec.ContactHalfAngle) ? spec.ContactHalfAngle : 45f, 0f, 90f) * (MathF.PI / 180f);
        float angle = MathF.Atan2(forward.X * d.Y - forward.Y * d.X, Vector2.Dot(forward, d));   // from the heading to d, counter-clockwise
        if (MathF.Abs(angle) <= max) return d;
        float turn = MathF.Sign(angle) * max;
        return new Vector2(forward.X * MathF.Cos(turn) - forward.Y * MathF.Sin(turn), forward.X * MathF.Sin(turn) + forward.Y * MathF.Cos(turn));
    }

    /// <summary>The yaw facing a horizontal direction (yaw 0 faces +Y), the nearest turn from <paramref name="current"/> (yaws are not wrapped).</summary>
    public static float Facing(Vector2 d, float current)
    {
        float target = MathF.Atan2(-d.X, d.Y);
        float delta = target - current;
        delta -= MathF.Round(delta / (2f * MathF.PI)) * 2f * MathF.PI;
        return current + delta;
    }

    /// <summary>The charger's state after a contact (<paramref name="charging"/>: in its charging phase): the stop (<see cref="Rush.Stopped"/>), facing along <paramref name="d"/> with the lock.</summary>
    public static PlayerState Stopped(PlayerState charging, Vector2 d, SimConfig c)
    {
        var stopped = Rush.Stopped(charging);
        return c.ChargeLock ? stopped with { Yaw = Facing(d, charging.Yaw) } : stopped;
    }

    /// <summary>
    /// The victim moved to the lock spot: <see cref="LockDistance"/> in front of the charger at <paramref name="charger"/> along <paramref name="d"/>, at most <see cref="MaxPull"/>
    /// toward it, swept through the level (a wall stops it there; other players do not block it). Facing, velocity and everything else unchanged.
    /// </summary>
    public static PlayerState Pull(PlayerState victim, Vector3 charger, Vector2 d, SimConfig c, CollisionWorld? world)
    {
        var spot = new Vector2(charger.X, charger.Y) + d * LockDistance(c);
        var delta = spot - new Vector2(victim.Position.X, victim.Position.Y);
        float length = delta.Length();
        if (!(length > 1e-3f) || !float.IsFinite(length)) return victim;
        if (length > MaxPull) delta *= MaxPull / length;
        if (world is null) return victim with { Position = victim.Position + new Vector3(delta.X, delta.Y, 0f) };
        PlayerState moved;
        using (PlayerCollision.Use(null))
            moved = Movement.Glide(victim with { Velocity = new Vector3(0f, 0f, 0f) }, victim.Yaw, delta / c.TickSeconds, c, world);
        return victim with { Position = moved.Position, Grounded = moved.Grounded };
    }

    /// <summary>
    /// The charging state whose move this tick is tested for contacts, as the world picks it (<c>World.Step</c>): the charge connecting after the tick, else the one a wall stopped this
    /// tick (<see cref="Rush.WallStoppedFrom"/>) while it still connected; null otherwise. (The landing that ends a held charge is the server's alone: not predicted.)
    /// </summary>
    public static PlayerState? Charging(PlayerState before, PlayerState next, SimConfig c)
    {
        if (!next.Alive) return null;
        if (Rush.Connecting(next, c)) return next;
        return Rush.WallStoppedFrom(before, next, c) is { } charging && Rush.Connecting(charging, c) ? charging : null;
    }

    /// <summary>
    /// The client's prediction of a bull rush's contact (the server resolves it in <c>World.ResolveRush</c> with the same view, bodies and rules): the stopped and turned state when the
    /// tick's move reaches a player, else <paramref name="next"/> unchanged. Only with <see cref="SimConfig.ChargeLock"/>, a view (the rewound world the input names) and the bull rush (the
    /// dash attack is not predicted: its ticks name no exact view).
    /// </summary>
    public static PlayerState Predict(int self, PlayerState before, PlayerState next, SimConfig c, IReadOnlyDictionary<int, PlayerState>? view, BodyView? bodies, CollisionWorld? world,
        out ChargeContact? contact)
    {
        contact = null;
        if (!c.ChargeLock || view is null || Charging(before, next, c) is not { Rush: RushPhase.Rush } charging || Rush.SpecOf(c, charging) is not { } spec) return next;
        if (Find(self, before.Position, charging.Position, charging.Yaw, spec, c, view, bodies, world) is not { } found) return next;
        contact = found;
        return Stopped(charging, Direction(before.Position, charging.Position, charging.Yaw, found, spec), c);
    }
}
