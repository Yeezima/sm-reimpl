using System.Text.RegularExpressions;
using SmSim;

namespace SmNet.Tests.Extras.Executions;

/// <summary>
/// Executions X1 (docs/added-content.md section 13): the source boundary of the executions folders, as text, and the switch. No <c>.cs</c> file outside an <c>Extras/Executions/</c> folder
/// names an <c>Extras.Executions</c> namespace except the pinned dispatchers; the <c>Executions*</c> hooks declared outside those folders (the dispatchers' lines and, from X4, the Musou files'
/// lines) are exactly <see cref="Hooks"/>, and every call of one there carries <c>// EXECUTIONS-HOOK</c>. The core <c>Extras*</c> hooks are pinned in <c>ExtrasBoundaryTests</c>.
/// A task that adds a hook line (X4's <c>MusouSim.Ai.cs</c> and <c>MusouSim.cs</c>, X5's <c>CrowdView.cs</c>) adds it here.
/// </summary>
public class ExecutionsBoundaryTests
{
    /// <summary>The executions hooks declared outside <c>Extras/Executions/</c>: (file, hook).</summary>
    static readonly HashSet<(string File, string Hook)> Hooks =
    [
        ("src/SmServer/Extras/GameServer.Extras.cs", "ExecutionsSend"),
        ("src/SmServer/Extras/GameServer.Extras.cs", "ExecutionsCreated"),
        ("client/Extras/NetworkView.Extras.cs", "ExecutionsEnvelope"),
        ("client/Extras/NetworkView.Extras.cs", "ExecutionsFrame"),
        ("src/SmContent/Extras/GameplayTuning.Extras.cs", "ExecutionsLoad"),
        ("client/Extras/Musou/CrowdView.cs", "ExecutionsHideEnemy"),        // X5a: a Warfront Ork playing an execution is drawn by its proxy, not the crowd
        ("client/Extras/Musou/MusouCrowd.cs", "ExecutionsCorpseProxied"),   // X5a: the executed Ork leaves no crowd corpse (its proxy lies and sinks)
        ("client/Extras/Musou/NetworkView.Musou.cs", "ExecutionsDeathsHanded"),   // X5 review X5-3: the crowd deaths at their source, before the sound frame empties the list
        // X4 (Warfront on Orks): the held Ork's AI step and the slot's spawn and reset (Extras/Executions/Orks/MusouSim.Executions.cs).
        ("src/SmSim/Extras/Musou/MusouSim.Ai.cs", "ExecutionsHeldStep"),
        ("src/SmSim/Extras/Musou/MusouSim.Ai.cs", "ExecutionsSpawned"),
        ("src/SmSim/Extras/Musou/MusouSim.Ai.cs", "ExecutionsReset"),
    ];

    /// <summary>Files outside the executions folders that may name the namespace: the config hash's dispatcher.</summary>
    static readonly HashSet<string> NamespaceUsers = ["src/SmSim/Extras/SimConfig.Extras.cs"];

