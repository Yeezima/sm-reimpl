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
        Assert.Equal(50, Protocol.Version);
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
        h.Run(TimeSpan.FromSeconds(4));
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
    public void A_spectator_has_no_row_and_a_player_before_its_first_acknowledgement_reads_as_not_known()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 1);
        h.Run(TimeSpan.FromSeconds(2));
        var watcher = h.AddClient(nonce: 4242, options: new ClientOptions { Observer = true });
        h.Run(TimeSpan.FromSeconds(2));
        var board = h.Server.ScoreboardOf();
        Assert.Single(board.Players);
        Assert.DoesNotContain(board.Players, p => p.PlayerId == watcher.PlayerId);
        Assert.InRange(board.Players[0].PingMs, 0, 40);
        // A server that has sent nothing yet: the row says -1 (the client shows a dash).
        Assert.Equal(-1, new RttEstimator().Ms);
    }
}
