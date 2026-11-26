using System.Numerics;
using SmNet.Tests.CrossExtras;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Chaos.Finale;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>
/// Executions X4b in the Chaos finale (owner 2026-11-26, card "Chaos exec": "Chosen can be executed", "Lord finisher at low health", "Finishers run into the duel"): the Lord is never executed outside
/// his duel; in it only the duelist may execute, only the Lord, only under <see cref="WarfrontDuelRules.LordExecutableBelow"/> of his health, and that finisher's kill frame wins the duel through its
/// own win path; a held Lord does nothing. Synthetic rows (<see cref="ChaosExecutionKit"/>) on the finale's stub fortress.
/// </summary>
public class DuelExecutionTests(ITestOutputHelper output)
{
    static SimConfig Config => ChaosExecutionKit.WithRows(WarfrontChaosRosterTests.ChaosConfig);

    [Fact]
    public void The_roster_keys_each_chaos_unit()
    {
        var keys = new Dictionary<MusouEnemyType, string?>
        {
            [MusouEnemyType.Boy] = "cultist", [MusouEnemyType.ShootaBoy] = "traitor_guard", [MusouEnemyType.ChaosMarine] = "chaos_tactical", [MusouEnemyType.Nob] = "chaos_champion",
            [MusouEnemyType.BigShootaNob] = "chaos_havoc", [MusouEnemyType.RokkitBoy] = "chaos_havoc", [MusouEnemyType.Stormboy] = "chaos_raptor", [MusouEnemyType.Gretchin] = "bloodletter",
            [MusouEnemyType.BossBoy] = "chosen", [MusouEnemyType.BossShootaBoy] = "chosen", [MusouEnemyType.BossArdBoy] = "chosen", [MusouEnemyType.BossNob] = "chosen",
            [MusouEnemyType.Warboss] = "chaos_lord",
        };
        foreach (var t in Enum.GetValues<MusouEnemyType>()) Assert.Equal(keys.GetValueOrDefault(t), WarfrontChaosRoster.ExecutionKey(t));
        foreach (var t in Enum.GetValues<MusouEnemyType>()) if (WarfrontChaosRoster.Fields(t) && t != MusouEnemyType.Warboss) Assert.NotNull(WarfrontChaosRoster.ExecutionKey(t));   // every unit a Chaos battle fields
    }

    [Fact]
    public void The_lord_is_never_executed_outside_his_duel()
    {
        var (w, b) = FinaleRig.Fortress(config: Config);
        FinaleRig.Steps(w, 10);
        w.Musou!.Frozen = true;
        ChaosExecutionKit.PlaceFacing(w, 1, FinaleRig.Lord(w, b).Position, 1.3f);
        ChaosExecutionKit.Idle(w);
        ChaosExecutionKit.Tap(w);
        Assert.Equal(WarfrontDuelPhase.Bodyguard, w.WarfrontChaosDuel!.Phase);
        Assert.Null(ChaosExecutionKit.StartedRow(w));
        Assert.False(w.Musou.ExecutionsIsHeld(b.BossId));
    }

    [Fact]
    public void A_chosen_is_executed_with_the_chaos_marine_rows()
    {
        var (w, b) = FinaleRig.Fortress(config: Config);
        FinaleRig.Steps(w, 10);
        w.Musou!.Frozen = true;
        ushort chosen = b.BossOrks[0];
        Assert.True(w.Musou.TryGet(chosen, out var c) && c.Alive);
        ChaosExecutionKit.PlaceFacing(w, 1, c.Position, 1.3f);
        ChaosExecutionKit.Idle(w);
        ChaosExecutionKit.Tap(w);
        Assert.Equal("C1.chainsword", ChaosExecutionKit.StartedRow(w));
        Assert.Equal(chosen, (ushort)ChaosExecutionKit.LiveTarget(w));
    }

    /// <summary>The duel under way with the duelist 1.3 m from the Lord facing him and the pool frozen (the Lord stands; the duel step still runs).</summary>
    static (World W, TfwBattleView B, WarfrontDuel D) DuelBeside(int humans = 2)
    {
        var (w, b, d) = FinaleRig.Duel(humans, Config);
        w.Musou!.Frozen = true;
        ChaosExecutionKit.PlaceFacing(w, 1, FinaleRig.Lord(w, b).Position, 1.3f);
        ChaosExecutionKit.Idle(w);
        return (w, new TfwBattleView(b), d);
    }

    /// <summary>The battle and its Lord (a small view so the tests read alike).</summary>
    readonly record struct TfwBattleView(SmSim.Extras.Musou.Campaign.TfwBattle Battle)
    {
        public ushort Lord => Battle.BossId;
        public float Max => Battle.BossMaxHealth;
    }

    static void LordTo(World w, TfwBattleView b, float share)
    {
        Assert.True(w.Musou!.TryGet(b.Lord, out var l));
        float want = b.Max * share;
        Assert.True(w.Musou.Damage(b.Lord, FinaleRig.Human, l.Health - want, Vector3.Zero, out _));   // the duelist's hit: the duel's gate admits his alone
        Assert.True(w.Musou.TryGet(b.Lord, out l));
        Assert.Equal(want, l.Health, 2);
    }

