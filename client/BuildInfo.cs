using System.Reflection;

namespace SmClient;

/// <summary>
/// The build stamp of a tester release (tools/release/release.ps1 passes <c>InformationalVersion = "&lt;git describe&gt;|&lt;yyyy-MM-dd&gt;"</c>
/// to the build; nothing is generated or committed). A plain <c>client.cmd</c> build has no stamp and reports "dev".
/// Nothing reads this yet: the front end's Join screen can show <see cref="Summary"/> (for example next to its title or in a corner label).
/// </summary>
public static class BuildInfo
{
    static readonly string[] Parts = Read();

    /// <summary><c>git describe --tags --always --dirty</c> of the release build, or "dev".</summary>
    public static string Version => Parts[0];

    /// <summary>The build date (UTC, yyyy-MM-dd), or "" for a development build.</summary>
    public static string BuildDate => Parts[1];

    /// <summary>The network protocol version this client speaks (clients and servers of different protocols refuse each other).</summary>
    public static int Protocol => SmNet.Protocol.Version;

    /// <summary>One line for a label: "5c95747, protocol 23, built 2026-10-28" or "dev, protocol 23".</summary>
    public static string Summary => BuildDate.Length > 0 ? $"{Version}, protocol {Protocol}, built {BuildDate}" : $"{Version}, protocol {Protocol}";

    static string[] Read()
    {
        string? info = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info is null || !info.Contains('|')) return ["dev", ""];
        int bar = info.IndexOf('|');
        string version = info[..bar].Trim();
        string date = info[(bar + 1)..].Split('+')[0].Trim();
        return [version.Length > 0 ? version : "dev", date];
    }
}
