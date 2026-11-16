namespace SmNet.Tests;

/// <summary>
/// A Classic PvP join refused because the client's own local Musou server kept the session on the Musou profile (the owner, 2026-11-07): stop that server and join again silently, once;
/// everything else shows the normal error (<see cref="MusouJoinRetry"/>).
/// </summary>
public class MusouJoinRetryTests
{
    static JoinRetryDecision D(RejectReason? reason = RejectReason.ConfigMismatch, bool running = true, bool musou = true, bool joinMusou = true, bool lobbyMusou = false,
        bool commandLine = false, bool toHosted = false, bool retried = false) =>
        MusouJoinRetry.Decide(reason, running, musou, joinMusou, lobbyMusou, commandLine, toHosted, retried);

    [Fact]
    public void A_config_refusal_of_a_join_made_musou_by_the_own_musou_server_stops_it_and_retries()
    {
        Assert.Equal(JoinRetryDecision.StopMusouAndRetry, D());
        Assert.Equal("Enable extended mode before reconnecting to the server.", ConnectFailure.RejectText(RejectReason.ConfigMismatch, null));   // the error it replaces
    }

    [Theory]
    [InlineData(RejectReason.ContentMismatch)]
    [InlineData(RejectReason.ProtocolMismatch)]
    [InlineData(RejectReason.MapMismatch)]
    [InlineData(RejectReason.ServerFull)]
    [InlineData(null)]
    public void Any_other_refusal_shows_its_error(RejectReason? reason) => Assert.Equal(JoinRetryDecision.ShowError, D(reason));

    [Fact]
    public void Without_a_musou_server_of_this_client_nothing_is_stopped()
    {
        Assert.Equal(JoinRetryDecision.ShowError, D(running: false));   // no server started here (another one on the PC is never touched)
        Assert.Equal(JoinRetryDecision.ShowError, D(musou: false));     // the hosted server is a Classic one: the mismatch is something else
    }

    [Fact]
    public void A_join_that_meant_musou_is_left_alone()
    {
        Assert.Equal(JoinRetryDecision.ShowError, D(joinMusou: false));    // the join did not offer Musou: the refusal has another cause
        Assert.Equal(JoinRetryDecision.ShowError, D(lobbyMusou: true));    // the lobby's own profile is Musou
        Assert.Equal(JoinRetryDecision.ShowError, D(commandLine: true));   // --content musou asked for it
        Assert.Equal(JoinRetryDecision.ShowError, D(toHosted: true));      // the own Musou server itself
    }

    [Fact]
    public void The_retry_happens_once_and_a_failed_retry_shows_the_normal_error() => Assert.Equal(JoinRetryDecision.ShowError, D(retried: true));

    [Theory]
    [InlineData("127.0.0.1:27015", 27015, true)]
    [InlineData("localhost:27015", 27015, true)]
    [InlineData("[::1]:27015", 27015, true)]
    [InlineData(" LOCALHOST:27015 ", 27015, true)]
    [InlineData("127.0.0.1:27016", 27015, false)]
    [InlineData("203.0.113.7:27015", 27015, false)]
    [InlineData("192.168.1.5:27015", 27015, false)]
    [InlineData("127.0.0.1", 27015, false)]
    [InlineData("", 27015, false)]
    [InlineData("127.0.0.1:+27015", 27015, false)]
    public void The_hosted_server_is_this_pcs_loopback_on_its_port(string target, int port, bool hosted) => Assert.Equal(hosted, MusouJoinRetry.IsHostedTarget(target, port));
}
