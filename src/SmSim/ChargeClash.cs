using System.Numerics;

namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// feel/charge-clash (protocol 59; the owner's recording of the original, 2026-11-06): a charge that stuns its victim locks the two together through the collision stop's forward
    /// shove (<see cref="ChargeClash"/>). False (test aid <c>--no-charge-clash</c> on the server and every client): the protocol 58 stop (the charger's shove collides with the victim's
    /// body picture; the victim stands). Hashed only when false.
    /// </summary>
    public bool ChargeClash { get; init; } = true;
}

/// <summary>
/// feel/charge-clash (protocol 59; spec/behavior.md "The charge clash"). The owner (2026-11-06), with a recording of the original: "the player should never, ever phase through another
/// player model. both models 'clash', one delivering the charge one receiving (kind of sticking together like a lock on)."
/// <para>
/// <b>What the recording shows</b> (30 fps, frames 163 to 199): the rush runs about 0.6 s, homing on the target; at the contact the charger's shoulder meets the victim's chest, the
/// victim's shield flashes, and for about 0.18 s (5 to 6 frames) the two travel together, the victim shoved ahead of the charger at a fixed distance; then the charger twists back in its
/// recoil while the victim, stunned, stays where the shove left it, about a metre in front. No knockback beyond the shove.
/// </para>
/// <para>
/// <b>The data</b>: the charger's stop is <c>SM_NAV_Bullrush2Idle_F_Collision_01</c>, whose extracted motion goes 1.83 m <i>forward</i> in its first 0.2 s (0.54, 0.51, 0.39, 0.24, 0.14,
/// 0.02 m a tick) and then 1.11 m back by 0.7 s: the shove and the recoil of the recording. Before this change the charger's shove collided with the victim's body as the charger's client
/// last saw it (often where it stood before the lock's pull), so the charger ran up to 1.8 m into the victim: the phasing the owner saw.
/// </para>
/// <para>
/// <b>The rules</b> (the shove's length and timing from the data; the pairing ours): a bull rush whose victim the hit put in a reaction (the stun; the Assault's stagger) opens a clash.
/// While the charger's collision stop moves forward (<see cref="CarryTicks"/>: up to the clip's farthest point, 6 ticks), (1) the charger's own move ignores the victim's body (its own
/// client predicts this exactly: <see cref="Carrying"/>, <see cref="Without"/>), and (2) the world carries the victim by the charger's move, swept through the level; a victim the level
/// stops holds the charger back by the same amount, and the guard's cuts against third players apply to both (<c>World.CarryClashes</c>, <c>World.HoldClashes</c>). The two keep the
/// lock distance (<see cref="ChargeLock.LockDistance"/>, the capsules touching) on every tick of the clash. The recoil (the clip's backward part) then separates them.
/// </para>
/// </summary>
public static class ChargeClash
{
    /// <summary>The shove's ticks: the collision stop's ticks up to the time its forward motion peaks (the data: 0.2 s, 6 ticks at 30 Hz); 0 without a forward part.</summary>
    public static int CarryTicks(RushMove? stop, SimConfig c)
    {
        if (stop is null || stop.Path.Count < 2 || !(stop.ClipLength > 0f) || !(c.TickSeconds > 0f)) return 0;
        float best = 0f; int bestTick = 0;
        int ticks = (int)MathF.Ceiling(stop.ClipLength / c.TickSeconds);
        for (int k = 1; k <= ticks; k++)
        {
            float y = stop.At(k * c.TickSeconds).Y;
            if (y > best + 1e-4f) { best = y; bestTick = k; }
        }
        return bestTick;
    }

    /// <summary>
    /// Whether the player's next tick moves it in a clash's shove: the bull rush's collision stop with <see cref="PlayerState.RushTicks"/> + 1 within <see cref="CarryTicks"/> (the step counts
    /// the tick before it moves). The server's world and the own client's prediction both use it to leave the victim out of the charger's bodies.
    /// </summary>
    public static bool Carrying(PlayerState before, SimConfig c) =>
        c.ChargeClash && c.ChargeLock && before.Alive && before.Rush == RushPhase.Collision && before.RushTicks + 1 <= CarryTicks(Rush.SpecOf(c, before)?.Collision, c);

