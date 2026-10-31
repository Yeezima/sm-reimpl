using System.Text.RegularExpressions;

namespace SmNet.Tests;

/// <summary>
/// Added content E1 (docs/added-content.md): the source boundary that keeps the added content separable at any time (the owner's rule,
/// 2026-10-28). Every <c>.cs</c> file of <c>src/</c>, <c>client/</c> and <c>tests/</c> is scanned as text:
/// <list type="bullet">
/// <item>no file outside an <c>Extras/</c> folder names an added-content namespace: <c>&lt;Project&gt;</c> dot <c>Extras</c>, or a bare
/// <c>Extras</c> dot <c>&lt;Feature&gt;</c> dot ... (as code inside a project namespace could write it), or <c>using</c> / <c>namespace</c> with it;
/// member access such as <c>SimConfig.Extras</c> or <c>Extras.HasFlag</c> is not a namespace and passes;</item>
/// <item>hook declarations (partial methods named <c>Extras*</c>) only as the pinned (file, hook) pairs of <see cref="Hooks"/>, and every
/// call of one in core code is marked <c>// EXTRAS-HOOK</c>;</item>
/// <item>the <c>SM_EXTRAS</c> symbol in a preprocessor condition only in the files of <see cref="SymbolFiles"/>.</item>
/// </list>
/// This is a text guard that catches mistakes early; the real gate is <c>faithful-build.cmd</c>, which compiles everything without the
/// <c>Extras/</c> folders. A new hook goes on <see cref="Hooks"/>; a file that must test the build symbol goes on <see cref="SymbolFiles"/>.
/// </summary>
public class ExtrasBoundaryTests
{
    /// <summary>The hooks core code may declare: (file relative to the repository with forward slashes, hook name).</summary>
    static readonly HashSet<(string File, string Hook)> Hooks =
    [
        ("src/SmSim/ContentExtras.cs", "ExtrasHash"),              // SimConfig.ExtrasHash(BinaryWriter), called in SimConfig.Hash
        ("src/SmContent/GameplayTuning.cs", "ExtrasLoad"),         // GameplayTuning.ExtrasLoad(ref SimConfig, string, List<string>), called in LoadForMatch
        ("src/SmServer/GameServer.Envelope.cs", "ExtrasSend"),     // GameServer.ExtrasSend(), called once a tick after SendPickups (E3's envelope), only with added content
        ("client/NetworkView.cs", "ExtrasInput"),                  // NetworkView.ExtrasInput(ref PlayerInput), on every sampled live input (loyalist barks: the FOR THE EMPEROR key)
        ("client/NetworkView.cs", "ExtrasFrame"),                  // NetworkView.ExtrasFrame(PlayerState, IReadOnlyList<PlayerState>, double), once a frame after the sound (loyalist barks: barks and the key's shouts)
        ("client/NetworkView.Match.cs", "ExtrasKill"),             // NetworkView.ExtrasKill(KillView), each kill-feed entry once (loyalist barks: kill barks)
        // Musou (MU0): each returns at once unless World._musou is set (World.StartMusou, only under ContentExtras.Musou).
        ("src/SmSim/Simulation.cs", "ExtrasStep"),                 // World.ExtrasStep(), once a tick after ApplyPerkBlasts, before CountProjectilesOut (Musou: the enemies)
        ("src/SmSim/Simulation.cs", "ExtrasMelee"),                // World.ExtrasMelee(int, PlayerState, MeleeStep), at the end of ResolveMelee (Musou: the cleave)
        ("src/SmSim/Projectiles.cs", "ExtrasBlast"),               // World.ExtrasBlast(int, Vector3, SplashSpec, bool), at the end of ApplyBlast (Musou: blasts on enemies)
        ("src/SmSim/Teams.cs", "ExtrasStepMatch"),                 // World.ExtrasStepMatch(ref bool handled), top of StepMatch; the core skips its flow when handled (Musou: one side)
        ("src/SmSim/Teams.cs", "ExtrasJoinSide"),                  // World.ExtrasJoinSide(ref Team, ref bool forced), in AssignClass / AssignSide (Musou: Space Marines only, no balance)
        ("src/SmServer/GameServer.cs", "ExtrasCreated"),           // GameServer.ExtrasCreated(), end of the constructor when Extras is not None (Musou: the nav graph, World.StartMusou)
    ];

    /// <summary>
    /// Files that may test <c>SM_EXTRAS</c> in a preprocessor condition: the switch itself, and the composition roots should they ever need it
    /// (none does yet; everything else asks <c>ContentExtras.Compiled</c>).
    /// </summary>
    static readonly HashSet<string> SymbolFiles =
    [
        "src/SmSim/ContentExtras.cs",
        "src/SmServer/Program.cs",
        "src/SmTool/Program.cs",
    ];

