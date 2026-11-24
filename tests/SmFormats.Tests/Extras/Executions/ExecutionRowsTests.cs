using System.Numerics;
using SmContent;
using SmContent.Extras.Executions;
using SmFormats.Havok;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using Xunit.Abstractions;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests.Extras.Executions;

/// <summary>
/// Executions X7: the catalogue rows (<see cref="ExecutionRows.All"/>) as data: valid and complete per docs/executions-coverage.md, every row's clips resolved
/// on a synthetic catalogue (<see cref="ExecutionCatalogueLoader.Fill"/>; no original bytes), the eligibility the owner's answers set (kinds, weapon families,
/// the power sword on the chainsword's rows, the knife on Orks only, the Chaos run, Classic's race swap), and an install-only load of every row.
/// </summary>
public class ExecutionRowsTests(ITestOutputHelper output)
{
    static readonly ExecutionCatalogue Catalogue = ExecutionCatalogue.Create(ExecutionRows.All);

    static string[] Ids(IEnumerable<ExecutionRow> rows) => rows.Select(r => r.Id).ToArray();

    [Fact]
    public void Every_row_is_valid_unique_and_kept()
    {
        var notes = new List<string>();
        var c = ExecutionCatalogue.Create(ExecutionRows.All, notes);
        Assert.Empty(notes);
        Assert.Equal(25, c.Rows.Count);
        Assert.Equal(ExecutionRows.All.Count, c.Rows.Count);
        foreach (var r in ExecutionRows.All)
        {
            Assert.True(r.IsValid, r.Id);
            Assert.Equal("*", r.AttackerClass);
            Assert.True(r.Offset.Y > 0f, r.Id);
            Assert.Equal(MathF.PI, r.RelativeYaw);
            Assert.NotEmpty(r.AttackerVariants);
            Assert.Contains(r.AttackerVariants, v => v.Clip == r.AttackerClip && MathF.Abs(v.Distance - r.Offset.Length()) < 0.05f);   // the reference clip's authored gap is the offset
            Assert.True(r.KillSeconds > 0f && r.KillSeconds < r.Seconds, r.Id);
            Assert.NotNull(r.At(ExecutionEventKind.Sync)); Assert.NotNull(r.At(ExecutionEventKind.Contact)); Assert.NotNull(r.At(ExecutionEventKind.End));
            Assert.True(ExecutionRows.VictimRigs.ContainsKey(r.Target), r.Id);
            Assert.Equal(c.IndexOf(r.Id), ExecutionRows.All.ToList().IndexOf(r));   // the wire index is the data order
        }
        // Per kind and per family (docs/executions-coverage.md section 5, owner answers).
        Assert.Equal(new Dictionary<string, int> { ["ork_boy"] = 12, ["gretchin"] = 4, ["chaos_marine"] = 3, ["bloodletter"] = 3, ["renegade"] = 3 },
            c.Rows.GroupBy(r => r.Target).ToDictionary(g => g.Key, g => g.Count()));
        Assert.Equal(new Dictionary<string, int> { ["chainsword"] = 8, ["power_axe"] = 8, ["thunder_hammer"] = 6, ["combat_knife"] = 3 },
            c.Rows.GroupBy(r => r.Weapon).ToDictionary(g => g.Key, g => g.Count()));
        Assert.Equal(["bloodletter", "chaos_marine", "gretchin", "ork_boy", "renegade"], c.Kinds);
    }

    [Fact]
    public void Rows_order_by_the_original_conduit_priority()
    {
        Assert.Equal(["O1.chainsword", "O2.chainsword", "O3.chainsword", "O4.chainsword"], Ids(Catalogue.For("Assault", "sm_mp_melee_chainsword", ExecutionTargets.OrkBoy)));
        Assert.Equal(["O5.power_axe", "O4.power_axe", "O3.power_axe", "O6.power_axe"], Ids(Catalogue.For("Tactical", "sm_mp_melee_power_axe", ExecutionTargets.OrkBoy)));
        Assert.Equal(["O7.thunder_hammer", "O3.thunder_hammer"], Ids(Catalogue.For("Assault", "sm_mp_melee_2hand_thunder_hammer", ExecutionTargets.OrkBoy)));
        Assert.Equal(["O3.combat_knife", "O8.combat_knife"], Ids(Catalogue.For("Tactical", "sm_mp_melee_combat_knife", ExecutionTargets.OrkBoy)));
        Assert.Equal(["O9.combat_knife"], Ids(Catalogue.For("Tactical", "sm_mp_melee_combat_knife", ExecutionTargets.Gretchin)));
    }

