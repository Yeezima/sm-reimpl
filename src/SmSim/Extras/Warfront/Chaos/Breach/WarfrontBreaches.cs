using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Chaos;

/// <summary>The battle the breaches are placed for: its kind, its node (lane battles, the run's 1 to 3) and, for the fortress, the plaza no breach stands in (radius 0: none).</summary>
public readonly record struct WarfrontBreachSite(WarfrontBreachBattle Battle, int Node, Vector3 PlazaCentre = default, float PlazaRadius = 0f);

/// <summary>
/// The warp breaches of one Chaos battle (Chaos Warfront C4; design "Warp breaches"; every number <see cref="WarfrontBreachRules"/>): placed once at the battle's start from its seed, each opens,
/// sends a Daemon squad on a cadence while its Daemons and all the breaches' stay under their caps (refilled as they die), and closes for good on one qualifying explosive of a human within
/// <see cref="WarfrontBreachRules.CloseRadius"/> of its centre. A closed breach sends no more; its Daemons fight on. Deterministic: the battle's own rolls and ticks only.
/// </summary>
public sealed class WarfrontBreaches
{
    readonly List<WarfrontBreach> _breaches = new();
    readonly List<WarfrontBreachClosure> _closures = new();
    readonly int _squadTicks;

    WarfrontBreaches(int rate) => _squadTicks = WarfrontBreachRules.Ticks(WarfrontBreachRules.SquadSeconds, rate);

    /// <summary>The breaches, in id order.</summary>
    public IReadOnlyList<WarfrontBreach> All => _breaches;
    /// <summary>How many the battle wanted (<see cref="WarfrontBreachRules.Count"/>); more than <see cref="All"/> holds when fewer places fitted.</summary>
    public int Wanted { get; private set; }
    /// <summary>Raised on every change the wire shows (a state, a Daemon count): the server sends the list when it moves.</summary>
    public int StateVersion { get; private set; }
    /// <summary>Every closure by a human so far, in order (a silent close is not one).</summary>
    public IReadOnlyList<WarfrontBreachClosure> Closures => _closures;
    /// <summary>The breach Daemons alive now, over all the breaches.</summary>
    public int AliveDaemons { get; private set; }
    /// <summary>Whether any breach still stands (the projectile hook returns at once when none does).</summary>
    public bool AnyStanding { get; private set; }
    /// <summary>Where the step's lines go (the server's log; null: nowhere).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// The breaches of a battle (design: on the enemy sources, on the walking graph, at least <see cref="WarfrontBreachRules.MinFromLanding"/> from the landing zone and
    /// <see cref="WarfrontBreachRules.MinFromPoints"/> from every capture point; <see cref="WarfrontBreachRules.MinApart"/> apart; none in the fortress's plaza). The sources are taken in an order the battle's
    /// <paramref name="roll"/> sets; each gives at most one breach, on the source itself or, when that spot fails, the first spot on the rings round it that passes. Fewer than wanted: what fits.
    /// </summary>
    public static WarfrontBreaches Place(TfwLayout layout, NavGraph? nav, Func<uint, float> roll, WarfrontBreachSite site, int rate, Action<string>? log = null)
    {
        var b = new WarfrontBreaches(rate) { Log = log };
        int want = Math.Min(WarfrontBreachRules.MaxBreaches, WarfrontBreachRules.Count(site.Battle, site.Node));
        b.Wanted = want;
        if (want == 0) return b;
        var sources = layout.Sources;
        var order = Enumerable.Range(0, sources.Length).OrderBy(k => roll(WarfrontBreachRules.PlacementSalt + (uint)k)).ThenBy(k => k).ToArray();
        var points = new List<Vector3>();
        if (site.Battle != WarfrontBreachBattle.Fortress)   // the fortress's "positions" are its waypoints, never a capture point
        {
            points.AddRange(layout.Positions);
            if (site.Battle == WarfrontBreachBattle.Lane) points.Add(layout.Hq);   // Warfront's D, a capture point in a node battle
        }
        int openAt = WarfrontBreachRules.Ticks(WarfrontBreachRules.OpenDelaySeconds, rate);
        int firstAt = openAt + WarfrontBreachRules.Ticks(WarfrontBreachRules.BuildUpSeconds, rate);
        foreach (int k in order)
        {
            if (b._breaches.Count >= want) break;
            float turn = roll(WarfrontBreachRules.PlacementSalt + 0x100u + (uint)k) * MathF.Tau;
            for (int ring = 0; ring <= WarfrontBreachRules.Rings; ring++)
            {
                bool placed = false;
                int spots = ring == 0 ? 1 : WarfrontBreachRules.RingSpots;
                for (int s = 0; s < spots && !placed; s++)
                {
                    float angle = turn + s * MathF.Tau / spots, r = ring * WarfrontBreachRules.RingStep;
                    var spot = sources[k] + new Vector3(MathF.Cos(angle) * r, MathF.Sin(angle) * r, 0f);
                    if (nav is { NodeCount: > 0 })
                    {
                        int node = nav.Nearest(spot, 2f, 4f);
                        if (node < 0) continue;   // in a wall or off the graph
                        spot = nav.Position(node);
                    }
                    if (!Fits(spot, layout.LandingZone, points, site, b._breaches)) continue;
                    b._breaches.Add(new WarfrontBreach((byte)(b._breaches.Count + 1), spot, openAt, firstAt));
                    placed = true;
                }
                if (placed) break;
            }
        }
        b.AnyStanding = b._breaches.Count > 0;
        string where = string.Join("; ", b._breaches.Select(x => $"#{x.Id} at ({x.Position.X:0.0}, {x.Position.Y:0.0}, {x.Position.Z:0.0})"));
        log?.Invoke($"chaos breaches: {site.Battle} node {site.Node}: {b._breaches.Count} of {want} placed{(b._breaches.Count < want ? " (no more places fit)" : "")}{(where.Length > 0 ? ": " + where : "")}");
        return b;
    }