    /// <summary>The project namespaces whose <c>.Extras</c> sub-namespaces hold added content.</summary>
    static readonly Regex ExtrasNamespace = new(@"\b(SmSim|SmContent|SmNet|SmServer|SmTool|SmFormats|SmClient)\s*\.\s*Extras\b", RegexOptions.Compiled);
    /// <summary>A bare <c>Extras.Feature.X</c> (not after a dot or a word: so not <c>Config.Extras.HasFlag</c>), or <c>using</c> / <c>namespace</c> of <c>Extras.Feature</c>.</summary>
    static readonly Regex BareExtrasNamespace = new(@"(?<![\w.])Extras\s*\.\s*[A-Z]\w*\s*\.\s*[A-Za-z_]|^\s*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?Extras\s*\.|^\s*namespace\s+Extras\b", RegexOptions.Compiled);
    static readonly Regex HookDeclaration = new(@"\bpartial\s+[\w<>\[\]?.,]+\s+(Extras\w*)\s*\(", RegexOptions.Compiled);
    /// <summary>A call of an <c>Extras*</c> method; not a type's construction or declaration (<c>new ExtrasUpdate(</c>, <c>record ExtrasUpdate(</c>: E3's envelope types).</summary>
    static readonly Regex HookCall = new(@"(?<![\w])(?<!\b(new|record|class|struct|interface)\s+)Extras[A-Z]\w*\s*\(", RegexOptions.Compiled);
    static readonly Regex SymbolCondition = new(@"^\s*#\s*(if|elif)\b.*\bSM_EXTRAS\b", RegexOptions.Compiled);

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
    }

    /// <summary>Every source file (relative path with forward slashes, lines), build output and Godot's cache left out.</summary>
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
                if (rel == "tests/SmNet.Tests/ExtrasBoundaryTests.cs") continue;   // this file names the patterns
                files.Add((rel, File.ReadAllLines(file)));
            }
        }
        return files;
    }

    static bool InExtras(string rel) => rel.Split('/').Contains("Extras");
    static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    [Fact]
    public void The_scan_sees_the_source_and_the_known_hooks()
    {
        var sources = Sources();
        Assert.True(sources.Count > 300, $"only {sources.Count} files scanned");
        var declared = sources.SelectMany(s => s.Lines.Select(l => (s.Path, Match: HookDeclaration.Match(l))).Where(x => x.Match.Success))
            .Select(x => (x.Path, x.Match.Groups[1].Value)).ToHashSet();
        foreach (var hook in Hooks) Assert.Contains(hook, declared);   // a pinned hook that is gone (renamed or moved) fails here: update Hooks
        Assert.Contains(sources, s => s.Path == "src/SmSim/ContentExtras.cs" && s.Lines.Any(l => SymbolCondition.IsMatch(l)));
    }

    [Theory]
    [InlineData("using SmSim.Extras.Gore;", true)]
    [InlineData("var g = SmContent.Extras.Gore.GoreTuning.Load(dir);", true)]
    [InlineData("using Extras.Gore;", true)]
    [InlineData("using G = Extras.Gore.GoreSpec;", true)]
    [InlineData("namespace Extras.Gore;", true)]
    [InlineData("var spec = Extras.Gore.GoreSpec.Default;", true)]
    [InlineData("if (Config.Extras.HasFlag(ContentExtras.Gore)) return;", false)]
    [InlineData("if (Extras.HasFlag(ContentExtras.Orks)) return;", false)]
    [InlineData("w.Write((uint)Extras); w.Write(ContentExtras.WireVersion);", false)]
    [InlineData("var e = tuning.Config.Extras;", false)]
    public void The_namespace_patterns_tell_namespaces_from_member_access(string line, bool flagged) =>
        Assert.Equal(flagged, ExtrasNamespace.IsMatch(line) || BareExtrasNamespace.IsMatch(line));

    [Theory]
    [InlineData("ExtrasSend();", true)]
    [InlineData("        ExtrasHash(w);", true)]
    [InlineData("this.ExtrasFrame(delta);", true)]
    [InlineData("var u = new ExtrasUpdate(5, [(1, [])]);", false)]
    [InlineData("public sealed record ExtrasUpdate(uint ServerTick, (byte Tag, byte[] Payload)[] Sections) : Message", false)]
    [InlineData("public readonly record struct ExtrasSection(uint ServerTick, byte Tag, byte[] Payload);", false)]
    [InlineData("var q = client.TakeExtras();", false)]
    [InlineData("server.SendExtras(update);", false)]
    public void The_hook_call_pattern_tells_hook_calls_from_the_envelope_types(string line, bool flagged) =>
        Assert.Equal(flagged, HookCall.IsMatch(line));

    [Fact]
    public void No_core_file_names_an_extras_namespace()
    {
        var offenders = Sources().Where(s => !InExtras(s.Path))
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l))
                .Where(x => !IsComment(x.Text) && (ExtrasNamespace.IsMatch(x.Text) || BareExtrasNamespace.IsMatch(x.Text))))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(offenders.Count == 0, "added-content namespaces named outside an Extras/ folder:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Hooks_are_declared_only_as_pinned_and_called_with_the_marker()
    {
        var sources = Sources().Where(s => !InExtras(s.Path)).ToList();
        var declared = sources
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l, Match: HookDeclaration.Match(l))).Where(x => !IsComment(x.Text) && x.Match.Success))
            .Where(x => !Hooks.Contains((x.Path, x.Match.Groups[1].Value)))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(declared.Count == 0, "Extras* hooks declared in core code that are not pinned (add (file, hook) to Hooks with a note):\n" + string.Join("\n", declared));
        var unmarked = sources
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l)))
            .Where(x => !IsComment(x.Text) && !HookDeclaration.IsMatch(x.Text) && HookCall.IsMatch(x.Text) && !x.Text.Contains("// EXTRAS-HOOK", StringComparison.Ordinal))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(unmarked.Count == 0, "Extras* hook calls without the // EXTRAS-HOOK marker:\n" + string.Join("\n", unmarked));
    }

    [Fact]
    public void The_build_symbol_is_tested_only_in_the_switch_and_the_composition_roots()
    {
        var offenders = Sources()
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l)).Where(x => SymbolCondition.IsMatch(x.Text)))
            .Where(x => !SymbolFiles.Contains(x.Path))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(offenders.Count == 0, "SM_EXTRAS tested outside ContentExtras.cs and the composition roots (use ContentExtras.Compiled):\n" + string.Join("\n", offenders));
    }
}
