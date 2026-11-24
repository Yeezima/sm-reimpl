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
            Assert.Equal(c.WireIndexOf(r.Id), ExecutionRows.All.ToList().IndexOf(r));   // the wire index is the data order
            Assert.Same(r, c.AtWire(c.WireIndexOf(r.Id)));
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
        Assert.Equal(ExecutionTargets.ChaosMarine, ExecutionTargets.Of("chaos_raptor", ExecutionTargets.ChaosRun));   // grounded only (owner 2026-11-25; X4b's check)
        // Classic: a Chaos victim takes C1 to C3; a loyalist the same rows with the races swapped (no loyalist rows of its own).
        Assert.Equal(["C1.chainsword"], Ids(Catalogue.For("*", "sm_mp_melee_chainsword", ExecutionTargets.ChaosMarine)));
        Assert.Equal(["C2.power_axe"], Ids(Catalogue.For("*", "mp_melee_chain_axe", ExecutionTargets.ChaosMarine)));
        Assert.Equal(["C3.thunder_hammer"], Ids(Catalogue.For("*", "csm_mp_melee_daemon_maul", ExecutionTargets.ChaosMarine)));
        Assert.Equal(["C1.chainsword"], Ids(Catalogue.For("*", "sm_mp_melee_powersword", ExecutionTargets.ChaosMarine)));
        Assert.Empty(Catalogue.For("*", "combat_knife", ExecutionTargets.ChaosMarine));
        Assert.DoesNotContain(Catalogue.Rows, r => r.Target == ExecutionTargets.SpaceMarine);
        Assert.Contains((ExecutionTargets.ChaosMarine, true), ExecutionTargets.Classic(Team.SpaceMarines));
        Assert.Equal([(ExecutionTargets.ChaosMarine, false)], ExecutionTargets.Classic(Team.Chaos));
        // By race (lead decision): a loyalist takes the Chaos Marine rows swapped, a Chaos player his own, an Ork player the Ork Boy rows; Ork players never execute.
        Assert.Contains((ExecutionTargets.ChaosMarine, true), ExecutionTargets.Classic(Race.SpaceMarines));
        Assert.Equal([(ExecutionTargets.ChaosMarine, false)], ExecutionTargets.Classic(Race.Chaos));
        foreach (var (kind, _) in ExecutionTargets.Classic(Race.Orks))
            foreach (var w in new[] { "chainsword", "power_axe", "thunder_hammer", "combat_knife", "powersword" })
                Assert.NotEmpty(Catalogue.For("*", w, kind));
        Assert.Contains(ExecutionTargets.Classic(Race.Orks), k => k.Kind == ExecutionTargets.OrkBoy);
        Assert.False(ExecutionTargets.CanExecute(Race.Orks));
        Assert.True(ExecutionTargets.CanExecute(Race.SpaceMarines) && ExecutionTargets.CanExecute(Race.Chaos));
        // The daemon maul's MP key (csm_mp_melee_2hand_power_mace) plays the hammer's rows.
        Assert.Equal(["C3.thunder_hammer"], Ids(Catalogue.For("*", "csm_mp_melee_2hand_power_mace", ExecutionTargets.ChaosMarine)));
        // Every row names its victim sequencer.
        Assert.All(ExecutionRows.All, r => Assert.False(string.IsNullOrEmpty(r.VictimSequencer), r.Id));
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

    /// <summary>Every row filled on the synthetic catalogue (each clip 1 m of forward extracted motion): playable rows with root paths, no original bytes.</summary>
    static IReadOnlyList<ExecutionRow> SyntheticFilled()
    {
        var attackerClips = ExecutionRows.All.SelectMany(r => r.AttackerVariants.Select(v => (v.Clip, r.AttackerSeconds)).Append((r.AttackerClip, r.AttackerSeconds)))
            .DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(x => Synthetic(x.Item1, x.Item2)).ToArray();
        var attacker = new HavokCharacterFile(Build(Bones, attackerClips));
        var victims = ExecutionRows.All.GroupBy(r => r.Target).ToDictionary(g => g.Key,
            g => new HavokCharacterFile(Build(Bones, g.DistinctBy(r => r.VictimClip).Select(r => Synthetic(r.VictimClip, r.VictimSeconds)).ToArray())));
        return ExecutionRows.All.Select(r => ExecutionCatalogueLoader.Fill(r, attacker, [], victims[r.Target], [])!).ToArray();
    }

    static StaticMesh Quads(params Vector3[][] quads)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        foreach (var q in quads) { int i = v.Count; v.AddRange(q); ix.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
        return new StaticMesh(v.ToArray(), ix.ToArray());
    }

    static readonly Vector3[] FloorQuad = [new(-30, -30, 0), new(30, -30, 0), new(30, 30, 0), new(-30, 30, 0)];

    static CollisionWorld OpenFloor() => new(Quads(FloorQuad), Quads(FloorQuad));

    [Fact]
    public void The_wire_index_is_the_rows_place_in_the_data_and_a_missing_row_plays_nothing()
    {
        // An install without O2's clips: O2 is left out, every other row keeps its index, and O2's index resolves to nothing (no execution, the normal kill).
        var all = ExecutionRows.All;
        var c = ExecutionCatalogue.Create(all.Where(r => r.Id != "O2.chainsword"), null, all.Select(r => r.Id).ToArray());
        Assert.Equal(all.Count - 1, c.Rows.Count);
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Id == "O2.chainsword") { Assert.Null(c.AtWire(i)); Assert.Equal(-1, c.WireIndexOf(all[i].Id)); continue; }
            Assert.Equal(i, c.WireIndexOf(all[i].Id));
            Assert.Same(all[i], c.AtWire(i));
        }
        Assert.Null(c.AtWire(-1)); Assert.Null(c.AtWire(all.Count));
        Assert.NotEqual(c.IndexOf("O3.chainsword"), c.WireIndexOf("O3.chainsword"));   // the compacted place is not the wire index
        // The hash tells the two catalogues apart.
        static byte[] Hash(ExecutionCatalogue x) { using var m = new MemoryStream(); using (var w = new BinaryWriter(m)) x.Write(w); return m.ToArray(); }
        Assert.NotEqual(Hash(ExecutionCatalogue.Create(all)), Hash(c));
        // The loader keeps the wire order of ExecutionRows.All when rows are left out (here: no install at all, every row left out).
        var none = ExecutionCatalogueLoader.Load(Path.Combine(Path.GetTempPath(), "sm-reimpl-no-install-" + Guid.NewGuid().ToString("N")), []);
        Assert.Empty(none.Rows);
        Assert.Equal(all.Select(r => r.Id), none.WireIds);
    }

    [Fact]
    public void The_real_rows_run_through_the_warfront_and_classic_choosers()
    {
        var rows = SyntheticFilled();
        Assert.All(rows, r => Assert.True(r.IsValid && r.AttackerRoot.Count > 0, r.Id));
        var c = new SimConfig();
        var level = OpenFloor();
        var marine = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines };
        // Warfront: a Boy 2 m ahead takes the chainsword's first row (O1, conduit priority 4); a Gretchin the punch; the knife O3 first.
        MusouEnemy At(ushort id, MusouEnemyType t, float y) => new(id, t, MusouEnemyState.Seek, new Vector3(0f, y, 0f), Vector2.Zero, MathF.PI, 100f, -1, 0, 0, 0);
        var (id, r) = ExecutionChoice.ChooseAlignedOrk(marine, 0f, "tactical", "sm_mp_melee_chainsword", [At(4, MusouEnemyType.Boy, 2f)], rows, c, level);
        Assert.Equal((4, true, "O1.chainsword"), ((int)id, r.Ok, r.Row?.Id));
        Assert.Equal(new Vector3(0f, 2f, 0f), r.VictimAt);   // the victim's feet never move
        var grot = ExecutionChoice.ChooseAlignedOrk(marine, 0f, "tactical", "sm_mp_melee_combat_knife", [At(5, MusouEnemyType.Gretchin, 2f)], rows, c, level);
        Assert.Equal((5, "O9.combat_knife"), ((int)grot.Id, grot.Result.Row?.Id));
        var knife = ExecutionChoice.ChooseAlignedOrk(marine, 0f, "tactical", "sm_mp_melee_combat_knife", [At(6, MusouEnemyType.ArdBoy, 2f)], rows, c, level);
        Assert.Equal("O3.combat_knife", knife.Result.Row?.Id);   // the Ard Boy plays the Boy rows only (owner)
        // The axe at 0.8 m: the decap backs off 0.9 m and fits; the axe hack O6 (3.37 m only) would back off 2.57 m, over MaxBackSlide.
        var axe = ExecutionChoice.ChooseAlignedOrk(marine, 0f, "tactical", "sm_mp_melee_power_axe", [At(7, MusouEnemyType.Boy, 0.8f)], rows, c, level, seedFor: _ => null);
        Assert.Equal("O5.power_axe", axe.Result.Row?.Id);
        Assert.Equal(ExecutionAlignFailure.TooFar, axe.Result.Tried[^1]);
        // Seeded: the pick is among the moves that fit, the same for every caller.
        ulong seed = ExecutionChoice.Seed(100, 1, 4);
        var p1 = ExecutionChoice.ChooseAlignedOrk(marine, 0f, "tactical", "sm_mp_melee_chainsword", [At(4, MusouEnemyType.Boy, 2f)], rows, c, level, seedFor: _ => seed);
        var p2 = ExecutionChoice.ChooseAlignedOrk(marine, 0f, "tactical", "sm_mp_melee_chainsword", [At(4, MusouEnemyType.Boy, 2f)], rows, c, level, seedFor: _ => seed);
        Assert.Equal(p1.Result.Row!.Id, p2.Result.Row!.Id);
        Assert.EndsWith(".chainsword", p1.Result.Row.Id);

        // Classic: a loyalist with a chainsword on a Chaos corpse 1.5 m ahead plays C1 (1.32 m: a 0.18 m slide in); a Chaos killer on a loyalist, swapped.
        var chaosCorpse = new PlayerState(2, new Vector3(0f, 1.5f, 0f), Vector3.Zero, MathF.PI, true) { Team = Team.Chaos };
        var classic = ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_chainsword", chaosCorpse, rows, c, level, null);
        Assert.Equal((true, "C1.chainsword", false), (classic.Ok, classic.Row?.Id, classic.RaceSwapped));
        Assert.Equal(1.32f, Vector2.Distance(new Vector2(classic.Attacker.Position.X, classic.Attacker.Position.Y), new Vector2(0f, 1.5f)), 1);
        var swapped = ExecutionChoice.ChooseClassic(marine with { Team = Team.Chaos }, "tactical", "csm_mp_melee_2hand_power_mace", chaosCorpse with { Team = Team.SpaceMarines }, rows, c, level, null);
        Assert.Equal((true, "C3.thunder_hammer", true), (swapped.Ok, swapped.Row?.Id, swapped.RaceSwapped));
        // The knife has no Classic row: the normal kill stands.
        Assert.False(ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_combat_knife", chaosCorpse, rows, c, level, null).Ok);
    }

    [Fact]
    public void The_bloodletter_rows_start_on_a_target_at_point_eight_metres_by_a_backward_slide()
    {
        // B3 needs 2.80 m and B2 2.50 m: before the backward slide (MaxBackSlide, ours) they started only between 1.8 / 1.5 m and WarfrontRange.
        var rows = SyntheticFilled();
        var c = new SimConfig();
        var marine = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines };
        var victim = new PlayerState(0, new Vector3(0f, 0.8f, 0f), Vector3.Zero, MathF.PI, true) { Alive = true };
        var walled = new CollisionWorld(Quads(FloorQuad, [new(-3, -1f, 0), new(3, -1f, 0), new(3, -1f, 3), new(-3, -1f, 3)]), Quads(FloorQuad));
        foreach (var (id, weapon, gap) in new[] { ("B3.thunder_hammer", "thunder_hammer", 2.80f), ("B2.power_axe", "power_axe", 2.50f) })
        {
            var variants = ExecutionChoice.Rows(rows, "tactical", ExecutionWeapons.FamilyOf(weapon)!, ExecutionTargets.Bloodletter);
            Assert.Equal(id, Assert.Single(variants).Id);
            var r = ExecutionAlign.Align(marine, victim, variants, c, OpenFloor());
            Assert.True(r.Ok, $"{id}: {r.Failure}");
            Assert.Equal(gap, r.Distance, 2);
            Assert.True(r.Attacker.Position.Y < 0f && r.Slide > ExecutionAlign.MaxSlide && r.Slide <= ExecutionAlign.MaxBackSlide, $"{id}: slide {r.Slide}");
            Assert.Equal(victim.Position, r.VictimAt);
            // Up against a wall behind him, the backward slide fails cleanly.
            var blocked = ExecutionAlign.Align(marine, victim, variants, c, walled);
            Assert.False(blocked.Ok);
            Assert.Equal(marine, blocked.Attacker);
        }
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
        Assert.Empty(notes);   // every row refreshed from its own sequencers (no fallback: each sequencer plays its clip, each named event has its tag)
        // Every variant's authored gap (the interaction bones met) against the gap its root travel gives (X2's derivation). They agree within the tolerance
        // except two variants whose end pose differs from the reference clip's: the head pop's 1 m variant ends 0.77 m further on (its interaction point at
        // the end lies at -2.34 m against the reference's -1.57 m) and the Bloodletter slam's 1 m variant 0.12 m. The interaction bones hold one world point
        // through each clip, so the authored gaps (1.00 and 2.80 m) stand; the odd gaps B3 2.80 / 8.84, B2 2.50, O5 1.70 and O8 1.87 agree on both readings.
        var known = new Dictionary<string, float> { ["SM_SYN_TH_OrkBoy_headPOP1M"] = 1.77f, ["SM_SYN_TH_Bloodletter_slam1M"] = 2.93f };
        var attacker = ExecutionCatalogueLoader.AttackerProfile.LoadCharacter(Game.Value!);
        var graphs = HavokBehavior.Read(attacker.Pack);
        foreach (var data in ExecutionRows.All.DistinctBy(r => r.AttackerClip))
        {
            var reference = ExecutionClipReader.Read(attacker, data.AttackerClip, graphs);
            var variants = data.AttackerVariants.Select(v => ExecutionClipReader.Read(attacker, v.Clip, graphs)).ToArray();
            output.WriteLine($"gaps {data.Id,-20} " + string.Join("  ", ExecutionCatalogueLoader.VariantGaps(data, reference, variants)
                .Zip(variants, (g, v) => $"{g.Clip} {g.Authored:F2}/{(g.Derived is { } d ? d.ToString("F2") : "-")} travel {(v.Root.Count > 1 ? v.Root[^1].Y - v.Root[0].Y : 0f):F2} ({v.RootSource})")));
            foreach (var (clip, authored, derived) in ExecutionCatalogueLoader.VariantGaps(data, reference, variants))
            {
                Assert.True(derived is not null, $"{data.Id} {clip}: no extracted motion");
                if (known.TryGetValue(clip, out float travelGap)) Assert.Equal(travelGap, derived!.Value, 1);
                else Assert.True(MathF.Abs(derived!.Value - authored) <= ExecutionCatalogueLoader.GapTolerance, $"{data.Id} {clip}: authored {authored} m, root travel {derived} m");
            }
        }
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
