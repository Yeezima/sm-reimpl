using System.Text.Json.Serialization;

namespace SmSim.Extras.Warfront.Results;

/// <summary>The tie-break keys, applied in the order the weights table lists them after the total.</summary>
public enum WarfrontTieBreak { Captures, Revives, FewerDeaths, EarlierJoin }

/// <summary>
/// The one tuning table of the end-of-run score (plan section 7; all values [ours/estimate]; spec "Warfront end of run"). A kill is worth <see cref="Kill"/>, an elite
/// <see cref="EliteKill"/> and a boss <see cref="BossKill"/> instead (the elite and boss counts are subsets of the kills). There is no weight for deaths or knockouts: they never add points.
/// The squad's figures weigh as the Squad* values say; a retainer's own score (for the top retainer) uses the same Squad* values.
/// </summary>
public sealed record WarfrontScoreWeights
{
    public decimal Kill { get; init; } = 1m;
    public decimal EliteKill { get; init; } = 5m;
    public decimal BossKill { get; init; } = 25m;
    public decimal Assist { get; init; } = 0.5m;
    public decimal Capture { get; init; } = 30m;
    public decimal Revive { get; init; } = 15m;
    /// <summary>A retainer's kill, elite kill and boss kill (flat by default: 0.5 each).</summary>
    public decimal SquadKill { get; init; } = 0.5m;
    public decimal SquadEliteKill { get; init; } = 0.5m;
    public decimal SquadBossKill { get; init; } = 0.5m;
    public decimal SquadAssist { get; init; }
    public decimal SquadRevive { get; init; } = 15m;
    /// <summary>0 by default: a secured point credits the humans on it; every Marine standing there would multiply it.</summary>
    public decimal SquadCapture { get; init; }
    public IReadOnlyList<WarfrontTieBreak> TieBreaks { get; init; } = new[] { WarfrontTieBreak.Captures, WarfrontTieBreak.Revives, WarfrontTieBreak.FewerDeaths, WarfrontTieBreak.EarlierJoin };

    [JsonIgnore] public static WarfrontScoreWeights Default { get; } = new();

    /// <summary>Whether every weight is at least 0 (a negative weight could make a death or a loss subtract, which the rule forbids).</summary>
    [JsonIgnore]
    public bool IsValid => Kill >= 0 && EliteKill >= 0 && BossKill >= 0 && Assist >= 0 && Capture >= 0 && Revive >= 0 && SquadKill >= 0 && SquadEliteKill >= 0 && SquadBossKill >= 0
        && SquadAssist >= 0 && SquadRevive >= 0 && SquadCapture >= 0;

    public const string OverrideFile = "score-weights.json";

    /// <summary>
    /// The weights the run is scored with: <c>score-weights.json</c> in the warfront folder when it exists and parses and <see cref="IsValid"/>, else <see cref="Default"/>. A bad file is
    /// not an error: the defaults are used and <paramref name="log"/> gets one line. The file holds the same property names as this record (omitted ones keep their default).
    /// </summary>
    public static WarfrontScoreWeights LoadOverride(string dir, Action<string>? log = null)
    {
        var path = Path.Combine(dir, OverrideFile);
        if (!File.Exists(path)) return Default;
        try
        {
            var w = System.Text.Json.JsonSerializer.Deserialize<WarfrontScoreWeights>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            if (w is null || !w.IsValid || w.TieBreaks.Count == 0) { log?.Invoke(OverrideFile + " has a negative weight or no tie-breaks: the default weights are used"); return Default; }
            log?.Invoke(OverrideFile + " applied");
            return w;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log?.Invoke(OverrideFile + " could not be read (" + ex.GetType().Name + "): the default weights are used");
            return Default;
        }
    }

    /// <summary>The formula in words, shown on the panel and in the export.</summary>
    public string FormulaText()
    {
        static string N(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"Score = kills x{N(Kill)}, elite x{N(EliteKill)}, boss x{N(BossKill)}, assists x{N(Assist)}, captures x{N(Capture)}, revives x{N(Revive)}, "
            + $"squad kills x{N(SquadKill)}, squad revives x{N(SquadRevive)}. Deaths and knockouts never add. Ties: "
            + string.Join(", ", TieBreaks.Select(t => t switch { WarfrontTieBreak.Captures => "captures", WarfrontTieBreak.Revives => "revives", WarfrontTieBreak.FewerDeaths => "fewer deaths", _ => "earlier join" })) + ".";
    }
}
