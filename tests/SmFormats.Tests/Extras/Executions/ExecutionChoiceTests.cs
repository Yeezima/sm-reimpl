using System.Numerics;
using SmContent;
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
        Assert.Equal(ExecutionWeapons.ThunderHammer, ExecutionWeapons.FamilyOf("csm_mp_melee_2hand_power_mace"));   // the daemon maul's real wargear
        // Every melee wargear the simulation knows (MpClasses.SimulatedMeleeSuffixes, the MeleeMaps list) with both race prefixes: the owner's families (power sword
        // on the chainsword's rows). A suffix added there without a family here fails, so a new melee weapon is a decision, not a silent "no execution".
        var families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["_mp_melee_combat_knife"] = ExecutionWeapons.CombatKnife, ["_mp_melee_chainsword_gold"] = ExecutionWeapons.Chainsword,
            ["_mp_melee_chainsword"] = ExecutionWeapons.Chainsword, ["_mp_melee_powersword"] = ExecutionWeapons.Chainsword,
            ["_mp_melee_power_axe"] = ExecutionWeapons.PowerAxe, ["_mp_melee_chain_axe"] = ExecutionWeapons.PowerAxe,
            ["_mp_melee_2hand_thunder_hammer"] = ExecutionWeapons.ThunderHammer, ["_mp_melee_2hand_power_mace"] = ExecutionWeapons.ThunderHammer,
        };
        Assert.NotEmpty(MpClasses.SimulatedMeleeSuffixes);
        foreach (var suffix in MpClasses.SimulatedMeleeSuffixes)
        {
            Assert.True(families.TryGetValue(suffix, out var family), $"no execution family decided for the melee wargear suffix '{suffix}'");
            foreach (var race in new[] { "sm", "csm" })
                Assert.Equal(family, ExecutionWeapons.FamilyOf(race + suffix));
        }
        Assert.Null(ExecutionWeapons.FamilyOf("sm_mp_core_abil_heavy_evade"));   // the heavies' kick and stomp
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
        Assert.Equal(1, ExecutionChoice.ChooseOrk(e, 0f, "c", "csm_mp_melee_2hand_power_mace", [Ork(1, MusouEnemyType.Boy, At(1f, 0f))], OrkRows(ExecutionWeapons.ThunderHammer), Config, null));
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
        Assert.Equal([(ExecutionTargets.ChaosMarine, false)], ExecutionTargets.Classic(Race.Chaos));
        Assert.Equal([(ExecutionTargets.SpaceMarine, false), (ExecutionTargets.ChaosMarine, true)], ExecutionTargets.Classic(Race.SpaceMarines));

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
        // By race, not team (an Ork Boy player sits on either team): an Ork victim takes the Ork Boy rows; an Ork killer cannot execute.
        var orkConfig = Config with { Classes = [new ClassLoadout("tactical", 0, -1, []), new ClassLoadout("ork_boy", 0, -1, []) { Race = Race.Orks }] };
        var orkRows = rows.Append(R("o3", ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword)).ToArray();
        var orkCorpse = chaosCorpse with { Class = 1, Team = Team.Chaos };
        var onOrk = ExecutionChoice.ChooseClassic(marine, "tactical", Sword, orkCorpse, orkRows, orkConfig, null, null);
        Assert.True(onOrk.Ok);
        Assert.Equal("o3", onOrk.Row!.Id);
        Assert.False(onOrk.RaceSwapped);
        // A Chaos killer on an Ork victim: the Ork Boy rows as they are (the loyalist attacker half), not swapped (lead decision).
        var chaosOnOrk = ExecutionChoice.ChooseClassic(marine with { Team = Team.Chaos }, "tactical", Sword, orkCorpse, orkRows, orkConfig, null, null);
        Assert.Equal(("o3", false), (chaosOnOrk.Row!.Id, chaosOnOrk.RaceSwapped));
        Assert.Equal([(ExecutionTargets.OrkBoy, false)], ExecutionTargets.Classic(Race.Orks));
        Assert.False(ExecutionChoice.ChooseClassic(marine with { Class = 1 }, "ork_boy", Sword, chaosCorpse, orkRows, orkConfig, null, null).Ok);
        Assert.False(ExecutionTargets.CanExecute(Race.Orks));
        Assert.Equal(0, ExecutionChoice.ChooseOrk(Executor() with { Class = 1 }, 0f, "ork_boy", Sword, [Ork(1, MusouEnemyType.Boy, At(1f, 0f))], OrkRows(), orkConfig, null));
        // Too far for every row's band and slide: a clean failure.
        var far = ExecutionChoice.ChooseClassic(marine, "assault", Sword, chaosCorpse with { Position = new Vector3(0f, 9f, 0f) }, rows, Config, null, null);
        Assert.False(far.Ok);
        Assert.Equal(ExecutionAlignFailure.TooFar, far.Failure);
    }

    [Fact]
    public void The_aligned_pick_skips_an_ork_whose_move_does_not_fit()
    {
        var rows = OrkRows(ExecutionWeapons.Chainsword);
        var e = Executor();
        // Ork 2 is the better ranked (0 degrees), but its move throws it back into a wall (a level wall, not on the floor mesh the ground probes use); Ork 5 is clear.
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(List<Vector3> vs, List<int> ix, Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = vs.Count; vs.AddRange([a, b, c, d]); ix.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
        Quad(v, idx, new(-20, -20, 0), new(20, -20, 0), new(20, 20, 0), new(-20, 20, 0));
        var floor = new StaticMesh(v.ToArray(), idx.ToArray());
        // The victim of Ork 2 is thrown back 1.3 m into a wall behind it.
        Quad(v, idx, new(-0.5f, 2.6f, 0), new(0.5f, 2.6f, 0), new(0.5f, 2.6f, 3), new(-0.5f, 2.6f, 3));
        var level = new CollisionWorld(new StaticMesh(v.ToArray(), idx.ToArray()), floor);
        var thrown = rows.Select(r => r with { VictimRoot = [Vector4.Zero, new Vector4(0f, -1.3f, 0f, 0f)] }).ToArray();
        MusouEnemy[] orks = [Ork(2, MusouEnemyType.Boy, At(1.5f, 0f)), Ork(5, MusouEnemyType.Boy, At(1.5f, 50f))];
        Assert.Equal(2, ExecutionChoice.ChooseOrk(e, 0f, "c", Sword, orks, thrown, Config, level));
        var (id, result) = ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, orks, thrown, Config, level);
        Assert.Equal(5, id);
        Assert.True(result.Ok);
        Assert.Equal((ushort)0, ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, [orks[0]], thrown, Config, level).Id);
    }

    [Fact]
    public void The_prompt_mode_stops_at_the_first_fitting_move_and_picks_the_same_ork()
    {
        // Ork 3's first row is out of reach (a 5 m gap the 1 m slide cannot close), the next two fit.
        ExecutionRow Fixed(string id, float forward) => R(id, ExecutionTargets.OrkBoy, ExecutionWeapons.Chainsword) with { AttackerVariants = [], Offset = new Vector3(0f, forward, 0f) };
        ExecutionRow[] rows = [Fixed("far", 5f) with { Priority = 9 }, Fixed("x", 1.5f) with { Priority = 5 }, Fixed("y", 1.5f)];
        var e = Executor();
        MusouEnemy[] orks = [Ork(3, MusouEnemyType.Boy, At(1.5f, 0f))];
        var prompt = ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, orks, rows, Config, null);
        var full = ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, orks, rows, Config, null, seedFor: _ => null);
        Assert.Equal((3, 3), ((int)prompt.Id, (int)full.Id));
        Assert.Equal([ExecutionAlignFailure.TooFar, ExecutionAlignFailure.None], prompt.Result.Tried);
        Assert.Equal([ExecutionAlignFailure.TooFar, ExecutionAlignFailure.None, ExecutionAlignFailure.None], full.Result.Tried);
        Assert.Equal((prompt.Result.Row!.Id, prompt.Result.Variant, prompt.Result.Attacker.Position), (full.Result.Row!.Id, full.Result.Variant, full.Result.Attacker.Position));
        // The press's seeded pick names the same Ork.
        Assert.Equal(3, ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, orks, rows, Config, null, seedFor: id => ExecutionChoice.Seed(7, 1, id)).Id);
    }

    [Fact]
    public void Bystanders_reach_the_alignment_through_every_chooser()
    {
        var rows = OrkRows(ExecutionWeapons.Chainsword).Select(r => r with { AttackerVariants = [], Offset = new Vector3(0f, 1.2f, 0f) }).ToArray();
        var e = Executor();
        var boy = Ork(3, MusouEnemyType.Boy, At(2f, 0f));   // a 0.8 m slide forward
        Vector3[] onSlide = [new(0.3f, 0.6f, 0f)], aside = [new(3f, 0.6f, 0f)];
        Assert.Equal(ExecutionAlignFailure.Bystander, ExecutionChoice.ChooseWarfront(e, "c", Sword, boy, rows, Config, null, null, onSlide).Failure);
        Assert.True(ExecutionChoice.ChooseWarfront(e, "c", Sword, boy, rows, Config, null, null, aside).Ok);
        Assert.Equal((ushort)0, ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, [boy], rows, Config, null, bystanders: onSlide).Id);
        Assert.Equal(ExecutionAlignFailure.Bystander, ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, [boy], rows, Config, null, bystanders: onSlide).Result.Failure);
        Assert.Equal((ushort)3, ExecutionChoice.ChooseAlignedOrk(e, 0f, "c", Sword, [boy], rows, Config, null, bystanders: aside).Id);
        // Classic: a teammate standing on the finisher's slide.
        var classic = new[] { R("c2", ExecutionTargets.ChaosMarine, ExecutionWeapons.PowerAxe) with { AttackerVariants = [], Offset = new Vector3(0f, 1.2f, 0f) } };
        var marine = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines };
        var corpse = new PlayerState(2, new Vector3(0f, 2f, 0f), Vector3.Zero, 0f, true) { Team = Team.Chaos };
        Assert.Equal(ExecutionAlignFailure.Bystander, ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_power_axe", corpse, classic, Config, null, null, onSlide).Failure);
        Assert.True(ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_power_axe", corpse, classic, Config, null, null, aside).Ok);
        Assert.True(ExecutionChoice.ChooseClassic(marine, "tactical", "sm_mp_melee_power_axe", corpse, classic, Config, null, null).Ok);
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