    /// <summary>Whether a player's last tick moved it in the shove (after the step: <see cref="PlayerState.RushTicks"/> within 1..<see cref="CarryTicks"/>).</summary>
    public static bool Carried(PlayerState after, SimConfig c) =>
        c.ChargeClash && c.ChargeLock && after.Alive && after.Rush == RushPhase.Collision && after.RushTicks >= 1 && after.RushTicks <= CarryTicks(Rush.SpecOf(c, after)?.Collision, c);

    /// <summary>
    /// The own client's guess that a contact opens a clash (the server opens it when the hit really put the victim in a reaction: <c>World.OpenClashes</c>): a bull rush (not the dash
    /// attack) of a charger on side <paramref name="side"/> meeting an opponent (<see cref="World.Hostile"/>) that can react now (<see cref="Reaction.CanReact"/>: no hyper armour, no flight,
    /// no immunity), with a primary reaction to give.
    /// </summary>
    public static bool Predicts(Team side, PlayerState victim, PlayerState charging, SimConfig c) =>
        c.ChargeClash && c.ChargeLock && victim.Alive && charging.Rush is RushPhase.Rush or RushPhase.Collision && !Rush.IsDash(charging.Rush) && World.Hostile(side, victim.Team)
        && Rush.SpecOf(c, charging) is { } spec && spec.PrimaryReaction.Kind != ReactionKind.None && Reaction.CanReact(victim, c);

    /// <summary>The bodies without player <paramref name="id"/> (the clash's victim, carried, not collided with); the same view when it is not in it.</summary>
    public static BodyView? Without(BodyView? bodies, int id)
    {
        if (bodies is null || id <= 0) return bodies;
        bool found = false;
        foreach (var b in bodies.Bodies) if (b.Id == id) { found = true; break; }
        return found ? BodyView.Of(bodies.Bodies.Where(b => b.Id != id), bodies.Self) : bodies;
    }

    /// <summary>
    /// The victim carried by <paramref name="move"/> (the charger's move this tick), swept through the level only (the charger is beside it, third players are the guard's): stopped before
    /// a kill volume; its horizontal velocity zeroed (the stun holds it). Returns the carried state.
    /// </summary>
    public static PlayerState Carry(PlayerState victim, Vector2 move, SimConfig c, CollisionWorld? world)
    {
        if (move.LengthSquared() < 1e-12f) return victim;
        PlayerState next;
        if (world is null) next = victim with { Position = victim.Position + new Vector3(move.X, move.Y, 0f) };
        else
        {
            using (PlayerCollision.Use(null)) next = Movement.Glide(victim, victim.Yaw, move / c.TickSeconds, c, world);
            if (world.KillsPlayer(victim.Position, next.Position, c.CapsuleHeight)) return victim with { Velocity = new Vector3(0f, 0f, victim.Velocity.Z) };
        }
        return next with { Velocity = new Vector3(0f, 0f, next.Velocity.Z) };
    }

    /// <summary>A move cut to the length <paramref name="other"/> reached along it (both shoves share one direction; a shorter or opposite one holds it back).</summary>
    public static Vector2 Common(Vector2 move, Vector2 other)
    {
        float length = move.Length();
        if (!(length > 1e-6f)) return Vector2.Zero;
        var dir = move / length;
        float along = Math.Clamp(Vector2.Dot(other, dir), 0f, length);
        return dir * along;
    }
}

public sealed partial class World
{
    /// <summary>feel/charge-clash: each charger in a clash and its victim (<see cref="ChargeClash"/>).</summary>
    readonly Dictionary<int, int> _clashes = new();
    /// <summary>feel/charge-clash: this tick's connects whose victim the hit put in a reaction (charger, victim), for <see cref="StopRushes"/> to open their clashes.</summary>
    readonly Dictionary<int, int> _clashContacts = new();

    /// <summary>The victim of a charger's clash (tests, diagnostics), or 0.</summary>
    public int ClashVictim(int charger) => _clashes.TryGetValue(charger, out int v) ? v : 0;

    /// <summary>Diagnostics (tests, logs): clashes opened since the world began.</summary>
    public int ClashesOpened { get; private set; }

