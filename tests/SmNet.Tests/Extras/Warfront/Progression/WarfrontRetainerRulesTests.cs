using System.Text.Json.Nodes;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Warfront.Squad.Classes;
using SmSim;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Progression;

/// <summary>
/// The retainer rules of owner 2026-11-23, card 2, and the approved retainer brief (Phase B T0): two retainers per player, none in the field for a Dreadnought, no Retainer card, a 40 s pick, class-matched bot
/// loadouts, the Librarian's per-retainer class roll, and old runs and progress files converting. Synthetic data only; never the owner's save.
/// </summary>
public class WarfrontRetainerRulesTests
{
    const ulong Key = 0xABCDEF01UL;

    [Fact]
    public void No_offer_holds_the_retainer_card_and_other_cards_fill_the_slots()
    {
        Assert.False(WarfrontOffers.RetainerEligible(1, false));
        for (int pick = 0; pick < 20; pick++)
            foreach (bool eligible in new[] { false, true })
            {
                var offer = WarfrontOffers.Roll(9UL, Key, pick, eligible);
                Assert.Equal(WarfrontProgressionRules.StatCards, offer);
                Assert.DoesNotContain(WarfrontCard.Retainer, offer);
            }
    }

    [Fact]
    public void The_picker_opens_five_cards_and_times_out_at_forty_seconds()
    {
        Assert.Equal(40.0, WarfrontProgressionRules.PickTimeoutSeconds);
        var c = new WarfrontCampaign(3UL);
        c.Join(Key, out _);
        c.AddXp(Key, WarfrontCampaign.XpForLevel(3));
        var picker = new WarfrontPicker(c);
        var offer = picker.TryOpen(Key, 0.0, aboard: true, lull: false, rosterSize: 1, dreadnought: false);
        Assert.NotNull(offer);
        Assert.Equal(5, offer!.Cards.Length);
        Assert.Empty(picker.Tick(39.9));
        Assert.True(picker.Protected(Key));
        var done = Assert.Single(picker.Tick(40.0));
        Assert.True(done.Timeout);
        Assert.Equal(WarfrontCard.AttackSpeed, done.Card);
    }

    [Fact]
    public void The_offer_wire_carries_the_whole_forty_seconds()
    {
        var bytes = WarfrontProgressionWire.Encode(new WarfrontOfferWire(true, 4, [.. WarfrontProgressionRules.StatCards, WarfrontCard.None], 400, true));
        Assert.Equal(WarfrontProgressionWire.OfferLength, bytes.Length);
        Assert.True(WarfrontProgressionWire.TryDecode(bytes, out WarfrontOfferWire back));
        Assert.Equal(400, back.TenthsLeft);
        Assert.Equal(40f, back.SecondsLeft);
        Assert.True(back.Aboard);
        Assert.Equal(WarfrontCard.None, back.Cards[WarfrontProgressionRules.RetainerSlot]);
    }

    /// <summary>A synthetic version 1 progress.json: level 6, five picks of which two were +1 Retainer, and the two extra retainers they earned.</summary>
    const string ProgressV1 = """
        {"version":1,"key":"00000000abcdef01","xp":12500,"picksTaken":5,"cards":[3,6,2,6,1],"extraRetainers":[{"className":"sm - tactical","name":"Cassian"},{"className":"sm - tactical","name":"Titus"}],"resetAt":null}
        """;

