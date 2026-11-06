using System.Numerics;

namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// fix/charge-player-collision (protocol 57; the owner's playtest of 2026-11-06): players never pass through each other and a charge meets the body it runs into
    /// (<see cref="SolidBodies"/>). False (test aid <c>--old-body-contact</c> on the server and every client): the protocol 56 rules (the charge's contact cone from the tick's
    /// start, the charger stopped where its tick's move ended, no check of the bodies as they really stand). Hashed only when false.
    /// </summary>
    public bool SolidBodies { get; init; } = true;
}

/// <summary>
/// fix/charge-player-collision (protocol 57; rules **ours**, spec/behavior.md "Solid bodies"). The owner (2026-11-06): "Sometimes you can charge through an enemy instead of impacting on
/// them and their body stopping your movement. It happens a lot. ... Send another pass to ensure that players never pass directly through other players."
/// <para>
/// <b>Why it happened</b>: every player's move collides with the others as its own client last saw them (sim/player-collision: prediction-exact, carried on by velocity), and nothing
/// checked the bodies as they really stand. A target that turned, stopped or dodged within a round trip was somewhere else in the charger's picture, so the charger's
/// move (1 to 1.3 m a tick in the bull rush's burst) went straight through the real body; the charge's contact then measured its 45 degree cone from wherever the tick's move began, so
/// a body off the line was met or missed by the tick's phase, and a charger that did connect kept the whole tick's move, up to a metre past the point it met.
/// </para>
/// <para>
/// <b>The rules</b>: (1) after every player has moved, the world sweeps each pair's motion this tick against each other as they really are (<see cref="World"/>'s guard): two bodies
/// whose relative path would bring their axes closer than <see cref="GuardDistance"/> (0.45 m; 0.75 m when either is charging), or closer than they already
/// were, are stopped where they reach it, each one only if it was moving toward the other (its motion into the other is lost, as against a wall). Server only: a client whose
/// picture was right predicts this already; one whose picture was wrong is corrected to the contact. (2) A charge also tests the bodies as they really moved
/// (<see cref="ChargeLock.Find"/>), so a charge that runs into a body always meets it. (3) The charge's contact is a sweep (<see cref="Rush.Contact"/>), met at the first touch of
/// its reach anywhere in front of the charger. (4) A charger that meets a body stops at the point of its move where it met it (<see cref="AtContact"/>), not a tick's move beyond.
/// (3) and (4) are predicted by the own client exactly as the server resolves them; (1) and (2) run only in the server's world (<see cref="World.SolidGuard"/>).
/// Every rule applies between opponents (<see cref="World.Hostile"/>) only: allies keep the protocol 56 bodies and contact, so the co-op of the added content is unchanged.
/// </para>
/// </summary>
public static class SolidBodies
{
    /// <summary>
    /// Ours: the deepest a move may push two capsules into each other when either is charging (the bull rush or the dash attack's lunge) before the guard stops it (metres): the charge
    /// meets the body at the capsules' edge, as the models' shoulders meet.
    /// </summary>
    public const float ChargeDepth = 0.15f;
    /// <summary>
    /// feel/charge-clash (ours): the depth with <see cref="SimConfig.ChargeClash"/> on: 5 cm, a slack the shared player collision does not reach (bodies pressing on each other through its lagged picture settle up to about 1.5 cm in,
    /// so the guard does not nudge them by rounding and the prediction stays exact) and too small to see; deeper intrusions from a stale picture are cut.
    /// </summary>
    public const float ClashDepth = 0.05f;
    /// <summary>Ours: the guard's passes over the pairs per tick (a stop can bring a player back into a third one's way).</summary>
    public const int GuardPasses = 3;

    /// <summary>
    /// The closest the guard lets two bodies' axes come by moving (metres): with a charging body the two radii less <see cref="ChargeDepth"/> (0.75 m), else one radius (0.45 m: no
    /// body more than half into another, never through it). Ours: the walkers' limit is deep because a shallower one corrects every jostle a lagging picture gets slightly wrong
    /// (measured in an eight-player crowd at 150 ms: a 0.75 m limit mispredicted 27 % of ticks, 0.45 m 5 %); the push apart opens what is left.
    /// </summary>
    public static float GuardDistance(SimConfig c, bool charging)
    {
        float r = MathF.Max(c.CapsuleRadius, 0.05f);
        return charging ? MathF.Max(2f * r - (c.ChargeClash ? ClashDepth : ChargeDepth), r) : r;   // feel/charge-clash: a charger meets a body at its edge, never inside it
    }

