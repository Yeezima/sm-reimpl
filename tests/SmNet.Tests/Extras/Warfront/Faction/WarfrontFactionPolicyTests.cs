using SmSim.Extras.Warfront.Faction;

namespace SmNet.Tests.Extras.Warfront.Faction;

/// <summary>Chaos Warfront C1: the faction setting's keys, the choice it makes for a new run, and the rotation file (faction.json), including a crash between the run's two writes. Synthetic temp folders only.</summary>
public sealed class WarfrontFactionPolicyTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-faction-" + Guid.NewGuid().ToString("N"));

    public WarfrontFactionPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    string FilePath => Path.Combine(_dir, WarfrontFactionRotation.FileName);

    [Theory]
    [InlineData(null, WarfrontFactionSetting.Alternate)]
    [InlineData("", WarfrontFactionSetting.Alternate)]
    [InlineData("alternate", WarfrontFactionSetting.Alternate)]
    [InlineData("orks", WarfrontFactionSetting.Orks)]
    [InlineData(" Chaos ", WarfrontFactionSetting.Chaos)]
    [InlineData("ORKS", WarfrontFactionSetting.Orks)]
    public void Setting_keys_parse(string? text, WarfrontFactionSetting expected)
    {
        Assert.True(WarfrontFactionPolicy.TryParse(text, out var s));
        Assert.Equal(expected, s);
    }

    [Theory]
    [InlineData("ork")]
    [InlineData("random")]
    [InlineData("1")]
    public void Unknown_keys_are_refused(string text) => Assert.False(WarfrontFactionPolicy.TryParse(text, out _));

    [Fact]
    public void Keys_round_trip_in_setting_order()
    {
        Assert.Equal(["alternate", "orks", "chaos"], WarfrontFactionPolicy.Keys);
        foreach (WarfrontFactionSetting s in Enum.GetValues<WarfrontFactionSetting>())
        {
            Assert.True(WarfrontFactionPolicy.TryParse(WarfrontFactionPolicy.Key(s), out var back));
            Assert.Equal(s, back);
        }
    }

    [Theory]
    [InlineData(WarfrontFactionSetting.Alternate, null, WarfrontFaction.Orks)]        // Alternate starts with Orks
    [InlineData(WarfrontFactionSetting.Alternate, WarfrontFaction.Orks, WarfrontFaction.Chaos)]
    [InlineData(WarfrontFactionSetting.Alternate, WarfrontFaction.Chaos, WarfrontFaction.Orks)]
    [InlineData(WarfrontFactionSetting.Orks, null, WarfrontFaction.Orks)]
    [InlineData(WarfrontFactionSetting.Orks, WarfrontFaction.Orks, WarfrontFaction.Orks)]
    [InlineData(WarfrontFactionSetting.Orks, WarfrontFaction.Chaos, WarfrontFaction.Orks)]
    [InlineData(WarfrontFactionSetting.Chaos, null, WarfrontFaction.Chaos)]
    [InlineData(WarfrontFactionSetting.Chaos, WarfrontFaction.Orks, WarfrontFaction.Chaos)]
    [InlineData(WarfrontFactionSetting.Chaos, WarfrontFaction.Chaos, WarfrontFaction.Chaos)]
    public void Every_policy_and_last_state_chooses(WarfrontFactionSetting setting, WarfrontFaction? last, WarfrontFaction expected) =>
        Assert.Equal(expected, WarfrontFactionPolicy.Choose(setting, last));

    [Fact]
    public void Orks_is_zero_so_old_saves_and_old_wires_mean_Orks()
    {
        Assert.Equal(0, (int)WarfrontFaction.Orks);
        Assert.Equal(1, (int)WarfrontFaction.Chaos);
        Assert.True(WarfrontFactionPolicy.IsKnown(0) && WarfrontFactionPolicy.IsKnown(1));
        Assert.False(WarfrontFactionPolicy.IsKnown(2) || WarfrontFactionPolicy.IsKnown(-1) || WarfrontFactionPolicy.IsKnown(255));
    }

    [Fact]
    public void No_file_means_the_next_alternate_run_is_Orks()
    {
        Assert.Null(WarfrontFactionRotation.Read(_dir));
        Assert.Equal(WarfrontFaction.Orks, WarfrontFactionRotation.Next(_dir, WarfrontFactionSetting.Alternate));
        Assert.Equal(WarfrontFaction.Chaos, WarfrontFactionRotation.Next(_dir, WarfrontFactionSetting.Chaos));
        Assert.False(File.Exists(FilePath));   // Next reads only
    }

    [Fact]
    public void Record_and_read_round_trip_with_the_army_by_name()
    {
        Assert.True(WarfrontFactionRotation.Record(_dir, WarfrontFaction.Chaos, 0xFEEDUL));
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Chaos, 0xFEEDUL), WarfrontFactionRotation.Read(_dir));
        Assert.Contains("\"LastFaction\":\"Chaos\"", File.ReadAllText(FilePath));
        Assert.Contains("\"LastRunSeed\":65261", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal(WarfrontFaction.Orks, WarfrontFactionRotation.Next(_dir, WarfrontFactionSetting.Alternate));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"LastFaction\":\"Tau\",\"LastRunSeed\":1}")]
    [InlineData("{\"LastFaction\":7,\"LastRunSeed\":1}")]
    [InlineData("{\"LastFaction\":\"Chaos\",\"LastRunSeed\":-4}")]
    [InlineData("null")]
    [InlineData("{}")]                      // C1 review: no army written is no record
    [InlineData("{\"LastRunSeed\":1}")]
    public void A_damaged_file_counts_as_none(string text)
    {
        File.WriteAllText(FilePath, text);
        Assert.Null(WarfrontFactionRotation.Read(_dir));
        Assert.Equal(WarfrontFaction.Orks, WarfrontFactionRotation.Next(_dir, WarfrontFactionSetting.Alternate));
        // the next reconcile repairs it
        Assert.True(WarfrontFactionRotation.Reconcile(_dir, WarfrontFaction.Chaos, 3));
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Chaos, 3), WarfrontFactionRotation.Read(_dir));
    }

    [Fact]
    public void Reconcile_writes_only_when_the_file_is_behind_the_run()
    {
        Assert.True(WarfrontFactionRotation.Reconcile(_dir, WarfrontFaction.Orks, 11));    // no file: written
        var stamp = File.GetLastWriteTimeUtc(FilePath);
        Assert.False(WarfrontFactionRotation.Reconcile(_dir, WarfrontFaction.Orks, 11));   // in line: untouched
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(FilePath));
        Assert.True(WarfrontFactionRotation.Reconcile(_dir, WarfrontFaction.Orks, 12));    // another run with the same army
        Assert.True(WarfrontFactionRotation.Reconcile(_dir, WarfrontFaction.Chaos, 12));   // the same seed (SM_WF_SEED), another army
        Assert.Equal(new WarfrontFactionRotation.State(WarfrontFaction.Chaos, 12), WarfrontFactionRotation.Read(_dir));
    }

    /// <summary>The two writes of a new run, as the supervisor makes them: run.json (here: the chosen army and seed) first, faction.json second; <paramref name="crash"/> stops before the second.</summary>
    (WarfrontFaction Faction, ulong Seed) NewRun(WarfrontFactionSetting setting, ulong seed, bool crash)
    {
        var f = WarfrontFactionRotation.Next(_dir, setting);
        if (!crash) WarfrontFactionRotation.Record(_dir, f, seed);
        return (f, seed);
    }

    [Fact]
    public void A_crash_between_the_two_writes_neither_repeats_nor_skips_an_army()
    {
        var first = NewRun(WarfrontFactionSetting.Alternate, 1, crash: false);
        Assert.Equal(WarfrontFaction.Orks, first.Faction);
        var second = NewRun(WarfrontFactionSetting.Alternate, 2, crash: true);   // run.json says Chaos; faction.json still says Orks
        Assert.Equal(WarfrontFaction.Chaos, second.Faction);
        Assert.Equal(WarfrontFaction.Orks, WarfrontFactionRotation.Read(_dir)!.LastFaction);
        // the restart: the loader reads run.json and reconciles from it, once
        Assert.True(WarfrontFactionRotation.Reconcile(_dir, second.Faction, second.Seed));
        Assert.False(WarfrontFactionRotation.Reconcile(_dir, second.Faction, second.Seed));   // a second load changes nothing: no double advance
        var third = NewRun(WarfrontFactionSetting.Alternate, 3, crash: false);
        Assert.Equal(WarfrontFaction.Orks, third.Faction);
    }

    [Fact]
    public void A_forced_run_counts_as_the_last_army_for_alternate()
    {
        NewRun(WarfrontFactionSetting.Chaos, 1, crash: false);
        Assert.Equal(WarfrontFaction.Orks, NewRun(WarfrontFactionSetting.Alternate, 2, crash: false).Faction);
        Assert.Equal(WarfrontFaction.Orks, NewRun(WarfrontFactionSetting.Orks, 3, crash: false).Faction);
        Assert.Equal(WarfrontFaction.Chaos, NewRun(WarfrontFactionSetting.Alternate, 4, crash: false).Faction);
    }

    [Fact]
    public void An_unwritable_folder_reports_false_and_throws_nothing()
    {
        string file = Path.Combine(_dir, "a-file");
        File.WriteAllText(file, "x");
        Assert.False(WarfrontFactionRotation.Record(file, WarfrontFaction.Chaos, 1));   // the "folder" is a file
        Assert.Null(WarfrontFactionRotation.Read(file));
    }
}