    [Fact]
    public void A_version_1_progress_file_loads_with_its_retainer_cards_and_extras_dropped_and_nothing_else_changed()
    {
        Assert.Equal(WarfrontProgressParse.Ok, WarfrontProgressFile.TryParse(ProgressV1, out var f));
        var notes = new List<string>();
        var s = WarfrontProgressFile.Sanitize(f, notes);
        Assert.Equal(2, s.Version);
        Assert.Equal(12500, s.Xp);
        Assert.Equal(f!.Level, s.Level);
        Assert.Equal(3, s.PicksTaken);                       // owner "Give picks back": each dropped Retainer card's pick is pending again
        Assert.Equal([3, 2, 1], s.Cards);                    // the stat cards in their order
        Assert.Empty(s.ExtraRetainers);
        Assert.Contains(notes, n => n.Contains("Retainer card"));
        var again = WarfrontProgressFile.Sanitize(s, notes = new List<string>());
        Assert.Empty(notes);                                 // idempotent
        Assert.Equal(s.ToJson(), again.ToJson());
        // the run's copy of such a player has the refunded picks pending again (owner "Give picks back")
        var c = new WarfrontCampaign(1UL);
        c.Join(Key, out _);
        Assert.True(c.ApplyProgress(Key, s));
        Assert.Equal(2, c.Player(Key)!.PendingPicks);
        Assert.Equal(0, WarfrontStatBonuses.For(c.Player(Key)!.Cards).Retainers);
    }

    [Fact]
    public void A_newer_progress_file_is_still_left_alone() =>
        Assert.Equal(WarfrontProgressParse.Newer, WarfrontProgressFile.TryParse("""{"version":3,"xp":0}""", out _));

    [Fact]
    public void A_version_5_run_loads_with_its_retainer_cards_and_records_dropped_and_each_card_gives_its_pick_back()
    {
        var c = new WarfrontCampaign(77UL);
        c.Join(Key, "Tester", out _);
        c.AddXp(Key, WarfrontCampaign.XpForLevel(5));
        Assert.True(c.TryTakePick(Key, (int)WarfrontCard.Damage));
        Assert.True(c.TryTakePick(Key, (int)WarfrontCard.MaxHp));
        // a version 5 save as the shipped build wrote it: a Retainer card among the picks and its record in the squad list
        var root = JsonNode.Parse(c.ToJson())!.AsObject();
        root["Version"] = 5;
        var player = root["Players"]![0]!.AsObject();
        player["PicksTaken"] = 3;
        player["Cards"] = new JsonArray(3, 6, 2);
        player["Squad"] = new JsonArray(new JsonObject { ["Id"] = "p1", ["ClassName"] = "sm - tactical", ["Name"] = "Titus" });
        var loaded = WarfrontSave.FromJson(root.ToJsonString());
        var p = loaded.Player(Key)!;
        Assert.Equal([3, 2], p.Cards);
        Assert.Equal(2, p.PicksTaken);   // owner "Give picks back": the dropped Retainer card's pick is pending again
        Assert.Equal(c.Player(Key)!.Level - 1 - 2, p.PendingPicks);
        Assert.Empty(p.Squad);
        Assert.Equal(c.Player(Key)!.Xp, p.Xp);
        Assert.Equal(c.Player(Key)!.Level, p.Level);
        Assert.Equal("Tester", p.Name);
        Assert.Contains("\"Version\":" + WarfrontCampaign.SaveVersion, loaded.ToJson());
        Assert.Equal(loaded.StateHash(), WarfrontSave.FromJson(loaded.ToJson()).StateHash());   // idempotent: a second load converts nothing
    }

    [Fact]
    public void The_conversion_runs_at_any_save_version_and_a_null_list_neither_crashes_nor_loses_the_player()
    {
        // T0 review (2026-11-25): another branch's later SaveVersion must still convert; a damaged "Cards": null / "Squad": null loads empty
        var c = new WarfrontCampaign(78UL);
        c.Join(Key, "Tester", out _);
        c.AddXp(Key, WarfrontCampaign.XpForLevel(4));
        var root = JsonNode.Parse(c.ToJson())!.AsObject();
        var player = root["Players"]![0]!.AsObject();
        player["PicksTaken"] = 2;
        player["Cards"] = new JsonArray(6, 3);
        var p = WarfrontSave.FromJson(root.ToJsonString()).Player(Key)!;
        Assert.Equal([3], p.Cards);
        Assert.Equal(1, p.PicksTaken);
        player["Cards"] = null;
        player["Squad"] = null;
        player["PicksTaken"] = 0;
        var q = WarfrontSave.FromJson(root.ToJsonString()).Player(Key)!;
        Assert.Empty(q.Cards);
        Assert.Empty(q.Squad);
    }