    [Fact]
    public void In_the_duel_the_lord_above_a_tenth_is_refused()
    {
        var (w, b, d) = DuelBeside();
        LordTo(w, b, 0.5f);
        ChaosExecutionKit.Tap(w);
        Assert.Null(ChaosExecutionKit.StartedRow(w));
        Assert.Equal(ExecutionWarfrontRefusal.Duel, w.ExecutionsWarfrontRefusedLast(1));
        LordTo(w, b, WarfrontDuelRules.LordExecutableBelow + 0.001f);   // just over the gate
        ChaosExecutionKit.Idle(w);
        ChaosExecutionKit.Tap(w);
        Assert.Null(ChaosExecutionKit.StartedRow(w));
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
    }

    [Fact]
    public void In_the_duel_only_the_duelist_may_execute_and_only_the_lord()
    {
        var (w, b, d) = DuelBeside();
        LordTo(w, b, 0.05f);
        Assert.Equal(1, w.WarfrontDuelist);
        Assert.Equal(0, w.ExecutionsWarfrontChoose(2, w.Players[2], prompt: true).Id);   // the spectator (aboard and held besides): never
        Assert.Equal(b.Lord, w.ExecutionsWarfrontChoose(1, w.Players[1], prompt: true).Id);
    }

    [Fact]
    public void The_lords_finisher_under_a_tenth_kills_him_and_wins_the_duel()
    {
        var (w, b, d) = DuelBeside();
        var log = new List<string>();
        w.WarfrontChaosLog = log.Add;
        var ends = new List<SmSim.Extras.Musou.Campaign.TfwMapResult>();
        w.Tfw!.Warfront!.NodeEnded += (_, _, r) => ends.Add(r);
        bool endedBeforeDone = false;
        LordTo(w, b, 0.05f);
        ChaosExecutionKit.Tap(w);
        Assert.Equal("C1.chainsword", ChaosExecutionKit.StartedRow(w));   // the Chaos Marine's rows (the Lord is the elite Chaos Marine)
        Assert.Equal(b.Lord, (ushort)ChaosExecutionKit.LiveTarget(w));
        Assert.True(w.Musou!.ExecutionsIsHeld(b.Lord));
        var before = FinaleRig.Lord(w, b.Battle).Position;
        string? why = null;
        bool killFrame = false;
        for (int t = 0; t < 120 && why is null; t++)
        {
            ChaosExecutionKit.Idle(w);
            if (!killFrame && w.Musou.TryGet(b.Lord, out var l) && l.Alive)
            {
                Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
                Assert.True(FinaleRig.Flat(before, l.Position) < 1e-3f, "a held Lord stands");
            }
            else killFrame = true;
            why = ChaosExecutionKit.EndedWhy(w);
            if (why is null && ends.Count > 0) endedBeforeDone = true;
        }
        foreach (var line in log) output.WriteLine(line);
        Assert.True(killFrame);
        Assert.False(w.Musou.TryGet(b.Lord, out var dead) && dead.Alive);
        Assert.Equal(WarfrontDuelPhase.Won, w.WarfrontChaosDuel!.Phase);
        Assert.True(b.Battle.Won);
        Assert.Contains(log, l => l.Contains("WON", StringComparison.Ordinal));
        Assert.Equal("Done", why);   // lead 41: the finisher plays out (the win's exit waited)
        Assert.False(endedBeforeDone);
        Assert.Contains(log, l => l.Contains("plays out before the win's exit", StringComparison.Ordinal));
        for (int t = 0; t < 5 && ends.Count == 0; t++) ChaosExecutionKit.Idle(w);
        Assert.Equal([SmSim.Extras.Musou.Campaign.TfwMapResult.Won], ends);   // the run still records Won
        Assert.Null(w.Tfw.Battle);
    }

    [Fact]
    public void The_wins_exit_waits_no_longer_than_the_finishers_bound_and_goes_at_once_after_an_abort()
    {
        var (w, b, d) = DuelBeside();
        var ends = new List<SmSim.Extras.Musou.Campaign.TfwMapResult>();
        w.Tfw!.Warfront!.NodeEnded += (_, _, r) => ends.Add(r);
        LordTo(w, b, 0.05f);
        ChaosExecutionKit.Tap(w);
        Assert.Equal("C1.chainsword", ChaosExecutionKit.StartedRow(w));
        for (int t = 0; t < 120 && !ChaosExecutionKit.KillDone(w); t++) ChaosExecutionKit.Idle(w);
        Assert.True(ChaosExecutionKit.KillDone(w));
        for (int t = 0; t < 3; t++) ChaosExecutionKit.Idle(w);
        output.WriteLine($"tick {w.Tick}, live {w.ExecutionsLive.Count}, ends {ends.Count}, duel {d.Phase}, battle {(w.Tfw.Battle is null ? "gone" : "here")}");
        Assert.Empty(ends);   // held
        w.ExecutionsPlaceForTests(1, w.Players[1].Position + new Vector3(5f, 0f, 0f), 0f);   // a teleport: the run ends Invalid next step
        for (int t = 0; t < 4 && ends.Count == 0; t++) ChaosExecutionKit.Idle(w);
        Assert.Equal([SmSim.Extras.Musou.Campaign.TfwMapResult.Won], ends);   // the exit at once, still Won
    }

