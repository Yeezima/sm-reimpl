using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU1): the director on the flat ground. Scripted KOs run the five waves (the design's totals, the breaks, the Warboss last) to the
/// end, won, and nothing else ends the match (not the score limit, not the marines' deaths); a scripted loss (the lives pool spent, every player dead) ends
/// it for Chaos and the next round starts fresh; the scale, the caps (the wave's and the hard 400), at most 6 releases a tick, gates at least 20 m from
/// every player, every spawn in the Space Marines' spawn room and the Warboss out of the Chaos one (owner, 2026-10-31), and the break's refill.
/// </summary>
public class MusouDirectorTests
{
    /// <summary>The two spawn rooms on the flat ground: the Space Marines' 6 x 6 m at x = -20, the Chaos one at x = +20.</summary>
    internal static readonly StartArea[] Rooms =
        [new(Team.SpaceMarines, new Vector3(-20f, 0f, 0f), new Vector3(6f, 6f, 3f), 0f), new(Team.Chaos, new Vector3(20f, 0f, 0f), new Vector3(6f, 6f, 3f), 0f)];

    internal static bool InRoom(Vector3 p, StartArea room) => MathF.Abs(p.X - room.Center.X) <= room.Size.X / 2f && MathF.Abs(p.Y - room.Center.Y) <= room.Size.Y / 2f;

    static World NewWorld(MusouRules rules, int players, MatchRules? match = null, IReadOnlyList<SpawnPoint>? spawns = null, IReadOnlyList<StartArea>? rooms = null)
    {
        var w = new World(MusouSimTests.Base with { MusouRules = rules }, null, spawns) { Rules = match ?? MusouSimTests.Endless, StartAreas = rooms ?? [] };
        Assert.True(w.StartMusou(null));
        for (int id = 1; id <= players; id++) w.Spawn(id, w.AssignClass(0));
        return w;
    }

    /// <summary>KOs every enemy alive (by player <paramref name="by"/>); <paramref name="bossLast"/>: the Warboss only once he is the last (his death ends the match).</summary>
    static void KoAll(World w, int by = 1, bool bossLast = false)
    {
        Span<ushort> ids = stackalloc ushort[MusouSim.Capacity];
        int n = w.Musou!.Query(Vector3.Zero, 1e6f, ids, 1e6f);
        ushort boss = w.MusouDirector!.BossId;
        foreach (var id in ids[..n])
            if (!bossLast || id != boss || n == 1) w.Musou.Damage(id, by, 1e6f, Vector3.UnitY, out _);
    }

    [Fact]
    public void Scripted_kos_run_the_five_waves_and_the_warboss_to_a_win()
    {
        // The default score limit (41): the KOs pass it many times over and the match still ends only with the Warboss.
        var w = NewWorld(MusouRules.Default with { PlayerDamageScale = 0f }, players: 1, match: new MatchRules { CountdownTime = 0f, TimeLimit = 0f }, rooms: Rooms);
        bool bossSeen = false;
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
            if (!bossSeen && d.BossId != 0 && w.Musou.TryGet(d.BossId, out var boss))
            {
                bossSeen = true;
                Assert.True(InRoom(boss.Position, Rooms[1]), $"the Warboss came at {boss.Position}");   // out of the Chaos spawn room
            }
            KoAll(w, bossLast: true);
        }
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.SpaceMarines, w.Winner);
        Assert.Equal(MusouPhase.Won, d.Phase);
        Assert.True(bossSeen);
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
        var match = MusouSimTests.Endless with { RestartDelay = 2f, CountdownTime = 1f };
        var w = NewWorld(MusouRules.Default with { PlayerDamageScale = 50f }, players: 1, match: match, rooms: Rooms);
        Assert.True(InRoom(w.Players[1].Position, Rooms[0]));   // the first spawn
        var d = w.MusouDirector!;
        int respawns = 0;
        for (int t = 0; t < 30 * 600 && w.State != MatchState.Ended; t++)
        {
            bool inPlay = d.Phase != MusouPhase.Waiting;   // the countdown's respawn takes no life
            w.Step(_ => default);
            if (inPlay && w.LastRespawns.Contains(1))
            {
                respawns++;
                Assert.True(InRoom(w.Players[1].Position, Rooms[0]), $"respawned at {w.Players[1].Position}");   // every respawn in the Space Marines' room
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
    // ---------------------------------------------------------------- the stranded failsafe, the pool, the host's limits

    static World RoomWorld(MusouRules rules)
    {
        // Players spawn outside the closed room; the gates (from the spawn markers and the open nodes that reach them) are outside it too.
        SpawnPoint[] spawns = [new(new Vector3(5f, 5f, 0f), 0f), new(new Vector3(8f, 5f, 0f), 0f)];
        var w = new World(MusouSimTests.Base with { MusouRules = rules }, null, spawns) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(MusouSimTests.RoomFloor()));
        w.MusouDirector!.Hold = true;   // no releases: the test places the enemy
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        w.Step(_ => default);
        Assert.Equal(MusouPhase.Wave, w.MusouDirector.Phase);
        Assert.All(w.MusouDirector.Gates, g => Assert.False(g.X is > 30f and < 36f && g.Y is > 30f and < 36f));
        return w;
    }

    static bool InRoom(Vector3 p) => p.X is > 30f and < 36f && p.Y is > 30f and < 36f;

    [Fact]
    public void A_stranded_enemy_is_moved_to_a_gate_and_then_walks_to_the_players()
    {
        var w = RoomWorld(MusouRules.Default with { PlayerDamageScale = 0f });
        var sim = w.Musou!;
        ushort shut = sim.Spawn(MusouEnemyType.Boy, new Vector3(33f, 33f, 0f), 0f, 85f);
        for (int t = 0; t < 30 * 7; t++) w.Step(_ => default);
        Assert.True(sim.TryGet(shut, out var e));
        Assert.True(InRoom(e.Position));
        Assert.Equal(0, w.MusouDirector!.Relocated);
        for (int t = 0; t < 30 * 2; t++) w.Step(_ => default);
        Assert.Equal(1, w.MusouDirector.Relocated);
        Assert.Equal(1, sim.Relocations(shut));
        for (int t = 0; t < 30 * 15; t++) w.Step(_ => default);
        Assert.True(sim.TryGet(shut, out e));
        Assert.False(InRoom(e.Position));
        Assert.True(Vector3.Distance(e.Position, w.Players[1].Position) < 9f, $"the moved enemy is at {e.Position}");
        Assert.Equal(0, w.MusouDirector.Removed);
    }

    [Fact]
    public void An_enemy_still_stranded_after_its_moves_is_removed_as_a_ko_for_nobody()
    {
        var w = RoomWorld(MusouRules.Default with { PlayerDamageScale = 0f, StrandedRelocations = 0 });
        var sim = w.Musou!;
        var d = w.MusouDirector!;
        ushort shut = sim.Spawn(MusouEnemyType.Boy, new Vector3(33f, 33f, 0f), 0f, 85f);
        int killedBefore = d.Killed;
        for (int t = 0; t < 30 * 9; t++) w.Step(_ => default);
        Assert.Equal((0, 1), (d.Relocated, d.Removed));
        Assert.True(!sim.TryGet(shut, out var e) || !e.Alive);
        Assert.Equal(killedBefore + 1, d.Killed);   // the wave's count goes on, so a wave always ends
        Assert.Equal(0, d.Kos);
        Assert.Equal(0, w.Scores[1].Kills);
        Assert.Empty(d.Scores);
    }

    [Fact]
    public void A_respawn_the_hold_missed_is_undone_when_the_pool_is_empty()
    {
        // A respawn time under a tick: the core respawns the very next tick, before any hold; with the pool spent it is put back.
        var w = new World(MusouSimTests.Base with { RespawnTime = 0.01f, MusouRules = MusouRules.Default with { PlayerDamageScale = 50f } }) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.Spawn(1, w.AssignClass(0));
        var d = w.MusouDirector!;
        int respawns = 0;
        bool emptySeen = false;
        for (int t = 0; t < 30 * 300 && w.State != MatchState.Ended; t++)
        {
            bool inPlay = d.Phase != MusouPhase.Waiting;
            w.Step(_ => default);
            if (inPlay && w.LastRespawns.Contains(1)) respawns++;
            if (d.Lives == 0 && d.Phase != MusouPhase.Waiting)
            {
                if (emptySeen) Assert.False(w.Players[1].Alive);   // never back once the pool is empty
                emptySeen |= !w.Players[1].Alive;
            }
        }
        Assert.Equal((MatchState.Ended, Team.Chaos), (w.State, w.Winner));
        Assert.Equal(3, respawns);
        Assert.Equal(4, w.Scores[1].Deaths);
    }

    [Fact]
    public void The_pool_is_shared_a_leaver_keeps_it_and_only_new_players_add_to_it()
    {
        var tiny = MusouRules.Default with { Waves = [new(4, 4, [(MusouEnemyType.Boy, 1f)]), new(4, 4, [(MusouEnemyType.Boy, 1f)])], BreakSeconds = 2f, PlayerDamageScale = 0f };
        var w = NewWorld(tiny, players: 2);
        var d = w.MusouDirector!;
        w.Step(_ => default);
        w.Step(_ => default);
        Assert.Equal((MusouPhase.Wave, 5), (d.Phase, d.Lives));
        w.Remove(2);   // a leaver mid-wave: the pool stays, the match plays on
        w.Step(_ => default);
        Assert.Equal((MatchState.Playing, 5), (w.State, d.Lives));
        while (d.Phase == MusouPhase.Wave) { KoAll(w); w.Step(_ => default); }
        Assert.Equal(MusouPhase.Break, d.Phase);
        w.Spawn(2, w.AssignClass(0));   // the same number of players again in the break: no new lives
        w.Step(_ => default);
        Assert.Equal(5, d.Lives);
        w.Spawn(3, w.AssignClass(0));   // one more than ever played: +2
        w.Step(_ => default);
        Assert.Equal(7, d.Lives);
        Assert.Equal(MusouPhase.Break, d.Phase);

        // Two dead and one life: only one comes back; the other waits, and with the pool empty both dead lose.
        var one = NewWorld(MusouRules.Default with { Lives = 1, LivesPerExtraPlayer = 0, PlayerDamageScale = 50f }, players: 2);
        int back = 0;
        for (int t = 0; t < 30 * 300 && one.State != MatchState.Ended; t++)
        {
            bool inPlay = one.MusouDirector!.Phase != MusouPhase.Waiting;
            one.Step(_ => default);
            if (inPlay) back += one.LastRespawns.Count;
        }
        Assert.Equal((MatchState.Ended, Team.Chaos), (one.State, one.Winner));
        Assert.Equal(1, back);
        Assert.Equal(3, one.Scores[1].Deaths + one.Scores[2].Deaths);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void Neither_the_hosts_score_limit_nor_its_time_limit_ends_a_musou_match(int scoreLimit)
    {
        var w = NewWorld(MusouRules.Default with { PlayerDamageScale = 50f, Lives = 50, Waves = [new(5000, 80, [(MusouEnemyType.Boy, 1f)])] }, players: 1,
            match: new MatchRules { CountdownTime = 0f, TimeLimit = 5f, ScoreLimit = scoreLimit });
        int deaths = 0;
        for (int t = 0; t < 30 * 40; t++)
        {
            w.Step(_ => default);
            deaths += w.LastKills.Count;
            if (t > 2) Assert.Equal(MatchState.Playing, w.State);
            if (t < 30 * 10 && t % 3 == 0) KoAll(w);   // KOs first, then the horde gets through
        }
        Assert.True(w.MusouDirector!.Kos > scoreLimit);
        Assert.True(deaths >= scoreLimit);   // marine deaths too (with a limit of 1 each one alone reached it)
        Assert.Equal(uint.MaxValue, w.TicksLeft);
        Assert.Equal(Team.None, w.Winner);
    }
}
