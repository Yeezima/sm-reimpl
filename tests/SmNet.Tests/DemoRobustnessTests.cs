using System.Diagnostics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M40 review S2, S3: a corrupt or hostile demo file is refused or has its bad records dropped, and a recorder that cannot write never
/// breaks the live client. Plus the format guard against new <see cref="PlayerInput"/> fields. Synthetic data only.
/// </summary>
public class DemoRobustnessTests(ITestOutputHelper output)
{
    static DemoHeader Header() => new(Demo.Version, Protocol.Version, 1, 2, 3, 30);

    static Demo Make(Action<Demo.Writer> body, double? close = null)
    {
        var ms = new MemoryStream();
        var w = new Demo.Writer(ms, Header(), leaveOpen: true);
        body(w);
        w.Close(close ?? w.LastTime);
        return Demo.Read(ms.ToArray());
    }

    // ---- the format guard ----

    [Fact]
    public void A_new_PlayerInput_field_needs_a_demo_format_bump()
    {
        var parameters = typeof(PlayerInput).GetConstructors()
            .Single(c => c.GetParameters().Length > 1).GetParameters().Select(p => p.Name).ToArray();
        // If this fails, PlayerInput changed: store the new field in Demo.Writer.Input / Demo.Read (kind 2 payload), add it to Demo.InputFields and
        // raise Demo.Version, so older demos are refused and not misread. (ViewTick, MeleeTarget and ViewFraction are not stored: the client recomputes them.)
        Assert.Equal(Demo.InputFields, parameters);
        Assert.Equal(14, parameters.Length);
        Assert.Equal(1, Demo.Version);
    }

    // ---- S2: bad times and sizes ----

    [Fact]
    public void Records_with_a_non_finite_negative_huge_or_backwards_time_are_dropped()
    {
        var demo = Make(w =>
        {
            w.Packet(1.0, new byte[] { 6 });
            w.Packet(double.NaN, new byte[] { 5 });
            w.Packet(double.PositiveInfinity, new byte[] { 5 });
            w.Packet(double.NegativeInfinity, new byte[] { 5 });
            w.Packet(-3.0, new byte[] { 5 });
            w.Packet(Demo.MaxTimeSeconds * 10, new byte[] { 5 });
            w.Packet(0.5, new byte[] { 5 });             // earlier than the previous accepted record
            w.Input(double.NaN, new PlayerInput(1, 0, 0, 0, InputButtons.None));
            w.View(double.PositiveInfinity, 1f, 1f);
            w.Session(-1.0, 1, 10);
            w.Packet(2.0, new byte[] { 5, 1 });
            w.Input(2.0, new PlayerInput(2, 0, 0, 0, InputButtons.None));   // equal times are fine
        }, close: 2.0);
        Assert.Equal([1.0, 2.0], demo.Packets.Select(p => p.Time));
        Assert.Single(demo.Inputs);
        Assert.Empty(demo.Views);
        Assert.Null(demo.Session);
        Assert.Equal(10, demo.DroppedRecords);   // the nine above and the End record, which carries the writer's largest time (1e8: out of range)
        Assert.True(demo.Truncated);
        Assert.True(double.IsFinite(demo.Duration));
        Assert.Equal(1.0, demo.Duration, 9);
        var play = new DemoPlayback(demo);
        int frames = 0;
        while (!play.Finished && frames++ < 1000) play.Advance(1.0 / 30);
        Assert.True(play.Finished);
        Assert.InRange(frames, 29, 31);
    }

    [Fact]
    public void An_end_record_with_a_bad_time_is_dropped_and_the_file_counts_as_cut_off()
    {
        var demo = Make(w => w.Packet(1.0, new byte[] { 6 }), close: double.NaN);
        Assert.True(demo.Truncated);
        Assert.Equal(1, demo.DroppedRecords);
        Assert.True(double.IsFinite(demo.Duration));
    }

    [Fact]
    public void Views_with_non_finite_angles_and_sessions_with_absurd_round_trips_are_dropped()
    {
        var demo = Make(w =>
        {
            w.Packet(1.0, new byte[] { 6 });
            w.View(1.1, float.NaN, 0f);
            w.View(1.2, 0f, float.PositiveInfinity);
            w.View(1.3, 0.5f, 0.1f);
            w.Session(1.4, 2, -5);
            w.Session(1.5, 2, 4_000_000);
            w.Session(1.6, 2, 25);
        });
        Assert.Single(demo.Views);
        Assert.Equal(new DemoSession(1.6, 2, 25), demo.Session);
        Assert.Equal(4, demo.DroppedRecords);
        Assert.False(float.IsNaN(new DemoPlayback(demo).ViewAt(0.1)!.Value.Yaw));
    }

