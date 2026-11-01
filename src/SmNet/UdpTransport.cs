using System.Net;
using System.Net.Sockets;

namespace SmNet;

/// <summary>
/// UDP server endpoint. Each remote address gets a connection id on its first packet; ids are never reused. The table of
/// unauthenticated addresses is bounded: when it is full, the one heard from least recently is forgotten (O(1)), so garbage
/// from many sources cannot grow it. Addresses the game server has <see cref="Pin"/>ned (authenticated players) are never
/// evicted; the game server releases them through <see cref="Disconnect"/> when players leave or time out.
/// </summary>
public sealed class UdpServerTransport : IServerTransport, IDisposable
{
    public const int MaxPacket = 1200;
    readonly Socket _socket;
    readonly SocketDatagrams _datagrams;
    readonly int _maxUnpinned;
    readonly Dictionary<EndPoint, int> _ids = new();
    readonly Dictionary<int, Entry> _byId = new();
    readonly LinkedList<int> _unpinnedByAge = new();   // least recently heard first
    readonly byte[] _buffer = new byte[2048];
    int _nextId;

    sealed class Entry(EndPoint endPoint, LinkedListNode<int>? node)
    {
        public EndPoint EndPoint { get; } = endPoint;
        public LinkedListNode<int>? Node { get; set; } = node;   // null when pinned
    }

    public UdpServerTransport(IPEndPoint bind, int maxAddresses = 4096)
    {
        _maxUnpinned = maxAddresses;
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
        if (OperatingSystem.IsWindows())
        {
            // Without this, an ICMP "port unreachable" from a departed client makes the next receive fail (SIO_UDP_CONNRESET).
            const int SioUdpConnReset = -1744830452;
            _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            // Without it Windows lets another server bind 127.0.0.1:P beside one on 0.0.0.0:P (or the reverse), and clients may
            // reach the wrong one. A port in use then fails here (SocketException: AddressAlreadyInUse or AccessDenied).
            _socket.ExclusiveAddressUse = true;
        }
        try { _socket.Bind(bind); }
        catch { _socket.Dispose(); throw; }
        _datagrams = new SocketDatagrams(_socket);
    }

    /// <summary>Unauthenticated addresses currently remembered (pinned ones are not counted).</summary>
    public int KnownAddresses => _unpinnedByAge.Count;

    /// <summary>The address and port the socket is bound to (an ephemeral port when bound to port 0).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public void Send(int connection, byte[] packet)
    {
        if (packet.Length > MaxPacket || !_byId.TryGetValue(connection, out var entry)) return;
        try { _socket.SendTo(packet, entry.EndPoint); } catch (SocketException) { }
    }

    public bool TryReceive(out int connection, out byte[] packet)
    {
        while (true)
        {
            EndPoint from = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            int n = DatagramReceive.Next(_datagrams, _buffer, ref from, MaxPacket);
            if (n < 0) break;
            if (_ids.TryGetValue(from, out connection))
            {
                var entry = _byId[connection];
                if (entry.Node is { } node) { _unpinnedByAge.Remove(node); _unpinnedByAge.AddLast(node); }
            }
            else
            {
                if (_unpinnedByAge.Count >= _maxUnpinned) Disconnect(_unpinnedByAge.First!.Value);
                connection = _nextId++;
                _ids[from] = connection;
                _byId[connection] = new Entry(from, _unpinnedByAge.AddLast(connection));
            }
            packet = _buffer[..n];
            return true;
        }
        (connection, packet) = (-1, []);
        return false;
    }

    public IPEndPoint? EndPointOf(int connection) => _byId.TryGetValue(connection, out var entry) ? entry.EndPoint as IPEndPoint : null;

    public void Pin(int connection)
    {
        if (_byId.TryGetValue(connection, out var entry) && entry.Node is { } node) { _unpinnedByAge.Remove(node); entry.Node = null; }
    }

