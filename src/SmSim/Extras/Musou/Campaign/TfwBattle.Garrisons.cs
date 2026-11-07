using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Guardsmen of the battle flow (expansion S6; the brief: reinforcements leave the base every 60 s and split over the points by need; the stationed garrison is capped at 200 a point, armies on the move may
/// exceed it; a destination lost in transit sends them to another Imperial point, else to the base). The garrison of a position is one Guard army holding it; the field squads (<see cref="StepSquads"/>) go on as
/// before beside them.
/// </summary>
public sealed partial class TfwBattle
{
    int _reinforceTicks, _guardHome, _orkHome;

    /// <summary>Reinforcements sent so far (tests).</summary>
    public int ReinforcementsSent { get; private set; }
    /// <summary>Guardsmen at the base waiting to go with the next reinforcement (survivors home), and Orks home waiting for the next wave.</summary>
    public int GuardsAtBase => _guardHome;
    public int OrksAwaitingWave => _orkHome;

    /// <summary>The army holding position <paramref name="k"/> (null: none).</summary>
    public TfwArmy? GarrisonOf(int k)
    {
        foreach (var a in _armies) if (a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.Hold && a.Goal == k) return a;
        return null;
    }

    /// <summary>Guardsmen stationed on position <paramref name="k"/> (its garrison's count).</summary>
    public int Stationed(int k) => GarrisonOf(k)?.Count ?? 0;

    /// <summary>Guardsmen on their way to position <paramref name="k"/>.</summary>
    int Inbound(int k)
    {
        int n = 0;
        foreach (var a in _armies) if (a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.March && a.Goal == k) n += a.Count;
        return n;
    }

    /// <summary>Position <paramref name="k"/>'s garrison, made (empty, its block on the point, facing the HQ) when it has none.</summary>
    TfwArmy? EnsureGarrison(int k)
    {
        if (GarrisonOf(k) is { } held) return held;
        var g = NewArmy(TfwArmySide.Guard, TfwArmyOrder.Hold, TfwArmyRules.GarrisonCap, 0f);
        if (g is null) return null;
        var point = Ground(_layout.Positions[k], _layout.Positions[k]);
        g.Yaw = YawTo(point, _layout.Hq);
        g.Anchor = point + new Vector3(TfwArmyLayout.Forward(g.Yaw) * (TfwArmyLayout.Depth(g.Side, g.Width, TfwArmyRules.GarrisonCap) * 0.5f), 0f);
        g.Goal = g.Post = k;
        g.From = g.To = k;
        return g;
    }

    /// <summary>Every <see cref="TfwArmyRules.ReinforceSeconds"/>: <see cref="TfwArmyRules.ReinforceMen"/> and the men home leave the base in whole squads, shared over the Imperial positions by what each lacks.</summary>
    void StepReinforcements()
    {
        if (!_guardsmen || Won || ++_reinforceTicks < Ticks(TfwArmyRules.ReinforceSeconds)) return;
        _reinforceTicks = 0;
        Span<int> need = stackalloc int[Positions];
        int total = 0;
        for (int k = 0; k < Positions; k++)
        {
            need[k] = Imperial(k) ? Math.Max(0, TfwArmyRules.GarrisonCap - Stationed(k) - Inbound(k)) : 0;
            total += need[k];
        }
        int men = TfwArmyRules.ReinforceMen + _guardHome;
        if (total == 0) return;   // nothing lacks: the men home wait
        int sent = 0;
        for (int k = 0; k < Positions && sent < men; k++)
        {
            if (need[k] == 0) continue;
            int share = (int)MathF.Round(men * need[k] / (float)total / GuardSim.SquadSize) * GuardSim.SquadSize;
            share = Math.Min(Math.Max(share, GuardSim.SquadSize), Math.Min(men - sent, (need[k] + GuardSim.SquadSize - 1) / GuardSim.SquadSize * GuardSim.SquadSize));
            if (share <= 0) continue;
            var army = NewArmy(TfwArmySide.Guard, TfwArmyOrder.March, share, 0f);
            if (army is null) break;
            army.Far = share;
            army.Start = share;
            army.Anchor = Ground(_layout.LandingZone, _layout.LandingZone);
            army.Yaw = YawTo(_layout.LandingZone, _layout.Positions[k]);
            army.From = army.To = BasePlace;
            army.Goal = army.Post = k;
            Redirect(army, k);
            army.Order = TfwArmyOrder.March;
            sent += share;
            ReinforcementsSent++;
        }
        _guardHome = Math.Max(0, Math.Min(_guardHome, men - sent));   // the men home not sent wait; the base's own share is not banked
    }

    /// <summary>
    /// A Guard army reaches position <paramref name="k"/>: it joins the garrison up to the cap (its aggregate first, then whole squads); the rest goes on to the Imperial position that lacks most, else home.
    /// </summary>
    void MergeIntoGarrison(TfwArmy a, int k)
    {
        var g = EnsureGarrison(k);
        if (g is null) { RedirectGuards(a); return; }
        int room = TfwArmyRules.GarrisonCap - g.Count;
        int far = Math.Min(room, a.FarCount);
        if (far > 0)
        {
            g.Far += far;
            a.Far = MathF.Max(0f, a.Far - far);
            room -= far;
        }
        for (int j = a.Squads.Count - 1; j >= 0; j--)
        {
            if (a.SquadLiving[j] > room) continue;
            int s = a.Squads[j], block = (g.SlotBase + GuardSim.SquadSize - 1) / GuardSim.SquadSize;
            room -= a.SquadLiving[j];
            g.Squads.Add(s); g.SquadBlocks.Add(block); g.SquadLiving.Add(a.SquadLiving[j]);
            g.SlotBase = (block + 1) * GuardSim.SquadSize;
            g.Real += a.SquadLiving[j];
            a.Real -= a.SquadLiving[j];
            _squadArmy[s] = g.Id;
            a.Squads.RemoveAt(j); a.SquadBlocks.RemoveAt(j); a.SquadLiving.RemoveAt(j);
            OrderSquad(g, g.Squads.Count - 1);
        }
        g.Start = Math.Max(g.Start, g.Count);   // reinforced: its losses count from here
        if (a.Count == 0) { _armies.Remove(a); return; }
        a.Start = a.Count;
        int next = MostShort(k);
        if (next >= 0) { a.Goal = a.Post = next; Redirect(a, next); }
        else { a.Goal = LandingGoal; Redirect(a, BasePlace); }
        a.Order = TfwArmyOrder.March;
        OrderSquads(a);
    }

    /// <summary>The Imperial position (other than <paramref name="except"/>) that lacks the most men; -1 with none lacking.</summary>
    int MostShort(int except)
    {
        int best = -1, most = 0;
        for (int k = 0; k < Positions; k++)
        {
            if (k == except || !Imperial(k)) continue;
            int lack = TfwArmyRules.GarrisonCap - Stationed(k) - Inbound(k);
            if (lack > most) { most = lack; best = k; }
        }
        return best;
    }

    /// <summary>The Imperial position nearest <paramref name="at"/> (flat; other than <paramref name="except"/>); -1 with none.</summary>
    int NearestImperial(Vector3 at, int except = -1)
    {
        int best = -1;
        float bestD = float.PositiveInfinity;
        for (int k = 0; k < Positions; k++)
        {
            if (k == except || !Imperial(k)) continue;
            float d = FlatDistance(at, _layout.Positions[k]);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    /// <summary>A Guard army whose destination is no longer Imperial: to the nearest other Imperial position, else to the base.</summary>
    void RedirectGuards(TfwArmy a)
    {
        int next = NearestImperial(a.Middle, a.Goal);
        if (next >= 0) { a.Goal = a.Post = next; Redirect(a, next); }
        else { a.Goal = LandingGoal; Redirect(a, BasePlace); }
        if (a.Order != TfwArmyOrder.Retreat) a.Order = TfwArmyOrder.March;
        OrderSquads(a);
    }
}

public sealed partial class TfwBattle
{
    /// <summary>Test aid: a reinforcement of <paramref name="men"/> leaves the base for position <paramref name="k"/> now (null with no room for an army).</summary>
    public TfwArmy? ReinforceForTests(int k, int men)
    {
        var army = NewArmy(TfwArmySide.Guard, TfwArmyOrder.March, men, 0f);
        if (army is null) return null;
        army.Far = men; army.Start = men;
        army.Anchor = Ground(_layout.LandingZone, _layout.LandingZone);
        army.Yaw = YawTo(_layout.LandingZone, _layout.Positions[k]);
        army.From = army.To = BasePlace;
        army.Goal = army.Post = k;
        Redirect(army, k);
        army.Order = TfwArmyOrder.March;
        return army;
    }

    /// <summary>Test aid: an army of <paramref name="men"/> standing at <paramref name="anchor"/> with the given order and goal (a far one until a human comes near).</summary>
    public TfwArmy? AddArmyForTests(TfwArmySide side, TfwArmyOrder order, int goal, int men, Vector3 anchor, float yaw = 0f, float eliteShare = 0f)
    {
        var army = NewArmy(side, order, men, eliteShare);
        if (army is null) return null;
        army.Far = men; army.Start = men;
        army.FarElites = side == TfwArmySide.Ork ? men * eliteShare : 0f;
        army.Anchor = anchor; army.Yaw = yaw;
        army.Goal = army.Post = goal;
        int place = GoalPlace(goal);
        army.From = army.To = place is >= 0 and < Places ? place : BasePlace;
        return army;
    }

    /// <summary>Test aid: position <paramref name="k"/> falls to the Orks now, as a retake does.</summary>
    public void LosePointForTests(int k)
    {
        if (_objectives[k].State != TfwPositionState.Secured) return;
        _objectives[k] = _objectives[k] with { State = TfwPositionState.Held, Progress = 0f };
        Secured--;
        SendAgain(k);
        if (Armies) ArmiesPointFell(k);
    }

    /// <summary>Test aid: the army's men are lost from its aggregate (a far clash's losses), elites first when <paramref name="leadersFirst"/>.</summary>
    public static void LoseForTests(TfwArmy a, int men, bool leadersFirst = false)
    {
        int lost = Math.Min(men, a.FarCount);
        if (leadersFirst) a.FarElites = MathF.Max(0f, a.FarElites - lost);
        else if (a.Far > 0f) a.FarElites = MathF.Max(0f, a.FarElites * (1f - lost / a.Far));
        a.Far -= lost;
        a.Lost += lost;
    }
}

public sealed partial class TfwBattle
{
    /// <summary>Test aid (captures, the server's <c>SM_TFW_ARMY_TEST</c>): position <paramref name="k"/> is secured now, its Orks within its ring and 5 m gone.</summary>
    public void ClearAndSecureForTests(int k)
    {
        if (_objectives[k].State == TfwPositionState.Secured) return;
        Span<ushort> ids = stackalloc ushort[256];
        int n = _enemies.Query(_layout.Positions[k], _layout.Radius(k) + 5f, ids, _layout.Reach(k) + 2f);
        for (int i = 0; i < n; i++) _enemies.Remove(ids[i]);
        _objectives[k] = _objectives[k] with { State = TfwPositionState.Secured, Progress = 1f };
        Secure(k);
    }

    /// <summary>Test aid: a place <paramref name="share"/> of the way along the route from position <paramref name="k"/> to the base, and the way's flat direction there.</summary>
    public (Vector3 At, Vector2 Way) RouteToBaseForTests(int k, float share)
    {
        var way = WayBetween(k, BasePlace);
        return way.Sample(way.Length * Math.Clamp(share, 0f, 1f));
    }

    /// <summary>Test aid: the army goes home now (a retreat, as its loss rule would send it).</summary>
    public void RetreatForTests(TfwArmy a) => Retreat(a);

    /// <summary>Test aid: a wave of <paramref name="size"/> leaves now for position <paramref name="target"/> (which must be Imperial), from the stronghold the muster picks.</summary>
    public TfwArmy? SendWaveForTests(int size, int target)
    {
        _nextDeparture = _now;
        if (_muster is null) StartMuster();
        if (_muster is not { } m || !Imperial(target)) return null;
        m.Planned = size; m.Far = size; m.FarElites = size * m.EliteShare;
        Redirect(m, target);
        m.Order = TfwArmyOrder.March;
        m.Goal = m.Post = target;
        m.Start = m.Count;
        m.Planned = 0;
        _muster = null;
        WavesSent++;
        Departures.Add(_now);
        _nextDeparture = _now + Seconds(TfwArmyRules.PeakMin, TfwArmyRules.PeakMax);
        return m;
    }
}
