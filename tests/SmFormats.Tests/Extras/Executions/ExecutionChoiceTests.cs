using System.Numerics;
using SmContent.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmFormats.Tests.Extras.Executions;

/// <summary>
/// Executions X2: <see cref="ExecutionChoice"/>, <see cref="ExecutionTargets"/> and <see cref="ExecutionWeapons"/> (synthetic rows, no original bytes): the
/// eligible Ork list as data, the Warfront ranking (distance band, angle band, id), reservations, range and sight, the weapon families (the power sword on the
/// chainsword's rows), the Classic kinds with the races swapped, the row order, and determinism.
/// </summary>
public class ExecutionChoiceTests
{
    static readonly SimConfig Config = new();

    static ExecutionRow R(string id, string kind, string weapon = "*", string cls = "*", float forward = 1.2f, int priority = 0) =>
        new(id, cls, weapon, kind, "SM_A_" + id, "V_" + id, 2f, 2f, [new(ExecutionEventKind.Kill, 1f)], new Vector3(0f, forward, 0f), MathF.PI, "g", "s")
        {
            Priority = priority,
            AttackerVariants = [new("SM_A_" + id + "_1m", 1f, []), new("SM_A_" + id + "_3.5m", 3.5f, []), new("SM_A_" + id + "_7m", 7f, [])],
        };

    static ExecutionRow[] OrkRows(string weapon = "*") => [R("boy", ExecutionTargets.OrkBoy, weapon), R("grot", ExecutionTargets.Gretchin, weapon)];

    static MusouEnemy Ork(ushort id, MusouEnemyType type, Vector3 at, MusouEnemyState state = MusouEnemyState.Seek, float health = 100f) =>
        new(id, type, state, at, Vector2.Zero, 0f, health, -1, 0, 0, 0);

    static PlayerState Executor(Vector3 at = default) => new(1, at, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines };

    static Vector3 At(float distance, float degrees)
    {
        float a = degrees * MathF.PI / 180f;
        return new Vector3(-MathF.Sin(a) * distance, MathF.Cos(a) * distance, 0f);
    }

    const string Sword = "sm_mp_melee_chainsword";

    [Fact]
    public void Ork_kinds_are_data_and_the_excluded_never_map()
    {
        var boy = new[] { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.ArdBoy, MusouEnemyType.RokkitBoy, MusouEnemyType.Stormboy };
        foreach (var t in Enum.GetValues<MusouEnemyType>())
        {
            string? expected = boy.Contains(t) ? ExecutionTargets.OrkBoy : t == MusouEnemyType.Gretchin ? ExecutionTargets.Gretchin : t == MusouEnemyType.ScrapMek ? "scrap_mek" : null;
            Assert.Equal(expected, ExecutionTargets.Of(t));
            Assert.Equal(expected is not null, ExecutionChoice.EligibleType(t));
        }
        Assert.Null(ExecutionTargets.Of(MusouEnemyType.BombSquig));   // never, even if a map named it
        Assert.Null(ExecutionTargets.Of("BombSquig", new Dictionary<string, string> { ["BombSquig"] = ExecutionTargets.OrkBoy }));
        // The Chaos run's roster.
        Assert.Equal(ExecutionTargets.Renegade, ExecutionTargets.Of("traitor_guard", ExecutionTargets.ChaosRun));
        Assert.Equal(ExecutionTargets.Renegade, ExecutionTargets.Of("Cultist", ExecutionTargets.ChaosRun));
        Assert.Equal(ExecutionTargets.ChaosMarine, ExecutionTargets.Of("chaos_havoc", ExecutionTargets.ChaosRun));
        Assert.Equal(ExecutionTargets.Bloodletter, ExecutionTargets.Of("bloodletter", ExecutionTargets.ChaosRun));
        Assert.Null(ExecutionTargets.Of("chosen", ExecutionTargets.ChaosRun));
        Assert.Null(ExecutionTargets.Of("chaos_lord", new Dictionary<string, string> { ["chaos_lord"] = ExecutionTargets.ChaosMarine }));
    }

