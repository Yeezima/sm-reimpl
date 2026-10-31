using SmClient.Customizer.Model;
using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// fix/connect-timeout: the hosted server's start watch (a taken port is reported, never joined) and the bots a hosted match is filled with (the owner's requests of
/// 2026-10-29). Engine-free rules only; sockets bind 127.0.0.1 on the test ports 27580-27589.
/// </summary>
public sealed class HostBotsTests
{
    [Fact]
    public void Plan_sides_starts_the_bots_each_side_asked_for()
    {
        var plan = HostBots.PlanSides(2, 3, humanChaos: false);
        Assert.Equal(2, plan.Count(b => b.Side == HostBots.Marines));
        Assert.Equal(3, plan.Count(b => b.Side == HostBots.Chaos));
        Assert.Equal(HostBots.Chaos, plan[0].Side);   // the human's side has a player already
        Assert.Equal(["chaos tactical", "havoc", "raptor"], plan.Where(b => b.Side == HostBots.Chaos).Select(b => b.Class));
        Assert.All(HostBots.PlanSides(0, 4, false), b => Assert.Equal(HostBots.Chaos, b.Side));
        Assert.Empty(HostBots.PlanSides(0, 0, true));
    }

    [Fact]
    public void Plan_sides_keeps_eight_a_side_with_the_human()
    {
        Assert.Equal(7, HostBots.MaxForSide(HostBots.Marines, humanChaos: false));
        Assert.Equal(8, HostBots.MaxForSide(HostBots.Chaos, humanChaos: false));
        Assert.Equal(7, HostBots.MaxForSide(HostBots.Chaos, humanChaos: true));
        var full = HostBots.PlanSides(99, 99, humanChaos: false);
        Assert.Equal(HostBots.MaxBots, full.Count);
        Assert.Equal(7, full.Count(b => b.Side == HostBots.Marines));
    }

    [Fact]
    public void Start_state_maps_the_servers_exit_and_listening_line()
    {
        Assert.Equal(HostStartState.Starting, HostStart.StateOf(running: true, ready: false, exitCode: null, secondsSinceStart: 5));
        Assert.Equal(HostStartState.Ready, HostStart.StateOf(true, true, null, 5));
        Assert.Equal(HostStartState.PortInUse, HostStart.StateOf(false, false, HostStart.ExitPortInUse, 1));
        Assert.Equal(HostStartState.Failed, HostStart.StateOf(false, false, 1, 1));
        Assert.Equal(HostStartState.Failed, HostStart.StateOf(false, false, null, 1));
        Assert.Equal(HostStartState.Failed, HostStart.StateOf(false, true, 0, 1));   // it listened, then ended: not joined
        Assert.Equal(HostStartState.TooSlow, HostStart.StateOf(true, false, null, HostStart.StartLimitSeconds + 1));
        Assert.Equal(3, HostStart.ExitPortInUse);
    }

    [Fact]
    public void Port_in_use_text_names_the_port_and_who_holds_it()
    {
        Assert.Equal("Port 27015 is already in use on this PC (another server is running). Close it or choose another port.", HostStart.PortInUseText(27015, PortHolder.Unknown));
        Assert.Contains("STOP SERVER", HostStart.PortInUseText(27015, PortHolder.ThisInstall));
        var other = HostStart.PortInUseText(27015, PortHolder.Other);
        Assert.Contains("another program is using port 27015", other);
        Assert.DoesNotContain("STOP SERVER", other);   // never offered for a program that is not ours
    }

