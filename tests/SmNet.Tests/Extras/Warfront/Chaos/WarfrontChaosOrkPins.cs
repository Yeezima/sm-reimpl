using SmContent.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Fortress;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos;

/// <summary>
/// Chaos Warfront C2: the Orks battles are unchanged by the Chaos roster. Seeded lane, HQ and fortress battles of the Orks run as they did on the Chaos base (claude/chaos-base-1125, before C2): the enemy
/// pool's state after 20 s hashes to the value pinned from that commit. A change that moves an Orks battle (on purpose) re-pins these with the reason.
/// </summary>
public class WarfrontChaosOrkPins(ITestOutputHelper output)
{
    /// <summary>A lane battle of this config, deployed and landed, the Weirdboyz and the Scrap Meks left on (so a Chaos battle shows that their spawners are off).</summary>
    internal static (World World, TfwBattle Battle) Lane(SimConfig config, bool guardsmen = false)
    {
        var w = TfwRig.War(config: config);
        w.Tfw!.Guardsmen = guardsmen;
        w.TfwMeksOn = true;
        var battle = TfwRig.Deploy(w);
        return (w, battle);
    }

    /// <summary>The HQ defence of this config (as <c>WarfrontHqBattleTests.Hq</c>, the Orks walking).</summary>
    internal static (World World, TfwBattle Battle) Hq(SimConfig config)
    {
        var map = HqMap.Value;
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = map.StartAreas };
        Assert.True(w.StartTfw(null, map.Tfw!.Field));
        w.Tfw!.AllyUp = _ => false;
        w.Tfw.Allies = false;
        w.Tfw.Guardsmen = false;
        w.Tfw.Patrols = false;
        w.TfwMeksOn = true;
        w.Spawn(TfwRig.Human, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        w.Tfw.Warfront!.Target = WarfrontTargetKind.Hq;
        return (w, TfwRig.Deploy(w));
    }

    static readonly Lazy<WarfrontLoadedMap> HqMap = new(() => WarfrontMaps.Load(WarfrontMaps.HqName, out _)!);

    /// <summary>The fortress battle of this config on the fortress tests' stub arena.</summary>
    internal static (World World, TfwBattle Battle) Fortress(SimConfig config)
    {
        var w = TfwRig.War(config: config);
        TfwRig.Steps(w, 5);
        w.Tfw!.Warfront!.Fortress = WarfrontFortressSetup.For(new WarfrontFortressBattleTests.StubArena());
        w.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        w.TfwMeksOn = true;
        return (w, TfwRig.Deploy(w));
    }

    /// <summary>The enemy pool's state: every slot in use (id, kind, state, position to the centimetre, health to the hundredth), in id order (FNV-1a, 64 bit).</summary>
    internal static ulong Digest(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        ulong h = 14695981039346656037UL;
        void Mix(long v) { for (int b = 0; b < 8; b++) { h ^= (byte)(v >> (8 * b)); h *= 1099511628211UL; } }
        Mix(n);
        foreach (var e in all.Take(n).OrderBy(e => e.Id))
        {
            Mix(e.Id); Mix((long)e.Type); Mix((long)e.State);
            Mix((long)MathF.Round(e.Position.X * 100f)); Mix((long)MathF.Round(e.Position.Y * 100f)); Mix((long)MathF.Round(e.Position.Z * 100f));
            Mix((long)MathF.Round(e.Health * 100f));
        }
        return h;
    }

    static ulong Run(World w, float seconds)
    {
        TfwRig.Seconds(w, seconds);
        return Digest(w);
    }

    [Fact]
    public void An_orks_lane_battle_is_as_it_was()
    {
        var (w, _) = Lane(WarfrontBattleTests.WarfrontConfig, guardsmen: true);
        ulong d = Run(w, 20f);
        output.WriteLine($"lane 0x{d:X16}");
        Assert.Equal(LanePin, d);
    }

    [Fact]
    public void An_orks_hq_defence_is_as_it_was()
    {
        var (w, _) = Hq(WarfrontBattleTests.WarfrontConfig);
        ulong d = Run(w, 20f);
        output.WriteLine($"hq 0x{d:X16}");
        Assert.Equal(HqPin, d);
    }

    [Fact]
    public void An_orks_fortress_battle_is_as_it_was()
    {
        var (w, _) = Fortress(WarfrontBattleTests.WarfrontConfig);
        ulong d = Run(w, 20f);
        output.WriteLine($"fortress 0x{d:X16}");
        Assert.Equal(FortressPin, d);
    }

    // Pinned from the Chaos base (before C2), by this file's own digest of the same seeded battles.
    const ulong LanePin = 0x3920808E3E1C112DUL, HqPin = 0xA0BAD633A482EE15UL, FortressPin = 0xB14E5D60A2AF5BE5UL;   // claude/chaos-base-1125 @ 6f1cfe8c, before C2
}
