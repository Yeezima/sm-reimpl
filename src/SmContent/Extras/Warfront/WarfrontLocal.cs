using System.Globalization;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Faction;
using SmSim.Extras.Warfront.Results;

namespace SmContent.Extras.Warfront;

/// <summary>What pressing CONTINUE CAMPAIGN or NEW CAMPAIGN does (slice W16).</summary>
public enum WarfrontPress
{
    /// <summary>No server executable was found next to the game.</summary>
    NoServer,
    /// <summary>No server of this client runs: start the supervisor.</summary>
    Start,
    /// <summary>This client's Warfront server is listening: join it.</summary>
    Join,
    /// <summary>It is still starting: wait, never a second one, never an early join.</summary>
    Wait,
    /// <summary>A server of another profile (Musou, the Host panel's) runs: stop it first.</summary>
    OtherServer,
}

/// <summary>What <c>run.json</c> says to the menu.</summary>
public enum WarfrontRunKind
{
    /// <summary>No run on disk.</summary>
    None,
    /// <summary>A run in progress: CONTINUE is offered.</summary>
    Continuable,
    /// <summary>The run is won or lost (results not yet left): only NEW CAMPAIGN.</summary>
    Finished,
    /// <summary>The file exists but cannot be read: no CONTINUE; NEW CAMPAIGN asks first (the server archives the file).</summary>
    Unreadable,
    /// <summary>The file is of a newer build (its version is above ours): never touched; no CONTINUE and no NEW CAMPAIGN (the server would refuse both), the page says "this campaign needs a newer build".</summary>
    Newer,
}

/// <summary>The status strip's figures: deployments fought, the three fronts ("2 / 1 / 3") and the first player's level.</summary>
public sealed record WarfrontStatusCells(string Deployments, string Fronts, string Level)
{
    /// <summary>What the strip shows with no run in progress.</summary>
    public static readonly WarfrontStatusCells None = new("-", "-", "-");
    /// <summary>The strip's labels, in cell order.</summary>
    public static readonly string[] Labels = ["DEPLOYMENTS", "FRONTS", "LEVEL"];
    public string[] Values => [Deployments, Fronts, Level];
}

/// <param name="Kind">What the file says.</param>
/// <param name="Line">One line for the page (deployments, fronts, level); empty unless <see cref="WarfrontRunKind.Continuable"/>.</param>
/// <param name="Cells">The three figures of the page's status strip; null unless <see cref="WarfrontRunKind.Continuable"/> (the strip then shows dashes).</param>
public sealed record WarfrontRunInfo(WarfrontRunKind Kind, string Line, WarfrontStatusCells? Cells = null)
{
    public static readonly WarfrontRunInfo NoRun = new(WarfrontRunKind.None, "");
    /// <summary>What the page says for a <see cref="WarfrontRunKind.Newer"/> file (the same words as the server's refusal and notice).</summary>
    public const string NewerText = "this campaign needs a newer build";
    public bool CanContinue => Kind == WarfrontRunKind.Continuable;
    /// <summary>NEW CAMPAIGN asks before it starts: a run that is not over (or not readable) is on disk.</summary>
    public bool NewNeedsConfirm => Kind is WarfrontRunKind.Continuable or WarfrontRunKind.Unreadable;
}

/// <summary>
/// The engine-free rules of the main menu's WARFRONT row and its page (slice W16; the <see cref="MusouLocal"/> pattern): the local campaign runs on this PC only, bound to 127.0.0.1, one human admitted.
/// Reads <c>run.json</c> and <c>records\</c> and never writes them. The texts and the page are ours; loopback plus one human is the owner's answer 3 (docs/changes-from-original.md).
/// </summary>
public static class WarfrontLocal
{
    public const string RowText = "Warfront";
    public const string RowTooltip = "A campaign of three fronts: your squad against the Orks or Chaos, alone on this PC or with up to 4 friends";
    public const string PageTitle = "Warfront";
    /// <summary>The strip under the page's title (where the page is).</summary>
    public const string PageHeading = "Campaign alone or together";
    /// <summary>Most players in one Warfront (the server's <c>--warfront-max-humans</c> default and cap).</summary>
    public const int MaxFriends = 4;
    public const string HostRow = "Host for friends", JoinRow = "Join a friend";
    public const string StopServerRowText = "Stop Warfront server";
    /// <summary>The Host panel's explainer on the Warfront profile (replaces the Classic mode and bot talk): what hosting a Warfront campaign is, and how friends get in.</summary>
    public const string HostExplainer = "Host a Warfront campaign for you and up to 3 friends: you fight the three fronts together as Space Marines with your squads. Friends join by address; there are no bots to add, and no mode or map to pick.";

