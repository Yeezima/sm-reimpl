using SmNet.Extras.Warfront.Results;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Results;

/// <summary>Warfront plan section 7 (slice W15): score, ranking, top retainer, export, record, honor roll, wire. Pure tests; temp directories only.</summary>
public class WarfrontResultsTests : IDisposable
{
    const ulong KeyA = 7777123456789UL, KeyB = 7777987654321UL, KeyC = 7777555500001UL;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wfres-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static WarfrontContributor P(string name, int join, int kills = 0, int elite = 0, int boss = 0, int assists = 0, int deaths = 0, int revives = 0, int caps = 0, bool conn = true,
        params WarfrontRetainerStats[] squad) =>
        new(new WarfrontPlayerStats(name, conn, join, kills, elite, boss, assists, deaths, revives, caps), squad);

    static IReadOnlyList<WarfrontLeaderRow> Rank(WarfrontScoreWeights? w, params WarfrontContributor[] c) => WarfrontLeaderboard.Rank(c, w);

    // ---- score ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Score_uses_the_table_and_elite_and_boss_replace_a_plain_kill()
    {
        var row = Rank(null, P("A", 0, kills: 10, elite: 2, boss: 1, assists: 4, revives: 2, caps: 1))[0];
        // 7 plain x1 + 2 elite x5 + 1 boss x25 + 4 assists x0.5 + 1 capture x30 + 2 revives x15
        Assert.Equal(7m + 10m + 25m + 2m + 30m + 30m, row.Total);
    }

