using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Added content E3 (docs/added-content.md): the extras envelope, message kind 16 (extras wire 2; no protocol version of its own). The wire round-trips, a receiver steps over the
/// tags it does not know by their length, malformed or oversized messages are refused without an exception escaping, a faithful server never sends
/// the kind, an extended one delivers through <see cref="GameServer.SendExtras"/> to <see cref="GameClient.TakeExtras"/>, and a demo plays on a
/// build that does not know a recorded kind. Synthetic data only.
/// </summary>
public class ExtrasEnvelopeTests(ITestOutputHelper output)
{
    static readonly SimConfig Extended = new() { Extras = ContentExtras.Gore };

    static ExtrasUpdate Sample() => new(123456789u, [(1, [1, 2, 3]), (7, []), (255, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray())]);

    static void AssertSame(ExtrasUpdate expected, ExtrasUpdate actual)
    {
        Assert.Equal(expected.ServerTick, actual.ServerTick);
        Assert.Equal(expected.Sections.Length, actual.Sections.Length);
        for (int k = 0; k < expected.Sections.Length; k++)
        {
            Assert.Equal(expected.Sections[k].Tag, actual.Sections[k].Tag);
            Assert.Equal(expected.Sections[k].Payload, actual.Sections[k].Payload);
        }
    }

    [Fact]
    public void The_envelope_round_trips_with_the_planned_layout()
    {
        var u = Sample();
        var bytes = Protocol.Write(u);
        Assert.Equal(16, bytes[0]);   // the kind
        Assert.Equal(123456789u, BitConverter.ToUInt32(bytes, 1));
        Assert.Equal(3, bytes[5]);   // the section count
        Assert.Equal((1, 3, 1), (bytes[6], BitConverter.ToUInt16(bytes, 7), bytes[9]));   // tag, length, first payload byte
        Assert.Equal(Protocol.EnvelopeBytes(u), bytes.Length);
        Assert.Equal(6 + 3 * 3 + 3 + 0 + 100, bytes.Length);
        AssertSame(u, Assert.IsType<ExtrasUpdate>(Protocol.Read(bytes)));
        // No sections, and the largest single payload, also travel.
        AssertSame(new ExtrasUpdate(0, []), Assert.IsType<ExtrasUpdate>(Protocol.Read(Protocol.Write(new ExtrasUpdate(0, [])))));
        var largest = new ExtrasUpdate(uint.MaxValue, [(9, new byte[Protocol.MaxExtrasPayloadBytes])]);
        var max = Protocol.Write(largest);
        Assert.Equal(Protocol.MaxExtrasMessageBytes, max.Length);
        AssertSame(largest, Assert.IsType<ExtrasUpdate>(Protocol.Read(max)));
    }

    [Fact]
    public void A_receiver_steps_over_tags_it_does_not_know_by_their_length()
    {
        // Tag 1 is the only one "this feature" knows; the others hold bytes that would mislead a reader that parsed them.
        var u = new ExtrasUpdate(42, [(200, [16, 0, 0, 0, 0, 255, 1, 255, 255]), (1, [9, 8, 7]), (3, [1]), (1, [6]), (77, new byte[300])]);
        var back = Assert.IsType<ExtrasUpdate>(Protocol.Read(Protocol.Write(u)));
        Assert.Equal(new[] { new byte[] { 9, 8, 7 }, new byte[] { 6 } }, back.PayloadsOf(1).ToArray());
        Assert.Empty(back.PayloadsOf(5));
        Assert.Equal(5, back.Sections.Length);
    }

