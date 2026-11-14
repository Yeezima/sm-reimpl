using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 13's door check (owner, 2026-11-14: "no weird things like a wall in front of a door"; docs/musou-ashfall-graybox.md "Draft 13 heights"): every door of a placed kit model (a through-opening of the window pass whose
/// sill stands within <see cref="MaxSill"/> of the model's base and that is at least <see cref="MinWidth"/> wide and <see cref="MinHeight"/> tall in the drawn size) has <see cref="Depth"/> m of open ground on both sides of
/// its wall: no collision box of any other item stands in the strip across the door's width, from a step over its sill to head height. Ours (placeholder demonstration).
/// </summary>
public static class AshfallDoorClearance
{
    /// <summary>The depth of open floor kept on each side of a door (m; ours: two strides at run speed, as a stair's foot zone).</summary>
    public const float Depth = 3f;

    /// <summary>What makes an opening a door: its sill at most this far over the model's base (m), its drawn width and height at least these (m). Ours: a Marine's body is 0.9 m wide and 1.9 m tall.</summary>
    public const float MaxSill = 2f, MinWidth = 1.2f, MinHeight = 2f;

    /// <summary>One door and what stands in front of it: <see cref="Blocker"/> is the other item found in the way (-1: none), <see cref="Front"/> the side (1 beyond the opening's end, -1 before its start).</summary>
    public sealed record Door(int Item, string Asset, Vector3 Centre, Vector2 Dir, float Width, float Height, float Sill, int Blocker, int Front);

    /// <summary>Every door of the level's placed models (with <see cref="Door.Blocker"/> -1 for the open ones).</summary>
    public static List<Door> Find(AshfallLevel level, AshfallDressing dressing)
    {
        var owner = new int[level.Parts.Count];
        Array.Fill(owner, -1);
        for (int i = 0; i < dressing.ItemParts.Count; i++) { var (f, n) = dressing.ItemParts[i]; for (int q = f; q < f + n; q++) owner[q] = i; }
        var boxes = new List<(int Item, AshfallBox Box)>();
        for (int q = 0; q < level.Parts.Count; q++)
        {
            var part = level.Parts[q];
            if (!part.Solid || owner[q] < 0 || part.Kind is AshfallKind.Stair) continue;
            if (AshfallBox.Of(part) is { } b) boxes.Add((owner[q], b));
        }
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < boxes.Count; k++)
        {
            var b = boxes[k].Box;
            float r = (b.Length + b.Depth) / 2f + 1f;
            for (int gx = (int)MathF.Floor((b.Cx - r) / 16f); gx <= (int)MathF.Floor((b.Cx + r) / 16f); gx++)
                for (int gy = (int)MathF.Floor((b.Cy - r) / 16f); gy <= (int)MathF.Floor((b.Cy + r) / 16f); gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = [];
                    l.Add(k);
                }
        }
        int BlockerAt(Vector2 p, float z0, float z1, int self)
        {
            if (!grid.TryGetValue(((int)MathF.Floor(p.X / 16f), (int)MathF.Floor(p.Y / 16f)), out var l)) return -1;
            foreach (int k in l)
            {
                var (item, b) = boxes[k];
                if (item != self && b.Z0 < z1 && b.Z1 > z0 && AshfallAtmosphere.Inside(b, p, 0.05f)) return item;
            }
            return -1;
        }
        var doors = new List<Door>();
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            var it = dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || dressing.ItemParts[i].Count == 0) continue;
            foreach (var o in AshfallKitCollision.Openings(it.Asset))
            {
                var lo = it.ToWorld(o.Centre - new Vector3(0f, o.Height / 2f, 0f)); var hi = it.ToWorld(o.Centre + new Vector3(0f, o.Height / 2f, 0f));
                var side = o.Axis == 0 ? new Vector3(0f, 0f, o.Width / 2f) : new Vector3(o.Width / 2f, 0f, 0f);
                var a = it.ToWorld(o.Centre + side); var b = it.ToWorld(o.Centre - side);
                float h = hi.Z - lo.Z, w = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)), sill = lo.Z - it.Position.Z;
                if (sill > MaxSill || sill < -1f || h < MinHeight || w < MinWidth) continue;
                var s0 = it.ToWorld(o.Start); var s1 = it.ToWorld(o.End);
                var d = new Vector2(s1.X - s0.X, s1.Y - s0.Y);
                if (d.LengthSquared() < 1e-6f) continue;
                d = Vector2.Normalize(d);
                var across = new Vector2(-d.Y, d.X);
                int blocker = -1, front = 0;
                foreach (int sd in new[] { 1, -1 })
                {
                    var origin = sd > 0 ? new Vector2(s1.X, s1.Y) : new Vector2(s0.X, s0.Y);
                    for (float at = 0.1f; at <= Depth && blocker < 0; at += 0.25f)
                        for (float t = -w / 2f + 0.2f; t <= w / 2f - 0.2f + 1e-3f; t += 0.4f)
                        {
                            blocker = BlockerAt(origin + d * (sd * at) + across * t, lo.Z + AshfallStairs.Step + 0.05f, lo.Z + 2f, i);
                            if (blocker >= 0) { front = sd; break; }
                        }
                    if (blocker >= 0) break;
                }
                doors.Add(new Door(i, it.Asset, new Vector3((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, lo.Z), d, w, h, sill, blocker, front));
            }
        }
        return doors;
    }
}

