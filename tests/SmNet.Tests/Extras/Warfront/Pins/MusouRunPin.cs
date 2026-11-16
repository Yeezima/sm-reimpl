using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W0 pins (Warfront): a deterministic scripted Musou war, no server and no install, built as <c>TfwRunTests</c> builds its world (<c>TfwRig.War</c>, StartTfw, 30 Hz). One human with a fixed input script:
/// deploy to lane B, 900 battle ticks, a pickup, deploy to lane A, 900 battle ticks. Pinned: the war's state hash after each of the five steps, the phase after each, and the end state. Captured on the
/// untouched baseline (commit 5428594e). Warfront must leave this run exactly as it is. A second run (won battle, pickup, lost battle) follows.
/// These pins live under Extras/Warfront and do not run in a -p:SmWarfront=false build, by design.
/// </summary>
public class MusouRunPin(ITestOutputHelper output)
{
    static PlayerInput Script(int k) => new(0, MoveX: (k / 90) % 2 == 0 ? 0.5f : -0.5f, MoveY: k % 120 < 80 ? 1f : 0f, Yaw: (k % 360) * 0.0174f, Buttons: k % 30 < 12 ? InputButtons.Fire : InputButtons.None);

    static void Battle(World w, int ticks)
    {
        for (int k = 0; k < ticks; k++) w.Step(_ => Script(k));
    }

    static ulong EnemyHash(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h = (h ^ v) * 1099511628211UL; }
        foreach (var e in all.Take(n).Where(e => e.Alive).OrderBy(e => e.Id))
        {
            Mix((ulong)e.Type);
            Mix((ulong)(long)MathF.Round(e.Position.X * 10f)); Mix((ulong)(long)MathF.Round(e.Position.Y * 10f)); Mix((ulong)(long)MathF.Round(e.Position.Z * 10f));
            Mix((ulong)(long)MathF.Round(e.Health));
        }
        return h;
    }

    static readonly ulong[] StepHashes = [0x7B694F28F6C678EDUL, 0x77D53A5F0C943EE8UL, 0x0C9766B564CCC176UL, 0x8302916050FA5A04UL, 0xA1640B6C53B74D59UL];
    const string Phases = "Battle,Battle,Hub,Battle,Battle";
    const int OrksKilled = 226, Secured = 0, Transport = 3, Supplies = 4;
    const uint Total = 20;
    const ulong Enemies = 0x18ABAA6AE660F303UL;

    [Fact]
    public void The_scripted_war_is_as_before_Warfront()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var hashes = new List<ulong>();
        var phases = new List<string>();
        void Mark() { hashes.Add(run.War.StateHash()); phases.Add(run.Phase.ToString()); }

        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.B);
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds + 0.2f);
        Mark();                                                   // 1: landed on lane B
        Battle(w, 900); Mark();                                   // 2: 900 battle ticks
        run.PickupForTests(w); Mark();                            // 3: the pickup
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.A);
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds + 0.2f);
        Mark();                                                   // 4: landed on lane A
        Battle(w, 900); Mark();                                   // 5: 900 battle ticks
        var battle = run.Battle;
        string phaseText = string.Join(",", phases);
        output.WriteLine($"hashes {string.Join(", ", hashes.Select(h => $"0x{h:X16}UL"))}");
        output.WriteLine($"phases {phaseText}");
        output.WriteLine($"battle {(battle is null ? "none" : $"killed {battle.OrksKilled}, secured {battle.Secured}")}; score {run.Score.TotalOf(TfwRig.Human)}; transport {run.Transport}, supplies {run.Supplies}; enemies 0x{EnemyHash(w):X16}UL; tick {w.Tick}");
        Assert.Equal(StepHashes, hashes);
        Assert.Equal(Phases, phaseText);
        Assert.NotNull(battle);
        Assert.Equal(OrksKilled, battle!.OrksKilled);
        Assert.Equal(Secured, battle.Secured);
        Assert.Equal(Total, run.Score.TotalOf(TfwRig.Human));
        Assert.Equal(Transport, run.Transport);
        Assert.Equal(Supplies, run.Supplies);
        Assert.Equal(Enemies, EnemyHash(w));
    }

    // ---------------------------------------------------------------- the second run: a won battle, the pickup, a lost one

    static string Line(World w, string step)
    {
        var run = w.Tfw!;
        var b = run.Battle;
        return $"{step}: {run.Phase} secured {b?.Secured ?? -1} kills {b?.OrksKilled ?? -1} transport {run.Transport} supplies {run.Supplies} score {run.Score.TotalOf(TfwRig.Human)} laneA front {run.War.LaneState(TfwLane.A).Front} enemies 0x{EnemyHash(w):X16} state 0x{run.War.StateHash():X16}";
    }

    static readonly string[] WonRun =
    [
        "deployed: Battle secured 0 kills 0 transport 4 supplies 4 score 0 laneA front 1 enemies 0x1576E0EB8448015B state 0xF9CF3CC995A41259",
        "secured three: Battle secured 3 kills 997 transport 6 supplies 5 score 46210 laneA front 1 enemies 0x79DA83D55872BD05 state 0x37FDE11D8C2C70C4",
        "commander dead, won, wave sent: Battle secured 3 kills 998 transport 6 supplies 5 score 48210 laneA front 2 enemies 0xF018DF1A7996F0E4 state 0x0075A9D63AA477A7",
        "picked up, hub: Hub secured -1 kills -1 transport 6 supplies 3 score 48210 laneA front 2 enemies 0xCBF29CE484222325 state 0x14705A1F32702BEB",
        "died, window out, extraction paid: Hub secured -1 kills -1 transport 5 supplies 1 score 48210 laneA front 2 enemies 0xCBF29CE484222325 state 0x0F83E341AD2E1BF2",
    ];
    const int LaneAFrontAfterWin = 2, VictoryWaveSent = 500;

    [Fact]
    public void The_won_battle_the_pickup_and_the_lost_battle_are_as_before_Warfront()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var lines = new List<string>();
        var battle = TfwRig.Deploy(w);
        lines.Add(Line(w, "deployed"));
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        lines.Add(Line(w, "secured three"));                      // the commander arrives with the third
        ushort commander = battle.BossId;
        Assert.NotEqual(0, commander);
        w.Musou!.Damage(commander, TfwRig.Human, 1e6f, System.Numerics.Vector3.Zero, out bool killed);
        Assert.True(killed);
        TfwRig.Seconds(w, 2f);
        Assert.True(battle.Won);
        lines.Add(Line(w, "commander dead, won, wave sent"));
        int sent = battle.VictoryWaveSent;
        int front = run.War.LaneState(TfwLane.A).Front;
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        lines.Add(Line(w, "picked up, hub"));
        int frontAfter = run.War.LaneState(TfwLane.A).Front;
        run.AllyUp = _ => true;
        TfwRig.Deploy(w);
        w.TfwDismissMarines();
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        TfwRig.Seconds(w, TfwBattleRules.ReviveWindowSeconds + 1f);
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.5f);
        lines.Add(Line(w, "died, window out, extraction paid"));
        output.WriteLine($"front before {front} after {frontAfter}; wave sent {sent}");
        foreach (var l in lines) output.WriteLine($"\"{l}\",");
        Assert.Equal(WonRun, lines);
        Assert.Equal(LaneAFrontAfterWin, frontAfter);
        Assert.Equal(VictoryWaveSent, sent);
    }
}
