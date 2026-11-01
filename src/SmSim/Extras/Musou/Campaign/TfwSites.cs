using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Where a battle's random places fall (the owner's item 4: "randomize spawn location in logical, reasonable places to add a bit of variety"; design section A, every number ours). Each
/// battle draws from a hash of its seed, so a replay lands the same way. Candidates are the graph's open nodes (room on every side) on its largest connected part; without a graph
/// (tests, a failed build) the places are fixed offsets of the map's markers. See spec "Musou Mode's map".
/// </summary>
public sealed class TfwSites
{
    /// <summary>A source stands this far from what it feeds.</summary>
    public const float SourceNear = 15f, SourceFar = 40f;
    /// <summary>Nothing of the Orks comes up nearer than this to the landing zone.</summary>
    public const float FromImperials = 25f;
    /// <summary>A landing zone away from the start area stands at least this far from every point.</summary>
    public const float LandingFromPoints = 60f;
    /// <summary>Sources keep this far from each other.</summary>
    public const float SourceSpacing = 12f;
    /// <summary>An open node has this much walkable ground in each of four directions (the spawn spots round a source fit).</summary>
    public const float OpenRoom = 4f;
    /// <summary>Draws per place before a rule is dropped.</summary>
    const int Tries = 600;
    const float EyeHeight = 1.8f;

    readonly TfwBattlefield _field;
    readonly NavGraph? _nav;
    readonly CollisionWorld? _world;
    readonly int[] _open;

    public TfwSites(TfwBattlefield field, NavGraph? nav, CollisionWorld? world)
    {
        _field = field;
        _nav = nav is { NodeCount: > 0 } ? nav : null;
        _world = world;
        _open = _nav is null ? [] : OpenNodes(_nav);
    }

    /// <summary>The nodes a place can use.</summary>
    public int Candidates => _open.Length;

    /// <summary>The nodes of the largest connected part (links taken both ways) with room round them, in node order.</summary>
    static int[] OpenNodes(NavGraph nav)
    {
        int n = nav.NodeCount;
        var part = new int[n];
        var back = new List<int>[n];
        for (int a = 0; a < n; a++)
            foreach (var (to, _, _) in nav.Links(a)) (back[to] ??= new List<int>(4)).Add(a);
        var stack = new Stack<int>();
        int parts = 0, best = 0, bestSize = 0;
        for (int start = 0; start < n; start++)
        {
            if (part[start] != 0) continue;
            int id = ++parts, size = 0;
            part[start] = id;
            stack.Push(start);
            while (stack.TryPop(out int a))
            {
                size++;
                foreach (var (to, _, _) in nav.Links(a)) if (part[to] == 0) { part[to] = id; stack.Push(to); }
                if (back[a] is { } froms) foreach (int from in froms) if (part[from] == 0) { part[from] = id; stack.Push(from); }
            }
            if (size > bestSize) { best = id; bestSize = size; }
        }
        var open = new List<int>(bestSize);
        ReadOnlySpan<Vector2> ways = [new(OpenRoom, 0f), new(-OpenRoom, 0f), new(0f, OpenRoom), new(0f, -OpenRoom)];
        for (int a = 0; a < n; a++)
        {
            if (part[a] != best || a % 4 != 0) continue;   // one node in four: plenty, and quick at the start
            var at = nav.Position(a);
            bool room = true;
            foreach (var way in ways) if (!nav.CanWalk(at, way)) { room = false; break; }
            if (room) open.Add(a);
        }
        return open.Count > 0 ? open.ToArray() : Enumerable.Range(0, n).Where(a => part[a] == best).ToArray();
    }

    /// <summary>FNV-1a over the words: the battle's dice.</summary>
    static ulong Hash(ulong h, ulong value)
    {
        for (int b = 0; b < 8; b++, value >>= 8) h = (h ^ (value & 0xFF)) * 1099511628211UL;
        return h;
    }