    static readonly Regex ExecutionsNamespace = new(@"\bExtras\s*\.\s*Executions\b", RegexOptions.Compiled);
    static readonly Regex HookDeclaration = new(@"\bpartial\s+[\w<>\[\]?.,]+\s+(Executions\w*)\s*\(", RegexOptions.Compiled);
    static readonly Regex HookCall = new(@"(?<![\w.])(?<!(new|record|class|struct|interface)\s+)Executions[A-Z]\w*\s*\(", RegexOptions.Compiled);

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
                if (rel == "tests/SmNet.Tests/Extras/Executions/ExecutionsBoundaryTests.cs") continue;   // this file names the patterns
                files.Add((rel, File.ReadAllLines(file)));
            }
        }
        return files;
    }

    static bool InExecutions(string rel)
    {
        var parts = rel.Split('/');
        int i = Array.IndexOf(parts, "Extras");
        return i >= 0 && i + 1 < parts.Length && parts[i + 1] == "Executions";
    }

    static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);
    static bool IsHookDeclaration(string line) => !IsComment(line) && HookDeclaration.IsMatch(line) && line.TrimEnd().EndsWith(';');

    [Theory]
    [InlineData("using SmSim.Extras.Executions;", true)]
    [InlineData("SmSim.Extras.Executions.ExecutionRules.Hash(w, this);", true)]
    [InlineData("if (Extras.HasFlag(ContentExtras.Executions)) return;", false)]
    [InlineData("ContentExtras.Executions", false)]
    public void The_namespace_pattern_tells_the_namespace_from_the_flag(string line, bool flagged) =>
        Assert.Equal(flagged, ExecutionsNamespace.IsMatch(line));

    [Theory]
    [InlineData("    partial void ExecutionsSend();", true)]
    [InlineData("    static partial void ExecutionsLoad(ref SimConfig config, string gameDir, List<string> notes);", true)]
    [InlineData("    partial void ExecutionsSend()", false)]   // an implementation
    public void The_declaration_pattern_counts_only_declarations(string line, bool declaration) =>
        Assert.Equal(declaration, IsHookDeclaration(line));

    [Fact]
    public void The_scan_sees_the_executions_source()
    {
        var sources = Sources();
        Assert.Contains(sources, s => s.Path == "src/SmSim/Extras/Executions/ExecutionRules.cs");
        Assert.Contains(sources, s => s.Path == "src/SmNet/Extras/Executions/ExecutionWire.cs");
    }

    [Fact]
    public void No_file_outside_the_executions_folders_names_the_namespace()
    {
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            if (InExecutions(path) || NamespaceUsers.Contains(path)) continue;
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && ExecutionsNamespace.IsMatch(lines[i])) offenders.Add($"{path}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "outside Extras/Executions/ (docs/added-content.md section 13):\n" + string.Join("\n", offenders));
    }

    /// <summary>Hook declarations outside <c>Extras/Executions/</c> are exactly the pinned list (no Musou file has one yet: X4 adds its two with their pins).</summary>
    [Fact]
    public void The_hook_list_is_pinned()
    {
        var found = new HashSet<(string, string)>();
        foreach (var (path, lines) in Sources())
        {
            if (InExecutions(path)) continue;
            foreach (string line in lines)
                if (IsHookDeclaration(line)) found.Add((path, HookDeclaration.Match(line).Groups[1].Value));
        }
        Assert.True(found.SetEquals(Hooks), "Executions hooks found: " + string.Join(", ", found.Select(f => $"{f.Item1}:{f.Item2}")));
    }

    [Fact]
    public void Every_hook_call_outside_the_folders_is_marked_and_no_musou_file_calls_one_unpinned()
    {
        var unmarked = new List<string>();
        var musou = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            if (InExecutions(path)) continue;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line) || !HookCall.IsMatch(line) || HookDeclaration.IsMatch(line)) continue;
                if (!line.Contains("// EXECUTIONS-HOOK", StringComparison.Ordinal)) unmarked.Add($"{path}:{i + 1}: {line.Trim()}");
                if (path.Contains("/Extras/Musou/", StringComparison.Ordinal) && !Hooks.Any(h => h.File == path)) musou.Add($"{path}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(unmarked.Count == 0, "Executions hook calls without // EXECUTIONS-HOOK:\n" + string.Join("\n", unmarked));
        Assert.True(musou.Count == 0, "Executions hook calls in Musou files without a pinned hook (Musou isolation):\n" + string.Join("\n", musou));
    }

    [Fact]
    public void The_flag_is_in_extended_and_warfront_only()
    {
        Assert.Equal(128u, (uint)ContentExtras.Executions);
        Assert.True(ContentExtras.All.HasFlag(ContentExtras.Executions));
        Assert.True(ContentProfiles.WarfrontProfile.HasFlag(ContentExtras.Executions));
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Musou, out var musou, out _));
        Assert.False(musou.HasFlag(ContentExtras.Executions));
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Faithful, out var faithful, out _));
        Assert.Equal(ContentExtras.None, faithful);
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Warfront, out var warfront, out _));
        Assert.Equal(ContentProfiles.WarfrontProfile, warfront);
        Assert.Equal(ContentProfiles.Warfront, ContentProfiles.Name(warfront));
        Assert.True(ContentProfiles.TryParse("executions", out var alone, out _));
        Assert.Equal(ContentExtras.Executions, alone);
        Assert.False(ContentProfiles.TryParse("musou,executions", out _, out _));   // Musou Mode stays a mode of its own
    }
}
