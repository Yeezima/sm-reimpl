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

    /// <summary>The formula in words, shown on the panel and in the export.</summary>
    public string FormulaText()
    {
        static string N(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"Score = kills x{N(Kill)}, elite x{N(EliteKill)}, boss x{N(BossKill)}, assists x{N(Assist)}, captures x{N(Capture)}, revives x{N(Revive)}, "
            + $"squad kills x{N(SquadKill)}, squad revives x{N(SquadRevive)}. Deaths and knockouts never add. Ties: "
            + string.Join(", ", TieBreaks.Select(t => t switch { WarfrontTieBreak.Captures => "captures", WarfrontTieBreak.Revives => "revives", WarfrontTieBreak.FewerDeaths => "fewer deaths", _ => "earlier join" })) + ".";
    }
}