    [Fact]
    public void Weapon_keys_map_to_families_with_the_power_sword_on_the_chainsword()
    {
        Assert.Equal(ExecutionWeapons.Chainsword, ExecutionWeapons.FamilyOf("sm_mp_melee_chainsword"));
        Assert.Equal(ExecutionWeapons.Chainsword, ExecutionWeapons.FamilyOf("sm_mp_melee_chainsword_gold"));
        Assert.Equal(ExecutionWeapons.Chainsword, ExecutionWeapons.FamilyOf("sm_mp_melee_powersword"));   // owner override
        Assert.Equal(ExecutionWeapons.Chainsword, ExecutionWeapons.FamilyOf("power_sword_chaos"));
        Assert.Equal(ExecutionWeapons.PowerAxe, ExecutionWeapons.FamilyOf("mp_melee_chain_axe"));
        Assert.Equal(ExecutionWeapons.PowerAxe, ExecutionWeapons.FamilyOf("sm_mp_melee_power_axe"));
        Assert.Equal(ExecutionWeapons.ThunderHammer, ExecutionWeapons.FamilyOf("thunder_hammer"));
        Assert.Equal(ExecutionWeapons.ThunderHammer, ExecutionWeapons.FamilyOf("csm_mp_melee_daemon_maul"));
        Assert.Equal(ExecutionWeapons.CombatKnife, ExecutionWeapons.FamilyOf("sm_mp_melee_combat_knife"));
        foreach (var none in new[] { "displace_kick", "lightning_claw", "power_spear", "bolter", "", null })
            Assert.Null(ExecutionWeapons.FamilyOf(none));
    }

