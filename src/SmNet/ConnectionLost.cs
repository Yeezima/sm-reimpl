namespace SmNet;

/// <summary>
/// fix/stall-keepalive (ours): what the client's log says when a connection that was up is gone, so a tester's log tells a freeze of the client itself (a frame that took
/// seconds: the main thread was blocked, e.g. compiling shaders on a first draw) from a server or network that went silent while the client's frames ran fine. The numbers come from
/// the client (frames measured by the Godot view, snapshots and keep-alives by <see cref="GameClient"/>); this only words them.
/// </summary>
public static class ConnectionLost
{
    /// <summary>A frame this long counts as a freeze (the server and the client both time out after 5 s of silence; a frame close under that is the same thing).</summary>
    public static double FreezeSeconds(double serverTimeoutSeconds) => Math.Max(1.0, serverTimeoutSeconds - 1.0);

    /// <param name="status">How the connection ended (<see cref="ClientStatus.Disconnected"/> by silence).</param>
    /// <param name="connectedFor">Seconds since the client connected.</param>
    /// <param name="sinceSnapshot">Seconds since the last snapshot was read.</param>
    /// <param name="worstFrameSinceSnapshot">The longest frame since the last snapshot was read, seconds.</param>
    /// <param name="longestFrame">The longest frame since the client connected, seconds, and <paramref name="longestFrameAt"/> when (seconds since it connected).</param>
    /// <param name="keepAlives">Keep-alive batches the background thread sent (<see cref="GameClient.KeepAlivesSent"/>); <paramref name="keepAliveOn"/>: it was running.</param>
    /// <param name="serverTimeout">The client's timeout, seconds (the server's is the same 5 s).</param>
    /// <param name="refusedSnapshots">Snapshots that arrived and were refused since the last accepted one (fix/dead-melee-direction).</param>
    public static string Text(ClientStatus status, double connectedFor, double sinceSnapshot, double worstFrameSinceSnapshot, double longestFrame, double longestFrameAt,
        int keepAlives, bool keepAliveOn, double serverTimeout, int refusedSnapshots = 0)
    {
        double freeze = FreezeSeconds(serverTimeout);
        string keep = keepAliveOn ? $"the keep-alive sent {keepAlives} packets" : "no keep-alive was running";
        string cause =
            refusedSnapshots > 0
                ? $"CAUSE: snapshots kept arriving but this client refused them ({refusedSnapshots} unreadable or implausible since the last good one in {sinceSnapshot:F1} s): a bug in the protocol or the state sent, not the network and not a freeze."
            : worstFrameSinceSnapshot >= freeze
                ? $"CAUSE: a freeze of this client: one frame took {worstFrameSinceSnapshot:F1} s (its main thread was blocked), so no snapshot was read for {sinceSnapshot:F1} s; {keep}."
            : longestFrame >= freeze
                ? $"CAUSE: probably a freeze of this client earlier: one frame took {longestFrame:F1} s at t {longestFrameAt:F1} s ({keep}); the server may have dropped the player then, and the " +
                  $"client noticed {sinceSnapshot:F1} s after the last snapshot (frames since then ran normally, the worst {worstFrameSinceSnapshot * 1000:F0} ms). Check 'hosted server:' lines for the reason."
            : $"CAUSE: the server or the network went silent, not a freeze of this client: frames ran normally (the worst since the last snapshot {worstFrameSinceSnapshot * 1000:F0} ms, " +
              $"the longest of the session {longestFrame:F1} s) but no snapshot was read for {sinceSnapshot:F1} s.";
        return $"t {connectedFor:F1} s: the connection is gone ({status}); frames since the last snapshot are not sent. {cause}";
    }
}
