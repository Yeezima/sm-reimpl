using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M40 T0: demo recording and playback (engine-free, synthetic matches only): the file format round-trips and survives a cut-off, a playback
/// feeds packets by the host's clock, and a client replayed at another frame rate ends up where the recording client was.
/// </summary>
public class DemoTests(ITestOutputHelper output)
{
    static DemoHeader Header() => new(Demo.Version, Protocol.Version, 0x1122334455667788UL, 0xAABBCCDDEEFF0011UL, 4242, 30);

    static byte[] Record(Action<Demo.Writer> body, double end = 10)
    {
        var ms = new MemoryStream();
        using (var w = new Demo.Writer(ms, Header(), leaveOpen: true)) { body(w); w.Close(end); }
        return ms.ToArray();
    }

    [Fact]
    public void The_file_round_trips_header_packets_inputs_views_and_session()
    {
        var input = new PlayerInput(77, 0.5f, -1f, 1.25f, InputButtons.Fire | InputButtons.Sprint, -0.3f, 0, 0.01f, -0.02f, 0.1f, 0.2f, 0.3f);
        var bytes = Record(w =>
        {
            w.Packet(1.5, new byte[] { 6, 1, 2, 3 });
            w.Packet(1.6, new byte[] { 5 });
            w.Session(1.7, 3, 41);
            w.Input(2.0, input);
            w.View(2.0, 1.5f, -0.25f);
        }, end: 12.5);
        var demo = Demo.Read(bytes);
        Assert.Equal(Header(), demo.Header);
        Assert.Equal([1.5, 1.6], demo.Packets.Select(p => p.Time));
        Assert.Equal(new byte[] { 6, 1, 2, 3 }, demo.Packets[0].Data);
        Assert.Equal(new DemoSession(1.7, 3, 41), demo.Session);
        Assert.Single(demo.Inputs);
        Assert.Equal(input, demo.Inputs[0].Input);   // ViewTick is not recorded (the client fills it in)
        Assert.Equal(new DemoView(2.0, 1.5f, -0.25f), demo.Views[0]);
        Assert.False(demo.Truncated);
        Assert.Equal(1.5, demo.StartTime);
        Assert.Equal(12.5, demo.EndTime);
        Assert.Equal(11.0, demo.Duration, 9);
    }

    [Fact]
    public void A_file_cut_short_ends_where_it_was_cut_and_is_flagged()
    {
        var bytes = Record(w => { w.Packet(1.0, new byte[] { 6 }); w.Packet(2.0, new byte[40]); w.Packet(3.0, new byte[40]); });
        // Without the End record and the last packet's tail: a killed recorder.
        var cut = bytes[..(bytes.Length - 13 - 20)];
        var demo = Demo.Read(cut);
        Assert.True(demo.Truncated);
        Assert.Equal(2, demo.Packets.Count);
        Assert.Equal(2.0, demo.EndTime);
        // Without the End record only (complete records): still flagged, nothing lost.
        var noEnd = Demo.Read(bytes[..(bytes.Length - 13)]);
        Assert.True(noEnd.Truncated);
        Assert.Equal(3, noEnd.Packets.Count);
    }

    [Fact]
    public void Foreign_or_newer_files_are_refused_not_misread()
    {
        Assert.Throws<InvalidDataException>(() => Demo.Read(new byte[100]));
        Assert.Throws<InvalidDataException>(() => Demo.Read("nope"u8));
        var bytes = Record(_ => { });
        bytes[8] = 2;   // format version 2
        var ex = Assert.Throws<InvalidDataException>(() => Demo.Read(bytes));
        Assert.Contains("format 2", ex.Message);
    }

    [Fact]
    public void Unknown_record_kinds_are_skipped()
    {
        var ms = new MemoryStream();
        using (var w = new Demo.Writer(ms, Header(), leaveOpen: true)) { w.Packet(1.0, new byte[] { 6 }); w.Close(2.0); }
        var bytes = ms.ToArray().ToList();
        // Insert a made-up record (kind 99, 3 bytes) before the End record (1 + 4 + 8 bytes).
        bytes.InsertRange(bytes.Count - 13, new byte[] { 99, 3, 0, 0, 0, 7, 7, 7 });
        var demo = Demo.Read(bytes.ToArray());
        Assert.Single(demo.Packets);
        Assert.False(demo.Truncated);
        Assert.Equal(2.0, demo.EndTime);
    }

