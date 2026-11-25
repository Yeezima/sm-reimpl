using System.Diagnostics;
using System.Numerics;
using SmBots;
using SmNet.Tests.Extras.Musou.Campaign;
using Vector2 = System.Numerics.Vector2;
using SmBots.Extras.Warfront;
using SmContent;
using SmServer.Extras.Warfront.Retainers;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Squad;
using Xunit.Abstractions;
using Rig = SmNet.Tests.Extras.Warfront.Wiring.WarfrontSquadWiringTests.Rig;

namespace SmNet.Tests.Extras.Warfront.Retainers;

/// <summary>
/// Phase B T4 (2026-11-25): the Warfront retainers' brain. The leash state machine (40 m, overshoot, the 3 s grace, order override and the return to Follow), decision parity with the
/// Classic strategy in matched encounters (per class), the strategy's leash, return and revive inputs, the server host driving real World retainers (follow, attack, determinism) and its tick cost.
/// </summary>
public class RetainerBrainTests(ITestOutputHelper output)
{
    // ---- the leash state machine --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Inside_the_40_m_circle_the_leash_stays_inside_and_a_plain_step_out_returns_until_4_m_inside()
    {
        Assert.Equal(40f, WarfrontLeash.Radius);
        var l = new WarfrontLeash();
        Assert.Equal(LeashState.Inside, l.Step(0, RetainerOrder.Follow, 39.9f, false, double.NegativeInfinity));
        Assert.Equal(LeashState.Return, l.Step(0.1, RetainerOrder.Follow, 40.5f, false, double.NegativeInfinity));   // not fighting: back at once
        Assert.Equal(LeashState.Return, l.Step(0.2, RetainerOrder.Follow, 37f, false, double.NegativeInfinity));     // within the margin: still returning
        Assert.Equal(LeashState.Inside, l.Step(0.3, RetainerOrder.Follow, 35.9f, false, double.NegativeInfinity));
    }

    [Fact]
    public void A_committed_move_overshoots_and_then_returns_without_a_grace()
    {
        var l = new WarfrontLeash();
        l.Step(0, RetainerOrder.Follow, 30f, false, 0);
        Assert.Equal(LeashState.Overshoot, l.Step(0.1, RetainerOrder.Follow, 44f, true, 0.1));   // a jump carried it out while fighting
        Assert.Equal(LeashState.Overshoot, l.Step(0.5, RetainerOrder.Follow, 47f, true, 0.5));
        Assert.Equal(LeashState.Return, l.Step(0.6, RetainerOrder.Follow, 47f, false, 0.6));     // the move done: back, no grace though it fought
        Assert.True(double.IsNaN(l.GraceStarted));
    }

    [Fact]
    public void The_owner_moving_away_gives_an_engaged_retainer_three_seconds_that_new_attacks_do_not_reset()
    {
        var l = new WarfrontLeash();
        l.Step(0, RetainerOrder.Follow, 35f, false, 0);
        Assert.Equal(LeashState.Grace, l.Step(1.0, RetainerOrder.Follow, 41f, false, 0.9));
        for (double t = 1.1; t < 3.95; t += 0.1) Assert.Equal(LeashState.Grace, l.Step(t, RetainerOrder.Follow, 45f, false, t));   // fighting all the while
        Assert.Equal(LeashState.Grace, l.Step(4.05, RetainerOrder.Follow, 45f, true, 4.05));   // 3 s gone, but a swing is under way: it finishes
        Assert.Equal(LeashState.Return, l.Step(4.1, RetainerOrder.Follow, 45f, false, 4.1));
        // back inside: a new grace may come later
        Assert.Equal(LeashState.Inside, l.Step(5, RetainerOrder.Follow, 30f, false, 5));
        Assert.Equal(LeashState.Grace, l.Step(6, RetainerOrder.Follow, 41f, false, 6));
    }