    [Fact]
    public void Weapons_follow_the_owner_answers()
    {
        foreach (var kind in Catalogue.Kinds)
        {
            // The power sword plays exactly the chainsword's rows in every mode with executions.
            Assert.Equal(Ids(Catalogue.For("*", "sm_mp_melee_chainsword", kind)), Ids(Catalogue.For("*", "sm_mp_melee_powersword", kind)));
            Assert.Equal(Ids(Catalogue.For("*", "sm_mp_melee_power_axe", kind)), Ids(Catalogue.For("*", "mp_melee_chain_axe", kind)));
            Assert.Equal(Ids(Catalogue.For("*", "thunder_hammer", kind)), Ids(Catalogue.For("*", "csm_mp_melee_daemon_maul", kind)));
            foreach (var none in new[] { "displace_kick", "lightning_claw", "power_spear", "bolter" }) Assert.Empty(Catalogue.For("*", none, kind));
        }
        // The knife: Ork rows only.
        foreach (var kind in new[] { ExecutionTargets.ChaosMarine, ExecutionTargets.Bloodletter, ExecutionTargets.Renegade })
            Assert.Empty(Catalogue.For("*", "combat_knife", kind));
        // No row of the power sword's own or of a weapon that is no family; no row on a never-executed kind.
        Assert.DoesNotContain(Catalogue.Rows, r => r.Weapon.Contains("sword", StringComparison.OrdinalIgnoreCase) && r.Weapon != ExecutionWeapons.Chainsword);
        Assert.DoesNotContain(Catalogue.Rows, r => ExecutionTargets.Never.Contains(r.Target));
    }

    static MusouEnemy Ork(MusouEnemyType type) => new(7, type, MusouEnemyState.Seek, Vector3.Zero, Vector2.Zero, 0f, 100f, -1, 0, 0, 0);

    [Fact]
    public void Warfront_eligibility_follows_the_owner_answers()
    {
        var eligible = new[] { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.ArdBoy, MusouEnemyType.RokkitBoy, MusouEnemyType.Stormboy, MusouEnemyType.Gretchin };
        foreach (var t in Enum.GetValues<MusouEnemyType>())
            Assert.True(eligible.Contains(t) == ExecutionChoice.Eligible(Ork(t), Catalogue.Rows), $"{t}");   // Scrap Mek (no clip), Bomb Squig, elites and bosses never
        // A Stormboy plays the Boy's rows (owner); every eligible Ork has a row for every family.
        foreach (var t in eligible)
            foreach (var w in new[] { "chainsword", "power_axe", "thunder_hammer", "combat_knife", "powersword" })
                Assert.NotEmpty(Catalogue.For("*", w, ExecutionTargets.Of(t)!));
    }

    [Fact]
    public void The_chaos_run_and_classic_follow_the_owner_answers()
    {
        foreach (var (name, kind) in ExecutionTargets.ChaosRun)
            foreach (var w in new[] { "chainsword", "power_axe", "thunder_hammer", "powersword" })
                Assert.True(Catalogue.For("*", w, kind).Count > 0, $"{name} with {w}");
        Assert.Equal(3, Catalogue.Rows.Count(r => r.Target == ExecutionTargets.Bloodletter));
        Assert.Equal(["R1.chainsword", "R1.power_axe", "R1.thunder_hammer"], Ids(Catalogue.Rows.Where(r => r.Target == ExecutionTargets.Renegade)));
        foreach (var never in new[] { "chosen", "chaos_lord", "BombSquig" }) Assert.Null(ExecutionTargets.Of(never, ExecutionTargets.ChaosRun));
        // Classic: a Chaos victim takes C1 to C3; a loyalist the same rows with the races swapped (no loyalist rows of its own).
        Assert.Equal(["C1.chainsword"], Ids(Catalogue.For("*", "sm_mp_melee_chainsword", ExecutionTargets.ChaosMarine)));
        Assert.Equal(["C2.power_axe"], Ids(Catalogue.For("*", "mp_melee_chain_axe", ExecutionTargets.ChaosMarine)));
        Assert.Equal(["C3.thunder_hammer"], Ids(Catalogue.For("*", "csm_mp_melee_daemon_maul", ExecutionTargets.ChaosMarine)));
        Assert.Equal(["C1.chainsword"], Ids(Catalogue.For("*", "sm_mp_melee_powersword", ExecutionTargets.ChaosMarine)));
        Assert.Empty(Catalogue.For("*", "combat_knife", ExecutionTargets.ChaosMarine));
        Assert.DoesNotContain(Catalogue.Rows, r => r.Target == ExecutionTargets.SpaceMarine);
        Assert.Contains((ExecutionTargets.ChaosMarine, true), ExecutionTargets.Classic(Team.SpaceMarines));
        Assert.Equal([(ExecutionTargets.ChaosMarine, false)], ExecutionTargets.Classic(Team.Chaos));
    }

    // ---------------------------------------------------------------- every row's clips on a synthetic catalogue