    [Fact]
    public void An_ork_needs_a_row_for_its_kind_and_must_be_in_play()
    {
        var rows = OrkRows();
        Assert.True(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Boy, At(1, 0)), rows));
        Assert.True(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Boy, At(1, 0), health: 1f), rows));   // any health
        Assert.True(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.ShootaBoy, At(1, 0)), rows));
        Assert.True(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Gretchin, At(1, 0)), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Gretchin, At(1, 0)), [rows[0]]));   // no Gretchin row
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.ScrapMek, At(1, 0)), rows));   // no clip, no row
        Assert.True(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.ScrapMek, At(1, 0)), [.. rows, R("mek", "scrap_mek")]));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.BombSquig, At(1, 0)), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Nob, At(1, 0)), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.BossBoy, At(1, 0)), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Stormboy, At(1, 0), MusouEnemyState.Airborne), rows));   // grounded Stormboys only
        Assert.True(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Stormboy, At(1, 0), MusouEnemyState.Recover), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Boy, At(1, 0), MusouEnemyState.Dying), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Boy, At(1, 0), MusouEnemyState.Spawning), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Boy, At(1, 0), health: 0f), rows));
        Assert.False(ExecutionChoice.Eligible(Ork(1, MusouEnemyType.Boy, At(1, 0)), []));
    }

    [Fact]
    public void Warfront_ranks_by_distance_band_then_angle_band_then_id()
    {
        var rows = OrkRows();
        var e = Executor();
        Assert.Equal(5, ExecutionChoice.ChooseOrk(e, 0f, "tactical", Sword, [Ork(3, MusouEnemyType.Boy, At(1.4f, 30f)), Ork(5, MusouEnemyType.Boy, At(1.6f, 0f))], rows, Config, null));
        Assert.Equal(3, ExecutionChoice.ChooseOrk(e, 0f, "tactical", Sword, [Ork(3, MusouEnemyType.Boy, At(0.9f, 90f)), Ork(5, MusouEnemyType.Boy, At(1.1f, 0f))], rows, Config, null));
        MusouEnemy[] tie = [Ork(9, MusouEnemyType.ArdBoy, At(1.5f, 2f)), Ork(4, MusouEnemyType.ShootaBoy, At(1.3f, 4f)), Ork(7, MusouEnemyType.Gretchin, At(1.2f, 1f))];
        Assert.Equal([4, 7, 9], ExecutionChoice.RankOrks(e, 0f, "tactical", Sword, tie, rows, Config, null).Select(x => (int)x));
        Assert.Equal([4, 7, 9], ExecutionChoice.RankOrks(e, 0f, "tactical", Sword, tie.Reverse().ToArray(), rows, Config, null).Select(x => (int)x));
        Assert.Equal(2, ExecutionChoice.ChooseOrk(e, 0f, "tactical", Sword, [Ork(2, MusouEnemyType.Boy, At(1.5f, 180f))], rows, Config, null));   // no cone
        Assert.Equal(3, ExecutionChoice.ChooseOrk(e, MathF.PI / 6f, "tactical", Sword, [Ork(3, MusouEnemyType.Boy, At(1.4f, 30f)), Ork(5, MusouEnemyType.Boy, At(1.6f, 0f))], rows, Config, null));
    }

    [Fact]
    public void Warfront_skips_reserved_far_high_ineligible_unplayable_and_unseen_orks()
    {
        var rows = OrkRows();
        var e = Executor();
        Assert.Equal(0, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Boy, At(2.6f, 0f))], rows, Config, null));   // past 2.5 m
        Assert.Equal(1, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Boy, At(2.4f, 0f))], rows, Config, null));
        Assert.Equal(0, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Boy, At(1f, 0f) + new Vector3(0, 0, 2f))], rows, Config, null));
        Assert.Equal(2, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Boy, At(1f, 0f)), Ork(2, MusouEnemyType.Boy, At(1.2f, 0f))], rows, Config, null, new HashSet<ushort> { 1 }));
        Assert.Equal(2, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Nob, At(1f, 0f)), Ork(2, MusouEnemyType.Boy, At(2f, 0f))], rows, Config, null));
        Assert.Equal(2, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.BombSquig, At(1f, 0f)), Ork(2, MusouEnemyType.Boy, At(2f, 0f))], rows, Config, null));
        // Rows for the hammer only: a chainsword has nothing; the daemon maul plays the hammer's; the kick has no family.
        Assert.Equal(0, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Boy, At(1f, 0f))], OrkRows(ExecutionWeapons.ThunderHammer), Config, null));
        Assert.Equal(1, ExecutionChoice.ChooseOrk(e, 0f, "c", "daemon_maul", [Ork(1, MusouEnemyType.Boy, At(1f, 0f))], OrkRows(ExecutionWeapons.ThunderHammer), Config, null));
        Assert.Equal(0, ExecutionChoice.ChooseOrk(e, 0f, "c", "displace_kick", [Ork(1, MusouEnemyType.Boy, At(1f, 0f))], rows, Config, null));
        // A wall between: not seen.
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
        Quad(new(-20, -20, 0), new(20, -20, 0), new(20, 20, 0), new(-20, 20, 0));
        Quad(new(-5, 0.6f, -1), new(5, 0.6f, -1), new(5, 0.6f, 4), new(-5, 0.6f, 4));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var walled = new CollisionWorld(mesh, mesh);
        Assert.Equal(2, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, [Ork(1, MusouEnemyType.Boy, At(1.2f, 0f)), Ork(2, MusouEnemyType.Boy, new Vector3(-1.5f, 0f, 0f))], rows, Config, walled));
    }

    [Fact]
    public void Warfront_picks_a_seeded_move_among_those_that_fit()
    {
        var rows = new[] { R("o1", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, priority: 4), R("o2", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, priority: 3),
            R("o9", ExecutionTargets.Gretchin, ExecutionWeapons.Chainsword) };
        var e = Executor();
        var boy = Ork(3, MusouEnemyType.Stormboy, At(2f, 0f));
        var picks = Enumerable.Range(0, 32).Select(k => ExecutionChoice.ChooseWarfront(e, "tactical", "sm_mp_melee_powersword", boy, rows, Config, null, ExecutionChoice.Seed((uint)k, 1, 3))).ToList();
        Assert.All(picks, p => Assert.True(p.Ok));
        Assert.Equal(["o1", "o2"], picks.Select(p => p.Row!.Id).Distinct().Order());
        Assert.Equal("o1", ExecutionChoice.ChooseWarfront(e, "tactical", Sword, boy, rows, Config, null, null).Row!.Id);   // unseeded: the highest priority
        Assert.False(ExecutionChoice.ChooseWarfront(e, "tactical", "lightning_claw", boy, rows, Config, null, 7).Ok);
        Assert.Equal(ExecutionAlignFailure.NoVariant, ExecutionChoice.ChooseWarfront(e, "tactical", Sword, Ork(4, MusouEnemyType.Nob, At(2f, 0f)), rows, Config, null, 7).Failure);
        // Victim stand-in: the Ork's feet stay where they are.
        Assert.Equal(boy.Position, picks[0].VictimAt);
    }

    [Fact]
    public void Classic_plays_the_chaos_marine_rows_and_swaps_races_for_a_loyalist_victim()
    {
        var rows = new[]
        {
            R("any-csm", ExecutionTargets.ChaosMarine),
            R("c1", ExecutionTargets.ChaosMarine, ExecutionWeapons.Chainsword, cls: "assault"),
            R("c2", ExecutionTargets.ChaosMarine, ExecutionWeapons.PowerAxe),
            R("c3", ExecutionTargets.ChaosMarine, ExecutionWeapons.ThunderHammer),
            R("bad", ExecutionTargets.ChaosMarine, ExecutionWeapons.Chainsword) with { AttackerSeconds = float.NaN },
        };
        Assert.Equal(["c1", "any-csm"], ExecutionChoice.Rows(rows, "Assault", ExecutionWeapons.Chainsword, ExecutionTargets.ChaosMarine).Select(r => r.Id));
        Assert.Equal(["any-csm"], ExecutionChoice.Rows(rows, "devastator", ExecutionWeapons.Chainsword, ExecutionTargets.ChaosMarine).Select(r => r.Id));
        Assert.Equal([(ExecutionTargets.ChaosMarine, false)], ExecutionTargets.Classic(Team.Chaos));
        Assert.Equal([(ExecutionTargets.SpaceMarine, false), (ExecutionTargets.ChaosMarine, true)], ExecutionTargets.Classic(Team.SpaceMarines));

        var marine = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines };
        var chaosCorpse = new PlayerState(2, new Vector3(0f, 1.5f, 0f), Vector3.Zero, 0f, true) { Team = Team.Chaos };
        var r = ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_power_axe", chaosCorpse, rows, Config, null, null);
        Assert.True(r.Ok);
        Assert.Equal("c2", r.Row!.Id);
        Assert.False(r.RaceSwapped);
        // A Chaos killer with a chain axe on a loyalist: the same rows, races swapped (ours).
        var chaos = marine with { Team = Team.Chaos };
        var loyalCorpse = chaosCorpse with { Team = Team.SpaceMarines };
        var swapped = ExecutionChoice.ChooseClassic(chaos, "tactical", "mp_melee_chain_axe", loyalCorpse, rows, Config, null, null);
        Assert.True(swapped.Ok);
        Assert.Equal("c2", swapped.Row!.Id);
        Assert.True(swapped.RaceSwapped);
        // The knife and the kick have no Classic row; the power sword plays the chainsword's.
        Assert.False(ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_combat_knife", chaosCorpse, rows[1..], Config, null, null).Ok);
        Assert.Equal("c1", ExecutionChoice.ChooseClassic(marine, "assault", "sm_mp_melee_powersword", chaosCorpse, rows, Config, null, null).Row!.Id);
        // Too far for every row's band and slide: a clean failure.
        var far = ExecutionChoice.ChooseClassic(marine, "assault", Sword, chaosCorpse with { Position = new Vector3(0f, 9f, 0f) }, rows, Config, null, null);
        Assert.False(far.Ok);
        Assert.Equal(ExecutionAlignFailure.TooFar, far.Failure);
    }

    [Fact]
    public void Rows_order_by_class_then_priority_then_catalogue()
    {
        var rows = new[]
        {
            R("a", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, priority: 1),
            R("b", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, priority: 4),
            R("c", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, cls: "assault", priority: 0),
            R("d", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword, priority: 4),
            R("e", ExecutionTargets.OrkBoy, ExecutionWeapons.PowerAxe, priority: 9),
        };
        Assert.Equal(["c", "b", "d", "a"], ExecutionChoice.Rows(rows, "assault", ExecutionWeapons.Chainsword, ExecutionTargets.OrkBoy).Select(r => r.Id));
        Assert.Equal(["b", "d", "a"], ExecutionChoice.Rows(rows, "tactical", ExecutionWeapons.Chainsword, ExecutionTargets.OrkBoy).Select(r => r.Id));
    }

    [Fact]
    public void The_catalogue_keeps_valid_rows_in_order_and_notes_the_rest()
    {
        var notes = new List<string>();
        var rows = OrkRows().Concat([
            R("squig", "BombSquig"), R("BOY", ExecutionTargets.OrkBoy), R("kick", ExecutionTargets.OrkBoy, "displace_kick"),
            R("broken", "") , R("late", ExecutionTargets.OrkBoy) with { Events = [new(ExecutionEventKind.Kill, 9f)] },
            R("c1", ExecutionTargets.ChaosMarine, ExecutionWeapons.Chainsword)]);
        var cat = ExecutionCatalogue.Create(rows, notes);
        Assert.Equal(["boy", "grot", "c1"], cat.Rows.Select(r => r.Id));
        Assert.Equal(5, notes.Count);
        Assert.Equal(2, cat.IndexOf("C1"));
        Assert.Equal("grot", cat.Find("GROT")!.Id);
        Assert.Equal(["c1"], cat.For("x", "sm_mp_melee_powersword", ExecutionTargets.ChaosMarine).Select(r => r.Id));
        Assert.Empty(cat.For("x", "displace_kick", ExecutionTargets.OrkBoy));
        Assert.Equal([ExecutionTargets.ChaosMarine, ExecutionTargets.Gretchin, ExecutionTargets.OrkBoy], cat.Kinds);
        Assert.Empty(ExecutionCatalogue.Empty.Rows);
        byte[] Hash(ExecutionCatalogue c) { using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms)) c.Write(w); return ms.ToArray(); }
        Assert.Equal(Hash(cat), Hash(ExecutionCatalogue.Create(rows)));
        Assert.NotEqual(Hash(cat), Hash(ExecutionCatalogue.Create(rows.Select(r => r.Id == "boy" ? r with { Offset = new Vector3(0f, 1.3f, 0f) } : r))));
    }

    [Fact]
    public void Row_timing_falls_back_from_kill_to_contact_to_sync_to_end()
    {
        var row = R("t", ExecutionTargets.OrkBoy) with { Events = [new(ExecutionEventKind.Sync, 0.4f), new(ExecutionEventKind.Contact, 0.9f), new(ExecutionEventKind.End, 1.8f)] };
        Assert.Equal(0.9f, row.KillSeconds);
        Assert.Equal(1.8f, row.Seconds);
        Assert.Equal(0.4f, (row with { Events = [new(ExecutionEventKind.Sync, 0.4f)] }).KillSeconds);
        Assert.Equal(2f, (row with { Events = [] }).Seconds);
        Assert.Equal(new Vector4(0f, 0.5f, 0f, 0f), ExecutionRow.RootAt([Vector4.Zero, new Vector4(0f, 1f, 0f, 0f)], 0.5f));
        Assert.Equal(Vector4.Zero, ExecutionRow.RootAt([], 0.5f));
    }
}
