using System.Numerics;
using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Firing positions on cramped ground (Guardsmen G1d). A point is cramped when at least half its slots at the current approach are cut to half their forward offset or less by <see cref="SlotPlace"/>. There the
/// squads take firing positions instead: walking-graph nodes reached by path within the radius plus 14 m, forward of the point, one a 3 x 3 m cell, at least 1.5 m off the walls, at most 48, each with a clear line of
/// sight (weapons mesh) from 1.5 m over it to 1.2 m over a spot 20 m out along the approach. The squads take them in rank order, the nearest to their grid slot, at least 6 m apart; a squad with none left keeps its
/// slot. The candidates are cached per point and approach. Open points keep their grid slots.
/// </summary>
public sealed partial class TfwBattle
{
    readonly Vector2[] _wfFiringDir = new Vector2[Positions];
    readonly List<Vector3>?[] _wfFiring = new List<Vector3>?[Positions];
    readonly bool[] _wfCramped = new bool[Positions];

    /// <summary>Whether point <paramref name="k"/> stood cramped at its last formation, and its firing positions (tests, the tools).</summary>
    public bool WarfrontCramped(int k) => k is >= 0 and < Positions && _wfCramped[k];
    public IReadOnlyList<Vector3> WarfrontFiringPositions(int k) => k is >= 0 and < Positions && _wfFiring[k] is { } f ? f : [];

    /// <summary>Whether a cut forward share marks a cramped slot (at most half its offset kept).</summary>
    public static bool CrampedSlot(float forwardKept) => forwardKept <= 0.5f;

    /// <summary>Whether a set of slots is cramped: at least <see cref="WarfrontLineRules.CrampedShare"/> of them cut.</summary>
    public static bool Cramped(int cut, int slots) => slots > 0 && cut >= WarfrontLineRules.CrampedShare * slots;

    /// <summary>Point <paramref name="k"/>'s firing positions at approach <paramref name="dir"/> (cached; rebuilt when the approach turned).</summary>
    List<Vector3> WfFiringFor(int k, Vector2 dir)
    {
        if (_wfFiring[k] is { } cached && _wfFiringDir[k] == dir) return cached;
        var list = _wfFiring[k] ?? new List<Vector3>();
        list.Clear();
        _wfFiring[k] = list;
        _wfFiringDir[k] = dir;
        if (_enemies.Walk is not { } walk) return list;
        var point = _layout.Positions[k];
        var origin = Ground(point, point);
        int start = walk.Nearest(origin, 3f, 2.5f);
        if (start < 0) return list;
        float most = RadiusOf(k) + WarfrontLineRules.MenSlack;
        var dist = new Dictionary<int, float>();   // lookups only, never iterated (determinism)
        var cells = new HashSet<long>();
        var open = new PriorityQueue<int, (float, int)>();
        dist[start] = 0f;
        open.Enqueue(start, (0f, start));
        int expanded = 0;
        while (open.TryDequeue(out int node, out var pri) && list.Count < WarfrontLineRules.FiringCandidates && expanded < 20_000)
        {
            if (pri.Item1 > dist[node]) continue;
            expanded++;
            var at = walk.Position(node);
            var off = new Vector2(at.X - origin.X, at.Y - origin.Y);
            if (Vector2.Dot(off, dir) >= 0f && walk.EdgeDistance(node) >= WarfrontLineRules.FiringClearance)
            {
                long cell = ((long)(int)MathF.Floor(at.X / WarfrontLineRules.FiringCell) << 32) ^ (uint)(int)MathF.Floor(at.Y / WarfrontLineRules.FiringCell);
                if (!cells.Contains(cell) && WfFiringSight(at, dir))
                {
                    cells.Add(cell);
                    list.Add(at);
                }
            }
            for (int link = walk.ForwardStart(node); link < walk.ForwardEnd(node); link++)
            {
                int to = walk.ForwardTo(link);
                float d = pri.Item1 + walk.ForwardCost(link);
                if (d > most || (dist.TryGetValue(to, out float had) && had <= d)) continue;
                dist[to] = d;
                open.Enqueue(to, (d, to));
            }
        }
        return list;
    }

    /// <summary>The firing sight: from the eye over <paramref name="at"/> to the chest over the spot <see cref="WarfrontLineRules.FiringSightMetres"/> out along the approach, clear of the weapons mesh.</summary>
    bool WfFiringSight(Vector3 at, Vector2 dir)
    {
        if (_guards.Sight is not { } sight) return true;
        var eye = at + new Vector3(0f, 0f, _guards.Rules.EyeHeight);
        var to = at + new Vector3(dir * WarfrontLineRules.FiringSightMetres, _guards.Rules.ChestHeight);
        var ray = to - eye;
        float length = ray.Length();
        return length < 1e-3f || !(sight.Weapons.Raycast(eye, ray / length, length) is { } wall && wall.Distance < length - 0.1f);
    }

    /// <summary>The squads in rank order take firing positions greedily (the nearest to each one's grid slot, at least <see cref="WarfrontLineRules.FiringSpacing"/> apart); -1 keeps the slot.</summary>
    static void AssignFiring(ReadOnlySpan<Vector3> slots, IReadOnlyList<Vector3> candidates, Span<int> pick)
    {
        Span<bool> taken = candidates.Count <= 64 ? stackalloc bool[candidates.Count] : new bool[candidates.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            pick[i] = -1;
            float best = float.PositiveInfinity;
            for (int c = 0; c < candidates.Count; c++)
            {
                if (taken[c]) continue;
                bool spaced = true;
                for (int j = 0; j < i && spaced; j++) if (pick[j] >= 0 && Vector3.Distance(candidates[pick[j]], candidates[c]) < WarfrontLineRules.FiringSpacing) spaced = false;
                if (!spaced) continue;
                float d = Vector3.DistanceSquared(candidates[c], slots[i]);
                if (d < best) { best = d; pick[i] = c; }
            }
            if (pick[i] >= 0) taken[pick[i]] = true;
        }
    }

    /// <summary>Test aid: the greedy firing assignment on given slots and candidates.</summary>
    public static int[] AssignFiringForTests(Vector3[] slots, Vector3[] candidates)
    {
        var pick = new int[slots.Length];
        AssignFiring(slots, candidates, pick);
        return pick;
    }
}
