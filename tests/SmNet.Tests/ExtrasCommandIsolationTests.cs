using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Message kind 17, added content's commands from client to server (Three-Front War TF0; no protocol bump): its layout, and that it changes nothing on a faithful or extended
/// server: such a server drops every command, valid session or not, sends nothing back and keeps its client; a client of those profiles sends none. Core folder (not
/// <c>Extras/</c>), so it also runs in <c>faithful-build.cmd</c>; tags and payloads here are made up on purpose (the core never reads them).
/// </summary>
public class ExtrasCommandIsolationTests
{
    static readonly SimConfig Faithful = new();
    static readonly SimConfig Extended = new() { Extras = ContentExtras.All };

    [Fact]
    public void The_message_is_kind_17_with_its_layout()
    {
        byte[] bytes = Protocol.Write(new ExtrasCommandMessage(0x04030201u, 7, [9, 8, 7]));
        Assert.Equal(new byte[] { 17, 1, 2, 3, 4, 7, 3, 0, 9, 8, 7 }, bytes);
        Assert.True(Protocol.IsKnownKind(17));
        var back = Assert.IsType<ExtrasCommandMessage>(Protocol.Read(bytes));
        Assert.Equal((0x04030201u, (byte)7), (back.Session, back.Tag));
        Assert.Equal([9, 8, 7], back.Payload);
        Assert.Empty(Assert.IsType<ExtrasCommandMessage>(Protocol.Read(Protocol.Write(new ExtrasCommandMessage(1, 255, [])))).Payload);
        Assert.Equal(Protocol.MaxExtrasCommandPayload, Assert.IsType<ExtrasCommandMessage>(Protocol.Read(Protocol.Write(new ExtrasCommandMessage(1, 1, new byte[Protocol.MaxExtrasCommandPayload])))).Payload.Length);
    }

    [Fact]
    public void A_malformed_command_reads_as_nothing_and_an_invalid_one_is_not_written()
    {
        Assert.Null(Protocol.Read(new byte[] { 17, 1, 2, 3, 4, 0, 0, 0 }));                       // the reserved tag 0
        Assert.Null(Protocol.Read(new byte[] { 17, 1, 2, 3, 4, 7, 3, 0, 9 }));                    // shorter than its length
        Assert.Null(Protocol.Read(new byte[] { 17, 1, 2, 3, 4, 7, 1, 0, 9, 9 }));                 // longer than its length
        Assert.Null(Protocol.Read(new byte[] { 17, 1, 2 }));                                      // cut in the header
        var tooLong = new byte[8 + Protocol.MaxExtrasCommandPayload + 1];
        tooLong[0] = 17; tooLong[5] = 7; tooLong[6] = Protocol.MaxExtrasCommandPayload + 1;
        Assert.Null(Protocol.Read(tooLong));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasCommandMessage(1, 0, [])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasCommandMessage(1, 1, new byte[Protocol.MaxExtrasCommandPayload + 1])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new ExtrasCommandMessage(1, 1, null!)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_faithful_or_extended_client_sends_no_command(bool extended)
    {
        var h = new Harness(LinkConditions.Perfect, 7, 1, extended ? Extended : Faithful);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.False(h.Clients[0].SendExtrasCommand(7, [1]));
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, h.Server.ExtrasCommandsAccepted + h.Server.ExtrasCommandsDropped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_faithful_or_extended_server_drops_a_command_and_nothing_changes(bool extended)
    {
        var config = extended ? Extended : Faithful;
        (long Packets, IReadOnlyList<PlayerState> Players, GameServer Server, Injector Wire) Run(int commands)
        {
            Injector? wire = null;
            var h = new Harness(LinkConditions.Perfect, 7, 2, config, wrapServer: inner => wire = new Injector(inner));
            for (int k = 0; k < 90; k++)
            {
                h.Run(TimeSpan.FromMilliseconds(100 / 3.0), Harness.Scripted);
                if (k >= 30 && k < 30 + commands)
                {
                    wire!.Inject(c => new ExtrasCommandMessage(c.Session, 7, [16, 0, 1, 0]));   // an honest client's session: only the profile refuses it
                    wire.Inject(c => new ExtrasCommandMessage(c.Session ^ 2, 7, [1]));          // a wrong session is not even read
                }
            }
            Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
            return (wire!.Sent, h.Server.World.Players.Values.OrderBy(p => p.PlayerId).ToArray(), h.Server, wire);
        }

        var quiet = Run(0);
        var noisy = Run(20);
        Assert.True(noisy.Wire.Injected >= 40);
        Assert.Equal(noisy.Wire.Injected / 2, noisy.Server.ExtrasCommandsDropped);   // every right-session command, each dropped
        Assert.Equal(0, noisy.Server.ExtrasCommandsAccepted);
        Assert.Equal(0, noisy.Server.Faults);
        Assert.Equal(quiet.Packets, noisy.Packets);                                  // no reply of any kind
        Assert.Equal(quiet.Players.Select(p => (p.PlayerId, p.Position, p.Yaw, p.Health)), noisy.Players.Select(p => (p.PlayerId, p.Position, p.Yaw, p.Health)));
    }

    /// <summary>Passes the server's traffic through, learns a connection's session from its input batches, and adds the test's commands to what the server receives.</summary>
    sealed class Injector(IServerTransport inner) : IServerTransport
    {
        readonly Queue<(int Connection, byte[] Packet)> _queue = new();
        (int Connection, uint Session)? _client;
        public long Sent { get; private set; }
        public int Injected { get; private set; }

        public readonly record struct Learned(uint Session);

        public void Inject(Func<Learned, Message> make)
        {
            if (_client is not { } c) return;
            _queue.Enqueue((c.Connection, Protocol.Write(make(new Learned(c.Session)))));
            Injected++;
        }

        public bool TryReceive(out int connection, out byte[] packet)
        {
            if (_queue.TryDequeue(out var next)) { (connection, packet) = next; return true; }
            if (!inner.TryReceive(out connection, out packet)) return false;
            if (Protocol.Read(packet) is InputBatch b && b.Session != 0) _client = (connection, b.Session);
            return true;
        }

        public void Send(int connection, byte[] packet) { Sent++; inner.Send(connection, packet); }
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
    }
}
