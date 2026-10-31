using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU1): the director on the flat ground. Scripted KOs run the five waves (the design's totals, the breaks, the Warboss last) to the
/// end, won, and nothing else ends the match (not the score limit, not the marines' deaths); a scripted loss (the lives pool spent, every player dead) ends
/// it for Chaos and the next round starts fresh; the scale, the caps (the wave's and the hard 400), at most 6 releases a tick, gates at least 20 m from
/// every player, the respawn at the spawn farthest from the horde, and the break's refill.
/// </summary>
public class MusouDirectorTests
{
    static World NewWorld(MusouRules rules, int players, MatchRules? match = null, IReadOnlyList<SpawnPoint>? spawns = null)
    {
        var w = new World(MusouSimTests.Base with { MusouRules = rules }, null, spawns) { Rules = match ?? MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        for (int id = 1; id <= players; id++) w.Spawn(id, w.AssignClass(0));
        return w;
    }

    static void KoAll(World w, int by = 1)
    {
        Span<ushort> ids = stackalloc ushort[MusouSim.Capacity];
        int n = w.Musou!.Query(Vector3.Zero, 1e6f, ids, 1e6f);
        foreach (var id in ids[..n]) w.Musou.Damage(id, by, 1e6f, Vector3.UnitY, out _);
    }

    [Fact]
    public void Scripted_kos_run_the_five_waves_and_the_warboss_to_a_win()
    {
        // The default score limit (41): the KOs pass it many times over and the match still ends only with the Warboss.
        var w = NewWorld(MusouRules.Default with { PlayerDamageScale = 0f }, players: 1, match: new MatchRules { CountdownTime = 0f, TimeLimit = 0f });
        var d = w.MusouDirector!;
        var totals = new Dictionary<int, int>();
        var phases = new List<MusouPhase>();
        int maxReleases = 0;
        for (int t = 0; t < 30 * 300 && w.State != MatchState.Ended; t++)
        {
            int before = w.Musou!.Count;
            w.Step(_ => default);
            maxReleases = Math.Max(maxReleases, w.Musou.Count - before);
            if (d.Phase != MusouPhase.Waiting && (phases.Count == 0 || phases[^1] != d.Phase)) phases.Add(d.Phase);
            if (d.Phase == MusouPhase.Wave) totals[d.Wave] = d.WaveTotal;
            Assert.Equal(d.Phase, w.Musou.Director.Phase);
            if (w.State == MatchState.Playing) Assert.True(w.Musou.Alive <= d.MaxAlive + (d.BossId != 0 ? 1 : 0));
            KoAll(w);
        }
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.SpaceMarines, w.Winner);
        Assert.Equal(MusouPhase.Won, d.Phase);
        Assert.Equal([MusouPhase.Wave, MusouPhase.Break, MusouPhase.Wave, MusouPhase.Break, MusouPhase.Wave, MusouPhase.Break, MusouPhase.Wave, MusouPhase.Break,
            MusouPhase.Wave, MusouPhase.Boss, MusouPhase.Won], phases);
        Assert.Equal(new Dictionary<int, int> { [1] = 80, [2] = 120, [3] = 162, [4] = 223, [5] = 304 }, totals);
        Assert.True(maxReleases <= 6);
        int all = 80 + 120 + 162 + 223 + 304 + 1;
        Assert.Equal(all, d.Kos);
        Assert.Equal(all, w.Scores[1].Kills);
        Assert.Equal(40, w.ScoreOf(Team.SpaceMarines));   // the KOs, kept under the limit so only the director ends the match
        Assert.Equal(0, w.ScoreOf(Team.Chaos));
        Assert.True(d.Scores[1].Points > all * 10);   // a chain every tick: the multiplier
        Assert.Equal(3f, d.Scores[1].Multiplier);
    }

    [Fact]
    public void A_scripted_loss_spends_the_pool_ends_for_chaos_and_the_next_round_starts_fresh()
    {
        SpawnPoint[] spawns = [new(new Vector3(-25f, 0f, 0f), 0f), new(new Vector3(25f, 0f, 0f), 0f), new(new Vector3(0f, 25f, 0f), 0f)];
        var match = MusouSimTests.Endless with { RestartDelay = 2f, CountdownTime = 1f };
        var w = NewWorld(MusouRules.Default with { PlayerDamageScale = 50f }, players: 1, match: match, spawns: spawns);
        var d = w.MusouDirector!;
        int respawns = 0;
        for (int t = 0; t < 30 * 600 && w.State != MatchState.Ended; t++)
        {
            bool inPlay = d.Phase != MusouPhase.Waiting;   // the countdown's respawn takes no life
            w.Step(_ => default);
            if (inPlay && w.LastRespawns.Contains(1))
            {
                respawns++;
                // The spawn farthest from the horde (chosen before the enemies' move this tick: within a step of it).
                var at = w.Players[1].Position;
                float mine = MathF.Sqrt(w.Musou!.NearestSquared(at));
                Assert.Contains(spawns, s => s.Position == at);
                Assert.All(spawns, s => Assert.True(MathF.Sqrt(w.Musou.NearestSquared(s.Position)) <= mine + 0.5f));
            }
            Assert.True(w.ScoreOf(Team.Chaos) <= 1);   // a death of this tick at most, never a running Chaos score
        }
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.Chaos, w.Winner);
        Assert.Equal(MusouPhase.Lost, d.Phase);
        Assert.Equal(0, d.Lives);
        Assert.Equal(3, respawns);   // the pool of one player
        Assert.Equal(4, w.Scores[1].Deaths);
        // Nobody comes back on the end screen.
        for (int t = 0; t < 50; t++) { w.Step(_ => default); Assert.False(w.Players[1].Alive); }
        // The restart: the countdown, then play with a fresh pool and no enemies left over.
        for (int t = 0; t < 30 * 4 && !(w.State == MatchState.Playing && d.Phase == MusouPhase.Wave); t++)
        {
            w.Step(_ => default);
            if (w.State == MatchState.Countdown) Assert.Equal(0, w.Musou!.Count);
        }
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal((1, 3), (d.Wave, d.Lives));
        Assert.True(w.Players[1].Alive);
    }

    [Fact]
    public void Wave_sizes_scale_with_the_players_and_the_hard_cap_holds()
    {
        var three = NewWorld(MusouRules.Default with { PlayerDamageScale = 0f }, players: 3);
        three.Step(_ => default);
        three.Step(_ => default);
        var d = three.MusouDirector!;
        Assert.Equal((1, 160, 160, 7, 2f), (d.Wave, d.WaveTotal, d.MaxAlive, d.Lives, d.Scale));

        var last = MusouRules.Default with { PlayerDamageScale = 0f, Waves = [MusouRules.Default.Waves[4]] };
        var eight = NewWorld(last, players: 8);
        for (int t = 0; t < 200; t++) eight.Step(_ => default);
        Assert.Equal((400, 400), (eight.MusouDirector!.MaxAlive, eight.Musou!.Alive));   // 250 x 4.5 = 1125, capped at 400
        Assert.Equal(1350 + 4, eight.MusouDirector.WaveTotal);
        // A ninth player joining adds lives.
        int lives = eight.MusouDirector.Lives;
        eight.Spawn(9, eight.AssignClass(0));
        eight.Step(_ => default);
        Assert.Equal(lives + 2, eight.MusouDirector.Lives);
    }

    [Fact]
    public void Gates_release_only_away_from_the_players()
    {
        var sim = new MusouSim(null);
        var d = new MusouDirector(sim, []);
        Assert.Equal(8, d.Gates.Count);
        d.Start(1);
        // The player stands on a gate: no enemy comes within 20 m of it.
        MusouTarget[] targets = [new(1, d.Gates[0], true)];
        var buffer = new MusouEnemy[MusouSim.Capacity];
        for (uint t = 1; t <= 20; t++)
        {
            sim.BeginTick(t);
            int before = sim.Count;
            d.Step(targets);
            Assert.True(sim.Count - before <= 6);
        }
        int n = sim.CopyTo(buffer);
        Assert.Equal(80, n);
        Assert.All(buffer[..n], e => Assert.True(Vector3.Distance(e.Position, d.Gates[0]) >= 20f));
        Assert.Equal(56, buffer[..n].Count(e => e.Type == MusouEnemyType.Boy));   // 70 %
        Assert.Equal(24, buffer[..n].Count(e => e.Type == MusouEnemyType.ShootaBoy));
    }

    [Fact]
    public void The_break_refills_health_and_ammunition()
    {
        var tiny = MusouRules.Default with { Waves = [new(4, 4, [(MusouEnemyType.Boy, 1f)]), new(4, 4, [(MusouEnemyType.Boy, 1f)])], BreakSeconds = 1f, PlayerDamageScale = 0f };
        var w = NewWorld(tiny, players: 1);
        var d = w.MusouDirector!;
        // Shoot for a while with no enemy out (reloads spend the reserve), then KO the wave.
        d.Hold = true;
        for (int t = 0; t < 30 * 8; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        Assert.True(w.Players[1].Reserve < 270);
        d.Hold = false;
        while (d.Phase == MusouPhase.Wave) { KoAll(w); w.Step(_ => default); }
        Assert.Equal(MusouPhase.Break, d.Phase);
        Assert.Equal(270, w.Players[1].Reserve);
        Assert.Equal(30, w.Musou!.Director.BreakTicksLeft);
        for (int t = 0; t < 30; t++) w.Step(_ => default);
        Assert.Equal((MusouPhase.Wave, 2), (d.Phase, d.Wave));
    }
}
