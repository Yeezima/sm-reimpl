using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Late batch F (owner, 2026-11-06; ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 9 layout"): the stair rules. Every placed stair (a kit model's ramp wedge, an
/// <see cref="AshfallKind.Stair"/> part with a sloped top) has an <b>opening at its foot</b> (nothing stands in the <see cref="Approach"/> m in front of its low end, across a run at least
/// <see cref="MinClear"/> wide, and the low end stands within a step of the ground or a floor there) and a <b>clear landing at its top</b> (the same <see cref="Approach"/> beyond its high end is free of
/// anything to head height and has a floor within a step of the stair's top). Measured on the level's collision boxes (what walkers meet) plus the ground's surface.
/// </summary>
public static class AshfallStairs
{
    /// <summary>The depth of the clear approach in front of a stair's foot and of the landing beyond its top (ours: a stride and a half at run speed, more than the capsule's 0.9 m).</summary>
    public const float Approach = 1.5f;

    /// <summary>
    /// Draft 10 (owner, 2026-11-09: "strengthen the rule that clears space at the bottom of stairs"; ours): the depth of the zone in front of a stair's foot that no other item may enter (twice the draft-9
    /// <see cref="Approach"/>: two strides at run speed, room to turn onto the flight), and the margin beside the stair on each side of its foot and landing zones.
    /// </summary>
    public const float FootApproach = 3.0f, SideMargin = 0.5f;

    /// <summary>Draft 10: the headroom a stair's carved corridor keeps over its ramp (the capsule's 1.9 m plus 0.3 m: no lintel or tread above catches the head on the way up).</summary>
    public const float CorridorHeadroom = 2.2f;

    /// <summary>The narrowest clear run across a stair's foot or top that counts as an opening (ours: the capsule's 0.9 m plus 0.1 m, the narrowest stair the level keeps).</summary>
    public const float MinClear = 1.0f;

    /// <summary>The headroom kept clear over a foot or a landing (the capsule's height, 1.9 m).</summary>
    public const float Headroom = 1.9f;

    /// <summary>The step the character climbs (<c>SimConfig.StepHeight</c>, 0.4 m).</summary>
    public const float Step = 0.4f;

    /// <summary>A placed stair: its part and item, the middles of its low and high edges (plan), their heights, the climbing direction (unit, plan) and its width.</summary>
    public readonly record struct Ramp(int Part, int Item, Vector2 Low, Vector2 High, float LowZ, float HighZ, Vector2 Dir, float Width)
    {
        public Vector2 Across => new(-Dir.Y, Dir.X);
        public float Run => Vector2.Distance(Low, High);
        public float Degrees => MathF.Atan2(HighZ - LowZ, MathF.Max(Run, 1e-3f)) * (180f / MathF.PI);
    }

    /// <summary>The ramp a sloped stair part describes (null for a box stair or anything else).</summary>
    public static Ramp? Of(AshfallPart part, int index, int item)
    {
        if (!part.MovementOnly) return null;
        var cols = new List<(Vector2 P, float Top)>();
        foreach (var v in part.Corners)
        {
            var p = new Vector2(v.X, v.Y);
            int k = cols.FindIndex(c => Vector2.DistanceSquared(c.P, p) < 1e-4f);
            if (k < 0) cols.Add((p, v.Z)); else if (v.Z > cols[k].Top) cols[k] = (cols[k].P, v.Z);
        }
        if (cols.Count != 4) return null;
        cols.Sort((a, b) => a.Top.CompareTo(b.Top));
        var low = (cols[0].P + cols[1].P) / 2f; var high = (cols[2].P + cols[3].P) / 2f;
        var d = high - low;
        if (d.LengthSquared() < 1e-6f) return null;
        return new Ramp(index, item, low, high, (cols[0].Top + cols[1].Top) / 2f, (cols[2].Top + cols[3].Top) / 2f, Vector2.Normalize(d), Vector2.Distance(cols[0].P, cols[1].P));
    }

    /// <summary>Every placed stair of the level (the items' parts ranges name their item; -1 outside any item).</summary>
    public static List<Ramp> All(AshfallLevel level, AshfallDressing dressing)
    {
        var owner = new int[level.Parts.Count];
        Array.Fill(owner, -1);
        for (int i = 0; i < dressing.ItemParts.Count; i++) { var (f, n) = dressing.ItemParts[i]; for (int p = f; p < f + n; p++) owner[p] = i; }
        var list = new List<Ramp>();
        for (int p = 0; p < level.Parts.Count; p++)
            if (level.Parts[p].Kind == AshfallKind.Stair && Of(level.Parts[p], p, owner[p]) is { } r) list.Add(r);
        return list;
    }

    /// <summary>The level's solid boxes (stairs and the ground's kinds left out: the ground is <see cref="AshfallMap.SurfaceAt"/>), on a grid for point queries.</summary>
    public sealed class Solids
    {
        const float Cell = 8f;
        readonly Dictionary<(int, int), List<AshfallBox>> _grid = [];

        public Solids(IEnumerable<AshfallBox> boxes)
        {
            foreach (var b in boxes) Add(b);
        }

        /// <summary>The ground under the boxes (the level's surface by default).</summary>
        public Func<float, float, float> Ground { get; init; } = AshfallMap.SurfaceAt;

        public static Solids Of(AshfallLevel level) => new(level.Parts.Where(p => p.Solid && p.Kind is not (AshfallKind.Stair or AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb))
            .Select(AshfallBox.Of).Where(b => b is not null).Select(b => b!.Value));

        public void Add(AshfallBox b)
        {
            var (s, c) = AshfallBuilder.Trig(b.YawStep);
            float ex = MathF.Abs(c) * b.Length / 2f + MathF.Abs(s) * b.Depth / 2f, ey = MathF.Abs(s) * b.Length / 2f + MathF.Abs(c) * b.Depth / 2f;
            for (int gx = (int)MathF.Floor((b.Cx - ex) / Cell); gx <= (int)MathF.Floor((b.Cx + ex) / Cell); gx++)
                for (int gy = (int)MathF.Floor((b.Cy - ey) / Cell); gy <= (int)MathF.Floor((b.Cy + ey) / Cell); gy++)
                {
                    if (!_grid.TryGetValue((gx, gy), out var l)) _grid[(gx, gy)] = l = [];
                    l.Add(b);
                }
        }

        IEnumerable<AshfallBox> At(Vector2 p) => _grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l) ? l.Where(b => AshfallAtmosphere.Inside(b, p, 0f)) : [];

        /// <summary>Whether a solid box at this point meets the height band [<paramref name="z0"/>, <paramref name="z1"/>].</summary>
        public bool Blocked(Vector2 p, float z0, float z1) => At(p).Any(b => b.Z0 < z1 && b.Z1 > z0);

        /// <summary>The highest floor (a box top or the ground) at this point at or under <paramref name="zMax"/>.</summary>
        public float FloorUnder(Vector2 p, float zMax)
        {
            float best = Ground(p.X, p.Y);
            foreach (var b in At(p)) if (b.Z1 <= zMax && b.Z1 > best) best = b.Z1;
            return best;
        }
    }

    /// <summary>
    /// One end of a stair, measured: the widest clear run across it (metres; a column is clear when nothing stands in it from a step over the end's height to head height over the whole
    /// <see cref="Approach"/>, and its floor lies within a step of the end), and the floor height found at its middle.
    /// </summary>
    public readonly record struct End(float ClearRun, float Floor, Vector2 Middle, float Need)
    {
        /// <summary>Whether the end is an opening: a clear run of <see cref="MinClear"/> (or the stair's whole width, when narrower).</summary>
        public bool Open => ClearRun >= Need - 1e-3f;
    }

    /// <summary>Draft 10: the landing depth measured on every solid (the walk back down starts this far beyond the top; a stair tower's landing turns at its wall after 1 to 1.5 m). Other items keep out of <see cref="Approach"/>.</summary>
    public const float LandingDepth = 1.0f;

    /// <summary>
    /// The foot (<paramref name="top"/> false: in front of the low end, over <see cref="Approach"/>; other items keep out of <see cref="FootApproach"/> since draft 10) or the landing (beyond the high end, over <see cref="LandingDepth"/>) of a stair.
    /// </summary>
    public static End Measure(Ramp r, Solids solids, bool top) => Measure(r, solids, top, top ? LandingDepth : Approach);

    /// <summary>One end of a stair measured over <paramref name="depth"/> metres.</summary>
    public static End Measure(Ramp r, Solids solids, bool top, float depth)
    {
        var origin = top ? r.High : r.Low;
        var outward = top ? r.Dir : -r.Dir;
        float z = top ? r.HighZ : r.LowZ;
        float w = MathF.Max(r.Width, 0.1f);
        int n = Math.Max(2, (int)MathF.Ceiling(w / 0.2f));
        float best = 0f, run = 0f;
        for (int i = 0; i <= n; i++)
        {
            float t = -w / 2f + w * i / n;
            bool clear = true;
            foreach (float a in new[] { 0.15f, depth / 4f, depth / 2f, 3f * depth / 4f, depth - 0.05f })   // draft 10: the last sample 5 cm short of the depth (a wall standing exactly there leaves the depth clear)
            {
                var p = origin + outward * a + r.Across * t;
                float floor = solids.FloorUnder(p, z + Step);
                if (MathF.Abs(floor - z) > Step + 0.05f || solids.Blocked(p, z + Step + 0.05f, z + Headroom)) { clear = false; break; }
            }
            run = clear ? run + w / n : 0f;   // draft 10: each clear sample column stands for its cell (w / n), so k columns are k cells (draft 9 counted k - 1)
            best = MathF.Max(best, run);
        }
        var mid = origin + outward * (depth / 2f);
        return new End(MathF.Min(best, w), solids.FloorUnder(mid, z + Step), mid, MathF.Min(MinClear, 0.8f * w));
    }
}