    /// <summary>
    /// The Host panel's explainer for the FACTION row's choice (Chaos Warfront C3): the players are always Space Marines; the enemy is the Orks, Chaos, or the two by turns. Ours.
    /// </summary>
    public static string HostExplainerFor(WarfrontFactionSetting faction) =>
        "Host a Warfront campaign for you and up to 3 friends: you fight the three fronts together as Space Marines with your squads, " + faction switch
        {
            WarfrontFactionSetting.Orks => "against the Orks.",
            WarfrontFactionSetting.Chaos => "against the forces of Chaos.",
            _ => "against the Orks or Chaos, by turns with each new campaign.",
        } + " Friends join by address; there are no bots to add, and no mode or map to pick.";

    /// <summary>The local page's FACTION plate (Chaos Warfront C3, owner 2026-11-25: "Add a fraction row"): its words for each choice, the setting's order.</summary>
    public static readonly string[] FactionPlateTexts = ["Faction: Alternate", "Faction: Orks", "Faction: Chaos"];
    public const string FactionPlateTooltip = "The enemy of your next new campaign on this PC: ALTERNATE switches between the Orks and Chaos with each new campaign, starting with the Orks. A campaign keeps its enemy to the end.";
    public const string CoopNote = "Up to 4 players fight together as Space Marines and their squads. New campaign and Continue play on this PC alone; host for friends (LAN / Internet) and they join by address.";

    public const string ConfirmTitle = "START A NEW CAMPAIGN?";
    public const string ConfirmBody = "THE CURRENT ONE IS ARCHIVED AS ABANDONED.";
    public const string ConfirmNewText = ConfirmTitle + " " + ConfirmBody;
    public const string PreparingTitle = "PREPARING THE BATTLEFIELD";
    public const string StartingText = "The Warfront server is starting. You join when it is ready.";
    public const string StoppedText = "Warfront server stopped.";
    public const string NoServerText = "No server was found next to the game, so Warfront cannot start.";
    public const string OtherServerText = "A server is running. Stop it first.";
    public const string RetryText = "Could not join the campaign. Press the row once more to retry.";

    /// <summary>The first map builds its layout and walking graph before the server listens: about 45 s for the layout, up to three minutes with the graph (measured); the limit leaves room.</summary>
    public const double StartLimitSeconds = 600;
    /// <summary>Humans admitted: the owner alone (owner answer 3).</summary>
    public const int MaxHumans = 1;

    public static WarfrontPress OnPress(bool hasServerExe, bool running, bool ready, bool warfrontProfile)
    {
        if (!hasServerExe) return WarfrontPress.NoServer;
        if (!running) return WarfrontPress.Start;
        if (!warfrontProfile) return WarfrontPress.OtherServer;
        return ready ? WarfrontPress.Join : WarfrontPress.Wait;
    }

    /// <summary>
    /// The arguments after the ones <c>ServerLaunch.Arguments</c> adds (<c>--port</c>, <c>--bind 127.0.0.1</c>, <c>--content warfront</c>, ...): one human, a new campaign when asked, and (Chaos Warfront C1) the faction
    /// setting when one is forced (<paramref name="faction"/>: Alternate, the server's default, adds nothing, so the launch of every earlier build is unchanged).
    /// </summary>
    public static IReadOnlyList<string> ExtraArgs(bool newCampaign, WarfrontFactionSetting faction = WarfrontFactionSetting.Alternate)
    {
        var a = new List<string> { "--warfront-max-humans", MaxHumans.ToString(CultureInfo.InvariantCulture) };
        if (newCampaign) a.Add("--warfront-new");
        if (faction != WarfrontFactionSetting.Alternate) { a.Add("--warfront-faction"); a.Add(WarfrontFactionPolicy.Key(faction)); }
        return a;
    }

    /// <summary>The host options of the local campaign: this PC only (always), the Warfront profile, no bots, the developer tools on (F6; weather and the rest), the faction setting of the next new run.</summary>
    public static HostOptions Options(int port, string gameDir, bool newCampaign, WarfrontFactionSetting faction = WarfrontFactionSetting.Alternate) =>
        new(ServerLaunch.ModeAnnihilation, port, LocalOnly: true, gameDir, AllowCustomEmblems: false, Content: SmSim.ContentProfiles.Warfront, Bots: 0, DevTools: true, ExtraArgs: ExtraArgs(newCampaign, faction));   // owner 2026-11-21: the full developer tools (F6), like Classic's This PC only; ServerLaunch adds --dev-tools only for LocalOnly and the server still wants a loopback bind and requester

