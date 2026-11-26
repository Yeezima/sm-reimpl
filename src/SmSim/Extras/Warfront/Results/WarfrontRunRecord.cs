using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmSim.Extras.Warfront.Campaign;

namespace SmSim.Extras.Warfront.Results;

/// <summary>
/// One finished run as the end-of-run panel, the export and the honor roll see it (plan section 7). Holds no campaign key and no account id: a row is named by its display name and join
/// index only. Written next to the archived run as <c>records\&lt;stem&gt;.results.json</c>; a new run never reads it.
/// </summary>
public sealed record WarfrontRunRecord(string Result, string EndedUtc, int DurationSeconds, WarfrontScoreWeights Weights, IReadOnlyList<string> Battles, int RollsTotal, int RollsLost,
    int RollsHeld, IReadOnlyList<WarfrontLeaderRow> Rows)
{
    public const int Version = 1;
    public int FormatVersion { get; init; } = Version;

    [JsonIgnore] public bool Won => Result == "won";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static WarfrontRunRecord FromJson(string json)
    {
        var r = JsonSerializer.Deserialize<WarfrontRunRecord>(json, Json) ?? throw new InvalidDataException("empty warfront results record");
        if (r.FormatVersion != Version) throw new InvalidDataException("unsupported warfront results record version " + r.FormatVersion);
        return r;
    }
}

/// <summary>Builds the record at the end of a run from the campaign (names, join order, battles, rolls) and the contributors' figures (from W5/W9).</summary>
public static class WarfrontResults
{
    /// <summary>
    /// Every player the campaign joined gets a row (a player without figures gets zeros), and so does every key in <paramref name="stats"/> the campaign does not know (someone whose
    /// campaign entry is gone). Name: the campaign's saved display name, else the figures' name, else "Player N". Connected: the campaign's own flag for a known player, else the figures'.
    /// Join index: the campaign's join order; unknown keys follow it, by key. The key itself never reaches the record.
    /// </summary>
    public static WarfrontRunRecord Build(WarfrontCampaign c, IReadOnlyDictionary<ulong, WarfrontContributor>? stats, TimeSpan duration, DateTime utc, WarfrontScoreWeights? weights = null)
    {
        var w = weights ?? WarfrontScoreWeights.Default;
        if (!w.IsValid) throw new ArgumentException("score weights must not be negative", nameof(weights));
        stats ??= new Dictionary<ulong, WarfrontContributor>();
        var list = new List<WarfrontContributor>();
        int index = 0;
        foreach (var key in c.JoinOrder)
        {
            var p = c.Player(key);
            stats.TryGetValue(key, out var s);
            string name = Clean(!string.IsNullOrWhiteSpace(p?.Name) ? p!.Name : s?.Player.Name, "Player " + (index + 1));
            var player = (s?.Player ?? new WarfrontPlayerStats(name)) with { Name = name, Connected = c.IsConnected(key), JoinIndex = index };
            list.Add(new WarfrontContributor(player, Clean(s?.Squad)));
            index++;
        }
        foreach (var key in stats.Keys.Where(k => c.Player(k) is null).OrderBy(k => k))
        {
            var s = stats[key];
            string name = Clean(s.Player.Name, "Player " + (index + 1));
            list.Add(new WarfrontContributor(s.Player with { Name = name, JoinIndex = index }, Clean(s.Squad)));
            index++;
        }
        var rows = WarfrontLeaderboard.Rank(list, w);
        string result = c.Phase == WarfrontPhase.Won ? "won" : c.Phase == WarfrontPhase.Lost ? "lost" : "abandoned";
        var battles = c.History.Select(h => $"#{h.Deployment} {h.Target.Kind}{(h.Target.Kind == WarfrontTargetKind.Hq ? "" : " lane " + (h.Target.Lane + 1))}: {OutcomeText(h.Outcome)}").ToList();
        return new WarfrontRunRecord(result, utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), (int)Math.Max(0, duration.TotalSeconds), w, battles,
            c.Rolls.Count, c.Rolls.Count(r => r.Lost), c.Rolls.Count(r => r.HeldAtFloor), rows);
    }

    /// <summary>A battle's outcome as the record prints it: Win and Wipe as before; the Chaos finale's lost relay in words (C5c, lead 40), not the enum's name.</summary>
    public static string OutcomeText(WarfrontOutcome outcome) => outcome == WarfrontOutcome.FinaleLost ? "Finale lost (no challenger left)" : outcome.ToString();

    /// <summary>A display name safe for a text block: no control characters, no backticks, no line breaks, at most 24 characters; "@" is broken so a paste never pings.</summary>
    public static string Clean(string? name, string fallback = "")
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in name ?? "")
        {
            if (char.IsControl(ch) || ch == '`' || ch == '​') continue;
            sb.Append(ch);
            if (ch == '@') sb.Append('​');
        }
        var t = sb.ToString().Trim();
        if (t.Length > 24) t = t[..24].TrimEnd();
        return t.Length == 0 ? fallback : t;
    }

    static IReadOnlyList<WarfrontRetainerStats> Clean(IReadOnlyList<WarfrontRetainerStats>? squad) =>
        squad is null ? Array.Empty<WarfrontRetainerStats>() : squad.Select((r, i) => r with { Name = Clean(r.Name, "Marine " + (i + 1)), ClassName = Clean(r.ClassName) }).ToList();
}