/// <summary>
/// Draft 13's high ground (owner, 2026-11-14: "i want devastator and tactical marines to have strong high ground positions to play from, and the ability for assault to leverage its ability to jump to gain an advantage on the
/// high ground"; docs/musou-ashfall-graybox.md "Draft 13 heights"). A <b>deck</b> is a flat floor at least <see cref="MinRise"/> m over the ground that a walkable stair's top lands on and that is at least
/// <see cref="MinArea"/> m2 of connected open floor (a roof, an upper floor, a terrace). A stair whose top lands on a smaller floor and continues in no further flight is a <b>dead end</b>. A deck is reachable
/// <b>on foot</b> when one of the flights that land on it passes the stair-access check (<see cref="AshfallStairAccess"/>: foot reached from the streets, flight walked up and down), and by an <b>Assault jump</b>
/// when a jump of the pack's data (<see cref="AssaultJump"/>) launched from open ground or from another deck lands on it over everything between. Ours (placeholder demonstration).
/// </summary>
public static class AshfallHighGround
{
    /// <summary>The lowest floor that counts as high ground (m over the ground; ours: over a Marine's reach and a rubble heap, the kit's firing platforms stand 5.6 m).</summary>
    public const float MinRise = 4f;

    /// <summary>The least connected floor a stair's top may lead to (m2; ours: four by four metres, a fire team's room).</summary>
    public const float MinArea = 16f;

    /// <summary>The least floor a flight's top may lead to without a further flight (m2; ours: a platform to stand and shoot from, over the 0.9 m body with room to turn: the kit's elevated perch AC-008 has 5).</summary>
    public const float MinLanding = 5f;

    /// <summary>The cell of the floor flood (m).</summary>
    public const float Cell = 0.5f;

    const int MaxCells = 20000;

    /// <summary>One deck: its floor height, area, bounds and centre, the flights that land on it and the ones that start from it, and what was measured.</summary>
    public sealed class Deck
    {
        public int Id;
        public float Z, Height, Area;
        public Vector2 Centre;
        public Vector4 Bounds;
        public List<int> Arrivals = [];      // indices into the level's stairs (AshfallStairs.All order)
        public List<int> Departures = [];
        public List<int> Items = [];         // the items whose flights land here
        public bool OnFoot;                  // one of the arriving flights passes the stair-access check
        public string? Source;               // the kit model of the first arriving flight
        public HashSet<(int, int)> Cells = [];
        public string Sees = "";             // the points whose centre this deck sees (A, B, C)
        public bool Authored;                // every arriving flight is an authored piece (the stronghold)
        public Vector2 JumpFrom;             // the best ground launch point (valid when JumpFromGround)
        public bool JumpFromGround;
        public string JumpTier = "";
        public List<int> JumpFromDecks = []; // decks a jump from there lands here
    }

    /// <summary>What the analysis found.</summary>
    public sealed class Result
    {
        public List<Deck> Decks = [];
        public List<(int Stair, string Why)> DeadEnds = [];
        public int Stairs;
    }