    [Fact]
    public void Playback_delivers_packets_by_the_host_clock_and_knows_when_it_has_finished()
    {
        var demo = Demo.Read(Record(w =>
        {
            w.Packet(100.0, new byte[] { 6 }); w.Packet(100.5, new byte[] { 5, 1 }); w.Packet(101.5, new byte[] { 5, 2 });
        }, end: 102.0));
        var play = new DemoPlayback(demo);
        Assert.Equal(100.0, play.Clock.Now.TotalSeconds);
        Assert.True(play.Transport.TryReceive(out var first)); Assert.Equal(6, first[0]);
        Assert.False(play.Transport.TryReceive(out _));
        play.Advance(0.4);
        Assert.False(play.Transport.TryReceive(out _));
        play.Advance(0.1);
        Assert.True(play.Transport.TryReceive(out var second)); Assert.Equal(new byte[] { 5, 1 }, second);
        play.Advance(-5); play.Advance(double.NaN);   // junk steps are ignored
        Assert.Equal(0.5, play.Elapsed, 9);
        Assert.False(play.Finished);
        for (int i = 0; i < 10; i++) play.Advance(0.1);
        Assert.True(play.Transport.TryReceive(out _));
        Assert.False(play.Finished);
        play.Advance(0.5);
        Assert.True(play.Finished);
        play.Transport.Send([1, 2, 3]);
        Assert.Equal(1, play.Transport.Sent);
    }

    [Theory]
    [InlineData(60.0, 30)]
    [InlineData(60.007, 30)]   // a recorder that closed one frame late still plays as 60 s of frames
    [InlineData(60.0, 60)]
    [InlineData(10.5, 24)]
    public void A_demo_plays_as_its_duration_in_frames(double seconds, int fps)
    {
        var demo = Demo.Read(Record(w => w.Packet(100.0, new byte[] { 6 }), end: 100.0 + seconds));
        var play = new DemoPlayback(demo);
        int frames = 0;
        while (!play.Finished && frames < 100000) { play.Advance(1.0 / fps); frames++; }
        Assert.InRange(frames, Math.Round(seconds * fps), Math.Round(seconds * fps));
    }

    [Fact]
    public void The_recorded_Welcome_is_rewritten_to_the_recorded_round_trip_and_nothing_else_is_touched()
    {
        var welcome = new Welcome(3, 500, 30, 4242, EchoSentMs: 123456, Session: 9);
        var wire = Protocol.Write(welcome);
        Assert.Equal(DemoTransport.WelcomeKindByte, wire[0]);
        var demo = Demo.Read(Record(w => { w.Packet(50.0, wire); w.Session(50.0, 3, 77); }, end: 51));
        var play = new DemoPlayback(demo);
        play.Advance(0.0);
        Assert.True(play.Transport.TryReceive(out var packet));
        var rewritten = Assert.IsType<Welcome>(Protocol.Read(packet));
        Assert.Equal(welcome with { EchoSentMs = (uint)(50000 - 77) }, rewritten);
    }

    [Fact]
    public void Missing_inputs_are_answered_with_the_nearest_earlier_one_or_idle()
    {
        var demo = Demo.Read(Record(w =>
        {
            w.Packet(1.0, new byte[] { 6 });
            w.Input(1.0, new PlayerInput(10, 1, 0, 0.5f, InputButtons.Sprint));
            w.Input(1.1, new PlayerInput(12, 0, 1, 0.6f, InputButtons.Fire));
        }));
        var play = new DemoPlayback(demo);
        Assert.Equal(InputButtons.Sprint, play.SampleInput(10).Buttons);
        Assert.Equal(0, play.InputMisses);
        var between = play.SampleInput(11);
        Assert.Equal((11u, InputButtons.Sprint), (between.Tick, between.Buttons));
        Assert.Equal(InputButtons.Fire, play.SampleInput(99).Buttons);
        var before = play.SampleInput(3);
        Assert.Equal((3u, InputButtons.None), (before.Tick, before.Buttons));
        Assert.Equal(3, play.InputMisses);
    }

