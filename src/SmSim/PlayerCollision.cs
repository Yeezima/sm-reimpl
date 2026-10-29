using System.Numerics;

namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/player-collision (protocol 43): living players' capsules block each other in the shared movement step (<see cref="PlayerCollision"/>).
    /// False (test aid <c>--no-player-collision</c> on the server and every client): players pass through each other as before. Hashed only when
    /// false, so every config hashes as before the change.
    /// </summary>
    public bool PlayerBodies { get; init; } = true;
}

/// <summary>
/// Another living player's capsule as one movement step sees it (<see cref="PlayerCollision.See"/>): its id, its feet carried on to the step's tick
/// (<see cref="Feet"/>, what blocks) and its feet at the snapshot tick (<see cref="Seen"/>, what the push apart is measured from); radius and height
/// are the config's capsule.
/// </summary>
public readonly record struct PlayerBody(int Id, Vector3 Feet, Vector3 Seen);

/// <summary>The other players' bodies one player's movement steps collide with during one tick, in id order (so every sum over them is the same on server and client).</summary>
public sealed class BodyView
{
    public static readonly BodyView Empty = new([], null);
    readonly PlayerBody[] _bodies;
    BodyView(PlayerBody[] sorted, Vector3? self) { _bodies = sorted; Self = self; }

    public IReadOnlyList<PlayerBody> Bodies => _bodies;
    /// <summary>The stepping player's own feet at the snapshot tick (null: not in it, e.g. dead then): the push apart counts its progress from there.</summary>
    public Vector3? Self { get; }

    public static BodyView Of(IEnumerable<PlayerBody> bodies, Vector3? self = null)
    {
        var a = bodies.ToArray();
        if (a.Length == 0 && self is null) return Empty;
        Array.Sort(a, (x, y) => x.Id.CompareTo(y.Id));
        return new BodyView(a, self);
    }
}

/// <summary>
/// sim/player-collision (protocol 43; the owner's request of 2026-10-29: "improve player model collision. the models shouldn't get stuck inside
/// each other"). Before it, players had no collision with each other at all in the simulation (only the cameras were pushed out of bodies, M40-T6).
/// Now every living player is an upright capsule (<see cref="SimConfig.CapsuleRadius"/> 0.45 m, <see cref="SimConfig.CapsuleHeight"/> 1.9 m, the
/// same capsule as against the level) that the other living players' capsules block in the shared movement step (<see cref="Movement.Step"/>,
/// <see cref="Movement.Glide"/>, <see cref="Movement.Fly"/>: walking, sprinting, evades, knockback, the charge, melee lunges, jump-pack flight and
/// dives). Allies and enemies alike; the dead (ragdolls) and pickups do not collide.
/// <para>
/// <b>What a step collides with</b> (ours: the data has no rule): the others as the stepping player's client last knew them, so the client's
/// prediction reproduces the server exactly. The input names the newest snapshot its client held when it was sampled
/// (<see cref="PlayerInput.BodyTick"/>); the server keeps every tick's end state (<c>World.RecordBodies</c>, the same floats the snapshot carried)
/// and both sides take that tick's living players, each moved on by its velocity at that tick for the ticks up to the input's tick (at most
/// <see cref="MaxExtrapolationTicks"/> and <see cref="MaxExtrapolation"/>), so a player running straight is met about where he is now rather than
/// where he was a round trip ago. A player's step depends only on past ticks, never on who stepped first this tick: the result is order-independent.
/// </para>
/// <para>
/// <b>The rules</b> (ours): the push apart. Two capsules that overlapped sideways in the snapshot are pushed apart along the line between their axes
/// there (a tie on one spot: one of eight fixed directions from the two ids, opposite for each): each player moves out half of that overlap (the other
/// half is the other player's own step), less what it has already moved out along that line since the snapshot, at most <see cref="SeparationSpeed"/>
/// a second. Measuring from the snapshot, not from the stale picture of now, is what keeps a crowd from rocking: a player stops pushing once its share
/// is done, though the snapshot still shows the overlap for a round trip. The move: the part going into a body it overlaps is removed, and the rest
/// sweeps the circles of the two capsules (radius 2 × 0.45 m) and slides along the first it meets (up to four slides); the velocity loses what went
/// into a body, as against a wall (the push is not velocity). A body whose feet are within <see cref="OnTopBand"/> of another's top is above it: it is
/// not blocked sideways but slides off the head (the whole overlap, at <see cref="SlideOffSpeed"/>), and a body coming down onto a head is held at
/// the head's top (not grounded) until it has slid off (a jump-pack landing on a player slides off). Bodies above a player's head band are the upper
/// player's to resolve.
/// </para>
/// The rules apply only inside a <see cref="Use"/> scope (the world's step and the client's prediction set one per player and tick); outside one
/// (tools, bots' planners, older tests) movement is as before.
/// </summary>
public static class PlayerCollision
{
    /// <summary>Most ticks another player is moved on by its velocity from the snapshot tick to the input's tick (10 ticks = 0.33 s, about the lag at 150 ms RTT; ours).</summary>
    public const int MaxExtrapolationTicks = 10;
    /// <summary>Longest such move (metres; ours): a charge or a dash is not carried more than a sprint's 0.33 s ahead.</summary>
    public const float MaxExtrapolation = 3f;
    /// <summary>
    /// Oldest snapshot an input may name (ticks before its own; 24 ticks = 0.8 s: the shot rewind's 15 ticks plus the view slack; ours). The world
    /// never collides with an older picture: an older (or, on a server, a missing) body tick is raised to this many ticks back
    /// (<c>World.BodiesFor</c>), so a client cannot shed the bodies by naming none or an old one; an honest client names its newest snapshot.
    /// </summary>
    public const int MaxBodyLagTicks = 24;
    /// <summary>How fast an overlap is pushed apart (m/s, each player's own push; ours).</summary>
    public const float SeparationSpeed = 3f;
    /// <summary>How fast a body on another's head slides off it (m/s; ours).</summary>
    public const float SlideOffSpeed = 6f;
    /// <summary>Feet this close below another body's top (metres) count as on top of it, not beside it (ours).</summary>
    public const float OnTopBand = 0.3f;
    /// <summary>Feet up to this far above another body's top still stand on its head (metres; ours).</summary>
    public const float HeadReach = 0.05f;
    /// <summary>Overlap ignored (metres): float noise between touching capsules.</summary>
    public const float Tolerance = 1e-4f;
    const int MaxSlides = 4;

