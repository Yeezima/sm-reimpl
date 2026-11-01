using SmNet;
using SmSim;
using Xunit;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// net/scoreboard-ping (protocol 50; owner 2026-10-31: "add ping (ms) value in the scoreboard for each player"; ours): the server's smoothed round-trip
/// time per player (<see cref="RttEstimator"/>, fed by snapshot acknowledgements) in every scoreboard row as a u16 (ms; 65535 not known).
/// </summary>
public class ScoreboardPingTests(ITestOutputHelper output)
{
    [Fact]
    public void The_estimator_takes_the_first_sample_then_moves_an_eighth_of_the_way_and_clamps()
    {
        var rtt = new RttEstimator();
        Assert.Equal(-1, rtt.Ms);   // nothing measured yet
        rtt.Add(double.NaN);
        Assert.Equal((-1, 0), (rtt.Ms, rtt.Samples));   // a non-finite sample is ignored
        rtt.Add(80);
        Assert.Equal(80, rtt.Ms);
        rtt.Add(160);
        Assert.Equal(90, rtt.Ms);   // 80 + (160 - 80) / 8
        for (int i = 0; i < 200; i++) rtt.Add(40);
        Assert.Equal(40, rtt.Ms);   // converges on a steady link
        // A 5 s stall counts as 999 ms, so one bad sample moves the estimate by at most (999 - 40) / 8.
        rtt.Add(5000);
        Assert.Equal(160, rtt.Ms);   // 40 + 959 / 8 = 159.9
        rtt.Add(-20);
        Assert.InRange(rtt.Ms, 0, 160);
        for (int i = 0; i < 200; i++) rtt.Add(100_000);
        Assert.Equal(RttEstimator.MaxMs, rtt.Ms);   // never above three digits
        var fresh = new RttEstimator();
        fresh.Add(-5);
        Assert.Equal(0, fresh.Ms);
    }

    [Fact]
    public void The_scoreboard_shows_the_ping_as_digits_or_a_dash_in_three_colour_bands()
    {
        Assert.Equal(("42", "0", "999", "—", "999"), (RttEstimator.Display(42), RttEstimator.Display(0), RttEstimator.Display(999), RttEstimator.Display(-1), RttEstimator.Display(5000)));
        Assert.Equal(
            [PingQuality.Unknown, PingQuality.Good, PingQuality.Good, PingQuality.Fair, PingQuality.Fair, PingQuality.Poor, PingQuality.Poor],
            new[] { -1, 0, 79, 80, 149, 150, 999 }.Select(RttEstimator.QualityOf));
    }

    [Fact]
    public void The_ping_round_trips_in_the_scoreboard_row_and_the_reader_refuses_values_above_999()
    {
        Assert.Equal(53, Protocol.Version);
        Assert.Equal(16, Protocol.ScoreRowBytes);
        var board = new Scoreboard(9, true, 41, 600f, false,
        [
            new ScoreEntry(1, Team.SpaceMarines, 0, 1, 2, 3, 0, "Brother Aurex", 42),
            new ScoreEntry(2, Team.Chaos, 1, 0, 0, 0, -1, "", 0),
            new ScoreEntry(3, Team.Chaos, 1, 0, 0),   // not measured yet: -1
            new ScoreEntry(4, Team.Chaos, 1, 0, 0, 0, -1, "", RttEstimator.MaxMs),
        ]);
        var bytes = Protocol.Write(board);
        Assert.Equal(13 + 4 * Protocol.ScoreRowBytes + "Brother Aurex".Length, bytes.Length);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(bytes));
        Assert.Equal(board.Players, back.Players);
        Assert.Equal([42, 0, -1, 999], back.Players.Select(p => p.PingMs));