    /// <summary>Whether two bodies' heights overlap side by side (neither on the other's head), as <see cref="PlayerCollision.Relate"/>'s Side.</summary>
    public static bool Beside(Vector3 a, Vector3 b, SimConfig c)
    {
        float height = MathF.Max(c.CapsuleHeight, 2f * MathF.Max(c.CapsuleRadius, 0.05f));
        return MathF.Abs(a.Z - b.Z) < height - PlayerCollision.OnTopBand;
    }

    /// <summary>
    /// One pair of the guard: <paramref name="a0"/> to <paramref name="a1"/> and <paramref name="b0"/> to <paramref name="b1"/> are this tick's moves. When the relative path brings the axes
    /// closer than <paramref name="distance"/> (<see cref="GuardDistance"/>; or than they were at the start, if already closer), the moves are cut at the first moment they reach that distance: each one that was moving
    /// toward the other (and, should the pair still be too close, both). Returns whether anything changed; <paramref name="normal"/> points from b toward a at that moment.
    /// </summary>
    public static bool Guard(Vector3 a0, ref Vector3 a1, Vector3 b0, ref Vector3 b1, float distance, SimConfig c, out Vector2 normal)
    {
        normal = default;
        if (!Beside(a0, b0, c) || !Beside(a1, b1, c)) return false;
        var d0 = Flat(a0 - b0);
        var d1 = Flat(a1 - b1);
        float limit = MathF.Min(distance, d0.Length());
        var rel = d1 - d0;
        float aa = rel.LengthSquared();
        if (!(aa > 1e-10f)) return false;
        // Closest approach along the relative path.
        float tc = Math.Clamp(-Vector2.Dot(d0, rel) / aa, 0f, 1f);
        if ((d0 + rel * tc).Length() >= limit - PlayerCollision.Tolerance) return false;
        // The first moment the axes are at the limit (at once when they start at it).
        float half = Vector2.Dot(d0, rel);
        float disc = half * half - aa * (d0.LengthSquared() - limit * limit);
        float t = disc > 0f ? Math.Clamp((-half - MathF.Sqrt(disc)) / aa, 0f, 1f) : 0f;
        var at = d0 + rel * t;
        normal = at.LengthSquared() > 1e-10f ? Vector2.Normalize(at) : (d0.LengthSquared() > 1e-10f ? Vector2.Normalize(d0) : new Vector2(1f, 0f));
        var da = Flat(a1 - a0); var db = Flat(b1 - b0);
        bool cutA = Vector2.Dot(da, normal) < 0f, cutB = Vector2.Dot(db, normal) > 0f;
        var na = cutA ? a0 + (a1 - a0) * t : a1;
        var nb = cutB ? b0 + (b1 - b0) * t : b1;
        if (Flat(na - nb).Length() < limit - PlayerCollision.Tolerance) { na = a0 + (a1 - a0) * t; nb = b0 + (b1 - b0) * t; }
        bool changed = na != a1 || nb != b1;
        a1 = na; b1 = nb;
        return changed;
    }

    /// <summary>A velocity without its part going into the body on the other side of <paramref name="normal"/> (the normal points away from that body).</summary>
    public static Vector3 WithoutInto(Vector3 v, Vector2 normal)
    {
        float into = v.X * normal.X + v.Y * normal.Y;
        return into < 0f ? new Vector3(v.X - normal.X * into, v.Y - normal.Y * into, v.Z) : v;
    }