    [ThreadStatic] static BodyView? t_current;
    [ThreadStatic] static Vector2 t_lastPush;

    /// <summary>
    /// The push apart the last movement step on this thread added to its move (metres; zero without bodies). A caller that tests the move it got
    /// against the move it wished (the evade's wall test) leaves it out: being pushed is not being stopped.
    /// </summary>
    public static Vector2 LastPush { get => t_lastPush; internal set => t_lastPush = value; }


    /// <summary>The bodies the movement steps of the current scope collide with (null: none, movement as before).</summary>
    public static BodyView? Current => t_current;

    /// <summary>Restores the previous scope's bodies when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        readonly BodyView? _previous;
        internal Scope(BodyView? previous) => _previous = previous;
        public void Dispose() => t_current = _previous;
    }

    /// <summary>Movement steps collide with <paramref name="view"/> until the returned scope is disposed (null: with nothing).</summary>
    public static Scope Use(BodyView? view)
    {
        var scope = new Scope(t_current);
        t_current = view;
        return scope;
    }

    /// <summary>
    /// The bodies player <paramref name="self"/>'s input of tick <paramref name="tick"/> collides with: the living players of
    /// <paramref name="seen"/> (the world at tick <paramref name="seenTick"/>, the snapshot its client held) other than itself, each moved on
    /// horizontally by its velocity for min(<paramref name="tick"/> − <paramref name="seenTick"/>, <see cref="MaxExtrapolationTicks"/>) ticks, at
    /// most <see cref="MaxExtrapolation"/> metres. Null when the config turns player collision off or nothing is seen.
    /// </summary>
    public static BodyView? See(IEnumerable<PlayerState>? seen, int self, uint seenTick, uint tick, SimConfig c)
    {
        if (!c.PlayerBodies || seen is null) return null;
        int ahead = tick > seenTick ? (int)Math.Min(tick - seenTick, (uint)MaxExtrapolationTicks) : 0;
        float t = ahead * c.TickSeconds;
        var bodies = new List<PlayerBody>();
        Vector3? me = null;
        foreach (var p in seen)
        {
            if (!p.Alive || !Finite(p.Position)) continue;
            if (p.PlayerId == self) { me = p.Position; continue; }
            var move = Finite(p.Velocity) ? new Vector2(p.Velocity.X, p.Velocity.Y) * t : Vector2.Zero;
            float length = move.Length();
            if (length > MaxExtrapolation) move *= MaxExtrapolation / length;
            bodies.Add(new PlayerBody(p.PlayerId, p.Position + new Vector3(move.X, move.Y, 0f), p.Position));
        }
        return BodyView.Of(bodies, me);
    }

    /// <summary>How another body lies against the stepping one, by height.</summary>
    public enum Relation { None, Side, Above, Below }

    /// <summary>
    /// Side: the capsules' heights overlap; Above: the stepping body's feet are within <see cref="OnTopBand"/> below the other's top and at
    /// most <see cref="HeadReach"/> above it (on its head); Below: the other is on the stepping body's head (its own step resolves it); None: apart.
    /// </summary>
    public static Relation Relate(float selfFeet, float otherFeet, float height)
    {
        float top = otherFeet + height;
        if (selfFeet >= top - OnTopBand) return selfFeet <= top + HeadReach ? Relation.Above : Relation.None;
        if (otherFeet >= selfFeet + height - OnTopBand) return otherFeet <= selfFeet + height + HeadReach ? Relation.Below : Relation.None;
        return Relation.Side;
    }

    static readonly Vector2[] TieDirections =
    [
        new(1f, 0f), new(0.70710677f, 0.70710677f), new(0f, 1f), new(-0.70710677f, 0.70710677f),
        new(-1f, 0f), new(-0.70710677f, -0.70710677f), new(0f, -1f), new(0.70710677f, -0.70710677f),
    ];

    /// <summary>The way <paramref name="self"/> leaves <paramref name="other"/> when their axes coincide: one of eight fixed directions from the pair (no trigonometry: the same bits everywhere), opposite for the other.</summary>
    public static Vector2 TieDirection(int self, int other)
    {
        uint lo = (uint)Math.Min(self, other), hi = (uint)Math.Max(self, other);
        var d = TieDirections[(int)((lo * 2654435761u ^ hi * 40503u) >> 29)];
        return self <= other ? d : -d;
    }

    /// <summary>The move a step hands the controller: <see cref="Motor"/> (velocity, the push included), what the body's velocity becomes (<see cref="Keep"/>) and the push's share (<see cref="Push"/>).</summary>
    internal readonly record struct Plan(Vector2 Motor, Vector2 Keep, Vector2 Push);

    [ThreadStatic] static List<Vector2>? t_normals;

    /// <summary>
    /// The horizontal move of a step of <paramref name="s"/> wishing <paramref name="horizontal"/> (m/s) against <paramref name="bodies"/>: the
    /// push out of overlaps, the inward part of the move removed, then the sweep against the circles with slides (see <see cref="PlayerCollision"/>).
    /// </summary>
    internal static Plan Resolve(PlayerState s, Vector2 horizontal, SimConfig c, BodyView bodies)
    {
        float dt = c.TickSeconds;
        if (!(dt > 0f)) return new Plan(horizontal, horizontal, Vector2.Zero);
        float r = MathF.Max(c.CapsuleRadius, 0.05f), reach = 2f * r, height = MathF.Max(c.CapsuleHeight, 2f * r);
        var normals = t_normals ??= new List<Vector2>(8);
        normals.Clear();
        var p = new Vector2(s.Position.X, s.Position.Y);
        var push = Vector2.Zero;
        var list = bodies.Bodies;
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            var relation = Relate(s.Position.Z, b.Feet.Z, height);
            if (relation is Relation.None or Relation.Below) continue;
            // The push: this player's share of the overlap the snapshot showed (half beside, all of it on a head), less its progress since.
            bool onHead = relation == Relation.Above;
            push += Push(s, b, onHead, bodies.Self, reach, (onHead ? SlideOffSpeed : SeparationSpeed) * dt);
            if (onHead) continue;
            var offset = p - Where(b, p, reach);
            float distance = offset.Length();
            if (reach - distance < -Tolerance) continue;   // touching counts
            normals.Add(distance > 1e-4f ? offset / distance : TieDirection(s.PlayerId, b.Id));   // overlapping now: no move further into it
        }
        var rem = horizontal * dt;
        rem = WithoutInward(rem, normals);
        var at = p + push;
        var moved = Vector2.Zero;
        for (int slide = 0; slide < MaxSlides && rem.LengthSquared() > 1e-12f; slide++)
        {
            float best = 1f;
            Vector2 bestCentre = default;
            bool hit = false;
            float a = rem.LengthSquared();
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (Relate(s.Position.Z, b.Feet.Z, height) != Relation.Side) continue;
                var centre = Where(b, p, reach);
                var off = at - centre;
                float c0 = off.LengthSquared() - reach * reach;
                float half = Vector2.Dot(off, rem);
                if (half >= 0f) continue;   // moving away or along it
                float t = 0f;   // touching or inside, moving in: met at once
                if (c0 > 0f)
                {
                    float disc = half * half - a * c0;
                    if (disc < 0f) continue;
                    t = MathF.Max(0f, (-half - MathF.Sqrt(disc)) / a);
                }
                if (t >= best) continue;
                best = t; bestCentre = centre; hit = true;
            }
            if (!hit) { at += rem; moved += rem; break; }
            var step = rem * best;
            at += step; moved += step;
            var normal = at - bestCentre;
            normal = normal.LengthSquared() > 1e-12f ? Vector2.Normalize(normal) : TieDirection(s.PlayerId, 0);
            normals.Add(normal);
            rem = WithoutInward(rem * (1f - best), normals);
        }
        return new Plan((push + moved) / dt, moved / dt, push / dt);
    }

    /// <summary>
    /// One body's push on the stepping player <paramref name="s"/>: with its own feet at the snapshot (<paramref name="self"/>), the overlap between them
    /// there, along their axes' line: its share (half; all of it on a head), less how far it has moved along that line since, between 0 and
    /// <paramref name="most"/>. Without (not in the snapshot): its share of the overlap with the body as carried on to now, at most <paramref name="most"/>.
    /// </summary>
    static Vector2 Push(PlayerState s, PlayerBody b, bool onHead, Vector3? self, float reach, float most)
    {
        float share = onHead ? 1f : 0.5f;
        var p = new Vector2(s.Position.X, s.Position.Y);
        var from = self is { } me ? new Vector2(me.X, me.Y) : p;
        var other = self is null ? Where(b, p, reach) : new Vector2(b.Seen.X, b.Seen.Y);
        var offset = from - other;
        float distance = offset.Length();
        float depth = reach - distance;
        if (depth <= Tolerance) return Vector2.Zero;
        var n = distance > 1e-4f ? offset / distance : TieDirection(s.PlayerId, b.Id);
        float progress = Vector2.Dot(p - from, n);
        return n * Math.Clamp(depth * share - progress, 0f, most);
    }

    /// <summary>
    /// Where body <paramref name=b/> stands for a player whose axis is at <paramref name=self/> (horizontal): carried on from the snapshot
    /// (<see cref=PlayerBody.Seen/>) toward <see cref=PlayerBody.Feet/>, but never into or through that player: the carry stops where the two
    /// capsules would touch (it could not have walked through the player it meets), and a body that already overlapped it in the snapshot and was
    /// coming closer is not carried at all. Without this a runner who stopped against the player is carried on past it, and the two walk through each other.
    /// </summary>
    static Vector2 Where(PlayerBody b, Vector2 self, float reach)
    {
        var from = new Vector2(b.Seen.X, b.Seen.Y);
        var carry = new Vector2(b.Feet.X, b.Feet.Y) - from;
        float a = carry.LengthSquared();
        if (a < 1e-12f) return from + carry;
        var off = from - self;
        float half = Vector2.Dot(off, carry);
        if (half >= 0f) return from + carry;   // carried away from the player
        float c0 = off.LengthSquared() - reach * reach;
        if (c0 <= 0f) return from;             // overlapping it already and coming closer: not carried
        float disc = half * half - a * c0;
        if (disc < 0f) return from + carry;    // passes by
        float t = (-half - MathF.Sqrt(disc)) / a;
        return t < 1f ? from + carry * MathF.Max(0f, t) : from + carry;
    }

    /// <summary>The move without its parts going into each normal (two passes, for a wedge between two bodies).</summary>
    static Vector2 WithoutInward(Vector2 move, List<Vector2> normals)
    {
        for (int pass = 0; pass < 2; pass++)
            foreach (var n in normals)
            {
                float into = Vector2.Dot(move, n);
                if (into < 0f) move -= n * into;
            }
        return move;
    }

    /// <summary>
    /// After the controller moved <paramref name="before"/> to <paramref name="moved"/> with <see cref="Plan.Motor"/>: the velocity the body keeps
    /// (the plan's, or, when a wall cut the move, the motor's without the push and no faster than the plan's), and a body that came down onto a
    /// head held at its top (not grounded, not falling) while it still overlaps it.
    /// </summary>
    /// <param name="level">The step ran against a level (else the flat ground at z = 0, where standing above 0 is standing on a head).</param>
    internal static PlayerState Finish(PlayerState before, PlayerState moved, Plan plan, SimConfig c, BodyView bodies, bool level)
    {
        t_lastPush = plan.Push * c.TickSeconds;
        var v = moved.Velocity;
        var flat = new Vector2(v.X, v.Y);
        Vector2 keep;
        if (flat == plan.Motor) keep = plan.Keep;
        else
        {
            keep = flat - plan.Push;
            float limit = plan.Keep.Length();
            if (keep.Length() > limit) keep = limit > 0f ? Vector2.Normalize(keep) * limit : Vector2.Zero;
        }
        var result = moved with { Velocity = new Vector3(keep.X, keep.Y, v.Z) };
        float r = MathF.Max(c.CapsuleRadius, 0.05f), reach = 2f * r, height = MathF.Max(c.CapsuleHeight, 2f * r);
        float hold = float.NegativeInfinity;
        var at = new Vector2(result.Position.X, result.Position.Y);
        foreach (var b in bodies.Bodies)
        {
            float top = b.Feet.Z + height;
            if (Vector2.Distance(at, Where(b, new Vector2(before.Position.X, before.Position.Y), reach)) >= reach - Tolerance) continue;
            if (before.Position.Z >= top - 0.01f && result.Position.Z < top) hold = MathF.Max(hold, top);   // coming down onto it from its top
        }
        if (float.IsFinite(hold))
        {
            // A ground pound's or slash's dive lands on the head in the tick it touches it (its impact then, as the original's landing on a
            // character would be immediate; ours); anything else is held there, not grounded and not falling, until it has slid off.
            bool dive = before.Jump == JumpPhase.Dive;
            result = result with { Position = new Vector3(result.Position.X, result.Position.Y, hold), Velocity = new Vector3(keep.X, keep.Y, dive ? 0f : MathF.Max(0f, v.Z)), Grounded = dive };
        }
        else if (!level && result.Grounded && result.Position.Z > 0f)
            result = result with { Grounded = false };   // after a dive's landing on a head: the flat ground has no floor up there (a level's controller finds none by itself)
        return result;
    }

    /// <summary>
    /// The closest two living players' capsules come in <paramref name="players"/>, for probes and tests: the horizontal distance between the axes of
    /// the closest pair whose heights overlap (as <see cref="Relate"/>'s Side), minus the two radii (negative: an overlap that deep), or +∞.
    /// </summary>
    public static float Clearance(IEnumerable<PlayerState> players, SimConfig c) => Clearance(players.Where(p => p.Alive).Select(p => p.Position).ToList(), c);

    /// <summary>As <see cref="Clearance(IEnumerable{PlayerState}, SimConfig)"/>, for feet positions.</summary>
    public static float Clearance(IReadOnlyList<Vector3> feet, SimConfig c)
    {
        float r = MathF.Max(c.CapsuleRadius, 0.05f), height = MathF.Max(c.CapsuleHeight, 2f * r);
        float best = float.PositiveInfinity;
        for (int i = 0; i < feet.Count; i++)
            for (int j = i + 1; j < feet.Count; j++)
            {
                if (MathF.Abs(feet[i].Z - feet[j].Z) >= height - OnTopBand) continue;
                float d = Vector2.Distance(new Vector2(feet[i].X, feet[i].Y), new Vector2(feet[j].X, feet[j].Y)) - 2f * r;
                if (d < best) best = d;
            }
        return best;
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