        // The writer clamps: above 999 travels as 999, any negative as "not known".
        var odd = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Players = [new ScoreEntry(1, Team.Chaos, 1, 0, 0, PingMs: 4000), new ScoreEntry(2, Team.Chaos, 1, 0, 0, PingMs: -7)] })));
        Assert.Equal([999, -1], odd.Players.Select(p => p.PingMs));

        // The ping is the u16 just before the name's length byte; 1000..65534 there is refused, 65535 reads as not known.
        var one = Protocol.Write(board with { Players = [new ScoreEntry(1, Team.Chaos, 1, 0, 0, 0, -1, "Skarne", 77)] });
        int pingAt = one.Length - "Skarne".Length - 1 - 2;
        Assert.Equal(77, one[pingAt] | one[pingAt + 1] << 8);
        foreach (int bad in new[] { 1000, 30000, 65534 })
        {
            var raw = one.ToArray(); raw[pingAt] = (byte)bad; raw[pingAt + 1] = (byte)(bad >> 8);
            Assert.Null(Protocol.Read(raw));
        }
        var unknown = one.ToArray(); unknown[pingAt] = 0xFF; unknown[pingAt + 1] = 0xFF;
        Assert.Equal(-1, Assert.IsType<Scoreboard>(Protocol.Read(unknown)).Players[0].PingMs);
    }

    [Fact]
    public void The_fullest_scoreboard_with_pings_and_spectators_fits_a_packet()
    {
        var seize = new SeizeGroundInfo(20f, 10f, 2, 5f, [.. Enumerable.Range(0, World.MaxControlPoints).Select(i => new ControlPointInfo(new string('p', Protocol.MaxMapName), new System.Numerics.Vector3(i, 2, 3), 5f, 2f))]);
        var full = new Scoreboard(1, true, 1, 1f, true,
            [.. Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, Team.Chaos, 1, 65535, 65535, 65535, 3, ("Name" + i).PadRight(PlayerNames.MaxLength, 'x'), 999))], seize,
            [.. Enumerable.Range(100, Protocol.MaxSpectators).Select(i => new SpectatorEntry(i, ("Spec" + i).PadRight(PlayerNames.MaxLength, 'y')))]);
        int size = Protocol.Write(full).Length;
        output.WriteLine($"fullest scoreboard (16 rows with pings and 24-character names, {World.MaxControlPoints} points, {Protocol.MaxSpectators} spectators): {size} bytes (budget {Protocol.SnapshotBudget})");
        Assert.Equal(1145, size);
        Assert.True(size <= Protocol.SnapshotBudget, $"{size} bytes");
        var back = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(full)));
        Assert.Equal(full.Players, back.Players);
        Assert.Equal(full.Spectators, back.Spectators);
    }

    /// <summary>The newest scoreboard row of the client's own player, if any.</summary>
    static ScoreEntry? OwnRow(GameClient c) => c.Scoreboard?.Players.Where(e => e.PlayerId == c.PlayerId).Select(e => (ScoreEntry?)e).FirstOrDefault();

    [Theory]
    [InlineData(0, 0, 40)]      // a perfect link: only the wait for the next input send (one tick at 30 Hz) and the 1 ms steps
    [InlineData(40, 80, 125)]   // 40 ms each way: 80 ms plus that wait
    [InlineData(100, 200, 245)]
    public void Clients_on_a_loopback_link_see_plausible_pings_for_everyone(int oneWayMs, int low, int high)
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(oneWayMs), TimeSpan.Zero, 0), seed: 5, clients: 3);
        var sent = new Dictionary<int, int>();
        h.Server.SnapshotHookForTests = id => sent[id] = sent.GetValueOrDefault(id) + 1;
        h.Run(TimeSpan.FromSeconds(4));
        // One sample per acknowledged snapshot: on a lossless link the client (30 input batches a second) acknowledges every snapshot (20 a second) once,
        // all but the ones still in flight (at most the last few round trips' worth).
        int inFlight = 2 + (int)Math.Ceiling((2 * oneWayMs + 40) / 50.0);
        foreach (var c in h.Clients)
        {
            int samples = h.Server.PingSamplesOfForTests(c.PlayerId);
            output.WriteLine($"{oneWayMs} ms one way: P{c.PlayerId} sent {sent[c.PlayerId]} snapshots, {samples} ping samples");
            Assert.InRange(samples, sent[c.PlayerId] - inFlight, sent[c.PlayerId]);
        }
        foreach (var c in h.Clients)
        {
            Assert.Equal(ClientStatus.Connected, c.Status);
            var board = Assert.IsType<Scoreboard>(c.Scoreboard);
            Assert.Equal(3, board.Players.Length);
            foreach (var row in board.Players)
            {
                output.WriteLine($"{oneWayMs} ms one way: client {c.PlayerId} sees P{row.PlayerId} at {row.PingMs} ms (server {h.Server.PingOfForTests(row.PlayerId)} ms)");
                Assert.InRange(row.PingMs, low, high);
            }
            Assert.NotNull(OwnRow(c));
        }
    }

    [Fact]
    public void A_lossy_jittery_link_still_gives_a_ping_and_the_board_follows_a_change_of_latency()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(10), 0.1), seed: 11, clients: 2);
        h.Run(TimeSpan.FromSeconds(4));
        int before = h.Server.PingOfForTests(h.Clients[0].PlayerId);
        output.WriteLine($"30 ms ± 10, 10 % loss: {before} ms");
        Assert.InRange(before, 40, 130);
        // The link gets slower: within a few seconds the smoothed value and the next scoreboard follow.
        h.Net.Conditions = new LinkConditions(TimeSpan.FromMilliseconds(120), TimeSpan.Zero, 0);
        h.Run(TimeSpan.FromSeconds(5));
        int after = h.Server.PingOfForTests(h.Clients[0].PlayerId);
        output.WriteLine($"then 120 ms each way: {after} ms; the client's board says {OwnRow(h.Clients[0])?.PingMs} ms");
        Assert.InRange(after, 230, 290);
        Assert.InRange(OwnRow(h.Clients[0])!.Value.PingMs, 200, 290);
    }

    [Fact]
    public void A_spectator_has_no_row_and_the_player_beside_it_is_measured()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 1);
        h.Run(TimeSpan.FromSeconds(2));
        var watcher = h.AddClient(nonce: 4242, options: new ClientOptions { Observer = true });
        h.Run(TimeSpan.FromSeconds(2));
        var board = h.Server.ScoreboardOf();
        Assert.Single(board.Players);
        Assert.DoesNotContain(board.Players, p => p.PlayerId == watcher.PlayerId);
        Assert.InRange(board.Players[0].PingMs, 0, 40);
    }

    // ---------------------------------------------------------------- the server's sampling, driven by a raw client

    /// <summary>A raw player on a perfect link: its id and session, and the snapshot ticks it has received (newest last).</summary>
    static (int Id, uint Session, List<uint> Snapshots) Join(Harness h, IClientTransport raw, uint nonce)
    {
        var (id, session) = RobustnessTests.ConnectRaw(h, raw, nonce);
        return (id, session, []);
    }

    /// <summary>Steps the harness <paramref name="ms"/> times (1 ms each), collecting the snapshot ticks <paramref name="raw"/> receives.</summary>
    static void Pump(Harness h, IClientTransport raw, List<uint> snapshots, int ms)
    {
        for (int i = 0; i < ms; i++)
        {
            h.Step();
            while (raw.TryReceive(out var p)) if (Protocol.Read(p) is Snapshot s) snapshots.Add(s.ServerTick);
        }
    }

    static void Ack(IClientTransport raw, uint session, uint snapshot, uint inputTick) =>
        raw.Send(Protocol.Write(new InputBatch([new PlayerInput(inputTick, 0, 0, 0, InputButtons.None)], session, snapshot)));

    [Fact]
    public void A_player_reads_as_not_known_in_the_scoreboard_until_its_first_acknowledgement()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 0);
        var raw = h.Net.Connect();
        var (id, session, snaps) = Join(h, raw, 7);
        // Right after the join: a row, but no measurement.
        Assert.Equal(-1, h.Server.ScoreboardOf().Players.Single(p => p.PlayerId == id).PingMs);
        Pump(h, raw, snaps, 300);   // snapshots arrive, nothing is acknowledged
        Assert.NotEmpty(snaps);
        Assert.Equal((-1, 0), (h.Server.ScoreboardOf().Players.Single(p => p.PlayerId == id).PingMs, h.Server.PingSamplesOfForTests(id)));
        Ack(raw, session, 0, h.Server.World.Tick + 2);   // an input that acknowledges nothing (0) gives no sample either
        Pump(h, raw, snaps, 5);
        Assert.Equal(0, h.Server.PingSamplesOfForTests(id));
        // The first acknowledgement of the newest snapshot gives the first sample.
        Ack(raw, session, snaps[^1], h.Server.World.Tick + 2);
        Pump(h, raw, snaps, 1);
        Assert.Equal(1, h.Server.PingSamplesOfForTests(id));
        Assert.InRange(h.Server.ScoreboardOf().Players.Single(p => p.PlayerId == id).PingMs, 1, 999);
    }

    [Fact]
    public void A_repeated_acknowledgement_adds_no_sample_and_one_that_skips_ticks_adds_one()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 0);
        var raw = h.Net.Connect();
        var (id, session, snaps) = Join(h, raw, 8);
        Pump(h, raw, snaps, 200);
        uint first = snaps[^1];
        int count = snaps.Count;
        Pump(h, raw, snaps, 40);   // the client takes 40 ms to answer the snapshot it holds
        Ack(raw, session, first, h.Server.World.Tick + 2);
        Pump(h, raw, snaps, 1);
        Assert.Equal(1, h.Server.PingSamplesOfForTests(id));
        int afterFirst = h.Server.PingOfForTests(id);
        Assert.InRange(afterFirst, 40, 50);   // the send-to-acknowledgement time, from the server's clock
        // The same acknowledgement again (every input batch carries it until a newer snapshot arrives): no sample, the value stays.
        for (int i = 0; i < 5; i++) { Ack(raw, session, first, h.Server.World.Tick + 2); Pump(h, raw, snaps, 10); }
        Assert.Equal((1, afterFirst), (h.Server.PingSamplesOfForTests(id), h.Server.PingOfForTests(id)));
        // An older one (a reordered batch): none either.
        Ack(raw, session, snaps[0], h.Server.World.Tick + 2);
        Pump(h, raw, snaps, 1);
        Assert.Equal(1, h.Server.PingSamplesOfForTests(id));
        Pump(h, raw, snaps, 200);
        // Several snapshots were missed (lost): acknowledging the newest one skips their ticks and gives exactly one sample, of that snapshot's age.
        Assert.True(snaps.Count - count >= 3, "snapshots kept arriving");
        uint skipped = snaps[^1];
        Assert.True(skipped - first >= 3);
        Ack(raw, session, skipped, h.Server.World.Tick + 2);
        Pump(h, raw, snaps, 1);
        Assert.Equal(2, h.Server.PingSamplesOfForTests(id));
        Assert.InRange(h.Server.PingOfForTests(id), 0, 52);   // the newest snapshot was sent within the last 51 ms: the estimate stays in that range
    }

    [Fact]
    public void An_acknowledgement_older_than_the_64_tick_ring_gives_no_sample()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 0);
        var raw = h.Net.Connect();
        var (id, session, snaps) = Join(h, raw, 9);
        Pump(h, raw, snaps, 100);
        uint old = snaps[^1];
        // More than 64 ticks (2.1 s at 30 Hz) later the slot of that tick has been reused by a newer snapshot: the acknowledgement is no longer timed.
        Pump(h, raw, snaps, (int)Math.Ceiling(1000.0 * (SmServer.GameServer.TimedTicks + 4) / h.Config.TickRate));
        Assert.True(h.Server.World.Tick - old > SmServer.GameServer.TimedTicks);
        Ack(raw, session, old, h.Server.World.Tick + 2);
        Pump(h, raw, snaps, 1);
        Assert.Equal((0, -1), (h.Server.PingSamplesOfForTests(id), h.Server.PingOfForTests(id)));
        // A snapshot still inside the ring is timed.
        uint recent = snaps[^1];
        Ack(raw, session, recent, h.Server.World.Tick + 2);
        Pump(h, raw, snaps, 1);
        Assert.Equal(1, h.Server.PingSamplesOfForTests(id));
    }

    [Fact]
    public void A_player_who_leaves_takes_its_ping_along_and_a_rejoin_starts_unmeasured()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0), seed: 4, clients: 2);
        h.Run(TimeSpan.FromSeconds(3));
        int leaving = h.Clients[1].PlayerId;
        Assert.InRange(h.Server.PingOfForTests(leaving), 40, 90);
        h.Clients[1].Leave();
        h.Clients.RemoveAt(1);
        h.Run(TimeSpan.FromSeconds(1.5));
        var board = h.Server.ScoreboardOf();
        Assert.DoesNotContain(board.Players, p => p.PlayerId == leaving);
        Assert.Single(board.Players);
        Assert.InRange(board.Players[0].PingMs, 40, 90);   // the one who stayed keeps its value
        // A new player starts with no measurement (no state carries over), then gets its own.
        var rejoin = h.AddClient(nonce: 555);
        for (int i = 0; i < 2000 && rejoin.Status != ClientStatus.Connected; i++) h.Step();
        Assert.Equal(-1, h.Server.ScoreboardOf().Players.Single(p => p.PlayerId == rejoin.PlayerId).PingMs);
        h.Run(TimeSpan.FromSeconds(2));
        Assert.InRange(h.Server.PingOfForTests(rejoin.PlayerId), 40, 90);
    }
}