    /// <summary>Breaches standing exactly on <paramref name="feet"/>, with the usual clocks, no placement rule applied (the tests'; a handmade map's own spots could use it).</summary>
    public static WarfrontBreaches At(int rate, params Vector3[] feet)
    {
        var b = new WarfrontBreaches(rate);
        int openAt = WarfrontBreachRules.Ticks(WarfrontBreachRules.OpenDelaySeconds, rate);
        int firstAt = openAt + WarfrontBreachRules.Ticks(WarfrontBreachRules.BuildUpSeconds, rate);
        foreach (var f in feet.Take(WarfrontBreachRules.MaxBreaches)) b._breaches.Add(new WarfrontBreach((byte)(b._breaches.Count + 1), f, openAt, firstAt));
        b.Wanted = b._breaches.Count;
        b.AnyStanding = b._breaches.Count > 0;
        return b;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    static bool Fits(Vector3 spot, Vector3 landing, List<Vector3> points, in WarfrontBreachSite site, List<WarfrontBreach> placed)
    {
        if (Flat(spot, landing) < WarfrontBreachRules.MinFromLanding) return false;
        foreach (var p in points) if (Flat(spot, p) < WarfrontBreachRules.MinFromPoints) return false;
        if (site.PlazaRadius > 0f && Flat(spot, site.PlazaCentre) <= site.PlazaRadius) return false;
        foreach (var other in placed) if (Flat(spot, other.Position) < WarfrontBreachRules.MinApart) return false;
        return true;
    }

    /// <summary>
    /// One battle tick (<paramref name="tick"/>, the battle's own): the dead leave their breach, a breach opens at its time, and an open breach whose squad is due sends
    /// min(squad, its cap's room, all the breaches' cap's room, the battle's room) Daemons when that is at least one (else the squad waits for room: a refill as Daemons die).
    /// </summary>
    public void Step(int tick, IWarfrontBreachField field)
    {
        int alive = 0;
        foreach (var b in _breaches)
        {
            int before = b.Daemons.Count;
            b.Daemons.RemoveAll(id => !field.IsDaemon(id));
            if (b.Daemons.Count != before) StateVersion++;
            alive += b.Daemons.Count;
        }
        foreach (var b in _breaches)
        {
            if (b.State == WarfrontBreachState.Closed) continue;
            if (b.State == WarfrontBreachState.Opening)
            {
                if (tick < b.OpenAt) continue;
                b.State = WarfrontBreachState.Open;
                StateVersion++;
                Log?.Invoke($"chaos breach #{b.Id} open");
            }
            if (tick < b.NextSquadAt) continue;
            int room = Math.Min(Math.Min(WarfrontBreachRules.SquadSize, WarfrontBreachRules.BreachAliveCap - b.Daemons.Count), Math.Min(WarfrontBreachRules.AllAliveCap - alive, field.Room));
            if (room < 1) continue;   // due, waiting for room
            int came = 0;
            for (int k = 0; k < room; k++)
            {
                float angle = (b.Spawned + k) * 2.399963f;   // the golden angle: a squad spreads round the ring
                var spot = b.Position + new Vector3(MathF.Cos(angle) * WarfrontBreachRules.RingRadius, MathF.Sin(angle) * WarfrontBreachRules.RingRadius, 0f);
                ushort id = field.Spawn(spot, b.Position);
                if (id == 0) break;   // the pool is full: what came came, the rest waits
                b.Daemons.Add(id);
                came++;
            }
            if (came == 0) continue;
            b.Spawned += came;
            alive += came;
            b.NextSquadAt = tick + _squadTicks;
            StateVersion++;
            string cap = b.Daemons.Count >= WarfrontBreachRules.BreachAliveCap ? " (at its cap)" : alive >= WarfrontBreachRules.AllAliveCap ? " (all breaches at their cap)" : "";
            Log?.Invoke($"chaos breach #{b.Id}: {came} Daemon(s) out, {b.Daemons.Count} alive{cap}");
        }
        AliveDaemons = alive;
    }

    /// <summary>
    /// A qualifying detonation of human <paramref name="closer"/> at <paramref name="at"/> (the caller has classified it): the nearest standing breach whose centre is within
    /// <see cref="WarfrontBreachRules.CloseRadius"/> horizontally and <see cref="WarfrontBreachRules.CloseVertical"/> vertically closes. One detonation closes at most one. Its id, or 0.
    /// </summary>
    public byte TryClose(Vector3 at, int closer, uint worldTick)
    {
        WarfrontBreach? best = null;
        float bestDistance = float.MaxValue;
        foreach (var b in _breaches)
        {
            if (!b.Standing) continue;
            var c = b.Centre;
            float flat = Flat(at, c);
            if (flat > WarfrontBreachRules.CloseRadius || MathF.Abs(at.Z - c.Z) > WarfrontBreachRules.CloseVertical) continue;
            float d = Vector3.Distance(at, c);
            if (d < bestDistance) { bestDistance = d; best = b; }
        }
        if (best is null) return 0;
        Close(best, closer, worldTick);
        _closures.Add(new WarfrontBreachClosure(worldTick, best.Id, closer));
        Log?.Invoke($"chaos breach #{best.Id} CLOSED by player {closer} ({bestDistance:0.00} m from its centre); {best.Daemons.Count} of its Daemons fight on");
        return best.Id;
    }

    /// <summary>Every standing breach closes (C5's duel). <paramref name="silent"/>: no closure event (no sound, no banner); the list still shows them closed.</summary>
    public void CloseAll(bool silent, uint worldTick)
    {
        foreach (var b in _breaches)
        {
            if (!b.Standing) continue;
            Close(b, 0, worldTick);
            if (!silent) _closures.Add(new WarfrontBreachClosure(worldTick, b.Id, 0));
        }
        Log?.Invoke($"chaos breaches: all closed{(silent ? " (silently)" : "")}");
    }

    void Close(WarfrontBreach b, int closer, uint worldTick)
    {
        b.State = WarfrontBreachState.Closed;
        b.ClosedBy = closer;
        b.ClosedAt = worldTick;
        StateVersion++;
        AnyStanding = _breaches.Exists(x => x.Standing);
    }

    /// <summary>
    /// Where along the segment from <paramref name="from"/> (direction <paramref name="direction"/>, unit, length <paramref name="max"/>) a projectile first meets a standing breach's sphere
    /// (<see cref="WarfrontBreachRules.SphereRadius"/> round its centre): 0 when it starts inside one; +infinity when it meets none within the segment.
    /// </summary>
    public float SphereEntry(Vector3 from, Vector3 direction, float max)
    {
        float best = float.PositiveInfinity;
        const float r2 = WarfrontBreachRules.SphereRadius * WarfrontBreachRules.SphereRadius;
        foreach (var b in _breaches)
        {
            if (!b.Standing) continue;
            var m = from - b.Centre;
            float c = Vector3.Dot(m, m) - r2;
            if (c <= 0f) return 0f;
            float h = Vector3.Dot(m, direction);
            if (h > 0f) continue;   // going away
            float disc = h * h - c;
            if (disc < 0f) continue;
            float t = -h - MathF.Sqrt(disc);
            if (t >= 0f && t <= max && t < best) best = t;
        }
        return best;
    }
}
