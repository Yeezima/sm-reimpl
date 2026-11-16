using SmSim;

namespace SmNet;

/// <summary>
/// Protocol 80 (dev-tools; ours): the developer tools on the client. <see cref="DevState"/> arrives only from a server that has them on (<c>--dev-tools</c>, loopback only); the panel shows it and
/// sends <see cref="DevCommand"/> requests. Nothing here runs against a server that sends no state: <see cref="DevActive"/> stays false and the prediction is untouched.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>Seconds a state stays valid (the server sends one a second and on every change).</summary>
    public const double DevStateLifetime = 3.5;
    DevState? _dev;
    TimeSpan _devAt;

    /// <summary>
    /// The unscaled clock the developer state's freshness is judged on (set by a client whose own clock is scalable). The server repeats the state every real second while it runs slow or paused, so a
    /// state that stops coming means the server is gone, whatever the scaled clock says; the client then returns to normal time and its own timeouts can run.
    /// </summary>
    public IClock? UnscaledClock { get; set; }

    TimeSpan DevNow => (UnscaledClock ?? _clock).Now;

    void OnDevState(DevState s) { _dev = s; _devAt = DevNow; }

    /// <summary>The newest developer-tools state while it is fresh, else null (a server without the tools never sends one).</summary>
    public DevState? Dev => _dev is not null && (DevNow - _devAt).TotalSeconds <= DevStateLifetime ? _dev : null;

    /// <summary>Whether the server announced the developer tools (recently).</summary>
    public bool DevActive => Dev is not null;

    /// <summary>The flags the own prediction mirrors: the server's, while they are announced; none otherwise.</summary>
    DevToolsFlags DevPredicted => _dev is not null ? (Dev?.Flags ?? DevToolsFlags.None) : DevToolsFlags.None;

    /// <summary>The own predicted state after a step with the developer tools' top-up applied the way the server applies it after its tick (no change without flags).</summary>
    PlayerState DevTopUp(PlayerState s) => _dev is null ? s : DevTools.TopUp(s, _config, DevPredicted);

    /// <summary>Sends a developer-tools request (one datagram, unacknowledged: the panel shows the server's state, so a lost click shows as no change). False, and nothing sent, unless connected as a player.</summary>
    public bool SendDevCommand(DevCommandKind kind, byte argument, bool value, int extra = 0)
    {
        if (Status != ClientStatus.Connected || _options.Observer) return false;
        _transport.Send(Protocol.Write(new DevCommand(_session, kind, argument, value, extra)));
        return true;
    }
}
