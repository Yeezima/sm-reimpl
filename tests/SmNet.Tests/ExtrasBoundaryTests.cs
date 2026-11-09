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
        ("src/SmSim/ContentExtras.cs", "ExtrasAirJumps"),
        ("src/SmSim/ContentExtras.cs", "ExtrasJumpPackOwnerCapacity"),   // SimConfig.JumpPackOwnerCapacity: Musou keeps the 300 jump-pack capacity
        ("src/SmSim/ContentExtras.cs", "ExtrasNoBulletSpread"),    // SimConfig.ExtrasNoBulletSpread(ref bool), read by SimConfig.NoBulletSpread (musou/no-spread: Musou Mode's shots have no spread)          // SimConfig.ExtrasAirJumps(ref bool), read by SimConfig.JumpPackAirJumps (sim/air-jumps: Musou Mode keeps the jump-pack air jump)
        ("src/SmContent/GameplayTuning.cs", "ExtrasLoad"),         // GameplayTuning.ExtrasLoad(ref SimConfig, string, List<string>), called in LoadForMatch
        ("src/SmServer/GameServer.Envelope.cs", "ExtrasSend"),     // GameServer.ExtrasSend(), called once a tick after SendPickups (E3's envelope), only with added content
        ("client/NetworkView.cs", "ExtrasInput"),                  // NetworkView.ExtrasInput(ref PlayerInput), on every sampled live input (loyalist barks: the FOR THE EMPEROR key)
        ("client/NetworkView.cs", "ExtrasFrame"),                  // NetworkView.ExtrasFrame(PlayerState, IReadOnlyList<PlayerState>, double), once a frame after the sound (loyalist barks: barks and the key's shouts)
        ("client/NetworkView.Match.cs", "ExtrasKill"),             // NetworkView.ExtrasKill(KillView), each kill-feed entry once (loyalist barks: kill barks)
        ("client/NetworkView.Scoreboard.cs", "ExtrasScoreboard"),  // NetworkView.ExtrasScoreboard(ref single, ref score, ref assists), the Tab scoreboard (Musou: one column, run points, no assists)
        ("client/ModelViewer.cs", "ExtrasTestAid"),                // ModelViewer.ExtrasTestAid(args, ref bool), before the game install is located (Ashfall kit gallery: a test view that needs no install)
        // Musou (MU0): each returns at once unless World._musou is set (World.StartMusou, only under ContentExtras.Musou).
        ("src/SmSim/Simulation.cs", "ExtrasStep"),                 // World.ExtrasStep(), once a tick after ApplyPerkBlasts, before CountProjectilesOut (Musou: the enemies)
        ("src/SmSim/Simulation.cs", "ExtrasMelee"),                // World.ExtrasMelee(int, PlayerState, MeleeStep), at the end of ResolveMelee (Musou: the cleave)
        ("src/SmSim/Projectiles.cs", "ExtrasBlast"),               // World.ExtrasBlast(int, Vector3, SplashSpec, bool), at the end of ApplyBlast (Musou: blasts on enemies)
        ("src/SmSim/JumpWorld.cs", "ExtrasJump"),                  // World.ExtrasJump(int, JumpEventKind), at the end of ResolveJump (Musou: the air slash's landing heals; owner 2026-11-09)
        ("src/SmSim/Stims.cs", "ExtrasStimBoost"),                 // SimConfig.ExtrasStimBoost(ref StimBoost), Musou Mode's combat stims (x3 lifetime, x2 melee, +20 % speed, no ammunition; owner 2026-11-09)
        ("src/SmSim/Stims.cs", "ExtrasThrownBlastScale"),          // SimConfig.ExtrasThrownBlastScale(ThrownSpec?, ref float), Musou Mode's frag blast x2 (owner 2026-11-09)
        ("src/SmSim/Projectiles.cs", "ExtrasProjectileStop"),      // World.ExtrasProjectileStop(Vector3, Vector3, float, ref float), in Fly before a hit is taken (Musou S4: a shield dome stops a projectile from outside)
        ("src/SmSim/DamageScale.cs", "ExtrasDamageTaken"),         // World.ExtrasDamageTaken(int target, ref float scale), in DamageTakenScale at every player-damage site (Musou: AI Marines take a tenth)
        ("src/SmSim/Teams.cs", "ExtrasStepMatch"),                 // World.ExtrasStepMatch(ref bool handled), top of StepMatch; the core skips its flow when handled (Musou: one side)
        ("src/SmSim/Teams.cs", "ExtrasJoinSide"),                  // World.ExtrasJoinSide(ref Team, ref bool forced), in AssignClass / AssignSide (Musou: Space Marines only, no balance)
        ("src/SmServer/GameServer.cs", "ExtrasCreated"),           // GameServer.ExtrasCreated(), end of the constructor when Extras is not None (Musou: the nav graph, World.StartMusou)
        // Musou Mode (TF0): each returns at once unless World.Tfw is set (World.StartTfw, only on the map tfw_war under ContentExtras.Musou), or for another map.
        ("src/SmSim/Simulation.cs", "ExtrasSampled"),              // World.ExtrasSampled(int, in PlayerInput), after the input is sanitized (Musou: the Librarian reads its keys)
        ("src/SmSim/Simulation.cs", "ExtrasPlayerInput"),          // World.ExtrasPlayerInput(int, ref PlayerInput, ref bool handled), before Step asks for a player's input (the AI Marines have no client)
        ("src/SmServer/GameServer.cs", "ExtrasGeneratedMap"),      // static GameServer.ExtrasGeneratedMap(ContentExtras, string, ref string?, ref GameMap?, ref string?), in GeneratedMap, which Program.cs calls (tfw_war, composed from the install)
        ("src/SmServer/GameServer.cs", "ExtrasPlayerName"),        // GameServer.ExtrasPlayerName(int, ref string), in ScoreboardOf (the AI Marines' names)
        ("src/SmServer/GameServer.Envelope.cs", "ExtrasCommand"),  // GameServer.ExtrasCommand(int, byte, byte[]), a client's command (message kind 17), only under ContentExtras.CommandUsers
        ("client/NetworkView.cs", "ExtrasMapFile"),                // NetworkView.ExtrasMapFile(ref string), the install's map behind a map name (tfw_war_m09: Manufactorum; tfw_war: none), in LoadMap and for the map's effects and sounds
        ("src/SmServer/GameServer.Appearance.cs", "ExtrasPuppetAppearance"),   // GameServer.ExtrasPuppetAppearance(int, ref (ushort, Race, AppearanceCode)?), in PeerlessLook (scoreboard version, appearance query): Musou Mode's retainers wear their owner's look
        ("client/NetworkView.MeleeFx.cs", "ExtrasSwordOverlay"),    // NetworkView.ExtrasSwordOverlay(PlayerState, WeaponModel, ref ShaderMaterial?), the power sword glow (Musou: the Librarian force sword)
        ("client/NetworkView.MeleeHitSound.cs", "ExtrasMeleeTarget"),   // NetworkView.ExtrasMeleeTarget(PlayerState, MeleeStep, ref Vector3?), the own strike's hit sound: Musou's enemies in reach
        ("client/NetworkView.MeleeHitSound.cs", "ExtrasOwnStrikeContact"),   // NetworkView.ExtrasOwnStrikeContact(PlayerState, MeleeStep), called from MeleeFxFor at the own strike's hit: Musou's predicted contact (combat feel 2026-11-09)
        ("client/NetworkView.Fx.cs", "ExtrasOwnShotEnd"),   // NetworkView.ExtrasOwnShotEnd(WeaponSpec, Vector3, Vector3, ref float, ref bool), in TraceOwn: Musou's Orks stop the drawn own shot
        ("client/NetworkView.Fx.cs", "ExtrasOwnShotContact"),   // NetworkView.ExtrasOwnShotContact(PlayerState, WeaponSpec, Vector3, Vector3, Vector3), in FxPlayOwnShot: the predicted shot's impact and hit sound on an Ork
        ("client/NetworkView.Fx.cs", "ExtrasShakeTotal"),   // NetworkView.ExtrasShakeTotal(float, float, ref float), in FxShake: Musou's shakes do not stack
        ("src/SmSim/WorldBodies.cs", "ExtrasRecordBodies"),   // World.ExtrasRecordBodies(), each tick: added content's solid bodies (Musou's big Orks, musou/boss-collision)
        ("src/SmSim/WorldBodies.cs", "ExtrasBodies"),   // World.ExtrasBodies(uint, ref BodyView?): those bodies in a player's movement collision
        ("src/SmSim/WorldBodies.cs", "ExtrasLockView"),   // World.ExtrasLockView(uint, in PlayerState, ref IReadOnlyDictionary<int, PlayerState>): the melee lock's extra targets (the big Orks)
        ("client/NetworkView.Appearance.cs", "ExtrasLook"),        // NetworkView.ExtrasLook(int, ref Func<ResolvedAppearance, ResolvedAppearance>?), in LookFor before the resolver: an added class's pieces over the look (Musou: the Librarian's armour)
        ("client/NetworkView.Loadout.cs", "ExtrasMatchMenu"),      // NetworkView.ExtrasMatchMenu(MatchMenu), when the Esc menu is built: an added mode's own rows (Musou Mode: the host's BACK TO STORMBIRD during a battle)
        ("client/NetworkView.MeleeHitSound.cs", "ExtrasMeleeHitEmitter"),   // NetworkView.ExtrasMeleeHitEmitter(SoundEmitter), in PostDataMeleeHit: Musou's melee hit voices play to their end and ring out (musou/audio-pass)
        ("client/NetworkView.Sound.cs", "ExtrasTrackEmitter"),   // NetworkView.ExtrasTrackEmitter(SoundEmitter, bool), in Track: Musou protects the own character's voice lines (musou/audio-pass)
        ("client/NetworkView.Sound.cs", "ExtrasFireLayers"),   // NetworkView.ExtrasFireLayers(WeaponSpec, SoundEmitter, bool), in PostFire: Musou's bolter rifles fire deeper (musou/audio-pass)
        ("client/NetworkView.Sound.cs", "ExtrasShotMaterial"),     // NetworkView.ExtrasShotMaterial(ShotView, int, ref bool?), in SoundShot: the material of another player's ranged hit (Musou: armoured Orks armour and flesh)
        ("client/NetworkView.cs", "ExtrasMapCollision"),           // NetworkView.ExtrasMapCollision(string, ref CollisionWorld, ref ulong), in LoadMap after the level's collision (tfw_war: the Stormbird and the composed hash)
        ("client/NetworkView.cs", "ExtrasMapContent"),             // NetworkView.ExtrasMapContent(string, ref MapContent?), in LoadMap before any file read (tfw_war: Ashfall Citadel, generated)
        ("client/NetworkView.cs", "ExtrasMapVisuals"),             // NetworkView.ExtrasMapVisuals(string, MapContent, ref bool), in LoadMap for a generated map (tfw_war: the graybox view)
        // Musou Mode co-op (2026-11-06): up to 4 players.
        ("src/SmServer/GameServer.cs", "ExtrasPlayerCap"),         // GameServer.ExtrasPlayerCap(ref int), in PlayerCap when Extras is not None (Musou: at most 4 players)
        ("client/NetworkView.cs", "ExtrasBotInput"),               // NetworkView.ExtrasBotInput(PlayerState, uint, ref PlayerInput?), in BotInput after the countdown (Musou Mode: the co-op bot fights the Orks)
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
