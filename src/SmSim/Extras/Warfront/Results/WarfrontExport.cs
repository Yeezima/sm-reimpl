using System.Globalization;
using System.Text;

namespace SmSim.Extras.Warfront.Results;

/// <summary>
/// The text people copy or save after a run (plan section 7): a fixed-width block that reads the same in a code fence on Discord. Nothing is posted anywhere; there is no campaign key,
/// path or account id in it, only display names and figures.
/// </summary>
public static class WarfrontExport
{
    /// <summary>Discord's message limit; <see cref="Discord"/> stays under it.</summary>
    public const int DiscordLimit = 2000;

    static string N(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    static string Pad(string s, int w) => s.Length >= w ? s[..w] : s.PadRight(w);
    static string R(object v, int w) => v.ToString()!.PadLeft(w);

    public static string Title(WarfrontRunRecord r) => r.Result switch { "won" => "WARFRONT - VICTORY", "lost" => "WARFRONT - DEFEAT", _ => "WARFRONT - RUN ABANDONED" };

    /// <summary>The plain block. <paramref name="retainers"/> adds each squad's top retainer line; <paramref name="formula"/> the score formula at the end.</summary>
    public static string Text(WarfrontRunRecord r, bool retainers = true, bool formula = true)
    {
        var sb = new StringBuilder();
        var t = TimeSpan.FromSeconds(r.DurationSeconds);
        sb.Append(Title(r)).Append('\n');
        sb.Append((r.EndedUtc.Length >= 16 ? r.EndedUtc[..16].Replace('T', ' ') : r.EndedUtc)).Append(" UTC | run time ").Append(((int)t.TotalHours).ToString(CultureInfo.InvariantCulture))
            .Append(t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)).Append(" | ").Append(r.Battles.Count).Append(" battles | rolls lost ").Append(r.RollsLost).Append(" of ").Append(r.RollsTotal).Append('\n');
        sb.Append('\n');
        sb.Append("".PadRight(42)).Append("YOU".PadRight(17)).Append("SQUAD\n");
        sb.Append(" #  ").Append(Pad("PLAYER", 24)).Append(R("SCORE", 8)).Append(R("K", 5)).Append(R("A", 4)).Append(R("D", 3)).Append(R("REV", 4)).Append(R("CAP", 4)).Append("  |")
            .Append(R("K", 6)).Append(R("KO", 4)).Append(R("REV", 4)).Append('\n');
        bool anyLeft = false;
        foreach (var row in r.Rows)
        {
            string name = row.Connected ? row.Name : row.Name + "*";
            anyLeft |= !row.Connected;
            sb.Append(R(row.Rank, 2)).Append("  ").Append(Pad(name, 24)).Append(R(N(row.Total), 8)).Append(R(row.Player.Kills, 5)).Append(R(row.Player.Assists, 4)).Append(R(row.Player.Deaths, 3))
                .Append(R(row.Player.Revives, 4)).Append(R(row.Player.Captures, 4)).Append("  |").Append(R(row.Squad.Kills, 6)).Append(R(row.Squad.Knockouts, 4)).Append(R(row.Squad.Revives, 4)).Append('\n');
            if (retainers && row.TopRetainer is { } top)
                sb.Append("      top retainer: ").Append(top.Name).Append(top.ClassName.Length > 0 ? " (" + top.ClassName + ")" : "").Append(" - ").Append(top.Reason).Append('\n');
        }
        if (anyLeft) sb.Append("\n* left before the end\n");
        if (formula) sb.Append('\n').Append(r.Weights.FormulaText()).Append('\n');
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Markdown (also what Discord renders): the block in a code fence. Retainer lines and the formula are dropped, in that order, when the whole would pass <see cref="DiscordLimit"/>.</summary>
    public static string Discord(WarfrontRunRecord r)
    {
        foreach (var (ret, form) in new[] { (true, true), (true, false), (false, false) })
        {
            var s = Fence(Text(r, ret, form));
            if (s.Length <= DiscordLimit) return s;
        }
        var lines = Text(r, false, false).Split('\n');
        var keep = new StringBuilder();
        foreach (var line in lines)
        {
            if (keep.Length + line.Length + 8 > DiscordLimit) break;
            keep.Append(line).Append('\n');
        }
        return Fence(keep.ToString());
    }

    static string Fence(string body) => "```\n" + body.TrimEnd('\n') + "\n```";
}
