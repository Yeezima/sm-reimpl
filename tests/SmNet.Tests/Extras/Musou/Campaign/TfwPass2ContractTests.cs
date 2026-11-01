using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode pass 2's contracts (M0): tags 11 to 14 and the two commands round-trip, fit and refuse bad input; a client reads them with no box; the new states and death kinds fit their fields; and,
/// since nothing uses them yet, they change no behaviour: a command does nothing in a war, a section does nothing to the rest of a client, and no server of any profile sends the tags.
/// </summary>
public class TfwPass2ContractTests
{
    static readonly MusouBox Box = new(new Vector3(-122f, -122f, -2f), new Vector3(122f, 122f, 13f));

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new() { Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    static ExtrasSection Section(uint tick, byte tag, byte[] payload) => new(tick, tag, payload);

    static readonly TfwFireEvent Shell = new(TfwFireKind.Shell, 900, 3, new Vector3(110.5f, -785.3f, 4f));
    static readonly TfwFireEvent Run = new(TfwFireKind.Run, 901, 1, new Vector3(-100f, -800f, 60f), new Vector3(500f, -790f, 60f));
    static readonly TfwRewardOffer Offer = new(7, new TfwRewardCard(TfwRewardKind.Weapon, 12), new TfwRewardCard(TfwRewardKind.Equipment, 300), new TfwRewardCard(TfwRewardKind.None, 0));

    // ---------------------------------------------------------------- the enums

    [Fact]
    public void Knocked_fits_the_four_bits_and_round_trips_in_the_enemy_record()
    {
        Assert.True(Enum.GetValues<MusouEnemyState>().Length <= 16);
        Assert.True((byte)Enum.GetValues<MusouEnemyState>().Max() <= 15);
        var e = new MusouEntity(5, Vector3.Zero, 0f, MusouEnemyType.Nob, MusouEnemyState.Knocked);
        var read = Assert.Single(MusouWire.ReadEntities(MusouWire.WriteEntities([e], Box), Box)!);
        Assert.Equal(MusouEnemyState.Knocked, read.State);
        Assert.Equal(MusouEnemyType.Nob, read.Type);
    }

    [Fact]
    public void A_knocked_guardsman_travels_in_his_state_byte()
    {
        var g = new TfwGuard(1, Vector3.Zero, 0f, (byte)GuardState.Knocked);
        Assert.Equal((byte)GuardState.Knocked, Assert.Single(TfwWire.ReadGuards(TfwWire.WriteGuards([g], Box), Box)!).State);
    }

    [Theory]
    [InlineData(MusouDeathKind.Barrage, 4)]
    [InlineData(MusouDeathKind.Strafe, 5)]
    public void The_new_deaths_travel_in_a_death_event(MusouDeathKind kind, byte value)
    {
        Assert.Equal(value, (byte)kind);
        var e = new MusouEvent(MusouEventKind.Death, 10, 1, 0, (byte)kind, Vector3.UnitX);
        Assert.Equal(kind, (MusouDeathKind)Assert.Single(MusouWire.ReadEvents(MusouWire.WriteEvents([e], Box), Box)!).Value);
    }

    [Fact]
    public void The_rules_version_is_six_and_in_the_musou_hash()
    {
        Assert.Equal(6u, TfwRules.Version);
        Assert.NotEqual(Config.Hash(), (Config with { Extras = ContentExtras.None }).Hash());
    }

    // ---------------------------------------------------------------- tag 11

    [Fact]
    public void Fire_round_trips_to_a_decimetre_and_a_full_section_fits()
    {
        var read = TfwWire.ReadFire(TfwWire.WriteFire([Shell, Run]))!;
        Assert.Equal(2, read.Length);
        Assert.Equal((Shell.Kind, Shell.Tick, Shell.Subject), (read[0].Kind, read[0].Tick, read[0].Subject));
        Assert.True(Vector3.Distance(Shell.At, read[0].At) < 0.1f);
        Assert.True(Vector3.Distance(Run.To, read[1].To) < 0.1f);
        foreach (var kind in Enum.GetValues<TfwFireKind>()) Assert.Equal(kind, Assert.Single(TfwWire.ReadFire(TfwWire.WriteFire([Shell with { Kind = kind }]))!).Kind);

        var many = Enumerable.Repeat(Shell, 200).ToList();
        var full = TfwWire.WriteFire(many);
        Assert.True(full.Length <= Protocol.MaxExtrasPayloadBytes, $"{full.Length}");
        Assert.Equal(TfwWire.MaxFire, TfwWire.ReadFire(full)!.Length);
    }

    [Fact]
    public void Bad_fire_is_refused_and_a_writer_makes_no_nan()
    {
        Assert.Null(TfwWire.ReadFire([]));
        Assert.Null(TfwWire.ReadFire([1, 0]));
        var bad = TfwWire.WriteFire([Shell]);
        bad[1] = 0;
        Assert.Null(TfwWire.ReadFire(bad));
        var nan = TfwWire.ReadFire(TfwWire.WriteFire([Shell with { At = new Vector3(float.NaN, float.PositiveInfinity, 1e9f) }]))![0].At;
        Assert.True(float.IsFinite(nan.X + nan.Y + nan.Z));
    }

    // ---------------------------------------------------------------- tag 12

    static readonly TfwScoreOwn Own = new(123_456, 4_000_000_000, 900, 77);
    static readonly TfwMapScore Map = new(TfwLane.C, 3, TfwMapResult.Extracted, 40, 9, 8800);

    [Fact]
    public void Each_part_of_the_score_round_trips_alone_and_together()
    {
        var onlyOwn = TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(Own)))!.Value;
        Assert.Equal(Own, onlyOwn.Own);
        Assert.Null(onlyOwn.Others);
        Assert.Null(onlyOwn.History);
        Assert.Equal(1 + TfwWire.ScoreOwnBytes, TfwWire.WriteScore(new TfwScoreUpdate(Own)).Length);

