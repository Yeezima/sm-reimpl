using SmSim;

namespace SmNet;

/// <summary>
/// fix/stall-keepalive (ours; no protocol change): keeps the server from timing this player out while the main thread is blocked.
///
/// The server drops a player it has not heard from for 5 s (<c>ServerOptions.PeerTimeout</c>), and the client sends only from <see cref="Update"/>, so any long freeze
/// of the main thread (a first draw that compiles shaders on a slow GPU, a driver stall, a debugger) dropped the player from any server. While no <see cref="Update"/>
/// has begun for <see cref="ClientOptions.KeepAliveAfter"/> (1 s), a background thread sends the newest <see cref="InputBatch"/> again every
/// <see cref="ClientOptions.KeepAliveInterval"/> (0.5 s), until the stall passes the bound <see cref="ClientOptions.KeepAliveBound"/> (30 s).
///
/// Why the repeated batch is harmless on a server of this protocol (<c>GameServer.OnInputs</c>): it sets <c>LastHeard</c> first, which is all the keep-alive is for; its
/// snapshot acknowledgement is the one already sent (no ping sample for a repeated or older one, and the acknowledgement only moves forward); every input in it has a tick the
/// server has long simulated (the stall is longer than the client's lead), and an input at or before the server's tick is skipped, so none is applied twice. The inputs are
/// also made neutral here (no movement, no buttons, the same heading), so a batch whose ticks were still ahead of the server would stop the frozen player, as the server does
/// by itself after 500 ms of silence (<c>NeutralInputAfter</c>), and the idle kick sees no play in it (<c>GameServer.IsRealInput</c>). A spectator's batch is its camera and
/// is repeated as it is. The simulation and the prediction are not touched; the thread only reads the last batch and sends.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>The newest batch sent by <see cref="StepTick"/> (an immutable record, replaced as a whole; read by the keep-alive thread).</summary>
    volatile InputBatch? _lastBatch;
    long _updatedAtTicks;
    long _keepAliveSentTicks = long.MinValue;
    int _keepAlivesSent;
    volatile bool _keepAliveStop;
    Thread? _keepAliveThread;

    /// <summary>How many keep-alive batches were sent so far (diagnostics; the log of a client that survived a freeze).</summary>
    public int KeepAlivesSent => Volatile.Read(ref _keepAlivesSent);

    /// <summary>Whether the keep-alive thread runs (it ends with the connection).</summary>
    public bool KeepAliveRunning => _keepAliveThread is { IsAlive: true };

    /// <summary>How long ago the newest snapshot arrived (diagnostics: the line that says a connection is gone).</summary>
    public TimeSpan SinceLastSnapshot => _clock.Now - _latestSnapshotAt;

    /// <summary>fix/dead-melee-direction: snapshots that arrived and were refused (unreadable or implausible) since the last accepted one, and in the whole connection.</summary>
    public int RefusedSinceSnapshot { get; private set; }
    public int RefusedSnapshots { get; private set; }

    void RefuseSnapshot() { RefusedSinceSnapshot++; RefusedSnapshots++; }

    void MarkUpdated() => Interlocked.Exchange(ref _updatedAtTicks, _clock.Now.Ticks);

    void StartKeepAlive()
    {
        if (!_options.StallKeepAlive || !_transport.ThreadSafeSend || _keepAliveThread is not null) return;
        _keepAliveStop = false;
        _keepAliveThread = new Thread(KeepAliveLoop) { IsBackground = true, Name = "StallKeepAlive" };
        _keepAliveThread.Start();
    }

    void StopKeepAlive() => _keepAliveStop = true;

    void KeepAliveLoop()
    {
        // Ends with the connection (leave, timeout, kick) or on Leave; a transport failure ends it too (never an unhandled exception on this thread).
        try
        {
            while (!_keepAliveStop && Status == ClientStatus.Connected)
            {
                Thread.Sleep(100);
                KeepAliveStep(_clock.Now);
            }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// One look at the stall (the keep-alive thread calls it every 100 ms with the client's clock; tests call it with theirs): sends a keep-alive when the main thread has not
    /// begun an <see cref="Update"/> for <see cref="ClientOptions.KeepAliveAfter"/> and not for longer than <see cref="ClientOptions.KeepAliveBound"/>, and the last one
    /// was <see cref="ClientOptions.KeepAliveInterval"/> ago or more. Returns whether one was sent. Never sends on a client that is not connected or has left.
    /// </summary>
    public bool KeepAliveStep(TimeSpan now)
    {
        if (_keepAliveStop || Status != ClientStatus.Connected || _lastBatch is not { } batch) return false;
        var stalled = now - TimeSpan.FromTicks(Interlocked.Read(ref _updatedAtTicks));
        if (stalled < _options.KeepAliveAfter || stalled > _options.KeepAliveBound) return false;
        long last = Interlocked.Read(ref _keepAliveSentTicks);
        if (last != long.MinValue && now - TimeSpan.FromTicks(last) < _options.KeepAliveInterval) return false;
        Interlocked.Exchange(ref _keepAliveSentTicks, now.Ticks);
        _transport.Send(Protocol.Write(_options.Observer ? batch : Neutral(batch)));
        Interlocked.Increment(ref _keepAlivesSent);
        return true;
    }

    /// <summary>The batch with every input made neutral: no movement, no buttons, no melee target; the tick, heading, pitch and view fields as they were.</summary>
    static InputBatch Neutral(InputBatch batch) =>
        batch with { Inputs = batch.Inputs.Select(i => i with { MoveX = 0, MoveY = 0, Buttons = InputButtons.None, MeleeTarget = 0 }).ToArray() };
}