    /// <summary>Analyses <paramref name="level"/>: the decks, the dead-end flights and the decks' jump reach (when <paramref name="grid"/> is given).</summary>
    public static Result Analyse(AshfallLevel level, AshfallDressing dressing, IReadOnlyList<AshfallStairAccess.Stair> access, AshfallGrid? grid, IReadOnlyList<ControlPoint>? points = null)
    {
        var res = new Result { Stairs = access.Count };
        var solids = AshfallStairs.Solids.Of(level);
        var ground = AshfallMap.GroundOf(level.Layout);
        const float step = AshfallStairs.Step;
        var cellOf = new Dictionary<(int, int), int>();      // cell -> deck id
        var comps = new List<Deck>();
        bool Floor(Vector2 p, float z) => MathF.Abs(solids.FloorUnder(p, z + step) - z) <= step + 0.05f && !solids.Blocked(p, z + step + 0.05f, z + AshfallStairs.Headroom);
        (int, int) Key(Vector2 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
        Vector2 Centre((int X, int Y) k) => new((k.X + 0.5f) * Cell, (k.Y + 0.5f) * Cell);

        // the decks: a flood of the floor from each passing flight's landing
        for (int i = 0; i < access.Count; i++)
        {
            var r = access[i].Ramp;
            if (r.HighZ - ground(r.High.X, r.High.Y) < step + 0.05f) continue;   // a step's ramp: its top is the ground
            var seed = r.High + r.Dir * 0.5f;
            var sk = Key(seed);
            if (cellOf.TryGetValue(sk, out int existing)) { comps[existing].Arrivals.Add(i); continue; }
            if (!Floor(Centre(sk), r.HighZ)) { if (!Floor(seed, r.HighZ)) continue; }
            var deck = new Deck { Id = comps.Count, Z = r.HighZ };
            var stack = new Stack<(int, int)>();
            cellOf[sk] = deck.Id; deck.Cells.Add(sk); stack.Push(sk);
            while (stack.Count > 0 && deck.Cells.Count < MaxCells)
            {
                var k = stack.Pop();
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (k.Item1 + dx, k.Item2 + dy);
                    if (cellOf.ContainsKey(n) || !Floor(Centre(n), r.HighZ)) continue;
                    cellOf[n] = deck.Id; deck.Cells.Add(n); stack.Push(n);
                }
            }
            deck.Arrivals.Add(i);
            comps.Add(deck);
        }
        for (int i = 0; i < access.Count; i++)
        {
            var r = access[i].Ramp;
            var foot = r.Low - r.Dir * 0.75f;
            if (cellOf.TryGetValue(Key(foot), out int d) && MathF.Abs(comps[d].Z - r.LowZ) <= step + 0.05f) comps[d].Departures.Add(i);
        }
        foreach (var d in comps)
        {
            d.Area = d.Cells.Count * Cell * Cell;
            var cs = d.Cells.Select(Centre).ToList();
            d.Bounds = new Vector4(cs.Min(c => c.X), cs.Min(c => c.Y), cs.Max(c => c.X), cs.Max(c => c.Y));
            d.Centre = new Vector2(cs.Average(c => c.X), cs.Average(c => c.Y));
            d.Height = d.Z - ground(d.Centre.X, d.Centre.Y);
            d.Items = [.. d.Arrivals.Select(a => access[a].Ramp.Item).Distinct()];
            d.OnFoot = d.Arrivals.Any(a => access[a].Problem is null);
            d.Authored = d.Arrivals.All(a => access[a].Authored);
        }
        // dead ends: a flight whose top lands on a floor smaller than MinArea that no further flight starts from
        for (int i = 0; i < access.Count; i++)
        {
            var r = access[i].Ramp;
            if (r.HighZ - ground(r.High.X, r.High.Y) < step + 0.05f) continue;   // a ramp of the plinth, the entry ramp: its top is the floor of the building
            var seed = r.High + r.Dir * 0.5f;
            if (!cellOf.TryGetValue(Key(seed), out int d))
            {
                // the top landed on no flooded floor (a landing slab narrower than a cell, a wall): a dead end unless a flight starts within 2 m of its landing
                bool next = Enumerable.Range(0, access.Count).Any(j => j != i && MathF.Abs(access[j].Ramp.LowZ - r.HighZ) <= step + 0.05f && Vector2.Distance(access[j].Ramp.Low, r.High) <= 3f);
                if (!next) res.DeadEnds.Add((i, "its top lands on no open floor"));
                continue;
            }
            var deck = comps[d];
            if (deck.Area < MinLanding && deck.Departures.Count == 0) res.DeadEnds.Add((i, $"its top lands on {deck.Area:0} m2 of floor and no flight goes on from there"));
        }
        foreach (var d in comps) if (d.Height >= MinRise && d.Area >= MinArea) res.Decks.Add(d);
        for (int i = 0; i < res.Decks.Count; i++) res.Decks[i].Id = i;
        foreach (var d in res.Decks) d.Source = d.Items.Count > 0 && d.Items[0] >= 0 ? dressing.Items[d.Items[0]].Asset : null;
        if (grid is not null) { Sight(res, grid, points); Jumps(res, grid, ground); }
        return res;
    }