    /// <summary>The private run folder (<c>%LOCALAPPDATA%\sm-reimpl\warfront</c>, or <c>SM_WF_DIR</c>, the test aid the server reads too).</summary>
    public static string RunDir()
    {
        string? env = Environment.GetEnvironmentVariable("SM_WF_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "warfront");
    }

    /// <summary>Reads <c>run.json</c> in <paramref name="dir"/> (read-only).</summary>
    public static WarfrontRunInfo ReadRun(string dir)
    {
        string path = Path.Combine(dir, WarfrontSave.RunFile);
        try
        {
            if (!File.Exists(path)) return WarfrontRunInfo.NoRun;
            var c = WarfrontSave.FromJson(File.ReadAllText(path));
            if (c.IsOver) return new WarfrontRunInfo(WarfrontRunKind.Finished, "");
            return new WarfrontRunInfo(WarfrontRunKind.Continuable, SummaryLine(c), SummaryCells(c));
        }
        catch (WarfrontSaveNewerException)
        {
            return new WarfrontRunInfo(WarfrontRunKind.Newer, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            return new WarfrontRunInfo(WarfrontRunKind.Unreadable, "");
        }
    }

    /// <summary>The status strip's three figures of a run (the same numbers as <see cref="SummaryLine"/>).</summary>
    public static WarfrontStatusCells SummaryCells(WarfrontCampaign c)
    {
        int level = c.JoinOrder.Count > 0 && c.Player(c.JoinOrder[0]) is { } p ? p.Level : 1;
        return new WarfrontStatusCells(c.Deployments.ToString(CultureInfo.InvariantCulture), string.Create(CultureInfo.InvariantCulture, $"{c.Front(0)} / {c.Front(1)} / {c.Front(2)}"), level.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"DEPLOYMENTS 4, FRONTS 2 / 1 / 3, LEVEL 5": deployments fought, the three fronts, the first player's level.</summary>
    public static string SummaryLine(WarfrontCampaign c)
    {
        int level = c.JoinOrder.Count > 0 && c.Player(c.JoinOrder[0]) is { } p ? p.Level : 1;
        return string.Create(CultureInfo.InvariantCulture, $"DEPLOYMENTS {c.Deployments}, FRONTS {c.Front(0)} / {c.Front(1)} / {c.Front(2)}, LEVEL {level}");
    }

    /// <summary>"RUNS 3, VICTORIES 1, FASTEST 41:20" from <c>records\</c> (read-only); null when there is no record. FASTEST only with a victory.</summary>
    public static string? HonorLine(string dir)
    {
        try
        {
            var roll = WarfrontResultsArchive.HonorRoll(dir);
            if (roll.Runs == 0) return null;
            string line = string.Create(CultureInfo.InvariantCulture, $"RUNS {roll.Runs}, VICTORIES {roll.Won}");
            var fastest = roll.Entries.FirstOrDefault(e => e.Title == "Fastest victory");
            if (fastest is not null && TimeSpan.TryParse(fastest.Value, CultureInfo.InvariantCulture, out var t))
                line += string.Create(CultureInfo.InvariantCulture, $", FASTEST {(int)t.TotalMinutes}:{t.Seconds:00}");
            return line;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The last <c>warfront:</c> status line among the server's output lines (a timestamp in front is allowed); empty when none.</summary>
    public static string LastStatus(IEnumerable<string> lines)
    {
        string last = "";
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            int at = line.IndexOf("warfront:", StringComparison.Ordinal);   // the server's own status lines are lower case; its "Warfront: enabled" banner is not one
            if (at >= 0) last = line[at..].Trim();
        }
        return last;
    }

    /// <summary>The elapsed time as "M:SS" (or "H:MM:SS" from an hour).</summary>
    public static string Elapsed(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t.Seconds:00}");
    }

    /// <summary>The message the networked view gives the front end when the player leaves a match himself (<c>ModelViewer.BackToFrontEnd</c>); any other message after a join is a failed join.</summary>
    public const string LeftMatchText = "You left the match.";

    /// <summary>What the menu does with the server this client started when the front end is built again after a join.</summary>
    public enum MenuReturn { Nothing, Keep, Stop }

    /// <summary>
    /// The front end is built (the game starts, or the player comes back from a match). <paramref name="serverOurs"/>: this client's Warfront server runs; <paramref name="joined"/>: a join was made to it.
    /// A failed join (a message other than <see cref="LeftMatchText"/>) keeps the server for one retry; leaving the match, or a second failure, stops it.
    /// </summary>
    public static MenuReturn AtMenu(bool serverOurs, bool joined, string? message, int retriesUsed)
    {
        if (!serverOurs || !joined) return MenuReturn.Nothing;
        bool failed = !string.IsNullOrEmpty(message) && message != LeftMatchText;
        return failed && retriesUsed == 0 ? MenuReturn.Keep : MenuReturn.Stop;
    }

    public const string RejoinRow = "Rejoin campaign", StopRow = "Stop server", ContinueRow = "Continue campaign", NewRow = "New campaign";

    /// <summary>
    /// What the page offers before its Customizer and Back rows. While this game's Warfront server runs: only REJOIN CAMPAIGN (the join) and STOP SERVER, so no row can start a campaign that is
    /// not the one running (a New campaign must never join the old one). While another server runs: STOP SERVER alone. With nothing running: CONTINUE CAMPAIGN (only with a run in progress) and NEW CAMPAIGN.
    /// </summary>
    public static IReadOnlyList<string> Choices(WarfrontRunInfo run, bool serverRunning = false, bool warfrontServer = false)
    {
        if (serverRunning) return warfrontServer ? [RejoinRow, StopRow] : [StopRow];
        return run.CanContinue ? [ContinueRow, NewRow] : [NewRow];
    }
}
