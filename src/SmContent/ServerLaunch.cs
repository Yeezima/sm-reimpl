namespace SmContent;

/// <summary>What the client's "Host a match" asks for: the mode, the UDP port and whether only this PC may connect.</summary>
/// <param name="Content">The client's <c>--content</c> profile name (docs/added-content.md), passed to the server so the two match; null: the server's default.</param>
/// <param name="Bots">fix/connect-timeout, fix/hosting-bots-logs: how many bots the front end starts once the server listens (<see cref="HostBots"/>; default none); with any,
/// the server keeps requested sides (<c>--honor-side-requests</c>) so the bots stay on theirs.</param>
/// <param name="Difficulty">Easy: the server scales the damage bots deal (<c>--bot-damage-scale</c> <see cref="HostBots.EasyDamageScale"/>); Normal: unscaled.</param>
public sealed record HostOptions(string Mode, int Port, bool LocalOnly, string GameDir, bool AllowCustomEmblems = false, string? Content = null,
    int Bots = HostBots.DefaultCount, BotDifficulty Difficulty = BotDifficulty.Easy);

/// <summary>
/// The pure parts of starting the bundled dedicated server from the client (the process handling is in the client): where the executable is looked for,
/// which command-line it gets, and the notes shown next to the button. The server's own flags are in <c>src/SmServer/Program.cs</c>
/// (<c>--port</c>, <c>--bind</c>, <c>--mode annihilation|seize</c>, <c>--game-dir</c>; there is no bot option).
/// </summary>
public static class ServerLaunch
{
    public const int DefaultPort = 27015;
    public const string ModeAnnihilation = "annihilation", ModeSeize = "seize";
    public const string LocalAddress = "127.0.0.1";
    /// <summary>The line the server prints once it is listening (<c>smserver on UDP &lt;port&gt;</c>).</summary>
    public const string ReadyPrefix = "smserver on UDP";

    /// <summary>
    /// Executable names looked for next to the client, in order: the platform's name directly beside it, then in a <c>server</c> folder below it, then in a
    /// <c>server</c> folder beside the client's own folder (the tester zip: the client in <c>game\</c>, the server in <c>server\</c>).
    /// </summary>
    public static IEnumerable<string> RelativeNames()
    {
        string name = OperatingSystem.IsWindows() ? "SmServer.exe" : "SmServer";
        yield return name;
        yield return Path.Combine("server", name);
        yield return Path.Combine("..", "server", name);
    }

    /// <summary>
    /// The server executable: <paramref name="overridePath"/> when it names a file (the client's <c>--server-exe</c> test aid), else the first of
    /// <see cref="RelativeNames"/> under <paramref name="baseDir"/> (the client executable's folder). Null when there is none: the Host button stays hidden.
    /// </summary>
    public static string? FindExecutable(string? baseDir, string? overridePath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(overridePath)) return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
            if (string.IsNullOrWhiteSpace(baseDir)) return null;
            foreach (var relative in RelativeNames())
            {
                string path = Path.Combine(baseDir, relative);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    /// <summary>A port from the text field: 1024 to 65535 (the privileged ports are refused). Null with a message when it is not.</summary>
    public static string? ValidatePort(string text, out int port)
    {
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
        {
            port = 0;
            return "The port must be a number from 1024 to 65535 (default 27015).";
        }
        return null;
    }

    /// <summary>
    /// null when UDP <paramref name="port"/> is free: no UDP listener on it in the system's table (any address), and this PC only (127.0.0.1) can be
    /// bound and released at once. Never binds every interface (0.0.0.0): that would make Windows Firewall ask the owner about the client or the test host.
    /// Otherwise a sentence saying it is taken (<see cref="HostStart.PortInUseText"/>; fix/connect-timeout): <paramref name="holderOf"/> names who holds it from
    /// the owning process ids (<see cref="HostStart.PortOwners"/>; null or no ids: not known). Checked before the server starts so a taken port gives a clear
    /// message, not a dead process.
    /// </summary>
    public static string? PortProblem(int port, Func<IReadOnlyList<int>, PortHolder>? holderOf = null)
    {
        string Taken()
        {
            var owners = HostStart.PortOwners(port);
            var holder = PortHolder.Unknown;
            if (owners.Count > 0) holder = holderOf?.Invoke(owners) ?? PortHolder.Other;
            return HostStart.PortInUseText(port, holder);
        }
        try
        {
            foreach (var listener in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners())
                if (listener.Port == port)
                    return Taken();
        }
        catch (Exception e) when (e is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException) { }   // no table here: the bind below still checks
        foreach (var address in new[] { System.Net.IPAddress.Loopback })
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;   // strict: fails if anyone else holds the port at all
                socket.Bind(new System.Net.IPEndPoint(address, port));
            }
            catch (System.Net.Sockets.SocketException)
            {
                return Taken();
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or PlatformNotSupportedException) { }   // cannot probe here: let the server try
        }
        return null;
    }

    /// <summary>The server's arguments for these options (each flag with its value, as the server reads them in pairs). Throws on an unknown mode or a bad port.</summary>
    public static List<string> Arguments(HostOptions options)
    {
        if (options.Mode is not (ModeAnnihilation or ModeSeize)) throw new ArgumentException($"unknown mode '{options.Mode}'", nameof(options));
        if (options.Port is < 1 or > 65535) throw new ArgumentException($"bad port {options.Port}", nameof(options));
        var args = new List<string> { "--port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--mode", options.Mode };
        if (options.LocalOnly) { args.Add("--bind"); args.Add(LocalAddress); }   // omitted: every interface (the server's default)
        if (!string.IsNullOrWhiteSpace(options.GameDir)) { args.Add("--game-dir"); args.Add(options.GameDir); }
        // Hosted games do not pull emblem images from strangers unless the host opts in (the server reads this flag before the pairs).
        if (!options.AllowCustomEmblems) args.Add("--no-custom-emblems");
        // Added content E1: the client's profile, so the hosted server's config hash matches the client joining it.
        if (!string.IsNullOrWhiteSpace(options.Content)) { args.Add("--content"); args.Add(options.Content); }
        args.AddRange(HostBots.ServerArguments(options.Bots, options.Difficulty));   // bots: keep requested sides, Easy scales their damage
        return args;
    }

    /// <summary>The host to put in the Join screen's server field for a server started here.</summary>
    public static string LocalHost(int port) => $"{LocalAddress}:{port}";

    /// <summary>Tester builds and issues live here (the public repository; never the private one).</summary>
    public const string ReleasesUrl = "https://github.com/Yeezima/sm-reimpl-releases";

    /// <summary>The note under the host options. This PC only is the default and needs no note beyond itself; the firewall and router advice shows only for the opt-in.</summary>
    public static string ReachNote(int port, bool localOnly) => localOnly
        ? "Only this PC can connect (127.0.0.1). Choose LAN / Internet to let other players in."
        : $"Other players connect to this PC's address and UDP port {port}. Windows Firewall will ask once: allow it. Over the internet you also need to forward UDP port {port} on your router; " +
          "that does not work behind carrier-grade NAT (CGNAT), where a VPN such as Tailscale or ZeroTier does.";
}