    static void Sight(Result res, AshfallGrid grid, IReadOnlyList<ControlPoint>? points)
    {
        if (points is null) return;
        foreach (var d in res.Decks)
        {
            var eye = new Vector3(d.Centre.X, d.Centre.Y, d.Z + 1.6f);
            var seen = "";
            foreach (var p in points)
            {
                var to = new Vector3(p.Center.X, p.Center.Y, p.Center.Z + 1.6f);
                var v = to - eye; float len = new Vector2(v.X, v.Y).Length();
                if (len > 250f) continue;
                int n = Math.Max(1, (int)(len / (grid.Cell * 0.5f)));
                bool clear = true;
                for (int s = 1; s < n && clear; s++)
                {
                    var q = eye + v * (s / (float)n);
                    var qp = new Vector2(q.X, q.Y);
                    if (Vector2.Distance(qp, d.Centre) < 3f || Vector2.Distance(qp, new Vector2(to.X, to.Y)) < 2f) continue;
                    if (grid.Top[grid.Index(qp)] > q.Z) clear = false;
                }
                if (clear) seen += p.Name;
            }
            d.Sees = seen;
        }
    }

    static void Jumps(Result res, AshfallGrid grid, Func<float, float, float> ground)
    {
        var tiers = AssaultJump.Tiers;
        foreach (var d in res.Decks)
        {
            // landing cells: deck cells whose whole 1 m neighbourhood is deck floor, thinned to a cap
            var land = d.Cells.Where(c => Enumerable.Range(-2, 5).All(dx => Enumerable.Range(-2, 5).All(dy => d.Cells.Contains((c.Item1 + dx, c.Item2 + dy))))).ToList();
            if (land.Count == 0) land = [.. d.Cells];
            int every = Math.Max(1, land.Count / 48);
            var picked = land.Where((_, k) => k % every == 0).ToList();
            foreach (var tier in tiers)
            {
                foreach (var c in picked)
                {
                    var target = new Vector2((c.Item1 + 0.5f) * Cell, (c.Item2 + 0.5f) * Cell);
                    for (int b = 0; b < 24; b++)
                    {
                        float ang = b * MathF.PI / 12f;
                        var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));   // the direction of travel
                        // launch from open ground
                        float gz = ground(target.X, target.Y);
                        float dist = tier.Landing(d.Z - gz);
                        if (dist <= 0f) continue;
                        var from = target - dir * dist;
                        if (!d.JumpFromGround && Launchable(grid, from, gz, ground) && Clear(grid, from, dir, dist, tier, gz, d))
                        { d.JumpFromGround = true; d.JumpFrom = from; d.JumpTier = tier.Name; }
                    }
                }
            }
        }
        // from deck to deck
        foreach (var a in res.Decks)
        {
            var launch = a.Cells.Where(c => Enumerable.Range(-1, 3).All(dx => Enumerable.Range(-1, 3).All(dy => a.Cells.Contains((c.Item1 + dx, c.Item2 + dy))))).ToList();
            int every = Math.Max(1, launch.Count / 40);
            var lp = launch.Where((_, k) => k % every == 0).Select(c => new Vector2((c.Item1 + 0.5f) * Cell, (c.Item2 + 0.5f) * Cell)).ToList();
            foreach (var b in res.Decks)
            {
                if (a == b || b.JumpFromDecks.Contains(a.Id) || Vector2.Distance(a.Centre, b.Centre) > 90f) continue;
                if (b.Z > a.Z + 17f) continue;
                bool ok = false;
                foreach (var tier in tiers)
                {
                    float dist = tier.Landing(b.Z - a.Z);
                    if (dist <= 0f) continue;
                    foreach (var from in lp)
                    {
                        for (int k = 0; k < 24 && !ok; k++)
                        {
                            float ang = k * MathF.PI / 12f;
                            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                            var to = from + dir * dist;
                            if (!b.Cells.Contains(((int)MathF.Floor(to.X / Cell), (int)MathF.Floor(to.Y / Cell)))) continue;
                            if (Clear(grid, from, dir, dist, tier, a.Z, b, a)) ok = true;
                        }
                        if (ok) break;
                    }
                    if (ok) break;
                }
                if (ok) b.JumpFromDecks.Add(a.Id);
            }
        }
    }

    static bool Launchable(AshfallGrid grid, Vector2 p, float gz, Func<float, float, float> ground)
    {
        if (MathF.Abs(p.X) > 236f || MathF.Abs(p.Y) > 236f) return false;
        int k = grid.Index(p);
        return grid.Clearance[k] >= 1.2f && grid.Top[k] <= ground(p.X, p.Y) + 0.05f;
    }

    /// <summary>Whether a jump from <paramref name="from"/> (feet at <paramref name="z0"/>) along <paramref name="dir"/> clears everything between and lands on <paramref name="target"/>'s floor after <paramref name="dist"/> m.</summary>
    static bool Clear(AshfallGrid grid, Vector2 from, Vector2 dir, float dist, AssaultJump.Tier tier, float z0, Deck target, Deck? origin = null)
    {
        for (float s = 0.5f; s < dist; s += 0.5f)
        {
            var p = from + dir * s;
            float z = z0 + tier.RiseAt(s);
            var k = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
            if (target.Cells.Contains(k) && s > dist - 3f) continue;           // over the target's own floor on the way down
            if (origin is not null && origin.Cells.Contains(k) && s < 3f) continue;
            if (MathF.Abs(p.X) > 238f || MathF.Abs(p.Y) > 238f) return false;
            if (grid.Top[grid.Index(p)] > z - 0.05f) return false;             // a solid under the feet's line
        }
        return true;
    }
}