    [Fact]
    public void An_order_overrides_the_leash_and_back_on_Follow_a_far_retainer_returns_after_its_move_with_no_grace()
    {
        var l = new WarfrontLeash();
        l.Step(0, RetainerOrder.Follow, 10f, false, 0);
        Assert.Equal(LeashState.Ordered, l.Step(1, RetainerOrder.Attack, 90f, false, 1));
        Assert.Equal(LeashState.Ordered, l.Step(2, RetainerOrder.JumpPound, 120f, true, 2));
        Assert.Equal(LeashState.Overshoot, l.Step(3, RetainerOrder.Follow, 80f, true, 3));   // Follow again during a move: it finishes it
        Assert.True(l.NoGrace);
        Assert.Equal(LeashState.Return, l.Step(3.1, RetainerOrder.Follow, 80f, false, 3.1));   // fighting, but no new grace window for this transition
        Assert.Equal(LeashState.Return, new WarfrontLeash().Step(0, RetainerOrder.Hold, 80f, false, 0) == LeashState.Ordered ? NewFollowFar() : LeashState.Inside);
    }

    static LeashState NewFollowFar()
    {
        var l = new WarfrontLeash();
        l.Step(0, RetainerOrder.Hold, 80f, false, 0);
        return l.Step(0.1, RetainerOrder.Follow, 80f, false, 0.1);
    }

    // ---- the strategy with a brain (a flat-ground fake world) --------------------------------------------------------------------------------

