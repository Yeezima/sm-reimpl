using System.Numerics;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The owner's tuning of 2026-11-11 (musou-tuning-1111b; Musou Mode only): the chain lightning's jump "16 m (double)" and "make the damage not falloff anymore - it always stays constant"
/// (<see cref="LibrarianRules"/>, the one rule <see cref="LibrarianChain"/> of the server and the client's foresight; tag 16's jumps in 0.2 m so observers draw the whole chain), and the human players'
/// ranged x1.2 "Add it to orbital strikes" (<see cref="TfwCombatRules.PlayerRangedDamageScale"/> on the shells of a strike a human called; an AI caller x1). The GPU spray cap of 600 is
/// <c>FxGpuLayoutTests</c>' (SmFormats.Tests).
/// </summary>
public class MusouTuning1111bTests
{
    const int Rate = 30;
    const float Dt = 1f / Rate;

    // ---------------------------------------------------------------- the chain lightning: 16 m jumps

    static LibrarianCandidate Boy(ushort id, float x, float y) => new(id, new Vector3(x, y, 0f), MusouRules.Default.Of(MusouEnemyType.Boy).Height);

    [Fact]
    public void The_shared_chain_rule_jumps_16_metres_and_no_further()
    {
        Assert.Equal(16f, LibrarianRules.JumpRadius);   // owner 2026-11-11: "16 m (double)" (8 m before)
        Assert.Equal(4f, LibrarianRules.VerticalReach);  // unchanged
        var eye = MusouSim.Chest(Vector3.Zero, MusouRules.Default.Of(MusouEnemyType.Boy).Height);
        // On the aim at 10 m, then 15.9 m on (past the old 8 m), then 16.5 m on: the chain ends before the last.
        LibrarianCandidate[] crowd = [Boy(1, 0f, 10f), Boy(2, 0f, 25.9f), Boy(3, 0f, 42.4f)];
        var links = new List<LibrarianLink>();
        Span<bool> struck = stackalloc bool[crowd.Length];
        Span<int> chain = stackalloc int[LibrarianRules.MaxTargets];
        int n = LibrarianChain.Run(Vector3.Zero, eye, 0f, 0f, crowd, null, links, struck, chain, new LibrarianChain.Scratch());
        Assert.Equal(2, n);
        Assert.Equal(new[] { true, true, false }, struck.ToArray());
        Assert.Equal(2, links.Count);
        // The client's foresight is the same function: the same chain.
        var foreseen = new List<LibrarianLink>();
        Span<bool> seen = stackalloc bool[crowd.Length];
        Assert.Equal(2, LibrarianPrediction.Chain(Vector3.Zero, eye, 0f, 0f, crowd, null, foreseen, seen));
        Assert.Equal(links, foreseen);
    }