    [Fact]
    public void An_oversized_packet_record_is_dropped()
    {
        var demo = Make(w => { w.Packet(1.0, new byte[] { 6 }); w.Packet(1.1, new byte[Demo.MaxPacketBytes + 1]); w.Packet(1.2, new byte[Demo.MaxPacketBytes]); });
        Assert.Equal(2, demo.Packets.Count);
        Assert.Equal(1, demo.DroppedRecords);
    }

    /// <summary>A stream that claims to be huge; reading it would run out of memory, so it must be refused first.</summary>
    sealed class HugeStream(long length) : Stream
    {
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return 0; }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused_before_it_is_read()
    {
        var huge = new HugeStream(3L * 1024 * 1024 * 1024);
        var ex = Assert.Throws<InvalidDataException>(() => Demo.Read(huge));
        Assert.Contains("limit", ex.Message);
        Assert.Equal(0, huge.Reads);
        Assert.Equal(2L * 1024 * 1024 * 1024 - 1, Demo.MaxFileBytes);
    }

    [Fact]
    public void Out_of_order_ticks_play_back_in_n_log_n_not_n_squared()
    {
        const int N = 150_000;
        var ms = new MemoryStream();
        using (var w = new Demo.Writer(ms, Header(), leaveOpen: true))
        {
            w.Packet(1.0, new byte[] { 6 });
            for (uint i = 0; i < N; i++) w.Input(1.0 + i * 1e-5, new PlayerInput((uint)(N - i), 0, 1, 0, InputButtons.None));   // ticks descending
        }
        var demo = Demo.Read(ms.ToArray());
        var watch = Stopwatch.StartNew();
        var play = new DemoPlayback(demo);
        Assert.Equal(1f, play.SampleInput(777).MoveY);
        Assert.Equal(InputButtons.None, play.SampleInput(N + 50).Buttons);   // after the last tick: the nearest earlier
        Assert.Equal(1, play.InputMisses);
        output.WriteLine($"{N} descending ticks indexed and answered in {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

    // ---- S3: a recorder that cannot write ----

    /// <summary>A stream that fails with the given exception once <paramref name="allowed"/> bytes were written.</summary>
    sealed class FailingStream(int allowed, Exception failure, bool failFlush = false) : Stream
    {
        int _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { if (failFlush) throw failure; }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_written + count > allowed) throw failure;
            _written += count;
        }
    }

    sealed class ListTransport(params byte[][] packets) : IClientTransport
    {
        readonly Queue<byte[]> _queue = new(packets);
        public void Send(byte[] packet) { }
        public bool TryReceive(out byte[] packet) { if (_queue.Count > 0) { packet = _queue.Dequeue(); return true; } packet = []; return false; }
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_recorder_that_cannot_write_still_hands_the_packet_to_the_client_and_stops_once(Type failure)
    {
        var clock = new ManualClock();
        var packets = Enumerable.Range(0, 10).Select(i => new byte[] { 5, (byte)i, 9, 9, 9 }).ToArray();
        // Room for the header and two packet records, then the disk "fills up".
        var writer = new Demo.Writer(new FailingStream(30 + 3 * 21, (Exception)Activator.CreateInstance(failure, "disk full")!), Header());
        var failures = new List<string>();
        writer.Failed += failures.Add;
        var recorder = new RecordingTransport(new ListTransport(packets), clock, writer);
        var received = new List<byte[]>();
        while (recorder.TryReceive(out var p)) { received.Add(p); clock.Advance(TimeSpan.FromMilliseconds(10)); }
        Assert.Equal(10, received.Count);
        Assert.Equal(packets.Select(p => p[1]), received.Select(p => p[1]));
        Assert.Single(failures);
        Assert.Contains("disk full", failures[0]);
        Assert.False(writer.IsOpen);
        Assert.NotNull(writer.Error);
        // Nothing is written, nothing allocated, and nothing throws once recording has stopped.
        var data = new byte[200];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { writer.Packet(5.0, data); writer.Input(5.0, default); writer.View(5.0, 0f, 0f); writer.Session(5.0, 1, 1); writer.Flush(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        writer.Dispose();   // does not throw either
    }

    [Fact]
    public void A_recorder_that_fails_on_close_reports_it_and_does_not_throw()
    {
        var failures = new List<string>();
        var writer = new Demo.Writer(new FailingStream(1 << 20, new IOException("flush failed"), failFlush: true), Header());
        writer.Failed += failures.Add;
        writer.Packet(1.0, new byte[] { 6 });
        writer.Close(2.0);
        Assert.Single(failures);
        writer.Close(3.0);
        Assert.Single(failures);
    }
}
