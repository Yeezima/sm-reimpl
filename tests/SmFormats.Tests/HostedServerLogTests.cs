using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>fix/stall-keepalive: which lines of the hosted server's output reach the client's log (synthetic lines in the server's own format).</summary>
public sealed class HostedServerLogTests
{
    const string Stamp = "[2026-11-01T10:15:30Z] ";

    [Theory]
    [InlineData("player 3 (Brother Marcus) joined from 127.0.0.1:50000 (1 players, 0 spectators)")]
    [InlineData("player 3 (Brother Marcus) left from 127.0.0.1:50000: left (0 players, 0 spectators)")]
    [InlineData("player 3 left from 127.0.0.1:50000: timed out (0 players, 0 spectators)")]
    [InlineData("player 3 left from 127.0.0.1:50000: idle for 300 s (no movement or buttons) (0 players, 0 spectators)")]
    [InlineData("refused 127.0.0.1:50001: already 2 players from 127.0.0.1 (--max-per-ip 2)")]
    public void Timestamped_joins_leaves_with_their_reason_and_refusals_are_copied(string body)
    {
        var log = new HostedServerLog();
        Assert.Equal(Stamp + body, log.Filter(Stamp + body));
        Assert.Equal(body, new HostedServerLog().Filter(body));   // and without a stamp, as before
    }

    [Fact]
    public void Fault_notes_the_drop_reasons_and_FATAL_are_copied_with_a_bounded_stack()
    {
        var log = new HostedServerLog();
        Assert.NotNull(log.Filter(Stamp + "packet from 127.0.0.1:50000 (80 bytes) failed: System.InvalidOperationException: boom"));
        int traces = 0;
        for (int i = 0; i < 40; i++) if (log.Filter("   at SmServer.GameServer.Dispatch(Int32 connection, Byte[] packet) in X.cs:line " + i) is not null) traces++;
        Assert.Equal(HostedServerLog.MaxTraceLines, traces);

        // The stack belongs to the note before it: after the next ordinary line, indented text is not copied.
        Assert.Null(log.Filter(Stamp + "ban file bans.txt: 0 entries"));
        Assert.Null(log.Filter("   at Somewhere.Else()"));

        Assert.NotNull(log.Filter(Stamp + "tick 90: snapshots for player 2 failed: System.IO.IOException: x (3 more faults not logged)"));
        Assert.NotNull(log.Filter(Stamp + "player 2 left from 127.0.0.1:1: dropped after its snapshot failed (IOException) (0 players, 0 spectators)"));
        Assert.NotNull(log.Filter(Stamp + "FATAL: 20 ticks in a row failed (last: X: y); the simulation cannot go on"));
    }

    [Fact]
    public void Other_server_output_stays_out_of_the_client_log()
    {
        var log = new HostedServerLog();
        Assert.Null(log.Filter("smserver on UDP 27015"));
        Assert.Null(log.Filter("note: tuning from the install"));
        Assert.Null(log.Filter(Stamp + "skipped 12 late ticks"));
        Assert.Null(log.Filter("   at Orphan.Stack()"));   // no fault before it
        Assert.Null(log.Filter(""));
        Assert.Null(log.Filter("   "));
    }
}
