using SmSim.Extras.Warfront.Campaign;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>Warfront run save: JSON round trip, atomic write, records archive. Temp directories only.</summary>
public class WarfrontSaveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wfsave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static WarfrontCampaign Busy()
    {
        var c = new WarfrontCampaign(4242);
        c.Join(11, out _); c.Join(12, out _);
        c.Embark();
        c.AddXp(11, 6000); c.AddXp(12, 2000);
        c.TryTakePick(11, 3); c.TryTakePick(11, 1);
        c.Player(12)!.Squad.Add(new WarfrontRetainerRecord { Id = "r1", ClassName = "Tactical", Name = "Brother Aurelian" });
        c.TryDeploy(11, WarfrontTarget.Node(1), out _);
        c.BeginBattle();
        c.Resolve(WarfrontOutcome.Win);
        c.Join(13, out _);
        return c;
    }

    [Fact]
    public void Json_round_trips_to_an_equal_state_hash()
    {
        var c = Busy();
        var back = WarfrontSave.FromJson(c.ToJson());
        Assert.Equal(c.StateHash(), back.StateHash());
        Assert.Equal(c.JoinOrder, back.JoinOrder);
        Assert.Equal(c.Deployments, back.Deployments);
        Assert.Equal(c.Rolls.Count, back.Rolls.Count);
        Assert.Equal(c.Front(1), back.Front(1));
        Assert.Equal(c.Advanced(1), back.Advanced(1));
    }

    [Fact]
    public void Per_player_state_comes_back_keyed_and_nothing_is_granted_twice()
    {
        var c = Busy();
        var p13 = c.Player(13)!;
        long xp13 = p13.Xp;
        Assert.True(p13.JoinGrantApplied);

        WarfrontSave.Write(_dir, c);
        var back = WarfrontSave.Load(_dir)!;
        Assert.Equal(c.Player(11)!.Cards, back.Player(11)!.Cards);
        Assert.Equal("Brother Aurelian", back.Player(12)!.Squad.Single().Name);

        back.Join(13, out var kind);
        Assert.Equal(WarfrontJoinKind.Returning, kind);
        Assert.Equal(xp13, back.Player(13)!.Xp); // no second grant
        back.Join(11, out _);
        Assert.Equal(c.Player(11)!.Xp, back.Player(11)!.Xp);
        Assert.Equal(c.Player(11)!.PicksTaken, back.Player(11)!.PicksTaken);
        Assert.Equal(11UL, back.Leader);
    }

    [Fact]
    public void Write_is_atomic_and_leaves_no_temp_file()
    {
        WarfrontSave.Write(_dir, Busy());
        WarfrontSave.Write(_dir, Busy());
        Assert.True(WarfrontSave.Exists(_dir));
        Assert.Equal(new[] { WarfrontSave.RunFile }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Load_without_a_save_is_null_and_a_bad_version_is_refused()
    {
        Assert.Null(WarfrontSave.Load(_dir));
        var json = Busy().ToJson().Replace("\"Version\":1", "\"Version\":99");
        Assert.Throws<InvalidDataException>(() => WarfrontSave.FromJson(json));
    }

    [Fact]
    public void Archive_moves_the_run_into_records_and_a_new_run_reads_nothing_from_there()
    {
        var c = Busy();
        WarfrontSave.Write(_dir, c);
        var rec = WarfrontSave.Archive(_dir, c, new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
        Assert.True(File.Exists(rec));
        Assert.EndsWith("-abandoned.json", rec);
        Assert.False(WarfrontSave.Exists(_dir));

        // a poisoned record must not leak into a fresh run
        File.WriteAllText(Path.Combine(_dir, WarfrontSave.RecordsDir, "zzz-won.json"), "not json at all");
        Assert.Null(WarfrontSave.Load(_dir));
        var fresh = WarfrontSave.NewRun(1);
        Assert.Empty(fresh.JoinOrder);
        Assert.Empty(fresh.Players);
        Assert.Equal(0, fresh.Deployments);
        for (int l = 0; l < 3; l++) { Assert.Equal(1, fresh.Front(l)); Assert.False(fresh.Advanced(l)); }
        Assert.Equal(WarfrontPhase.Lobby, fresh.Phase);
    }
}
