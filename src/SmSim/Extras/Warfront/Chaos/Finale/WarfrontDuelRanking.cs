using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;

namespace SmSim.Extras.Warfront.Chaos.Finale;

/// <summary>
/// The duel queue's order (Chaos Warfront C5; brief: "ranked by total campaign score with a stable tiebreak"): <see cref="WarfrontLeaderboard.Rank"/> over the campaign's contributors, built as
/// <see cref="WarfrontResults.Build"/> builds them (the campaign's join order is the join index; the stats ledger's own figures, which the battle feeds live, so this battle's kills count; the squad's
/// service records when given), then only the living (the caller's test: alive and not downed, connected, deployed, a human). Ties end as the leaderboard's do: the weights' tie-breaks, join index, name.
/// Pure and deterministic.
/// </summary>
public static class WarfrontDuelRanking
{
    /// <summary>The ranked keys of the living.</summary>
    public static IReadOnlyList<ulong> Queue(WarfrontCampaign campaign, Func<ulong, bool> living, Func<ulong, IReadOnlyList<WarfrontRetainerStats>>? squadOf = null, WarfrontScoreWeights? weights = null)
    {
        var entries = new List<(ulong Key, WarfrontContributor Who)>();
        int index = 0;
        foreach (ulong key in campaign.JoinOrder)
        {
            var p = campaign.Player(key);
            string name = WarfrontResults.Clean(!string.IsNullOrWhiteSpace(p?.Name) ? p!.Name : null, "Player " + (index + 1));
            var stats = campaign.Stats.ToPlayerStats(key) with { Name = name, Connected = campaign.IsConnected(key), JoinIndex = index };
            entries.Add((key, new WarfrontContributor(stats, squadOf?.Invoke(key) ?? [])));
            index++;
        }
        return Queue(entries, living, weights);
    }

    /// <summary>The same over given contributors (the tests'): each key's row, ranked, the living kept.</summary>
    public static IReadOnlyList<ulong> Queue(IReadOnlyList<(ulong Key, WarfrontContributor Who)> entries, Func<ulong, bool> living, WarfrontScoreWeights? weights = null)
    {
        var byJoin = new Dictionary<int, ulong>();
        foreach (var (key, who) in entries) byJoin.TryAdd(who.Player.JoinIndex, key);
        var rows = WarfrontLeaderboard.Rank(entries.Select(e => e.Who), weights);
        var queue = new List<ulong>();
        foreach (var row in rows)
            if (byJoin.TryGetValue(row.JoinIndex, out ulong key) && living(key) && !queue.Contains(key)) queue.Add(key);
        return queue;
    }
}