    /// <summary>The next draw of <paramref name="state"/>, as a 64-bit number (one step of SplitMix64).</summary>
    static ulong Next(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>Whether <paramref name="spot"/> is hidden from someone standing at <paramref name="from"/>: a shot's surface lies between the eyes.</summary>
    bool Hidden(Vector3 from, Vector3 spot)
    {
        if (_world is null) return true;
        var eye = from + new Vector3(0f, 0f, EyeHeight);
        var way = spot + new Vector3(0f, 0f, EyeHeight) - eye;
        float length = way.Length();
        return length < 0.01f || _world.Weapons.Raycast(eye, way / length, length) is not null;
    }

    /// <summary>The first open node of a drawn order that <paramref name="rule"/> accepts.</summary>
    Vector3? Draw(ref ulong dice, Func<Vector3, bool> rule)
    {
        for (int k = 0; k < Tries && _open.Length > 0; k++)
        {
            var at = _nav!.Position(_open[(int)(Next(ref dice) % (ulong)_open.Length)]);
            if (rule(at)) return at;
        }
        return null;
    }

    /// <summary>
    /// The layout of a battle: the map's points in order A to C or, on an even <paramref name="node"/>, C to A (B, the depot, is always second), the commander in the Chaos start area, and
    /// the landing zone and the sources drawn from <paramref name="seed"/>. Lanes differ by the draw alone (a turned lane means nothing on a real map).
    /// </summary>
    public TfwLayout Layout(TfwLane lane, int node, ulong seed)
    {
        var points = _field.Points;
        int[] order = node % 2 == 0 ? [2, 1, 0] : [0, 1, 2];
        var positions = order.Select(k => OnGraph(points[k].Centre)).ToArray();
        ulong dice = Hash(Hash(Hash(14695981039346656037UL, seed), (ulong)lane), (ulong)node);
        var landing = Landing(ref dice);
        var hq = OnGraph(_field.Commander);
        Vector3[] anchors = [positions[0], positions[1], positions[2], hq];
        var sources = new Vector3[anchors.Length];
        for (int k = 0; k < sources.Length; k++) sources[k] = Source(ref dice, anchors[k], landing, sources.AsSpan(0, k).ToArray());
        return new TfwLayout(landing, positions, hq, sources, order.Select(k => points[k].Radius).ToArray(), order.Select(k => points[k].HalfHeight).ToArray());
    }

    /// <summary><paramref name="p"/> on the ground under it, when the graph has a node near; else as it is.</summary>
    Vector3 OnGraph(Vector3 p)
    {
        if (_nav is null) return p;
        int node = _nav.Nearest(p, 6f, 8f);
        return node >= 0 ? _nav.Position(node) : p;
    }

    /// <summary>A third of the time the Space Marine start area; else two draws of open ground away from every point, the farther from all of them winning.</summary>
    Vector3 Landing(ref ulong dice)
    {
        var start = OnGraph(_field.MarineStart);
        if (_nav is null || Next(ref dice) % 3 == 0) return start;
        Func<Vector3, bool> away = at =>
        {
            foreach (var p in _field.Points) if (Flat(at, p.Centre) < LandingFromPoints) return false;
            return Flat(at, _field.Commander) >= LandingFromPoints;
        };
        var one = Draw(ref dice, away);
        var two = Draw(ref dice, away);
        if (one is null) return two ?? start;
        if (two is null) return one.Value;
        float Clearance(Vector3 at) => _field.Points.Min(p => Flat(at, p.Centre));
        return Clearance(one.Value) >= Clearance(two.Value) ? one.Value : two.Value;
    }

    /// <summary>A source for <paramref name="anchor"/>: all the rules, then without the view rule, then the ring alone; without any node, a fixed step from the anchor away from the landing zone.</summary>
    Vector3 Source(ref ulong dice, Vector3 anchor, Vector3 landing, Vector3[] others)
    {
        bool Ring(Vector3 at) { float d = Flat(at, anchor); return d >= SourceNear && d <= SourceFar; }
        bool Clear(Vector3 at)
        {
            if (!Ring(at) || Flat(at, landing) < FromImperials) return false;
            foreach (var o in others) if (Flat(at, o) < SourceSpacing) return false;
            return true;
        }
        var found = Draw(ref dice, at => Clear(at) && Hidden(landing, at)) ?? Draw(ref dice, Clear) ?? Draw(ref dice, Ring);
        if (found is { } spot) return spot;
        var way = new Vector2(anchor.X - landing.X, anchor.Y - landing.Y);
        way = way.LengthSquared() > 1e-4f ? Vector2.Normalize(way) : Vector2.UnitY;
        return anchor + new Vector3(way * ((SourceNear + SourceFar) / 2f), 0f);
    }
}