        var onlyOthers = TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, [new TfwScorePlayer(2, 9)])))!.Value;
        Assert.Null(onlyOthers.Own);
        Assert.Equal(new TfwScorePlayer(2, 9), Assert.Single(onlyOthers.Others!));

        var all = TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(Own, [new TfwScorePlayer(2, 9), new TfwScorePlayer(3, 10)], [Map, Map with { Lane = TfwLane.A, Result = TfwMapResult.Won }])))!.Value;
        Assert.Equal(Own, all.Own);
        Assert.Equal(2, all.Others!.Count);
        Assert.Equal(Map, all.History![0]);
        Assert.Equal(TfwMapResult.Won, all.History[1].Result);

        var none = TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, [], [])))!.Value;
        Assert.Empty(none.Others!);   // an empty list is a part, not its absence
        Assert.Empty(none.History!);
    }

    [Fact]
    public void A_full_score_fits_and_keeps_to_its_limits()
    {
        var players = Enumerable.Range(1, 30).Select(i => new TfwScorePlayer((ushort)i, (uint)i)).ToList();
        var maps = Enumerable.Repeat(Map, 30).ToList();
        var bytes = TfwWire.WriteScore(new TfwScoreUpdate(Own, players, maps));
        Assert.True(bytes.Length <= Protocol.MaxExtrasPayloadBytes, $"{bytes.Length}");
        var read = TfwWire.ReadScore(bytes)!.Value;
        Assert.Equal(TfwWire.MaxScorePlayers, read.Others!.Count);
        Assert.Equal(TfwWire.MaxScoreMaps, read.History!.Count);
    }

    [Fact]
    public void A_bad_score_is_refused()
    {
        Assert.Null(TfwWire.ReadScore([]));
        Assert.Null(TfwWire.ReadScore([0]));         // no part
        Assert.Null(TfwWire.ReadScore([8]));         // an unknown part
        Assert.Null(TfwWire.ReadScore([1, 0, 0]));   // short
        var good = TfwWire.WriteScore(new TfwScoreUpdate(Own));
        Assert.Null(TfwWire.ReadScore([.. good, 0])); // long
        Assert.Null(TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, [new TfwScorePlayer(0, 1)]))));
        Assert.Null(TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, [new TfwScorePlayer(TfwContracts.PuppetIdFirst, 1)]))));   // humans only
        Assert.Null(TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, [new TfwScorePlayer(2, 1), new TfwScorePlayer(2, 2)]))));
        Assert.Null(TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, null, [Map with { Result = 0 }]))));
        Assert.Null(TfwWire.ReadScore(TfwWire.WriteScore(new TfwScoreUpdate(null, null, [Map with { Lane = (TfwLane)3 }]))));
    }

    // ---------------------------------------------------------------- tags 13 and 14

    [Fact]
    public void Bars_round_trip_and_bad_ones_are_refused()
    {
        TfwBar[] bars = [new(1, 255), new((ushort)MusouSim.Capacity, 0)];
        Assert.Equal(bars, TfwWire.ReadBars(TfwWire.WriteBars(bars)));
        var many = Enumerable.Range(1, 300).Select(i => new TfwBar((ushort)i, 9)).ToList();
        var full = TfwWire.WriteBars(many);
        Assert.True(full.Length <= Protocol.MaxExtrasPayloadBytes);
        Assert.Equal(TfwWire.MaxBars, TfwWire.ReadBars(full)!.Length);
        Assert.Null(TfwWire.ReadBars([]));
        Assert.Null(TfwWire.ReadBars([1, 0]));
        Assert.Null(TfwWire.ReadBars(TfwWire.WriteBars([new TfwBar(0, 1)])));
        Assert.Null(TfwWire.ReadBars(TfwWire.WriteBars([new TfwBar((ushort)(MusouSim.Capacity + 1), 1)])));
    }

    [Fact]
    public void An_offer_round_trips_and_a_bad_one_is_refused()
    {
        var bytes = TfwWire.WriteReward(Offer);
        Assert.Equal(11, bytes.Length);
        Assert.Equal(Offer, TfwWire.ReadReward(bytes));
        Assert.Null(TfwWire.ReadReward(bytes.AsSpan(0, 10)));
        Assert.Null(TfwWire.ReadReward(TfwWire.WriteReward(Offer with { Sequence = 0 })));
        var badKind = (byte[])bytes.Clone();
        badKind[5] = 9;
        Assert.Null(TfwWire.ReadReward(badKind));
    }

    // ---------------------------------------------------------------- the commands

    [Fact]
    public void The_two_commands_round_trip_and_a_card_must_exist()
    {
        var change = new TfwCommandRecord(5, TfwCommand.ChangeClass, 0);
        var choose = new TfwCommandRecord(6, TfwCommand.ChooseReward, 2);
        Assert.Equal(change, TfwWire.ReadCommand(TfwWire.WriteCommand(change)));
        Assert.Equal(choose, TfwWire.ReadCommand(TfwWire.WriteCommand(choose)));
        Assert.False(TfwWire.HasPoint(TfwCommand.ChangeClass));
        Assert.Null(TfwWire.ReadCommand(TfwWire.WriteCommand(choose with { Argument = TfwWire.RewardCards })));
    }

    [Fact]
    public void The_two_commands_do_nothing_in_a_war_yet()
    {
        var w = new World(Config, null, TfwMap.Spawns) { Rules = Rules };
        Assert.True(w.StartTfw(null));
        w.Spawn(1, 0);
        for (int k = 0; k < 60; k++) w.Step(_ => default);
        var run = w.Tfw!;
        var before = (w.Players[1], run.Phase, run.Transport, run.Supplies);
        Assert.True(run.Enqueue(1, new TfwCommandRecord(1, TfwCommand.ChangeClass, 0)));
        Assert.True(run.Enqueue(1, new TfwCommandRecord(2, TfwCommand.ChooseReward, 1)));
        w.Step(_ => default);
        Assert.Equal(0, run.QueuedCommands);          // taken, not left to pile up
        Assert.Equal((before.Item2, before.Transport, before.Supplies), (run.Phase, run.Transport, run.Supplies));
        Assert.Equal(before.Item1.Position, w.Players[1].Position);
        Assert.Equal(before.Item1.Class, w.Players[1].Class);
    }

    // ---------------------------------------------------------------- the client

    [Fact]
    public void A_client_reads_the_new_tags_without_a_box_and_keeps_each_part()
    {
        var s = new TfwClientState();
        Assert.True(s.Apply(Section(100, TfwWire.FireTag, TfwWire.WriteFire([Shell, Run])), null, 1.0));
        Assert.True(s.Apply(Section(101, TfwWire.FireTag, TfwWire.WriteFire([Shell])), null, 1.0));   // a repeat
        Assert.Equal(2, s.NewFire.Count);
        Assert.Equal(1, s.EventCopies);
        s.TakeNew();
        Assert.Empty(s.NewFire);

        Assert.True(s.Apply(Section(100, TfwWire.ScoreTag, TfwWire.WriteScore(new TfwScoreUpdate(Own, [new TfwScorePlayer(2, 9)], [Map]))), null, 1.0));
        Assert.True(s.Apply(Section(110, TfwWire.ScoreTag, TfwWire.WriteScore(new TfwScoreUpdate(Own with { Kos = 901 }))), null, 1.0));
        Assert.Equal((ushort)901, s.ScoreOwn!.Value.Kos);
        Assert.Single(s.ScoreOthers);                 // a part left out stays
        Assert.Single(s.ScoreHistory);
        Assert.True(s.Apply(Section(105, TfwWire.ScoreTag, TfwWire.WriteScore(new TfwScoreUpdate(Own with { Kos = 1 }))), null, 1.0));
        Assert.Equal((ushort)901, s.ScoreOwn!.Value.Kos);   // late
        Assert.Equal(1, s.StaleSections);

        Assert.True(s.Apply(Section(100, TfwWire.BarsTag, TfwWire.WriteBars([new TfwBar(1, 200)])), null, 1.0));
        Assert.True(s.Apply(Section(99, TfwWire.BarsTag, TfwWire.WriteBars([])), null, 1.0));
        Assert.Single(s.Bars);

        Assert.True(s.Apply(Section(100, TfwWire.RewardTag, TfwWire.WriteReward(Offer)), null, 1.0));
        Assert.True(s.Apply(Section(101, TfwWire.RewardTag, TfwWire.WriteReward(Offer with { Sequence = 6, First = default })), null, 1.0));
        Assert.Equal(Offer, s.Reward);                // the sequence decides, not the tick
        Assert.Equal(0, s.SectionsMalformed);

        s.Clear();
        Assert.Null(s.ScoreOwn);
        Assert.Null(s.Reward);
        Assert.Empty(s.Bars);
        Assert.Empty(s.ScoreHistory);
        Assert.True(s.Apply(Section(100, TfwWire.FireTag, TfwWire.WriteFire([Shell])), null, 2.0));
        Assert.Single(s.NewFire);                     // forgotten
    }

    [Fact]
    public void A_bad_new_section_is_counted_and_a_fire_id_is_forgotten_in_time()
    {
        var s = new TfwClientState();
        Assert.False(s.Apply(Section(1, TfwWire.FireTag, [9]), Box, 0.0));
        Assert.False(s.Apply(Section(1, TfwWire.ScoreTag, [0]), Box, 0.0));
        Assert.False(s.Apply(Section(1, TfwWire.BarsTag, [9]), Box, 0.0));
        Assert.False(s.Apply(Section(1, TfwWire.RewardTag, [9]), null, 0.0));
        Assert.Equal(4, s.SectionsMalformed);
        Assert.Equal(0, s.SectionsRead);

        s.Apply(Section(2, TfwWire.FireTag, TfwWire.WriteFire([Shell])), null, 1.0);
        s.TakeNew();
        s.Sample(0.0, 1.0 + TfwClientState.MemorySeconds + 0.1);
        s.Apply(Section(3, TfwWire.FireTag, TfwWire.WriteFire([Shell])), null, 7.0);
        Assert.Single(s.NewFire);
    }

    // ---------------------------------------------------------------- isolation

    [Theory]
    [InlineData("faithful")]
    [InlineData("extended")]
    [InlineData("five waves")]
    public void No_server_sends_the_new_tags_or_takes_the_new_commands(string profile)
    {
        var extras = profile switch { "faithful" => ContentExtras.None, "extended" => ContentExtras.All, _ => ContentExtras.Musou };
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config with { Extras = extras }, new ServerOptions { Match = Rules });
        var heard = new List<byte>();
        h.Run(TimeSpan.FromSeconds(2), after: () => heard.AddRange(h.Clients[0].TakeExtras().Select(s => s.Tag)));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.DoesNotContain(heard, t => t is >= TfwWire.FireTag and <= TfwWire.RewardTag);
        h.Clients[0].SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(1, TfwCommand.ChangeClass, 0)));
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal((ushort)0, h.Server.TfwCommandEcho(h.Clients[0].PlayerId));
    }

    [Fact]
    public void A_war_sends_none_of_the_new_tags_yet()
    {
        string? name = null;
        var map = GameServer.GeneratedMap(ContentExtras.Musou, ref name, out _)!;
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        var heard = new List<byte>();
        h.Run(TimeSpan.FromSeconds(3), after: () => heard.AddRange(h.Clients[0].TakeExtras().Select(s => s.Tag)));
        Assert.Contains(TfwWire.WarTag, heard);
        Assert.DoesNotContain(heard, t => t is >= TfwWire.FireTag and <= TfwWire.RewardTag);
    }
}
