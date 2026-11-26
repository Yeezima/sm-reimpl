using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>
/// Executions X4: Warfront's executions change nothing until a press. A seeded Ork lane battle (<c>TfwRig</c>'s war under the Warfront profile with X4's chainsword classes and Ork rows, lane B,
/// 900 scripted ticks of moving, turning and firing, no Execute) gives the same state as on X3's tip (eb865746, before X4): the war's state hash, every living Ork's type, place, yaw, state and
/// health, the human's place, health and armour, and the battle's kills. The Musou profile never holds an Ork even with Execute pressed (the bit is dropped without the flag).
/// The classic 900-tick replay and the Musou hashes are <c>MusouIsolationTests</c>' and the Warfront pins' (unchanged by X4: no core or Musou-version change).
/// </summary>
public class ExecutionWarfrontIsolationTests(ITestOutputHelper output)
{
    static PlayerInput Script(int k) => new(0, MoveX: (k / 90) % 2 == 0 ? 0.5f : -0.5f, MoveY: k % 120 < 80 ? 1f : 0f, Yaw: (k % 360) * 0.0174f,
        Buttons: k % 30 < 12 ? InputButtons.Fire : k % 47 == 0 ? InputButtons.Melee : InputButtons.None);

    static ulong Hash(World w)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h = (h ^ v) * 1099511628211UL; }
        long Q(float v) => (long)MathF.Round(v * 100f);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        foreach (var e in all.Take(n).OrderBy(e => e.Id))
        {
            Mix(e.Id); Mix((ulong)e.Type); Mix((ulong)e.State);
            Mix((ulong)Q(e.Position.X)); Mix((ulong)Q(e.Position.Y)); Mix((ulong)Q(e.Position.Z)); Mix((ulong)Q(e.Yaw)); Mix((ulong)Q(e.Health));
        }
        foreach (var (id, p) in w.Players.OrderBy(kv => kv.Key))
        {
            Mix((ulong)id); Mix(p.Alive ? 1UL : 0UL);
            Mix((ulong)Q(p.Position.X)); Mix((ulong)Q(p.Position.Y)); Mix((ulong)Q(p.Position.Z)); Mix((ulong)Q(p.Health)); Mix((ulong)Q(p.Shield));
        }
        Mix(w.Tfw!.War.StateHash());
        Mix((ulong)(w.Tfw.Battle?.OrksKilled ?? -1));
        Mix(w.Tick);
        return h;
    }

    /// <summary>Captured on X3's tip (eb865746) with this test alone added: the Ork lane battle without any press.</summary>
    const ulong LaneBattleHash = 0x71E39FD05F2D90FEUL;

    [Fact]
    public void OrkLaneBattleHashUnchangedWithoutPress()
    {
        var w = ExecutionOrkKit.Battle();
        for (int k = 0; k < 900; k++) w.Step(_ => Script(k));
        ulong h = Hash(w);
        output.WriteLine($"lane battle 0x{h:X16}UL; kills {w.Tfw!.Battle?.OrksKilled}; tick {w.Tick}");
        Assert.Empty(w.ExecutionsLive);
        Assert.Equal(LaneBattleHash, h);
    }

    /// <summary>Musou Mode (no Executions flag) with Execute held and tapped next to Orks: nothing is held, nothing runs, no overshield is kept.</summary>
    [Fact]
    public void MusouHeldSetAlwaysEmpty()
    {
        var c = ExecutionOrkKit.Config(extras: ContentExtras.Musou);
        Assert.False(c.Extras.HasFlag(ContentExtras.Executions));
        Assert.False(c.KeepsOvershield);
        var w = TfwRig.War(config: c);
        TfwRig.Deploy(w, TfwLane.B);
        ExecutionOrkKit.OrkAhead(w);
        for (int k = 0; k < 120; k++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, w.Players[1].Yaw, k % 2 == 0 ? InputButtons.Execute : InputButtons.None, 0f, w.Tick));
            Assert.Equal(0, w.Musou!.ExecutionsHeldCount);
            Assert.Empty(w.ExecutionsLive);
            Assert.Empty(w.ExecutionsRefusedThisTick);
        }
    }

    /// <summary>The Warfront hooks do nothing outside Warfront: Classic PvP Extended's damage-taken scale and overshield rule are X3's (no scale change, nothing kept).</summary>
    [Fact]
    public void Classic_extended_is_untouched_by_the_warfront_hooks()
    {
        var c = ExecutionTestKit.Config();
        Assert.False(c.KeepsOvershield);
        var w = ExecutionTestKit.Arena(c, (ExecutionTestKit.Tactical, new System.Numerics.Vector3(0f, 0f, 0f)), (ExecutionTestKit.Chaos, new System.Numerics.Vector3(0f, 1.5f, 0f)));
        w.ExecutionsStartForTests(new ExecutionRun(1, ExecutionTargetKind.Player, 2, 0, 1.3f, w.Players[1].Position, w.Players[1].Position, 0f, 0, w.Tick, false));
        Assert.True(w.ExecutionsLocked(1));
        Assert.Equal(1f, w.DamageTakenScale(1));   // Classic: the executor is not invulnerable (X3; owner brief)
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
    }
}