/// <summary>
/// The Assault Marine's jump pack as the sim has it (spec/behavior.md "Jump pack and ground pound": height by hold 4, 13 and 18 m with measured apexes 3.79, 11.63 and 17.54 m, lift-off horizontal 16 m/s, ascent gravity 20,
/// hang 12 for 0.5 s, fall 30): the path of a jump from flat ground with the stick held forward, for the layout check. Ours: the take-off's 0.2 s of ground movement and the air control are left out (a conservative reach).
/// </summary>
public static class AssaultJump
{
    public const float Speed = 16f, AscentGravity = 20f, HangGravity = 12f, HangTime = 0.5f, FallGravity = 30f, Dt = 1f / 30f;

    /// <summary>One hold: its apex (m, as measured in the sim) and the sampled path (horizontal distance, height over the launch).</summary>
    public sealed class Tier
    {
        public string Name = "";
        public float Apex;
        internal List<(float D, float Z)> Path = [];

        /// <summary>The height over the launch point at horizontal distance <paramref name="d"/> (m).</summary>
        public float RiseAt(float d)
        {
            if (d <= 0f) return 0f;
            for (int i = 1; i < Path.Count; i++)
                if (Path[i].D >= d) { float t = (d - Path[i - 1].D) / MathF.Max(Path[i].D - Path[i - 1].D, 1e-6f); return Path[i - 1].Z + (Path[i].Z - Path[i - 1].Z) * t; }
            return Path[^1].Z;
        }

        /// <summary>The horizontal distance at which the descending jump reaches <paramref name="rise"/> m over its launch (0 when the apex is lower).</summary>
        public float Landing(float rise)
        {
            if (rise > Apex - 0.3f) return 0f;
            for (int i = Path.Count - 1; i > 0; i--)
                if (Path[i - 1].Z >= rise && Path[i].Z < rise) return Path[i].D;
            return 0f;
        }
    }

    public static readonly IReadOnlyList<Tier> Tiers = [Make("tap", 3.79f), Make("half", 11.63f), Make("full", 17.54f)];

    /// <summary>The highest floor an Assault jump from the ground reaches (m): the full hold's apex.</summary>
    public static float Reach => Tiers[^1].Apex;

    static Tier Make(string name, float apex)
    {
        var t = new Tier { Name = name, Apex = apex };
        float vy = MathF.Sqrt(2f * AscentGravity * apex) + 0.5f * AscentGravity * Dt, z = 0f, time = 0f, hang = 0f;   // the half step makes the stepped peak the measured apex
        bool apexed = false;
        t.Path.Add((0f, 0f));
        for (int i = 0; i < 600 && z > -40f; i++)
        {
            float g = !apexed ? AscentGravity : hang < HangTime ? HangGravity : FallGravity;
            vy -= g * Dt; z += vy * Dt; time += Dt;
            if (!apexed && vy <= 0f) apexed = true; else if (apexed && hang < HangTime) hang += Dt;
            t.Path.Add((Speed * time, z));
        }
        return t;
    }
}
