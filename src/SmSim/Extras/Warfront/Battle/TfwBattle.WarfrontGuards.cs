using System.Numerics;
using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Warfront's Guardsmen in the battle (owner 2026-11-20): twice the numbers (<see cref="GuardScale"/>), their strength at the Orks' favour (<see cref="WarfrontGuardRules"/>, in the World's GuardSim) and the defence in depth: the
/// squads of a held point stand in a grid of rows facing the Orks' approach, each row in the gaps of the one before, so the rows behind fire past the front line; a battered front squad falls back to the rear row and the squad
/// behind it steps up; a fallen front squad's place is taken by the next row. Lane battles and the fortress; the HQ defence keeps its own posts (only its numbers are doubled). Every hook returns at once outside Warfront.
/// </summary>
public sealed partial class TfwBattle
{
    readonly int[] _wfRank = new int[GuardSim.MaxSquads];              // a squad's place in its point's order (0 first: the front line's left end)
    readonly Vector3[] _wfPlace = new Vector3[GuardSim.MaxSquads];      // the place its last formation order sent it to
    readonly float[] _wfYaw = new float[GuardSim.MaxSquads];
    readonly Vector2[] _wfApproach = new Vector2[Positions];            // each point's approach (unit, flat); zero: not chosen yet
    readonly List<int> _wfSquads = new();
    readonly ushort[] _wfNear = new ushort[96];
    int _wfFormationSets;

    /// <summary>Formation orders given so far (tests, the bench).</summary>
    public int WarfrontFormationOrders => _wfFormationSets;

    /// <summary>A point's approach (the unit vector from its centre toward where the Orks come from; zero before the first formation).</summary>
    public Vector2 WarfrontApproach(int position) => position is >= 0 and < Positions ? _wfApproach[position] : Vector2.Zero;

    /// <summary>The place squad <paramref name="squad"/> was last ordered to by the formation (zero: none) (tests).</summary>
    public Vector3 WarfrontSlotOf(int squad) => squad is >= 0 and < GuardSim.MaxSquads ? _wfPlace[squad] : Vector3.Zero;

    partial void WarfrontGuardScale(ref int scale)
    {
        if (_enemies.Warfront is not null) scale = WarfrontGuardRules.NumberScale;
    }

    partial void WarfrontFinalFieldHook(ref bool fortress)
    {
        if (_enemies.Warfront?.Fortress is not null) fortress = true;
    }

    partial void WarfrontGuardWorth(ref float worth)
    {
        if (_enemies.Warfront is not null) worth *= WarfrontGuardRules.ClashWorthScale;
    }

    /// <summary>The rows reach past a point's radius: a squad in them is on its goal.</summary>
    partial void WarfrontOnGoal(int squad, int goal, ref bool on)
    {
        if (on || WarfrontGuardRules.Old || _enemies.Warfront is null || WarfrontHq || goal >= Positions) return;
        var at = PlaceOf(goal);
        var c = _guards.Centre(squad);
        on = new Vector2(c.X - at.X, c.Y - at.Y).Length() <= RadiusOf(goal) + WarfrontGuardRules.GoalSlack;
    }

    /// <summary>
    /// Once a second after the squads' posts are set: on each secured point the squads stand in rows facing the approach (healthy squads in their order, battered ones last), and a squad whose place moved is ordered to the new one.
    /// </summary>
    partial void WarfrontFormation()
    {
        if (WarfrontGuardRules.Old || _enemies.Warfront is null || WarfrontHq || !_guardsmen || !_guards.Rules.Spread) return;
        for (int k = 0; k < Positions; k++)
        {
            if (_objectives[k].State != TfwPositionState.Secured) continue;
            _wfSquads.Clear();
            for (int s = 0; s < GuardSim.MaxSquads; s++)
                if (_squadGoal[s] == k && _guards.AliveIn(s) > 0 && !ArmySquad(s) && !VantageSquad(s)) _wfSquads.Add(s);
            foreach (var army in _armies)   // the garrison's promoted squads stand in the same rows (their block layout is a dense rectangle)
                if (army.Side == TfwArmySide.Guard && army.Order == TfwArmyOrder.Hold && army.Goal == k)
                    foreach (int s in army.Squads) if (_guards.AliveIn(s) > 0 && !VantageSquad(s)) _wfSquads.Add(s);
            if (_wfSquads.Count == 0) continue;
            var dir = Approach(k);
            // Healthy squads keep their order (new ones after), battered ones fall to the back, unless every one is battered.
            int healthy = 0;
            foreach (int s in _wfSquads) if (_guards.AliveIn(s) > WarfrontGuardRules.BatteredMen) healthy++;
            bool fall = healthy > 0;
            _wfSquads.Sort((a, b) =>
            {
                int ba = fall && _guards.AliveIn(a) <= WarfrontGuardRules.BatteredMen ? 1 : 0, bb = fall && _guards.AliveIn(b) <= WarfrontGuardRules.BatteredMen ? 1 : 0;
                int c = ba.CompareTo(bb);
                if (c != 0) return c;
                c = WfRankKey(a).CompareTo(WfRankKey(b));
                return c != 0 ? c : a.CompareTo(b);
            });
            for (int i = 0; i < _wfSquads.Count; i++) _wfRank[_wfSquads[i]] = i;
            var point = _layout.Positions[k];
            float yaw = MathF.Atan2(-dir.X, dir.Y);
            for (int i = 0; i < _wfSquads.Count; i++)
            {
                int s = _wfSquads[i];
                var slot = SlotPlace(point, dir, i, _wfSquads.Count, RadiusOf(k));
                if (WfFormed(s) && Vector3.Distance(_wfPlace[s], slot) < 1f && MathF.Abs(_wfYaw[s] - yaw) < 0.01f) continue;
                Span<Vector3> posts = stackalloc Vector3[GuardSim.SquadSize];
                Posts(slot, dir, posts);
                if (_guards.OrderPosts(s, slot, posts, yaw, point))
                {
                    _wfPlace[s] = slot; _wfYaw[s] = yaw; _wfFormationSets++;
                }
            }
        }
    }

    int WfRankKey(int s) => WfFormed(s) ? _wfRank[s] : 1000 + s;   // a squad not placed yet comes after the placed ones

    /// <summary>Squad <paramref name="s"/> still stands on the formation order this file gave it: not re-sent by the battle, not a new squad in a dead one's slot (review 2026-11-20: the memory is per slot index).</summary>
    bool WfFormed(int s) => _wfPlace[s] != Vector3.Zero && Vector3.DistanceSquared(_guards.OrderOf(s), _wfPlace[s]) < 1e-4f;

    /// <summary>Point <paramref name="k"/>'s approach: the Orks within <see cref="WarfrontGuardRules.ApproachRadius"/> when there are enough, else its Ork source; turned only past <see cref="WarfrontGuardRules.ApproachTurnDegrees"/>.</summary>
    Vector2 Approach(int k)
    {
        var point = _layout.Positions[k];
        var want = Vector2.Zero;
        int n = _enemies.Query(point, WarfrontGuardRules.ApproachRadius, _wfNear, WarfrontGuardRules.ApproachRadius);
        if (n >= WarfrontGuardRules.ApproachOrks)
        {
            var sum = Vector2.Zero; int counted = 0;
            for (int i = 0; i < n; i++)
                if (_enemies.TryGet(_wfNear[i], out var e)) { sum += new Vector2(e.Position.X - point.X, e.Position.Y - point.Y); counted++; }
            if (counted > 0) want = sum / counted;
        }
        if (want.LengthSquared() < 4f)
        {
            if (_wfApproach[k] != Vector2.Zero) return _wfApproach[k];
            var src = _layout.Sources.Length > k ? _layout.Sources[k] : _layout.Hq;
            want = new Vector2(src.X - point.X, src.Y - point.Y);
            if (want.LengthSquared() < 4f) want = new Vector2(_layout.Hq.X - point.X, _layout.Hq.Y - point.Y);
            if (want.LengthSquared() < 4f) want = Vector2.UnitY;
        }
        want = Vector2.Normalize(want);
        var now = _wfApproach[k];
        if (now == Vector2.Zero || MathF.Acos(Math.Clamp(Vector2.Dot(now, want), -1f, 1f)) * 180f / MathF.PI > WarfrontGuardRules.ApproachTurnDegrees) _wfApproach[k] = now = want;
        return now;
    }

    /// <summary>
    /// Squad slot <paramref name="i"/> of <paramref name="count"/>: its row (front = 0) and place in it (at most <see cref="WarfrontGuardRules.MaxRows"/> rows: a bigger crowd of squads widens the rows), rows staggered half a squad.
    /// The offset's forward and sideways parts are shortened (whole, three quarters, half, a quarter, none; forward first) until the place is on the walking graph, reachable in a straight line from the point and
    /// <see cref="SlotClearance"/> off the walls, else the best clearance found.
    /// </summary>
    Vector3 SlotPlace(Vector3 point, Vector2 dir, int i, int count, float radius)
    {
        int perRow = Math.Max(WarfrontGuardRules.SquadsPerRow, (count + WarfrontGuardRules.MaxRows - 1) / WarfrontGuardRules.MaxRows);
        int row = i / perRow, col = i % perRow;
        int inRow = Math.Min(perRow, count - row * perRow);
        float lateral = (col - (inRow - 1) * 0.5f) * WarfrontGuardRules.SquadSpacing + (row % 2 == 1 ? WarfrontGuardRules.SquadSpacing * 0.5f : 0f);
        float forward = WarfrontGuardRules.FrontShare * radius - row * WarfrontGuardRules.RowSpacing;
        var side = new Vector2(-dir.Y, dir.X);
        var origin = Ground(point, point);
        if (_enemies.Walk is not { } walk) return Ground(point + new Vector3(dir * forward + side * lateral, 0f), point);
        float bestEdge = -1f;
        var best = Vector2.Zero;
        for (int fs = 4; fs >= 0; fs--)
            for (int ls = 4; ls >= 0; ls--)
            {
                var offset = dir * (forward * fs * 0.25f) + side * (lateral * ls * 0.25f);
                var q = new Vector2(origin.X + offset.X, origin.Y + offset.Y);
                int node = walk.NodeIn(walk.ColumnOf(q.X, q.Y), origin.Z, 1.5f);
                if (node < 0 || (offset != Vector2.Zero && !walk.Graph.CanWalk(origin, offset))) continue;
                float edge = walk.EdgeDistance(node);
                if (edge >= SlotClearance) return Ground(origin + new Vector3(offset, 0f), origin);
                if (edge > bestEdge) { bestEdge = edge; best = offset; }
            }
        return Ground(origin + new Vector3(best, 0f), origin);
    }

    /// <summary>The metres a squad's place keeps from a wall or ledge where the ground allows it.</summary>
    const float SlotClearance = 2.5f;

    /// <summary>A squad's ten posts about its slot: two ranks of five abreast facing <paramref name="dir"/>, the second rank behind.</summary>
    void Posts(Vector3 slot, Vector2 dir, Span<Vector3> posts)
    {
        var side = new Vector2(-dir.Y, dir.X);
        for (int m = 0; m < GuardSim.SquadSize; m++)
        {
            int rank = m / 5, col = m % 5;
            var o = side * ((col - 2) * WarfrontGuardRules.ManSpacing) - dir * (rank * WarfrontGuardRules.RankSpacing);
            var p = new Vector3(slot.X + o.X, slot.Y + o.Y, slot.Z);
            posts[m] = _enemies.Nav is { } nav && nav.Nearest(p, 1.2f, 2f) is var node && node >= 0 ? nav.Position(node) : slot;
        }
    }
}