    // ---- the client: the dot's gate and heights ----

    static byte[] State(WarfrontDuelPhase phase, int champion, float health, float max = 10000f) =>
        SmNet.Extras.Warfront.WarfrontChaosDuelWire.Encode(SmNet.Extras.Warfront.WarfrontChaosDuelWire.Of(phase, champion, 0, 0, 0, 99, health, max));

    [Fact]
    public void The_clients_gate_shows_the_dot_only_to_the_champion_under_a_tenth()
    {
        var m = new SmClient.Extras.Warfront.Chaos.Finale.ChaosDuelModel();
        Assert.Equal((false, (ushort)0), m.ExecutionGate(3, 0.0));   // no duel state: no duel
        Assert.True(m.Apply(State(WarfrontDuelPhase.Bodyguard, 0, 10000f), 1.0));
        Assert.Equal((false, (ushort)0), m.ExecutionGate(3, 1.0));   // the bodyguard fight: the Lord is never a target (the kind map leaves him out)
        m.Apply(State(WarfrontDuelPhase.Snapshot, 0, 10000f), 2.0);
        Assert.Equal((true, (ushort)0), m.ExecutionGate(3, 2.0));   // the snapshot: nobody
        m.Apply(State(WarfrontDuelPhase.Duel, 3, 1500f), 3.0);
        Assert.Equal((true, (ushort)0), m.ExecutionGate(3, 3.0));   // 15 %
        m.Apply(State(WarfrontDuelPhase.Duel, 3, 995f), 3.1);
        Assert.Equal((true, (ushort)0), m.ExecutionGate(3, 3.1));   // 9.95 %: the wire's permille rounds up to 100, the dot waits (never promises a refused press)
        m.Apply(State(WarfrontDuelPhase.Duel, 3, 980f), 3.2);
        Assert.Equal((true, (ushort)99), m.ExecutionGate(3, 3.2));   // 9.8 %: the champion
        Assert.Equal((true, (ushort)0), m.ExecutionGate(4, 3.2));    // a spectator: never
        Assert.Equal((false, (ushort)0), m.ExecutionGate(3, 3.2 + SmClient.Extras.Warfront.Chaos.Finale.ChaosDuelLook.StaleSeconds + 0.1));   // a stale state: no duel
        m.Apply(State(WarfrontDuelPhase.Handoff, 0, 980f), 4.0);
        Assert.Equal((true, (ushort)0), m.ExecutionGate(3, 4.0));
    }

    [Fact]
    public void The_dot_stands_over_each_chaos_bodys_top()
    {
        var rules = WarfrontChaosRosterTests.ChaosConfig.MusouRules;
        float H(MusouEnemyType t) => SmClient.ExecutionPromptRules.ChaosMarkerHeight(rules, t);
        Assert.Equal(2.0f + 0.3f, H(MusouEnemyType.Boy), 3);              // Cultist
        Assert.Equal(2.0f + 0.3f, H(MusouEnemyType.ShootaBoy), 3);        // Traitor Guardsman
        Assert.Equal(2.3f + 0.3f, H(MusouEnemyType.ChaosMarine), 3);      // Chaos Tactical Marine
        Assert.Equal(2.3f + 0.3f, H(MusouEnemyType.Stormboy), 3);         // Raptor
        Assert.Equal(2.3f * 1.1f + 0.3f, H(MusouEnemyType.Nob), 3);       // Aspiring Champion
        Assert.Equal(2.6f + 0.3f, H(MusouEnemyType.Gretchin), 3);         // Daemon (Bloodletter)
        Assert.Equal(2.3f * 1.2f + 0.3f, H(MusouEnemyType.BossNob), 3);   // Chosen
        Assert.Equal(2.3f * 1.35f + 0.3f, H(MusouEnemyType.Warboss), 3);  // the Lord, x1.35
        Assert.Equal(SmContent.Extras.Warfront.WarfrontChaosTuning.LordBossScale, 1.35f);
        Assert.Equal(WarfrontDuelRules.LordExecutableBelow, 0.10f);
    }

    [Fact]
    public void A_held_lord_does_nothing_of_his_own()
    {
        var (w, b, d) = DuelBeside();
        LordTo(w, b, 0.05f);
        ChaosExecutionKit.Tap(w);
        Assert.True(w.Musou!.ExecutionsIsHeld(b.Lord));
        w.Musou.Frozen = false;   // the pool and his boss code run
        int telegraphs = w.Musou.LordTelegraphCount;
        for (int t = 0; t < 20 && w.Musou.ExecutionsIsHeld(b.Lord); t++)
        {
            ChaosExecutionKit.Idle(w);
            Assert.Equal(telegraphs, w.Musou.LordTelegraphCount);   // no slam, charge, leap or bolt begins
            Assert.Empty(w.Musou.LordBolts);
        }
    }
}
