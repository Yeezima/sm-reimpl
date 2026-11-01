using System;
using System.Diagnostics;
using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// fix/stall-keepalive (ours; no protocol change). A playtester on an integrated GPU was dropped when a first draw (shader compile) blocked the main thread for seconds: the server
/// timed him out after 5 s of silence and the client's log only said "the connection is gone". Three parts: the keep-alive thread of <see cref="GameClient"/> (on unless
/// <c>--no-stall-keepalive</c>), this file's frame bookkeeping so every player's log says whether a freeze of the client or a silent server ended the connection
/// (<see cref="ConnectionLost"/>), and the test aid <c>--test-freeze-at &lt;s&gt; --test-freeze-seconds &lt;n&gt;</c>: once connected for that long, the main thread sleeps for n seconds
/// (the live check of the keep-alive; with <c>--no-stall-keepalive</c> the same freeze is the failure).
/// </summary>
public partial class NetworkView
{
    /// <summary>Test aid (<c>--no-stall-keepalive</c>): no background keep-alive, the behaviour before this fix.</summary>
    static readonly bool NoStallKeepAlive = CmdFlag("no-stall-keepalive");
    static readonly float TestFreezeAt = CmdFloat("test-freeze-at");
    static readonly float TestFreezeSeconds = CmdFloat("test-freeze-seconds");

    /// <summary>A frame this long is written to the log (it only happens when the main thread was blocked).</summary>
    const double SlowFrameLogSeconds = 1.0;

    bool _freezeDone;
    long _stallFrameStamp = Stopwatch.GetTimestamp();
    uint _stallSnapshotTick;
    double _worstFrameSinceSnapshot, _longestConnectedFrame, _longestConnectedFrameAt;
    int _keepAlivesLogged;

    /// <summary>Called first in every connected frame, before <see cref="GameClient.Update"/>: the time since the previous frame began, kept per snapshot window and for the session.</summary>
    void StallFrameStart()
    {
        long now = Stopwatch.GetTimestamp();
        double gap = Stopwatch.GetElapsedTime(_stallFrameStamp, now).TotalSeconds;
        _stallFrameStamp = now;
        if (_client is null || _client.Status != ClientStatus.Connected || _connectedFor <= 0) return;
        _worstFrameSinceSnapshot = Math.Max(_worstFrameSinceSnapshot, gap);
        if (gap > _longestConnectedFrame) { _longestConnectedFrame = gap; _longestConnectedFrameAt = _connectedFor; }
        if (gap >= SlowFrameLogSeconds)
        {
            int sent = _client.KeepAlivesSent;
            GD.Print($"t {_connectedFor:F1} s: a frame took {gap:F1} s (the main thread was blocked); the stall keep-alive sent {sent - _keepAlivesLogged} packets meanwhile ({(_client.KeepAliveRunning ? "running" : "off")}).");
            _keepAlivesLogged = sent;
        }
    }

    /// <summary>After <see cref="GameClient.Update"/>: a new snapshot starts a new window.</summary>
    void StallFrameUpdated()
    {
        if (_client is null || _client.LatestSnapshotTick == _stallSnapshotTick) return;
        _stallSnapshotTick = _client.LatestSnapshotTick;
        _worstFrameSinceSnapshot = 0;
    }

    /// <summary>The line for a connection that was up and is gone (see <see cref="ConnectionLost"/>).</summary>
    string ConnectionGoneText() =>
        ConnectionLost.Text(_client!.Status, _connectedFor, _client.SinceLastSnapshot.TotalSeconds, _worstFrameSinceSnapshot, _longestConnectedFrame, _longestConnectedFrameAt,
            _client.KeepAlivesSent, _client.KeepAliveRunning || _client.KeepAlivesSent > 0, ConnectionTimeoutSeconds, _client.RefusedSinceSnapshot);

    /// <summary>The client's own timeout for silence from the server (<see cref="ClientOptions.ServerTimeout"/>).</summary>
    static double ConnectionTimeoutSeconds => new ClientOptions().ServerTimeout.TotalSeconds;

    /// <summary>The freeze test aid: sleeps the main thread once.</summary>
    void StallTestFreeze()
    {
        if (_freezeDone || !float.IsFinite(TestFreezeAt) || !float.IsFinite(TestFreezeSeconds) || TestFreezeSeconds <= 0f || _connectedFor < TestFreezeAt) return;
        _freezeDone = true;
        GD.Print($"t {_connectedFor:F1} s: test aid --test-freeze-at: the main thread sleeps for {TestFreezeSeconds:F1} s (stall keep-alive {(NoStallKeepAlive ? "off" : "on")})");
        System.Threading.Thread.Sleep((int)Math.Min(TestFreezeSeconds * 1000f, 120_000f));
        GD.Print($"t {_connectedFor:F1} s: the test freeze ended");
    }
}
