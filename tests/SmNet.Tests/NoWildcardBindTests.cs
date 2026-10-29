using System.Text.RegularExpressions;

namespace SmNet.Tests;

/// <summary>
/// Owner's rule (2026-10-28): nothing in tests, tools or test runs binds every interface, because Windows Firewall then asks the owner about
/// the test host, Godot or the server. This scans the repository's C# sources so a new wildcard bind fails `dotnet test` before it can prompt.
/// The real server may still listen on every interface when an operator starts it without --bind; those files are listed below.
/// </summary>
public class NoWildcardBindTests
{
    // Files where IPAddress.Any is the server's own default bind for operators (never used by tests, which pass 127.0.0.1).
    static readonly string[] ServerDefaults = ["src/SmServer/GameServer.CommandLine.cs", "src/SmServer/Program.cs"];

    // A receive buffer's placeholder end point (`EndPoint from = new IPEndPoint(IPAddress.Any, 0)`) binds nothing; nor does a comparison with the default.
    static readonly Regex Harmless = new(@"EndPoint\s+from\s*=|IPAddress\.IPv6Any\s*:\s*IPAddress\.Any|Assert\.Equal\(IPAddress\.Any|\.Equals\(IPAddress\.Any\)", RegexOptions.Compiled);
    static readonly Regex Wildcard = new(@"IPAddress\.(Any|IPv6Any)\b|""0\.0\.0\.0""|""::""", RegexOptions.Compiled);
    static readonly Regex NewUdpSocket = new(@"new\s+(System\.Net\.Sockets\.)?Socket\([^)]*SocketType\.Dgram|new\s+(System\.Net\.Sockets\.)?UdpClient\(\s*\)", RegexOptions.Compiled);

    [Fact]
    public void No_source_binds_every_interface_outside_the_servers_operator_default()
    {
        string root = RepoRoot();
        var problems = new List<string>();
        foreach (var file in Sources(root))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ServerDefaults.Contains(rel) || rel == "tests/SmNet.Tests/NoWildcardBindTests.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (Wildcard.IsMatch(line) && !Harmless.IsMatch(line)) problems.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(problems.Count == 0, "Wildcard binds (use IPAddress.Loopback; see CLAUDE.md hard rules):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Every_UDP_socket_a_test_creates_is_bound_to_loopback_before_use()
    {
        // An unbound UDP socket binds every interface on its first send, which is enough for a firewall prompt.
        string root = RepoRoot();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel == "tests/SmNet.Tests/NoWildcardBindTests.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!NewUdpSocket.IsMatch(lines[i])) continue;
                bool bound = lines.Skip(i + 1).Take(3).Any(l => l.Contains(".Bind(", StringComparison.Ordinal) && l.Contains("Loopback", StringComparison.Ordinal));
                if (!bound) problems.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(problems.Count == 0, "UDP sockets in tests must be bound to IPAddress.Loopback within 3 lines of creation:\n" + string.Join("\n", problems));
    }

    static IEnumerable<string> Sources(string root) =>
        new[] { "src", "tests", "client" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f));

    static bool IsBuildOutput(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Contains("/obj/", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal) || p.Contains("/.godot/", StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test output");
    }
}
