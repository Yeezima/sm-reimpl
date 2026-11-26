using System.Text.Json;
using System.Text.Json.Nodes;
using SmServer;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>
/// Codebase fix 1: the run loader tells a load failure from a write failure and never replaces a file this build cannot use. Temp folders only (never the real run folder).
/// </summary>
public class WarfrontRunLoaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-loader-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime Now = new(2026, 11, 22, 3, 4, 5, DateTimeKind.Utc);

    public WarfrontRunLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    string RunPath => Path.Combine(_dir, "run.json");
    string Records => Path.Combine(_dir, "records");

    /// <summary>A progressed run on disk: two players, XP, a pick, an extra retainer.</summary>
    WarfrontCampaign WriteProgressedRun(ulong seed = 0xABCDEF)
    {
        var c = WarfrontSave.NewRun(seed);
        c.Embark();
        c.Join(111, "Alpha", out _);
        c.Join(222, "Beta", out _);
        c.ApplyProgress(111, new WarfrontProgressFile { Xp = 4200, PicksTaken = 1, Cards = [(int)WarfrontCard.Retainer], ExtraRetainers = [new WarfrontExtraRetainer { ClassName = "Tactical", Name = "Cassian" }] });
        c.AddXp(222, 1800);
        WarfrontSave.Write(_dir, c);
        return c;
    }

    static WarfrontLoaderIo Io() => new() { SleepMs = _ => { } };

    [Fact]
    public void Absent_file_starts_a_new_run_and_writes_it()
    {
        var load = WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        Assert.Equal(WarfrontLoadState.New, load.State);
        Assert.Null(load.Notice);
        Assert.False(load.NoSave);
        Assert.False(load.SavePending);
        Assert.True(File.Exists(RunPath));
        Assert.False(File.Exists(Path.Combine(_dir, WarfrontRunLoader.NoticeFile)));
    }

    [Fact]
    public void Loaded_but_not_rewritten_keeps_the_same_campaign_and_the_file()
    {
        var original = WriteProgressedRun();
        var bytes = File.ReadAllBytes(RunPath);
        int writes = 0;
        var io = Io();
        io.Write = (_, _) => { writes++; throw new IOException("injected: the replace failed after parsing"); };

        var first = WarfrontRunLoader.Load(_dir, null, io, () => Now);
        Assert.Equal(1, writes);
        Assert.Equal(WarfrontLoadState.LoadedNotWritten, first.State);
        Assert.True(first.SavePending);
        Assert.False(first.NoSave);
        Assert.Null(first.Notice);
        Assert.Equal(bytes, File.ReadAllBytes(RunPath));   // nothing was replaced, nothing archived, nothing moved
        Assert.False(Directory.Exists(Records));
        Assert.Equal(original.RunSeed, first.Campaign.RunSeed);

        var second = WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        Assert.Equal(WarfrontLoadState.Loaded, second.State);
        Assert.Equal(first.Campaign.RunSeed, second.Campaign.RunSeed);
        Assert.Equal(first.Campaign.Deployments, second.Campaign.Deployments);
        var a = first.Campaign.Player(111)!; var b = second.Campaign.Player(111)!;
        Assert.Equal(a.Xp, b.Xp);
        Assert.Equal(a.Cards, b.Cards);
        Assert.Equal(a.Squad.Select(r => r.Name), b.Squad.Select(r => r.Name));
        Assert.Equal(4200, b.Xp);
        Assert.Empty(b.Squad);                 // the retired Retainer card and its retainer are dropped (owner 2026-11-23, card 2) ...
        Assert.Empty(b.Cards);
        Assert.Equal(0, b.PicksTaken);         // ... and its pick is given back (owner "Give picks back")
        Assert.Equal(first.Campaign.StateHash(), second.Campaign.StateHash());
        Assert.Equal(bytes, File.ReadAllBytes(RunPath));   // the file is byte-equal after the second loader's rewrite
    }

    [Fact]
    public void Newer_build_file_is_untouched_and_refused()
    {
        WriteProgressedRun();
        var node = JsonNode.Parse(File.ReadAllText(RunPath))!.AsObject();
        node["Version"] = 99;
        node["FutureField"] = new JsonObject { ["x"] = 1 };
        File.WriteAllText(RunPath, node.ToJsonString());
        var bytes = File.ReadAllBytes(RunPath);

        var load = WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        Assert.Equal(WarfrontLoadState.Newer, load.State);
        Assert.True(load.NoSave);
        Assert.Equal(WarfrontPhase.Ship, load.Campaign.Phase);
        Assert.Equal(bytes, File.ReadAllBytes(RunPath));
        Assert.False(Directory.Exists(Records));
        var notice = ReadNotice();
        Assert.Equal("newer", notice.GetProperty("kind").GetString());
        Assert.Equal("2026-11-22T03:04:05Z", notice.GetProperty("utc").GetString());
        Assert.Equal("this campaign needs a newer build", notice.GetProperty("text").GetString());
        Assert.Equal(load.Notice!.Text, notice.GetProperty("text").GetString());
    }

    [Fact]
    public void Locked_file_is_read_three_times_then_refused_untouched()
    {
        WriteProgressedRun();
        var bytes = File.ReadAllBytes(RunPath);
        int reads = 0; var sleeps = new List<int>();
        var io = Io();
        io.ReadAllText = _ => { reads++; throw new IOException("the process cannot access the file"); };
        io.SleepMs = sleeps.Add;

        var load = WarfrontRunLoader.Load(_dir, null, io, () => Now);
        Assert.Equal(3, reads);
        Assert.Equal([200, 200], sleeps);
        Assert.Equal(WarfrontLoadState.Locked, load.State);
        Assert.True(load.NoSave);
        Assert.Equal(bytes, File.ReadAllBytes(RunPath));
        Assert.Equal("locked", ReadNotice().GetProperty("kind").GetString());
        Assert.Equal("the campaign file cannot be read", load.Notice!.Text);
    }

    [Fact]
    public void A_lock_that_clears_within_the_retries_loads_normally()
    {
        WriteProgressedRun();
        int reads = 0;
        var io = Io();
        io.ReadAllText = p => { if (++reads < 3) throw new IOException("busy"); return File.ReadAllText(p); };
        var load = WarfrontRunLoader.Load(_dir, null, io, () => Now);
        Assert.Equal(WarfrontLoadState.Loaded, load.State);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void Damaged_file_is_moved_with_its_squads_and_a_new_run_begins()
    {
        string garbage = "{ this is not a campaign";
        File.WriteAllText(RunPath, garbage);
        File.WriteAllText(Path.Combine(_dir, "squads.json"), "{\"rosters\":1}");

        var load = WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        Assert.Equal(WarfrontLoadState.NewAfterDamaged, load.State);
        Assert.False(load.NoSave);
        Assert.Equal("unreadable", load.Notice!.Kind);
        Assert.Equal(garbage, File.ReadAllText(Path.Combine(Records, "20261122T030405Z-unreadable.json")));
        Assert.Equal("{\"rosters\":1}", File.ReadAllText(Path.Combine(Records, "20261122T030405Z-unreadable-squads.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "squads.json")));
        var fresh = WarfrontSave.FromJson(File.ReadAllText(RunPath));   // a valid new run now stands in run.json
        Assert.Equal(load.Campaign.RunSeed, fresh.RunSeed);
        Assert.Equal("unreadable", ReadNotice().GetProperty("kind").GetString());
    }

    [Fact]
    public void A_second_damaged_file_in_the_same_second_gets_a_numbered_name()
    {
        File.WriteAllText(RunPath, "garbage one");
        WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        File.WriteAllText(RunPath, "garbage two");
        WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        Assert.Equal("garbage one", File.ReadAllText(Path.Combine(Records, "20261122T030405Z-unreadable.json")));
        Assert.Equal("garbage two", File.ReadAllText(Path.Combine(Records, "20261122T030405Z-unreadable-2.json")));
    }

    [Fact]
    public void A_failed_move_falls_back_to_a_copy_and_the_original_is_never_lost()
    {
        File.WriteAllText(RunPath, "damaged text");
        var io = Io();
        io.Move = (_, _) => throw new IOException("injected: move failed");
        var load = WarfrontRunLoader.Load(_dir, null, io, () => Now);
        Assert.Equal(WarfrontLoadState.NewAfterDamaged, load.State);
        Assert.Equal("damaged text", File.ReadAllText(Path.Combine(Records, "20261122T030405Z-unreadable.json")));
        WarfrontSave.FromJson(File.ReadAllText(RunPath));   // the new run replaced it only after the copy was safe
    }

    [Fact]
    public void Move_and_copy_both_failing_refuses_and_leaves_the_file()
    {
        File.WriteAllText(RunPath, "damaged text");
        var io = Io();
        io.Move = (_, _) => throw new IOException("injected: move failed");
        io.Copy = (_, _) => throw new IOException("injected: copy failed");
        var load = WarfrontRunLoader.Load(_dir, null, io, () => Now);
        Assert.Equal(WarfrontLoadState.Refused, load.State);
        Assert.True(load.NoSave);
        Assert.Equal("damaged text", File.ReadAllText(RunPath));
        Assert.Equal("unreadable", ReadNotice().GetProperty("kind").GetString());
    }

    JsonElement ReadNotice()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, WarfrontRunLoader.NoticeFile)));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_run_older_than_version_6_is_kept_once_under_records_with_its_squads_before_the_rewrite()
    {
        // T0 review (2026-11-25): the retainer conversion keeps a one-time safety copy of the old run and its squads file
        WriteProgressedRun();
        var node = JsonNode.Parse(File.ReadAllText(RunPath))!.AsObject();
        node["Version"] = 5;
        File.WriteAllText(RunPath, node.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "squads.json"), "{\"old\":true}");
        string old = File.ReadAllText(RunPath);
        var first = WarfrontRunLoader.Load(_dir, null, Io(), () => Now);
        Assert.Equal(WarfrontLoadState.Loaded, first.State);
        var kept = Path.Combine(Records, "20261122T030405Z-before-v6.json");
        Assert.Equal(old, File.ReadAllText(kept));
        Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(Records, "20261122T030405Z-before-v6-squads.json")));
        Assert.Equal(0, first.Campaign.Player(111)!.PicksTaken);   // its Retainer card's pick given back (owner "Give picks back")
        // the rewrite is at the current version: a second load keeps no second copy
        var second = WarfrontRunLoader.Load(_dir, null, Io(), () => Now.AddMinutes(1));
        Assert.Equal(WarfrontLoadState.Loaded, second.State);
        Assert.Equal(2, Directory.GetFiles(Records).Length);
    }
}
