using System.Text.Json.Nodes;
using SmContent;
using SmContent.Extras.Warfront;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Faction;

namespace SmNet.Tests.Extras.Warfront.Faction;

/// <summary>
/// Chaos Warfront C1 on the server and the launchers: <c>--warfront-faction</c>, the child stages' arguments, the run's army chosen at a new run only (Orks, Chaos, Orks across restarts; the forced settings),
/// faction.json reconciled from run.json by the loader and the supervisor (a crash between the two writes), the stage's recorded army and the restage rule, the dedicated server's own new run, the Hub byte,
/// and the local campaign's and the Host panel's launch arguments. Synthetic temp run folders; in-process stage on a loopback network for the dedicated case; nothing binds a real socket.
/// Serial with the stage tests (the run folder and the faction setting are process-wide).
/// </summary>
[Collection("WarfrontStage")]
public sealed class WarfrontFactionServerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-facsrv-" + Guid.NewGuid().ToString("N"));

    public WarfrontFactionServerTests()
    {
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.FactionSetting = WarfrontFactionSetting.Alternate;
        WarfrontRunFiles.StageFaction = WarfrontFaction.Orks;
    }

    public void Dispose()
    {
        WarfrontRunFiles.FactionSetting = WarfrontFactionSetting.Alternate;
        WarfrontRunFiles.StageFaction = WarfrontFaction.Orks;
        WarfrontRunFiles.Dedicated = false;
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        GameServer.WarfrontPrebuild = SmContent.Extras.Warfront.WarfrontMaps.Prebuild;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string RunPath => Path.Combine(_dir, WarfrontSave.RunFile);
    WarfrontFactionRotation.State? Rotation => WarfrontFactionRotation.Read(_dir);

    // ---- the command line ------------------------------------------------------------------------------------------------------------------------

    static ServerArguments Parse(params string[] args)
    {
        Assert.True(ServerCommandLine.TryParse(args, out var a, out string error), error);
        return a;
    }

    [Theory]
    [InlineData("alternate", "alternate")]
    [InlineData("orks", "orks")]
    [InlineData("CHAOS", "chaos")]
    public void The_flag_takes_the_three_keys(string value, string expected) =>
        Assert.Equal(expected, Parse("--content", "warfront", "--warfront-faction", value).WarfrontFaction);

    [Fact]
    public void The_flag_is_absent_by_default_refuses_other_words_and_needs_warfront()
    {
        Assert.Null(Parse("--content", "warfront").WarfrontFaction);
        Assert.False(ServerCommandLine.TryParse(["--content", "warfront", "--warfront-faction", "tau"], out _, out string bad));
        Assert.Contains("alternate, orks or chaos", bad);
        Assert.False(ServerCommandLine.TryParse(["--content", "warfront", "--warfront-faction"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--warfront-faction", "chaos"], out _, out string needs));   // the default profile
        Assert.Contains("--warfront-faction", needs);
        Assert.Equal("chaos", Parse("--warfront-dedicated", "--warfront-faction", "chaos").WarfrontFaction);   // dedicated implies the Warfront profile
        Assert.Equal(WarfrontFactionPolicy.Keys, ServerArguments.WarfrontFactionKeys);
        Assert.Contains("--warfront-faction <f>", ServerCommandLine.Usage);
    }

    [Fact]
    public void Program_applies_the_setting()
    {
        WarfrontRunFiles.ApplyFactionSetting("chaos");
        Assert.Equal(WarfrontFactionSetting.Chaos, WarfrontRunFiles.FactionSetting);
        WarfrontRunFiles.ApplyFactionSetting(null);
        Assert.Equal(WarfrontFactionSetting.Alternate, WarfrontRunFiles.FactionSetting);
        WarfrontRunFiles.ApplyFactionSetting("orks");
        Assert.Equal(WarfrontFactionSetting.Orks, WarfrontRunFiles.FactionSetting);
    }

    [Fact]
    public void Child_stages_and_the_warm_up_inherit_the_flag()
    {
        string[] args = ["--content", "warfront", "--bind", "127.0.0.1", "--warfront-faction", "chaos", "--warfront-new", "--map", "x"];
        var child = WarfrontSupervisor.ChildArguments(args, "wf_b1");
        Assert.Equal("chaos", child[child.IndexOf("--warfront-faction") + 1]);
        Assert.DoesNotContain("--warfront-new", child);
        Assert.True(ServerCommandLine.TryParse(child, out var parsed, out string error), error);
        Assert.Equal("chaos", parsed.WarfrontFaction);
        var warm = WarfrontSupervisor.PrebuildArguments(["--warfront-dedicated", "--warfront-faction", "orks"]);
        Assert.Equal("orks", warm[warm.IndexOf("--warfront-faction") + 1]);
    }

    // ---- the run's army: chosen at a new run only ------------------------------------------------------------------------------------------------

    /// <summary>One process start, as the supervisor makes it: PrepareRun (a new campaign when asked), then the stage's loader.</summary>
    WarfrontCampaign Start(bool newCampaign, List<string>? log = null)
    {
        log ??= [];
        Assert.Equal(0, WarfrontSupervisor.PrepareRun(_dir, newCampaign, log.Add, log.Add));
        var load = WarfrontRunLoader.Load(_dir, null, null, null, log.Add);
        Assert.False(load.NoSave);
        return load.Campaign;
    }

    [Fact]
    public void Alternate_gives_Orks_then_Chaos_then_Orks_across_restarts_and_new_campaigns()
    {
        var first = Start(newCampaign: false);   // no run on disk: a new one
        Assert.Equal(WarfrontFaction.Orks, first.Faction);
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Orks, first.RunSeed), Rotation);
        Assert.Equal(WarfrontFaction.Orks, Start(newCampaign: false).Faction);   // a restart keeps the run and its army
        var second = Start(newCampaign: true);
        Assert.Equal(WarfrontFaction.Chaos, second.Faction);
        Assert.Equal(WarfrontFaction.Chaos, Start(newCampaign: false).Faction);
        Assert.Equal(WarfrontFaction.Chaos, WarfrontSave.Load(_dir)!.Faction);
        var third = Start(newCampaign: true);
        Assert.Equal(WarfrontFaction.Orks, third.Faction);
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Orks, third.RunSeed), Rotation);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, WarfrontSave.RecordsDir), "*.json").Length);   // the two archived runs; faction.json outlived them
    }

    [Theory]
    [InlineData(WarfrontFactionSetting.Orks, WarfrontFaction.Orks)]
    [InlineData(WarfrontFactionSetting.Chaos, WarfrontFaction.Chaos)]
    public void A_forced_setting_gives_its_army_every_time(WarfrontFactionSetting setting, WarfrontFaction army)
    {
        WarfrontRunFiles.FactionSetting = setting;
        Assert.Equal(army, Start(newCampaign: false).Faction);
        Assert.Equal(army, Start(newCampaign: true).Faction);
        Assert.Equal(army, Start(newCampaign: true).Faction);
        WarfrontRunFiles.FactionSetting = WarfrontFactionSetting.Alternate;   // back to alternate: the other army next
        Assert.NotEqual(army, Start(newCampaign: true).Faction);
    }

    [Fact]
    public void Changing_the_setting_never_changes_the_run_in_progress()
    {
        Assert.Equal(WarfrontFaction.Orks, Start(newCampaign: false).Faction);
        WarfrontRunFiles.FactionSetting = WarfrontFactionSetting.Chaos;
        Assert.Equal(WarfrontFaction.Orks, Start(newCampaign: false).Faction);
        Assert.Equal(WarfrontFaction.Chaos, Start(newCampaign: true).Faction);   // only the next new run
    }

    [Fact]
    public void A_save_without_a_faction_loads_as_Orks_and_reconciles_the_rotation()
    {
        var node = JsonNode.Parse(new WarfrontCampaign(55).ToJson())!.AsObject();
        node["Version"] = 5; node.Remove("Faction"); node["Phase"] = "Ship";
        File.WriteAllText(RunPath, node.ToJsonString());
        var c = Start(newCampaign: false);
        Assert.Equal(WarfrontFaction.Orks, c.Faction);
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Orks, 55), Rotation);
        Assert.Equal(WarfrontFaction.Chaos, Start(newCampaign: true).Faction);   // the owner's old Orks campaign counts as the last army
    }

    [Fact]
    public void A_crash_between_the_two_writes_is_repaired_by_the_next_load()
    {
        Start(newCampaign: false);   // run 1: Orks, recorded
        // run 2 written, then the process died before faction.json: run.json says Chaos, the file still says Orks
        var crashed = WarfrontRunFiles.BuildNewRun(77, WarfrontRotationNext());
        Assert.Equal(WarfrontFaction.Chaos, crashed.Faction);
        WarfrontSave.Write(_dir, crashed);
        Assert.Equal(WarfrontFaction.Orks, Rotation!.LastFaction);
        var log = new List<string>();
        var loaded = Start(newCampaign: false, log);
        Assert.Equal(WarfrontFaction.Chaos, loaded.Faction);
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Chaos, 77), Rotation);
        Assert.Contains(log, l => l.Contains("faction.json brought in line", StringComparison.Ordinal));
        Assert.Equal(WarfrontFaction.Orks, Start(newCampaign: true).Faction);   // never Chaos twice
    }

    WarfrontFaction WarfrontRotationNext() => WarfrontFactionRotation.Next(_dir, WarfrontRunFiles.FactionSetting);

    [Fact]
    public void A_new_campaign_right_after_a_crash_reconciles_before_it_archives()
    {
        Start(newCampaign: false);   // Orks, recorded
        WarfrontSave.Write(_dir, WarfrontRunFiles.BuildNewRun(78, WarfrontFaction.Chaos));   // crashed before faction.json
        var log = new List<string>();
        Assert.Equal(0, WarfrontSupervisor.PrepareRun(_dir, true, log.Add, log.Add));   // --warfront-new, with no load in between
        Assert.Equal(WarfrontFaction.Orks, WarfrontSave.Load(_dir)!.Faction);
        Assert.Contains(log, l => l.Contains("faction.json brought in line", StringComparison.Ordinal));
    }

    [Fact]
    public void The_supervisor_loop_reconciles_from_the_run_it_reads()
    {
        WarfrontSave.Write(_dir, WarfrontRunFiles.BuildNewRun(79, WarfrontFaction.Chaos));   // no faction.json at all
        var log = new List<string>();
        using var stop = new ManualResetEventSlim(false);
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, new ExitAtOnce(), dedicated: false, new WarfrontSupervisorTiming(), stop, log.Add));
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Chaos, 79), Rotation);
    }

    sealed class ExitAtOnce : IWarfrontStageLauncher
    {
        sealed class Done : IWarfrontStageProcess
        {
            public bool WaitForExit(int milliseconds) => true;
            public int ExitCode => 0;
            public void Kill() { }
            public void Dispose() { }
        }
        public IWarfrontStageProcess Start(string map) => new Done();
    }

    [Fact]
    public void The_start_fronts_test_aid_keeps_the_army()
    {
        string? before = Environment.GetEnvironmentVariable("SM_WF_START");
        try
        {
            Environment.SetEnvironmentVariable("SM_WF_START", "a2");
            var c = WarfrontRunFiles.BuildNewRun(5, WarfrontFaction.Chaos);
            Assert.Equal(2, c.Front(0));
            Assert.Equal(WarfrontFaction.Chaos, c.Faction);
        }
        finally { Environment.SetEnvironmentVariable("SM_WF_START", before); }
    }

    // ---- the stage's army and the restage rule ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_stage_records_the_army_of_the_run_on_disk()
    {
        Assert.Contains("Orks", WarfrontRunFiles.RecordStageFaction(_dir));   // no run
        Assert.Equal(WarfrontFaction.Orks, WarfrontRunFiles.StageFaction);
        WarfrontSave.Write(_dir, WarfrontRunFiles.BuildNewRun(80, WarfrontFaction.Chaos));
        string line = WarfrontRunFiles.RecordStageFaction(_dir);
        Assert.Equal(WarfrontFaction.Chaos, WarfrontRunFiles.StageFaction);
        Assert.StartsWith("Warfront faction: this stage runs Chaos", line);
        Assert.DoesNotContain("warfront:", line);   // not a menu status line
        File.WriteAllText(RunPath, "{ damaged");
        WarfrontRunFiles.RecordStageFaction(_dir);
        Assert.Equal(WarfrontFaction.Orks, WarfrontRunFiles.StageFaction);
        Assert.Equal("{ damaged", File.ReadAllText(RunPath));   // read only
    }

    [Theory]
    [InlineData("wf_b1", "wf_b1", WarfrontFaction.Orks, WarfrontFaction.Orks, true)]
    [InlineData("wf_b1", "wf_b1", WarfrontFaction.Chaos, WarfrontFaction.Chaos, true)]
    [InlineData("wf_b1", "wf_b1", WarfrontFaction.Chaos, WarfrontFaction.Orks, false)]   // the run's army differs from the stage's: restage
    [InlineData("wf_b1", "wf_b1", WarfrontFaction.Orks, WarfrontFaction.Chaos, false)]
    [InlineData("wf_a2", "wf_b1", WarfrontFaction.Orks, WarfrontFaction.Orks, false)]
    public void A_deployment_lands_here_only_on_the_same_map_and_army(string target, string stage, WarfrontFaction run, WarfrontFaction stageArmy, bool here) =>
        Assert.Equal(here, WarfrontStagePlan.CanLandHere(target, stage, run, stageArmy));

    // ---- the dedicated server's own new run -------------------------------------------------------------------------------------------------------

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou | ContentExtras.Warfront,
    };

    [Fact]
    public void The_dedicated_servers_own_new_run_follows_the_rotation()
    {
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = Path.Combine(_dir, "nav");
        WarfrontRunFiles.Dedicated = true;
        GameServer.WarfrontPrebuild = (map, _, progress) => { progress?.Invoke(30, "stub"); progress?.Invoke(100, "ready"); return map + " (stub)"; };
        // a finished Orks run (an old save: no faction.json yet)
        var seed = new WarfrontCampaign(9);
        seed.Embark();
        var node = JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Phase"] = "Won";
        File.WriteAllText(RunPath, node.ToJsonString());

        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var clientOptions = new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) };
        var h = new Harness(LinkConditions.Perfect, 5, 0, Config, new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map, clientOptions: clientOptions);
        h.Run(TimeSpan.FromSeconds(13));   // nobody connected: the new run begins after the 10 s grace
        Assert.True(File.Exists(RunPath));
        var fresh = WarfrontSave.Load(_dir)!;
        Assert.Equal(WarfrontFaction.Chaos, fresh.Faction);
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Chaos, fresh.RunSeed), Rotation);
        Assert.True(h.Server.RestartDue(out _));
    }

    // ---- the Hub byte -------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WarfrontFaction.Orks)]
    [InlineData(WarfrontFaction.Chaos)]
    public void The_hub_carries_the_army_as_its_last_byte(WarfrontFaction f)
    {
        var c = new WarfrontCampaign(77, null, f);
        c.Join(111, "Alpha", out _); c.Embark();
        var bytes = WarfrontWire.Encode(WarfrontWire.HubFor(c, 111));
        Assert.Equal((byte)f, bytes[^1]);
        Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontHubWire back));
        Assert.Equal(f, back.Faction);
        Assert.Equal(c.Forecast, back.Forecast);
        Assert.Equal(bytes, WarfrontWire.Encode(back));
    }

    [Fact]
    public void An_older_hub_decodes_as_Orks_and_a_bad_faction_byte_is_refused()
    {
        var c = new WarfrontCampaign(77, null, WarfrontFaction.Chaos);
        c.Join(111, "Alpha", out _); c.Embark();
        var full = WarfrontWire.Encode(WarfrontWire.HubFor(c, 111));
        Assert.True(WarfrontWire.TryDecode(full[..^1], out WarfrontHubWire format2));   // Format 2: ends after the forecast
        Assert.Equal(WarfrontFaction.Orks, format2.Faction);
        Assert.NotNull(format2.Forecast);
        Assert.True(WarfrontWire.TryDecode(full[..^(2 + SmSim.Extras.Warfront.Weather.WarfrontForecast.Slots)], out WarfrontHubWire format1));   // Format 1: no forecast either
        Assert.Equal(WarfrontFaction.Orks, format1.Faction);
        foreach (byte bad in new byte[] { 2, 9, 255 })
        {
            var b = (byte[])full.Clone(); b[^1] = bad;
            Assert.False(WarfrontWire.TryDecode(b, out WarfrontHubWire _));
        }
        Assert.False(WarfrontWire.TryDecode(full.Append((byte)0).ToArray(), out WarfrontHubWire _));   // nothing after the faction
        for (int n = 0; n < full.Length; n++) WarfrontWire.TryDecode(full[..n], out WarfrontHubWire _);   // never throws
    }

    // ---- the launchers ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_local_campaign_passes_a_forced_setting_and_nothing_for_alternate()
    {
        Assert.DoesNotContain("--warfront-faction", WarfrontLocal.ExtraArgs(true));
        Assert.DoesNotContain("--warfront-faction", WarfrontLocal.ExtraArgs(true, WarfrontFactionSetting.Alternate));
        Assert.Equal(new[] { "--warfront-max-humans", "1", "--warfront-faction", "chaos" }, WarfrontLocal.ExtraArgs(false, WarfrontFactionSetting.Chaos));
        var args = ServerLaunch.Arguments(WarfrontLocal.Options(27286, "game", newCampaign: true, WarfrontFactionSetting.Orks));
        Assert.True(ServerCommandLine.TryParse(args, out var parsed, out string error), error);
        Assert.Equal("orks", parsed.WarfrontFaction);
        Assert.True(parsed.WarfrontNew);
        Assert.Equal("127.0.0.1", args[args.IndexOf("--bind") + 1]);
    }

    [Fact]
    public void The_host_panels_choice_reaches_the_server_launch()
    {
        var none = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27287, true, "", Content: "warfront"));
        Assert.DoesNotContain("--warfront-faction", none);   // alternate: the launch of every earlier build
        var chaos = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27287, true, "", Content: "warfront", WarfrontFaction: "chaos"));
        Assert.True(ServerCommandLine.TryParse(chaos, out var parsed, out string error), error);
        Assert.Equal("chaos", parsed.WarfrontFaction);
    }
}
