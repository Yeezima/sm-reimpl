using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Retreats and rallies (expansion S6; the brief). Orks retreat at 50 % losses or more with no elite or boss within 100 m (so killing its leaders breaks a charge once the losses are met) and go home to the
/// nearest Ork stronghold; Guardsmen retreat at more than 50 % losses or when their position falls and go to the base. Survivors join the next wave or reinforcement. A human within 15 m of a retreating Guard
/// army rallies it, once a minute at most: it goes back to its position, or a nearer Imperial one, its losses count from its count at the rally, and for 30 s it cannot retreat (a lost position redirects it).
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>Test aid: called when an army retreats, and when a Guard army is rallied.</summary>
    public Action<TfwArmy>? OnRetreat { get; set; }
    public Action<TfwArmy>? OnRally { get; set; }

    /// <summary>Whether an Ork army has a leader: its own elites, or any elite or boss within <see cref="TfwArmyRules.LeaderRadius"/> of it.</summary>
    bool LeaderNear(TfwArmy a) => a.Led || _enemies.AnyWithin(a.Middle, TfwArmyRules.LeaderRadius, IsLeader);

    /// <summary>Twice a second: the loss rules (Orks with no leader near, Guardsmen past their no-retreat window), and a garrison's start follows its highest count.</summary>
    void StepRetreats()
    {
        for (int n = _armies.Count - 1; n >= 0; n--)
        {
            var a = _armies[n];
            if (a.Order is TfwArmyOrder.Retreat or TfwArmyOrder.Muster) continue;
            if (a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.Hold) a.Start = Math.Max(a.Start, a.Count);
            if (a.Start <= 0) continue;
            bool retreat = a.Side == TfwArmySide.Ork
                ? a.LossShare >= TfwArmyRules.RetreatLoss && !LeaderNear(a)
                : _now >= a.RetreatReady && a.LossShare > TfwArmyRules.RetreatLoss;
            if (retreat) Retreat(a);
        }
    }

    /// <summary>An army goes home: Orks to the nearest Ork stronghold, Guardsmen to the base. Its real Orks follow their places; Guard squads take the new order.</summary>
    void Retreat(TfwArmy a)
    {
        if (a.Side == TfwArmySide.Ork)
        {
            int home = HqPlace;
            float best = float.PositiveInfinity;
            for (int p = 0; p < Places; p++)
            {
                if (p == BasePlace || !Stronghold(p)) continue;
                float d = FlatDistance(a.Middle, PlacePos(p));
                if (d < best) { best = d; home = p; }
            }
            Redirect(a, home);
            a.Goal = StrongholdGoal(home);
        }
        else
        {
            if (a.Order is TfwArmyOrder.Hold) a.Post = a.Goal;
            Redirect(a, BasePlace);
            a.Goal = LandingGoal;
        }
        a.Order = TfwArmyOrder.Retreat;
        OrderSquads(a);
        OnRetreat?.Invoke(a);
    }

    /// <summary>Twice a second: a retreating Guard army with a human within <see cref="TfwArmyRules.RallyRadius"/> of its ranks is rallied, if its last rally was a minute ago and an Imperial position stands.</summary>
    void StepRallies()
    {
        foreach (var a in _armies)
        {
            if (a.Side != TfwArmySide.Guard || a.Order != TfwArmyOrder.Retreat || _now < a.RallyReady) continue;
            var middle = a.Middle;
            float reach = TfwArmyRules.RallyRadius + a.Radius;
            bool near = false;
            foreach (var h in _armyHumans) if (FlatDistance(h, middle) <= reach) { near = true; break; }
            if (!near) continue;
            int k = NearestImperial(middle);
            if (k < 0) continue;   // nothing to go back to: it goes on home (the lock is not spent)
            Rally(a, k);
        }
    }

    void Rally(TfwArmy a, int k)
    {
        a.Goal = a.Post = k;
        Redirect(a, k);
        a.Order = TfwArmyOrder.March;
        a.Start = Math.Max(1, a.Count);
        a.RallyReady = _now + Ticks(TfwArmyRules.RallyLockSeconds);
        a.RetreatReady = _now + Ticks(TfwArmyRules.NoRetreatSeconds);
        a.Rallies++;
        OrderSquads(a);
        OnRally?.Invoke(a);
    }

    /// <summary>
    /// Position <paramref name="k"/> was secured: Orks attacking or holding it lost it and go home; Ork armies on their way to it attack it on arrival (it is Imperial now).
    /// </summary>
    void ArmiesSecured(int k)
    {
        for (int n = _armies.Count - 1; n >= 0; n--)
        {
            var a = _armies[n];
            if (a.Side == TfwArmySide.Ork && AtPoint(a, k)) Retreat(a);
        }
    }

    /// <summary>
    /// Position <paramref name="k"/> fell: its garrison retreats (one in its no-retreat window goes to another Imperial position instead); Guardsmen on their way are redirected; Ork armies on their way to it
    /// go to the nearest other Imperial position, else home; Orks attacking it now hold it.
    /// </summary>
    void ArmiesPointFell(int k)
    {
        for (int n = _armies.Count - 1; n >= 0; n--)
        {
            var a = _armies[n];
            if (a.Goal != k) continue;
            if (a.Side == TfwArmySide.Guard)
            {
                if (a.Order == TfwArmyOrder.Hold && _now >= a.RetreatReady) Retreat(a);
                else if (a.Order is TfwArmyOrder.Hold or TfwArmyOrder.March) RedirectGuards(a);
                continue;
            }
            if (a.Order == TfwArmyOrder.Attack) { a.Order = TfwArmyOrder.Hold; continue; }
            if (a.Order != TfwArmyOrder.March) continue;
            int next = NearestImperial(a.Middle, k);
            if (next >= 0) { a.Goal = a.Post = next; Redirect(a, next); }
            else if (BaseTargets) { a.Goal = a.Post = LandingGoal; Redirect(a, BasePlace); }   // the owner's "base is a target" (2026-11-08)
            else Retreat(a);
        }
    }

    /// <summary>Test aid: a human-free check of every army's rules now (the far step's retreats and rallies), as twice a second does.</summary>
    public void StepRetreatsForTests() { StepRetreats(); StepRallies(); }
}