    /// <summary>
    /// The charger's state stopped at the point of its tick's move where it met a body (<paramref name="t"/>, 0..1 of the move from <paramref name="before"/> to
    /// <paramref name="charging"/>), swept through the level from where it began the tick (as <see cref="ChargeLock.Pull"/> does); <paramref name="charging"/> itself without
    /// <see cref="SimConfig.SolidBodies"/> or for a contact at the move's end. The server and the own client's prediction call it alike.
    /// </summary>
    public static PlayerState AtContact(PlayerState before, PlayerState charging, float t, SimConfig c, CollisionWorld? world)
    {
        if (!c.SolidBodies || !(t < 1f) || !before.Alive) return charging;
        t = Math.Clamp(t, 0f, 1f);
        var delta = charging.Position - before.Position;
        if (world is null) return charging with { Position = before.Position + delta * t };
        var move = Flat(delta) * t;
        if (move.LengthSquared() < 1e-12f) return charging with { Position = before.Position, Grounded = before.Grounded };
        PlayerState moved;
        using (PlayerCollision.Use(null)) moved = Movement.Glide(before, charging.Yaw, move / c.TickSeconds, c, world);
        return charging with { Position = moved.Position, Grounded = moved.Grounded };
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
}

public sealed partial class World
{
    /// <summary>
    /// fix/charge-player-collision: each living player's move this tick after the guard (where it began the tick, where it ended), for the charges' check of the bodies as they
    /// really are (<see cref="ChargeLock.Find"/>). Players who spawned this tick or were dead at its start have none.
    /// </summary>
    readonly Dictionary<int, (Vector3 From, Vector3 To, int Class, Team Team)> _moves = new();

    /// <summary>
    /// fix/charge-player-collision: the world runs the guard and lets a charge meet the bodies as they really moved (<see cref="SolidBodies"/> rules 1 and 2; with
    /// <see cref="SimConfig.SolidBodies"/>). The server sets it (as <see cref="BoundBodies"/>); tools and the older world tests step without it.
    /// </summary>
    public bool SolidGuard { get; init; }

    /// <summary>Diagnostics (tests, <c>--log-overlaps</c>): pairs the guard stopped during the last tick.</summary>
    public int LastGuardStops { get; private set; }

    /// <summary>
    /// The guard (<see cref="SolidBodies"/>, rule 1): after every player moved, each pair of living opponents (<see cref="Hostile"/>) that were alive at the tick's start is swept against each other as they
    /// really moved, in id order, up to <see cref="SolidBodies.GuardPasses"/> passes; a player cut short loses its velocity into the other.
    /// </summary>
    void GuardBodies(IReadOnlyDictionary<int, PlayerState> start)
    {
        _moves.Clear();
        LastGuardStops = 0;
        var ids = new List<int>();
        foreach (var (id, now) in _players)
            if (now.Alive && start.TryGetValue(id, out var then) && then.Alive && !_respawns.Contains(id)) ids.Add(id);
        ids.Sort();
        foreach (int id in ids) _moves[id] = (start[id].Position, _players[id].Position, _players[id].Class, _players[id].Team);
        if (!Config.PlayerBodies || !Config.SolidBodies || !SolidGuard || ids.Count < 2) return;
        for (int pass = 0; pass < SolidBodies.GuardPasses; pass++)
        {
            bool changed = false;
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var (a0, a1, ca, ta) = _moves[ids[i]];
                    var (b0, b1, cb, tb) = _moves[ids[j]];
                    if (!Hostile(ta, tb)) continue;   // opponents only: allies keep protocol 56's bodies (so the co-op of the added content is unchanged)
                    var na = a1; var nb = b1;
                    bool charging = Charging(ids[i], start) || Charging(ids[j], start);
                    if (!SolidBodies.Guard(a0, ref na, b0, ref nb, SolidBodies.GuardDistance(Config, charging), Config, out var normal)) continue;
                    changed = true;
                    LastGuardStops++;
                    if (na != a1)
                    {
                        _moves[ids[i]] = (a0, na, ca, ta);
                        _players[ids[i]] = _players[ids[i]] with { Position = na, Velocity = SolidBodies.WithoutInto(_players[ids[i]].Velocity, normal) };
                    }
                    if (nb != b1)
                    {
                        _moves[ids[j]] = (b0, nb, cb, tb);
                        _players[ids[j]] = _players[ids[j]] with { Position = nb, Velocity = SolidBodies.WithoutInto(_players[ids[j]].Velocity, -normal) };
                    }
                }
            if (!changed) break;
        }
    }

    /// <summary>Whether a player is charging (the bull rush or the dash attack's lunge) at the tick's start or after its move (a wall's stop this tick included).</summary>
    bool Charging(int id, IReadOnlyDictionary<int, PlayerState> start) =>
        Rush.Charging(Rush.ChargingPhase(_players[id])) || (start.TryGetValue(id, out var then) && Rush.Charging(then.Rush))
        || (Config.ChargeClash && _players[id].Alive && _players[id].Rush is RushPhase.Collision or RushPhase.DashHit or RushPhase.DashCollision);   // feel/charge-clash: a charge's stop (its shove and recoil) too
}