    /// <summary>The bodies a player's step collides with, less its clash victim while it shoves (<see cref="ChargeClash.Carrying"/>).</summary>
    BodyView? ClashBodies(int id, BodyView? bodies) =>
        bodies is not null && _clashes.TryGetValue(id, out int victim) && _players.TryGetValue(id, out var s) && ChargeClash.Carrying(s, Config) ? ChargeClash.Without(bodies, victim) : bodies;

    /// <summary>
    /// After every player stepped (before the guard): each clash still shoving carries its victim by the charger's move (<see cref="ChargeClash.Carry"/>); a victim the level stops holds
    /// the charger back to the same move. A clash whose charger left the shove, or whose charger or victim died or left its reaction, closes.
    /// </summary>
    void CarryClashes(IReadOnlyDictionary<int, PlayerState> start)
    {
        if (_clashes.Count == 0) return;
        foreach (var (charger, victim) in _clashes.OrderBy(kv => kv.Key).ToList())
        {
            if (!_players.TryGetValue(charger, out var c) || !_players.TryGetValue(victim, out var v) || !c.Alive || !v.Alive || v.Reaction == ReactionKind.None
                || !ChargeClash.Carried(c, Config) || !start.TryGetValue(charger, out var c0) || !c0.Alive || _respawns.Contains(charger) || _respawns.Contains(victim))
            {
                _clashes.Remove(charger);
                continue;
            }
            var move = Flat(c.Position - c0.Position);
            var carried = ChargeClash.Carry(v, move, Config, Collision);
            var achieved = Flat(carried.Position - v.Position);
            var common = ChargeClash.Common(move, achieved);
            if ((common - move).LengthSquared() > 1e-8f)
                _players[charger] = c with { Position = c0.Position + new Vector3(common.X, common.Y, c.Position.Z - c0.Position.Z), Velocity = new Vector3(0f, 0f, c.Velocity.Z) };
            if ((achieved - common).LengthSquared() > 1e-8f) carried = ChargeClash.Carry(v, common, Config, Collision);
            _players[victim] = carried;
        }
    }

    /// <summary>
    /// After the guard: a pair whose two moves the guard cut differently is held to the shorter (both from where they began the tick), so the lock distance holds and neither runs into
    /// the other.
    /// </summary>
    void HoldClashes(IReadOnlyDictionary<int, PlayerState> start)
    {
        foreach (var (charger, victim) in _clashes.OrderBy(kv => kv.Key))
        {
            if (!_players.TryGetValue(charger, out var c) || !_players.TryGetValue(victim, out var v) || !start.TryGetValue(charger, out var c0) || !start.TryGetValue(victim, out var v0)) continue;
            var mc = Flat(c.Position - c0.Position); var mv = Flat(v.Position - v0.Position);
            if ((mc - mv).LengthSquared() < 1e-8f) continue;
            var common = ChargeClash.Common(mc, mv);
            _players[charger] = c with { Position = new Vector3(c0.Position.X + common.X, c0.Position.Y + common.Y, c.Position.Z), Velocity = new Vector3(0f, 0f, c.Velocity.Z) };
            _players[victim] = v with { Position = new Vector3(v0.Position.X + common.X, v0.Position.Y + common.Y, v.Position.Z), Velocity = new Vector3(0f, 0f, v.Velocity.Z) };
            if (_moves.TryGetValue(charger, out var m)) _moves[charger] = (m.From, _players[charger].Position, m.Class, m.Team);
            if (_moves.TryGetValue(victim, out var n)) _moves[victim] = (n.From, _players[victim].Position, n.Class, n.Team);
        }
    }

    /// <summary>Opens the clash of each charger stopped this tick on a victim its hit put in a reaction (after <see cref="StopRushes"/>).</summary>
    void OpenClashes()
    {
        try
        {
            if (!Config.ChargeClash || !Config.ChargeLock) return;
            foreach (var (charger, victim) in _clashContacts)
                if (_players.TryGetValue(charger, out var c) && c is { Alive: true, Rush: RushPhase.Collision, RushTicks: 0 }
                    && _players.TryGetValue(victim, out var v) && v.Alive && v.Reaction != ReactionKind.None)
                {
                    _clashes[charger] = victim;
                    ClashesOpened++;
                }
        }
        finally { _clashContacts.Clear(); }
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
}
