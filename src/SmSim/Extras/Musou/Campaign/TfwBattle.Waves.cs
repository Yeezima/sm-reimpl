using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Ork waves (expansion S6; the brief: "Orks muster at their strongholds and leave in large waves: peaks every 3-5 min, lulls of 90-150 s"; sizes 200 / 500 / 1,000 or more at 70 / 25 / 5 %). A wave musters at
/// the Ork stronghold farthest from every human, leaves at its peak for the Imperial position farthest from every human (one of the two farthest, by the dice) and keeps that destination whatever the players do;
/// only the loss of its destination sends it elsewhere. With no Imperial position it waits at its stronghold. No UI names its destination.
/// </summary>
public sealed partial class TfwBattle
{
    TfwArmy? _muster;

    /// <summary>The owner's "base is a target" (2026-11-08): with no Imperial position a wave marches on the base (false: it waits, as expansion S6 did; tests of the old flow).</summary>
    public bool BaseTargets { get; set; } = true;
    /// <summary>Test aid: the tick the first wave reached its destination (-1: not yet).</summary>
    public int FirstArrival { get; private set; } = -1;
    int _nextDeparture = -1;
    uint _waveRolls;

    /// <summary>Waves sent so far, and the tick each left (tests).</summary>
    public int WavesSent { get; private set; }
    public List<int> Departures { get; } = new();
    /// <summary>Test aid: called when a wave leaves, with the army (its goal is the committed destination).</summary>
    public Action<TfwArmy>? OnWaveLeft { get; set; }
    /// <summary>The wave gathering now (null: none).</summary>
    public TfwArmy? Mustering => _muster;

    /// <summary>The battle's dice for the waves: seeded by its places, so a battle goes the same each time.</summary>
    float WaveRoll() => MusouSim.Unit(MusouSim.Mix(MusouSim.Mix((uint)BitConverter.SingleToInt32Bits(_layout.LandingZone.X) ^ (uint)BitConverter.SingleToInt32Bits(_layout.Hq.Y), 0x77617665u), _waveRolls++));

    int Seconds(float min, float max) => Ticks(min + (max - min) * WaveRoll());

    /// <summary>Each tick: the first lull, the muster before each peak, the departure (or the wait for a destination), the next peak.</summary>
    void StepWaves()
    {
        if (Won) return;
        if (_nextDeparture < 0) _nextDeparture = _now + Seconds(TfwArmyRules.LullMin, TfwArmyRules.LullMax);
        if (_muster is null && _now >= _nextDeparture - Ticks(TfwArmyRules.MusterSeconds)) StartMuster();
        if (_muster is not { } m) return;
        float rate = m.Planned / MathF.Max(1f, TfwArmyRules.MusterSeconds) * Dt;
        float brought = OrkAggregates ? m.Far : m.Mustered;   // real Ork armies (2026-11-09): the muster's men are made real at once, so its count is what it brought
        if (brought < m.Planned)
        {
            float add = MathF.Min(rate, m.Planned - brought);
            m.Far += add;
            m.Mustered += add;
            m.FarElites += add * m.EliteShare;
        }
        bool lost = !Stronghold(m.From);   // its stronghold was taken: it goes with what it has
        if (_now < m.Leaves && !lost) return;
        if (!TryDepart(m)) m.Leaves = _now + Ticks(TfwArmyRules.WaitRecheck);
    }

    /// <summary>A wave begins to gather at the Ork stronghold farthest from every human: its size by the dice, and the Orks who came home since.</summary>
    void StartMuster()
    {
        int place = -1;
        float best = float.NegativeInfinity;
        for (int p = 0; p < Places; p++)
        {
            if (p == BasePlace || !Stronghold(p)) continue;
            float d = NearestHuman(PlacePos(p));
            if (d > best) { best = d; place = p; }
        }
        if (place < 0 || _armies.Count >= TfwArmyRules.MaxArmies) return;   // no room for an army: no dice rolled, the Orks home stay banked
        int size = TfwArmyRules.WaveSize(WaveRoll(), WaveRoll()) + _orkHome;
        _orkHome = 0;
        var army = NewArmy(TfwArmySide.Ork, TfwArmyOrder.Muster, size, EliteShare);
        if (army is null) return;
        var at = Ground(PlacePos(place), PlacePos(place));
        army.Yaw = FaceCentre(at);
        army.Anchor = at + new Vector3(TfwArmyLayout.Forward(army.Yaw) * (TfwArmyLayout.Depth(army.Side, army.Width, size) * 0.5f), 0f);
        army.From = army.To = place;
        army.Goal = StrongholdGoal(place);
        army.Planned = size;
        army.Leaves = Math.Max(_now, _nextDeparture);
        _muster = army;
    }

    /// <summary>The flat distance from <paramref name="p"/> to the nearest human (infinite with none).</summary>
    float NearestHuman(Vector3 p)
    {
        float best = float.PositiveInfinity;
        foreach (var h in _armyHumans) best = MathF.Min(best, FlatDistance(h, p));
        return best;
    }

    /// <summary>The wave leaves for the Imperial position farthest from every human (one of the two farthest, by the dice): committed. False, and it waits, with no Imperial position.</summary>
    bool TryDepart(TfwArmy m)
    {
        int target = PickWaveTarget();
        if (target < 0) return false;
        Redirect(m, GoalPlace(target));
        m.Order = TfwArmyOrder.March;
        m.Goal = m.Post = target;
        m.Start = Math.Max(1, m.Count);
        m.Planned = 0;
        _muster = null;
        WavesSent++;
        Departures.Add(_now);
        _nextDeparture = _now + Seconds(TfwArmyRules.PeakMin, TfwArmyRules.PeakMax);
        OnWaveLeft?.Invoke(m);
        return true;
    }

    readonly List<(float D, int K)> _targets = new();

    /// <summary>
    /// The destination of a wave leaving now: the Imperial positions by their distance from the nearest human, farthest first, one of the first two by the dice; with none, the Imperial base (the owner's
    /// "base is a target", 2026-11-08: <see cref="LandingGoal"/>), so the players must push out.
    /// </summary>
    int PickWaveTarget()
    {
        _targets.Clear();
        for (int k = 0; k < Positions; k++) if (Imperial(k)) _targets.Add((NearestHuman(_layout.Positions[k]), k));
        if (_targets.Count == 0) return BaseTargets ? LandingGoal : -1;
        _targets.Sort((x, y) => y.D != x.D ? y.D.CompareTo(x.D) : x.K.CompareTo(y.K));
        int draw = Math.Min(TfwArmyRules.FarthestDraw, _targets.Count);
        return _targets[Math.Min(draw - 1, (int)(WaveRoll() * draw))].K;
    }

    /// <summary>Test aid: the next wave musters now and leaves at once (its size by the dice, or <paramref name="size"/> when above 0).</summary>
    public TfwArmy? SendWaveForTests(int size = 0)
    {
        _nextDeparture = _now;
        if (_muster is null) StartMuster();
        if (_muster is not { } m) return null;
        if (size > 0) { m.Planned = size; }
        m.Far = m.Planned;
        m.FarElites = m.Planned * m.EliteShare;
        var army = m;
        return TryDepart(m) ? army : null;
    }
}
