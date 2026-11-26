using System.Numerics;
using SmNet.Tests.CrossExtras;
using SmNet.Tests.Extras.Executions.Orks;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using static SmNet.Tests.Extras.Executions.ExecutionTestKit;

namespace SmNet.Tests.Extras.Executions.ChaosKinds;

/// <summary>
/// Executions X4b (owner 2026-11-25 "All with originals", 2026-11-26 "Chosen can be executed", card "Knife"): the Chaos battle's kind map, the Orks' map unchanged, the combat knife on the
/// chainsword's rows for every non-Ork victim (Classic's race swap too), and a Chaos stage's press, kill frame and end for every eligible kind (synthetic rows; the stage made Chaos by the test aid,
/// the real roster's keys in <c>Warfront/Chaos/ChaosExecutionTests</c>).
/// </summary>
public class ExecutionChaosKindTests
{
    /// <summary>The roster keys the Warfront roster gives (a copy for the map's own test; <c>ChaosExecutionTests</c> pins the roster's).</summary>
    static string? Key(MusouEnemyType t) => t switch
    {
        MusouEnemyType.Boy => "cultist", MusouEnemyType.ShootaBoy => "traitor_guard", MusouEnemyType.ChaosMarine => "chaos_tactical", MusouEnemyType.Nob => "chaos_champion",
        MusouEnemyType.BigShootaNob or MusouEnemyType.RokkitBoy => "chaos_havoc", MusouEnemyType.Stormboy => "chaos_raptor", MusouEnemyType.Gretchin => "bloodletter",
        MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => "chosen", MusouEnemyType.Warboss => "chaos_lord",
        _ => null,
    };

    static string? Expected(MusouEnemyType t, bool lord) => t switch
    {
        MusouEnemyType.Boy or MusouEnemyType.ShootaBoy => ExecutionTargets.Renegade,
        MusouEnemyType.ChaosMarine or MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.RokkitBoy or MusouEnemyType.Stormboy
            or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => ExecutionTargets.ChaosMarine,
        MusouEnemyType.Gretchin => ExecutionTargets.Bloodletter,
        MusouEnemyType.Warboss => lord ? ExecutionTargets.ChaosMarine : null,
        _ => null,
    };

    [Fact]
    public void The_chaos_map_takes_every_unit_but_the_lord_and_the_lord_only_through_his_gate()
    {
        var map = ExecutionKindMap.Chaos(Key);
        var duel = ExecutionKindMap.Chaos(Key, lord: true);
        foreach (var t in Enum.GetValues<MusouEnemyType>())
        {
            Assert.Equal(Expected(t, lord: false), map.Of(t));
            Assert.Equal(Expected(t, lord: true), duel.Of(t));
        }
        Assert.Equal(ExecutionTargets.ChaosMarine, ExecutionTargets.Of("chosen", ExecutionTargets.ChaosRun));   // owner 2026-11-26: the Chosen are eligible
        Assert.Null(ExecutionTargets.Of(ExecutionTargets.ChaosLord, ExecutionTargets.ChaosRun));   // never by the map
        Assert.Contains(ExecutionTargets.ChaosLord, ExecutionTargets.Never);
    }

    [Fact]
    public void The_orks_map_is_unchanged()
    {
        foreach (var t in Enum.GetValues<MusouEnemyType>())
        {
            string? want = t switch
            {
                MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.ArdBoy or MusouEnemyType.RokkitBoy or MusouEnemyType.Stormboy => ExecutionTargets.OrkBoy,
                MusouEnemyType.Gretchin => ExecutionTargets.Gretchin,
                MusouEnemyType.ScrapMek => "scrap_mek",
                _ => null,
            };
            Assert.Equal(want, ExecutionKindMap.Orks.Of(t));
            Assert.Equal(ExecutionTargets.Of(t), ExecutionKindMap.Orks.Of(t));
        }
    }

    // ---- the knife (owner 2026-11-26, card "Knife": "Use chainsword moves"; ours, a lookalike) ----

    static readonly ExecutionRow[] KnifeRows =
    [
        Row("C1.chainsword", ExecutionTargets.ChaosMarine), Row("R1.chainsword", ExecutionTargets.Renegade), Row("B1.chainsword", ExecutionTargets.Bloodletter),
        Row("O1.chainsword", ExecutionTargets.OrkBoy), Row("O3.combat_knife", ExecutionTargets.OrkBoy, ExecutionWeapons.CombatKnife),
        Row("O8.combat_knife", ExecutionTargets.OrkBoy, ExecutionWeapons.CombatKnife), Row("O9.combat_knife", ExecutionTargets.Gretchin, ExecutionWeapons.CombatKnife),
    ];