    [Fact]
    public void A_run_of_version_6_is_newer_for_the_shipped_build_and_round_trips_here()
    {
        Assert.True(WarfrontCampaign.SaveVersion >= WarfrontCampaign.RetainerCardsRetiredVersion);
        Assert.Equal(6, WarfrontCampaign.RetainerCardsRetiredVersion);
        var c = new WarfrontCampaign(5UL);
        c.Join(Key, out _);
        Assert.Equal(c.StateHash(), WarfrontSave.FromJson(c.ToJson()).StateHash());
    }

    [Fact]
    public void A_saved_roster_of_any_size_loads_with_two_keeping_the_first_names_and_records()
    {
        var big = new SquadMarineSave[5];
        for (int i = 0; i < big.Length; i++) big[i] = new SquadMarineSave("M" + i, new SquadLoadout { ClassId = "sm - tactical" }, new ServiceRecord { Kills = i + 1 });
        var save = new SquadRosterSave(40, [new SquadOwnerSave(1UL, false, big), new SquadOwnerSave(2UL, true, [])]);
        var r = new SquadRosters();
        r.Import(save);
        Assert.Equal(5, r.Count(1UL));                       // a plain import keeps the saved counts (the class book conforms a run's roster at the owner's next look aboard)
        r.Import(save, SquadRules.RetainersPerPlayer);
        Assert.Equal(2, r.Count(1UL));
        Assert.Equal("M0", r.Marine(1UL, 0)!.Name);
        Assert.Equal("M1", r.Marine(1UL, 1)!.Name);
        Assert.Equal(2, r.Marine(1UL, 1)!.Record.Kills);
        Assert.Equal(2, r.Count(2UL));                       // a Dreadnought's empty held roster gets two, still held
        Assert.True(r.IsHeld(2UL));
        Assert.NotEqual(r.Marine(2UL, 0)!.Name, r.Marine(2UL, 1)!.Name);
    }

    [Fact]
    public void Every_player_has_two_retainers_and_a_dreadnought_none_in_the_field()
    {
        var c = SquadClassesTests.Make();
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 11UL);
        var standard = book.OnOwnerLoadout(1UL, SquadOwnerLoadout.Standard(new LoadoutChoice(0)));
        Assert.True(standard.Applied);
        Assert.Equal(SquadRules.RetainersPerPlayer, r.Count(1UL));
        Assert.False(book.Recruit(1UL));
        Assert.Equal(2, r.Count(1UL));