    public void Disconnect(int connection)
    {
        if (!_byId.Remove(connection, out var entry)) return;
        _ids.Remove(entry.EndPoint);
        if (entry.Node is { } node) _unpinnedByAge.Remove(node);
    }

    public void Dispose() => _socket.Dispose();
}

/// <summary>UDP client endpoint talking to one server.</summary>
public sealed class UdpClientTransport : IClientTransport, IDisposable
{
    readonly Socket _socket;
    readonly SocketDatagrams _datagrams;
    readonly byte[] _buffer = new byte[2048];

    public UdpClientTransport(IPEndPoint server)
    {
        _socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
        _socket.Connect(server);
        _datagrams = new SocketDatagrams(_socket);
    }

    readonly object _sendLock = new();

    /// <summary>Safe from a second thread beside the main thread's sends (fix/stall-keepalive): one datagram at a time; a send after <see cref="Dispose"/> is dropped.</summary>
    public void Send(byte[] packet)
    {
        lock (_sendLock)
        {
            try { _socket.Send(packet); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }
    }

    public bool ThreadSafeSend => true;

    public bool TryReceive(out byte[] packet)
    {
        EndPoint from = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        int n = DatagramReceive.Next(_datagrams, _buffer, ref from, _buffer.Length);
        packet = n < 0 ? [] : _buffer[..n];
        return n >= 0;
    }

    public void Dispose() => _socket.Dispose();
}

/// <summary>
/// The socket calls the UDP receive loop makes. A seam so tests can reproduce each platform's receive semantics: on Linux
/// <c>Available</c> (FIONREAD) is the size of the first queued datagram, so it is 0 for a zero-length one; on Windows it is
/// the total of all queued bytes.
/// </summary>
public interface IDatagramSocket
{
    int Available { get; }
    /// <summary><see cref="Socket.Poll(int, SelectMode)"/>: with <see cref="SelectMode.SelectRead"/>, true when a datagram (even an empty one) waits.</summary>
    bool Poll(int microseconds, SelectMode mode);
    /// <summary>Reads one datagram (throws <see cref="SocketException"/>, <see cref="SocketError.WouldBlock"/> when none waits on a non-blocking socket).</summary>
    int ReceiveFrom(byte[] buffer, ref EndPoint remote);
}

sealed class SocketDatagrams(Socket socket) : IDatagramSocket
{
    public int Available => socket.Available;
    public bool Poll(int microseconds, SelectMode mode) => socket.Poll(microseconds, mode);
    public int ReceiveFrom(byte[] buffer, ref EndPoint remote) => socket.ReceiveFrom(buffer, ref remote);
}

/// <summary>The UDP transports' receive loop.</summary>
public static class DatagramReceive
{
    /// <summary>Datagrams one call may skip (empty, oversized, failed) before it gives up until the next call, so a flood cannot hold it.</summary>
    public const int MaxSkippedPerCall = 256;

    /// <summary>
    /// Reads the next datagram of 1 to <paramref name="maxLength"/> bytes into <paramref name="buffer"/> and returns its length, or -1
    /// when none waits. Empty, oversized and failed reads are skipped (each such read consumes its datagram).
    /// Readiness comes from <see cref="Socket.Poll(int, SelectMode)"/>, not <see cref="Socket.Available"/>: on Linux Available is
    /// the size of the first queued datagram, 0 for an empty one, so a loop gated on it would never read past an empty datagram
    /// and one zero-length packet would deafen the receiver (server hardening M1).
    /// </summary>
    public static int Next(IDatagramSocket socket, byte[] buffer, ref EndPoint from, int maxLength)
    {
        for (int skipped = 0; skipped < MaxSkippedPerCall; skipped++)
        {
            try { if (!socket.Poll(0, SelectMode.SelectRead)) return -1; }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return -1; }
            int n;
            try { n = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain) { return -1; }
            catch (SocketException) { continue; }   // a reset or a datagram larger than the buffer: consumed, skip it
            catch (ObjectDisposedException) { return -1; }
            if (n == 0 || n > maxLength) continue;
            return n;
        }
        return -1;
    }
}
