namespace SmNet;

/// <summary>What a failed join does next (<see cref="MusouJoinRetry.Decide"/>).</summary>
public enum JoinRetryDecision
{
    /// <summary>The normal error, as before.</summary>
    ShowError,
    /// <summary>Stop the Musou server this client started (only that process) and join again, silently.</summary>
    StopMusouAndRetry,
}

/// <summary>
/// A Classic PvP join refused while this client's own local Musou server runs (the owner, 2026-11-07: "currently when you try to join a classic pvp match when you still have musou mode
/// local server running the 'enable extended mode' error message appears. it should detect that the error is the musou server running. it should automatically stop the musou server and
/// attempt a rejoin silently with no notification to the user."). Cause: while the front end's Musou server runs the session keeps the Musou profile, so a join from the lobby offers the
/// Musou config and a Classic server refuses it (<see cref="RejectReason.ConfigMismatch"/>, "Enable extended mode ..."). The decision is the front end's own knowledge only: the server
/// it started (<c>HostedServer</c>, never a process found by name), the profile the join offered and the one the lobby remembers. Engine-free.
/// </summary>
public static class MusouJoinRetry
{
    /// <summary>
    /// <see cref="JoinRetryDecision.StopMusouAndRetry"/> when every one holds: the server refused the config (<see cref="RejectReason.ConfigMismatch"/>); this client started a server and
    /// it runs Musou (<paramref name="hostedRunning"/>, <paramref name="hostedIsMusou"/>); the join offered Musou only because of it (<paramref name="joinWasMusou"/> while the lobby's
    /// own profile <paramref name="lobbyIsMusou"/> is not, and the game was not started with <c>--content musou</c>: <paramref name="commandLineMusou"/>); the join went to another
    /// server than that one (<paramref name="targetIsHosted"/> false: joining the Musou server itself with the Musou profile is no mismatch); and no retry was made for this join yet.
    /// Anything else: <see cref="JoinRetryDecision.ShowError"/>.
    /// </summary>
    public static JoinRetryDecision Decide(RejectReason? reason, bool hostedRunning, bool hostedIsMusou, bool joinWasMusou, bool lobbyIsMusou, bool commandLineMusou,
        bool targetIsHosted, bool alreadyRetried) =>
        reason == RejectReason.ConfigMismatch && hostedRunning && hostedIsMusou && joinWasMusou && !lobbyIsMusou && !commandLineMusou && !targetIsHosted && !alreadyRetried
            ? JoinRetryDecision.StopMusouAndRetry
            : JoinRetryDecision.ShowError;

    /// <summary>
    /// Whether <paramref name="target"/> ("host:port") is the server this client hosts on <paramref name="hostedPort"/>: the same port on this PC's loopback name (127.0.0.1, localhost, ::1).
    /// Another address with that port counts as another server (a LAN address of this PC is not recognised: its retry then fails and shows the normal error).
    /// </summary>
    public static bool IsHostedTarget(string target, int hostedPort)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        string t = target.Trim();
        int colon = t.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(t[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) || port != hostedPort) return false;
        string host = t[..colon].Trim('[', ']').ToLowerInvariant();
        return host is "127.0.0.1" or "localhost" or "::1";
    }
}
