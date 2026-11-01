using SmNet;

namespace SmNet.Tests;

/// <summary>fix/stall-keepalive: the "connection is gone" line tells a freeze of the client from a server that went silent while the frames ran.</summary>
public class ConnectionLostTests
{
    static string Text(double sinceSnapshot, double worstSinceSnapshot, double longest, double longestAt, int keepAlives = 0, bool on = true) =>
        ConnectionLost.Text(ClientStatus.Disconnected, 42.0, sinceSnapshot, worstSinceSnapshot, longest, longestAt, keepAlives, on, 5.0);

    [Fact]
    public void A_long_frame_since_the_last_snapshot_is_a_freeze_of_this_client()
    {
        string t = Text(sinceSnapshot: 13.5, worstSinceSnapshot: 13.4, longest: 13.4, longestAt: 40.0);
        Assert.Contains("a freeze of this client", t);
        Assert.Contains("13.4 s", t);
        Assert.DoesNotContain("went silent", t);
    }

    [Fact]
    public void Normal_frames_with_no_snapshot_are_a_silent_server_or_network()
    {
        string t = Text(sinceSnapshot: 5.2, worstSinceSnapshot: 0.034, longest: 0.2, longestAt: 3.0);
        Assert.Contains("the server or the network went silent", t);
        Assert.Contains("not a freeze of this client", t);
        Assert.Contains("34 ms", t);
    }

    [Fact]
    public void A_freeze_earlier_in_the_session_is_named_when_the_later_silence_follows_it()
    {
        string t = Text(sinceSnapshot: 5.1, worstSinceSnapshot: 0.02, longest: 9.0, longestAt: 12.5, keepAlives: 0, on: false);
        Assert.Contains("probably a freeze of this client earlier", t);
        Assert.Contains("9.0 s at t 12.5 s", t);
        Assert.Contains("no keep-alive was running", t);
    }

    [Fact]
    public void Snapshots_that_arrived_and_were_refused_are_named_before_any_other_cause()
    {
        string t = ConnectionLost.Text(ClientStatus.Disconnected, 42.0, 5.1, 0.034, 0.2, 3.0, 0, true, 5.0, refusedSnapshots: 102);
        Assert.Contains("this client refused them (102", t);
        Assert.DoesNotContain("went silent", t);
    }

    [Fact]
    public void The_line_says_how_many_keep_alives_were_sent_and_keeps_the_status()
    {
        string t = Text(sinceSnapshot: 40, worstSinceSnapshot: 40, longest: 40, longestAt: 1, keepAlives: 59);
        Assert.Contains("the keep-alive sent 59 packets", t);
        Assert.Contains("(Disconnected)", t);
        Assert.StartsWith("t 42.0 s: the connection is gone", t);
    }
}
