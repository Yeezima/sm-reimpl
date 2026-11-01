using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode on the wire (TF6): every section and command round-trips and fits its limit; a malformed one is refused; the commands are idempotent by sequence, resent until tag 7 echoes them
/// and held at the core's rate; a client stays within 60 KB/s at 160 Orks and 120 Guardsmen; faithful, extended and 5-wave servers never send tags 7 to 10.
/// </summary>
public class TfwWireTests(ITestOutputHelper output)
{
    static readonly MusouBox Box = new(new Vector3(-122f, -122f, -2f), new Vector3(122f, 122f, 13f));
    static readonly Vector3 Near = TfwWireStep();

    static Vector3 TfwWireStep() => MusouWire.PositionStep(Box);

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    static SimConfig ConfigFor(ContentExtras extras) => new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = extras,
    };

    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    static Harness WarServer(int clients = 1, LinkConditions? link = null)
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(link ?? LinkConditions.Perfect, 5, clients, ConfigFor(ContentExtras.Musou), new ServerOptions { Match = Rules }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        h.Server.World.Tfw!.Idle = true;   // these tests are the queue's and the feed's own (TF4's run would take both)
        return h;
    }

    /// <summary>A war with a director (the box), the war's clock and the given men around the origin.</summary>
    static void Feed(GameServer server, int guards, int objectives = 5, Vector3 around = default)
    {
        server.World.Musou!.Director = new MusouDirectorView(0, 0, MusouPhase.Campaign, 0, 0, 0, 0, 0f, 0f, 0, Box);
        var feed = server.TfwFeed;
        feed.War = new WarSim();
        feed.Transport = 5;
        feed.Supplies = 4;
        feed.Objectives.Clear();
        for (int i = 0; i < objectives; i++) feed.Objectives.Add(new TfwObjective((byte)i, TfwPositionState.Held, 0.5f, new Vector3(i * 10f, 0f, 0f)));
        feed.GuardCount = guards;
        for (int i = 0; i < guards; i++)
        {
            float ring = 2f + (i % 12), angle = i * 0.7f;
            feed.Guards[i] = new TfwGuard((ushort)(i + 1), around + new Vector3(ring * MathF.Cos(angle), ring * MathF.Sin(angle), 0f), angle, 2);
        }
    }

    static IEnumerable<byte> Tags(GameClient client) => client.TakeExtras().Select(s => s.Tag);

    // ---------------------------------------------------------------- the sections

    static TfwWarView SampleWar() => new(TfwRunPhase.Battle, TfwLane.C, 1234.5f, 0.25f, 5, 4, 0xABCD,
        new TfwLaneState(-1, -0.75f, 123.25f, 180.5f, 0, 12.5f, WarRules.LordWounded),
        new TfwLaneState(0, 0.125f, 100f, 110f, 0b000, 0f, WarRules.LordPresent),
        new TfwLaneState(3, 1f, 200f, 250f, 0b111, 60f, WarRules.LordPresent));

    [Fact]
    public void The_war_round_trips_exactly_and_a_real_view_too()
    {
        var war = SampleWar();
        var bytes = TfwWire.WriteWar(war);
        Assert.Equal(TfwWire.WarBytes, bytes.Length);
        Assert.Equal(war, TfwWire.ReadWar(bytes));

        var sim = new WarSim();
        for (int i = 0; i < 700; i++) sim.Step();
        var view = sim.View(3, 2, 7);
        Assert.Equal(view, TfwWire.ReadWar(TfwWire.WriteWar(view)));
    }

    [Theory]
    [InlineData(0, 99)]        // an unknown phase
    [InlineData(1, 3)]         // an unknown lane
    [InlineData(14, 4)]        // front 4
    [InlineData(14, 0xFE)]     // front -2
    [InlineData(27, 8)]        // a fourth position
    [InlineData(32, 2)]        // an unknown lord state
    public void A_bad_war_is_refused(int at, byte value)
    {
        var bytes = TfwWire.WriteWar(SampleWar());
        bytes[at] = value;
        Assert.Null(TfwWire.ReadWar(bytes));
    }

    [Fact]
    public void A_war_with_a_bad_number_or_length_is_refused_and_a_writer_never_makes_one()
    {
        var bytes = TfwWire.WriteWar(SampleWar());
        Assert.Null(TfwWire.ReadWar(bytes.AsSpan(0, bytes.Length - 1)));
        foreach (int at in new[] { 2, 6, 15, 19, 23, 28 })
        {
            var bad = (byte[])bytes.Clone();
            BitConverter.TryWriteBytes(bad.AsSpan(at), float.NaN);
            Assert.Null(TfwWire.ReadWar(bad));
        }
        var nan = SampleWar() with { Clock = float.NaN, A = SampleWar().A with { Progress = float.PositiveInfinity } };
        Assert.Equal(0f, TfwWire.ReadWar(TfwWire.WriteWar(nan))!.Value.Clock);
    }

    [Fact]
    public void The_objectives_round_trip_within_a_step_and_180_bytes()
    {
        TfwObjective[] list =
        [
            new(0, TfwPositionState.Secured, 1f, new Vector3(-40f, -45f, 0f)),
            new(1, TfwPositionState.Securing, 0.4f, new Vector3(35f, 10f, 0f)),
            new(4, TfwPositionState.Locked, 0f, new Vector3(0f, 105f, 0f)),
        ];
        var read = TfwWire.ReadObjectives(TfwWire.WriteObjectives(list, Box), Box)!;
        Assert.Equal(list.Length, read.Length);
        for (int i = 0; i < list.Length; i++)
        {
            Assert.Equal((list[i].Index, list[i].State), (read[i].Index, read[i].State));
            Assert.Equal(list[i].Progress, read[i].Progress, 1e-4f);
            Assert.True(Vector3.Abs(list[i].Position - read[i].Position).X <= Near.X && Vector3.Abs(list[i].Position - read[i].Position).Y <= Near.Y);
        }
        var many = Enumerable.Range(0, 40).Select(i => new TfwObjective((byte)i, TfwPositionState.Held, 0f, default)).ToList();
        var full = TfwWire.WriteObjectives(many, Box);
        Assert.True(full.Length <= TfwWire.MaxObjectivesBytes, $"{full.Length}");
        Assert.Equal(TfwWire.MaxObjectives, TfwWire.ReadObjectives(full, Box)!.Length);

        Assert.Empty(TfwWire.ReadObjectives(TfwWire.WriteObjectives([], Box), Box)!);       // the end of a battle
        Assert.Null(TfwWire.ReadObjectives([], Box));
        Assert.Null(TfwWire.ReadObjectives([2, 0, 0], Box));                                 // short
        var twice = TfwWire.WriteObjectives([list[0], list[0]], Box);
        Assert.Null(TfwWire.ReadObjectives(twice, Box));
        var badState = TfwWire.WriteObjectives([list[0]], Box);
        badState[2] = 9;
        Assert.Null(TfwWire.ReadObjectives(badState, Box));
    }

    [Fact]
    public void The_guardsmen_round_trip_within_a_step_and_a_full_section_fits()
    {
        var list = Enumerable.Range(1, 200).Select(i => new TfwGuard((ushort)i, new Vector3(i - 100f, 50f - i, 1f), i * 0.03f, (byte)(i % 4))).ToList();
        var bytes = TfwWire.WriteGuards(list, Box);
        Assert.True(bytes.Length <= Protocol.MaxExtrasPayloadBytes, $"{bytes.Length}");
        var read = TfwWire.ReadGuards(bytes, Box)!;
        Assert.Equal(TfwWire.MaxGuards, read.Length);
        for (int i = 0; i < read.Length; i++)
        {
            Assert.Equal((list[i].Id, list[i].State), (read[i].Id, read[i].State));
            var d = Vector3.Abs(list[i].Position - read[i].Position);
            Assert.True(d.X <= Near.X && d.Y <= Near.Y && d.Z <= Near.Z, $"{d}");
            Assert.Equal(list[i].Yaw, read[i].Yaw, 2f * MathF.PI / 256f);
        }
        var id0 = TfwWire.WriteGuards([list[0]], Box); id0[1] = 0;
        Assert.Null(TfwWire.ReadGuards(id0, Box));
        Assert.Null(TfwWire.ReadGuards(TfwWire.WriteGuards([new TfwGuard(TfwWire.GuardCapacity + 1, default, 0f, 1)], Box), Box));
        Assert.Null(TfwWire.ReadGuards(TfwWire.WriteGuards([list[0], list[0]], Box), Box));
        Assert.Null(TfwWire.ReadGuards(bytes.AsSpan(0, 11), Box));
        Assert.Null(TfwWire.ReadGuards([], Box));
    }

    [Fact]
    public void The_guard_events_round_trip_within_a_step_and_a_full_section_fits()
    {
        var list = Enumerable.Range(1, 120).Select(i => new TfwGuardEvent((TfwGuardEventKind)(i % 3 + 1), 1000u + (uint)i, (ushort)i, (ushort)(i * 2), new Vector3(i, -i, 2f))).ToList();
        Assert.Equal(15, TfwWire.GuardEventBytes);
        var bytes = TfwWire.WriteGuardEvents(list, Box);
        Assert.True(bytes.Length <= Protocol.MaxExtrasPayloadBytes, $"{bytes.Length}");
        var read = TfwWire.ReadGuardEvents(bytes, Box)!;
        Assert.Equal(TfwWire.MaxGuardEvents, read.Length);
        for (int i = 0; i < read.Length; i++)
        {
            Assert.Equal(list[i] with { Vector = default }, read[i] with { Vector = default });
            Assert.True(Vector3.Abs(list[i].Vector - read[i].Vector).X <= Near.X);
        }
        var bad = TfwWire.WriteGuardEvents([list[0]], Box);
        bad[1] = 0;
        Assert.Null(TfwWire.ReadGuardEvents(bad, Box));
        Assert.Null(TfwWire.ReadGuardEvents(bytes.AsSpan(0, 20), Box));
        Assert.Null(TfwWire.ReadGuardEvents([], Box));
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public void Every_command_round_trips_and_the_orders_with_a_place_carry_it()
    {
        foreach (var command in Enum.GetValues<TfwCommand>())
        {
            var point = TfwWire.HasPoint(command) ? new Vector3(-40.3f, 60.7f, 1.2f) : default;
            byte argument = command switch { TfwCommand.Deploy => (byte)TfwLane.C, TfwCommand.SelectSlot => 4, _ => 0 };
            var c = new TfwCommandRecord(0xFFFE, command, argument, point, command == TfwCommand.ChooseReward ? (ushort)9 : (ushort)0);
            var bytes = TfwWire.WriteCommand(c);
            Assert.True(bytes.Length <= Protocol.MaxExtrasCommandPayload);
            Assert.Equal(TfwWire.HasPoint(command) ? TfwWire.PointCommandBytes : command == TfwCommand.ChooseReward ? TfwWire.OfferCommandBytes : TfwWire.CommandBytes, bytes.Length);
            var read = TfwWire.ReadCommand(bytes)!.Value;
            Assert.Equal(c with { Point = default }, read with { Point = default });
            Assert.True(Vector3.Distance(point, read.Point) < 0.1f, $"{read.Point}");
        }
        Assert.True(TfwWire.HasPoint(TfwCommand.Hold));
        Assert.True(TfwWire.HasPoint(TfwCommand.Attack));
    }

    [Fact]
    public void A_bad_command_is_refused_and_a_point_cannot_be_a_nan()
    {
        Assert.Null(TfwWire.ReadCommand([1, 0, (byte)TfwCommand.Hold, 0]));                 // a Hold without its place
        Assert.Null(TfwWire.ReadCommand([1, 0, (byte)TfwCommand.Follow, 0, 0, 0, 0, 0, 0, 0]));   // a place on an order without one
        Assert.Null(TfwWire.ReadCommand([1, 0, (byte)TfwCommand.Deploy, 3]));               // an unknown lane
        var nan = TfwWire.ReadCommand(TfwWire.WriteCommand(new TfwCommandRecord(1, TfwCommand.Attack, 0, new Vector3(float.NaN, float.PositiveInfinity, 1e9f))))!.Value;
        Assert.True(float.IsFinite(nan.Point.X + nan.Point.Y + nan.Point.Z));
        Assert.Equal(0f, nan.Point.X);
        Assert.Equal(0f, nan.Point.Y);
        Assert.InRange(nan.Point.Z, 3276f, 3277f);                                          // clamped
    }

    [Fact]
    public void Sequences_compare_across_the_wrap()
    {
        Assert.True(TfwWire.IsNewer(1, 65535));
        Assert.False(TfwWire.IsNewer(65535, 1));
        Assert.False(TfwWire.IsNewer(9, 9));
        Assert.True(TfwWire.IsNewer(10, 9));
    }

    [Fact]
    public void A_sender_resends_until_echoed_one_at_a_time_and_never_above_the_rate()
    {
        var sender = new TfwCommandSender();
        Assert.Null(sender.Due(0.0));
        ushort first = sender.Issue(TfwCommand.Follow)!.Value;
        ushort second = sender.Issue(TfwCommand.Hold, point: new Vector3(1f, 2f, 0f))!.Value;
        Assert.Equal((ushort)1, first);
        Assert.Equal((ushort)2, second);

        var sent = new List<(double At, TfwCommandRecord Command)>();
        void Pump(double from, double to) { for (double t = from; t < to; t += 0.01) if (sender.Due(t) is { } p) sent.Add((t, TfwWire.ReadCommand(p)!.Value)); }
        Pump(0.0, 0.9);
        Assert.Equal(4, sent.Count);                                  // at 0, then every 250 ms
        Assert.All(sent, s => Assert.Equal(first, s.Command.Sequence));   // the second waits for the first's echo

        sender.Acknowledge(0);                                        // an echo of nothing
        Assert.Equal(2, sender.Pending);
        sender.Acknowledge(first);
        Assert.Equal(1, sender.Pending);
        sent.Clear();
        Pump(1.0, 1.05);
        Assert.Equal(second, Assert.Single(sent).Command.Sequence);   // at once, no resend wait
        sender.Acknowledge(second);
        Assert.Equal(0, sender.Pending);

        // Orders as fast as they come: at most 8 a second.
        sent.Clear();
        for (double t = 2.0; t < 5.0; t += 0.01)
        {
            sender.Issue(TfwCommand.Follow);
            if (sender.Due(t) is { } p) { sent.Add((t, TfwWire.ReadCommand(p)!.Value)); sender.Acknowledge(sent[^1].Command.Sequence); }
        }
        Assert.All(sent.Zip(sent.Skip(1)), x => Assert.True(x.Second.At - x.First.At >= TfwCommandSender.MinGapSeconds - 1e-9));

        var full = new TfwCommandSender();
        for (int i = 0; i < TfwCommandSender.MaxPending; i++) Assert.NotNull(full.Issue(TfwCommand.Follow));
        Assert.Null(full.Issue(TfwCommand.Follow));
    }

    [Fact]
    public void A_sequence_counts_once_and_the_wrap_goes_on()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(1));
        var client = h.Clients[0];
        var run = h.Server.World.Tfw!;
        int player = client.PlayerId;
        void Send(ushort sequence)
        {
            Assert.True(client.SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(sequence, TfwCommand.RequestPickup, 0))));
            h.Run(TimeSpan.FromMilliseconds(130));   // under the rate
        }
        Send(65534);
        Send(65534);   // a resend
        Send(65535);
        Send(65534);   // late
        Send(1);       // over the wrap
        Assert.Equal(3, run.QueuedCommands);
        Assert.Equal(2, h.Server.TfwCommandsRepeated);
        Assert.Equal((ushort)1, h.Server.TfwCommandEcho(player));
        var seen = new List<ushort>();
        while (run.TryDequeue(out _, out var c)) seen.Add(c.Sequence);
        Assert.Equal(new ushort[] { 65534, 65535, 1 }, seen);
    }

    [Fact]
    public void A_full_queue_leaves_the_echo_so_the_resend_gets_in()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(1));
        var client = h.Clients[0];
        var run = h.Server.World.Tfw!;
        for (int i = 0; i < TfwRun.MaxQueuedCommands; i++) Assert.True(run.Enqueue(99, default));
        client.SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(5, TfwCommand.Follow, 0)));
        h.Run(TimeSpan.FromMilliseconds(130));
        Assert.Equal(1, h.Server.TfwCommandsRefused);
        Assert.Equal((ushort)0, h.Server.TfwCommandEcho(client.PlayerId));
        Assert.True(run.TryDequeue(out _, out _));
        client.SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(5, TfwCommand.Follow, 0)));
        h.Run(TimeSpan.FromMilliseconds(130));
        Assert.Equal((ushort)5, h.Server.TfwCommandEcho(client.PlayerId));
    }

    [Fact]
    public void A_flood_is_cut_at_the_cores_rate_whatever_the_sequences()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(1));
        for (ushort s = 1; s <= 30; s++) h.Clients[0].SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(s, TfwCommand.RequestPickup, 0)));
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(8, h.Server.World.Tfw!.QueuedCommands);
        Assert.Equal(22, h.Server.ExtrasCommandsDropped);
    }

    [Fact]
    public void Over_a_lossy_link_a_command_arrives_once_and_comes_back_echoed()
    {
        var h = WarServer(link: new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), 0.3));
        h.Run(TimeSpan.FromSeconds(4));
        Feed(h.Server, 0);
        var client = h.Clients[0];
        Assert.Equal(ClientStatus.Connected, client.Status);
        var state = new TfwClientState();
        var run = h.Server.World.Tfw!;
        int applied = 0;
        ushort first = state.Commands.Issue(TfwCommand.Deploy, (byte)TfwLane.B)!.Value;
        ushort second = state.Commands.Issue(TfwCommand.Hold, point: new Vector3(3f, 4f, 0f))!.Value;
        h.Run(TimeSpan.FromSeconds(8), after: () =>
        {
            foreach (var s in client.TakeExtras()) if (TfwWire.Owns(s.Tag)) state.Apply(s, Box, h.Clock.Now.TotalSeconds);
            if (state.Commands.Due(h.Clock.Now.TotalSeconds) is { } payload) client.SendExtrasCommand(TfwWire.CommandTag, payload);
            while (run.TryDequeue(out int who, out var c))
            {
                Assert.Equal(client.PlayerId, who);
                Assert.Equal(++applied == 1 ? first : second, c.Sequence);
            }
        });
        Assert.Equal(2, applied);
        Assert.Equal(0, state.Commands.Pending);
        Assert.Equal(second, state.War!.Value.CommandEcho);
    }

    // ---------------------------------------------------------------- the send

    [Fact]
    public void A_client_hears_the_war_each_second_and_on_a_change_and_the_objectives_every_ten_ticks()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(3));
        Feed(h.Server, 0);
        h.Run(TimeSpan.FromSeconds(1));
        var client = h.Clients[0];
        client.TakeExtras();
        var wars = new List<uint>();
        var objectives = new List<uint>();
        void Listen()
        {
            foreach (var s in client.TakeExtras())
            {
                Assert.True(s.Payload.Length <= Protocol.MaxExtrasPayloadBytes);
                if (s.Tag == TfwWire.WarTag) wars.Add(s.ServerTick);
                if (s.Tag == TfwWire.ObjectivesTag) objectives.Add(s.ServerTick);
            }
        }
        h.Run(TimeSpan.FromSeconds(3), after: Listen);
        Assert.InRange(wars.Count, 3, 4);
        Assert.All(wars.Zip(wars.Skip(1)), p => Assert.Equal(30u, p.Second - p.First));
        Assert.All(objectives.Zip(objectives.Skip(1)), p => Assert.Equal(10u, p.Second - p.First));
        Assert.InRange(objectives.Count, 8, 10);

        wars.Clear();
        h.Server.TfwFeed.Transport = 4;   // a change goes at once
        h.Run(TimeSpan.FromMilliseconds(100), after: Listen);
        Assert.NotEmpty(wars);
    }

    [Fact]
    public void Without_a_box_the_war_still_goes_but_no_position_does()
    {
        var h = WarServer();
        Feed(h.Server, 10);
        h.Server.World.Musou!.Director = default;
        var feed = h.Server.TfwFeed;
        feed.AddEvent(new TfwGuardEvent(TfwGuardEventKind.Death, 1, 1, 0, default));
        h.Run(TimeSpan.FromSeconds(3));
        var tags = Tags(h.Clients[0]).Distinct().ToArray();
        Assert.Equal(new[] { TfwWire.WarTag }, tags);
        Assert.Single(feed.Events);   // kept for the first send with a box
    }

    [Fact]
    public void Deaths_go_three_times_and_first_and_shots_once()
    {
        var window = new TfwEventWindow();
        var death = new TfwGuardEvent(TfwGuardEventKind.Death, 5, 1, 0, default);
        var shot = new TfwGuardEvent(TfwGuardEventKind.Shot, 5, 2, 7, default);
        Assert.Equal(new[] { death, shot }, window.Step(5, [shot, death], 10).ToArray());
        Assert.Equal(new[] { death }, window.Step(6, [], 10).ToArray());
        Assert.Equal(new[] { death }, window.Step(7, [], 10).ToArray());
        Assert.Empty(window.Step(8, [], 10));

        var old = new TfwGuardEvent(TfwGuardEventKind.Shot, 10, 3, 0, default);
        Assert.Equal(new[] { death with { Tick = 10 } }, window.Step(10, [old, death with { Tick = 10 }], 1).ToArray());   // over the cap: the death
        for (uint t = 11; t <= 11 + TfwSendRules.MaxEventAgeTicks; t++) window.Step(t, [], 0);
        Assert.Empty(window.Step(100, [], 10));
        Assert.True(window.EventsDropped >= 1);
    }

    [Fact]
    public void The_interest_keeps_its_bands_a_changed_state_and_its_message_cap()
    {
        var interest = new TfwGuardInterest();
        TfwGuard[] men = [new(1, new Vector3(10f, 0f, 0f), 0f, 1), new(2, new Vector3(30f, 0f, 0f), 0f, 1), new(3, new Vector3(0f, 70f, 0f), 0f, 1)];
        var sent = new Dictionary<ushort, List<uint>> { [1] = new(), [2] = new(), [3] = new() };
        for (uint tick = 1; tick <= 40; tick++)
            foreach (var m in interest.Plan(tick, 30, null, null, men, Vector3.Zero, Box, [], 0f))
                foreach (var g in m.Where(s => s.Tag == TfwWire.GuardsTag).SelectMany(s => TfwWire.ReadGuards(s.Payload, Box)!))
                    sent[g.Id].Add(tick);
        Assert.Equal(40, sent[1].Count);
        Assert.All(sent[2].Skip(1).Zip(sent[2].Skip(2)), p => Assert.Equal(2u, p.Second - p.First));
        Assert.All(sent[3].Skip(1).Zip(sent[3].Skip(2)), p => Assert.Equal(5u, p.Second - p.First));

        men[2] = men[2] with { State = 3 };
        var at = interest.Plan(41, 30, null, null, men, Vector3.Zero, Box, [], 0f);
        Assert.Contains(at.SelectMany(m => m).Where(s => s.Tag == TfwWire.GuardsTag).SelectMany(s => TfwWire.ReadGuards(s.Payload, Box)!), g => g.Id == 3);

        var crowd = Enumerable.Range(1, 240).Select(i => new TfwGuard((ushort)i, Vector3.Zero, 0f, 1)).ToArray();
        var fresh = new TfwGuardInterest();
        for (uint tick = 1; tick <= 100; tick++)
        {
            var plan = fresh.Plan(tick, 30, null, null, crowd, Vector3.Zero, Box, [], 0f);
            Assert.True(plan.Count <= TfwSendRules.MaxMessagesPerTick);
            Assert.All(plan, m => Assert.True(Protocol.EnvelopeBytes(new ExtrasUpdate(tick, m)) <= Protocol.MaxExtrasMessageBytes));
        }
        Assert.True(fresh.RecordsHeld > 0);   // the budget holds them back
    }

    [Fact]
    public void The_war_gets_only_what_musou_leaves()
    {
        var crowd = Enumerable.Range(1, 120).Select(i => new TfwGuard((ushort)i, Vector3.Zero, 0f, 1)).ToArray();
        long Run(float musouPerTick)
        {
            var interest = new TfwGuardInterest();
            long bytes = 0;
            for (uint tick = 1; tick <= 3000; tick++)
                bytes += interest.Plan(tick, 30, null, null, crowd, Vector3.Zero, Box, [], musouPerTick).Sum(m => (long)Protocol.EnvelopeBytes(new ExtrasUpdate(tick, m)));
            return bytes / 100;   // a second's worth
        }
        Assert.InRange(Run(0f), 15_000L, TfwSendRules.GuardBudgetBytesPerSecond + 200L);
        Assert.True(Run(55_000f / 30f) <= TfwSendRules.ClientBudgetBytesPerSecond - 55_000 + 200, $"{Run(55_000f / 30f)}");
        Assert.True(Run(60_000f / 30f) <= 200, $"{Run(60_000f / 30f)}");
    }

    [Fact]
    public void Musous_spending_reads_from_its_bucket()
    {
        var interest = new TfwGuardInterest();
        Assert.Equal(0f, interest.MusouBytes(null, 30));
        Assert.Equal(0f, interest.MusouBytes((float)MusouSendRules.BurstBytes, 30));                       // a full bucket that stayed full
        Assert.Equal(1000f, interest.MusouBytes((float)MusouSendRules.BurstBytes - 1000f, 30), 0.01f);
        interest.MusouBytes(1000f, 30);
        Assert.Equal(MusouSendRules.BudgetBytesPerSecond / 30f + 500f, interest.MusouBytes(500f, 30), 0.1f);
    }

    [Fact]
    public void A_client_stays_within_60_kilobytes_a_second_at_160_orks_and_120_guardsmen()
    {
        var h = WarServer(0);
        h.Server.SetMusouDummies(160);
        h.AddClient(nonce: 1, options: new ClientOptions { LoadMap = _ => new LoadedMap(h.Server.Map!.Collision, h.Server.Map.Hash) });
        h.Run(TimeSpan.FromSeconds(8));
        Feed(h.Server, 120, around: h.Server.World.Players.Values.Single().Position);   // every man within 15 m of the player: the worst case, every tick due
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        Assert.True(h.Server.World.Musou!.Alive >= 150, $"{h.Server.World.Musou.Alive} alive");

        var client = h.Clients[0];
        var musou = new MusouClientState();
        var war = new TfwClientState();
        // A place a guardsman's shots land, each tick: 12 a tick is 360 a second, more than 120 men fire.
        uint shot = 0;
        h.Run(TimeSpan.FromSeconds(1), after: () => client.TakeExtras());
        long bytes0 = h.Server.ExtrasBytesSent, records0 = h.Server.TfwRecordsSent, musou0 = h.Server.MusouRecordsSent;
        const int seconds = 20;
        h.Run(TimeSpan.FromSeconds(seconds), after: () =>
        {
            if (h.Server.TfwFeed.Events.Count == 0)
                for (int i = 0; i < 12; i++) h.Server.TfwFeed.AddEvent(new TfwGuardEvent(TfwGuardEventKind.Shot, h.Server.World.Tick, (ushort)(1 + (shot++ % 120)), 0, new Vector3(1f, 2f, 1f)));
            foreach (var s in client.TakeExtras())
            {
                if (TfwWire.Owns(s.Tag)) war.Apply(s, musou.Director?.Box.Sane(), h.Clock.Now.TotalSeconds);
                else musou.Apply(s, h.Clock.Now.TotalSeconds);
            }
        });
        double perSecond = (h.Server.ExtrasBytesSent - bytes0) / (double)seconds;
        double guardRate = (h.Server.TfwRecordsSent - records0) / (double)seconds, orkRate = (h.Server.MusouRecordsSent - musou0) / (double)seconds;
        output.WriteLine($"160 Orks, 120 Guards, 1 client: {perSecond / 1000.0:F1} KB/s, guards {guardRate:F0} records/s, orks {orkRate:F0} records/s, largest {h.Server.LargestExtrasMessage} B, " +
                         $"guards known {war.GuardCount}, orks known {musou.Count}, events {war.EventsPlayed}");
        Assert.True(perSecond <= 60_000, $"{perSecond:F0} B/s");
        Assert.True(h.Server.LargestExtrasMessage <= Protocol.MaxExtrasMessageBytes);
        Assert.Equal(0, war.SectionsMalformed);
        Assert.Equal(0, musou.SectionsMalformed);
        Assert.Equal(120, war.GuardCount);
        Assert.NotNull(war.War);
        Assert.Equal(5, war.Objectives.Count);
        Assert.True(war.EventsPlayed > 0);
        Assert.True(guardRate >= 300, $"{guardRate:F0} records/s");
    }

    // ---------------------------------------------------------------- the client

    static ExtrasSection Section(uint tick, byte tag, byte[] payload) => new(tick, tag, payload);

    [Fact]
    public void The_client_waits_for_the_box_interpolates_and_plays_an_event_once()
    {
        var state = new TfwClientState();
        var guard = new TfwGuard(7, new Vector3(0f, 0f, 0f), 0f, 1);
        var moved = guard with { Position = new Vector3(10f, 0f, 0f) };
        var death = new TfwGuardEvent(TfwGuardEventKind.Death, 200, 7, 0, default);

        Assert.True(state.Apply(Section(100, TfwWire.GuardsTag, TfwWire.WriteGuards([guard], Box)), null, 1.0));
        Assert.Equal(0, state.GuardCount);                 // held
        Assert.False(state.Apply(Section(100, TfwWire.GuardsTag, [9]), null, 1.0));
        Assert.True(state.Apply(Section(100, TfwWire.WarTag, TfwWire.WriteWar(SampleWar())), null, 1.0));
        Assert.NotNull(state.War);                         // the war needs no box
        Assert.True(state.Apply(Section(110, TfwWire.GuardsTag, TfwWire.WriteGuards([moved], Box)), Box, 1.1));
        Assert.Equal(1, state.GuardCount);                 // the held one came first
        var shown = Assert.Single(state.Sample(105.0, 1.1));
        Assert.Equal(5f, shown.Position.X, 0.02f);
        Assert.Equal((byte)1, shown.State);

        var objectives = TfwWire.WriteObjectives([new TfwObjective(1, TfwPositionState.Clearing, 0f, default)], Box);
        Assert.True(state.Apply(Section(120, TfwWire.ObjectivesTag, objectives), Box, 1.2));
        Assert.True(state.Apply(Section(119, TfwWire.ObjectivesTag, TfwWire.WriteObjectives([], Box)), Box, 1.2));
        Assert.Single(state.Objectives);                   // the late one is ignored
        Assert.Equal(1, state.StaleSections);

        var events = TfwWire.WriteGuardEvents([death], Box);
        state.Apply(Section(200, TfwWire.GuardEventsTag, events), Box, 1.3);
        state.Apply(Section(201, TfwWire.GuardEventsTag, events), Box, 1.3);
        Assert.Equal((1, 1), (state.EventsPlayed, state.EventCopies));
        Assert.Equal(death.Tick, Assert.Single(state.NewEvents).Tick);
        Assert.Equal(0, state.GuardCount);                 // his death removes him
        state.TakeNew();
        Assert.Empty(state.NewEvents);
        state.Apply(Section(150, TfwWire.GuardsTag, TfwWire.WriteGuards([moved], Box)), Box, 1.4);
        Assert.Equal(0, state.GuardCount);                 // a late record does not raise him
        Assert.Equal(1, state.StaleRecords);
        state.Apply(Section(260, TfwWire.GuardsTag, TfwWire.WriteGuards([moved], Box)), Box, 1.5);
        Assert.Equal(1, state.GuardCount);                 // a new man in that slot
        Assert.Empty(state.Sample(260.0, 1.5 + TfwClientState.SilenceSeconds + 0.1));   // silence
    }

    [Fact]
    public void A_client_takes_a_restarted_clock_and_a_clear_forgets_it_all()
    {
        var state = new TfwClientState();
        state.Apply(Section(1000, TfwWire.WarTag, TfwWire.WriteWar(SampleWar())), Box, 1.0);
        state.Apply(Section(1000, TfwWire.GuardsTag, TfwWire.WriteGuards([new TfwGuard(1, default, 0f, 1)], Box)), Box, 1.0);
        state.Apply(Section(999, TfwWire.WarTag, TfwWire.WriteWar(SampleWar() with { Transport = 1 })), Box, 1.0);
        Assert.Equal((byte)5, state.War!.Value.Transport);   // stale
        state.Apply(Section(10, TfwWire.WarTag, TfwWire.WriteWar(SampleWar() with { Transport = 2 })), Box, 2.0);
        Assert.Equal(1, state.Restarts);
        Assert.Equal((byte)2, state.War!.Value.Transport);
        Assert.Equal(0, state.GuardCount);
        state.Commands.Issue(TfwCommand.Follow);
        state.Clear();
        Assert.Null(state.War);
        Assert.Equal(0, state.Commands.Pending);
        Assert.Equal((ushort)1, state.Commands.Issue(TfwCommand.Follow));
    }

    [Fact]
    public void A_bad_section_is_counted_and_nothing_else_happens()
    {
        var state = new TfwClientState();
        Assert.False(state.Apply(Section(1, TfwWire.WarTag, [1, 2]), Box, 0.0));
        Assert.False(state.Apply(Section(1, TfwWire.ObjectivesTag, [9]), Box, 0.0));
        Assert.False(state.Apply(Section(1, TfwWire.GuardsTag, [9]), Box, 0.0));
        Assert.False(state.Apply(Section(1, TfwWire.GuardEventsTag, [9]), Box, 0.0));
        Assert.False(state.Apply(Section(1, MusouWire.DirectorTag, []), Box, 0.0));
        Assert.Equal(5, state.SectionsMalformed);
        Assert.Null(state.War);
    }

    // ---------------------------------------------------------------- isolation

    [Theory]
    [InlineData("faithful")]
    [InlineData("extended")]
    [InlineData("five waves")]   // Musou without a war
    public void Only_a_war_sends_the_wars_tags(string profile)
    {
        var extras = profile switch { "faithful" => ContentExtras.None, "extended" => ContentExtras.All, _ => ContentExtras.Musou };
        var h = new Harness(LinkConditions.Perfect, 5, 1, ConfigFor(extras), new ServerOptions { Match = Rules });
        // A feed, ready as a war's: nothing here may read it.
        h.Server.TfwFeed.War = new WarSim();
        h.Server.TfwFeed.Objectives.Add(new TfwObjective(0, TfwPositionState.Held, 0f, default));
        h.Server.TfwFeed.GuardCount = 1;
        h.Server.TfwFeed.Guards[0] = new TfwGuard(1, default, 0f, 1);
        Assert.Null(h.Server.World.Tfw);
        var heard = new List<byte>();
        h.Run(TimeSpan.FromSeconds(3), after: () => heard.AddRange(Tags(h.Clients[0])));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.DoesNotContain(heard, TfwWire.Owns);
        Assert.Equal(0L, h.Server.TfwRecordsSent);
        // And a war's command does nothing there.
        h.Clients[0].SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(1, TfwCommand.Follow, 0)));
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal((ushort)0, h.Server.TfwCommandEcho(h.Clients[0].PlayerId));
    }

    [Fact]
    public void The_war_has_no_sections_before_a_feed()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(Tags(h.Clients[0]), TfwWire.Owns);
    }
}
