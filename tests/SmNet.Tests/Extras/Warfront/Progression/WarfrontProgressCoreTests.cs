using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Progression;

/// <summary>Codebase fix 1, task T1: the progress record's pure rules (Sanitize, Better), its wire, the dropped join grant and the newer-save exception.</summary>
public class WarfrontProgressCoreTests
{
    static WarfrontProgressFile Good(long xp = 6000, int picks = 2, params int[] cards) => new() { Xp = xp, PicksTaken = picks, Cards = cards.Length == 0 ? new List<int> { 1, 2 } : new List<int>(cards) };

    // ---- Sanitize ----

    [Fact]
    public void Max_level_is_50_and_xp_over_the_curve_is_clamped()
    {
        Assert.Equal(50, WarfrontProgressFile.LevelForXp(long.MaxValue));
        Assert.Equal(50, WarfrontProgressFile.LevelForXp(WarfrontCampaign.XpForLevel(50)));
        Assert.Equal(49, WarfrontProgressFile.LevelForXp(WarfrontCampaign.XpForLevel(50) - 1));
        var s = WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Xp = 99_999_999_999 });
        Assert.Equal(WarfrontProgressFile.MaxXp, s.Xp);
        Assert.Equal(50, s.Level);
        Assert.Equal(0, WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Xp = -5 }).Xp);
    }

    [Fact]
    public void Picks_never_exceed_level_minus_one_and_the_cards_follow_the_picks()
    {
        var notes = new List<string>();
        var s = WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Xp = 1600, PicksTaken = 40, Cards = Enumerable.Repeat(1, 40).ToList() }, notes);
        Assert.Equal(1, s.PicksTaken);   // level 2: one pick
        Assert.Single(s.Cards);
        Assert.NotEmpty(notes);
        var few = WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Xp = 60000, PicksTaken = 5, Cards = new List<int> { 1, 2 } });
        Assert.Equal(5, few.PicksTaken);   // since record version 2 a pick taken may hold no card (a retired Retainer card): the picks are never raised back by a lost card
        Assert.Equal([1, 2], few.Cards);
    }

    [Fact]
    public void Too_many_cards_unknown_ids_and_too_many_extras_are_trimmed()
    {
        var s = WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Xp = WarfrontProgressFile.MaxXp, PicksTaken = 49, Cards = Enumerable.Repeat(6, 60).ToList() });
        Assert.Empty(s.Cards);                 // the retired Retainer cards are dropped (owner 2026-11-23, card 2) ...
        Assert.Equal(49, s.PicksTaken);        // ... and their picks stay taken
        var u = WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Xp = 60000, PicksTaken = 3, Cards = new List<int> { 0, 9, -1, 1, 2, 3 } });
        Assert.Equal(new[] { 1, 2, 3 }, u.Cards.ToArray());
        var e = WarfrontProgressFile.Sanitize(new WarfrontProgressFile
        {
            Xp = 60000, PicksTaken = 2, Cards = new List<int> { 6, 1 },
            ExtraRetainers = Enumerable.Range(0, 12).Select(i => new WarfrontExtraRetainer { ClassName = "Tactical", Name = "R" + i }).ToList(),
        });
        Assert.Empty(e.ExtraRetainers);    // no retainer is earned since record version 2: every extra is dropped
        Assert.Equal([1], e.Cards);
        Assert.Equal(2, e.PicksTaken);
    }

    [Fact]
    public void Sanitize_cleans_retainer_strings_and_is_idempotent_and_never_throws()
    {
        var f = new WarfrontProgressFile { Xp = 60000, PicksTaken = 1, Cards = new List<int> { 6 }, ExtraRetainers = new List<WarfrontExtraRetainer> { new() { ClassName = "", Name = "  A\u0007B  " + new string('x', 80) } } };
        var s = WarfrontProgressFile.Sanitize(f);
        Assert.Empty(s.ExtraRetainers);   // dropped with the Retainer card since record version 2
        Assert.StartsWith("AB", WarfrontProgressFile.CleanText("  A\u0007B  " + new string('x', 80), WarfrontProgressFile.MaxNameBytes));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(WarfrontProgressFile.CleanText(new string('x', 80), WarfrontProgressFile.MaxNameBytes)) <= WarfrontProgressFile.MaxNameBytes);
        Assert.Equal(s.ToJson(), WarfrontProgressFile.Sanitize(s).ToJson());
        var nulls = WarfrontProgressFile.Sanitize(new WarfrontProgressFile { Cards = null!, ExtraRetainers = null! });
        Assert.Empty(nulls.Cards);
        Assert.Equal(0, WarfrontProgressFile.Sanitize(null).Xp);
    }

    // ---- Better ----

    [Fact]
    public void Better_orders_by_xp_then_picks_then_extras_and_a_full_tie_goes_to_the_host()
    {
        var host = Good(6000, 2); host.Key = "host";
        var player = Good(7000, 2); player.Key = "player";
        Assert.Same(player, WarfrontProgressFile.Better(host, player));
        player.Xp = 6000;
        Assert.Same(host, WarfrontProgressFile.Better(host, player));   // full tie
        player.PicksTaken = 3; player.Cards = new List<int> { 1, 2, 3 };
        Assert.Same(player, WarfrontProgressFile.Better(host, player));
        player.PicksTaken = 2; player.Cards = new List<int> { 1, 2 };
        player.ExtraRetainers.Add(new WarfrontExtraRetainer { ClassName = "Tactical", Name = "A" });
        Assert.Same(player, WarfrontProgressFile.Better(host, player));
        Assert.True(WarfrontProgressFile.ClientMayWrite(host, host.Clone()));
        Assert.False(WarfrontProgressFile.ClientMayWrite(host, player));
        // the winner comes whole: cards and retainers are the winner's own
        var win = WarfrontProgressFile.Better(host, player);
        Assert.Equal(player.Cards, win.Cards);
        Assert.Single(win.ExtraRetainers);
    }

    // ---- JSON ----

    [Fact]
    public void Json_round_trips_and_a_newer_or_damaged_file_is_told_apart_without_throwing()
    {
        var f = Good(); f.Key = "00ff"; f.ResetAt = "2026-11-22T10:00:00.0000000Z";
        Assert.Equal(WarfrontProgressParse.Ok, WarfrontProgressFile.TryParse(f.ToJson(), out var back));
        Assert.Equal(f.ToJson(), back!.ToJson());
        Assert.Equal(WarfrontProgressParse.Newer, WarfrontProgressFile.TryParse("{\"version\":3,\"xp\":\"weird\"}", out _));
        foreach (var junk in new[] { "", "   ", "{", "[]", "{\"xp\":5}", "{\"version\":0}", "{\"version\":1,\"xp\":\"x\"}", "null", "{\"version\":\"1\"}" })
            Assert.Equal(WarfrontProgressParse.Damaged, WarfrontProgressFile.TryParse(junk, out _));
        Assert.Equal(WarfrontProgressParse.Damaged, WarfrontProgressFile.TryParse(null, out _));
    }

    // ---- wire ----

    [Fact]
    public void The_record_round_trips_and_the_longest_record_fits_the_chunk_limit()
    {
        var f = Good(WarfrontProgressFile.MaxXp, WarfrontProgressFile.MaxCards);
        f.Cards = Enumerable.Repeat(1, 41).Concat(Enumerable.Repeat(6, 8)).ToList();
        f.ExtraRetainers = Enumerable.Range(0, 8).Select(i => new WarfrontExtraRetainer { ClassName = new string('c', 16), Name = new string('n', 24) }).ToList();
        var bytes = WarfrontProgressSaveWire.Encode(f, WarfrontProgressSaveWire.FlagReset);
        Assert.Equal(WarfrontProgressSaveWire.MaxRecordBytes, bytes.Length);
        Assert.True(WarfrontProgressSaveWire.TryDecode(bytes, out var rec));
        Assert.True(rec.Reset); Assert.False(rec.Absent);
        Assert.Equal(f.Xp, rec.State!.Xp); Assert.Equal(f.Cards, rec.State.Cards); Assert.Equal(8, rec.State.ExtraRetainers.Count);
        Assert.Equal(f.ExtraRetainers[3].Name, rec.State.ExtraRetainers[3].Name);
        var absent = WarfrontProgressSaveWire.Encode(null);
        Assert.Single(absent);
        Assert.True(WarfrontProgressSaveWire.TryDecode(absent, out var a) && a.Absent && a.State is null);
    }

    [Fact]
    public void The_command_chunks_fit_the_64_byte_command_and_reassemble_in_any_order()
    {
        var f = Good(WarfrontProgressFile.MaxXp, 49);
        f.Cards = Enumerable.Repeat(6, 8).Concat(Enumerable.Repeat(2, 41)).ToList();
        f.ExtraRetainers = Enumerable.Range(0, 8).Select(i => new WarfrontExtraRetainer { ClassName = "Assault", Name = "Brother " + i }).ToList();
        var record = WarfrontProgressSaveWire.Encode(f);
        var chunks = WarfrontProgressSaveWire.CommandChunks(record, 7);
        Assert.All(chunks, c => Assert.True(c.Length <= Protocol.MaxExtrasCommandPayload));
        Assert.True(chunks.Count <= WarfrontProgressSaveWire.MaxChunks);
        var r = new WarfrontProgressSaveWire.Reassembler();
        WarfrontProgressRecord? done = null;
        foreach (var chunk in chunks.Reverse())
        {
            var cmd = WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Progress, Chunk: chunk[1..]));
            Assert.Equal(chunk, cmd);
            Assert.True(WarfrontWire.TryDecode(cmd, out WarfrontClientCommand back));
            Assert.Equal(WarfrontClientCommandKind.Progress, back.Kind);
            done = r.Add(back.Chunk!) ?? done;
        }
        Assert.NotNull(done);
        Assert.Equal(f.Cards, done!.State!.Cards);
        Assert.Equal("Brother 7", done.State.ExtraRetainers[7].Name);
    }

    [Fact]
    public void Random_bytes_never_throw_in_any_progress_decoder()
    {
        var rng = new Random(2026);
        var good = WarfrontProgressSaveWire.Encode(Good());
        for (int i = 0; i < 20000; i++)
        {
            var b = new byte[rng.Next(0, 90)];
            rng.NextBytes(b);
            if (i % 3 == 0 && b.Length > 0) b[0] = (byte)rng.Next(0, 4);
            WarfrontProgressSaveWire.TryDecode(b, out _);
            var cmd = new byte[Math.Min(b.Length + 1, 70)];
            cmd[0] = 5; Array.Copy(b, 0, cmd, 1, cmd.Length - 1);
            WarfrontWire.TryDecode(cmd, out WarfrontClientCommand _);
            new WarfrontProgressSaveWire.Reassembler().Add(b);
            var mutated = (byte[])good.Clone();
            mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
            WarfrontProgressSaveWire.TryDecode(mutated, out _);
        }
        for (int n = 0; n < good.Length; n++) WarfrontProgressSaveWire.TryDecode(good[..n], out _);   // every truncation: no throw
        Assert.False(WarfrontProgressSaveWire.TryDecode(good[..(good.Length - 1)], out _));
        Assert.False(WarfrontProgressSaveWire.TryDecode(good.Concat(new byte[] { 0 }).ToArray(), out _));   // trailing bytes
        Assert.False(WarfrontProgressSaveWire.TryDecode(new byte[] { 4 }, out _));   // unknown flag
    }

    // ---- campaign ----

    [Fact]
    public void ApplyProgress_sets_xp_level_picks_cards_and_extra_retainers_and_clamps_a_bad_record()
    {
        var c = new WarfrontCampaign(1);
        c.Join(11, out _);
        c.Embark();
        var f = new WarfrontProgressFile { Xp = 7000, PicksTaken = 3, Cards = new List<int> { 1, 6, 3 }, ExtraRetainers = new List<WarfrontExtraRetainer> { new() { ClassName = "Devastator", Name = "Sevatar" } } };
        Assert.True(c.ApplyProgress(11, f));
        var p = c.Player(11)!;
        Assert.Equal(7000, p.Xp);
        Assert.Equal(WarfrontCampaign.LevelForXp(7000), p.Level);
        Assert.Equal(3, p.PicksTaken);
        Assert.Equal(new[] { 1, 3 }, p.Cards.ToArray());   // the retired Retainer card is dropped, its pick stays taken (owner 2026-11-23, card 2)
        Assert.Empty(p.Squad);                              // and no retainer is earned
        Assert.Equal(WarfrontProgressFile.Sanitize(f).Xp, c.ProgressOf(11)!.Xp);
        Assert.False(c.ApplyProgress(999, f));
        Assert.True(c.ApplyProgress(11, new WarfrontProgressFile { Xp = long.MaxValue, PicksTaken = 500 }));
        Assert.Equal(WarfrontProgressFile.MaxXp, p.Xp);
        Assert.Equal(50, p.Level);
    }

    [Fact]
    public void The_join_grant_no_longer_grants_xp()
    {
        var c = new WarfrontCampaign(1);
        c.Join(1, out _); c.Join(2, out _);
        c.AddXp(1, 50000);
        Assert.False(c.ApplyJoinGrant(2));
        Assert.Equal(0, c.Player(2)!.Xp);
        Assert.True(c.Player(2)!.JoinGrantApplied);
    }

    [Fact]
    public void A_save_of_a_newer_version_throws_the_newer_exception_and_garbage_stays_a_plain_failure()
    {
        var c = new WarfrontCampaign(5);
        c.Join(1, out _);
        string json = c.ToJson().Replace("\"Version\":" + WarfrontCampaign.SaveVersion, "\"Version\":7");
        Assert.Contains("\"Version\":7", json);
        var ex = Assert.Throws<WarfrontSaveNewerException>(() => WarfrontSave.FromJson(json));
        Assert.Equal(7, ex.FoundVersion);
        // a newer file need not even fit this build's fields
        Assert.Throws<WarfrontSaveNewerException>(() => WarfrontSave.FromJson("{\"Version\":9,\"Phase\":\"Mystery\",\"Fronts\":\"x\"}"));
        Assert.Throws<InvalidDataException>(() => WarfrontSave.FromJson(c.ToJson().Replace("\"Version\":" + WarfrontCampaign.SaveVersion, "\"Version\":0")));
        Assert.ThrowsAny<Exception>(() => WarfrontSave.FromJson("not json"));
        Assert.IsNotType<WarfrontSaveNewerException>(Record.Exception(() => WarfrontSave.FromJson("not json")));
    }
}
