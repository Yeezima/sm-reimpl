using System.Globalization;

namespace SmSim.Extras.Warfront.Results;

/// <summary>One honor roll line: a title, who holds it, the figure and the run it came from (the record's stem, which is its UTC end time and result).</summary>
public sealed record WarfrontHonorEntry(string Title, string Holder, string Value, string Run);

/// <summary>A name's totals over the archive (display names only; two people with the same name share a line).</summary>
public sealed record WarfrontLifetimeRow(string Name, int Runs, decimal TotalScore, decimal BestScore);

/// <summary>The catalogue of every archived run: counts, the records and the lifetime table. A view of the archive only; a new run never reads it.</summary>
public sealed record WarfrontHonorRoll(int Runs, int Won, int Lost, int Abandoned, IReadOnlyList<WarfrontHonorEntry> Entries, IReadOnlyList<WarfrontLifetimeRow> Players)
{
    static string N(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Builds the roll from <paramref name="runs"/> (stem and record). Equal figures go to the earlier stem; the result does not depend on the input order.</summary>
    public static WarfrontHonorRoll Build(IEnumerable<(string Stem, WarfrontRunRecord Record)> runs)
    {
        var all = runs.OrderBy(r => r.Stem, StringComparer.Ordinal).ToList();
        var entries = new List<WarfrontHonorEntry>();

        void Best(string title, Func<WarfrontRunRecord, WarfrontLeaderRow, decimal> value, Func<decimal, string> text)
        {
            (string Stem, string Holder, decimal V)? best = null;
            foreach (var (stem, rec) in all)
                foreach (var row in rec.Rows)
                {
                    var v = value(rec, row);
                    if (v > 0 && (best is null || v > best.Value.V)) best = (stem, row.Name, v);
                }
            if (best is { } b) entries.Add(new WarfrontHonorEntry(title, b.Holder, text(b.V), b.Stem));
        }
        Best("Highest score", (_, r) => r.Total, v => N(v));
        Best("Most kills by a player", (_, r) => r.Player.Kills, v => N(v));
        Best("Most squad kills", (_, r) => r.Squad.Kills, v => N(v));
        Best("Most revives", (_, r) => r.TotalRevives, v => N(v));
        Best("Most captures", (_, r) => r.TotalCaptures, v => N(v));

        (string Stem, string Holder, decimal V)? bestRet = null;
        foreach (var (stem, rec) in all)
            foreach (var row in rec.Rows)
                foreach (var m in row.Members ?? Array.Empty<WarfrontRetainerStats>())
                {
                    var v = WarfrontLeaderboard.RetainerScore(m, rec.Weights);
                    if (v > 0 && (bestRet is null || v > bestRet.Value.V)) bestRet = (stem, m.Name + " (" + row.Name + "'s squad)", v);
                }
        if (bestRet is { } br) entries.Add(new WarfrontHonorEntry("Best retainer", br.Holder, N(br.V), br.Stem));

        (string Stem, int Secs)? fastest = null;
        foreach (var (stem, rec) in all)
            if (rec.Won && rec.DurationSeconds > 0 && (fastest is null || rec.DurationSeconds < fastest.Value.Secs)) fastest = (stem, rec.DurationSeconds);
        if (fastest is { } f) entries.Add(new WarfrontHonorEntry("Fastest victory", "the squad", TimeSpan.FromSeconds(f.Secs).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture), f.Stem));

        var life = new Dictionary<string, (int Runs, decimal Total, decimal Best)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, rec) in all)
            foreach (var row in rec.Rows)
            {
                life.TryGetValue(row.Name, out var cur);
                life[row.Name] = (cur.Runs + 1, cur.Total + row.Total, Math.Max(cur.Best, row.Total));
            }
        var players = life.Select(kv => new WarfrontLifetimeRow(kv.Key, kv.Value.Runs, kv.Value.Total, kv.Value.Best))
            .OrderByDescending(p => p.TotalScore).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new WarfrontHonorRoll(all.Count, all.Count(r => r.Record.Result == "won"), all.Count(r => r.Record.Result == "lost"), all.Count(r => r.Record.Result == "abandoned"), entries, players);
    }

    /// <summary>The roll as plain text.</summary>
    public string Text()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("WARFRONT HONOR ROLL\n").Append(Runs).Append(" runs: ").Append(Won).Append(" won, ").Append(Lost).Append(" lost, ").Append(Abandoned).Append(" abandoned\n");
        foreach (var e in Entries) sb.Append('\n').Append(e.Title).Append(": ").Append(e.Holder).Append(" - ").Append(e.Value);
        if (Players.Count > 0) sb.Append("\n\nTOTAL SCORE\n");
        foreach (var p in Players) sb.Append(p.Name).Append(" - ").Append(N(p.TotalScore)).Append(" over ").Append(p.Runs).Append(" runs (best ").Append(N(p.BestScore)).Append(")\n");
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
