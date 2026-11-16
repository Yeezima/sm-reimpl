namespace SmSim.Extras.Warfront.Results;

/// <summary>The squad's summed figures on a row.</summary>
public sealed record WarfrontSquadTotals(int Members, int Kills, int Assists, int Knockouts, int Revives, int Captures);

/// <summary>A squad's best retainer and why (<see cref="Reason"/>, e.g. "most kills: 212 (9 elite)").</summary>
public sealed record WarfrontTopRetainer(string Name, string ClassName, decimal Score, string Reason);

/// <summary>One ranked row: the player's own figures and score beside the squad's.</summary>
public sealed record WarfrontLeaderRow(int Rank, int JoinIndex, string Name, bool Connected, WarfrontPlayerStats Player, WarfrontSquadTotals Squad,
    decimal PlayerScore, decimal SquadScore, decimal Total, WarfrontTopRetainer? TopRetainer, IReadOnlyList<WarfrontRetainerStats>? Members = null)
{
    public int TotalKills => Player.Kills + Squad.Kills;
    public int TotalRevives => Player.Revives + Squad.Revives;
    public int TotalCaptures => Player.Captures + Squad.Captures;
}

/// <summary>Scoring and ranking (plan section 7). Pure and deterministic: the same input and weights always give the same order.</summary>
public static class WarfrontLeaderboard
{
    static decimal Pts(int kills, int elite, int boss, decimal k, decimal e, decimal b)
    {
        elite = Math.Max(0, elite); boss = Math.Max(0, boss);
        int normal = Math.Max(0, kills - elite - boss);
        return normal * k + elite * e + boss * b;
    }

    public static decimal PlayerScore(WarfrontPlayerStats p, WarfrontScoreWeights w) =>
        Pts(p.Kills, p.EliteKills, p.BossKills, w.Kill, w.EliteKill, w.BossKill) + Math.Max(0, p.Assists) * w.Assist + Math.Max(0, p.Captures) * w.Capture + Math.Max(0, p.Revives) * w.Revive;

    /// <summary>A retainer's contribution under the Squad* weights. Knockouts add nothing.</summary>
    public static decimal RetainerScore(WarfrontRetainerStats r, WarfrontScoreWeights w) =>
        Pts(r.Kills, r.EliteKills, r.BossKills, w.SquadKill, w.SquadEliteKill, w.SquadBossKill) + Math.Max(0, r.Assists) * w.SquadAssist
        + Math.Max(0, r.Revives) * w.SquadRevive + Math.Max(0, r.Captures) * w.SquadCapture;

    public static WarfrontSquadTotals Totals(IReadOnlyList<WarfrontRetainerStats> squad) =>
        new(squad.Count, squad.Sum(r => r.Kills), squad.Sum(r => r.Assists), squad.Sum(r => r.Knockouts), squad.Sum(r => r.Revives), squad.Sum(r => r.Captures));

    /// <summary>The squad's best retainer: highest score, then more kills, fewer knockouts, name, slot. Null for an empty squad.</summary>
    public static WarfrontTopRetainer? TopRetainer(IReadOnlyList<WarfrontRetainerStats> squad, WarfrontScoreWeights w)
    {
        if (squad.Count == 0) return null;
        var best = squad.Select(r => (R: r, S: RetainerScore(r, w)))
            .OrderByDescending(x => x.S).ThenByDescending(x => x.R.Kills).ThenBy(x => x.R.Knockouts).ThenBy(x => x.R.Name, StringComparer.Ordinal).ThenBy(x => x.R.Slot).First();
        return new WarfrontTopRetainer(best.R.Name, best.R.ClassName, best.S, Reason(best.R, best.S, w));
    }

    /// <summary>The biggest scoring component names the reason; kills first on a tie, then revives, captures, assists.</summary>
    public static string Reason(WarfrontRetainerStats r, decimal score, WarfrontScoreWeights w)
    {
        if (score <= 0) return "no score yet";
        decimal kills = Pts(r.Kills, r.EliteKills, r.BossKills, w.SquadKill, w.SquadEliteKill, w.SquadBossKill);
        decimal revives = Math.Max(0, r.Revives) * w.SquadRevive, caps = Math.Max(0, r.Captures) * w.SquadCapture, assists = Math.Max(0, r.Assists) * w.SquadAssist;
        decimal top = Math.Max(Math.Max(kills, revives), Math.Max(caps, assists));
        if (kills == top)
        {
            var extra = new List<string>();
            if (r.EliteKills > 0) extra.Add(r.EliteKills + " elite");
            if (r.BossKills > 0) extra.Add(r.BossKills + " boss");
            return "most kills: " + r.Kills + (extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : "");
        }
        if (revives == top) return "most revives: " + r.Revives;
        if (caps == top) return "most captures: " + r.Captures;
        return "most assists: " + r.Assists;
    }

    /// <summary>Scores and ranks every contributor, connected or not. Rank is the 1-based position; the order is total (ties end on join index, then name).</summary>
    public static IReadOnlyList<WarfrontLeaderRow> Rank(IEnumerable<WarfrontContributor> contributors, WarfrontScoreWeights? weights = null)
    {
        var w = weights ?? WarfrontScoreWeights.Default;
        var rows = contributors.Select(c =>
        {
            var ps = PlayerScore(c.Player, w);
            var ss = c.Squad.Sum(r => RetainerScore(r, w));
            return new WarfrontLeaderRow(0, c.Player.JoinIndex, c.Player.Name, c.Player.Connected, c.Player, Totals(c.Squad), ps, ss, ps + ss, TopRetainer(c.Squad, w), c.Squad);
        }).ToList();
        rows.Sort((a, b) => Compare(a, b, w));
        for (int i = 0; i < rows.Count; i++) rows[i] = rows[i] with { Rank = i + 1 };
        return rows;
    }

    static int Compare(WarfrontLeaderRow a, WarfrontLeaderRow b, WarfrontScoreWeights w)
    {
        int c = b.Total.CompareTo(a.Total);
        if (c != 0) return c;
        foreach (var t in w.TieBreaks)
        {
            c = t switch
            {
                WarfrontTieBreak.Captures => b.TotalCaptures.CompareTo(a.TotalCaptures),
                WarfrontTieBreak.Revives => b.TotalRevives.CompareTo(a.TotalRevives),
                WarfrontTieBreak.FewerDeaths => a.Player.Deaths.CompareTo(b.Player.Deaths),
                _ => a.JoinIndex.CompareTo(b.JoinIndex),
            };
            if (c != 0) return c;
        }
        c = a.JoinIndex.CompareTo(b.JoinIndex);
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }
}
