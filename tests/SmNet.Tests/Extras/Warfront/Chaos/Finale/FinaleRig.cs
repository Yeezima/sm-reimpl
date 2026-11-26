using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Chaos.Finale;
using SmSim.Extras.Warfront.Fortress;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5's rigs: a Chaos fortress on the fortress tests' stub arena with N humans (player ids 1 to N, the stand-in campaign keys them by id), the Chosen killed, the duel stepped.</summary>
internal static class FinaleRig
{
    public const int Human = TfwRig.Human;

    /// <summary>A Chaos fortress battle of <paramref name="humans"/> humans, landed and stepped twice.</summary>
    public static (World World, TfwBattle Battle) Fortress(int humans = 1, SimConfig? config = null, bool guardsmen = false)
    {
        var w = TfwRig.War(config: config ?? WarfrontChaosRosterTests.ChaosConfig);
        w.Tfw!.Guardsmen = guardsmen;
        for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Steps(w, 5);
        w.Tfw.Warfront!.Fortress = WarfrontFortressSetup.For(new WarfrontFortressBattleTests.StubArena());
        w.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        var b = TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        Assert.True(b.ChaosFortress);
        return (w, b);
    }

    public static WarfrontChaosRoster Roster(World w) => w.Tfw!.Warfront!.ChaosRoster!;

    /// <summary>Every Chosen dies by <paramref name="by"/>; then <paramref name="ticks"/> ticks (the snapshot is the next tick's).</summary>
    public static void KillChosen(World w, TfwBattle b, int by = Human, int ticks = 2)
    {
        Assert.True(b.FortressBossOrksMade > 0);
        foreach (ushort id in b.BossOrks.ToList()) w.Musou!.Damage(id, by, 1e7f, Vector3.Zero, out _);
        Steps(w, ticks);
    }

    /// <summary>A Chaos fortress whose duel has begun (the Chosen dead; the first challenger on the plaza).</summary>
    public static (World World, TfwBattle Battle, WarfrontDuel Duel) Duel(int humans = 1, SimConfig? config = null)
    {
        var (w, b) = Fortress(humans, config);
        KillChosen(w, b);
        var d = Assert.IsType<WarfrontDuel>(w.WarfrontChaosDuel);
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
        return (w, b, d);
    }

    public static void Steps(World w, int ticks, Func<int, PlayerInput>? input = null)
    {
        for (int k = 0; k < ticks; k++) w.Step(input ?? (_ => default));
    }

    public static MusouEnemy Lord(World w, TfwBattle b) { Assert.True(w.Musou!.TryGet(b.BossId, out var l)); return l; }

    public static int HealthBits(World w, TfwBattle b) => BitConverter.SingleToInt32Bits(Lord(w, b).Health);

    public static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    public static int Alive(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Count(e => e.Alive);
    }
}