    /// <summary>A flat-ground world of one bot (player 1, Space Marines, class <paramref name="cls"/>) and the given others.</summary>
    sealed class FakeWorld : IBotWorldView
    {
        public FakeWorld(SimConfig config, int cls)
        {
            Config = config;
            Me = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, config) with { Team = Team.SpaceMarines };
        }
        public PlayerState Me;
        public List<PlayerState> OthersList = [];
        public double Now;
        public int PlayerId => 1;
        public PlayerState Predicted => Me;
        public IReadOnlyList<PlayerState> Others => OthersList;
        public double ConnectedFor => Now;
        public SimConfig? Config { get; }
        public CollisionWorld? Collision => null;
        public MatchView Match => new(MatchState.Playing, Team.None, 1000, 0, 0);
        public IReadOnlyList<ControlPointView> Points => [];
        public SeizeGroundInfo? SeizeLayout => null;
        public bool SeizeGround => false;
        public string MapName => "";
        public TimeSpan CurrentInterpolationDelay => TimeSpan.Zero;
        public IEnumerable<ProjectileView> NewestProjectiles() => [];
        public IReadOnlyList<ProjectileView> Projectiles() => [];
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float GrenadesOutFor => 0f;
        public int OwnShotsFired => 0;
        public ContentExtras MatchExtras => ContentExtras.None;
        public StimSpec? StimOf(PlayerState s) => null;
        public float AimHeightOf(PlayerState target) => target.PlayerId >= WarfrontRetainerBrains.OrkIdBase ? 1.2f : ClassicBotBrain.BotTargetHeight;
        public BotWork Work { get; } = new();
    }

    static SimConfig Config() => Wiring.WarfrontSquadWiringTests.Cfg();

    static ClassicBotBrain Brain(FakeWorld world, IBotStrategy? strategy) =>
        new(world, BotArgs.None, new ClassicBotOptions { SyncPlans = true }, BotLog.None, IBotHooks.None, strategy);

    static PlayerState Ork(SimConfig config, int id, Vector3 at) =>
        Combat.Respawned(new PlayerState(WarfrontRetainerBrains.OrkIdBase + id, at, Vector3.Zero, 0f, true), config) with { Team = Team.Chaos };

    [Theory]
    [InlineData(0)]   // Tactical
    [InlineData(1)]   // Assault (jump pack)
    [InlineData(2)]   // Devastator
    public void Inside_its_leash_a_retainer_makes_every_fight_decision_of_a_Classic_bot(int cls)
    {
        // decision parity (the approved brief: "match decision logic, aim, actions, timing, equipment use"): the same encounters, the Classic strategy against the Warfront one (Follow, owner beside it)
        var config = Config();
        int encounters = 0, same = 0, total = 0;
        foreach (float distance in new[] { 2.5f, 6f, 12f, 22f })
        {
            encounters++;
            var classic = new FakeWorld(config, cls);
            var warfront = new FakeWorld(config, cls);
            var strategy = new WarfrontRetainerStrategy();
            strategy.Situation.Owner = new Vector3(0f, -3f, 0f);
            var a = Brain(classic, null);
            var b = Brain(warfront, strategy);
            int matched = 0;
            for (uint tick = 1; tick <= 300; tick++)
            {
                double t = tick / 30.0;
                // the Ork circles the bot at the encounter's distance (the same scripted world for both)
                float angle = (float)(t * 0.6);
                var ork = Ork(config, 7, new Vector3(MathF.Sin(angle) * distance, MathF.Cos(angle) * distance, 0f));
                foreach (var w in new[] { classic, warfront }) { w.Now = t; w.OthersList.Clear(); w.OthersList.Add(ork); }
                var ia = a.Hold(a.Step(tick));
                var ib = b.Hold(b.Step(tick));
                total++;
                if (ia == ib && classic.Yaw == warfront.Yaw && classic.Pitch == warfront.Pitch) { same++; matched++; }
            }
            output.WriteLine($"class {cls}, Ork at {distance} m: {matched}/300 identical inputs (leash {strategy.Leash.State}, last decision {strategy.Decision})");
        }
        output.WriteLine($"class {cls}: {same}/{total} identical over {encounters} encounters");
        Assert.Equal(total, same);
    }

    [Fact]
    public void A_far_goal_is_kept_to_the_reachable_edge_of_the_circle_and_a_hold_keeps_its_ring()
    {
        var world = new FakeWorld(Config(), 0);
        var s = new WarfrontRetainerStrategy();
        var brain = Brain(world, s);
        s.Situation.Owner = new Vector3(10f, 0f, 0f);
        var clamped = s.ClampGoal(brain, world.Me, new Vector3(10f, 100f, 2f));
        Assert.Equal(new Vector3(10f, WarfrontLeash.Radius - 1f, 2f), clamped);
        Assert.Equal(new Vector3(10f, 20f, 0f), s.ClampGoal(brain, world.Me, new Vector3(10f, 20f, 0f)));   // inside: unchanged
        s.Situation.Order = RetainerOrder.Hold;
        s.Situation.HoldAt = new Vector3(0f, 0f, 0f);
        Assert.Equal(WarfrontRetainerStrategy.HoldRadius, s.ClampGoal(brain, world.Me, new Vector3(0f, 50f, 0f)).Y, 3);
        s.Situation.Order = RetainerOrder.Attack;
        Assert.Equal(new Vector3(0f, 50f, 0f), s.ClampGoal(brain, world.Me, new Vector3(0f, 50f, 0f)));   // an order overrides the leash
    }

    [Fact]
    public void A_retainer_left_far_behind_walks_back_to_its_owner_and_one_beside_a_downed_ally_holds_Use()
    {
        var config = Config();
        var world = new FakeWorld(config, 0);
        var s = new WarfrontRetainerStrategy();
        var brain = Brain(world, s);
        s.Situation.Owner = new Vector3(0f, 60f, 0f);   // 60 m north, nothing to fight
        var input = brain.Hold(brain.Step(1));
        Assert.Equal(LeashState.Return, s.Leash.State);
        Assert.Equal("return", s.Decision);
        Assert.True(input.MoveY > 0.5f, $"walks forward ({input.MoveY})");
        Assert.InRange(MathF.Abs(world.Yaw), 0f, 0.05f);   // facing north (direction (-sin yaw, cos yaw))

        s.Situation.Owner = Vector3.Zero;
        s.Situation.ReviveAt = new Vector3(1f, 0f, 0f);
        world.Now = 1;
        var revive = brain.Hold(brain.Step(2));
        Assert.Equal("revive", s.Decision);
        Assert.True((revive.Buttons & InputButtons.Interact) != 0);
        Assert.Equal(0f, revive.MoveY);
    }

    // ---- the server host over real World retainers ---------------------------------------------------------------------------------------------

    static (Rig Rig, WarfrontRetainerBrains Brains) Driven(int humans = 1, int marines = 2, bool parallel = true)
    {
        var r = Rig.Make(humans: humans, marines: marines);
        var brains = new WarfrontRetainerBrains(r.W, r.Ret, () => null, () => "test arena") { JumpLinks = false, Parallel = parallel }.Attach();
        return (r, brains);
    }

    [Fact]
    public void Driven_retainers_follow_their_owner_and_shoot_an_Ork_near_them()
    {
        var (r, brains) = Driven();
        var owner = r.Where(1) + new Vector3(0f, 18f, 0f);
        TfwRig.Teleport(r.W, owner);
        for (int t = 0; t < 30 * 12; t++) { r.Clear(); r.Stand(1); }
        for (int s = 0; s < 2; s++)
        {
            float d = new Vector2(r.Body(1, s).Position.X - owner.X, r.Body(1, s).Position.Y - owner.Y).Length();
            output.WriteLine($"retainer {s}: {d:0.0} m from the owner, decision {brains.StrategyOf(r.IdOf(1, s))!.Decision}");
            Assert.True(d < 8f, $"retainer {s} followed ({d:0.0} m)");
        }
        // an Ork 10 m from the first retainer: the Classic fight
        var at = r.Body(1, 0).Position + new Vector3(10f, 0f, 0f);
        ushort ork = r.Ork(MusouEnemyType.Boy, at, 1e5f);
        r.W.Musou!.Frozen = true;
        float before = 1e5f;
        bool fought = false;
        for (int t = 0; t < 30 * 6; t++)
        {
            r.Stand(1);
            fought |= brains.StrategyOf(r.IdOf(1, 0))!.Decision == "fight";
        }
        Assert.True(fought, "it fought the Ork");
        Assert.True(r.W.Musou.TryGet(ork, out var hurt) && hurt.Health < before, "its shots or blows hurt the Ork");
        output.WriteLine($"the Ork's health {hurt.Health:0} of {before:0}; brain tick {brains.LastMilliseconds:0.00} ms");
    }

    [Fact]
    public void The_inputs_do_not_depend_on_the_threads()
    {
        var (a, ba) = Driven(parallel: true);
        var (b, bb) = Driven(parallel: false);
        foreach (var r in new[] { a, b })
        {
            TfwRig.Teleport(r.W, r.Where(1) + new Vector3(12f, 6f, 0f));
            r.Ork(MusouEnemyType.Boy, r.Where(1) + new Vector3(-9f, 4f, 0f), 400f);
        }
        for (int t = 0; t < 30 * 8; t++) { a.Stand(1); b.Stand(1); }
        for (int s = 0; s < 2; s++)
        {
            Assert.Equal(a.Body(1, s).Position, b.Body(1, s).Position);
            Assert.Equal(a.Body(1, s).Yaw, b.Body(1, s).Yaw);
        }
        Assert.True(ba.Steps > 0 && bb.Steps > 0);
    }

    [Fact]
    public void Tick_cost_with_four_players_and_eight_driven_retainers_against_none()
    {
        // bounded (the design's T6 input): 300 measured ticks each, after 60 to settle
        static (double P50, double P99) Measure(Rig r, int ticks, WarfrontRetainerBrains? brains, List<double> own)
        {
            var ms = new List<double>(ticks);
            for (int t = 0; t < 60; t++) r.Stand(1);
            for (int t = 0; t < ticks; t++)
            {
                long s = Stopwatch.GetTimestamp();
                r.Stand(1);
                ms.Add(Stopwatch.GetElapsedTime(s).TotalMilliseconds);
                if (brains is not null) own.Add(brains.LastMilliseconds);
            }
            ms.Sort();
            return (ms[ticks / 2], ms[Math.Min(ticks - 1, (int)(ticks * 0.99))]);
        }
        static double P(List<double> v, double q) { var c = v.OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[Math.Min(c.Count - 1, (int)(c.Count * q))]; }
        var (with, brains) = Driven(humans: 4, marines: 2);
        var without = Rig.Make(humans: 4, marines: 2);   // the same retainers standing (no brain)
        for (int p = 1; p <= 4; p++) { with.Ork(MusouEnemyType.Boy, with.Where(p) + new Vector3(15f, 0f, 0f), 300f); without.Ork(MusouEnemyType.Boy, without.Where(p) + new Vector3(15f, 0f, 0f), 300f); }
        var own = new List<double>();
        var a = Measure(with, 300, brains, own);
        var b = Measure(without, 300, null, own);
        output.WriteLine(FormattableString.Invariant($"tick with 4 players x 2 driven retainers: p50 {a.P50:0.00} ms, p99 {a.P99:0.00} ms; the brains' own step p50 {P(own, 0.5):0.00} ms, p99 {P(own, 0.99):0.00} ms"));
        output.WriteLine(FormattableString.Invariant($"tick with the same 8 retainers standing (no brain): p50 {b.P50:0.00} ms, p99 {b.P99:0.00} ms"));
        Assert.Equal(8, with.Ret.All.Count(x => x.WorldId != 0));
        Assert.True(brains.Steps >= 360);
    }
}
