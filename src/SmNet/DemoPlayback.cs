using SmSim;

namespace SmNet;

/// <summary>A clock a host sets by hand (a demo's, driven by the engine's frame time).</summary>
public sealed class DemoClock : IClock
{
    public TimeSpan Now { get; private set; }
    public void Set(TimeSpan now) => Now = now;
}

/// <summary>
/// Wraps the live transport of a client that records: every received packet goes into the demo with the clock reading it was
/// received at (the same clock the client uses), then on to the client unchanged. A write error (disk full ...) never reaches the client:
/// the packet is still returned, recording stops (see <see cref="Demo.Writer.Failed"/>), and nothing is allocated for it afterwards.
/// </summary>
public sealed class RecordingTransport(IClientTransport inner, IClock clock, Demo.Writer writer) : IClientTransport
{
    public void Send(byte[] packet) => inner.Send(packet);

    /// <summary>The clock reading (seconds) the first packet arrived at, or null before it.</summary>
    public double? FirstPacketAt { get; private set; }

    public bool TryReceive(out byte[] packet)
    {
        if (!inner.TryReceive(out packet)) return false;
        double now = clock.Now.TotalSeconds;
        FirstPacketAt ??= now;
        if (writer.IsOpen)
        {
            try { writer.Packet(now, packet); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { /* the writer reports it through Failed; the packet goes on */ }
        }
        return true;
    }
}

/// <summary>
/// Feeds a demo's recorded packets to a client at their recorded times on the demo clock and swallows what the client sends. The
/// recorded Welcome is rewritten so that the client measures the round trip it measured when recording (its Echo names a send time
/// that never happened in this run), which fixes the tick the client starts predicting from.
/// </summary>
public sealed class DemoTransport(Demo demo, DemoClock clock) : IClientTransport
{
    /// <summary>The first byte of a Welcome on the wire (<c>Protocol.Kind.Welcome</c>, private there; a test checks it).</summary>
    public const byte WelcomeKindByte = 2;
    int _next;
    public long Sent { get; private set; }
    /// <summary>Recorded packets handed out or skipped so far.</summary>
    public int Delivered => _next;
    /// <summary>
    /// Recorded packets of a message kind this build does not know (<see cref="Protocol.IsKnownKind"/>), skipped (added content's envelope): a demo
    /// recorded by a build with more kinds plays on a build without them. The recorder (<see cref="RecordingTransport"/>) stores every datagram as
    /// received, without reading it, so a demo keeps the kinds the recording build had.
    /// </summary>
    public int SkippedUnknown { get; private set; }

    public void Send(byte[] packet) => Sent++;

    public bool TryReceive(out byte[] packet)
    {
        while (_next < demo.Packets.Count && demo.Packets[_next].Time <= clock.Now.TotalSeconds + 1e-9)
        {
            packet = demo.Packets[_next++].Data;
            if (packet.Length > 0 && !Protocol.IsKnownKind(packet[0])) { SkippedUnknown++; continue; }
            if (packet.Length > 0 && packet[0] == WelcomeKindByte && demo.Session is { } session && Protocol.Read(packet) is Welcome w)
                packet = Protocol.Write(w with { EchoSentMs = unchecked((uint)((long)clock.Now.TotalMilliseconds - session.RoundTripMs)) });
            return true;
        }
        packet = [];
        return false;
    }
}

/// <summary>
/// Plays a <see cref="Demo"/> back (M40 T0): <see cref="Advance"/> moves the demo clock by the host's frame time, so the client's
/// network clock follows the engine's time (Movie Maker footage at any size is exactly real time); <see cref="SampleInput"/> answers the
/// client's input sampling with what was sampled for that tick when recording; <see cref="ViewAt"/> gives the recorded view.
/// </summary>
public sealed class DemoPlayback
{
    readonly Dictionary<uint, PlayerInput> _inputs = new();
    readonly uint[] _ticks;   // the recorded ticks, ascending, for the nearest-earlier lookup

    public DemoPlayback(Demo demo)
    {
        Demo = demo;
        Clock = new DemoClock();
        Transport = new DemoTransport(demo, Clock);
        foreach (var i in demo.Inputs) _inputs[i.Input.Tick] = i.Input;   // a repeated tick keeps the latest
        _ticks = _inputs.Keys.ToArray();
        Array.Sort(_ticks);   // O(n log n) however the ticks were ordered in the file
        Clock.Set(TimeSpan.FromSeconds(demo.StartTime));
    }

    public Demo Demo { get; }
    public DemoClock Clock { get; }
    public DemoTransport Transport { get; }
    /// <summary>Seconds of demo time played.</summary>
    public double Elapsed { get; private set; }
    /// <summary>Ticks asked for that the recording has no input for (answered with the nearest earlier one, or idle).</summary>
    public int InputMisses { get; private set; }
    double _lastStep;
    /// <summary>
    /// The demo's duration has been played: the last frame is the one whose time span (half a step either side of <see cref="Elapsed"/>)
    /// reaches the end, so a 60.007 s demo plays as exactly 1800 frames at 30 fps, not 1801.
    /// </summary>
    public bool Finished => !double.IsFinite(Demo.Duration) || Elapsed + _lastStep * 0.5 >= Demo.Duration - 1e-6;

    /// <summary>Plays <paramref name="seconds"/> more (a negative or non-finite step counts as 0).</summary>
    public void Advance(double seconds)
    {
        if (double.IsFinite(seconds) && seconds > 0) { Elapsed += seconds; _lastStep = seconds; }
        Clock.Set(TimeSpan.FromSeconds(Demo.StartTime + Elapsed));
    }

    /// <summary>The input recorded for <paramref name="tick"/>; else the nearest earlier one with the tick replaced; else an idle input.</summary>
    public PlayerInput SampleInput(uint tick)
    {
        if (_inputs.TryGetValue(tick, out var exact)) return exact;
        InputMisses++;
        int lo = 0, hi = _ticks.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_ticks[mid] < tick) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (found < 0) return new PlayerInput(tick, 0f, 0f, 0f, InputButtons.None);
        return _inputs[_ticks[found]] with { Tick = tick };
    }

    int _viewIndex;

    /// <summary>The recorded view at demo time <paramref name="elapsed"/> (interpolated between frames; yaw by the shorter way); null for a demo without views.</summary>
    public (float Yaw, float Pitch)? ViewAt(double elapsed)
    {
        var views = Demo.Views;
        if (views.Count == 0) return null;
        double t = Demo.StartTime + elapsed;
        if (_viewIndex >= views.Count || (_viewIndex > 0 && views[_viewIndex].Time > t)) _viewIndex = 0;
        while (_viewIndex + 1 < views.Count && views[_viewIndex + 1].Time <= t) _viewIndex++;
        var a = views[_viewIndex];
        if (t <= a.Time || _viewIndex + 1 >= views.Count) return (a.Yaw, a.Pitch);
        var b = views[_viewIndex + 1];
        float f = (float)Math.Clamp((t - a.Time) / Math.Max(b.Time - a.Time, 1e-9), 0.0, 1.0);
        return (a.Yaw + ViewSmoothing.WrapPi(b.Yaw - a.Yaw) * f, a.Pitch + (b.Pitch - a.Pitch) * f);
    }
}
