using System.Net;
using System.Net.Sockets;
using SmNet;

namespace SmNet.Tests;

/// <summary>Server hardening: a second server on a port already in use fails to start (127.0.0.1, ephemeral port; never every interface).</summary>
public class UdpExclusiveBindTests
{
    [Fact]
    public void Second_server_on_the_same_port_fails_to_start()
    {
        using var first = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, 0));
        var taken = first.LocalEndPoint;
        var e = Assert.Throws<SocketException>(() => new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, taken.Port)));
        Assert.True(e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied, $"error {e.SocketErrorCode}");

        // The first server still receives.
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));   // an unbound sender would bind every interface on its first send
        sender.SendTo([0x42], taken);
        byte[]? got = null;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (got is null && DateTime.UtcNow < until)
            if (first.TryReceive(out _, out var packet)) got = packet; else Thread.Sleep(1);
        Assert.Equal(new byte[] { 0x42 }, got);
    }
}
