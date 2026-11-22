using SmClient.Extras.Warfront.Stage;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.ClientStage;

/// <summary>Codebase fix 1, the client side (no Godot): the progress file's IO (main, .bak, newer, key mismatch, reset) and the Progress command's resend until the first ProgressSave. Temp folders only, never the owner's.</summary>
public sealed class WarfrontProgressStoreTests : IDisposable
{
    const ulong Key = 0xA1B2C3D4E5F60718, OtherKey = 0x1111222233334444;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-progress-" + Guid.NewGuid().ToString("N"));

    public WarfrontProgressStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    WarfrontProgressStore Store(ulong key = Key) => new(Path.Combine(_dir, "progress.json"), key) { Log = _ => { } };

    static WarfrontProgressFile File5(ulong key, long xp, int picks = 0, params int[] cards)
    {
        var f = WarfrontProgressFile.Fresh(key.ToString("x16"));
        f.Xp = xp; f.PicksTaken = picks; f.Cards = cards.ToList();
        return f;
    }

    [Fact]
    public void Save_is_atomic_and_the_second_save_leaves_a_bak_of_the_first()
    {
        var s = Store();
        Assert.Equal(WarfrontProgressLoad.Absent, s.Load());
        Assert.True(s.Save(File5(Key, 2000, 1, 1)));
        Assert.False(File.Exists(s.Path + ".tmp"));
        Assert.False(File.Exists(s.BakPath));
        Assert.True(s.Save(File5(Key, 4000, 2, 1, 2)));
        Assert.True(File.Exists(s.BakPath));
        Assert.Equal(2000, WarfrontProgressFile.TryParse(File.ReadAllText(s.BakPath), out var old) == WarfrontProgressParse.Ok ? old!.Xp : -1);
        var again = Store();
        Assert.Equal(WarfrontProgressLoad.Ok, again.Load());
        Assert.Equal(4000, again.Current!.Xp);
        Assert.Equal(Key.ToString("x16"), again.Current.Key);
    }

    [Fact]
    public void A_damaged_main_file_falls_back_to_the_bak_and_both_damaged_is_absent()
    {
        var s = Store();
        Assert.True(s.Save(File5(Key, 2000, 1, 1)));
        Assert.True(s.Save(File5(Key, 4000, 2, 1, 2)));
        File.WriteAllText(s.Path, "{ this is not json");
        var r = Store();
        Assert.Equal(WarfrontProgressLoad.Ok, r.Load());
        Assert.Equal(2000, r.Current!.Xp);
        File.WriteAllText(s.BakPath, "");
        Assert.Equal(WarfrontProgressLoad.Absent, Store().Load());
        Assert.Equal("{ this is not json", File.ReadAllText(s.Path));   // a damaged file is never deleted by a load
    }

    [Fact]
    public void A_newer_file_is_never_touched_by_a_load_a_save_a_host_copy_or_a_reset()
    {
        string path = Path.Combine(_dir, "progress.json");
        const string text = "{\"version\": 2, \"key\": \"a1b2c3d4e5f60718\", \"xp\": 99, \"somethingNew\": [1,2,3]}";
        File.WriteAllText(path, text);
        var s = Store();
        Assert.Equal(WarfrontProgressLoad.Newer, s.Load());
        Assert.True(s.Newer);
        Assert.Null(s.Current);
        Assert.False(s.Save(File5(Key, 9000)));
        Assert.False(s.ApplyHostCopy(File5(Key, 9000)));
        Assert.False(s.Reset(DateTime.UtcNow));
        Assert.Equal(text, File.ReadAllText(path));
        Assert.False(File.Exists(s.BakPath));
        Assert.False(File.Exists(s.BeforeResetPath));
    }

    [Fact]
    public void A_file_of_another_key_is_treated_as_absent_and_kept_when_the_first_new_write_replaces_it()
    {
        var other = Store(OtherKey);
        Assert.True(other.Save(File5(OtherKey, 5000, 1, 1)));
        string before = File.ReadAllText(other.Path);
        var s = Store();
        Assert.Equal(WarfrontProgressLoad.KeyMismatch, s.Load());
        Assert.Null(s.Current);
        Assert.Equal(before, File.ReadAllText(s.Path));   // loading keeps it
        Assert.True(s.ApplyHostCopy(File5(Key, 100)));   // the host's copy becomes this key's first file
        Assert.Equal(before, File.ReadAllText(s.OtherKeyCopyPath));
        Assert.Equal(WarfrontProgressLoad.Ok, Store().Load());
    }

