using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Match flow (ours; spec/behavior.md "Match flow"): the "MATCH BEGINS" countdown between warm-up and play, the attack lock during it,
/// the round that follows an ended match, the way the countdown travels in the unchanged match block, and the client's prediction
/// agreeing with the server. Synthetic classes and weapons only (no game data).
/// </summary>
public class MatchFlowTests
{
    const int SmTac = 0, CsmTac = 1;

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps)
    { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], Side = side, Role = "tactical" };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Sidearm],
        Thrown = [GrenadeTests.Frag],
        Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f, MaxRewindTicks = 40,
    };

    /// <summary>One second of countdown, an instant restart: ticks are 1/30 s.</summary>
    static readonly MatchRules Rules = new() { TimeLimit = 0f, CountdownTime = 1f, RestartDelay = 0.2f };
    static int CountdownTicks => (int)MathF.Round(Rules.CountdownTime * Config.TickRate);

    static PlayerInput Idle(int id) => new(0, 0, 0, MathF.PI, InputButtons.None);
    static Func<int, PlayerInput> Only1(InputButtons buttons, float moveY = 0f) =>
        id => id == 1 ? new PlayerInput(0, 0, moveY, 0f, buttons) : Idle(id);

    /// <summary>Player 1 (Space Marines) at the origin facing +Y, player 2 (Chaos) 5 m ahead; only player 1 has joined unless <paramref name="both"/>.</summary>
    static World Arena(MatchRules? rules = null, bool both = true)
    {
        var world = new World(Config, null, [new SpawnPoint(new Vector3(0, 0, 0), 0f), new SpawnPoint(new Vector3(0, 5, 0), 0f)]) { Rules = rules ?? Rules };
        world.Spawn(1, SmTac);
        if (both) world.Spawn(2, CsmTac);
        return world;
    }

    [Fact]
    public void The_countdown_runs_from_the_moment_both_sides_have_a_player_to_play()
    {
        var w = Arena(both: false);
        for (int k = 0; k < 10; k++) w.Step(Idle);
        Assert.Equal((MatchState.Warmup, 0u), (w.State, w.TicksLeft));       // one side only: plain warm-up, no countdown
        w.Spawn(2, CsmTac);
        w.Step(Idle);
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.Equal((uint)CountdownTicks, w.TicksLeft);
        for (int k = 1; k < CountdownTicks; k++) { w.Step(Idle); Assert.Equal(MatchState.Countdown, w.State); }
        Assert.Equal(1u, w.TicksLeft);
        w.Step(Idle);                                                         // the last tick of the countdown starts the round
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(uint.MaxValue, w.TicksLeft);                            // no time limit in these rules: "none"
        Assert.Equal((0, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
    }

    [Fact]
    public void Play_starts_with_the_time_limit_after_the_countdown_not_before()
    {
        var w = Arena(Rules with { TimeLimit = 60f });
        w.Step(Idle);
        Assert.Equal((MatchState.Countdown, (uint)CountdownTicks), (w.State, w.TicksLeft));
        for (int k = 0; k < CountdownTicks; k++) w.Step(Idle);
        Assert.Equal((MatchState.Playing, (uint)(60 * Config.TickRate)), (w.State, w.TicksLeft));
    }

    [Fact]
    public void Everyone_stands_at_a_spawn_point_when_the_countdown_starts_and_may_move_and_look_but_not_attack()
    {
        var w = Arena(both: false);
        for (int k = 0; k < 30; k++) w.Step(Only1(InputButtons.None, 1f));    // warm-up: player 1 walks away from its spawn
        Assert.True(w.Players[1].Position.Y > 1f);
        w.Spawn(2, CsmTac);
        w.Step(Only1(InputButtons.None));
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.Equal([new Vector3(0, 0, 0), new Vector3(0, 5, 0)], w.Players.Values.Select(p => p.Position).Order(Comparer<Vector3>.Create((a, b) => a.Y.CompareTo(b.Y))));   // one on each spawn point
        // Moving and looking work.
        float y = w.Players[1].Position.Y;
        for (int k = 0; k < 10; k++) w.Step(Only1(InputButtons.None, 1f));
        Assert.True(w.Players[1].Position.Y > y + 0.5f);
        // Attacks do nothing: no shot, no hit, no ammunition spent, no melee, no grenade.
        int clip = w.Players[1].Clip;
        for (int k = 0; k < 8; k++)
        {
            w.Step(Only1(InputButtons.Fire | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Grenade));
            Assert.Empty(w.LastShots);
            Assert.Empty(w.LastHits);
        }
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.Equal((clip, 1000f), (w.Players[1].Clip, w.Players[2].Health));
    }

    [Fact]
    public void Attacks_work_again_the_moment_play_begins()
    {
        var w = Arena();
        while (w.State != MatchState.Playing) w.Step(Only1(InputButtons.Fire));   // the trigger is held through the whole countdown
        Assert.Equal(1000f, w.Players[2].Health);
        w.Step(Only1(InputButtons.None));                                        // release and press again: a semi-automatic rifle fires on the press
        w.Step(Only1(InputButtons.Fire));
        Assert.Equal(980f, w.Players[2].Health);
    }

    [Fact]
    public void Nobody_is_hurt_during_the_countdown_and_the_check_is_the_one_every_damage_path_uses()
    {
        var w = Arena();
        w.Step(Idle);
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.False(w.Harms(1, 2));
        Assert.False(w.Harms(2, 1));
        Assert.False(w.Harms(1, 1));
        while (w.State == MatchState.Countdown) w.Step(Idle);
        Assert.True(w.Harms(1, 2));
    }

    [Fact]
    public void A_side_that_empties_during_the_countdown_sends_the_match_back_to_warmup()
    {
        var w = Arena();
        w.Step(Idle);
        for (int k = 0; k < 5; k++) w.Step(Idle);
        Assert.Equal(MatchState.Countdown, w.State);
        w.Remove(2);
        w.Step(Idle);
        Assert.Equal((MatchState.Warmup, 0u), (w.State, w.TicksLeft));
        w.Spawn(2, CsmTac);
        w.Step(Idle);
        Assert.Equal((MatchState.Countdown, (uint)CountdownTicks), (w.State, w.TicksLeft));   // a fresh countdown, not the remainder
    }

    [Fact]
    public void Without_a_countdown_time_a_round_starts_at_once_as_before_the_match_flow()
    {
        var w = Arena(Rules with { CountdownTime = 0f });
        w.Step(Idle);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(0f, new MatchRules().CountdownTime);                          // the sim default: none; only the server asks for one
        Assert.Equal(5f, MatchRules.DefaultCountdown);
    }

    [Fact]
    public void After_a_match_ends_the_restart_goes_through_the_countdown_again_with_everyone_respawned()
    {
        // One rifle shot kills (20 health), the score limit is 1: the first kill ends the match.
        var fragile = Config with { HealthMax = 20f };
        var f = new World(fragile, null, [new SpawnPoint(new Vector3(0, 0, 0), 0f), new SpawnPoint(new Vector3(0, 5, 0), 0f)]) { Rules = Rules with { ScoreLimit = 1 } };
        f.Spawn(1, SmTac); f.Spawn(2, CsmTac);
        var states = new List<MatchState>();
        void Track() { if (states.Count == 0 || states[^1] != f.State) states.Add(f.State); }
        Track();
        while (f.State != MatchState.Playing) { f.Step(Idle); Track(); }
        f.Step(Only1(InputButtons.Fire)); Track();
        Assert.Equal((MatchState.Ended, Team.SpaceMarines), (f.State, f.Winner));
        for (int k = 0; k < 400 && f.State != MatchState.Playing; k++)
        {
            f.Step(Idle); Track();
            if (f.State == MatchState.Countdown) Assert.All(f.Players.Values, p => Assert.True(p.Alive && (p.Position == new Vector3(0, 0, 0) || p.Position == new Vector3(0, 5, 0))));   // respawned at once
        }
        Assert.Equal([MatchState.Warmup, MatchState.Countdown, MatchState.Playing, MatchState.Ended, MatchState.Countdown, MatchState.Playing], states);   // the end goes straight to the next countdown
        Assert.All(f.Players.Values, p => Assert.True(p.Alive));
        Assert.Equal((0, 0), (f.ScoreOf(Team.SpaceMarines), f.ScoreOf(Team.Chaos)));
    }

    [Fact]
    public void A_grenade_in_flight_when_the_countdown_starts_is_gone_and_nothing_flies_into_play()
    {
        var w = Arena(both: false);
        for (int k = 0; k < 60 && w.Projectiles.Count == 0; k++) w.Step(Only1(k == 0 ? InputButtons.Grenade : InputButtons.None));
        Assert.NotEmpty(w.Projectiles);                                           // a thrown grenade is on its way in warm-up
        w.Spawn(2, CsmTac);
        w.Step(Idle);
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.Empty(w.Projectiles);
        Assert.Empty(w.Areas);
        Assert.All(w.Players.Values, p => Assert.Equal(0, p.ProjectilesOut));
        while (w.State == MatchState.Countdown) { w.Step(Only1(InputButtons.Grenade)); Assert.Empty(w.Projectiles); }
        Assert.Empty(w.Projectiles);
        Assert.Equal(1000f, w.Players[2].Health);
    }

    [Fact]
    public void A_player_who_joins_during_the_countdown_waits_with_the_others_and_cannot_attack()
    {
        var w = Arena();
        w.Step(Idle);
        for (int k = 0; k < 10; k++) w.Step(Idle);
        uint left = w.TicksLeft;
        Assert.Equal(MatchState.Countdown, w.State);
        w.Spawn(3, SmTac);
        w.Step(Idle);
        Assert.Equal((MatchState.Countdown, left - 1), (w.State, w.TicksLeft));   // the countdown is not restarted by a join
        Assert.Equal(3, w.Players.Count);
        int clip = w.Players[3].Clip;
        w.Step(id => id == 3 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Fire | InputButtons.Melee | InputButtons.Grenade) : Idle(id));
        Assert.Empty(w.LastShots);
        Assert.Equal(clip, w.Players[3].Clip);
        while (w.State == MatchState.Countdown) w.Step(Idle);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(3, w.Players.Count(p => p.Value.Alive));
    }

    [Fact]
    public void Seize_Ground_with_a_countdown_holds_the_points_until_play_and_the_score_clock_starts_after_it()
    {
        var point = new ControlPoint("mkr_objective_1", new Vector3(0, 0, 0), 5f, 10f);
        var rules = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 60f, CountdownTime = 1f };
        var start = (Vector3)new(0, 0, 0);
        var w = new World(Config) { Rules = rules, ControlPoints = [point], StartAreas = [new StartArea(Team.SpaceMarines, start, new Vector3(1, 1, 2), 0f), new StartArea(Team.Chaos, new Vector3(-60, 0, 0), new Vector3(1, 1, 2), MathF.PI)] };
        w.Spawn(1, SmTac); w.Spawn(2, CsmTac);
        w.Step(Idle);
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.Equal(start, w.Players[1].Position);                              // the marine stands on the point: it must not count yet
        for (int k = 0; k < CountdownTicks - 1; k++) { w.Step(Idle); Assert.Equal(default, w.Points[0]); }
        Assert.Equal(1u, w.TicksLeft);
        w.Step(Idle);
        Assert.Equal((MatchState.Playing, (uint)(60 * Config.TickRate)), (w.State, w.TicksLeft));
        Assert.Equal(default, w.Points[0]);                                      // neutral at the first tick of play
        w.Step(Idle);
        Assert.Equal(Team.SpaceMarines, w.Points[0].Side);                       // capture counts from the tick after play began
        Assert.Equal(1, w.Points[0].Progress);
        Assert.True(w.Harms(1, 2));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void The_countdown_travels_as_warmup_with_ticks_left_and_reads_back_as_a_countdown()
    {
        Assert.Equal(MatchState.Warmup, MatchFlowWire.ToWire(MatchState.Countdown));
        foreach (var s in new[] { MatchState.None, MatchState.Warmup, MatchState.Playing, MatchState.Ended }) Assert.Equal(s, MatchFlowWire.ToWire(s));

        var w = Arena();
        w.Step(Idle);
        var view = GameServer.MatchOf(w);
        Assert.Equal((MatchState.Warmup, (uint)CountdownTicks), (view.State, view.TicksLeft));
        // Through the real encoder: nothing about the layout changed (it fits the same bits).
        var read = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Self: 0, Match: view))));
        Assert.Equal(view, read.Match);
        Assert.True(read.Match!.Value.IsCountdown());
        Assert.Equal(MatchState.Countdown, read.Match.Value.Effective());

        // Plain warm-up, play and the end are not countdowns.
        Assert.False(new MatchView(MatchState.Warmup, Team.None, 0, 0, 0).IsCountdown());
        Assert.False(new MatchView(MatchState.Playing, Team.None, 30, 0, 0).IsCountdown());
        Assert.False(new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 0, 0).IsCountdown());
        Assert.False(new MatchView(MatchState.Ended, Team.SpaceMarines, 30, 0, 0).IsCountdown());
        Assert.Equal(MatchState.Warmup, new MatchView(MatchState.Warmup, Team.None, 0, 0, 0).Effective());
        Assert.Equal(MatchState.Ended, new MatchView(MatchState.Ended, Team.Chaos, 30, 0, 0).Effective());
    }

    [Fact]
    public void The_shared_lock_drops_exactly_the_attack_buttons()
    {
        var all = (InputButtons)0xFFFF;
        var locked = MatchFlow.Locked(new PlayerInput(5, 0.5f, 1f, 1f, all));
        Assert.Equal(InputButtons.None, locked.Buttons & MatchFlow.LockedDuringCountdown);
        Assert.Equal(all & ~MatchFlow.LockedDuringCountdown, locked.Buttons);
        Assert.True(locked.Buttons.HasFlag(InputButtons.Jump) && locked.Buttons.HasFlag(InputButtons.Sprint) && locked.Buttons.HasFlag(InputButtons.Aim));
        Assert.Equal((5u, 0.5f, 1f, 1f), (locked.Tick, locked.MoveX, locked.MoveY, locked.Yaw));
        // Only a running countdown changes a client's input, and only the ticks inside it (protocol 25): a snapshot of tick 100 that says
        // 20 ticks to go locks the server's steps 101..120; step 121 is the first of play.
        var countdown = new MatchView(MatchState.Warmup, Team.None, 20, 0, 0);
        PlayerInput Press(uint tick) => new(tick, 0, 0, 0, InputButtons.Fire | InputButtons.Jump);
        foreach (uint tick in new uint[] { 101, 110, 120 }) Assert.Equal(InputButtons.Jump, countdown.Predicted(100, Press(tick)).Buttons);
        foreach (uint tick in new uint[] { 99, 100, 121, 140 }) Assert.Equal(Press(tick), countdown.Predicted(100, Press(tick)));
        Assert.False(new MatchView(MatchState.Playing, Team.None, 20, 0, 0).Locks(100, 105));
        Assert.False(new MatchView(MatchState.Warmup, Team.None, 0, 0, 0).Locks(100, 105));
        Assert.False(new MatchView(MatchState.Warmup, Team.None, uint.MaxValue, 0, 0).Locks(100, 105));
        Assert.True(new MatchView(MatchState.Warmup, Team.None, 20, 0, 0).Locks(uint.MaxValue - 5, uint.MaxValue));   // no overflow at the end of the tick range
    }

    [Fact]
    public void The_splash_and_the_stinger_follow_the_viewers_side_and_the_winner()
    {
        var (sm, csm, none) = (Team.SpaceMarines, Team.Chaos, Team.None);
        Assert.Equal([MatchResult.Victory, MatchResult.Defeat, MatchResult.Defeat, MatchResult.Victory],
            [MatchFlow.ResultFor(sm, sm), MatchFlow.ResultFor(csm, sm), MatchFlow.ResultFor(sm, csm), MatchFlow.ResultFor(csm, csm)]);
        Assert.Equal([MatchResult.Draw, MatchResult.Draw], [MatchFlow.ResultFor(sm, none), MatchFlow.ResultFor(csm, none)]);
        Assert.Equal(MatchResult.Victory, MatchFlow.ResultFor(none, csm));                              // no side: the winner's view
        Assert.Equal("play_mp_sm_victory play_mp_sm_loss play_mp_chaos_victory play_mp_chaos_loss",
            string.Join(' ', MatchFlow.StingerFor(sm, MatchResult.Victory), MatchFlow.StingerFor(sm, MatchResult.Defeat), MatchFlow.StingerFor(csm, MatchResult.Victory), MatchFlow.StingerFor(csm, MatchResult.Defeat)));
        Assert.Null(MatchFlow.StingerFor(sm, MatchResult.Draw));                                        // the data has no draw stinger
    }

    [Fact]
    public void The_display_counts_whole_seconds_down_to_one()
    {
        Assert.Equal([5, 5, 4, 4, 1, 1, 0], new[] { 5.0, 4.01, 4.0, 3.2, 1.0, 0.01, 0.0 }.Select(MatchFlow.DisplaySeconds));
        Assert.Equal(6.5f, MatchFlow.SplashSeconds);
    }

    // ---------------------------------------------------------------- the server and a client

    [Fact]
    public void A_client_sees_the_countdown_and_its_prediction_spends_no_ammunition_during_it()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 3, 0, Config, new ServerOptions { Match = Rules with { CountdownTime = 2f, ScoreLimit = 1000 } });
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var b = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = CsmTac });
        h.Run(TimeSpan.FromMilliseconds(700));
        Assert.Equal([ClientStatus.Connected, ClientStatus.Connected], h.Clients.Select(c => c.Status));
        Assert.Equal(MatchState.Countdown, h.Server.World.State);
        Assert.True(a.Match.IsCountdown() && b.Match.IsCountdown(), $"{a.Match} {b.Match}");
        int clip = a.Predicted.Clip;
        int fireTicks = 0;
        // Client 0 holds the trigger while the countdown runs (about 1 s more).
        h.Run(TimeSpan.FromMilliseconds(500), (client, tick) =>
        {
            if (client == 0 && h.Server.World.State == MatchState.Countdown) fireTicks++;
            return new PlayerInput(tick, 0, 0, 0, client == 0 ? InputButtons.Fire : InputButtons.None);
        });
        Assert.True(fireTicks > 5, $"{fireTicks}");                                 // the client samples its input once per tick
        Assert.Equal(MatchState.Countdown, h.Server.World.State);
        Assert.Equal(clip, a.Predicted.Clip);                                       // the prediction dropped the trigger
        Assert.Equal(clip, h.Server.World.Players[a.PlayerId].Clip);
        // Then play begins and the match block says so.
        h.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        Assert.False(a.Match.IsCountdown());
        Assert.Equal(MatchState.Playing, a.Match.State);
    }

    [Fact]
    public void A_fire_held_through_the_countdown_reaches_the_server_on_the_first_tick_of_play()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, 0);   // a long round trip: the snapshot that says "countdown" is old when play begins
        var h = new Harness(link, 11, 0, Config, new ServerOptions { Match = Rules with { CountdownTime = 1.5f, ScoreLimit = 1000 } });
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = CsmTac });
        PlayerInput Hold(int client, uint tick) => new(tick, 0, 0, client == 0 ? 0f : MathF.PI, client == 0 ? InputButtons.Fire : InputButtons.None);
        int clip = 0; uint playTick = 0, shotTick = 0;
        for (int ms = 0; ms < 6000 && shotTick == 0; ms++)
        {
            h.Step(Hold);
            var world = h.Server.World;
            if (world.State == MatchState.Countdown && clip == 0 && world.Players.TryGetValue(a.PlayerId, out var p0)) clip = p0.Clip;
            if (world.State == MatchState.Playing && playTick == 0) playTick = world.Tick;
            if (playTick != 0 && world.Players[a.PlayerId].Clip < clip) shotTick = world.Tick;
        }
        Assert.NotEqual(0u, playTick);
        Assert.NotEqual(0u, shotTick);
        Assert.True(a.Predicted.Clip < clip, $"the client had not predicted the first shot of play yet: it kept the lock past the countdown's last tick (predicted {a.Predicted.Clip}, server start {clip}, client tick {a.Tick}, server tick {h.Server.World.Tick}, match {a.Match})");
        Assert.InRange(shotTick - playTick, 1u, 2u);                            // the first step of play fires: the raw input was sent, the client did not keep dropping it
        // The prediction agrees at the next snapshots: no ammunition flicker.
        h.Run(TimeSpan.FromMilliseconds(400), Hold);
        Assert.Equal(h.Server.World.Players[a.PlayerId].Clip, a.Predicted.Clip);
    }

    [Fact]
    public void Plain_warmup_always_carries_zero_ticks_left_so_a_countdown_is_never_mistaken_and_a_raw_countdown_is_refused_on_the_wire()
    {
        // Through every phase of a full cycle (warm-up, countdown, play, end, cancelled countdown), with and without a countdown time.
        foreach (float countdown in new[] { 0f, 1f })
        {
            var w = Arena(Rules with { CountdownTime = countdown, ScoreLimit = 1 }, both: false);
            var seen = new List<MatchState>();
            void Check()
            {
                var view = GameServer.MatchOf(w);
                Assert.NotEqual(MatchState.Countdown, view.State);
                if (view.State == MatchState.Warmup) Assert.Equal(w.State == MatchState.Countdown, view.IsCountdown());
                if (w.State == MatchState.Warmup) Assert.Equal(0u, view.TicksLeft);
                seen.Add(w.State);
            }
            for (int k = 0; k < 5; k++) { w.Step(Idle); Check(); }
            w.Spawn(2, CsmTac);
            for (int k = 0; k < 20; k++) { w.Step(Idle); Check(); }
            w.Remove(2);                                                          // cancels a countdown (or ends a match without one)
            for (int k = 0; k < 30; k++) { w.Step(Idle); Check(); }
            Assert.Contains(MatchState.Warmup, seen);
        }
        var raw = new Snapshot(1, 1, 1, [], Self: 0, Match: new MatchView(MatchState.Countdown, Team.None, 20, 0, 0));
        Assert.Throws<ArgumentException>(() => Protocol.Write(raw));
    }

    [Fact]
    public void The_countdown_time_is_clamped_to_a_sane_range()
    {
        Assert.Equal(MatchRules.MaxCountdown, new MatchRules { CountdownTime = 1e9f }.CountdownTime);
        Assert.Equal(0f, new MatchRules { CountdownTime = -3f }.CountdownTime);
        Assert.Equal(0f, new MatchRules { CountdownTime = float.NaN }.CountdownTime);
        Assert.Equal(0f, new MatchRules { CountdownTime = float.PositiveInfinity }.CountdownTime);
        Assert.Equal(2.5f, new MatchRules { CountdownTime = 2.5f }.CountdownTime);
        var w = Arena(Rules with { CountdownTime = 1e9f });
        w.Step(Idle);
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.InRange(w.TicksLeft, 1u, (uint)(MatchRules.MaxCountdown * Config.TickRate));   // far below the wire's "no limit" value
    }
}
