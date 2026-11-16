using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W0 pins (Warfront): a deterministic scripted Musou war, no server and no install, built as <c>TfwRunTests</c> builds its world (<c>TfwRig.War</c>, StartTfw, 30 Hz). One human with a fixed input script:
/// deploy to lane B, 900 battle ticks, a pickup, deploy to lane A, 900 battle ticks. Pinned: the war's state hash after each of the five steps, the phase after each, and the end state. Captured on the
/// untouched baseline (commit 5428594e). Warfront must leave this run exactly as it is.
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
}