    /// <summary>
    /// A line of <paramref name="count"/> Boys about 12 m apart (past the old 8 m jump): the first on the aim 10 m ahead, the rest running off to the side (out of the aim cone, so the first is the
    /// primary), zig-zagging 1.5 m; each with 10,000 health.
    /// </summary>
    static (MusouSim Sim, List<ushort> Ids, Vector3 Eye) Line(int count)
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        var ids = new List<ushort>();
        for (int k = 0; k < count; k++) ids.Add(sim.Spawn(MusouEnemyType.Boy, new Vector3(12f * k, k % 2 == 0 ? 10f : 11.5f, 0f), 0f, 10000f));
        Assert.DoesNotContain((ushort)0, ids);
        return (sim, ids, MusouSim.Chest(Vector3.Zero, sim.Body(MusouEnemyType.Boy).Height));
    }

    [Fact]
    public void Every_ork_down_the_chain_takes_the_full_damage_the_twentieth_and_the_fortieth_as_the_first()
    {
        var (sim, ids, eye) = Line(45);
        var psyker = new LibrarianPsyker();
        for (int t = 1; t <= Rate; t++)
        {
            sim.BeginTick((uint)t);
            psyker.Step(sim, new LibrarianCaster(1, Vector3.Zero, eye, 0f, 0f), new LibrarianPress(true, false), Dt);
            Assert.Equal(LibrarianRules.MaxTargets, psyker.LastStruck);
        }
        float Lost(int k) => 10000f - (sim.TryGet(ids[k], out var e) ? e.Health : 0f);
        Assert.Equal(112.5f, Lost(0), 2);   // one second held: 112.5 (owner 2026-11-11: "+25% range and damage")
        Assert.Equal(Lost(0), Lost(19), 3);   // the 20th: 5.1 a second with the old x0.85 a jump
        Assert.Equal(Lost(0), Lost(39), 3);   // the 40th: 0.2 a second before
        for (int k = 0; k < LibrarianRules.MaxTargets; k++) Assert.Equal(Lost(0), Lost(k), 3);
        for (int k = LibrarianRules.MaxTargets; k < ids.Count; k++) Assert.Equal(0f, Lost(k));   // 40 at most, as before
    }

    [Fact]
    public void The_drawn_chain_is_the_struck_chain_with_16_metre_jumps_and_the_wire_carries_it_whole()
    {
        var (sim, ids, eye) = Line(30);
        var candidates = ids.Select(id => { sim.TryGet(id, out var e); return new LibrarianCandidate(id, e.Position, sim.Body(e.Type).Height); }).ToArray();
        var foreseen = new List<LibrarianLink>();
        var seen = new bool[candidates.Length];
        int drawn = LibrarianPrediction.Chain(Vector3.Zero, eye, 0f, 0f, candidates, null, foreseen, seen);
        Assert.Equal(30, drawn);

        var psyker = new LibrarianPsyker();
        sim.BeginTick(1);
        psyker.Step(sim, new LibrarianCaster(1, Vector3.Zero, eye, 0f, 0f), new LibrarianPress(true, false), Dt);
        Assert.Equal(30, psyker.LastStruck);
        Assert.Equal(foreseen, psyker.Links);   // the links the own client draws are the server's
        var damaged = sim.Events.Where(e => e.Kind == MusouEventKind.Hit).Select(e => e.Enemy).ToHashSet();
        Assert.Equal(ids.Where((_, k) => seen[k]).Select(id => (int)id).ToHashSet(), damaged.Select(id => (int)id).ToHashSet());   // every Ork drawn is damaged, nothing else

        // Tag 16: every 12 m jump fits a byte (0.2 m steps, 25.4 m), so another player draws the whole chain.
        Assert.Equal(30, LibrarianWire.Drawable(psyker.Links));
        var view = new LibrarianView(1, 10f, false, true, 0f, 0f, 0, 0, psyker.Links);
        var read = LibrarianWire.Read(LibrarianWire.Write([view]))!;
        Assert.Equal(30, read[0].Links.Count);
        for (int k = 0; k < 30; k++) Assert.True(Vector3.Distance(read[0].Links[k].To, psyker.Links[k].To) <= 0.1733f, $"link {k}");
        Assert.Equal(0.2f, LibrarianWire.JumpStep, 6);
    }

    [Fact]
    public void The_customizer_shows_the_16_metre_jump_and_no_fall_off()
    {
        var chain = Assert.Single(LibrarianPowers.Present(), p => p.Name == "Chain lightning");
        Assert.Contains("jumps up to 16 m from Ork to Ork", chain.Text);
        Assert.Contains("112.5 damage a second to every Ork in the chain", chain.Text);
        Assert.DoesNotContain("each jump", chain.Text);
        Assert.Null(LibrarianPowers.Value("JumpFalloff"));
    }

    // ---------------------------------------------------------------- the orbital strike: x1.2 for a human caller

    const int Human = TfwRig.Human;
    const int Marine = TfwContracts.PuppetIdFirst;
    static readonly Vector3 Open = new(60f, -60f, 0f);

    /// <summary>A battle with the field emptied of its own Orks and Guardsmen, the schedule off, the crowd held still (as <c>TfwFireTests</c>), and a Boss Ork of 1,000,000 health at <see cref="Open"/>.</summary>
    static (World W, ushort Boss) Battle(bool before = false)
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        w.Tfw!.Fire.NoSchedule = true;
        w.Musou!.Clear();
        w.TfwGuards!.Clear();
        w.TfwRespawnAt(Human, new Vector3(0f, -100f, 0f), 0f);
        w.Musou.Frozen = true;
        if (before) w.Musou.War = w.Musou.War! with { PlayerRangedDamageScale = 1f };
        ushort boss = w.Musou.Spawn(MusouEnemyType.BossNob, Open, 0f, 1e6f);
        Assert.NotEqual(0, boss);
        return (w, boss);
    }

    static float Lost(World w, ushort id) => 1e6f - (w.Musou!.TryGet(id, out var e) ? e.Health : 0f);

    /// <summary>An AI Marine in the battle <paramref name="w"/>, standing well away from <see cref="Open"/>.</summary>
    static void AddMarine(World w)
    {
        w.Tfw!.PuppetBrain = (_, p) => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None);
        if (!w.Players.ContainsKey(Marine)) { w.Spawn(Marine, 0); TfwRig.Steps(w, 1); }
        Assert.True(w.TfwRespawnAt(Marine, new Vector3(10f, -100f, 0f), 0f));
        TfwRig.Steps(w, 1);
        Assert.True(w.Players[Marine].Alive);
        Assert.False(TfwCombatRules.ScalesPlayer(Marine));
        Assert.True(TfwCombatRules.ScalesPlayer(Human));
    }

    [Fact]
    public void A_shell_of_a_humans_strike_deals_a_boss_1_2_times_and_of_an_ai_marines_or_nobodys_1_time()
    {
        var lost = new Dictionary<string, float>();
        foreach (var (name, caller) in new[] { ("human", Human), ("marine", Marine), ("nobody", 0) })
        {
            var (w, boss) = Battle();
            AddMarine(w);
            w.Musou!.BeginTick(w.Tick);
            w.TfwShellForTests(Open, caller);
            lost[name] = Lost(w, boss);
        }
        Assert.Equal(TfwFireRules.BossDamage, lost["nobody"], 3);   // 30: a Boss Ork is only scratched (owner, 2026-11-06)
        Assert.Equal(TfwFireRules.BossDamage * 1.2f, lost["human"], 3);   // owner 2026-11-11: "Add it to orbital strikes"
        Assert.Equal(TfwFireRules.BossDamage, lost["marine"], 3);   // AI Marines keep theirs ("Musou, players only")
    }

    [Fact]
    public void A_humans_called_orbital_strike_deals_1_2_times_what_it_did_and_the_players_take_what_they_took()
    {
        var lost = new float[2];
        var hurt = new float[2];
        var shells = new int[2];
        foreach (bool before in new[] { false, true })
        {
            var (w, boss) = Battle(before);
            var fire = w.Tfw!.Fire;
            float was = w.Players[Human].Health + w.Players[Human].Shield;
            Assert.True(w.TfwOrbital(Human, Open));
            Assert.All(fire.Shells, s => Assert.Equal(Human, s.Caller));   // every shell carries its caller
            for (int t = 0; t < 30 * Rate && fire.Shells.Count > 0; t++) TfwRig.Steps(w, 1);
            Assert.Empty(fire.Shells);
            lost[before ? 1 : 0] = Lost(w, boss);
            hurt[before ? 1 : 0] = was - (w.Players[Human].Health + w.Players[Human].Shield);
            shells[before ? 1 : 0] = fire.ShellsLanded;
        }
        Assert.Equal(shells[1], shells[0]);   // the same barrage in both worlds
        Assert.True(lost[1] >= TfwFireRules.BossDamage, $"{lost[1]}");
        Assert.Equal(lost[1] * 1.2f, lost[0], 2);
        Assert.Equal(hurt[1], hurt[0], 3);   // never the damage a player takes
    }

    [Fact]
    public void The_shells_kills_still_credit_nobody()
    {
        var (w, _) = Battle();
        ushort boy = w.Musou!.Spawn(MusouEnemyType.Boy, Open + new Vector3(3f, 0f, 0f), 0f, w.Musou.Rules.Of(MusouEnemyType.Boy).Health);
        w.Musou.BeginTick(w.Tick);
        w.TfwShellForTests(Open, Human);
        var death = Assert.Single(w.Musou.Events, x => x.Kind == MusouEventKind.Death && x.Enemy == boy);
        Assert.Equal((0, (byte)MusouDeathKind.Barrage), (death.Player, death.Value));
    }
}
