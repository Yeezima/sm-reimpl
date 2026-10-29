using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The pure side of the client's "Host a match": executable lookup, arguments and port checks. Synthetic temp folders only.</summary>
public sealed class ServerLaunchTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sm-serverlaunch-" + Guid.NewGuid().ToString("N"));

    public ServerLaunchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static string ExeName => OperatingSystem.IsWindows() ? "SmServer.exe" : "SmServer";

    [Fact]
    public void FindExecutable_prefers_beside_the_client_then_the_server_folder_and_hides_when_absent()
    {
        Assert.Null(ServerLaunch.FindExecutable(_root));
        Directory.CreateDirectory(Path.Combine(_root, "server"));
        string inFolder = Path.Combine(_root, "server", ExeName);
        File.WriteAllBytes(inFolder, []);
        Assert.Equal(inFolder, ServerLaunch.FindExecutable(_root));
        string beside = Path.Combine(_root, ExeName);
        File.WriteAllBytes(beside, []);
        Assert.Equal(beside, ServerLaunch.FindExecutable(_root));
        Assert.Null(ServerLaunch.FindExecutable(null));
        Assert.Null(ServerLaunch.FindExecutable(""));
    }

    [Fact]
    public void FindExecutable_finds_the_server_folder_beside_the_game_folder()
    {
        // The tester zip: the client in game\, the server in server\ next to it (the front end's Host button from game\sm-reimpl.exe).
        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        Assert.Null(ServerLaunch.FindExecutable(game));
        Directory.CreateDirectory(Path.Combine(_root, "server"));
        string sibling = Path.Combine(_root, "server", ExeName);
        File.WriteAllBytes(sibling, []);
        Assert.Equal(sibling, ServerLaunch.FindExecutable(game));
        // A server inside the client's own folder still wins.
        Directory.CreateDirectory(Path.Combine(game, "server"));
        string inside = Path.Combine(game, "server", ExeName);
        File.WriteAllBytes(inside, []);
        Assert.Equal(inside, ServerLaunch.FindExecutable(game));
    }

    [Fact]
    public void FindExecutable_override_must_exist()
    {
        string file = Path.Combine(_root, "custom.exe");
        Assert.Null(ServerLaunch.FindExecutable(_root, file));
        File.WriteAllBytes(file, []);
        Assert.Equal(file, ServerLaunch.FindExecutable(_root, file));
    }

    [Fact]
    public void Arguments_for_all_interfaces_omit_bind()
    {
        var args = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeSeize, 27150, false, "G:/game"));
        Assert.Equal(["--port", "27150", "--mode", "seize", "--game-dir", "G:/game", "--no-custom-emblems"], args);
    }

    [Fact]
    public void Arguments_for_this_pc_only_bind_loopback()
    {
        var args = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27151, true, ""));
        Assert.Equal(["--port", "27151", "--mode", "annihilation", "--bind", "127.0.0.1", "--no-custom-emblems"], args);
        Assert.DoesNotContain("--no-custom-emblems", ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27151, true, "", AllowCustomEmblems: true)));
    }

    [Fact]
    public void Arguments_pass_the_clients_content_profile()
    {
        // Added content E1: the hosted server runs the joining client's --content, else the client is refused (config hash).
        var args = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27152, true, "", Content: "faithful"));
        Assert.Equal(["--port", "27152", "--mode", "annihilation", "--bind", "127.0.0.1", "--no-custom-emblems", "--content", "faithful"], args);
        Assert.DoesNotContain("--content", ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27152, true, "")));   // null: the server's default
    }

    [Fact]
    public void Arguments_refuse_bad_mode_and_port()
    {
        Assert.Throws<ArgumentException>(() => ServerLaunch.Arguments(new HostOptions("ffa-ish", 27015, true, "")));
        Assert.Throws<ArgumentException>(() => ServerLaunch.Arguments(new HostOptions("seize", 0, true, "")));
        Assert.Throws<ArgumentException>(() => ServerLaunch.Arguments(new HostOptions("seize", 70000, true, "")));
    }

    [Theory]
    [InlineData("27015", true, 27015)]
    [InlineData(" 27150 ", true, 27150)]
    [InlineData("1024", true, 1024)]
    [InlineData("1023", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("27015.5", false, 0)]
    public void ValidatePort_accepts_1024_to_65535(string text, bool ok, int expected)
    {
        string? problem = ServerLaunch.ValidatePort(text, out int port);
        Assert.Equal(ok, problem is null);
        Assert.Equal(expected, port);
    }

    [Fact]
    public void PortProblem_is_null_for_a_free_port_and_a_message_for_a_taken_one()
    {
        const int port = 27152;   // test ports are 27150-27159 only
        Assert.Null(ServerLaunch.PortProblem(port));
        using (var holder = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port)))   // never 0.0.0.0 in tests: Windows Firewall would prompt for the test host
        {
            string? problem = ServerLaunch.PortProblem(port);
            Assert.NotNull(problem);
            Assert.Contains(port.ToString(), problem);
            Assert.Contains("already in use", problem);
        }
        Assert.Null(ServerLaunch.PortProblem(port));   // released again
    }

    [Fact]
    public void PortProblem_sees_a_loopback_only_holder_too()
    {
        const int port = 27153;
        using var holder = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        Assert.NotNull(ServerLaunch.PortProblem(port));
    }

    [Fact]
    public void Notes_and_host_text()
    {
        Assert.Equal("127.0.0.1:27150", ServerLaunch.LocalHost(27150));
        Assert.Contains("27150", ServerLaunch.ReachNote(27150, false));
        Assert.Contains("Firewall", ServerLaunch.ReachNote(27150, false));
        Assert.Contains("CGNAT", ServerLaunch.ReachNote(27150, false));
        Assert.Contains("Tailscale", ServerLaunch.ReachNote(27150, false));
        var local = ServerLaunch.ReachNote(27150, true);
        Assert.Contains("only this PC", local, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Firewall", local);
        Assert.DoesNotContain("forward", local);
        Assert.StartsWith("https://github.com/Yeezima/sm-reimpl-releases", ServerLaunch.ReleasesUrl);
    }
}