        var dread = book.OnOwnerLoadout(2UL, SquadOwnerLoadout.Dreadnought());
        Assert.True(dread.Held);
        Assert.True(r.IsHeld(2UL));
        Assert.Equal(2, r.Count(2UL));
        var back = book.OnOwnerLoadout(2UL, SquadOwnerLoadout.Standard(new LoadoutChoice(2)));
        Assert.True(back.Released);
        Assert.False(back.FreeRetainer);                     // the free retainer is retired: still two
        Assert.Equal(2, r.Count(2UL));
        for (int s = 0; s < 2; s++) Assert.Equal("sm - devastator", r.Marine(2UL, s)!.Loadout.ClassId);   // never a Dreadnought
    }

    [Fact]
    public void A_standard_players_retainers_roll_valid_bot_loadouts_of_his_class_and_keep_his_look()
    {
        var c = SquadClassesTests.Make();
        var look = AppearanceCode.Default.WithPalette(ArmourSlot.Torso, 3, 7, 9).WithPiece(ArmourSlot.Torso, 2);
        for (ulong key = 1; key <= 12; key++)
            foreach (int cls in new[] { 0, 1, 2, 3, 4, 5 })
            {
                var r = new SquadRosters();
                var book = new SquadClassBook(c, r, 21UL);
                book.OnOwnerLoadout(key, SquadOwnerLoadout.Standard(new LoadoutChoice(cls), look));
                for (int s = 0; s < 2; s++)
                {
                    var m = r.Marine(key, s)!.Loadout;
                    Assert.Equal(c.Classes[cls].Name, m.ClassId);
                    // one of the owner's Classic bot presets of this class that the config allows (BotLoadouts.Resolve runs BotLoadouts.Check), or the class default when none is legal
                    var expected = BotLoadouts.For(c.Classes[cls].Role).Select(p => BotLoadouts.Resolve(c, cls, p)).Where(p => p.Legal)
                        .Select(p => SquadLoadoutBuilder.FromChoice(c, new LoadoutChoice(cls, p.Primary, p.Pistol, p.Melee, p.Equipment, p.Perk1, p.Perk2, p.Second), look)!).ToList();
                    if (expected.Count == 0) expected.Add(SquadLoadoutBuilder.FromChoice(c, new LoadoutChoice(cls), look)!);
                    Assert.Contains(expected, e => e.Primary == m.Primary && e.Secondary == m.Secondary && e.Melee == m.Melee && e.Perks.SequenceEqual(m.Perks));
                    Assert.Contains("class:" + c.Classes[cls].Role, m.Armour);
                    Assert.Contains("torso:2", m.Armour);   // his own armour pieces
                    Assert.Equal(SquadLoadoutBuilder.FromChoice(c, new LoadoutChoice(cls), look)!.Colours, m.Colours);
                }
                // stable for the run: the same confirmation rolls nothing new
                Assert.Equal(0, book.OnOwnerLoadout(key, SquadOwnerLoadout.Standard(new LoadoutChoice(cls), look)).Changed);
            }
    }

    [Fact]
    public void A_librarians_retainers_each_roll_tactical_assault_or_devastator_on_their_own()
    {
        var c = SquadClassesTests.Make();
        var seen = new HashSet<string>();
        int mixed = 0;
        for (ulong key = 1; key <= 40; key++)
        {
            var r = new SquadRosters();
            var book = new SquadClassBook(c, r, 31UL);
            book.OnOwnerLoadout(key, SquadOwnerLoadout.ForLibrarian(SquadClass.Tactical));
            var a = r.Marine(key, 0)!.Loadout.ClassId;
            var b = r.Marine(key, 1)!.Loadout.ClassId;
            Assert.Equal(SquadClassBook.LibrarianClassOf(31UL, key, 0), SquadClassRules.OfRole(c.Classes.First(x => x.Name == a).Role));
            Assert.Equal(SquadClassBook.LibrarianClassOf(31UL, key, 1), SquadClassRules.OfRole(c.Classes.First(x => x.Name == b).Role));
            seen.Add(a); seen.Add(b);
            if (a != b) mixed++;
            // the stored squad-class option no longer matters
            Assert.Equal(0, book.OnOwnerLoadout(key, SquadOwnerLoadout.ForLibrarian(SquadClass.Devastator)).Changed);
        }
        Assert.Equal(["sm - assault marine", "sm - devastator", "sm - tactical"], seen.Order().ToArray());
        Assert.InRange(mixed, 1, 39);
        // a Chaos Librarian's retainers come from the Chaos classes
        var rc = new SquadRosters();
        new SquadClassBook(c, rc, 31UL).OnOwnerLoadout(9UL, SquadOwnerLoadout.ForLibrarian(SquadClass.Tactical, side: Team.Chaos));
        Assert.All(new[] { 0, 1 }, s => Assert.StartsWith("csm - ", rc.Marine(9UL, s)!.Loadout.ClassId));
    }
}