    [Fact]
    public void The_client_never_lowers_its_own_file_and_a_tie_or_better_host_copy_is_written()
    {
        var s = Store();
        Assert.True(s.Save(File5(Key, 6000, 2, 1, 2)));
        string json = File.ReadAllText(s.Path);
        Assert.False(s.ApplyHostCopy(File5(Key, 3000, 1, 1)));        // lower xp
        Assert.False(s.ApplyHostCopy(File5(Key, 6000, 1, 1)));        // same xp, fewer picks
        Assert.False(s.ApplyHostCopy(File5(Key, 6000, 2, 1, 2)));     // a full tie with the same cards: nothing new, no write
        Assert.Equal(json, File.ReadAllText(s.Path));
        Assert.True(s.ApplyHostCopy(File5(Key, 7000, 2, 1, 2)));
        Assert.Equal(7000, Store().Tap().Xp);
        Assert.True(s.ApplyHostCopy(File5(Key, 7000, 2, 2, 1)));       // a tie with different cards: the host's copy wins whole
        Assert.Equal(new[] { 2, 1 }, Store().Tap().Cards.ToArray());
    }

    [Fact]
    public void A_host_copy_with_no_file_here_becomes_the_first_file_and_is_sanitized()
    {
        var s = Store();
        s.Load();
        var bad = File5(Key, 50_000_000_000, 99, 1, 1, 1);   // far past the curve
        Assert.True(s.ApplyHostCopy(bad));
        var f = Store().Tap();
        Assert.Equal(WarfrontProgressFile.MaxXp, f.Xp);
        Assert.True(f.PicksTaken <= WarfrontProgressFile.MaxLevel - 1);
    }

    [Fact]
    public void Reset_keeps_one_safety_copy_writes_a_level_1_file_and_drops_the_bak()
    {
        var s = Store();
        Assert.True(s.Save(File5(Key, 2000, 1, 1)));
        Assert.True(s.Save(File5(Key, 4000, 2, 1, 2)));
        Assert.True(File.Exists(s.BakPath));
        string old = File.ReadAllText(s.Path);
        var at = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(s.Reset(at));
        Assert.Equal(old, File.ReadAllText(s.BeforeResetPath));
        Assert.False(File.Exists(s.BakPath));
        var f = Store().Tap();
        Assert.Equal(1, f.Level);
        Assert.Equal(0, f.Xp);
        Assert.Empty(f.Cards);
        Assert.Empty(f.ExtraRetainers);
        Assert.Equal(Key.ToString("x16"), f.Key);
        Assert.StartsWith("2026-11-22T10:00:00", f.ResetAt);
        Assert.True(WarfrontProgressStore.IsPendingReset(f));
        // a second reset replaces the one safety copy with what was current then
        Assert.True(s.Save(File5(Key, 1700, 0)));
        string second = File.ReadAllText(s.Path);
        Assert.True(s.Reset(at.AddHours(1)));
        Assert.Equal(second, File.ReadAllText(s.BeforeResetPath));
        Assert.Single(Directory.GetFiles(_dir, "*before-reset*"));
    }

    [Fact]
    public void Reset_with_no_file_still_writes_the_level_1_file_and_a_progressed_file_is_not_a_pending_reset()
    {
        var s = Store();
        Assert.True(s.Reset(DateTime.UtcNow));
        Assert.False(File.Exists(s.BeforeResetPath));
        Assert.True(WarfrontProgressStore.IsPendingReset(Store().Tap()));
        var progressed = Store().Tap();
        progressed.Xp = 1600;
        Assert.False(WarfrontProgressStore.IsPendingReset(progressed));
    }

