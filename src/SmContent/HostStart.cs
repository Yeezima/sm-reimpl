using System.Runtime.InteropServices;

namespace SmContent;

/// <summary>Who holds a UDP port that a hosted server wanted (fix/connect-timeout).</summary>
public enum PortHolder
{
    /// <summary>Not known (no owner table on this system, or the owner could not be read).</summary>
    Unknown,
    /// <summary>A server of this install (the same <c>SmServer</c> executable the front end starts): STOP SERVER can stop it.</summary>
    ThisInstall,
    /// <summary>Any other program (another copy of the server, an older build, anything else): never stopped from here.</summary>
    Other,
}

/// <summary>Where the start of a hosted server stands (fix/connect-timeout; <see cref="HostStart.StateOf"/>).</summary>
public enum HostStartState { Starting, Ready, PortInUse, Failed, TooSlow }

/// <summary>
/// fix/connect-timeout (the owner's report of the tester release: an old server held UDP 27015, the hosted one could not bind it and exited, and JOIN then
/// reached the old one and waited forever): the pure rules of watching a hosted server's start. The server prints <see cref="ServerLaunch.ReadyPrefix"/> once
/// it listens, and exits with <see cref="ExitPortInUse"/> when its port is taken (<c>src/SmServer/Program.cs</c>). A start that fails is reported and never
/// joined; only a server that printed its listening line is joined.
/// </summary>
public static class HostStart
{
    /// <summary>The server's exit code for a UDP port in use (docs/dedicated-server.md).</summary>
    public const int ExitPortInUse = 3;
    /// <summary>A start that has not printed its listening line after this long is stopped (it reads the game files first; ours).</summary>
    public const double StartLimitSeconds = 180;

    /// <summary>
    /// The start's state: <see cref="HostStartState.Ready"/> once the listening line came and the process runs; a process that ended is
    /// <see cref="HostStartState.PortInUse"/> for exit code 3, else <see cref="HostStartState.Failed"/> (also when it ended after the listening line);
    /// a running process past <paramref name="limitSeconds"/> without the line is <see cref="HostStartState.TooSlow"/>; otherwise still starting.
    /// </summary>
    public static HostStartState StateOf(bool running, bool ready, int? exitCode, double secondsSinceStart, double limitSeconds = StartLimitSeconds)
    {
        if (!running) return exitCode == ExitPortInUse ? HostStartState.PortInUse : HostStartState.Failed;
        if (ready) return HostStartState.Ready;
        return secondsSinceStart > limitSeconds ? HostStartState.TooSlow : HostStartState.Starting;
    }

    /// <summary>The plain message for a UDP port that is taken, by who holds it.</summary>
    public static string PortInUseText(int port, PortHolder holder) => holder switch
    {
        PortHolder.ThisInstall => $"Port {port} is already in use on this PC (a server of this game is running: STOP SERVER stops it). Close it or choose another port.",
        PortHolder.Other => $"Port {port} is already in use on this PC (another program is using port {port}, for example another server). Close it or choose another port.",
        _ => $"Port {port} is already in use on this PC (another server is running). Close it or choose another port.",
    };

    /// <summary>
    /// The process ids that own an IPv4 UDP endpoint on <paramref name="port"/> (any local address), read from the system's table without touching the port.
    /// Windows only (<c>GetExtendedUdpTable</c>); empty elsewhere or when the table cannot be read.
    /// </summary>
    public static IReadOnlyList<int> PortOwners(int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535) return [];
        try
        {
            int size = 0;
            const int AfInet = 2, UdpTableOwnerPid = 1, ErrorInsufficientBuffer = 122;
            uint first = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableOwnerPid, 0);
            if (first != ErrorInsufficientBuffer && first != 0) return [];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    uint result = GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableOwnerPid, 0);
                    if (result == ErrorInsufficientBuffer) continue;   // the table grew: again with the new size
                    if (result != 0) return [];
                    int count = Marshal.ReadInt32(buffer);
                    var owners = new List<int>();
                    for (int i = 0; i < count; i++)
                    {
                        // MIB_UDPROW_OWNER_PID: local address, local port (network order in the low 16 bits), owning pid; 12 bytes after the 4-byte count.
                        IntPtr row = buffer + 4 + i * 12;
                        int raw = Marshal.ReadInt32(row + 4);
                        int rowPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        if (rowPort == port) owners.Add(Marshal.ReadInt32(row + 8));
                    }
                    return owners.Distinct().ToList();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException or ArgumentException) { }
        return [];
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);
}
