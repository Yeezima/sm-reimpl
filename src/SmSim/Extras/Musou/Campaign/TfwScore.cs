namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The run's score (owner's item 17; addendum to the pass 2 design), held by the server. A human's own credited KO earns the type's points times the chain's multiplier, rounded: the 5-wave run's rule and
/// values (<see cref="MusouRules.ChainSeconds"/>, <see cref="MusouRules.ChainStep"/>, <see cref="MusouRules.ChainMax"/>, <see cref="MusouTypeSpec.Points"/>), which <see cref="MusouDirector.OnKill"/> no longer reaches in a
/// war: its phase stays Waiting. A map's total is the points earned on it; the run's total adds every map, and a map left, lost or extracted from still banks. No bonus for a win (the addendum's first cut).
/// The chain and the map's figures start over at a landing. The AI Marines, the Guardsmen and fire support score nothing: the World asks only for a human, and those name none.
/// </summary>
public sealed class TfwScore
{
    sealed class Entry
    {
        public uint MapPoints, RunTotal, LastKoTick;
        public int Kos, BestChain, Chain;
        public bool AnyKo;
        public readonly List<TfwMapScore> History = new();
    }

    readonly Dictionary<int, Entry> _players = new();
    readonly List<int> _order = new();
    TfwLane _lane;
    byte _node;

    /// <summary>Whether a map is under way (begun and not banked).</summary>
    public bool MapOpen { get; private set; }

    /// <summary>Humans with a score, by id.</summary>
    public IReadOnlyList<int> Players => _order;

    /// <summary>A new run: nothing earned.</summary>
    public void NewRun()
    {
        _players.Clear();
        _order.Clear();
        MapOpen = false;
    }

    Entry Of(int player)
    {
        if (_players.TryGetValue(player, out var e)) return e;
        _players[player] = e = new Entry();
        int at = _order.BinarySearch(player);
        _order.Insert(~at, player);
        return e;
    }

    /// <summary>A landing: <paramref name="humans"/> start the map (<paramref name="lane"/>, node <paramref name="node"/>) with no points and no chain.</summary>
    public void BeginMap(TfwLane lane, byte node, IReadOnlyList<int> humans)
    {
        _lane = lane;
        _node = node;
        MapOpen = true;
        foreach (var e in _players.Values) { e.MapPoints = 0; e.Kos = e.BestChain = e.Chain = 0; e.AnyKo = false; }
        foreach (int id in humans) Of(id);
    }

    /// <summary>What one KO earns at chain <paramref name="chain"/> (0: the first of a chain): the type's points times min(max, 1 + step x chain), rounded as the director rounds.</summary>
    public static int Points(MusouRules rules, MusouEnemyType type, int chain) =>
        (int)MathF.Round(rules.Of(type).Points * MathF.Min(rules.ChainMax, 1f + rules.ChainStep * chain));

    /// <summary>A human's KO on <paramref name="tick"/>: a no-op outside a map; a KO within the chain's window of the same player's last one chains.</summary>
    public void Note(in MusouKill kill, MusouRules rules, int tickRate, uint tick)
    {
        if (!MapOpen) return;
        var e = Of(kill.Player);
        bool chained = e.AnyKo && tick - e.LastKoTick <= (uint)MathF.Round(rules.ChainSeconds * tickRate);
        e.Chain = chained ? e.Chain + 1 : 0;
        int points = Points(rules, kill.Type, e.Chain);
        e.MapPoints += (uint)points;
        e.RunTotal += (uint)points;
        e.Kos++;
        e.BestChain = Math.Max(e.BestChain, e.Chain + 1);
        e.LastKoTick = tick;
        e.AnyKo = true;
    }

    /// <summary>The map under way ends with <paramref name="result"/>: each player's row joins its history. Nothing when no map is open, so the run's end can always call it.</summary>
    public void Bank(TfwMapResult result)
    {
        if (!MapOpen) return;
        MapOpen = false;
        foreach (var e in _players.Values)
            e.History.Add(new TfwMapScore(_lane, _node, result, Saturate(e.Kos), Saturate(e.BestChain), e.MapPoints));
    }

    static ushort Saturate(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);

    /// <summary>The figures tag 12 carries for <paramref name="player"/>.</summary>
    public TfwScoreOwn OwnOf(int player) =>
        _players.TryGetValue(player, out var e) ? new TfwScoreOwn(e.MapPoints, e.RunTotal, Saturate(e.Kos), Saturate(e.BestChain)) : default;

    /// <summary>The player's finished maps, oldest first.</summary>
    public IReadOnlyList<TfwMapScore> HistoryOf(int player) => _players.TryGetValue(player, out var e) ? e.History : [];

    /// <summary>The player's run total.</summary>
    public uint TotalOf(int player) => _players.TryGetValue(player, out var e) ? e.RunTotal : 0;
}