    static readonly Bone[] Bones = [new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity), new("RIG_Head", 0, new Vector3(0f, 0f, 1.7f), Quaternion.Identity)];

    static Clip Synthetic(string name, float seconds) =>
        new(name, seconds, Math.Max(2, (int)(seconds * 30f) + 1), [new(Vector3.Zero, Quaternion.Identity), new(new Vector3(0f, 0f, 1.7f), Quaternion.Identity)],
            [new(0, 0, 0, 0), new(0, 1f, 0, 0)]);

    [Fact]
    public void Every_row_resolves_its_clips_on_a_synthetic_catalogue()
    {
        // The attacker rig: every attacker clip and variant; the graphs: each row's sequencer with its hit tag moved 0.1 s (the install's tag wins over the data).
        var attackerClips = ExecutionRows.All.SelectMany(r => r.AttackerVariants.Select(v => (v.Clip, r.AttackerSeconds)).Append((r.AttackerClip, r.AttackerSeconds)))
            .DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(x => Synthetic(x.Item1, x.Item2)).ToArray();
        var graphs = ExecutionRows.All.GroupBy(r => r.Graph).Select(g => new Graph(g.Key, ["ACTN_Hit_Primary_Target"],
            g.DistinctBy(r => r.Sequencer).Select(r => new Sequencer(r.Sequencer, [r.AttackerClip], [new Tag("Event", (r.KillSeconds + 0.1f) / r.AttackerSeconds, (r.KillSeconds + 0.1f) / r.AttackerSeconds, 0)])).ToArray())).ToArray();
        var attacker = new HavokCharacterFile(Build(Bones, attackerClips));
        var attackerGraphs = HavokBehavior.Read(new HavokPackfile(BuildBehavior(graphs)));
        var victims = ExecutionRows.All.GroupBy(r => r.Target).ToDictionary(g => g.Key,
            g => new HavokCharacterFile(Build(Bones, g.DistinctBy(r => r.VictimClip).Select(r => Synthetic(r.VictimClip, r.VictimSeconds)).ToArray())));
        var notes = new List<string>();
        var filled = new List<ExecutionRow>();
        foreach (var row in ExecutionRows.All)
        {
            var r = ExecutionCatalogueLoader.Fill(row, attacker, attackerGraphs, victims[row.Target], [], notes);
            Assert.True(r is not null, $"{row.Id}: {string.Join("; ", notes)}");
            Assert.True(r!.IsValid, row.Id);
            Assert.Equal(row.AttackerSeconds, r.AttackerSeconds, 2);
            Assert.Equal(row.VictimSeconds, r.VictimSeconds, 2);
            Assert.NotEmpty(r.AttackerRoot); Assert.NotEmpty(r.VictimRoot);
            Assert.Equal(row.AttackerVariants.Select(v => (v.Clip, v.Distance)), r.AttackerVariants.Select(v => (v.Clip, v.Distance)));   // the authored gaps are kept
            Assert.All(r.AttackerVariants, v => Assert.NotEmpty(v.Root));
            // The sequencer that plays the reference clip retimes the kill (the reference clip is the first of its sequencer here).
            if (attackerGraphs.Any(g => g.Name == row.Graph && g.Sequencers.Any(s => s.Name == row.Sequencer && s.Animations[0] == row.AttackerClip)))
                Assert.Equal(row.KillSeconds + 0.1f, r.KillSeconds, 2);
            filled.Add(r);
        }
        Assert.Equal(25, ExecutionCatalogue.Create(filled).Rows.Count);
        // A missing clip leaves the row out with a note, never a wrong clip.
        notes.Clear();
        Assert.Null(ExecutionCatalogueLoader.Fill(ExecutionRows.All[0] with { VictimClip = "Missing" }, attacker, attackerGraphs, victims[ExecutionTargets.OrkBoy], [], notes));
        Assert.Contains(notes, n => n.Contains(ExecutionRows.All[0].Id));
    }

    // ---------------------------------------------------------------- install only

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (reads the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_install_resolves_every_rows_clips()
    {
        var notes = new List<string>();
        var c = ExecutionCatalogueLoader.Load(Game.Value!, notes);
        foreach (var n in notes) output.WriteLine("note: " + n);
        foreach (var r in c.Rows)
            output.WriteLine($"{r.Id,-20} {r.AttackerSeconds:F3}/{r.VictimSeconds:F3} s kill {r.KillSeconds:F3} end {r.Seconds:F3} band {r.DistanceBand} " +
                             $"root {r.AttackerRoot.Count}/{r.VictimRoot.Count} end ({(r.AttackerRoot.Count > 0 ? r.AttackerRoot[^1].Y : 0f):F2})");
        Assert.Equal(ExecutionRows.All.Select(r => r.Id), c.Rows.Select(r => r.Id));
        foreach (var r in c.Rows)
        {
            var data = ExecutionRows.All.First(x => x.Id == r.Id);
            Assert.Equal(data.AttackerSeconds, r.AttackerSeconds, 1);
            Assert.Equal(data.VictimSeconds, r.VictimSeconds, 1);
            Assert.True(MathF.Abs(data.KillSeconds - r.KillSeconds) < 0.05f, $"{r.Id}: kill {r.KillSeconds} against {data.KillSeconds}");
            Assert.NotEmpty(r.AttackerRoot); Assert.NotEmpty(r.VictimRoot);
            Assert.All(r.AttackerVariants, v => Assert.NotEmpty(v.Root));
        }
    }
}
