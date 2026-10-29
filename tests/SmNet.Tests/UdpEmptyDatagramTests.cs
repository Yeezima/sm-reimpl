using System.Net;
using System.Net.Sockets;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Server hardening M1: a zero-length UDP datagram at the head of the receive queue must not block the packets behind it.
/// <c>Socket.Available</c> is 0 for such a datagram, so a loop gated on it never read the queue again (one empty packet
/// deafened the server). Real sockets on 127.0.0.1, ephemeral ports.
/// </summary>
public class UdpEmptyDatagramTests
{
    /// <summary>A socket with Linux's receive semantics: <c>Available</c> (FIONREAD) is the size of the first queued datagram only.</summary>
    sealed class LinuxLikeSocket(params byte[][] queued) : IDatagramSocket
    {
        readonly Queue<byte[]> _queue = new(queued);
        public int Available => _queue.TryPeek(out var head) ? head.Length : 0;
        public bool Poll(int microseconds, SelectMode mode) => mode == SelectMode.SelectRead && _queue.Count > 0;
        public int ReceiveFrom(byte[] buffer, ref EndPoint remote)
        {
            if (!_queue.TryDequeue(out var d)) throw new SocketException((int)SocketError.WouldBlock);
            d.CopyTo(buffer, 0);
            return d.Length;
        }
    }

    [Fact]
    public void Receive_loop_reads_past_an_empty_datagram_with_linux_semantics()
    {
        var socket = new LinuxLikeSocket([], [], [5, 6, 7]);
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        Assert.Equal(3, DatagramReceive.Next(socket, buffer, ref from, UdpServerTransport.MaxPacket));
        Assert.Equal(new byte[] { 5, 6, 7 }, buffer[..3]);
        Assert.Equal(-1, DatagramReceive.Next(socket, buffer, ref from, UdpServerTransport.MaxPacket));
    }

    /// <summary>A socket whose first reads fail as a reset (an ICMP port unreachable) and a datagram larger than the buffer (Windows).</summary>
    sealed class FailingReadsSocket(params object[] reads) : IDatagramSocket
    {
        readonly Queue<object> _reads = new(reads);
        public int Available => _reads.Count;
        public bool Poll(int microseconds, SelectMode mode) => _reads.Count > 0;
        public int ReceiveFrom(byte[] buffer, ref EndPoint remote)
        {
            var next = _reads.Dequeue();
            if (next is SocketError error) throw new SocketException((int)error);
            var d = (byte[])next;
            d.CopyTo(buffer, 0);
            return d.Length;
        }
    }

    [Fact]
    public void Receive_loop_skips_resets_and_too_large_datagrams()
    {
        var socket = new FailingReadsSocket(SocketError.ConnectionReset, SocketError.MessageSize, new byte[] { 4, 2 });
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        Assert.Equal(2, DatagramReceive.Next(socket, buffer, ref from, UdpServerTransport.MaxPacket));
        Assert.Equal(-1, DatagramReceive.Next(socket, buffer, ref from, UdpServerTransport.MaxPacket));
        // A would-block (nothing there after all) ends the call at once.
        Assert.Equal(-1, DatagramReceive.Next(new FailingReadsSocket(SocketError.WouldBlock, new byte[] { 1 }), buffer, ref from, UdpServerTransport.MaxPacket));
    }

    [Fact]
    public void Receive_loop_skips_oversized_datagrams()
    {
        var socket = new LinuxLikeSocket(new byte[UdpServerTransport.MaxPacket + 1], [1]);
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        Assert.Equal(1, DatagramReceive.Next(socket, buffer, ref from, UdpServerTransport.MaxPacket));
    }

    [Fact]
    public void Empty_datagram_does_not_deafen_the_server()
    {
        var clock = new ManualClock();
        var config = new SimConfig();
        using var transport = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new GameServer(config, 7, transport, clock);
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sender.ReceiveTimeout = 2000;
        sender.SendTo([], transport.LocalEndPoint);
        sender.SendTo(Protocol.Write(new Hello(Protocol.Version, 7, config.Hash(), 42)), transport.LocalEndPoint);

        // The first-contact Hello behind the empty datagram must be answered with a Challenge.
        var buffer = new byte[2048];
        Message? reply = null;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (reply is null && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            server.Update();
            if (sender.Poll(1000, SelectMode.SelectRead))
            {
                int n = sender.Receive(buffer);
                reply = Protocol.Read(buffer[..n]);
            }
        }
        var challenge = Assert.IsType<Challenge>(reply);
        Assert.Equal(42u, challenge.Nonce);
    }

    [Fact]
    public void Empty_datagram_does_not_deafen_the_client()
    {
        using var serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new UdpClientTransport((IPEndPoint)serverSocket.LocalEndPoint!);
        client.Send([1, 2, 3]);   // tells the test socket where the client is
        var buffer = new byte[64];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        serverSocket.ReceiveTimeout = 2000;
        serverSocket.ReceiveFrom(buffer, ref from);

        serverSocket.SendTo([], from);
        serverSocket.SendTo([9, 8, 7], from);
        byte[]? got = null;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (got is null && DateTime.UtcNow < until)
            if (client.TryReceive(out var packet)) got = packet; else Thread.Sleep(1);
        Assert.Equal(new byte[] { 9, 8, 7 }, got);
    }

    [Fact]
    public void Many_empty_datagrams_are_drained_without_losing_the_real_one()
    {
        using var transport = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        for (int i = 0; i < 300; i++) sender.SendTo([], transport.LocalEndPoint);
        sender.SendTo([0xAB], transport.LocalEndPoint);
        byte[]? got = null;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (got is null && DateTime.UtcNow < until)
            if (transport.TryReceive(out _, out var packet)) got = packet; else Thread.Sleep(1);
        Assert.Equal(new byte[] { 0xAB }, got);
    }
}
