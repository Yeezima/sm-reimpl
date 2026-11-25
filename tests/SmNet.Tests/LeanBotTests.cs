namespace SmNet.Tests;

/// <summary>perf/lean-bots (ours): only a headless test bot without <c>--bot-full-load</c> or a presentation test aid skips the presentation's loads.</summary>
public class LeanBotTests
{
    static readonly string[] BotArgs = ["--connect", "127.0.0.1:27370", "--bot", "--no-sound", "--no-hud", "--class", "tactical", "--side", "chaos"];

    [Fact]
    public void A_headless_bot_is_lean()
    {
        Assert.True(LeanBot.Decide(bot: true, "headless", BotArgs));
        Assert.True(LeanBot.Decide(bot: true, "Headless", BotArgs));   // the name is compared without case
        Assert.True(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--bot-accuracy").Append("0.5").Append("--bot-pickups")));   // bot behaviour aids stay lean
    }

    [Fact]
    public void The_opt_out_flag_loads_everything()
    {
        Assert.False(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--bot-full-load")));
        Assert.False(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--BOT-FULL-LOAD")));
        Assert.Equal("bot-full-load", LeanBot.FullLoadFlag);
    }

    [Theory]
    [InlineData("--anim-probe")]
    [InlineData("--frame-profile")]
    [InlineData("--screenshot")]
    [InlineData("--record-demo")]
    [InlineData("--bot-gait")]
    [InlineData("--log-sound")]
    [InlineData("--aim-test")]
    [InlineData("--no-player-hit-fx")]   // onhit-range-intensity (owner 2026-11-13): the no-particle look, presentation only
    [InlineData("--player-hit-fx")]   // classic-no-player-hit-fx: the restore switch is presentation only
    [InlineData("--no-jump-death")]
    public void A_presentation_test_aid_loads_everything(string aid) =>
        Assert.False(LeanBot.Decide(bot: true, "headless", BotArgs.Append(aid)));

    [Fact]
    public void A_value_that_looks_like_an_aid_is_not_one()
    {
        // Only flags count: "anim-probe" as the value of another argument does not switch lean mode off.
        Assert.True(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--bot-trace").Append("anim-probe")));
    }

    [Theory]
    [InlineData("Windows")]   // a windowed bot (screenshots, recordings: --position -4000,-4000 --test-window) draws, so it loads everything
    [InlineData("X11")]
    [InlineData("")]
    [InlineData(null)]
    public void A_bot_with_a_display_is_not_lean(string? displayServer) =>
        Assert.False(LeanBot.Decide(bot: true, displayServer, BotArgs));

    [Fact]
    public void The_hosted_bots_arguments_are_lean()
    {
        // HostBots.ClientArguments' user part (after Godot's "--"): the menu's bots must stay lean.
        var all = SmContent.HostBots.ClientArguments(27370, new SmContent.BotSlot("marines", "tactical"), "extended");
        Assert.True(LeanBot.Decide(bot: true, "headless", all.SkipWhile(a => a != "--").Skip(1)));
    }

    /// <summary>Every flag of the client that reads like a probe, log, capture, sweep, test, screenshot, wait or demo is classified: a new one fails here until it is added to one of the sets.</summary>
    [Fact]
    public void Every_test_aid_flag_of_the_client_is_classified()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SmReimpl.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var flag = new System.Text.RegularExpressions.Regex("(?:CmdFlag|CmdArgs|CmdArg|ContainsKey|GetValueOrDefault|BarkArg)\\(\"(?:--)?([a-z0-9-]+)\"[,)]|\"--([a-z0-9-]+)\"");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        // T1 (2026-11-25): the bot brain's flags moved with it to src/SmBots; the client hands it its arguments, so they are the client's flags too.
        foreach (var file in new[] { "client", Path.Combine("src", "SmBots") }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(root!.FullName, d), "*.cs", SearchOption.AllDirectories)))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            if (file.Contains(Path.DirectorySeparatorChar + ".godot" + Path.DirectorySeparatorChar)) continue;
            foreach (System.Text.RegularExpressions.Match m in flag.Matches(File.ReadAllText(file)))
                names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        Assert.Contains("anim-probe", names);   // the scan finds the flags at all
        Assert.Contains("bot-trace", names);    // and the brain's (src/SmBots)
        var unclassified = names.Where(n => LeanBot.LooksLikeAid(n) && !LeanBot.PresentationAids.Contains(n) && !LeanBot.LeanSafeAids.Contains(n)).ToList();
        Assert.True(unclassified.Count == 0, "client flags that look like test aids but are in neither LeanBot.PresentationAids nor LeanBot.LeanSafeAids: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void The_frame_cap_is_the_server_tick_unless_overridden()
    {
        // perf/pass-1, merged: a lean bot runs at most 30 frames a second and 30 physics steps; --bot-fps n overrides the cap (0 = none), anything else keeps it.
        Assert.Equal((30, 30), (LeanBot.FrameCap, LeanBot.PhysicsTicks));
        Assert.Equal(30, LeanBot.FrameCapFor(null));
        Assert.Equal(60, LeanBot.FrameCapFor("60"));
        Assert.Equal(0, LeanBot.FrameCapFor("0"));
        Assert.Equal(30, LeanBot.FrameCapFor("-5"));
        Assert.Equal(30, LeanBot.FrameCapFor("fast"));
        Assert.True(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--bot-fps").Append("60").Append("--perf-probe")));   // neither switches lean mode off
        Assert.True(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--" + LeanBot.FrameProfileFlag)));   // bots/a1-perf: the lean bot's own phase profile keeps it lean
        Assert.False(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--frame-profile")));   // the full client's profile still loads everything
    }

    [Fact]
    public void A_player_is_never_lean()
    {
        Assert.False(LeanBot.Decide(bot: false, "headless", ["--connect", "127.0.0.1:27370"]));   // a headless human client or a demo replay keeps every load
        Assert.False(LeanBot.Decide(bot: false, "Windows", []));
    }

    /// <summary>bots/a1-perf: a lean bot's jump-link build uses one thread on a small machine; elsewhere a third of the cores as before; <c>--bot-jump-threads n</c> wins.</summary>
    [Theory]
    [InlineData(null, 4, true, 1)]
    [InlineData(null, 8, true, 1)]
    [InlineData(null, 8, false, 2)]    // a full-load bot keeps the old rule
    [InlineData(null, 12, true, 4)]    // a bigger machine keeps the old rule
    [InlineData(null, 2, false, 1)]
    [InlineData(null, 1, false, 1)]
    [InlineData("3", 4, true, 3)]
    [InlineData("1", 32, false, 1)]
    [InlineData("0", 12, true, 4)]     // not a thread count: the rule
    [InlineData("x", 4, true, 1)]
    public void The_jump_build_threads_follow_the_lean_cap(string? flag, int cores, bool lean, int expected)
    {
        Assert.Equal(expected, LeanBot.JumpBuildThreadsFor(flag, cores, lean));
        Assert.True(LeanBot.Decide(bot: true, "headless", BotArgs.Append("--" + LeanBot.JumpThreadsFlag).Append("1")));   // the switch keeps the bot lean
    }
}