    [Fact]
    public void Truncated_padded_oversized_or_reserved_input_is_refused_without_exceptions()
    {
        var bytes = Protocol.Write(Sample());
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));   // every cut
        Assert.Null(Protocol.Read([.. bytes, 0]));   // a byte too many
        // A section length past the end of the packet.
        var lying = (byte[])bytes.Clone();
        lying[7] = 250;
        Assert.Null(Protocol.Read(lying));
        // A count that promises more sections than there are.
        var count = (byte[])bytes.Clone();
        count[5] = 4;
        Assert.Null(Protocol.Read(count));
        // The reserved tag 0.
        var reserved = (byte[])bytes.Clone();
        reserved[6] = Protocol.ReservedExtrasTag;
        Assert.Null(Protocol.Read(reserved));
        // A well-formed envelope over the 1200-byte limit (two sections) is refused when read and when written.
        var big = new ExtrasUpdate(1, [(1, new byte[700]), (2, new byte[600])]);
        Assert.Throws<ArgumentException>(() => Protocol.Write(big));
        var raw = new List<byte> { 16, 1, 0, 0, 0, 2 };
        raw.AddRange([1, 700 & 255, 700 >> 8]); raw.AddRange(new byte[700]);
        raw.AddRange([2, 600 & 255, 600 >> 8]); raw.AddRange(new byte[600]);
        Assert.Equal(6 + 3 + 700 + 3 + 600, raw.Count);
        Assert.Null(Protocol.Read(raw.ToArray()));
        // A single section longer than any message can carry.
        var longSection = new List<byte> { 16, 1, 0, 0, 0, 1, 1, 255, 255 };
        longSection.AddRange(new byte[ushort.MaxValue]);
        Assert.Null(Protocol.Read(longSection.ToArray()));
        // Invalid messages are refused when written.
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasUpdate(1, [(0, [1])])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasUpdate(1, [(1, null!)])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasUpdate(1, null!)));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasUpdate(1, Enumerable.Repeat(((byte)1, Array.Empty<byte>()), 256).ToArray())));
        // Random bytes after the kind never throw.
        var rng = new Random(31);
        for (int i = 0; i < 5000; i++)
        {
            var junk = new byte[rng.Next(1, 64)];
            rng.NextBytes(junk);
            junk[0] = 16;
            var m = Protocol.Read(junk);
            Assert.True(m is null or ExtrasUpdate);
        }
    }

    [Fact]
    public void The_kind_is_known_and_the_snapshot_budget_is_untouched()
    {
        Assert.True(ContentExtras.WireVersion >= 2);   // the envelope is an extras-only wire change: no protocol version of its own
        Assert.True(Protocol.IsKnownKind(16));
        Assert.True(Protocol.IsKnownKind(15));
        Assert.False(Protocol.IsKnownKind(0));
        Assert.False(Protocol.IsKnownKind(17));
        Assert.False(Protocol.IsKnownKind(200));
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);   // the envelope adds nothing to snapshots
        Assert.Equal(Protocol.SnapshotBudget, Protocol.MaxExtrasMessageBytes);
    }

    /// <summary>A server transport that notes the kind byte of every packet sent.</summary>
    sealed class KindTap(IServerTransport inner) : IServerTransport
    {
        public Dictionary<byte, int> Kinds { get; } = new();
        public void Send(int connection, byte[] packet)
        {
            if (packet.Length > 0) Kinds[packet[0]] = Kinds.GetValueOrDefault(packet[0]) + 1;
            inner.Send(connection, packet);
        }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
    }

    [Fact]
    public void A_faithful_server_never_sends_the_extras_kind()
    {
        KindTap? tap = null;
        var h = new Harness(LinkConditions.Perfect, 3, 2, new SimConfig(), wrapServer: t => tap = new KindTap(t));
        h.Run(TimeSpan.FromSeconds(3), Harness.Scripted);
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        // Even asked to, it refuses.
        Assert.False(h.Server.SendExtras(new ExtrasUpdate(h.Server.World.Tick, [(1, [1])])));
        h.Run(TimeSpan.FromSeconds(1), Harness.Scripted);
        output.WriteLine("kinds sent: " + string.Join(", ", tap!.Kinds.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value}")));
        Assert.True(tap.Kinds.GetValueOrDefault((byte)5) > 50);   // snapshots went out
        Assert.False(tap.Kinds.ContainsKey(16));
        Assert.Equal((0, 1), (h.Server.ExtrasMessagesSent, h.Server.ExtrasRefused));
        Assert.All(h.Clients, c => Assert.Equal(0, c.ExtrasMessages));
    }

    [Fact]
    public void A_client_running_no_added_content_drops_an_extras_message()
    {
        var h = new Harness(LinkConditions.Perfect, 4, 1, new SimConfig());
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        h.Net.Server.Send(0, Protocol.Write(new ExtrasUpdate(5, [(1, [1, 2])])));   // as a confused or hostile sender would
        h.Run(TimeSpan.FromMilliseconds(50));
        Assert.Equal((0, 1), (h.Clients[0].ExtrasMessages, h.Clients[0].ExtrasIgnored));
        Assert.Empty(h.Clients[0].TakeExtras());
    }

    [Fact]
    public void An_extended_server_delivers_sections_to_every_client_or_one_and_the_queue_stays_bounded()
    {
        var h = new Harness(LinkConditions.Perfect, 5, 2, Extended);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        uint tick = h.Server.World.Tick;
        Assert.True(h.Server.SendExtras(new ExtrasUpdate(tick, [(1, [10]), (99, [11, 12])])));
        int second = h.Clients[1].PlayerId;
        Assert.True(h.Server.SendExtras(new ExtrasUpdate(tick + 1, [(1, [20])]), toPlayer: second));
        Assert.False(h.Server.SendExtras(new ExtrasUpdate(tick, [(1, [30])]), toPlayer: 9999));   // nobody of that id
        Assert.False(h.Server.SendExtras(new ExtrasUpdate(tick, [])));   // nothing to send
        h.Run(TimeSpan.FromMilliseconds(50));
        var first = h.Clients[0].TakeExtras();
        Assert.Equal(new[] { (tick, (byte)1, 10), (tick, (byte)99, 11) }, first.Select(s => (s.ServerTick, s.Tag, (int)s.Payload[0])).ToArray());
        var both = h.Clients[1].TakeExtras();
        Assert.Equal(3, both.Count);
        Assert.Equal((tick + 1, (byte)1, (byte)20), (both[2].ServerTick, both[2].Tag, both[2].Payload[0]));
        Assert.Empty(h.Clients[0].TakeExtras());   // each once
        Assert.Equal((2, 1), (h.Server.ExtrasMessagesSent, h.Server.ExtrasRefused));   // an absent player is no refusal, just no recipient

        // A view that never takes them: the oldest go.
        for (int i = 0; i < GameClient.MaxQueuedExtras; i++) h.Server.SendExtras(new ExtrasUpdate((uint)i, [(1, [(byte)i]), (2, [])]));
        h.Run(TimeSpan.FromMilliseconds(50));
        var queued = h.Clients[0].TakeExtras();
        Assert.Equal(GameClient.MaxQueuedExtras, queued.Count);
        Assert.Equal((uint)(GameClient.MaxQueuedExtras / 2), queued[0].ServerTick);   // the newest half of 512 sections
        Assert.Equal(GameClient.MaxQueuedExtras, h.Clients[0].ExtrasOverflow);
    }

    [Fact]
    public void An_extended_server_sends_extras_only_through_SendExtras()
    {
        KindTap? tap = null;
        var h = new Harness(LinkConditions.Perfect, 6, 2, Extended, wrapServer: t => tap = new KindTap(t));
        h.Run(TimeSpan.FromSeconds(2), Harness.Scripted);
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        // The core sends nothing by itself: every kind-16 datagram is one of SendExtras's (from added content's ExtrasSend hook, if this build has one).
        Assert.Equal(h.Server.ExtrasMessagesSent * h.Clients.Count, tap!.Kinds.GetValueOrDefault((byte)16));
        output.WriteLine($"extras messages sent by hooks: {h.Server.ExtrasMessagesSent} (extras compiled: {ContentExtras.Compiled})");
    }

    [Fact]
    public void A_demo_with_a_kind_this_build_does_not_know_plays_and_keeps_the_extras()
    {
        var h = new Harness(LinkConditions.Perfect, 7, 1, Extended);
        var ms = new MemoryStream();
        var header = new DemoHeader(Demo.Version, Protocol.Version, Extended.Hash(), Harness.Fingerprint, 4242, Extended.TickRate);
        var writer = new Demo.Writer(ms, header, leaveOpen: true);
        var recorder = new GameClient(h.Config, Harness.Fingerprint, new RecordingTransport(h.Net.Connect(), h.Clock, writer), h.Clock, nonce: 4242);
        int unknown = 0, sent = 0;
        bool sessionWritten = false;
        for (int i = 0; i < 4000; i++)
        {
            h.Step(Harness.Scripted);
            recorder.Update(t => { var input = Harness.Scripted(3, t); writer.Input(h.Clock.Now.TotalSeconds, input); return input; });
            if (!sessionWritten && recorder.Status == ClientStatus.Connected)
            {
                sessionWritten = true;
                writer.Session(h.Clock.Now.TotalSeconds, recorder.PlayerId, (int)recorder.HandshakeRoundTrip.TotalMilliseconds);
            }
            if (!sessionWritten || i % 250 != 0) continue;
            if (h.Server.SendExtras(new ExtrasUpdate(h.Server.World.Tick, [(1, [(byte)sent])]))) sent++;
            writer.Packet(h.Clock.Now.TotalSeconds, [200, 1, 2, 3]);   // a kind from a later build
            writer.Packet(h.Clock.Now.TotalSeconds, [17]);
            unknown += 2;
        }
        writer.Close(h.Clock.Now.TotalSeconds);
        var taken = recorder.TakeExtras().Select(s => (int)s.Payload[0]).ToArray();
        Assert.True(sent >= 10, $"{sent} extras messages");
        Assert.Equal(Enumerable.Range(0, sent).ToArray(), taken);

        var demo = Demo.Read(ms.ToArray());
        Assert.Contains(demo.Packets, p => p.Data[0] == 16);   // the recorder kept the envelope as received
        Assert.Equal(unknown, demo.Packets.Count(p => p.Data[0] is 200 or 17));
        var play = new DemoPlayback(demo);
        var replay = new GameClient(h.Config, Harness.Fingerprint, play.Transport, play.Clock, nonce: demo.Header.Nonce);
        var replayed = new List<int>();
        while (!play.Finished)
        {
            play.Advance(1.0 / 30);
            replay.Update(play.SampleInput);
            replayed.AddRange(replay.TakeExtras().Select(s => (int)s.Payload[0]));
        }
        output.WriteLine($"{demo.Packets.Count} packets, {play.Transport.SkippedUnknown} skipped, {replayed.Count} extras sections replayed");
        Assert.Equal(ClientStatus.Connected, replay.Status);
        Assert.Equal(unknown, play.Transport.SkippedUnknown);
        Assert.Equal(taken, replayed);
    }
}
