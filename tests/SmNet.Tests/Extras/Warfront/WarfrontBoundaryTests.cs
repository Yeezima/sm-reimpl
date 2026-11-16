using System.Text.RegularExpressions;

namespace SmNet.Tests.Extras.Warfront;

/// <summary>
/// Warfront W1 (docs/added-content.md section 11): the source boundary of the Warfront folders, as text. No <c>.cs</c> file outside an
/// <c>Extras/Warfront/</c> folder names an <c>Extras.Warfront</c> namespace (the hash dispatcher <c>SimConfig.Extras.cs</c>, which writes
/// <c>WarfrontRules.Version</c> under <c>#if SM_WARFRONT</c>, is the one exception), and the list of Warfront hooks is pinned (W3's sixteen).
/// A slice that adds a hook (W3, W5) adds it to <see cref="Hooks"/> here.
/// </summary>
public class WarfrontBoundaryTests
{
    /// <summary>The Warfront hooks core or Musou code may declare: (file, hook): W3's.</summary>
    static readonly HashSet<(string File, string Hook)> Hooks =
    [
        ("src/SmSim/Extras/Musou/Campaign/World.Tfw.cs", "WarfrontStarted"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontReset"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontCommand"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontWarClock"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontBattleCreated"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontRetainer"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontBattleWon"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontDowned"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontLeft"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontPlayerScale"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontCommander"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontObjectives"),
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontSpawn"),
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontSpawned"),
        ("src/SmSim/Extras/Musou/World.Musou.cs", "WarfrontOrkDamage"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwPuppets.cs", "WarfrontOrder"),
    ];

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

    [Theory]
    [InlineData("    partial void WarfrontStep(World w);", true)]
    [InlineData("    partial void WarfrontJoin(ref bool b);", true)]
    [InlineData("    partial void WarfrontStep(World w)", false)]   // an implementation, not a declaration
    [InlineData("    void Other();", false)]
    public void The_declaration_pattern_counts_only_declarations(string line, bool declaration) =>
        Assert.Equal(declaration, IsHookDeclaration(line));

    static bool IsHookDeclaration(string line) => !IsComment(line) && HookDeclaration.IsMatch(line) && line.TrimEnd().EndsWith(';');

    /// <summary>Hook declarations outside <c>Extras/Warfront/</c> (the implementations live inside it) are exactly the pinned list.</summary>
    [Fact]
    public void The_hook_list_is_pinned()
    {
        var found = new HashSet<(string, string)>();
        foreach (var (path, lines) in Sources())
        {
            if (InWarfront(path)) continue;
            foreach (string line in lines)
                if (IsHookDeclaration(line)) found.Add((path, HookDeclaration.Match(line).Groups[1].Value));
        }
        Assert.True(found.SetEquals(Hooks), "Warfront hooks found: " + string.Join(", ", found.Select(f => $"{f.Item1}:{f.Item2}")));
    }

    static readonly Regex HookCall = new(@"(?<![\w.])(?<!(new|record|class|struct|interface)\s+)Warfront[A-Z]\w*\s*\(", RegexOptions.Compiled);

    [Theory]
    [InlineData("        WarfrontStep(w);   // WARFRONT-HOOK", true)]
    [InlineData("        WarfrontStep(w);", false)]
    public void A_hook_call_carries_the_marker(string line, bool marked) =>
        Assert.Equal(marked, line.Contains("// WARFRONT-HOOK", StringComparison.Ordinal));

    /// <summary>Every call of a Warfront hook outside <c>Extras/Warfront/</c> is marked <c>// WARFRONT-HOOK</c>.</summary>
    [Fact]
    public void Every_hook_call_site_is_marked()
    {
        var unmarked = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            if (InWarfront(path)) continue;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line) || !HookCall.IsMatch(line) || HookDeclaration.IsMatch(line)) continue;
                if (!line.Contains("// WARFRONT-HOOK", StringComparison.Ordinal)) unmarked.Add($"{path}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(unmarked.Count == 0, "Warfront hook calls without // WARFRONT-HOOK:" + Environment.NewLine + string.Join(Environment.NewLine, unmarked));
    }

    /// <summary>The build symbol <c>SM_WARFRONT</c> appears in code only in the switch and the hash dispatcher.</summary>
    [Fact]
    public void The_build_symbol_is_used_only_in_the_pinned_files()
    {
        var allowed = new HashSet<string> { "src/SmSim/ContentExtras.cs", "src/SmSim/Extras/SimConfig.Extras.cs", "src/SmTool/Program.cs" };   // SmTool: the warfront-* command routes
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && lines[i].Contains("SM_WARFRONT", StringComparison.Ordinal) && !allowed.Contains(path)) offenders.Add($"{path}:{i + 1}");
        Assert.True(offenders.Count == 0, "SM_WARFRONT outside its files: " + string.Join(", ", offenders));
    }
}
