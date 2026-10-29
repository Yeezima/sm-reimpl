using System.Globalization;

namespace SmContent;

/// <summary>How hard the hosted match's bots hit: Easy scales their damage on the server, Normal does not.</summary>
public enum BotDifficulty { Easy, Normal }

/// <summary>One bot to start: its side as the client's <c>--side</c> takes it (<c>marines</c> / <c>chaos</c>) and its <c>--class</c> name.</summary>
public sealed record BotSlot(string Side, string Class);

/// <summary>
/// The pure rules of the bots a front end adds to a match it hosts (the owner's requests of 2026-10-29: "no bots are joining after successfully hosting"; then
/// fix/hosting-bots-logs: none by default, the player picks how many on the Host panel's BOTS spinner, applied when the server starts). The bots are the test bots
/// (<c>client --bot</c>), started headless and silent as child processes of the hosting program once the server listens. All values here are **ours** (spec
/// "Hosted match bots").
/// </summary>
public static class HostBots
{
    /// <summary>At most this many bots (8 a side less the human).</summary>
    public const int MaxBots = 15;
    /// <summary>
    /// The default number of bots: none, in every reach (fix/hosting-bots-logs, the owner's decision of 2026-10-29: a tester's 8 GB laptop ran out of memory with the
    /// old default of 7 bots, about 0.65 GB each). Never remembered between runs.
    /// </summary>
    public const int DefaultCount = 0;
    /// <summary>Easy: every hit a bot deals is scaled by this on the server (the owner's testing setting, "reduce all bot damage by 66%").</summary>
    public const float EasyDamageScale = 0.34f;
    /// <summary>Seconds between two bot starts, so their loads do not stall the PC at once (ours).</summary>
    public const double StaggerSeconds = 3.0;
    public const string Marines = "marines", Chaos = "chaos";

    /// <summary>The class order of each side, as the owner's playtests fill it (repeated for more bots).</summary>
    public static readonly IReadOnlyList<string> MarineClasses = ["tactical", "devastator", "assault"];
    public static readonly IReadOnlyList<string> ChaosClasses = ["chaos tactical", "havoc", "raptor", "chaos tactical"];

