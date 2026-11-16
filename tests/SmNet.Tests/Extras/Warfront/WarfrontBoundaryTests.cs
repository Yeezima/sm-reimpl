using System.Text.RegularExpressions;

namespace SmNet.Tests.Extras.Warfront;

/// <summary>
/// Warfront W1 (docs/added-content.md section 11): the source boundary of the Warfront folders, as text. No <c>.cs</c> file outside an
/// <c>Extras/Warfront/</c> folder names an <c>Extras.Warfront</c> namespace (the hash dispatcher <c>SimConfig.Extras.cs</c>, which writes
/// <c>WarfrontRules.Version</c> under <c>#if SM_WARFRONT</c>, is the one exception), and the list of Warfront hooks is pinned: still empty.
/// A slice that adds a hook (W3, W5) adds it to <see cref="Hooks"/> here.
/// </summary>
public class WarfrontBoundaryTests
{
    /// <summary>The Warfront hooks core or Musou code may declare: (file, hook). Empty in W1.</summary>
    static readonly HashSet<(string File, string Hook)> Hooks = [];

    /// <summary>Files outside the Warfront folders that may name the namespace.</summary>
    static readonly HashSet<string> NamespaceUsers = ["src/SmSim/Extras/SimConfig.Extras.cs"];

    static readonly Regex WarfrontNamespace = new(@"\bExtras\s*\.\s*Warfront\b", RegexOptions.Compiled);
    static readonly Regex HookDeclaration = new(@"\bpartial\s+[\w<>\[\]?.,]+\s+(Warfront\w*)\s*\(", RegexOptions.Compiled);

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
    }

    static List<(string Path, string[] Lines)> Sources()
    {
        string root = RepoRoot();
        var files = new List<(string, string[])>();
        foreach (string top in new[] { "src", "client", "tests" })
        {
            string dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Split('/').Any(part => part is "bin" or "obj" or ".godot")) continue;
                if (rel == "tests/SmNet.Tests/Extras/Warfront/WarfrontBoundaryTests.cs") continue;   // this file names the patterns
                files.Add((rel, File.ReadAllLines(file)));
            }
        }
        return files;
    }

    static bool InWarfront(string rel)
    {
        var parts = rel.Split('/');
        int i = Array.IndexOf(parts, "Extras");
        return i >= 0 && i + 1 < parts.Length && parts[i + 1] == "Warfront";
    }

    static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    [Theory]
    [InlineData("using SmSim.Extras.Warfront;", true)]
    [InlineData("var x = SmNet.Extras.Warfront.WarfrontWire.Version;", true)]
    [InlineData("namespace SmSim.Extras.Warfront.Campaign;", true)]
    [InlineData("using SmSim.Extras.Musou;", false)]
    [InlineData("var a = config.Extras.HasFlag(ContentExtras.Warfront);", false)]
    [InlineData("ContentExtras.Warfront", false)]
    public void The_namespace_pattern_tells_the_namespace_from_the_flag(string line, bool flagged) =>
        Assert.Equal(flagged, WarfrontNamespace.IsMatch(line));

    [Fact]
    public void The_scan_sees_the_warfront_source()
    {
        var sources = Sources();
        Assert.Contains(sources, s => s.Path == "src/SmSim/Extras/Warfront/WarfrontRules.cs");
        Assert.Contains(sources, s => s.Path == "src/SmSim/Extras/SimConfig.Extras.cs");
    }

    [Fact]
    public void No_file_outside_the_warfront_folders_names_a_warfront_namespace()
    {
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            if (InWarfront(path) || NamespaceUsers.Contains(path)) continue;
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && WarfrontNamespace.IsMatch(lines[i])) offenders.Add($"{path}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "outside Extras/Warfront/ (docs/added-content.md section 11):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_dispatcher_names_the_namespace_only_under_its_build_symbol()
    {
        var lines = Sources().First(s => s.Path == "src/SmSim/Extras/SimConfig.Extras.cs").Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsComment(lines[i]) || !WarfrontNamespace.IsMatch(lines[i])) continue;
            bool guarded = false;
            for (int j = i - 1; j >= 0 && !guarded; j--)
            {
                string t = lines[j].Trim();
                if (t.StartsWith("#endif", StringComparison.Ordinal)) break;
                if (t.StartsWith("#if SM_WARFRONT", StringComparison.Ordinal)) guarded = true;
            }
            Assert.True(guarded, $"SimConfig.Extras.cs:{i + 1} names the Warfront namespace outside #if SM_WARFRONT");
        }
    }

    [Fact]
    public void The_hook_list_is_pinned_and_still_empty()
    {
        var found = new HashSet<(string, string)>();
        foreach (var (path, lines) in Sources())
            foreach (string line in lines)
                if (!IsComment(line) && HookDeclaration.Match(line) is { Success: true } m) found.Add((path, m.Groups[1].Value));
        Assert.Empty(Hooks);
        Assert.True(found.SetEquals(Hooks), "Warfront hooks found: " + string.Join(", ", found.Select(f => $"{f.Item1}:{f.Item2}")));
    }
}