    [Fact]
    public void The_recorded_view_is_interpolated_and_turns_the_short_way_across_pi()
    {
        var demo = Demo.Read(Record(w =>
        {
            w.Packet(10.0, new byte[] { 6 });
            w.View(10.0, 3.0f, 0.0f); w.View(11.0, -3.0f, 0.5f); w.View(12.0, -2.0f, 0.5f);
        }, end: 12));
        var play = new DemoPlayback(demo);
        var (yaw, pitch) = play.ViewAt(0.5)!.Value;
        Assert.Equal(3.0f + (MathF.Tau - 6.0f) * 0.5f, yaw, 4);   // 3.0 to -3.0 is +0.283 across pi, not -6
        Assert.Equal(0.25f, pitch, 4);
        Assert.Equal(-2.5f, play.ViewAt(1.5)!.Value.Yaw, 4);
        Assert.Equal(-2.0f, play.ViewAt(5.0)!.Value.Yaw);          // after the last: held
        Assert.Equal(3.0f, play.ViewAt(0.0)!.Value.Yaw);          // going back works too
        Assert.Null(new DemoPlayback(Demo.Read(Record(w => w.Packet(1, new byte[] { 6 })))).ViewAt(0));
    }

    // ---- a whole session ----

    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(15), 0.01);

    [Fact]
    public void A_session_recorded_at_millisecond_steps_replays_at_a_thirtieth_of_a_second_to_the_same_place()
    {
        var h = new Harness(Link, seed: 21, clients: 2);
        var ms = new MemoryStream();
        var writer = new Demo.Writer(ms, Header(), leaveOpen: true);
        var recorder = new GameClient(h.Config, Harness.Fingerprint, new RecordingTransport(h.Net.Connect(), h.Clock, writer), h.Clock, nonce: 4242);
        bool sessionWritten = false;
        // 20 s at 1 ms steps; the recorder and the other clients run scripted input, the recorder logs what it sampled and its view.
        for (int i = 0; i < 20_000; i++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            for (int c = 0; c < h.Clients.Count; c++) { int index = c; h.Clients[c].Update(t => Harness.Scripted(index, t)); }
            recorder.Update(t =>
            {
                var input = Harness.Scripted(5, t);
                writer.Input(h.Clock.Now.TotalSeconds, input);
                return input;
            });
            if (!sessionWritten && recorder.Status == ClientStatus.Connected)
            {
                sessionWritten = true;
                writer.Session(h.Clock.Now.TotalSeconds, recorder.PlayerId, (int)recorder.HandshakeRoundTrip.TotalMilliseconds);
            }
            if (i % 16 == 0 && sessionWritten) writer.View(h.Clock.Now.TotalSeconds, 0.1f * (i / 16), 0f);
        }
        writer.Close(h.Clock.Now.TotalSeconds);
        var demo = Demo.Read(ms.ToArray());
        Assert.False(demo.Truncated);
        Assert.NotNull(demo.Session);
        Assert.True(demo.Packets.Count > 300, $"{demo.Packets.Count} packets");
        Assert.Equal(ClientStatus.Connected, recorder.Status);

        // Replay: a fresh client on the demo's transport and clock, one update per 1/30 s (a frame of Movie Maker footage).
        var play = new DemoPlayback(demo);
        var replay = new GameClient(h.Config, Harness.Fingerprint, play.Transport, play.Clock, nonce: demo.Header.Nonce);
        int frames = 0;
        while (!play.Finished)
        {
            play.Advance(1.0 / 30);
            replay.Update(play.SampleInput);
            frames++;
        }
        Assert.Equal(ClientStatus.Connected, replay.Status);
        Assert.Equal(recorder.PlayerId, replay.PlayerId);
        Assert.Equal(recorder.HandshakeRoundTrip, replay.HandshakeRoundTrip);
        // The recorded duration in frames, to the frame (from the first packet to the end: a little under 20 s).
        Assert.InRange(frames, demo.Duration * 30 - 0.01, demo.Duration * 30 + 1.01);
        // Same tick, same place: the replay's prediction followed the recorded inputs through the same snapshots.
        int maxTickLag = Math.Abs((int)recorder.Tick - (int)replay.Tick);
        float distance = Vector3.Distance(recorder.Predicted.Position, replay.Predicted.Position);
        output.WriteLine($"frames {frames}; ticks recorded {recorder.Tick} replayed {replay.Tick}; own position differs by {distance:F3} m; input misses {play.InputMisses}; packets {demo.Packets.Count}");
        Assert.True(maxTickLag <= 3, $"ticks differ by {maxTickLag}");
        Assert.True(distance < 1.5f, $"the replayed player is {distance:F2} m from the recorded one");
        // Other players are shown at the same server time (within a snapshot).
        var (renderRecorded, _) = recorder.RemotePlayers();
        var (renderReplayed, others) = replay.RemotePlayers();
        output.WriteLine($"render tick recorded {renderRecorded:F2} replayed {renderReplayed:F2}; {others.Count} others");
        Assert.InRange(renderReplayed - renderRecorded, -2.0, 2.0);
        Assert.True(others.Count >= 1);
    }
}