    /// <summary>The server's extra arguments: with bots, requested sides are kept (<c>--honor-side-requests</c>) and Easy scales the bots' damage.</summary>
    public static IEnumerable<string> ServerArguments(int bots, BotDifficulty difficulty)
    {
        if (bots <= 0) yield break;
        yield return "--honor-side-requests";
        if (difficulty == BotDifficulty.Easy)
        {
            yield return "--bot-damage-scale";
            yield return EasyDamageScale.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The class of the <paramref name="index"/>th bot (from 0) of a side.</summary>
    public static string ClassFor(string side, int index)
    {
        var list = side == Chaos ? ChaosClasses : MarineClasses;
        return list[((index % list.Count) + list.Count) % list.Count];
    }

    /// <summary>
    /// The first <paramref name="count"/> bots (0 to <see cref="MaxBots"/>) for one human on <paramref name="humanChaos"/>'s side (Auto counts as the Space Marines:
    /// the server puts an Auto join there while the sides are even), in start order: each on the side with fewer players (<see cref="Next"/>; ties to the side the
    /// human did not pick), so the human's side never has more players than the other while they join (an Auto human is then never balanced away).
    /// 7 bots = a 4v4 with the human, 15 = 8v8.
    /// </summary>
    public static IReadOnlyList<BotSlot> Plan(int count, bool humanChaos)
    {
        var plan = new List<BotSlot>();
        for (int k = 0; k < Math.Clamp(count, 0, MaxBots); k++)
        {
            if (Next(plan, humanChaos) is not { } slot) break;
            plan.Add(slot);
        }
        return plan;
    }

    /// <summary>
    /// The next bot of a plan, given the bots before it (their slots) and the human's side: on the side with fewer players (the human counts on theirs; ties go
    /// to the side the human did not pick), the next class of that side's order. Null at <see cref="MaxBots"/> or when both sides are full (8 each).
    /// </summary>
    public static BotSlot? Next(IReadOnlyCollection<BotSlot> running, bool humanChaos)
    {
        if (running.Count >= MaxBots) return null;
        string mine = humanChaos ? Chaos : Marines, other = humanChaos ? Marines : Chaos;
        int mineCount = 1 + running.Count(b => b.Side == mine), otherCount = running.Count(b => b.Side == other);
        string side = mineCount < otherCount ? mine : other;
        if ((side == mine ? mineCount : otherCount) >= 8) side = side == mine ? other : mine;
        if ((side == mine ? mineCount : otherCount) >= 8) return null;
        return new BotSlot(side, ClassFor(side, running.Count(b => b.Side == side)));
    }

    /// <summary>
    /// The arguments of one bot client: headless (<paramref name="projectPath"/>: the Godot project folder when the client runs from the editor binary, null for the
    /// exported game), its own log file (<paramref name="logFile"/>: Godot's <c>--log-file</c>, an engine option before <c>--</c>; fix/hosting-bots-logs: never the
    /// player's <c>logs\godot.log</c>, see <see cref="BotLogs"/>), then after <c>--</c> the connect, <c>--bot --no-sound --no-hud</c>, side, class and the content profile.
    /// A headless <c>--bot</c> is a lean bot by itself (SmNet.LeanBot: perf/lean-bots' skipped loads plus perf/pass-1's frame cap and physics rate); no flag is needed.
    /// </summary>
    public static List<string> ClientArguments(int port, BotSlot slot, string? content, string? projectPath = null, string? logFile = null)
    {
        var args = new List<string> { "--headless" };
        if (!string.IsNullOrWhiteSpace(projectPath)) { args.Add("--path"); args.Add(projectPath); }
        if (!string.IsNullOrWhiteSpace(logFile)) { args.Add("--log-file"); args.Add(logFile); }
        args.AddRange(["--", "--connect", ServerLaunch.LocalHost(port), "--bot", "--no-sound", "--no-hud", "--side", slot.Side, "--class", slot.Class]);
        if (!string.IsNullOrWhiteSpace(content)) { args.Add("--content"); args.Add(content); }
        return args;
    }

    /// <summary>
    /// The executable a bot runs: the console build of the running game (<c>name.console.exe</c> beside an exported <c>name.exe</c>, <c>name_console.exe</c> beside an
    /// editor binary), so a headless bot never opens a window; the running executable itself when there is none (or it already is one). Null for an empty path.
    /// </summary>
    public static string? BotExecutable(string? runningExe)
    {
        if (string.IsNullOrWhiteSpace(runningExe)) return null;
        try
        {
            string dir = Path.GetDirectoryName(runningExe) ?? "", name = Path.GetFileNameWithoutExtension(runningExe), ext = Path.GetExtension(runningExe);
            if (name.EndsWith(".console", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_console", StringComparison.OrdinalIgnoreCase)) return runningExe;
            foreach (var candidate in new[] { Path.Combine(dir, name + ".console" + ext), Path.Combine(dir, name + "_console" + ext) })
                if (File.Exists(candidate)) return candidate;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
        return runningExe;
    }

    /// <summary>"7 bots", "1 bot", "" for none: the Host panel's count.</summary>
    public static string CountText(int bots) => bots <= 0 ? "" : bots == 1 ? "1 bot" : $"{bots} bots";

    /// <summary>The BOTS spinner's text for a count: "No bots", "1 bot", "7 bots".</summary>
    public static string Label(int bots) => bots <= 0 ? "No bots" : CountText(bots);

    /// <summary>A typed bot count (the test aid <c>--host-bots</c>): 0 to <see cref="MaxBots"/>, <c>none</c>, or the old fills <c>4v4</c> (7) and <c>8v8</c> (15); else null.</summary>
    public static int? ParseCount(string? text)
    {
        string? t = text?.Trim().ToLowerInvariant();
        if (t is null) return null;
        if (t == "none") return 0;
        if (t == "4v4") return 7;
        if (t == "8v8") return MaxBots;
        return int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= MaxBots ? n : null;
    }

    /// <summary>A saved or typed difficulty name (easy, normal), else null.</summary>
    public static BotDifficulty? ParseDifficulty(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "easy" => BotDifficulty.Easy,
        "normal" => BotDifficulty.Normal,
        _ => null,
    };
}

/// <summary>
/// The log files of the bots a front end starts (fix/hosting-bots-logs, the owner's decision of 2026-10-29): each bot writes its own file in <c>logs\bots\</c> under the
/// game's user folder (<c>%APPDATA%\sm-reimpl</c>) through Godot's <c>--log-file</c>, so the player's <c>logs\godot.log</c> is never rotated away by bots (Godot keeps
/// a few files in its log folder) and two processes never write one file. Old bot logs are pruned to the newest <see cref="Keep"/>.
/// </summary>
public static class BotLogs
{
    /// <summary>Bot logs kept in the folder (ours).</summary>
    public const int Keep = 30;
    public const string Prefix = "bot-", Extension = ".log";

    /// <summary>The bots' log folder under the game's user folder.</summary>
    public static string Folder(string userDataDir) => Path.Combine(userDataDir, "logs", "bots");

    /// <summary>A new file name for a bot: <c>bot-&lt;n&gt;-&lt;side&gt;-&lt;yyyyMMdd-HHmmss&gt;-&lt;host pid&gt;.log</c> (n: the bot's number in this front end).</summary>
    public static string FileName(int number, string side, DateTime now, int hostPid) =>
        $"{Prefix}{number}-{(side == HostBots.Chaos ? "chaos" : "marines")}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{hostPid}{Extension}";

    /// <summary>Deletes the oldest <c>bot-*.log</c> files in <paramref name="folder"/> beyond the newest <paramref name="keep"/> (files in use are skipped); never anything else.</summary>
    public static int Prune(string folder, int keep = Keep)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            var old = new DirectoryInfo(folder).GetFiles(Prefix + "*" + Extension)
                .Where(f => f.Name.StartsWith(Prefix, StringComparison.Ordinal) && f.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(Math.Max(0, keep)).ToList();
            foreach (var f in old)
            {
                try { f.Delete(); deleted++; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return deleted;
    }
}