    [Fact]
    public void Deaths_and_knockouts_never_add_points()
    {
        var squadA = new[] { new WarfrontRetainerStats("M", Kills: 10, Knockouts: 0) };
        var squadB = new[] { new WarfrontRetainerStats("M", Kills: 10, Knockouts: 99) };
        var a = Rank(null, P("A", 0, kills: 20, deaths: 0, squad: squadA))[0];
        var b = Rank(null, P("A", 0, kills: 20, deaths: 50, squad: squadB))[0];
        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.PlayerScore, b.PlayerScore);
        Assert.Equal(a.SquadScore, b.SquadScore);
    }

    [Fact]
    public void Negative_weights_are_refused_so_a_death_cannot_subtract()
    {
        Assert.False((WarfrontScoreWeights.Default with { Kill = -1m }).IsValid);
        var c = new WarfrontCampaign(1);
        c.Join(KeyA, "A", out _);
        Assert.Throws<ArgumentException>(() => WarfrontResults.Build(c, null, TimeSpan.Zero, DateTime.UtcNow, WarfrontScoreWeights.Default with { Capture = -5m }));
    }

    [Fact]
    public void Changing_a_weight_changes_the_ranking_as_expected()
    {
        var killer = P("Killer", 0, kills: 100);
        var holder = P("Holder", 1, caps: 4);
        Assert.Equal(new[] { "Holder", "Killer" }, Rank(null, killer, holder).Select(r => r.Name));   // 120 against 100
        var tuned = WarfrontScoreWeights.Default with { Capture = 5m };
        Assert.Equal(new[] { "Killer", "Holder" }, Rank(tuned, killer, holder).Select(r => r.Name));   // 100 against 20
        var kills = WarfrontScoreWeights.Default with { Kill = 2m };
        Assert.Equal(200m, Rank(kills, killer)[0].Total);
    }

    [Fact]
    public void The_squad_counts_in_the_total_and_stays_separate_on_the_row()
    {
        var squad = new[] { new WarfrontRetainerStats("One", Kills: 20, Revives: 1), new WarfrontRetainerStats("Two", Kills: 10, Knockouts: 3) };
        var row = Rank(null, P("A", 0, kills: 4, squad: squad))[0];
        Assert.Equal(4m, row.PlayerScore);
        Assert.Equal(30 * 0.5m + 15m, row.SquadScore);
        Assert.Equal(row.PlayerScore + row.SquadScore, row.Total);
        Assert.Equal(new WarfrontSquadTotals(2, 30, 0, 3, 1, 0), row.Squad);
        Assert.Equal(34, row.TotalKills);
    }

    // ---- tie-breaks -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ties_break_on_captures_then_revives_then_fewer_deaths_then_earlier_join()
    {
        // all five total 30
        var byCaps = P("Caps", 4, caps: 1);                         // 30
        var byRevive = P("Revive", 3, revives: 2);                   // 30, no captures
        var fewDeaths = P("FewDeaths", 2, kills: 30, deaths: 1);     // 30
        var manyDeaths = P("ManyDeaths", 1, kills: 30, deaths: 5);   // 30
        var late = P("Late", 6, kills: 30, deaths: 1);               // 30, same as FewDeaths but joined later
        var order = Rank(null, late, manyDeaths, byRevive, fewDeaths, byCaps).Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "Caps", "Revive", "FewDeaths", "Late", "ManyDeaths" }, order);
    }

    [Fact]
    public void The_order_does_not_depend_on_the_input_order()
    {
        var all = new[] { P("A", 0, kills: 5), P("B", 1, kills: 5), P("C", 2, kills: 5), P("D", 3, kills: 9), P("E", 4) };
        var expect = Rank(null, all).Select(r => r.Name).ToArray();
        var rng = new Random(12);
        for (int i = 0; i < 20; i++)
            Assert.Equal(expect, Rank(null, all.OrderBy(_ => rng.Next()).ToArray()).Select(r => r.Name).ToArray());
        Assert.Equal(new[] { "D", "A", "B", "C", "E" }, expect);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Rank(null, all).Select(r => r.Rank));
    }

    [Fact]
    public void The_tie_break_order_is_data()
    {
        var a = P("A", 0, kills: 30, deaths: 0);
        var b = P("B", 1, caps: 1, deaths: 9);
        Assert.Equal("B", Rank(null, a, b)[0].Name);
        var tuned = WarfrontScoreWeights.Default with { TieBreaks = new[] { WarfrontTieBreak.FewerDeaths, WarfrontTieBreak.Captures } };
        Assert.Equal("A", Rank(tuned, a, b)[0].Name);
    }

    // ---- top retainer ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Top_retainer_is_the_best_scorer_with_the_reason()
    {
        var squad = new[]
        {
            new WarfrontRetainerStats("Cato", "Tactical", 1, Kills: 212, EliteKills: 9, BossKills: 1),
            new WarfrontRetainerStats("Brutus", "Assault", 2, Kills: 150, Knockouts: 0),
            new WarfrontRetainerStats("Medic", "Tactical", 3, Kills: 4, Revives: 5),
        };
        var top = Rank(null, P("A", 0, squad: squad))[0].TopRetainer!;
        Assert.Equal("Cato", top.Name);
        Assert.Equal("Tactical", top.ClassName);
        Assert.Equal("most kills: 212 (9 elite, 1 boss)", top.Reason);
        Assert.Equal(106m, top.Score);

        var healer = new[] { new WarfrontRetainerStats("Medic", Kills: 4, Revives: 5), new WarfrontRetainerStats("Cato", Kills: 100) };
        var t2 = WarfrontLeaderboard.TopRetainer(healer, WarfrontScoreWeights.Default)!;
        Assert.Equal("Medic", t2.Name);
        Assert.Equal("most revives: 5", t2.Reason);
    }

    [Fact]
    public void Top_retainer_ties_go_to_more_kills_fewer_knockouts_then_name_and_empty_squads_have_none()
    {
        var squad = new[] { new WarfrontRetainerStats("Zed", Kills: 30, Knockouts: 2), new WarfrontRetainerStats("Amos", Kills: 30, Knockouts: 1), new WarfrontRetainerStats("Bo", Kills: 30, Knockouts: 1) };
        Assert.Equal("Amos", WarfrontLeaderboard.TopRetainer(squad, WarfrontScoreWeights.Default)!.Name);
        Assert.Null(WarfrontLeaderboard.TopRetainer(Array.Empty<WarfrontRetainerStats>(), WarfrontScoreWeights.Default));
        Assert.Equal("no score yet", WarfrontLeaderboard.TopRetainer(new[] { new WarfrontRetainerStats("Idle", Knockouts: 4) }, WarfrontScoreWeights.Default)!.Reason);
    }

    // ---- building the record from a campaign ----------------------------------------------------------------------------------------------

    static WarfrontCampaign Campaign()
    {
        var c = new WarfrontCampaign(9, new ForcedRolls(false, false, false));
        c.Join(KeyA, "Alpha", out _);
        c.Join(KeyB, "Bravo", out _);
        c.Embark();
        c.Leave(KeyB);   // Bravo disconnected before the end
        return c;
    }

    static Dictionary<ulong, WarfrontContributor> Stats() => new()
    {
        [KeyA] = P("ignored", 99, kills: 40, deaths: 2, caps: 1, squad: new[] { new WarfrontRetainerStats("Cato", "Tactical", Kills: 60, Revives: 2) }),
        [KeyB] = P("ignored", 99, kills: 50, assists: 10, deaths: 1),
        [KeyC] = P("Charlie", 0, revives: 1, conn: false),   // left, and the campaign no longer knows the key
    };

    [Fact]
    public void Every_contributor_is_a_row_and_a_disconnected_one_is_marked()
    {
        var rec = WarfrontResults.Build(Campaign(), Stats(), TimeSpan.FromMinutes(75), new DateTime(2026, 11, 17, 14, 3, 5, DateTimeKind.Utc));
        Assert.Equal(3, rec.Rows.Count);
        var bravo = rec.Rows.Single(r => r.Name == "Bravo");
        Assert.False(bravo.Connected);                       // the campaign's flag wins for a known player
        Assert.True(rec.Rows.Single(r => r.Name == "Alpha").Connected);
        var charlie = rec.Rows.Single(r => r.Name == "Charlie");
        Assert.False(charlie.Connected);
        Assert.Equal(2, charlie.JoinIndex);                  // after the campaign's own join order
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, rec.Rows.OrderBy(r => r.JoinIndex).Select(r => r.Name));
        Assert.Equal("abandoned", rec.Result);
        Assert.Equal(4500, rec.DurationSeconds);
    }

    [Fact]
    public void A_player_without_figures_still_gets_a_zero_row_and_the_saved_name()
    {
        var rec = WarfrontResults.Build(Campaign(), null, TimeSpan.Zero, DateTime.UtcNow);
        Assert.Equal(new[] { "Alpha", "Bravo" }, rec.Rows.Select(r => r.Name));
        Assert.All(rec.Rows, r => Assert.Equal(0m, r.Total));
    }

    [Fact]
    public void Won_and_lost_runs_are_named_from_the_phase()
    {
        var c = Campaign();
        foreach (var (phase, name) in new[] { ("Won", "won"), ("Lost", "lost") })
        {
            var won = WarfrontSave.FromJson(c.ToJson().Replace("\"Phase\":\"Ship\"", "\"Phase\":\"" + phase + "\""));
            var rec = WarfrontResults.Build(won, null, TimeSpan.Zero, DateTime.UtcNow);
            Assert.Equal(name, rec.Result);
        }
    }

    [Fact]
    public void Names_are_cleaned_for_a_text_block()
    {
        Assert.Equal("a@​everyone", WarfrontResults.Clean("a@everyone"));
        Assert.Equal("a@​everyone", WarfrontResults.Clean(WarfrontResults.Clean("a@everyone")));   // idempotent
        Assert.Equal("ab", WarfrontResults.Clean("`a\nb\t"));
        Assert.Equal("Player 3", WarfrontResults.Clean("   ", "Player 3"));
        Assert.Equal(24, WarfrontResults.Clean(new string('x', 80)).Length);
    }

    // ---- the export text ------------------------------------------------------------------------------------------------------------------

    static WarfrontRunRecord GoldenRecord()
    {
        var c = Campaign();
        var stats = new Dictionary<ulong, WarfrontContributor>
        {
            [KeyA] = P("Alpha", 0, kills: 310, elite: 12, boss: 1, assists: 40, deaths: 2, revives: 5, caps: 3,
                squad: new[]
                {
                    new WarfrontRetainerStats("Cato", "Tactical", 1, Kills: 212, EliteKills: 9, BossKills: 1, Knockouts: 2, Revives: 3),
                    new WarfrontRetainerStats("Brutus", "Assault", 2, Kills: 90, Knockouts: 4, Revives: 1),
                }),
            [KeyB] = P("Bravo", 1, kills: 120, assists: 15, deaths: 0, revives: 1,
                squad: new[] { new WarfrontRetainerStats("Dio", "Devastator", 1, Kills: 30, Knockouts: 1) }),
        };
        return WarfrontResults.Build(c, stats, TimeSpan.FromSeconds(4325), new DateTime(2026, 11, 17, 14, 3, 5, DateTimeKind.Utc));
    }

    [Fact]
    public void Export_text_matches_the_golden_block()
    {
        const string golden =
            "WARFRONT - RUN ABANDONED\n" +
            "2026-11-17 14:03 UTC | run time 1:12:05 | 0 battles | rolls lost 0 of 0\n" +
            "\n" +
            "                                        PLAYER             SQUAD\n" +
            " #  NAME                       SCORE    K   A  D REV CAP  |     K  KO REV  TOT K\n" +
            " 1  Alpha                        778  310  40  2   5   3  |   302   6   4    612\n" +
            "      Top retainer: Cato (Tactical) - most kills: 212 (9 elite, 1 boss)\n" +
            " 2  Bravo*                     157.5  120  15  0   1   0  |    30   1   0    150\n" +
            "      Top retainer: Dio (Devastator) - most kills: 30\n" +
            "\n" +
            "* left before the end\n" +
            "\n" +
            "Score = kills x1, elite x5, boss x25, assists x0.5, captures x30, revives x15, squad kills x0.5, squad revives x15. Deaths and knockouts never add. Ties: captures, revives, fewer deaths, earlier join.\n";
        Assert.Equal(golden, WarfrontExport.Text(GoldenRecord()));
        Assert.Equal("```\n" + golden.TrimEnd('\n') + "\n```", WarfrontExport.Discord(GoldenRecord()));
    }

    [Fact]
    public void No_campaign_key_reaches_any_export()
    {
        var rec = GoldenRecord();
        var wire = System.Text.Encoding.UTF8.GetString(WarfrontResultsWire.Encode(rec));
        foreach (var key in new[] { KeyA, KeyB, KeyC })
            foreach (var text in new[] { WarfrontExport.Text(rec), WarfrontExport.Discord(rec), rec.ToJson(), wire, WarfrontHonorRoll.Build(new[] { ("s", rec) }).Text() })
                Assert.DoesNotContain(key.ToString(), text);
        // and not through a file export
        var path = WarfrontResultsArchive.Export(_dir, rec, DateTime.UtcNow, markdown: true);
        Assert.DoesNotContain(KeyA.ToString(), File.ReadAllText(path));
        Assert.DoesNotContain("Users", File.ReadAllText(path));
    }

    [Fact]
    public void Discord_copy_stays_under_the_message_limit_even_for_a_crowded_roll()
    {
        var rows = Enumerable.Range(0, 32).Select(i => P("Player number " + i, i, kills: 100 + i, squad: new[] { new WarfrontRetainerStats("Marine with a long name " + i, "Tactical", Kills: 50) })).ToArray();
        var rec = new WarfrontRunRecord("won", "2026-11-17T00:00:00Z", 100, WarfrontScoreWeights.Default, Array.Empty<string>(), 0, 0, 0, Rank(null, rows));
        var s = WarfrontExport.Discord(rec);
        Assert.True(s.Length <= WarfrontExport.DiscordLimit, s.Length.ToString());
        Assert.StartsWith("```", s);
        Assert.EndsWith("```", s);
        Assert.Contains("Player number 31", s);   // the top rows are kept
    }

    // ---- files: records, exports, new run -------------------------------------------------------------------------------------------------

    [Fact]
    public void Archive_appends_the_results_record_beside_the_run_and_exports_go_to_their_folder()
    {
        var c = Campaign();
        var rec = GoldenRecord();
        var utc = new DateTime(2026, 11, 17, 14, 3, 5, DateTimeKind.Utc);
        var run = WarfrontResultsArchive.Archive(_dir, c, rec, utc);
        Assert.True(File.Exists(run));
        var results = Path.Combine(_dir, "records", "20261117T140305Z-abandoned.results.json");
        Assert.True(File.Exists(results));
        Assert.Equal(rec.Rows.Select(r => r.Name), WarfrontRunRecord.FromJson(File.ReadAllText(results)).Rows.Select(r => r.Name));
        var txt = WarfrontResultsArchive.Export(_dir, rec, utc, markdown: false);
        var md = WarfrontResultsArchive.Export(_dir, rec, utc, markdown: true);
        var again = WarfrontResultsArchive.Export(_dir, rec, utc, markdown: false);
        Assert.Equal(Path.Combine(_dir, "exports", "20261117T140305Z.txt"), txt);
        Assert.EndsWith(".md", md);
        Assert.EndsWith("-2.txt", again);
        Assert.Equal(WarfrontExport.Text(rec), File.ReadAllText(txt));
    }

    [Fact]
    public void A_new_run_starts_empty_whatever_the_records_hold()
    {
        var c = Campaign();
        WarfrontResultsArchive.Archive(_dir, c, GoldenRecord(), DateTime.UtcNow);
        Assert.False(WarfrontSave.Exists(_dir));              // nothing to continue
        Assert.Null(WarfrontSave.Load(_dir));
        var fresh = WarfrontSave.NewRun(5);
        Assert.Empty(fresh.Players);
        Assert.Empty(fresh.History);
        Assert.Empty(fresh.JoinOrder);
        Assert.Equal(WarfrontPhase.Lobby, fresh.Phase);
        // the results of a run with no one in it are empty rows, not the old ones
        var empty = WarfrontResults.Build(fresh, null, TimeSpan.Zero, DateTime.UtcNow);
        Assert.Empty(empty.Rows);
        Assert.Equal(1, WarfrontResultsArchive.HonorRoll(_dir).Runs);   // the roll is a view; it is only read when asked for
    }

    // ---- record, wire, honor roll ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_record_round_trips_through_json_and_the_wire()
    {
        var rec = GoldenRecord();
        var j = WarfrontRunRecord.FromJson(rec.ToJson());
        Assert.Equal(rec.Rows.Select(r => (r.Rank, r.Name, r.Total, r.Connected)), j.Rows.Select(r => (r.Rank, r.Name, r.Total, r.Connected)));
        Assert.Equal(rec.Weights.FormulaText(), j.Weights.FormulaText());
        Assert.Equal(WarfrontExport.Text(rec), WarfrontExport.Text(j));

        var w = WarfrontResultsWire.Decode(WarfrontResultsWire.Encode(rec));
        Assert.Equal(WarfrontExport.Text(rec), WarfrontExport.Text(w));
        Assert.Equal(rec.Rows[0].Members!.Select(m => m.Name), w.Rows[0].Members!.Select(m => m.Name));
        Assert.Equal(rec.Rows[0].TopRetainer, w.Rows[0].TopRetainer);
    }

    [Fact]
    public void The_wire_rejects_garbage_and_truncation_without_a_crash()
    {
        var bytes = WarfrontResultsWire.Encode(GoldenRecord());
        for (int cut = 0; cut < bytes.Length; cut += 7)
            Assert.Throws<InvalidDataException>(() => WarfrontResultsWire.Decode(bytes.AsSpan(0, cut)));
        Assert.Throws<InvalidDataException>(() => WarfrontResultsWire.Decode(bytes.Concat(new byte[] { 1 }).ToArray()));
        var bad = (byte[])bytes.Clone(); bad[0] = 9;
        Assert.Throws<InvalidDataException>(() => WarfrontResultsWire.Decode(bad));
        var rng = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            var junk = new byte[rng.Next(0, 300)];
            rng.NextBytes(junk);
            junk = junk.Length > 0 ? junk : new byte[] { 1 };
            junk[0] = WarfrontResultsWire.Format;
            try { WarfrontResultsWire.Decode(junk); } catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void The_honor_roll_catalogues_every_record_and_does_not_depend_on_order()
    {
        var a = GoldenRecord();
        var b = a with { Result = "won", DurationSeconds = 3000, Rows = Rank(null, P("Alpha", 0, kills: 10), P("Zed", 1, kills: 500, revives: 9, caps: 7)) };
        var c = a with { Result = "lost" };
        var roll = WarfrontHonorRoll.Build(new[] { ("20261118T000000Z-won", b), ("20261117T000000Z-abandoned", a), ("20261119T000000Z-lost", c) });
        Assert.Equal((3, 1, 1, 1), (roll.Runs, roll.Won, roll.Lost, roll.Abandoned));
        Assert.Equal("Zed", roll.Entries.Single(e => e.Title == "Highest score").Holder);
        Assert.Equal("0:50:00", roll.Entries.Single(e => e.Title == "Fastest victory").Value);
        Assert.Equal("Cato (Alpha's squad)", roll.Entries.Single(e => e.Title == "Best retainer").Holder);
        Assert.Equal("Alpha", roll.Players[0].Name);
        Assert.Equal("Alpha, Zed", roll.Entries.Single(e => e.Title == "Fastest victory").Holder);   // that run's players, not a label
        Assert.Equal(3, roll.Players.Single(p => p.Name == "Alpha").Runs);
        var shuffled = WarfrontHonorRoll.Build(new[] { ("20261119T000000Z-lost", c), ("20261117T000000Z-abandoned", a), ("20261118T000000Z-won", b) });
        Assert.Equal(roll.Text(), shuffled.Text());
        Assert.Equal(0, WarfrontHonorRoll.Build(Array.Empty<(string, WarfrontRunRecord)>()).Runs);
    }

    // ---- second pass ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_largest_legal_record_splits_into_chunks_that_each_fit_one_extras_section()
    {
        var rows = Enumerable.Range(0, 4).Select(i => P(new string((char)('A' + i), 24), i, kills: 99999, elite: 99, boss: 9, assists: 9999, deaths: 999, revives: 99, caps: 9, squad:
            Enumerable.Range(0, 9).Select(m => new WarfrontRetainerStats(new string((char)('a' + m), 24), "Devastator Marine", m, 99999, 99, 9, 999, 99, 99, 9, 99)).ToArray())).ToArray();
        var battles = Enumerable.Range(0, 60).Select(b => "#" + b + " Node lane 3: Win").ToList();
        var rec = new WarfrontRunRecord("won", "2026-11-17T14:03:05Z", 99999, WarfrontScoreWeights.Default, battles, 60, 30, 5, Rank(null, rows));
        var whole = WarfrontResultsWire.Encode(rec);
        Assert.True(whole.Length > SmNet.Protocol.MaxExtrasPayloadBytes, "the test must need more than one section");
        var chunks = WarfrontResultsWire.Split(whole, 7);
        Assert.All(chunks, c => Assert.True(c.Length <= SmNet.Protocol.MaxExtrasPayloadBytes));
        // each chunk really goes into an extras message
        foreach (var c in chunks) Assert.True(SmNet.Protocol.Write(new SmNet.ExtrasUpdate(1, new[] { ((byte)1, c) })).Length <= SmNet.Protocol.MaxExtrasMessageBytes);
        var re = new WarfrontResultsWire.Reassembler();
        byte[]? got = null;
        foreach (var c in chunks.Reverse()) got = re.Add(c) ?? got;   // any order
        Assert.NotNull(got);
        Assert.Equal(WarfrontExport.Text(rec), WarfrontExport.Text(WarfrontResultsWire.Decode(got!)));
        Assert.Null(new WarfrontResultsWire.Reassembler().Add(new byte[] { 1, 5, 2 }));   // index past count: ignored
        Assert.Null(new WarfrontResultsWire.Reassembler().Add(new byte[] { 1 }));
    }

    [Fact]
    public void A_score_weights_file_overrides_the_defaults_when_valid_and_falls_back_with_a_log_line_when_not()
    {
        var logs = new List<string>();
        Assert.Same(WarfrontScoreWeights.Default, WarfrontScoreWeights.LoadOverride(_dir, logs.Add));   // no file
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "score-weights.json");
        File.WriteAllText(path, "{\"Capture\": 5, \"TieBreaks\": [\"FewerDeaths\", \"Captures\"]}");
        var w = WarfrontScoreWeights.LoadOverride(_dir, logs.Add);
        Assert.Equal(5m, w.Capture);
        Assert.Equal(15m, w.Revive);   // omitted: default
        Assert.Equal(new[] { WarfrontTieBreak.FewerDeaths, WarfrontTieBreak.Captures }, w.TieBreaks);
        File.WriteAllText(path, "{\"Kill\": -3}");
        Assert.Same(WarfrontScoreWeights.Default, WarfrontScoreWeights.LoadOverride(_dir, logs.Add));
        File.WriteAllText(path, "not json");
        Assert.Same(WarfrontScoreWeights.Default, WarfrontScoreWeights.LoadOverride(_dir, logs.Add));
        Assert.Equal(3, logs.Count(l => l.Contains("score-weights.json")));
    }

    [Fact]
    public void The_archived_run_holds_no_key_but_the_live_save_keeps_them()
    {
        var c = Campaign();
        c.AddXp(KeyA, 2000);
        WarfrontSave.Write(_dir, c);
        Assert.Contains(KeyA.ToString(), File.ReadAllText(Path.Combine(_dir, "run.json")));
        var rec = GoldenRecord();
        var run = WarfrontResultsArchive.Archive(_dir, c, rec, DateTime.UtcNow);
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "records")))
            foreach (var key in new[] { KeyA, KeyB, KeyC })
                Assert.DoesNotContain(key.ToString(), File.ReadAllText(f));
        var text = File.ReadAllText(run);
        Assert.Contains("Alpha", text);
        Assert.Contains("Bravo", text);
        Assert.Contains("\"JoinOrder\":[0,1]", text);
    }
}