    [Theory]
    [InlineData(ExecutionTargets.ChaosMarine, "C1.chainsword")]
    [InlineData(ExecutionTargets.Renegade, "R1.chainsword")]
    [InlineData(ExecutionTargets.Bloodletter, "B1.chainsword")]
    [InlineData(ExecutionTargets.OrkBoy, "O3.combat_knife,O8.combat_knife")]
    [InlineData(ExecutionTargets.Gretchin, "O9.combat_knife")]
    [InlineData(ExecutionTargets.SpaceMarine, "")]
    public void The_knife_plays_the_chainswords_rows_on_every_non_ork_kind(string kind, string ids)
    {
        string family = ExecutionWeapons.FamilyOf("sm_mp_melee_combat_knife")!;
        Assert.Equal(ExecutionWeapons.CombatKnife, family);
        var rows = ExecutionChoice.Rows(KnifeRows, "tactical", family, kind).Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(ids.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal), rows);
        Assert.True(ExecutionWeapons.KnifeBorrowsChainsword);
        Assert.Equal(ExecutionWeapons.Chainsword, ExecutionWeapons.RowFamily(ExecutionWeapons.CombatKnife, ExecutionTargets.ChaosMarine));
        Assert.Equal(ExecutionWeapons.CombatKnife, ExecutionWeapons.RowFamily(ExecutionWeapons.CombatKnife, ExecutionTargets.OrkBoy));
        Assert.Equal(ExecutionWeapons.PowerAxe, ExecutionWeapons.RowFamily(ExecutionWeapons.PowerAxe, ExecutionTargets.ChaosMarine));   // only the knife borrows
    }

    static SimConfig KnifeConfig() => Config(ExecutionRowSet.OfAll(KnifeRows)) is var c
        ? c with { Classes = [.. c.Classes.Select(k => k.Name is "tactical" or "chaos" or "ork" ? k with { MeleeChoices = [k.MeleeChoices[0] with { Wargear = (k.Side == Team.Chaos ? "csm" : "sm") + "_mp_melee_combat_knife" }] } : k)] }
        : throw new InvalidOperationException();

    [Theory]
    [InlineData(Tactical, Chaos, "C1.chainsword", false)]   // a loyalist's knife on a Chaos marine
    [InlineData(Chaos, Tactical, "C1.chainsword", true)]    // a Chaos knife on a loyalist: the race swap
    [InlineData(Tactical, Ork, "O3.combat_knife", false)]   // an Ork player: the knife's own Ork rows
    public void A_classic_knife_kill_finishes_on_the_chainswords_rows(int killer, int victim, string row, bool swapped)
    {
        var w = Arena(KnifeConfig(), (killer, Vector3.Zero), (victim, new Vector3(0f, 1.2f, 0f)));
        uint kill = KillByMelee(w);
        Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        string id = w.Config.Executions.AtWire(run.Wire)!.Id;
        if (row.StartsWith("O3", StringComparison.Ordinal)) Assert.StartsWith("O", id);   // O3 or O8 (the seeded pick)
        else Assert.Equal(row, id);
        Assert.Equal(swapped, run.RaceSwapped);
    }

    // ---- a Chaos stage (the test aid; synthetic rows on the Orks config's bodies) ----

    static (World W, ushort Enemy) ChaosAhead(MusouEnemyType type, float health = 100f)
    {
        var w = ExecutionOrkKit.Rig(ChaosExecutionKit.WithRows(ExecutionOrkKit.Config()));
        w.ExecutionsChaosStageForTests = true;
        ushort e = ExecutionOrkKit.OrkAhead(w, type, health: health);
        ExecutionOrkKit.Settle(w, e);
        return (w, e);
    }

    public static TheoryData<MusouEnemyType, string> Eligible => new()
    {
        { MusouEnemyType.Boy, "R1.chainsword" }, { MusouEnemyType.ShootaBoy, "R1.chainsword" },
        { MusouEnemyType.ChaosMarine, "C1.chainsword" }, { MusouEnemyType.Nob, "C1.chainsword" }, { MusouEnemyType.BigShootaNob, "C1.chainsword" },
        { MusouEnemyType.RokkitBoy, "C1.chainsword" }, { MusouEnemyType.Stormboy, "C1.chainsword" },
        { MusouEnemyType.BossBoy, "C1.chainsword" }, { MusouEnemyType.BossShootaBoy, "C1.chainsword" }, { MusouEnemyType.BossArdBoy, "C1.chainsword" }, { MusouEnemyType.BossNob, "C1.chainsword" },
        { MusouEnemyType.Gretchin, "B1.chainsword" },
    };

    [Theory]
    [MemberData(nameof(Eligible))]
    public void Every_chaos_kind_starts_dies_at_the_kill_frame_and_ends_done(MusouEnemyType type, string row)
    {
        var (w, e) = ChaosAhead(type, health: 400f);
        ExecutionOrkKit.Tap(w);
        Assert.Equal(row, ChaosExecutionKit.StartedRow(w));
        Assert.Equal(e, (ushort)ChaosExecutionKit.LiveTarget(w));
        Assert.True(w.Musou!.ExecutionsIsHeld(e));
        var r = w.ExecutionOf(1)!.Value;
        var rowData = w.Config.Executions.AtWire(r.Wire)!;
        uint kill = ExecutionTrack.EventTick(r, rowData, ExecutionEventKind.Kill, w.Config.TickRate);
        while (w.Tick < kill - 1) { ExecutionOrkKit.Idle(w); Assert.True(w.Musou.TryGet(e, out var held) && held.Alive, "alive before the kill frame"); }
        var ended = ExecutionOrkKit.RunOut(w);
        Assert.NotNull(ended);
        Assert.Equal(ExecutionEndReason.Done, ended!.Value.Reason);
        Assert.True(ended.Value.Run.KillDone);
        Assert.False(w.Musou.TryGet(e, out var dead) && dead.Alive);
        var p = w.Players[1];
        Assert.True(p.Shield > Loadouts.Body(w.Config, p).ShieldMax);   // the overshield, as for the Orks
    }

    [Theory]
    [InlineData(MusouEnemyType.Warboss)]   // the Lord: only in his duel (ChaosExecutionTests)
    [InlineData(MusouEnemyType.ArdBoy)]    // an Ork-only slot (the Chaos Marine fields there)
    public void The_lord_and_the_ork_only_kinds_are_never_taken_on_a_chaos_stage(MusouEnemyType type)
    {
        var (w, e) = ChaosAhead(type, health: 400f);
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.NoTarget, ExecutionOrkKit.RefusedLast(w));
        Assert.False(w.Musou!.ExecutionsIsHeld(e));
    }

    [Fact]
    public void An_airborne_raptor_is_never_taken()
    {
        var (w, e) = ChaosAhead(MusouEnemyType.Stormboy);
        Assert.True(w.Musou!.TryGet(e, out var raptor));
        var rows = w.Config.ExecutionRows;
        var map = ExecutionKindMap.Chaos(Key);
        Assert.True(ExecutionChoice.Eligible(raptor, rows, map));
        Assert.False(ExecutionChoice.Eligible(raptor with { State = MusouEnemyState.Airborne }, rows, map));
    }

    [Fact]
    public void The_orks_still_play_their_own_rows_on_an_orks_stage()
    {
        var w = ExecutionOrkKit.Rig(ChaosExecutionKit.WithRows(ExecutionOrkKit.Config()));
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        Assert.Equal("O1.chainsword", ChaosExecutionKit.StartedRow(w));
    }

    [Fact]
    public void A_player_solid_target_is_not_its_own_bystander()
    {
        // A Chosen (a Boss Ork row: player-solid, so in the bystander list) and a row whose gap (0.8 m) is inside the bystanders' 0.9 m clearance: the slide toward him ends 0.8 m from his feet,
        // which refused it (Bystander) while his own feet counted.
        var rows = ExecutionRowSet.OfAll([Row("C1.chainsword", ExecutionTargets.ChaosMarine, gap: 0.8f, near: 0.6f)]);
        var w = ExecutionOrkKit.Rig(ChaosExecutionKit.WithRows(ExecutionOrkKit.Config()) with { Executions = rows });
        w.ExecutionsChaosStageForTests = true;
        ushort e = ExecutionOrkKit.OrkAhead(w, MusouEnemyType.BossNob, gap: 1.7f, health: 5000f);
        ExecutionOrkKit.Settle(w, e);
        ExecutionOrkKit.Tap(w);
        Assert.Equal("C1.chainsword", ChaosExecutionKit.StartedRow(w));
        Assert.True(w.ExecutionOf(1)!.Value.SlideTicks > 0);
    }
}