    [Fact]
    public void The_path_follows_the_key_file()
    {
        Assert.Equal(Path.Combine("x", "key-a.json") + ".progress.json", WarfrontProgressStore.PathFor(Path.Combine("x", "key-a.json")));
        Assert.EndsWith("progress.json", WarfrontProgressStore.PathFor(null));
        var s = new WarfrontProgressStore(Path.Combine(_dir, "k.json.progress.json"), Key);
        Assert.EndsWith("k.json.progress-before-reset.json", s.BeforeResetPath);
    }

    // ---- the Progress command and its acknowledgement ----------------------------------------------------------------------------------------

    static WarfrontStageModel Model(WarfrontProgressFile? own, bool newer = false)
    {
        var m = new WarfrontStageModel(Key, "Alpha");
        m.SetProgress(own, newer);
        return m;
    }

    static WarfrontProgressRecord Decode(IReadOnlyList<byte[]> chunks)
    {
        var r = new WarfrontProgressSaveWire.Reassembler();
        WarfrontProgressRecord? done = null;
        foreach (var c in chunks) { Assert.Equal(WarfrontProgressSaveWire.CommandKind, c[0]); done = r.Add(c.AsSpan(1)); }
        return done!;
    }

    [Fact]
    public void ProgressDue_waits_for_the_identify_then_resends_every_second_until_the_first_tag_49()
    {
        var own = File5(Key, 4000, 2, 1, 2);
        var m = Model(own);
        Assert.Null(m.ProgressDue(0));                    // Identify has not gone out yet
        Assert.NotNull(m.IdentifyDue(0));
        var first = m.ProgressDue(0.1);
        Assert.NotNull(first);
        var rec = Decode(first!);
        Assert.Equal(0, rec.Flags);
        Assert.Equal(4000, rec.State!.Xp);
        Assert.Equal(new[] { 1, 2 }, rec.State.Cards.ToArray());
        Assert.Null(m.ProgressDue(0.5));
        Assert.NotNull(m.ProgressDue(1.2));               // resent
        Assert.NotNull(m.ProgressDue(2.3));
        var ack = WarfrontProgressSaveWire.Encode(own);
        Assert.NotNull(m.ApplyProgressSave(ack));
        Assert.True(m.ProgressAcked);
        Assert.Null(m.ProgressDue(10));                   // acknowledged: stops
    }

    [Fact]
    public void ProgressDue_sends_flag_1_without_a_file_and_flag_2_for_a_pending_reset_and_nothing_for_a_newer_file()
    {
        var none = Model(null);
        none.IdentifyDue(0);
        var rec = Decode(none.ProgressDue(0)!);
        Assert.True(rec.Absent);
        Assert.Null(rec.State);

        var fresh = WarfrontProgressFile.Fresh(Key.ToString("x16"), "2026-11-22T10:00:00.0000000Z");
        var reset = Model(fresh);
        reset.IdentifyDue(0);
        var r2 = Decode(reset.ProgressDue(0)!);
        Assert.True(r2.Reset);
        Assert.Equal(0, r2.State!.Xp);

        var newer = Model(null, newer: true);
        newer.IdentifyDue(0);
        Assert.Null(newer.ProgressDue(0));
        Assert.Null(newer.ProgressRecord());
    }

    [Fact]
    public void ProgressDue_gives_up_after_the_cap_and_a_malformed_tag_49_is_not_an_acknowledgement()
    {
        var m = Model(File5(Key, 100));
        m.IdentifyDue(0);
        int sent = 0;
        for (int i = 0; i < 100; i++) if (m.ProgressDue(i * 2.0) is not null) sent++;
        Assert.Equal(WarfrontStageModel.MaxProgressSends, sent);
        var m2 = Model(File5(Key, 100));
        Assert.Null(m2.ApplyProgressSave(new byte[] { 9, 9, 9 }));
        Assert.False(m2.ProgressAcked);
    }
}

static class ProgressStoreTestExtensions
{
    /// <summary>The store's file as it is on disk now (loads it).</summary>
    public static WarfrontProgressFile Tap(this WarfrontProgressStore s)
    {
        Assert.Equal(WarfrontProgressLoad.Ok, s.Load());
        return s.Current!;
    }
}
