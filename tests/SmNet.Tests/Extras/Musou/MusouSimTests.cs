using System.Numerics;
using System.Security.Cryptography;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU1): the enemies on synthetic ground. The flow field (incremental equals whole; the gradient leads to the player), enemies that
/// walk around a wall through its gap and never through it, the attack tokens (at most 4 melee on a player, the rest circling), the melee wind-up (a
/// telegraph, then a strike half a second later that a step back dodges), the shooters' band, burst, hit roll and line-of-sight budget, the players' hit-scan
/// shots killing an enemy end to end (the KO on the scoreboard and the team score), the spawn guard, and a deterministic replay.
/// </summary>
public class MusouSimTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, 0, -1, [Light]) { PrimaryChoices = [0], Side = side, Role = "tactical" };
    internal static readonly SimConfig Base = new() { Weapons = [Rifle], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou };
    internal static MatchRules Endless => new() { CountdownTime = 0f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    /// <summary>A 40 x 40 m floor with a wall along x = 20 from y = 0 to 30 (the gap: y 30 to 40).</summary>
    static NavGraph WalledFloor() =>
        NavGraph.Build(new NavigationTests.Level().Floor(0f, 0f, 40f, 40f).Wall(20f, 0f, 20f, 30f).Mesh(), null, NavSettings.From(new SimConfig()));

    /// <summary>The walled floor with a closed room at x 30 to 36, y 30 to 36 (its floor is on the graph, nothing links it to the rest).</summary>
    internal static NavGraph RoomFloor() =>
        NavGraph.Build(new NavigationTests.Level().Floor(0f, 0f, 40f, 40f).Wall(20f, 0f, 20f, 30f)
            .Wall(30f, 30f, 36f, 30f).Wall(30f, 36f, 36f, 36f).Wall(30f, 30f, 30f, 36f).Wall(36f, 30f, 36f, 36f).Mesh(), null, NavSettings.From(new SimConfig()));

    static MusouEnemy[] All(MusouSim sim)
    {
        var buffer = new MusouEnemy[MusouSim.Capacity];
        return buffer[..sim.CopyTo(buffer)];
    }

    static void Steps(MusouSim sim, ref uint tick, int count, ReadOnlySpan<MusouTarget> targets, bool fight, List<MusouAttack> attacks, Action? each = null)
    {
        for (int k = 0; k < count; k++)
        {
            sim.BeginTick(++tick);
            sim.Step(targets, null, fight, attacks);
            each?.Invoke();
        }
    }

    [Fact]
    public void The_flow_field_built_in_slices_equals_the_whole_one_and_leads_to_the_player()
    {
        var nav = new MusouNav(WalledFloor());
        int source = nav.Nearest(new Vector3(5f, 5f, 0f));
        int far = nav.Nearest(new Vector3(35f, 5f, 0f));
        Assert.True(source >= 0 && far >= 0);
        var whole = new FlowField(nav);
        whole.Complete([source]);
        var sliced = new FlowField(nav);
        int ticks = 0;
        while (sliced.Generation == 0) { sliced.Step([source], [], 500, 15); ticks++; Assert.True(sliced.LastExpansions <= 500); }
        Assert.True(ticks > 3);   // it really took several ticks
        for (int n = 0; n < nav.NodeCount; n++) Assert.Equal(whole.Distance(n), sliced.Distance(n));
        // Around the wall through the gap: far longer than the 30 m straight line, about 30 + 2 x 25 m.
        Assert.InRange(whole.Distance(far), 60f, 80f);
        // Following the gradient from the far node reaches the source, every step walkable and lower.
        int at = far, steps = 0;
        while (at != source && steps++ < 1000)
        {
            int next = whole.Next(at);
            Assert.True(next >= 0);
            Assert.True(whole.Distance(next) < whole.Distance(at));
            at = next;
        }
        Assert.Equal(source, at);
    }

    [Fact]
    public void Enemies_walk_around_the_wall_through_its_gap_to_the_player()
    {
        var sim = new MusouSim(WalledFloor());
        for (int k = 0; k < 20; k++) Assert.NotEqual(0, sim.Spawn(MusouEnemyType.Boy, new Vector3(30f + (k % 5) * 1.5f, 3f + (k / 5) * 1.5f, 0f), 0f, 85f));
        MusouTarget[] targets = [new(1, new Vector3(5f, 5f, 0f), true)];
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        var buffer = new MusouEnemy[MusouSim.Capacity];
        Steps(sim, ref tick, 30 * 30, targets, fight: false, attacks, () =>
        {
            int n = sim.CopyTo(buffer);
            foreach (var e in buffer.AsSpan(0, n))
                if (MathF.Abs(e.Position.X - 20f) < 0.5f) Assert.True(e.Position.Y > 29.5f, $"enemy {e.Id} went through the wall at {e.Position}");
        });
        int count = sim.CopyTo(buffer);
        Assert.Equal(20, count);
        Assert.All(buffer[..count], e => Assert.True(Vector3.Distance(e.Position, targets[0].Position) < 9f, $"enemy {e.Id} at {e.Position}"));
    }

    [Fact]
    public void At_most_four_strike_at_once_and_the_rest_circle()
    {
        var sim = new MusouSim(null);
        for (int k = 0; k < 30; k++)
        {
            float a = k * MathF.Tau / 30f;
            sim.Spawn(MusouEnemyType.Boy, new Vector3(MathF.Cos(a) * 15f, MathF.Sin(a) * 15f, 0f), 0f, 85f);
        }
        MusouTarget[] targets = [new(7, Vector3.Zero, true)];
        var attacks = new List<MusouAttack>();
        var telegraphs = new Dictionary<ushort, uint>();
        int strikes = 0;
        uint tick = 0;
        var buffer = new MusouEnemy[MusouSim.Capacity];
        Steps(sim, ref tick, 30 * 12, targets, fight: true, attacks, () =>
        {
            int n = sim.CopyTo(buffer);
            Assert.True(buffer.AsSpan(0, n).ToArray().Count(e => e.AttackToken == MusouSim.MeleeToken) <= 4);
            foreach (var ev in sim.Events)
            {
                if (ev.Kind == MusouEventKind.Telegraph) { Assert.Equal(MusouSim.MeleeWindUpTelegraph, ev.Value); telegraphs[ev.Enemy] = ev.Tick; }
                if (ev.Kind == MusouEventKind.Strike)
                {
                    strikes++;
                    Assert.Equal((ushort)7, ev.Player);
                    Assert.Equal(15u, ev.Tick - telegraphs[ev.Enemy]);   // the 0.5 s wind-up at 30 Hz
                }
            }
        });
        Assert.True(strikes > 10, $"{strikes} strikes");
        Assert.Equal(strikes, attacks.Count);
        Assert.All(attacks, a => Assert.Equal((7, 8f, true), (a.Player, a.Damage, a.Melee)));
        // Without a token: circling at about 3 to 6 m (the crowd pushes some a little out).
        int n2 = sim.CopyTo(buffer);
        var circling = buffer[..n2].Where(e => e.AttackToken == 0).Select(e => new Vector2(e.Position.X, e.Position.Y).Length()).ToList();
        Assert.True(circling.Count >= 20);
        Assert.True(circling.Count(d => d is >= 2.5f and <= 8f) >= circling.Count * 9 / 10, string.Join(", ", circling.Select(d => d.ToString("0.0"))));
    }

    [Fact]
    public void A_step_back_during_the_wind_up_dodges_the_strike()
    {
        var sim = new MusouSim(null);
        ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 1.3f, 0f), 0f, 85f);
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        MusouTarget[] near = [new(1, Vector3.Zero, true)];
        int k = 0;
        while (!sim.Events.Any(e => e.Kind == MusouEventKind.Telegraph) && k++ < 120) Steps(sim, ref tick, 1, near, fight: true, attacks);
        Assert.True(sim.TryGet(boy, out var winding));
        Assert.Equal(MusouEnemyState.WindUp, winding.State);
        // The player steps 4 m away: the strike lands on nothing.
        MusouTarget[] away = [new(1, new Vector3(0f, -4f, 0f), true)];
        Steps(sim, ref tick, 15, away, fight: true, attacks);
        Assert.True(sim.TryGet(boy, out var struck));
        Assert.Equal(MusouEnemyState.Strike, struck.State);
        Assert.Empty(attacks);
        Assert.DoesNotContain(sim.Events, e => e.Kind == MusouEventKind.Strike);
    }

    [Fact]
    public void Shooters_hold_their_band_fire_bursts_and_keep_to_the_line_of_sight_budget()
    {
        var sim = new MusouSim(null);
        for (int k = 0; k < 40; k++)
        {
            float a = k * MathF.Tau / 40f;
            sim.Spawn(MusouEnemyType.ShootaBoy, new Vector3(MathF.Cos(a) * 35f, MathF.Sin(a) * 35f, 0f), 0f, 85f);
        }
        MusouTarget[] targets = [new(3, Vector3.Zero, true)];
        var attacks = new List<MusouAttack>();
        int tracers = 0, hits = 0;
        uint tick = 0;
        var buffer = new MusouEnemy[MusouSim.Capacity];
        Steps(sim, ref tick, 30 * 15, targets, fight: true, attacks, () =>
        {
            Assert.True(sim.LastLosChecks <= 16);
            int n = sim.CopyTo(buffer);
            Assert.True(buffer.AsSpan(0, n).ToArray().Count(e => e.AttackToken == MusouSim.ShooterToken) <= 6);
            foreach (var ev in sim.Events.Where(e => e.Kind == MusouEventKind.Tracer)) { tracers++; if (ev.Player == 3) hits++; }
        });
        int count = sim.CopyTo(buffer);
        Assert.All(buffer[..count], e => Assert.InRange(new Vector2(e.Position.X, e.Position.Y).Length(), 9f, 21f));   // in the 12-20 m band, give or take the crowd
        Assert.True(tracers > 50, $"{tracers} tracers");
        Assert.Equal(hits, attacks.Count);
        Assert.InRange(hits / (float)tracers, 0.2f, 0.5f);   // the roll: 0.45 near, 0.25 far
        Assert.All(attacks, a => Assert.Equal((3, 3f, false), (a.Player, a.Damage, a.Melee)));
    }

    [Fact]
    public void A_moving_player_and_one_in_the_air_stay_flow_sources()
    {
        var sim = new MusouSim(WalledFloor());
        for (int k = 0; k < 20; k++) sim.Spawn(MusouEnemyType.Boy, new Vector3(28f + (k % 5) * 1.5f, 3f + (k / 5) * 1.5f, 0f), 0f, 85f);
        var attacks = new List<MusouAttack>();
        var targets = new MusouTarget[1];
        uint tick = 0;
        // 10 s walking a 6 m circle around (8, 8), then 20 s 6 to 10 m up (a jump pack's flight, off the graph), still circling.
        for (int t = 0; t < 30 * 30; t++)
        {
            float a = t * 0.01f, up = t < 300 ? 0f : 6f + 4f * MathF.Sin(t * 0.02f);
            targets[0] = new MusouTarget(1, new Vector3(8f + MathF.Cos(a) * 6f, 8f + MathF.Sin(a) * 6f, up), true);
            sim.BeginTick(++tick);
            sim.Step(targets, null, fight: false, attacks);
            if (t > 300) Assert.True(sim.Flow!.Ready);
        }
        var under = targets[0].Position;
        Assert.All(All(sim), e => Assert.True(new Vector2(e.Position.X - under.X, e.Position.Y - under.Y).Length() < 10f, $"enemy {e.Id} at {e.Position}, the player above {under}"));
        Assert.All(All(sim), e => Assert.False(float.IsPositiveInfinity(sim.Flow!.Distance(e.Node))));

        // A player who has never stood on the graph but flies 10 m over it still counts from the floor below.
        var fresh = new MusouSim(WalledFloor());
        ushort boy = fresh.Spawn(MusouEnemyType.Boy, new Vector3(35f, 5f, 0f), 0f, 85f);
        MusouTarget[] high = [new(2, new Vector3(5f, 5f, 10f), true)];
        tick = 0;
        Steps(fresh, ref tick, 30, high, fight: false, attacks);
        Assert.True(fresh.Flow!.Ready);
        Assert.True(fresh.TryGet(boy, out var b));
        Assert.Equal(2, b.TargetPlayer);
        Assert.InRange(fresh.Flow.Distance(b.Node), 60f, 80f);
    }

    [Fact]
    public void A_player_who_leaves_the_graph_keeps_the_last_field()
    {
        var sim = new MusouSim(WalledFloor());
        ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(35f, 5f, 0f), 0f, 85f);
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        MusouTarget[] on = [new(1, new Vector3(5f, 5f, 0f), true)];
        Steps(sim, ref tick, 30, on, fight: false, attacks);
        Assert.True(sim.Flow!.Ready);
        // Far off the graph (no node within the wide search): the last node stays the source; the horde walks on round the wall, not into it.
        MusouTarget[] off = [new(1, new Vector3(-60f, 5f, 0f), true)];
        Steps(sim, ref tick, 30 * 20, off, fight: false, attacks);
        Assert.True(sim.Flow.Ready);
        Assert.True(sim.TryGet(boy, out var b));
        Assert.True(b.Position.X < 20f, $"the enemy at {b.Position} never came round the wall");
        Assert.False(float.IsPositiveInfinity(sim.Flow.Distance(b.Node)));
    }

    [Fact]
    public void Enemies_go_for_the_player_they_can_walk_to_and_hold_when_none_is()
    {
        var sim = new MusouSim(RoomFloor());
        for (int k = 0; k < 10; k++) sim.Spawn(MusouEnemyType.Boy, new Vector3(25f + (k % 5) * 1.5f, 3f + (k / 5) * 1.5f, 0f), 0f, 85f);
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        // Player 1 shut in the room (on the graph, out of reach), player 2 outside: everyone goes for 2, though 1 is nearer for some.
        MusouTarget[] both = [new(1, new Vector3(33f, 33f, 0f), true), new(2, new Vector3(5f, 35f, 0f), true)];
        Steps(sim, ref tick, 30 * 20, both, fight: false, attacks);
        Assert.All(All(sim), e => Assert.Equal(2, e.TargetPlayer));
        Assert.All(All(sim), e => Assert.True(Vector3.Distance(e.Position, both[1].Position) < 9f, $"enemy {e.Id} at {e.Position}"));

        // Only the shut-in player: they hold where they are (no walking into the room's walls) and the failsafe lists them after 8 s.
        var held = new MusouSim(RoomFloor());
        for (int k = 0; k < 10; k++) held.Spawn(MusouEnemyType.Boy, new Vector3(5f + (k % 5) * 1.5f, 3f + (k / 5) * 1.5f, 0f), 0f, 85f);
        var start = All(held).ToDictionary(e => e.Id, e => e.Position);
        MusouTarget[] shut = [new(1, new Vector3(33f, 33f, 0f), true)];
        tick = 0;
        var stranded = new List<ushort>();
        Steps(held, ref tick, 30 * 7, shut, fight: false, attacks, () => held.TakeStranded(stranded));
        Assert.All(All(held), e => Assert.True(Vector3.Distance(e.Position, start[e.Id]) < 1.5f, $"enemy {e.Id} wandered to {e.Position}"));
        Assert.All(All(held), e => Assert.Equal(0, e.TargetPlayer));
        Assert.Empty(stranded);
        Steps(held, ref tick, 30 * 2, shut, fight: false, attacks, () => held.TakeStranded(stranded));
        Assert.Equal(10, stranded.Distinct().Count());
    }

    [Fact]
    public void Separation_takes_the_nearest_neighbours_so_a_pile_spreads_evenly()
    {
        var sim = new MusouSim(null);
        // 60 bodies on almost one spot: the pile spreads out round where it stood, not off to one side.
        for (int k = 0; k < 60; k++) sim.Spawn(MusouEnemyType.Gretchin, new Vector3((k % 8 - 3.5f) * 0.05f, (k / 8 - 3.5f) * 0.05f, 0f), 0f, 30f);
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        Steps(sim, ref tick, 30 * 3, [], fight: false, attacks);
        var all = All(sim);
        var centre = all.Aggregate(Vector3.Zero, (c, e) => c + e.Position) / all.Length;
        Assert.True(new Vector2(centre.X, centre.Y).Length() < 0.5f, $"the pile drifted to {centre}");
        Assert.True(all.Max(e => new Vector2(e.Position.X, e.Position.Y).Length()) > 1.5f);   // it did spread
    }

    [Fact]
    public void Spawn_refuses_values_that_are_not_finite()
    {
        var sim = new MusouSim(null);
        Assert.Equal(0, sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, float.NaN));
        Assert.Equal(0, sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, float.PositiveInfinity));
        Assert.Equal(0, sim.Spawn(MusouEnemyType.Boy, new Vector3(float.NaN, 0f, 0f), 0f, 85f));
        Assert.Equal(0, sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, float.NaN, 85f));
        Assert.Equal(0, sim.Spawn((MusouEnemyType)15, Vector3.Zero, 0f, 85f));
        Assert.Equal(0, sim.Count);
        Assert.Equal(1, sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 85f));
    }

    [Fact]
    public void A_players_hit_scan_shots_ko_an_enemy_and_it_scores()
    {
        var w = new World(Base with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f } }) { Rules = Endless };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);
        var me = w.Players[1];
        var sim = w.Musou!;
        ushort boy = sim.Spawn(MusouEnemyType.Boy, me.Position + new Vector3(0f, 10f, 0f), 0f, 85f);
        var hits = new List<MusouEvent>();
        for (int t = 0; t < 150 && sim.TryGet(boy, out var e) && e.Alive; t++)
        {
            var d = e.Position - w.Players[1].Position;
            float yaw = MathF.Atan2(-d.X, d.Y);
            w.Step(_ => new PlayerInput(0, 0f, 0f, yaw, InputButtons.Fire));
            hits.AddRange(sim.Events.Where(v => v.Kind == MusouEventKind.Hit));
        }
        Assert.True(sim.TryGet(boy, out var dead));
        Assert.False(dead.Alive);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal((ushort)1, h.Player));
        Assert.Equal(1, w.Scores[1].Kills);
        Assert.Equal(1, w.ScoreOf(Team.SpaceMarines));
        Assert.Equal(1, w.MusouDirector.Scores[1].Kos);
        Assert.Equal(10, w.MusouDirector.Scores[1].Points);
    }

    [Fact]
    public void A_musou_world_replays_identically()
    {
        static string Run()
        {
            var w = new World(Base) { Rules = Endless };
            Assert.True(w.StartMusou(null));
            for (int id = 1; id <= 2; id++) w.Spawn(id, w.AssignClass(0));
            using var sha = SHA256.Create();
            var buffer = new MusouEnemy[MusouSim.Capacity];
            var bytes = new List<byte>();
            for (int t = 0; t < 900; t++)
            {
                w.Step(id => new PlayerInput(0, 0f, t % 90 < 45 ? 1f : -1f, id * 1.3f + t * 0.01f, t % 20 < 10 ? InputButtons.Fire : 0));
                int n = w.Musou!.CopyTo(buffer);
                foreach (var e in buffer.AsSpan(0, n))
                    bytes.AddRange(BitConverter.GetBytes(HashCode.Combine(e.Id, e.Type, e.State, e.Position, e.Health, e.TargetPlayer, e.AttackToken)));
                foreach (var e in w.Musou.Events) bytes.AddRange(BitConverter.GetBytes(HashCode.Combine(e.Kind, e.Tick, e.Enemy, e.Player, e.Value, e.Vector)));
                foreach (var p in w.Players.Values) bytes.AddRange(BitConverter.GetBytes(HashCode.Combine(p.Position, p.Health, p.Shield, p.Alive)));
                var v = w.Musou.Director;
                bytes.AddRange(BitConverter.GetBytes(HashCode.Combine(v.Wave, v.Phase, v.EnemiesLeft, v.EnemiesAlive, v.Lives, w.ScoreOf(Team.SpaceMarines))));
            }
            Assert.True(w.Musou!.Count > 0);
            return Convert.ToHexString(sha.ComputeHash(bytes.ToArray()));
        }
        Assert.Equal(Run(), Run());
    }
}