    [Fact]
    public void The_host_path_reports_a_taken_port_with_its_holder()
    {
        const int port = 27582;
        Assert.Null(ServerLaunch.PortProblem(port));
        using var holder = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        if (OperatingSystem.IsWindows())
            Assert.Contains(Environment.ProcessId, HostStart.PortOwners(port));   // the owner table names this test process
        IReadOnlyList<int>? seen = null;
        string? problem = ServerLaunch.PortProblem(port, owners => { seen = owners; return PortHolder.Other; });
        Assert.NotNull(problem);
        Assert.StartsWith($"Port {port} is already in use on this PC", problem);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("another program", problem);
            Assert.Contains(Environment.ProcessId, seen!);
        }
    }

    [Fact]
    public void Bots_change_the_servers_arguments_only_when_there_are_any()
    {
        var none = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27583, true, ""));
        Assert.DoesNotContain("--honor-side-requests", none);
        Assert.DoesNotContain("--bot-damage-scale", none);
        var easy = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27583, true, "", Bots: 7));
        Assert.Equal(["--honor-side-requests", "--bot-damage-scale", "0.34"], easy.Skip(easy.IndexOf("--honor-side-requests")).ToList());
        var normal = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27583, true, "", Bots: 15, Difficulty: BotDifficulty.Normal));
        Assert.Contains("--honor-side-requests", normal);
        Assert.DoesNotContain("--bot-damage-scale", normal);
    }

    [Fact]
    public void Seven_bots_make_a_4v4_with_the_human_and_alternate_from_the_other_side()
    {
        var marine = HostBots.Plan(7, humanChaos: false);
        Assert.Equal(7, marine.Count);
        Assert.Equal(3, marine.Count(b => b.Side == HostBots.Marines));
        Assert.Equal(4, marine.Count(b => b.Side == HostBots.Chaos));
        Assert.Equal(HostBots.Chaos, marine[0].Side);
        // While they join, the human's side never has more players than the other (an Auto human stays on the Space Marines).
        for (int k = 1; k <= marine.Count; k++)
        {
            int mm = marine.Take(k).Count(b => b.Side == HostBots.Marines), cc = marine.Take(k).Count(b => b.Side == HostBots.Chaos);
            Assert.True(mm <= cc, $"after {k} bots: marine bots {mm}, chaos {cc}");
        }
        Assert.Equal(["tactical", "devastator", "assault"], marine.Where(b => b.Side == HostBots.Marines).Select(b => b.Class));
        Assert.Equal(["chaos tactical", "havoc", "raptor", "chaos tactical"], marine.Where(b => b.Side == HostBots.Chaos).Select(b => b.Class));

        var chaos = HostBots.Plan(7, humanChaos: true);
        Assert.Equal((4, 3), (chaos.Count(b => b.Side == HostBots.Marines), chaos.Count(b => b.Side == HostBots.Chaos)));
        Assert.Equal(HostBots.Marines, chaos[0].Side);
    }

    [Fact]
    public void No_bots_by_default_and_any_count_from_0_to_15()
    {
        Assert.Equal(0, HostBots.DefaultCount);
        Assert.Empty(HostBots.Plan(HostBots.DefaultCount, false));
        Assert.Empty(HostBots.Plan(-3, false));
        Assert.Equal(new BotSlot(HostBots.Chaos, "chaos tactical"), Assert.Single(HostBots.Plan(1, false)));   // one bot: the other side
        Assert.Equal(HostBots.Marines, Assert.Single(HostBots.Plan(1, true)).Side);
        for (int n = 0; n <= HostBots.MaxBots; n++) Assert.Equal(n, HostBots.Plan(n, n % 2 == 0).Count);
        Assert.Equal(HostBots.MaxBots, HostBots.Plan(99, false).Count);
        var full = HostBots.Plan(15, false);
        Assert.Equal((7, 8), (full.Count(b => b.Side == HostBots.Marines), full.Count(b => b.Side == HostBots.Chaos)));
    }

    [Fact]
    public void Plus_bot_goes_to_the_side_with_fewer_players_ties_to_the_other_side()
    {
        var running = new List<BotSlot>();
        var first = HostBots.Next(running, humanChaos: false)!;   // 1 marine (the human) vs 0: chaos
        Assert.Equal(new BotSlot(HostBots.Chaos, "chaos tactical"), first);
        running.Add(first);
        Assert.Equal(HostBots.Chaos, HostBots.Next(running, false)!.Side);   // 1 vs 1: the tie goes to the side the human did not pick
        running.Add(new BotSlot(HostBots.Chaos, "havoc"));
        Assert.Equal(new BotSlot(HostBots.Marines, "tactical"), HostBots.Next(running, false));   // 1 vs 2: marines
        var full = HostBots.Plan(15, false).ToList();
        Assert.Null(HostBots.Next(full, false));   // 15: the cap
        var marinesFull = Enumerable.Range(0, 7).Select(k => new BotSlot(HostBots.Marines, HostBots.ClassFor(HostBots.Marines, k))).ToList();
        Assert.Equal(HostBots.Chaos, HostBots.Next(marinesFull, false)!.Side);
    }

    [Fact]
    public void Bot_client_arguments_are_headless_silent_and_name_side_class_content_and_log_file()
    {
        var args = HostBots.ClientArguments(27584, new BotSlot(HostBots.Chaos, "havoc"), "extended");
        Assert.Equal(["--headless", "--", "--connect", "127.0.0.1:27584", "--bot", "--no-sound", "--no-hud", "--side", "chaos", "--class", "havoc", "--content", "extended"], args);
        var dev = HostBots.ClientArguments(27584, new BotSlot(HostBots.Marines, "tactical"), null, "C:/repo/client");
        Assert.Equal(["--headless", "--path", "C:/repo/client", "--"], dev.Take(4));
        Assert.DoesNotContain("--content", dev);
        // fix/hosting-bots-logs: the bot's own log file is an engine option, before "--".
        var logged = HostBots.ClientArguments(27584, new BotSlot(HostBots.Marines, "tactical"), null, null, "C:/logs/bots/bot-1.log");
        Assert.Equal(["--headless", "--log-file", "C:/logs/bots/bot-1.log", "--"], logged.Take(4));
    }

    [Fact]
    public void Bot_logs_have_their_own_folder_names_and_are_pruned_to_the_newest()
    {
        Assert.Equal(Path.Combine("u", "logs", "bots"), BotLogs.Folder("u"));
        Assert.Equal("bot-3-chaos-20261029-141516-4242.log", BotLogs.FileName(3, HostBots.Chaos, new DateTime(2026, 10, 29, 14, 15, 16), 4242));
        string dir = Path.Combine(Path.GetTempPath(), "sm-botlogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 6; i++)
            {
                string f = Path.Combine(dir, $"bot-{i}.log");
                File.WriteAllText(f, "x");
                File.SetLastWriteTimeUtc(f, start.AddMinutes(i));
            }
            File.WriteAllText(Path.Combine(dir, "godot.log"), "player");   // never touched
            File.WriteAllText(Path.Combine(dir, "bot-notes.txt"), "x");
            Assert.Equal(2, BotLogs.Prune(dir, 4));
            Assert.Equal(["bot-2.log", "bot-3.log", "bot-4.log", "bot-5.log", "bot-notes.txt", "godot.log"], Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(0, BotLogs.Prune(Path.Combine(dir, "missing")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Bot_executable_prefers_the_console_build_beside_the_game()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-hostbots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string game = Path.Combine(root, "sm-reimpl.exe"), console = Path.Combine(root, "sm-reimpl.console.exe");
            File.WriteAllBytes(game, []);
            Assert.Equal(game, HostBots.BotExecutable(game));   // none beside it: the game itself
            File.WriteAllBytes(console, []);
            Assert.Equal(console, HostBots.BotExecutable(game));
            Assert.Equal(console, HostBots.BotExecutable(console));
            Assert.Null(HostBots.BotExecutable(""));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void The_bot_count_is_never_remembered_and_old_fills_are_dropped()
    {
        Assert.Equal(BotDifficulty.Easy, FrontEndSettings.Default.BotDifficulty);
        string dir = Path.Combine(Path.GetTempPath(), "sm-hostbots-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = FrontEndSettings.Default with { BotDifficulty = BotDifficulty.Normal };
            Assert.Null(saved.Save(dir));
            Assert.Equal(saved, FrontEndSettings.Load(dir));
            string file = Path.Combine(dir, FrontEndSettings.FileName);
            Assert.DoesNotContain("hostBots", File.ReadAllText(file));
            // An earlier build saved "hostBotsLocal": "FourVsFour" (7 bots): read without error, gone at the next save.
            File.WriteAllText(file, "{\"host\":\"127.0.0.1:27015\",\"hostBotsLocal\":\"FourVsFour\",\"hostBotsLan\":\"EightVsEight\",\"hostBotDifficulty\":\"Normal\"}");
            var old = FrontEndSettings.Load(dir);
            Assert.Equal(BotDifficulty.Normal, old.BotDifficulty);
            Assert.Null(old.Save(dir));
            Assert.DoesNotContain("hostBotsLocal", File.ReadAllText(file));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Count_text_and_labels()
    {
        Assert.Equal("", HostBots.CountText(0));
        Assert.Equal("1 bot", HostBots.CountText(1));
        Assert.Equal("7 bots", HostBots.CountText(7));
        Assert.Equal("No bots", HostBots.Label(0));
        Assert.Equal("1 bot", HostBots.Label(1));
        Assert.Equal(15, HostBots.ParseCount("8v8"));
        Assert.Equal(7, HostBots.ParseCount("4v4"));
        Assert.Equal(0, HostBots.ParseCount("None"));
        Assert.Equal(3, HostBots.ParseCount("3"));
        Assert.Null(HostBots.ParseCount("16"));
        Assert.Null(HostBots.ParseCount("lots"));
        Assert.Equal(BotDifficulty.Normal, HostBots.ParseDifficulty("Normal"));
    }
}
