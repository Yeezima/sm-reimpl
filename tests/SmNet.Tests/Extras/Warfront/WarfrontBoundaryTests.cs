using System.Text.RegularExpressions;

namespace SmNet.Tests.Extras.Warfront;

/// <summary>
/// Warfront W1 (docs/added-content.md section 11): the source boundary of the Warfront folders, as text. No <c>.cs</c> file outside an
/// <c>Extras/Warfront/</c> folder names an <c>Extras.Warfront</c> namespace (the hash dispatcher <c>SimConfig.Extras.cs</c>, which writes
/// <c>WarfrontRules.Version</c> under <c>#if SM_WARFRONT</c>, is the one exception), and the list of Warfront hooks is pinned (W3's sixteen, W3b's fifteen more).
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
        // W5a: the server's stage flow
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontCreated"),
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontSend"),
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontCommand"),
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontPlayerCap"),
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontRestartDue"),
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontSnapshotRecords"),   // Phase B T2b (protocol 94): a crowded snapshot's order, the AI Marines lean
        ("src/SmServer/Extras/GameServer.Extras.cs", "WarfrontScoreRank"),         // Phase B T2b: the pod Marines' scoreboard rows last
        ("src/SmServer/Extras/Musou/Campaign/GameServer.Tfw.cs", "WarfrontGeneratedMap"),
        ("src/SmServer/Extras/Musou/Campaign/GameServer.Tfw.cs", "WarfrontMapNote"),
        ("src/SmServer/Extras/Musou/GameServer.TfwMap.cs", "WarfrontNav"),
        // W5b: the client's stage flow (the band view's three hooks W2 wrote are declared beside their call sites too, so a build without Warfront still compiles the calls)
        ("client/Extras/NetworkView.Extras.cs", "WarfrontMapContent"),
        ("client/Extras/NetworkView.Extras.cs", "WarfrontMapVisuals"),
        ("client/Extras/NetworkView.Extras.cs", "WarfrontEnvelope"),
        ("client/Extras/NetworkView.Extras.cs", "WarfrontFrame"),
        ("client/Extras/Musou/Tfw/NetworkView.Tfw.cs", "WarfrontBandFrame"),
        ("client/Extras/Musou/Tfw/NetworkView.TfwMap.cs", "WarfrontIsGeneratedMap"),
        ("client/Extras/Musou/Tfw/TfwStations.cs", "WarfrontOpenWarMap"),
        ("src/SmServer/Extras/Musou/GameServer.Musou.cs", "WarfrontViewCentre"),
        ("src/SmSim/Extras/Musou/Campaign/World.Tfw.cs", "WarfrontStep"),
        ("src/SmSim/Extras/Musou/Campaign/World.Tfw.cs", "WarfrontKill"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontBattleChange"),
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.Coop.cs", "WarfrontJoinLate"),
        ("src/SmSim/Extras/Musou/World.MusouPowerups.cs", "WarfrontAttackerScales"),
        ("src/SmSim/Extras/Musou/World.MusouPowerups.cs", "WarfrontAttackerScale"),
        ("src/SmSim/Extras/Musou/World.MusouPowerups.cs", "WarfrontMeleeCap"),
        ("src/SmSim/Extras/Musou/World.Musou.cs", "WarfrontDamageTaken"),
        ("src/SmSim/Extras/Musou/World.Musou.cs", "WarfrontInputLocked"),
        ("client/Extras/FrontEndView.Extras.cs", "WarfrontShowEntry"),   // 2026-11-19: opens the WARFRONT page (dispatched from ExtrasShowEntry)
        ("client/Extras/FrontEndView.Extras.cs", "WarfrontMenuRows"),   // W16: the main menu's row (dispatched from ExtrasMenuRows)
        ("client/Extras/HostPanel.Extras.cs", "WarfrontHostRowShown"),   // weather W3: Warfront's Host rows, dispatched from ExtrasHostRowShown
        ("client/Extras/HostPanel.Extras.cs", "WarfrontFactionRowShown"),   // Chaos Warfront C1: the FACTION row only on the Warfront profile, dispatched from ExtrasHostRowShown
        ("src/SmSim/Extras/Musou/World.Musou.cs", "WarfrontSquadHit"),
        ("src/SmSim/Extras/Musou/Campaign/TfwFire.cs", "WarfrontCooldown"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwSupply.cs", "WarfrontCooldown"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwGuards.cs", "WarfrontTargets"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwGuards.cs", "WarfrontFought"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontStuck"),
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontDamaged"),
        // W13b: the battle plan, the defence's opening, retake and source gates
        ("src/SmSim/Extras/Musou/Campaign/TfwRun.cs", "WarfrontBattlePlan"),
        // Guardsmen depth (owner 2026-11-20): twice the Guardsmen, the Orks' favour, the rows
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontGuardScale"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontFinalFieldHook"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontFormation"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontOnGoal"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.Armies.cs", "WarfrontGuardWorth"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwGuards.cs", "WarfrontGuardRulesHook"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontStart"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontRetake"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontSourceActive"),
        // W5c: the client's hooks 2 (the two core lines are the EXTRAS-HOOK ExtrasPickerBusy and ExtrasSpectateView that call the first two)
        ("client/Extras/NetworkView.Extras.cs", "WarfrontPickerBusy"),
        ("client/Extras/NetworkView.Extras.cs", "WarfrontSpectateView"),
        ("client/Extras/Musou/Tfw/TfwStations.cs", "WarfrontPeekWarMap"),
        ("client/Extras/Musou/Tfw/TfwStations.cs", "WarfrontCustomizerOpened"),
        ("client/Extras/Musou/Tfw/TfwStations.cs", "WarfrontCustomizerClosed"),
        ("client/Extras/Musou/NetworkView.MusouPowerups.cs", "WarfrontCarry"),
        ("client/Extras/Musou/Customizer/CreatorView.Musou.cs", "WarfrontRowAction"),
        ("client/Extras/Musou/Customizer/CreatorView.Musou.cs", "WarfrontLibrarianRows"),
        ("client/Extras/Musou/Customizer/CreatorView.Musou.Armoury.cs", "WarfrontArmourySlots"),   // the armoury RETAINERS card of a Librarian (armoury-retainers-row, 2026-11-25)
        // 2026-11-18: Warfront melee cleave (server and the client's drawn arc)
        ("src/SmSim/Extras/Musou/World.MusouCombat.cs", "WarfrontCleave"),
        ("client/Extras/Musou/NetworkView.MusouSlashArc.cs", "WarfrontCleaveScaleFor"),
        // W13e: the stranded failsafe's "near" (an Ork inside its goal's ring has arrived)
        ("src/SmSim/Extras/Musou/MusouSim.Ai.cs", "WarfrontHeadwayNear"),
        // 2026-11-25: no Squigs in Warfront (owner: "temporarily remove squigs from warfront mode"; WarfrontRules.NoSquigs)
        ("src/SmSim/Extras/Musou/Campaign/World.TfwSquigs.cs", "WarfrontSquigs"),
        // Chaos Warfront C0 seams (2026-11-25; no-op until the Chaos faction's bodies in Extras/Warfront/): the sim's kind, damage gate, numbers and Ork-only spawners (the Squigs keep WarfrontSquigs)
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontKind"),
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontDamageGate"),
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontStaggerGate"),
        ("src/SmSim/Extras/Musou/MusouSim.cs", "WarfrontRuleKind"),   // Chaos Warfront C2 review R3: the 'Ard slot's Chaos Marine follows the 'Ard Boy's rule rows   // Chaos Warfront C2 (C0 review finding 4): the gate covers the staggers, the bosses' melee stun and the fodder throw too
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontEnemyScale"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontOrkKind"),
        // Chaos Warfront C5 (2026-11-25): the Chaos Lord on the Warboss's boss code (his rules, his duel kit; no-ops outside a Chaos fortress's duel)
        ("src/SmSim/Extras/Musou/MusouBoss.cs", "WarfrontBossRules"),
        ("src/SmSim/Extras/Musou/MusouBoss.cs", "WarfrontBossStep"),
        // ... and the client's: the Host panel's faction row and the presentation tables (looks, sounds, gore, flinch)
        ("client/Extras/HostPanel.Extras.cs", "WarfrontHostFactionRow"),
        ("client/Extras/Musou/MusouLooks.cs", "WarfrontLookFor"),
        ("client/Extras/Musou/NetworkView.MusouSounds.cs", "WarfrontSoundSetOf"),
        ("client/Extras/Musou/Gore/NetworkView.MusouGore.cs", "WarfrontGoreChest"),
        ("client/Extras/Musou/Gore/NetworkView.MusouGore.cs", "WarfrontGoreOrk"),
        ("client/Extras/Musou/Gore/NetworkView.MusouGore.cs", "WarfrontGorePlan"),
        ("client/Extras/Musou/MusouCrowd.cs", "WarfrontFlinchReacts"),
        ("client/Extras/Musou/MusouCrowd.cs", "WarfrontFlinchSize"),
        // Chaos Warfront C3: the crowd test stage with the Chaos army's looks (test aid --warfront-chaos)
        ("client/Extras/Musou/MusouCrowdTestView.cs", "WarfrontCrowdTest"),
        // 2026-11-25: walk-over pickups (Warfront only; weapons stay on E)
        ("src/SmSim/Extras/Musou/World.MusouDrops.cs", "WarfrontWalkOver"),
        ("src/SmSim/Extras/Musou/World.MusouDrops.cs", "WarfrontWalksOver"),
        ("client/Extras/Musou/MusouDrops.cs", "WarfrontDropWalksOver"),
        ("client/Extras/Musou/MusouDrops.cs", "WarfrontDropPicked"),
        ("client/Extras/Musou/MusouDrops.cs", "WarfrontDropLive"),
        ("client/Extras/NetworkView.Extras.cs", "WarfrontPickupWalkOver"),
        // series-37 integration: executions X4 phase 2 asks Warfront for the army (no executions on a Chaos stage; the marker hides in a Chaos run)
        ("src/SmSim/Extras/Executions/Orks/World.ExecutionsOrks.cs", "WarfrontChaosArmy"),
        ("client/Extras/Executions/NetworkView.ExecutionInput.cs", "WarfrontChaosArmyShown"),
        // executions X4b (2026-11-26): a Chaos stage's kinds (the roster key of each type byte) and the finale duel's gate (only the duelist, only the Lord under a tenth), on the server and the client's marker
        ("src/SmSim/Extras/Executions/Orks/World.ExecutionsChaos.cs", "WarfrontChaosUnit"),
        ("src/SmSim/Extras/Executions/Orks/World.ExecutionsChaos.cs", "WarfrontDuelGate"),
        ("client/Extras/Executions/NetworkView.ExecutionInput.cs", "WarfrontChaosUnitShown"),
        ("client/Extras/Executions/NetworkView.ExecutionInput.cs", "WarfrontDuelGateShown"),
        // Phase B T2 (2026-11-25): the retainers as World players (MarineRole.Squad, which only Warfront spawns): their brain, their tick, their damage taken, the owner's answers on the human-only scales, traps and boss stuns
        ("src/SmSim/Extras/Musou/Campaign/World.Tfw.cs", "WarfrontPuppetInput"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwPuppets.cs", "WarfrontSquadMarineStep"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwSupply.cs", "WarfrontSquadDamage"),
        ("src/SmSim/Extras/Musou/World.MusouCombat.cs", "WarfrontScalesPlayer"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwMeks.cs", "WarfrontTrapAiMarine"),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwDropPod.cs", "WarfrontPodIds"),   // Phase B T2b: Warfront's pods take the pod id range
        ("src/SmSim/Extras/Musou/MusouSim.BossStun.cs", "WarfrontBossStunner"),
        // T2 review fixes (2026-11-25): retainers never capture; their melee reach is a human's (the cleave trim)
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontNoCapture"),
        ("src/SmSim/Extras/Musou/World.MusouCombat.cs", "WarfrontCleaveTrim"),
        // Phase B T5 (2026-11-25): the client's presentation of the retainers as players: the arc's human-only trim follows the server's switch, and a retainer by a landing pod is not the pod's (no battle cry)
        ("client/Extras/Musou/NetworkView.MusouSlashArc.cs", "WarfrontArcHuman"),
        ("client/Extras/Musou/Tfw/NetworkView.TfwDropPod.cs", "WarfrontNotPodMarine"),
        // Phase B T5 follow-up (2026-11-25): a retainer's hit event ranks with a human's; a retainer wears its current owner's look, a Librarian's squad only his colours
        ("src/SmNet/Extras/Musou/MusouSend.cs", "WarfrontHitRank"),
        ("src/SmServer/Extras/Musou/Campaign/GameServer.TfwLook.cs", "WarfrontPuppetWearer"),
        ("src/SmServer/Extras/Musou/Campaign/GameServer.TfwLook.cs", "WarfrontColoursOnly"),
        // Guardsmen G1 (2026-11-26): the line step, the retreat's fall-back place, the lane battle's rally, the reinforcement weight and the arrival note (no-ops outside a Warfront lane battle; WfLine)
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontLine"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontLineRows"),   // G5 F5
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.Retreats.cs", "WarfrontGuardFallBack"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.Retreats.cs", "WarfrontRallies"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.Garrisons.cs", "WarfrontReinforceNeed"),
        ("src/SmSim/Extras/Musou/Campaign/TfwBattle.Garrisons.cs", "WarfrontReinforced"),
    ];

    /// <summary>
    /// Phase B T2: every change the retainer spine made in a Musou file is a marked Warfront line (a hook call, its declaration, or the one enum value only Warfront spawns), pinned by its text so a later edit that
    /// drops the marker or adds an unmarked Warfront use fails here. Musou Mode never spawns <c>MarineRole.Squad</c> and the hooks return at once without Warfront's retainers.
    /// </summary>
    [Theory]
    [InlineData("src/SmSim/Extras/Musou/Campaign/MarineBrain.cs", "Squad = 3 }   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/World.Tfw.cs", "WarfrontPuppetInput(id, puppet, ref input, ref handled);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/World.TfwPuppets.cs", "if (m.Role == MarineRole.Squad) { WarfrontSquadMarineStep(m, p); continue; }   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/World.TfwSupply.cs", "WarfrontSquadDamage(target, ref own);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/World.MusouCombat.cs", "WarfrontScalesPlayer(playerId, ref scales);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/World.TfwMeks.cs", "WarfrontTrapAiMarine(id, ref aiMarine);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/World.TfwDropPod.cs", "WarfrontPodIds(ref own);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/MusouSim.BossStun.cs", "WarfrontBossStunner(attacker, ref aiMarine);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/World.Musou.cs", "WarfrontOrkDamage(attack.Enemy, attack.Player, ref damage);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontNoCapture(ref noCapture);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/World.MusouCombat.cs", "WarfrontCleaveTrim(attacker, ref trim);   // WARFRONT-HOOK")]
    [InlineData("client/Extras/Musou/NetworkView.MusouSlashArc.cs", "WarfrontArcHuman(player, ref human); return human; }   // WARFRONT-HOOK")]
    [InlineData("client/Extras/Musou/Tfw/NetworkView.TfwDropPod.cs", "WarfrontNotPodMarine(id, ref not); return not; }   // WARFRONT-HOOK")]
    [InlineData("src/SmNet/Extras/Musou/MusouSend.cs", "if (e.Kind == MusouEventKind.Hit && rank == 4) WarfrontHitRank(e.Player, ref rank);   // WARFRONT-HOOK")]
    [InlineData("src/SmServer/Extras/Musou/Campaign/GameServer.TfwLook.cs", "WarfrontPuppetWearer(playerId, ref wearer, ref ownPieces);   // WARFRONT-HOOK")]
    [InlineData("src/SmServer/Extras/Musou/Campaign/GameServer.TfwLook.cs", "if (!ownPieces) WarfrontColoursOnly(ref code);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "WarfrontLine();   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.cs", "if (alive == 0) { WarfrontLineRows(); return; }   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.Retreats.cs", "WarfrontGuardFallBack(a, ref place);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.Retreats.cs", "WarfrontRallies(ref skip);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.Garrisons.cs", "WarfrontReinforceNeed(k, need[k], ref weight[k]);   // WARFRONT-HOOK")]
    [InlineData("src/SmSim/Extras/Musou/Campaign/TfwBattle.Garrisons.cs", "WarfrontReinforced(a, k);   // WARFRONT-HOOK")]
    public void The_retainer_spines_Musou_lines_are_marked_Warfront_hooks(string file, string line)
    {
        var lines = Sources().First(s => s.Path == file).Lines;
        Assert.Contains(lines, l => l.Contains(line, StringComparison.Ordinal));
    }

    /// <summary>Phase B T2: the war's human-only scales still ask <c>TfwCombatRules.ScalesPlayer</c> only through the World's one wrapper (<c>MusouScalesPlayer</c>), so Warfront's answer reaches every use.</summary>
    [Fact]
    public void The_human_only_scales_ask_through_the_one_wrapper()
    {
        var direct = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            if (!path.StartsWith("src/SmSim/", StringComparison.Ordinal)) continue;
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && lines[i].Contains("TfwCombatRules.ScalesPlayer(", StringComparison.Ordinal) && !lines[i].Contains("bool scales = ", StringComparison.Ordinal)) direct.Add($"{path}:{i + 1}");
        }
        Assert.True(direct.Count == 0, "direct ScalesPlayer uses: " + string.Join(", ", direct));
    }

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
        var allowed = new HashSet<string> { "src/SmSim/ContentExtras.cs", "src/SmSim/Extras/SimConfig.Extras.cs", "src/SmTool/Program.cs", "src/SmServer/Program.cs" };   // SmTool: the warfront-* command routes
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && lines[i].Contains("SM_WARFRONT", StringComparison.Ordinal) && !allowed.Contains(path)) offenders.Add($"{path}:{i + 1}");
        Assert.True(offenders.Count == 0, "SM_WARFRONT outside its files: " + string.Join(", ", offenders));
    }

    /// <summary>W8 review 1b: the card picker is live on a human's client (W5c flipped it once <c>PickerBusy</c> existed); a flip back to false fails here.</summary>
    [Fact]
    public void The_card_picker_is_live()
    {
        var lines = Sources().First(s => s.Path == "client/Extras/Warfront/Progression/NetworkView.WarfrontProgression.cs").Lines;
        Assert.Contains(lines, l => !IsComment(l) && l.Contains("internal static bool WarfrontPickerLive = true;", StringComparison.Ordinal));
    }
}
