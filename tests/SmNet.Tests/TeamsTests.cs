using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Teams and Annihilation (M15): side assignment and balance, friendly fire on every damage path (with enemy controls and
/// self damage unchanged), scoring (kills, suicides, team kills), the score and time limits, warm-up, an emptied side, the
/// restart, team spawns and the respawn timer, the kill feed and scoreboard end to end, prediction with teams, protocol 10
/// bounds and refusals, and the config hash. Synthetic classes and weapons only (no game data).
/// </summary>
public class TeamsTests(ITestOutputHelper output)
{
    const int SmTac = 0, SmJump = 1, CsmTac = 2, CsmJump = 3, Neutral = 4;
    const int RifleChoice = 0, MeltaChoice = 1, DartChoice = 2, LingerChoice = 3, StickyChoice = 4;

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly SplashSpec Blast = new(6f, new DamageSpec(75f, 0f, 1f, 1f, 0f, 0f, false), [new(0f, 1f), new(4f, 1f), new(5f, 0.5f), new(6f, 0.05f)], true, false, "target_all");
    /// <summary>A dart: 30 m/s, no gravity, detonates on impact with a 6 m blast that also hurts its owner.</summary>
    static readonly ProjectileSpec Dart = new("ebps\\test\\dart", 30f, 0f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null, Blast);
    static readonly WeaponSpec DartGun = new("test_dart_gun", WeaponSlot.Ranged, TriggerKind.SemiAuto, 10f, 0.2f, 10, 20, 0.6f, 150f, 150f, 150f, 1f, 1f, 1f, 0f, 0f)
    { Delivery = DeliveryKind.Projectile, Projectile = Dart };
    /// <summary>The dart with a lingering blast (every 0.25 s for 5 s).</summary>
    static readonly WeaponSpec LingerGun = DartGun with { Name = "test_linger_gun", Projectile = Dart with { Blueprint = "ebps\\test\\linger", Splash = Blast with { Lifetime = 5f, Period = 0.25f } } };
    /// <summary>A sticky dart: sticks to what it strikes, detonates 1 s after launch.</summary>
    static readonly WeaponSpec StickyGun = DartGun with { Name = "test_sticky_gun", Damage = 0f,
        Projectile = new ProjectileSpec("ebps\\test\\sticky", 30f, 1f, "raycast", 0f, [new(0f, 0f)], false, true, true, 0, 0f, 0f, 0, null, Blast) };

    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps)
    { PrimaryChoices = [0, 2, 3, 4, 5], PistolChoices = [1], EquipmentChoices = [0], Side = side, Role = "tactical" };
    static ClassLoadout Jumper(string name, Team side) => new(name, -1, 1, SimConfig.PlaceholderMeleeSteps)
    { PistolChoices = [1], JumpPack = 0, Side = side, Role = "assault" };

    /// <summary>No shield and a deep health pool: damage shows as exact health losses.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Sidearm, MeltaTests.Melta, DartGun, LingerGun, StickyGun],
        Thrown = [GrenadeTests.Frag],
        Classes = [Tac("sm - tactical", Team.SpaceMarines), Jumper("sm - assault", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos), Jumper("csm - raptor", Team.Chaos),
                   Tac("neutral", Team.None) with { Role = "" }],
        JumpPacks = [JumpPackTests.Pack],
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f, MaxRewindTicks = 40,
    };
    /// <summary>Two rifle hits kill; a quick respawn.</summary>
    static readonly SimConfig Fragile = Config with { HealthMax = 40f, RespawnTime = 0.5f };
    static readonly MatchRules Rules = new() { TimeLimit = 0f };

    static float Chest => (Config.BodyBottom + Config.BodyTop) / 2f;

    /// <summary>A world with players 1..n of the given classes at the given points (spawn points in that order; checked).</summary>
    static World Arena(SimConfig config, MatchRules? rules, params (int Class, Vector3 At, int Primary)[] players)
    {
        var world = new World(config, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = rules };
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class, players[i].Primary);
        for (int i = 0; i < players.Length; i++) Assert.Equal(players[i].At, world.Players[i + 1].Position);
        return world;
    }

    static (int, Vector3, int) P(int cls, float x, float y, int primary = RifleChoice) => (cls, new Vector3(x, y, 0), primary);

    /// <summary>Player 1 presses <paramref name="buttons"/> facing +Y at <paramref name="pitch"/>; everyone else idles.</summary>
    static Func<int, PlayerInput> Only1(InputButtons buttons, float pitch = 0f, float yaw = 0f) =>
        id => id == 1 ? new PlayerInput(0, 0, 0, yaw, buttons, pitch) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None);
    static PlayerInput Idle(int id) => new(0, 0, 0, MathF.PI, InputButtons.None);

    static float Health(World w, int id) => w.Players[id].Health;

    // ---------------------------------------------------------------- sides

    [Fact]
    public void A_joining_player_gets_its_classes_side_unless_that_side_is_larger_then_the_same_role_on_the_other()
    {
        var none = new World(Config);
        Assert.Equal(SmTac, none.AssignClass(SmTac));                        // no rules: as asked
        Assert.Equal(Team.None, none.Spawn(1, SmTac).Team);

        var w = new World(Config) { Rules = Rules };
        Assert.Equal(SmTac, w.AssignClass(SmTac));                           // 0 v 0
        Assert.Equal(Team.SpaceMarines, w.Spawn(1, w.AssignClass(SmTac)).Team);
        Assert.Equal(CsmTac, w.AssignClass(SmTac));                          // 1 v 0: moved, same role
        Assert.Equal(CsmJump, w.AssignClass(SmJump));
        Assert.Equal(Team.Chaos, w.Spawn(2, w.AssignClass(SmJump)).Team);
        Assert.Equal(SmTac, w.AssignClass(SmTac));                           // 1 v 1: as asked
        Assert.Equal(CsmTac, w.AssignClass(SmTac, Team.Chaos));              // a requested side wins when it is not larger
        Assert.Equal(SmJump, w.AssignClass(CsmJump, Team.SpaceMarines));
        w.Spawn(3, w.AssignClass(CsmTac, Team.SpaceMarines));
        Assert.Equal((Team.SpaceMarines, SmTac), (w.Players[3].Team, w.Players[3].Class));
        Assert.Equal(CsmTac, w.AssignClass(SmTac, Team.SpaceMarines));       // 2 v 1: the request yields to balance
        Assert.Equal(CsmTac, w.AssignClass(CsmTac, Team.None));
        // A class without a side keeps its class and joins the smaller side (ties: Space Marines).
        Assert.Equal(Neutral, w.AssignClass(Neutral));
        Assert.Equal(Team.Chaos, w.Spawn(4, Neutral).Team);
        Assert.Equal(Team.SpaceMarines, w.Spawn(5, Neutral).Team);
        // A full side (MaxPerSide) is refused even when it is not larger.
        var capped = new World(Config) { Rules = Rules with { MaxPerSide = 1 } };
        capped.Spawn(1, SmTac); capped.Spawn(2, CsmTac);
        Assert.Equal(CsmTac, capped.AssignClass(SmTac));
        Assert.Equal(2, capped.CountSide(Team.SpaceMarines) + capped.CountSide(Team.Chaos));
    }

    [Fact]
    public void With_honor_side_requests_a_requested_side_is_kept_up_to_the_limit_and_the_balance_applies_only_to_joins_without_one()
    {
        var w = new World(Config) { Rules = Rules with { HonorSideRequests = true, MaxPerSide = 3 } };
        // Space Marines asked for six times in a row: kept up to MaxPerSide, then the other side takes the rest.
        for (int id = 1; id <= 3; id++)
        {
            Assert.Equal(SmTac, w.AssignClass(SmTac, Team.SpaceMarines));
            Assert.Equal(Team.SpaceMarines, w.Spawn(id, w.AssignClass(SmTac, Team.SpaceMarines)).Team);   // 1 v 0, 2 v 0, 3 v 0: not balanced away
        }
        Assert.Equal(CsmTac, w.AssignClass(SmTac, Team.SpaceMarines));   // 3 v 0 and the side is full: the other side
        Assert.Equal(CsmTac, w.AssignClass(CsmTac, Team.Chaos));
        Assert.Equal(CsmJump, w.AssignClass(SmJump, Team.Chaos));        // a class of the other side keeps its role on the asked side
        // A join with no side request is still balanced: 3 v 0 sends it to Chaos.
        Assert.Equal(CsmTac, w.AssignClass(SmTac));
        Assert.Equal(CsmTac, w.AssignClass(SmTac, Team.None));
        for (int id = 4; id <= 6; id++) w.Spawn(id, w.AssignClass(CsmTac, Team.Chaos));
        Assert.Equal(3, w.CountSide(Team.Chaos));
        // Both sides full: the rule falls back on the other side (nowhere to go; the server's player limit refuses first), as without the option.
        Assert.Equal(CsmTac, w.AssignClass(SmTac, Team.SpaceMarines));
        // Default off: the same request is balanced away (existing behaviour).
        var normal = new World(Config) { Rules = Rules with { MaxPerSide = 3 } };
        normal.Spawn(1, SmTac);
        Assert.Equal(CsmTac, normal.AssignClass(SmTac, Team.SpaceMarines));
        Assert.False(new MatchRules().HonorSideRequests);
    }

    [Fact]
    public void The_server_balances_joining_clients_and_honours_a_requested_side()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 7, 0, Config, new ServerOptions { Match = Rules });
        var joined = new List<PlayerState>();
        h.Server.PlayerJoined += joined.Add;
        void Join(uint nonce, int cls, Team side = Team.None)
        {
            h.AddClient(nonce: nonce, options: new ClientOptions { RequestedClass = (byte)cls, RequestedSide = side });
            h.Run(TimeSpan.FromMilliseconds(300));
        }
        Join(100, SmTac); Join(101, SmTac); Join(102, SmTac); Join(103, SmTac, Team.Chaos); Join(104, CsmJump, Team.SpaceMarines);
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.Equal([(Team.SpaceMarines, SmTac), (Team.Chaos, CsmTac), (Team.SpaceMarines, SmTac), (Team.Chaos, CsmTac), (Team.SpaceMarines, SmJump)],
            joined.Select(p => (p.Team, p.Class)));
        // Each client learns its side from its own record, and every client sees the others' sides (slim records).
        h.Run(TimeSpan.FromMilliseconds(300));
        foreach (var c in h.Clients)
        {
            Assert.Equal(h.Server.World.Players[c.PlayerId].Team, c.Predicted.Team);
            Assert.All(c.RemotePlayers().Players, r => Assert.Equal(h.Server.World.Players[r.PlayerId].Team, r.Team));
        }
        Assert.Equal((Team)0, GameServer.SideFor(0));
        Assert.Equal((Team.SpaceMarines, Team.Chaos, Team.None), (GameServer.SideFor(1), GameServer.SideFor(2), GameServer.SideFor(3)));
    }

    // ---------------------------------------------------------------- friendly fire, path by path

    [Fact]
    public void Rays_do_not_hurt_allies_who_still_stop_them_and_hurt_enemies()
    {
        var w = Arena(Config, Rules, P(SmTac, 0, 0), P(SmTac, 0, 5), P(CsmTac, 0, 10));
        w.Step(Only1(InputButtons.Fire));
        Assert.Empty(w.LastHits);
        Assert.Equal((1000f, 1000f), (Health(w, 2), Health(w, 3)));         // the ally stops the ray: the enemy behind is safe
        // Control: an enemy in front takes the shot.
        var enemy = Arena(Config, Rules, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        enemy.Step(Only1(InputButtons.Fire));
        Assert.Equal((2, 980f), (Assert.Single(enemy.LastHits).Target, Health(enemy, 2)));
        // With friendly fire the ally is hit.
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmTac, 0, 0), P(SmTac, 0, 5), P(CsmTac, 0, 10));
        ff.Step(Only1(InputButtons.Fire));
        Assert.Equal((980f, 1000f), (Health(ff, 2), Health(ff, 3)));
        // Without rules (free-for-all, as before M15) everyone is hostile.
        var ffa = Arena(Config, null, P(SmTac, 0, 0), P(SmTac, 0, 5));
        ffa.Step(Only1(InputButtons.Fire));
        Assert.Equal(980f, Health(ffa, 2));
    }

    [Fact]
    public void The_cone_skips_allies_and_burns_enemies()
    {
        float pitch = MathF.Atan2(Chest - Config.EyeHeight, 5f);
        var w = Arena(Config, Rules, P(SmTac, 0, 0, MeltaChoice), P(SmTac, -1.1f, 5), P(CsmTac, 1.1f, 5));
        w.Step(Only1(InputButtons.Fire, pitch));
        Assert.Equal(1000f, Health(w, 2));
        Assert.True(Health(w, 3) < 1000f, $"{Health(w, 3)}");
        Assert.All(w.LastHits, h => Assert.True(h.Cone && h.Target == 3 && h.Weapon == 2));
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmTac, 0, 0, MeltaChoice), P(SmTac, -1.1f, 5), P(CsmTac, 1.1f, 5));
        ff.Step(Only1(InputButtons.Fire, pitch));
        Assert.True(Health(ff, 2) < 1000f && Health(ff, 3) < 1000f);
    }

    static void FireDart(World w, float pitch = 0f)
    {
        w.Step(Only1(InputButtons.Fire, pitch));
        for (int k = 0; k < 30 && w.Projectiles.Count > 0; k++) w.Step(Idle);
        Assert.Empty(w.Projectiles);
    }

    [Fact]
    public void A_projectile_on_an_ally_deals_nothing_to_allies_but_its_blast_hits_enemies_and_its_owner()
    {
        var w = Arena(Config, Rules, P(SmTac, 0, 0, DartChoice), P(SmTac, 0, 5), P(CsmTac, 2, 5.5f));
        var hits = new List<HitEvent>();
        w.Step(Only1(InputButtons.Fire)); hits.AddRange(w.LastHits);
        for (int k = 0; k < 30 && w.Projectiles.Count > 0; k++) { w.Step(Idle); hits.AddRange(w.LastHits); }
        Assert.Empty(w.Projectiles);
        Assert.Equal(1000f, Health(w, 2));                                    // neither the direct hit nor the blast
        Assert.Equal(925f, Health(w, 3));                                     // the full blast
        Assert.True(Health(w, 1) < 1000f, $"{Health(w, 1)}");                // self damage is unchanged
        Assert.Contains(hits, h => h.Target == 1 && h.Shooter == 1 && h.Splash);
        Assert.All(hits, h => Assert.Equal((3, false), (h.Weapon, h.ThrownItem)));
        output.WriteLine($"owner took {1000f - Health(w, 1):F1} from its own blast");
        // Control: with friendly fire the ally takes the direct hit and the blast.
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmTac, 0, 0, DartChoice), P(SmTac, 0, 5), P(CsmTac, 2, 5.5f));
        FireDart(ff);
        Assert.Equal(1000f - 10f - 75f, Health(ff, 2));
    }

    [Fact]
    public void Melee_passes_over_allies_to_the_enemy()
    {
        static List<HitEvent> Swing(World w)
        {
            var hits = new List<HitEvent>();
            for (int k = 0; k < 60 && hits.Count == 0; k++) { w.Step(Only1(k == 0 ? InputButtons.Melee : InputButtons.None)); hits.AddRange(w.LastHits); }
            return hits;
        }
        var w = Arena(Config, Rules, P(SmTac, 0, 0), P(SmTac, 0, 2.2f), P(CsmTac, 1.2f, 3.2f));
        var hit = Assert.Single(Swing(w));
        Assert.Equal((3, true, 40f), (hit.Target, hit.Melee, hit.HealthDamage));   // the enemy is the primary target
        Assert.Equal(1000f, Health(w, 2));
        // Control: with friendly fire the nearer ally is the primary target and the enemy takes the secondary damage.
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmTac, 0, 0), P(SmTac, 0, 2.2f), P(CsmTac, 1.2f, 3.2f));
        var both = Swing(ff);
        Assert.Equal([(2, 40f), (3, 20f)], both.Select(h => (h.Target, h.HealthDamage)));
    }

    [Fact]
    public void The_ground_pound_spares_allies_and_hits_enemies()
    {
        static void Pound(World w)
        {
            bool pounded = false;
            for (int k = 0; k < 300; k++)
            {
                var p = w.Players[1];
                var b = k == 0 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
                if (b == InputButtons.Melee) pounded = true;
                w.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, b, -1.4f) : Idle(id));
                if (w.LastJumps.Any(j => j.Kind == JumpEventKind.Impact)) return;
            }
            throw new Xunit.Sdk.XunitException("no impact");
        }
        var w = Arena(Config, Rules, P(SmJump, 0, 0), P(SmTac, 2.5f, 0), P(CsmTac, -2.5f, 0));
        Pound(w);
        Assert.Equal(1000f, Health(w, 2));
        Assert.True(Health(w, 3) < 1000f, $"{Health(w, 3)}");
        Assert.Contains(w.LastHits, h => h.Landing && h.Target == 3);
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmJump, 0, 0), P(SmTac, 2.5f, 0), P(CsmTac, -2.5f, 0));
        Pound(ff);
        Assert.True(Health(ff, 2) < 1000f);
    }

    // ---------------------------------------------------------------- scoring

    /// <summary>Player 1 fires the rifle (press edges) at its yaw until <paramref name="target"/> dies.</summary>
    static void ShootDead(World w, int target, float yaw = 0f)
    {
        for (int k = 0; k < 200 && w.Players[target].Alive; k++) w.Step(Only1(k % 2 == 0 ? InputButtons.Fire : InputButtons.None, 0f, yaw));
        Assert.False(w.Players[target].Alive);
    }

    [Fact]
    public void A_kill_scores_for_the_killers_side_and_names_the_killer_victim_and_weapon()
    {
        var w = Arena(Fragile, Rules, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        w.Step(Idle);
        Assert.Equal(MatchState.Playing, w.State);
        ShootDead(w, 2);
        var kill = Assert.Single(w.LastKills);
        Assert.Equal((1, 2, KillCause.Weapon, 0), (kill.Killer, kill.Victim, kill.Cause, kill.Index));
        Assert.Equal((1, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal((new PlayerScore(1, 0), new PlayerScore(0, 1)), (w.Scores[1], w.Scores[2]));
        Assert.Equal(kill, Assert.Single(w.RecentKills));
        for (int k = 0; k < World.KillMemoryTicks; k++) w.Step(Idle);
        Assert.Empty(w.RecentKills);                                          // remembered for KillMemoryTicks ticks
        Assert.Empty(w.LastKills);
    }

    [Fact]
    public void A_suicide_scores_for_the_other_side_and_is_no_kill()
    {
        var w = Arena(Fragile, Rules, P(SmTac, 0, 0, DartChoice), P(CsmTac, 0, 20));
        w.Step(Idle);
        FireDart(w, -1.4f);                                                   // at its own feet
        Assert.False(w.Players[1].Alive);
        var kill = w.RecentKills.Single();
        Assert.Equal((1, 1, KillCause.Weapon, 3), (kill.Killer, kill.Victim, kill.Cause, kill.Index));
        Assert.Equal((0, 1), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal(new PlayerScore(0, 1), w.Scores[1]);
    }

    [Fact]
    public void A_team_kill_with_friendly_fire_scores_for_the_other_side_and_is_no_kill()
    {
        var w = Arena(Fragile, Rules with { FriendlyFire = true }, P(SmTac, 0, 0), P(SmTac, 0, 5), P(CsmTac, 20, -20));
        w.Step(Idle);
        ShootDead(w, 2);
        var kill = Assert.Single(w.LastKills);
        Assert.Equal((1, 2), (kill.Killer, kill.Victim));
        Assert.Equal((0, 1), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal((0, 1), (w.Scores[1].Kills, w.Scores[2].Deaths));
    }

    [Fact]
    public void The_score_limit_ends_the_match_stops_damage_and_the_match_restarts_with_everyone_respawned()
    {
        var w = Arena(Fragile, Rules with { ScoreLimit = 2, RestartDelay = 1f }, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        w.Step(Idle);
        ShootDead(w, 2);
        while (!w.Players[2].Alive) w.Step(Idle);
        Assert.Equal(new Vector3(0, 5, 0), w.Players[2].Position);           // the only free spawn
        ShootDead(w, 2);
        Assert.Equal((MatchState.Ended, Team.SpaceMarines, 2), (w.State, w.Winner, w.ScoreOf(Team.SpaceMarines)));
        Assert.Equal(30u, w.TicksLeft);
        // Nothing hurts after the end (the victim respawned; the winner's shots and its own blasts deal nothing).
        while (!w.Players[2].Alive) w.Step(Idle);
        for (int k = 0; k < 10; k++) w.Step(Only1(k % 2 == 0 ? InputButtons.Fire : InputButtons.None));
        Assert.Equal(40f, Health(w, 2));
        Assert.Equal(MatchState.Ended, w.State);
        // The restart: 30 ticks after the end.
        while (w.State == MatchState.Ended) w.Step(Idle);
        Assert.Equal((MatchState.Playing, Team.None, 0, 0), (w.State, w.Winner, w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.All(w.Players.Values, p => Assert.Equal((true, 40f), (p.Alive, p.Health)));
        Assert.All(w.Scores.Values, s => Assert.Equal(default, s));
        Assert.Equal(uint.MaxValue, w.TicksLeft);                            // no time limit in these rules
    }

    [Fact]
    public void The_time_limit_ends_the_match_with_the_higher_score_winning_or_a_draw()
    {
        var draw = Arena(Fragile, Rules with { TimeLimit = 1f }, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        draw.Step(Idle);
        Assert.Equal((MatchState.Playing, 30u), (draw.State, draw.TicksLeft));
        for (int k = 0; k < 29; k++) draw.Step(Idle);
        Assert.Equal((MatchState.Playing, 1u), (draw.State, draw.TicksLeft));
        draw.Step(Idle);
        Assert.Equal((MatchState.Ended, Team.None), (draw.State, draw.Winner));

        var won = Arena(Fragile, Rules with { TimeLimit = 2f }, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        won.Step(Idle);
        ShootDead(won, 2);
        while (won.State == MatchState.Playing) won.Step(Idle);
        Assert.Equal((Team.SpaceMarines, 1, 0), (won.Winner, won.ScoreOf(Team.SpaceMarines), won.ScoreOf(Team.Chaos)));
    }

    [Fact]
    public void Warm_up_lasts_until_both_sides_have_a_player_and_a_side_that_empties_loses()
    {
        var w = Arena(Fragile, Rules, P(SmTac, 0, 0, DartChoice));
        w.Step(Idle);
        Assert.Equal(MatchState.Warmup, w.State);
        FireDart(w, -1.4f);                                                   // a death in warm-up: fed, not scored
        Assert.Single(w.RecentKills);
        Assert.Equal((0, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        w.Spawn(2, CsmTac);
        Assert.Equal(MatchState.Warmup, w.State);
        w.Step(Idle);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(new PlayerScore(0, 0), w.Scores[1]);                    // the round starts from zero
        w.Remove(2);
        w.Step(Idle);
        Assert.Equal((MatchState.Ended, Team.SpaceMarines), (w.State, w.Winner));
        while (w.State == MatchState.Ended) w.Step(Idle);
        Assert.Equal(MatchState.Warmup, w.State);                            // back to waiting for a second side
        Assert.Equal(MatchState.None, new World(Config).State);              // no rules: no match
    }

    // ---------------------------------------------------------------- spawns and respawn

    [Fact]
    public void Players_spawn_and_respawn_at_the_free_point_farthest_from_their_enemies_after_the_respawn_time()
    {
        SpawnPoint[] points = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(10, 0, 0), 0f), new(new Vector3(50, 0, 0), 0f), new(new Vector3(-20, 0, 0), 0f)];
        var w = new World(Fragile, null, points) { Rules = Rules };
        Assert.Equal(points[0].Position, w.Spawn(1, SmTac).Position);       // no enemy yet: the rotation
        Assert.Equal(points[2].Position, w.Spawn(2, CsmTac).Position);      // farthest from player 1
        Assert.Equal(points[3].Position, w.Spawn(3, SmTac).Position);       // farthest from player 2 (70 m; 10 m is 40 m)
        w.Step(Idle);
        ShootDead(w, 2, yaw: -MathF.PI / 2f);                                // +X
        Assert.Equal(Fragile.RespawnTime, w.Players[2].RespawnLeft, 4);
        int ticks = 0;
        while (!w.Players[2].Alive) { w.Step(Idle); ticks++; }
        output.WriteLine($"respawned after {ticks} ticks");
        Assert.InRange(ticks, (int)(Fragile.RespawnTime * Fragile.TickRate) - 1, (int)(Fragile.RespawnTime * Fragile.TickRate) + 1);
        Assert.Equal(points[2].Position, w.Players[2].Position);             // 10 m from player 1 or 50: 50
        Assert.Equal(Team.Chaos, w.Players[2].Team);                          // kept across respawns
        // Without rules, the rotation as before.
        var ffa = new World(Fragile, null, points);
        Assert.Equal([points[0].Position, points[1].Position, points[2].Position], new[] { ffa.Spawn(1, SmTac), ffa.Spawn(2, CsmTac), ffa.Spawn(3, SmTac) }.Select(p => p.Position));
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public void Clients_get_the_match_the_kill_feed_once_and_the_scoreboard()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5), 0.1);
        var h = new Harness(link, 11, 0, Fragile, new ServerOptions { Match = Rules });
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var b = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = CsmTac });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(MatchState.Playing, a.Match.State);
        var world = h.Server.World;
        var from = world.Players[a.PlayerId].Position; var to = world.Players[b.PlayerId].Position;
        float yaw = MathF.Atan2(-(to.X - from.X), to.Y - from.Y);            // forward = (−sin yaw, cos yaw)
        h.Run(TimeSpan.FromSeconds(2), (i, t) => new PlayerInput(t, 0, 0, i == 0 ? yaw : 0f, i == 0 && t % 2 == 0 && world.ScoreOf(Team.SpaceMarines) == 0 ? InputButtons.Fire : InputButtons.None));
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(1, world.ScoreOf(Team.SpaceMarines));
        foreach (var c in new[] { a, b })
        {
            var kill = Assert.Single(c.TakeKills());                          // repeated in several snapshots, handed out once
            Assert.Equal((a.PlayerId, b.PlayerId, KillCause.Weapon, 0), (kill.Killer, kill.Victim, kill.Cause, kill.Index));
            Assert.Empty(c.TakeKills());
            Assert.Equal((MatchState.Playing, 1, 0), (c.Match.State, c.Match.MarineScore, c.Match.ChaosScore));
            Assert.Equal(uint.MaxValue, c.Match.TicksLeft);
            var board = Assert.IsType<Scoreboard>(c.Scoreboard);
            Assert.True(board.Teams && !board.FriendlyFire && board.ScoreLimit == Rules.ScoreLimit);
            Assert.Equal([new ScoreEntry(a.PlayerId, Team.SpaceMarines, SmTac, 1, 0, 0, Loadouts.EquipmentIndex(Fragile, SmTac)), new ScoreEntry(b.PlayerId, Team.Chaos, CsmTac, 0, 1, 0, Loadouts.EquipmentIndex(Fragile, CsmTac))],
                board.Players.Select(p => p with { PingMs = -1 }));   // protocol 40: the equipment (protocol 50's ping: ScoreboardPingTests)
            Assert.All(board.Players, p => Assert.InRange(p.PingMs, 30, 120));   // 20 ms each way, jitter and loss: a measured ping
        }
    }

    [Fact]
    public void Prediction_stays_exact_with_teams()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, 0);
        var h = new Harness(link, 3, 0, Config, new ServerOptions { Match = Rules });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = SmJump });
        h.Run(TimeSpan.FromSeconds(6), Harness.Scripted);
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        foreach (var c in h.Clients)
        {
            var e = c.CorrectionErrors;
            output.WriteLine($"player {c.PlayerId} ({c.Predicted.Team}): {e.Count} corrections, max {e.Max():F6} m");
            Assert.True(e.Count > 100);
            Assert.True(e.Max() < 1e-4f, $"max correction {e.Max()}");
            Assert.Equal(h.Server.World.Players[c.PlayerId].Team, c.Predicted.Team);
        }
    }

    // ---------------------------------------------------------------- protocol 10

    [Fact]
    public void Protocol_10_carries_teams_the_match_and_the_kill_feed_within_the_budget()
    {
        Assert.Equal(53, Protocol.Version);
        Assert.Equal(1199, Protocol.MaxSnapshotBytes);   // protocol 23: + the 2-byte perk word; protocol 21: + 1 ready-tick byte; protocol 11: + the point count and 8 points; protocol 12: + the hit block; protocol 13: + the evade bytes − 1 projectile record; 16: − 3 projectile records + 5 shot records; 17: + 4 reaction bytes − 1 shot record
        Assert.True(Protocol.MaxSnapshotBytes <= 1200);   // protocol 12: 13 bytes of headroom; protocol 13: 9; protocol 15: 5; protocol 16: 1 (more shot records fill smaller snapshots); protocol 23: 0
        var hello = new Hello(Protocol.Version, 1, 2, 3, 4, 5, 6, 2, 1, 0, 7, 1, (byte)Team.Chaos);
        Assert.Equal(hello, Protocol.Read(Protocol.Write(hello)));
        Assert.Equal(0, new Hello(Protocol.Version, 1, 2, 3).RequestedSide);

        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, Class = 5, Team = Team.Chaos };
        var other = me with { PlayerId = 4, Class = 63, Team = Team.SpaceMarines };
        var match = new MatchView(MatchState.Ended, Team.Chaos, 123456, 41, 65535);
        KillView[] kills = [new(65535, 3, 4, KillCause.JumpPack, 0), new(0, 0, 4, KillCause.World, -1), new(7, 4, 4, KillCause.Thrown, 254), new(8, 65535, 3, KillCause.Melee, 1)];
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 8, 1, [me, other], Self: 3, Match: match, Kills: kills))));
        Assert.Equal([me, Protocol.Slim(other)], back.Players);
        Assert.Equal((Team.Chaos, Team.SpaceMarines, 63), (back.Players[0].Team, back.Players[1].Team, back.Players[1].Class));
        Assert.Equal(match, back.Match);
        Assert.Equal(kills, back.Kills!);
        // Defaults: no match, no kills; a class above 63 is clamped; ids outside 0..65535 travel as 0; at most 4 kills.
        var plain = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 8, 1, [me with { Class = 200, Team = (Team)7 }], Self: 3))));
        Assert.Equal((default(MatchView), 0), (plain.Match!.Value, plain.Kills!.Length));
        Assert.Equal((63, Team.None), (plain.Players[0].Class, plain.Players[0].Team));
        var many = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 8, 1, [], Kills: [.. Enumerable.Range(1, 9).Select(k => new KillView(k, 70000, k, KillCause.Weapon, 0))]))));
        Assert.Equal([1, 2, 3, 4], many.Kills!.Select(k => k.Sequence));
        Assert.All(many.Kills!, k => Assert.Equal(0, k.Killer));

        // Refusals: an unknown team in the class byte, unknown match bits or winner, too many kills, an unknown cause, a repeat.
        var bytes = Protocol.Write(new Snapshot(9, 8, 1, [other], Match: match, Kills: kills[..1]));
        int classByte = 13 + 1 + 1 + 4 + 12 + 12 + 4 + 4 + 1;
        Assert.Equal(63 | (1 << 6), bytes[classByte]);
        Assert.Null(Protocol.Read(Patch(bytes, classByte, 63 | (3 << 6))));
        int state = bytes.Length - 1 - Protocol.HitBlockBytes - 1 - 9 - 1 - 9;  // match block, the kill count and one 9-byte entry (protocol 15), the point count, the hit block, the shot count (16)
        Assert.Equal((int)MatchState.Ended | ((int)Team.Chaos << 2), bytes[state]);
        Assert.NotNull(Protocol.Read(Patch(bytes, state, 2 | (1 << 2))));    // control
        Assert.Null(Protocol.Read(Patch(bytes, state, 3 | (3 << 2))));       // winner 3
        Assert.NotNull(Protocol.Read(Patch(bytes, state, 16)));              // mode 1 (Seize Ground, protocol 11)
        Assert.Null(Protocol.Read(Patch(bytes, state, 32)));                 // unknown bits (mode 2)
        Assert.Null(Protocol.Read(Patch(bytes, bytes.Length - 1 - Protocol.HitBlockBytes - 11, 5)));   // count 5 (the records do not follow either)
        Assert.Null(Protocol.Read(Patch(bytes, bytes.Length - 1 - Protocol.HitBlockBytes - 4, 6)));    // cause 6 (5 is a perk kill since protocol 23)
        var two = Protocol.Write(new Snapshot(9, 8, 1, [], Kills: [kills[0], kills[0] with { Victim = 1 }]));
        Assert.Null(Protocol.Read(two));
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
    }

    static byte[] Patch(byte[] bytes, int at, int value) { var copy = (byte[])bytes.Clone(); copy[at] = (byte)value; return copy; }

    // ---------------------------------------------------------------- M23: death screen

    [Fact]
    public void A_dead_player_ignores_every_input_and_stays_where_it_died_until_it_respawns()
    {
        var w = Arena(Fragile, Rules, P(SmTac, 0, 0), P(CsmTac, 0, 5), P(CsmTac, 3, 3));
        w.Step(Idle);
        ShootDead(w, 2);
        var dead = w.Players[2];
        Assert.Equal((0f, Fragile.RespawnTime), (dead.Health, dead.RespawnLeft));
        int ticks = 0;
        // Every button (press edges every other tick), full stick, a turning view and pitch: nothing moves, turns, fires or throws.
        for (int k = 0; w.Players[2] is { Alive: false } now; k++, ticks++)
        {
            Assert.Equal((dead.Position, dead.Yaw, dead.Pitch, dead.Velocity), (now.Position, now.Yaw, now.Pitch, now.Velocity));
            Assert.Equal((dead.Slot, dead.Clip, dead.Reserve, dead.Grenades, dead.Health), (now.Slot, now.Clip, now.Reserve, now.Grenades, now.Health));
            Assert.Equal((0, 0f, false, JumpPhase.None, 0), (now.MeleeStep, now.ChargeTime, now.Aiming, now.Jump, now.EvadeTicks));
            Assert.Equal(Math.Max(0f, Fragile.RespawnTime - ticks * Fragile.TickSeconds), now.RespawnLeft, 4);
            var buttons = k % 2 == 0 ? (InputButtons)0x7FF : InputButtons.None;
            w.Step(id => id == 2 ? new PlayerInput(0, 1f, 1f, 0.5f * k, buttons, 0.4f) : Idle(id));
            Assert.DoesNotContain(w.LastShots, s => s.Shooter == 2);
            Assert.DoesNotContain(w.LastHits, h => h.Shooter == 2);
            Assert.DoesNotContain(w.Projectiles, p => p.Owner == 2);
            Assert.True(ticks < 100, "never respawned");
        }
        // RespawnTime of ticks, then the respawn (one tick later when the float countdown stays a hair above 0, as here: 16 for 15)
        Assert.InRange(ticks, (int)MathF.Round(Fragile.RespawnTime * Fragile.TickRate), (int)MathF.Round(Fragile.RespawnTime * Fragile.TickRate) + 1);
        Assert.True(w.Players[2].Alive);
        output.WriteLine($"dead for {ticks} ticks at {dead.Position}");
    }

    [Fact]
    public void A_kill_carries_the_killing_hits_direction_a_shot_along_its_ray_melee_from_the_attacker_a_blast_from_its_point()
    {
        var shot = Arena(Fragile, Rules, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        shot.Step(Idle);
        ShootDead(shot, 2);
        var kill = Assert.Single(shot.LastKills);
        Assert.True(Vector3.Distance(new Vector3(0, 1, 0), kill.Direction) < 1e-4f, $"{kill.Direction}");
        Assert.Equal(kill.Direction, shot.LastHits.Last(h => h.Killed).Direction);
        // Turned 90° left (yaw +π/2 faces −X): the push follows the ray.
        var left = Arena(Fragile, Rules, P(SmTac, 0, 0), P(CsmTac, -5, 0));
        left.Step(Idle);
        ShootDead(left, 2, MathF.PI / 2);
        Assert.True(Vector3.Distance(new Vector3(-1, 0, 0), Assert.Single(left.LastKills).Direction) < 1e-4f);

        var melee = Arena(Fragile with { HealthMax = 20f }, Rules, P(SmTac, 0, 0), P(CsmTac, 1f, 1.5f));
        melee.Step(Idle);
        for (int k = 0; k < 60 && melee.Players[2].Alive; k++) melee.Step(Only1(k == 0 ? InputButtons.Melee : InputButtons.None));
        var cut = Assert.Single(melee.RecentKills);
        Assert.Equal(KillCause.Melee, cut.Cause);
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(1f, 1.5f, 0)), cut.Direction) < 1e-4f, $"{cut.Direction}");   // level, attacker to victim

        var blast = Arena(Fragile, Rules, P(SmTac, 0, 0, DartChoice), P(CsmTac, 0, 20));
        blast.Step(Idle);
        FireDart(blast, -1.4f);                                               // at its own feet
        var own = blast.RecentKills.Single();
        Assert.Equal((1, 1), (own.Killer, own.Victim));
        Assert.True(own.Direction.Z > 0.5f && MathF.Abs(own.Direction.Length() - 1f) < 1e-4f, $"{own.Direction}");   // from the blast below the chest: upward
    }

    [Fact]
    public void Protocol_15_carries_the_kill_direction_in_one_more_byte_and_quantizes_it()
    {
        var dir = Vector3.Normalize(new Vector3(0.6f, -0.7f, 0.3f));
        KillView[] kills = [new(5, 1, 2, KillCause.Weapon, 0, dir), new(6, 0, 3, KillCause.World, -1), new(7, 2, 2, KillCause.Thrown, 0, -Vector3.UnitZ),
                            new(8, 1, 4, KillCause.JumpPack, 0, Vector3.UnitZ)];
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 8, 1, [], Kills: kills)))).Kills!;
        Assert.Equal(kills.Select(Protocol.Quantize), back);
        Assert.True(Vector3.Dot(dir, back[0].Direction) > MathF.Cos(4f * MathF.PI / 180f), $"{back[0].Direction}");   // within 4°: 1.4° yaw, 3° pitch steps
        Assert.Equal((Vector3.Zero, false, true), (back[1].Direction, back[1].HasDirection, back[0].HasDirection));
        Assert.True(Vector3.Distance(-Vector3.UnitZ, back[2].Direction) < 1e-5f && Vector3.Distance(Vector3.UnitZ, back[3].Direction) < 1e-5f);
        Assert.Equal(Protocol.Quantize(back[0]), back[0]);                    // a received entry is already quantized
        // Re-quantizing is stable for every direction, vertical ones too (pitch codes 1 and 31 travel with yaw 0, decode exactly vertical).
        var rng = new Random(23);
        Vector3[] probes = [Vector3.UnitZ, -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.01f, 0.02f, 1f)), Vector3.Normalize(new Vector3(-0.03f, 0.01f, -1f)),
            .. Enumerable.Range(0, 200).Select(_ => new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f))];
        foreach (var p in probes)
        {
            var once = Protocol.Quantize(new KillView(1, 1, 2, KillCause.Weapon, 0, p));
            Assert.Equal(once, Protocol.Quantize(once));
            var wire = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 8, 1, [], Kills: [once])))).Kills!.Single();
            Assert.Equal(once, wire);
        }
        Assert.Equal((Vector3.UnitZ, -Vector3.UnitZ), (Protocol.Quantize(new KillView(1, 1, 2, KillCause.Weapon, 0, probes[2])).Direction,
            Protocol.Quantize(new KillView(1, 1, 2, KillCause.Weapon, 0, probes[3])).Direction));   // within 3° of vertical: exactly vertical
        Assert.Equal(Vector3.Zero, Protocol.Quantize(new KillView(1, 1, 1, KillCause.Weapon, 0, new Vector3(float.NaN, 0, 0))).Direction);

        // A direction-less entry is all zeros in its direction bits; a yaw without a pitch code is refused.
        var bytes = Protocol.Write(new Snapshot(9, 8, 1, [], Kills: [kills[1]]));
        int yaw = bytes.Length - 1 - Protocol.HitBlockBytes - 1 - 1, cause = yaw - 2;   // (the shot count ends the snapshot since protocol 16)
        Assert.Equal((0, (int)KillCause.World), (bytes[yaw], bytes[cause]));
        Assert.Null(Protocol.Read(Patch(bytes, yaw, 1)));
        Assert.NotNull(Protocol.Read(Patch(bytes, cause, (int)KillCause.World | (16 << 3))));   // a level push straight ahead (+Y)

        // The server passes the world's kill direction to the feed.
        var w = Arena(Fragile, Rules, P(SmTac, 0, 0), P(CsmTac, 0, 5));
        w.Step(Idle);
        ShootDead(w, 2);
        var view = Assert.Single(GameServer.KillsFor(w, 0));
        Assert.Equal(w.KillLog.Single().Direction, view.Direction);
    }

    [Fact]
    public void The_scoreboard_round_trips_and_malformed_ones_are_refused()
    {
        var board = new Scoreboard(77, true, 41, 600f, false, [new ScoreEntry(1, Team.SpaceMarines, 1, 3, 2), new ScoreEntry(70000, Team.Chaos, 63, 65535, 0)]);
        var bytes = Protocol.Write(board);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(bytes));
        Assert.Equal((board.ServerTick, board.Teams, board.ScoreLimit, board.TimeLimit, board.FriendlyFire), (back.ServerTick, back.Teams, back.ScoreLimit, back.TimeLimit, back.FriendlyFire));
        Assert.Equal(board.Players, back.Players);
        Assert.Equal(1 + 4 + 1 + 2 + 4 + 1 + 2 * 16, bytes.Length);   // protocol 22: 12-byte rows (appearance version); protocol 40: 13 (equipment); protocol 41: 14 (an empty name); protocol 50: 16 (the ping)
        Assert.Null(Protocol.Read(Patch(bytes, 5, 8)));                       // unknown flag
        Assert.Null(Protocol.Read(Patch(bytes, 5, 4)));                       // Seize Ground flag without its block
        Assert.Null(Protocol.Read(Patch(bytes, 13 + 4, 3)));                  // team 3
        Assert.Null(Protocol.Read(Patch(bytes, 13 + 5, 64)));                 // class 64
        Assert.Null(Protocol.Read(Patch(bytes, 12, 17)));                     // 17 rows
        Assert.Null(Protocol.Read(Protocol.Write(board with { Players = [board.Players[0], board.Players[0]] })));   // a player twice
        var nan = Protocol.Write(board with { TimeLimit = float.NaN });
        Assert.Equal(0f, Assert.IsType<Scoreboard>(Protocol.Read(nan)).TimeLimit);   // written as 0
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
    }

    [Fact]
    public void The_feed_pages_through_unacknowledged_kills_oldest_first()
    {
        var w = Arena(Fragile, Rules, P(SmTac, 0, 0, DartChoice), P(SmTac, 3, -3, DartChoice), P(SmTac, -3, -3, DartChoice), P(SmTac, 6, -6, DartChoice), P(SmTac, -6, -6, DartChoice));
        // Five suicides in one tick (each at its own feet).
        w.Step(id => new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, -1.4f));
        for (int k = 0; k < 10 && w.Projectiles.Count > 0; k++) w.Step(Idle);
        Assert.Equal(5, w.KillLog.Count);
        Assert.Equal([1, 2, 3, 4], GameServer.KillsFor(w, 0).Select(k => k.Sequence));
        Assert.Equal([5], GameServer.KillsFor(w, 4).Select(k => k.Sequence));
        Assert.Empty(GameServer.KillsFor(w, 5));
        Assert.Equal(5, w.LastKillSequence);
        Assert.Equal(GameServer.MatchOf(w), new MatchView(MatchState.Warmup, Team.None, 0, 0, 0));
    }

    [Fact]
    public void Every_death_reaches_every_client_even_when_many_happen_at_once()
    {
        var config = Fragile with { RespawnTime = 60f };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5), 0.1);
        var h = new Harness(link, 21, 0, config);
        for (int i = 0; i < 7; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = SmTac, RequestedPrimary = DartChoice });
        var world = h.Server.World;
        h.Run(TimeSpan.FromSeconds(1));
        uint start = world.Tick + 10;
        // Clients 1..6 fire at their feet from the same server tick on; client 0 watches.
        h.Run(TimeSpan.FromSeconds(4), (i, t) => new PlayerInput(t, 0, 0, 0f, i > 0 && world.Tick >= start && t % 2 == 0 ? InputButtons.Fire : InputButtons.None, -1.4f));
        var deaths = world.KillLog;
        int burst = deaths.Max(d => deaths.Count(e => e.Tick >= d.Tick && e.Tick - d.Tick < World.KillMemoryTicks));
        output.WriteLine($"{deaths.Count} deaths; at most {burst} within {World.KillMemoryTicks} ticks");
        Assert.True(deaths.Count >= 6, $"{deaths.Count}");
        Assert.True(burst > Protocol.MaxSnapshotKills, $"{burst}");   // more than one snapshot can carry
        foreach (var c in h.Clients)
        {
            var got = c.TakeKills();
            Assert.Equal(deaths.Select(d => (d.Sequence & 0xFFFF, d.Killer, d.Victim)), got.Select(k => (k.Sequence, k.Killer, k.Victim)));
        }
    }

    [Fact]
    public void A_simultaneous_reach_of_the_score_limit_goes_to_the_higher_score_or_is_a_draw()
    {
        var tie = Arena(Fragile, Rules with { ScoreLimit = 1 }, P(SmTac, 0, 0, DartChoice), P(CsmTac, 0, 20, DartChoice));
        tie.Step(Idle);
        FireAllAtFeet(tie);
        Assert.Equal((MatchState.Ended, Team.None, 1, 1), (tie.State, tie.Winner, tie.ScoreOf(Team.SpaceMarines), tie.ScoreOf(Team.Chaos)));
        var won = Arena(Fragile, Rules with { ScoreLimit = 1 }, P(SmTac, 0, 0, DartChoice), P(CsmTac, 20, 20, DartChoice), P(CsmTac, 0, 20, DartChoice));
        won.Step(Idle);
        FireAllAtFeet(won);
        Assert.Equal((MatchState.Ended, Team.SpaceMarines, 2, 1), (won.State, won.Winner, won.ScoreOf(Team.SpaceMarines), won.ScoreOf(Team.Chaos)));
    }

    static void FireAllAtFeet(World w)
    {
        w.Step(id => new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, -1.4f));
        for (int k = 0; k < 10 && w.Projectiles.Count > 0; k++) w.Step(Idle);
        Assert.All(w.Players.Values, p => Assert.False(p.Alive));
    }

    [Fact]
    public void Lingering_blasts_spare_allies_and_keep_hurting_enemies()
    {
        var w = Arena(Config, Rules, P(SmTac, 0, 0, LingerChoice), P(SmTac, 0, 5), P(CsmTac, 2, 5.5f));
        w.Step(Only1(InputButtons.Fire));
        for (int k = 0; k < 30; k++) w.Step(Idle);
        Assert.NotEmpty(w.Areas);
        Assert.Equal(1000f, Health(w, 2));
        Assert.True(Health(w, 3) <= 1000f - 3 * 75f, $"{Health(w, 3)}");     // the detonation and repeats
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmTac, 0, 0, LingerChoice), P(SmTac, 0, 5), P(CsmTac, 2, 5.5f));
        ff.Step(Only1(InputButtons.Fire));
        for (int k = 0; k < 30; k++) ff.Step(Idle);
        Assert.True(Health(ff, 2) < 1000f - 75f);
    }

    [Fact]
    public void A_sticky_projectile_on_an_ally_does_not_hurt_it_but_its_blast_hits_enemies()
    {
        var w = Arena(Config, Rules, P(SmTac, 0, 0, StickyChoice), P(SmTac, 0, 5), P(CsmTac, 2, 5.5f));
        w.Step(Only1(InputButtons.Fire));
        for (int k = 0; k < 10; k++) w.Step(Idle);
        var stuck = Assert.Single(w.Projectiles);
        Assert.Equal((ProjectileMotion.Attached, 2), (stuck.Motion, stuck.AttachedTo));
        for (int k = 0; k < 40 && w.Projectiles.Count > 0; k++) w.Step(Idle);
        Assert.Empty(w.Projectiles);
        Assert.Equal((1000f, 925f), (Health(w, 2), Health(w, 3)));
    }

    [Fact]
    public void Thrown_grenades_spare_allies_and_hit_enemies()
    {
        static List<HitEvent> Throw(World w)
        {
            var hits = new List<HitEvent>();
            for (int k = 0; k < 200 && w.LastDetonations.Count == 0; k++) { w.Step(Only1(k == 0 ? InputButtons.Grenade : InputButtons.None, -1.4f)); hits.AddRange(w.LastHits); }
            Assert.NotEmpty(w.LastDetonations);
            return hits;
        }
        var w = Arena(Config, Rules, P(SmTac, 0, 0), P(SmTac, 3, 0), P(CsmTac, -3, 0));
        var hits = Throw(w);
        Assert.Equal(1000f, Health(w, 2));
        Assert.True(Health(w, 3) < 1000f, $"{Health(w, 3)}");
        Assert.Contains(hits, h => h.Target == 3 && h.ThrownItem && h.Weapon == 0);
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmTac, 0, 0), P(SmTac, 3, 0), P(CsmTac, -3, 0));
        Throw(ff);
        Assert.True(Health(ff, 2) < 1000f);
    }

    [Fact]
    public void A_regular_landing_blast_spares_allies_and_hits_enemies()
    {
        static void Hop(World w)
        {
            for (int k = 0; k < 300; k++)
            {
                w.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, k == 0 ? InputButtons.Jump : InputButtons.None) : Idle(id));
                if (w.LastJumps.Any(j => j.Kind == JumpEventKind.Land)) return;
            }
            throw new Xunit.Sdk.XunitException("no landing");
        }
        var w = Arena(Config, Rules, P(SmJump, 0, 0), P(SmTac, 2.5f, 0), P(CsmTac, -2.5f, 0));
        Hop(w);
        Assert.Equal((1000f, 995f), (Health(w, 2), Health(w, 3)));            // r 4, 5 damage below a 15 m drop
        var ff = Arena(Config, Rules with { FriendlyFire = true }, P(SmJump, 0, 0), P(SmTac, 2.5f, 0), P(CsmTac, -2.5f, 0));
        Hop(ff);
        Assert.Equal(995f, Health(ff, 2));
    }

    [Fact]
    public void After_the_end_melee_and_self_damage_deal_nothing()
    {
        var w = Arena(Fragile, Rules with { ScoreLimit = 1, RestartDelay = 10f }, P(SmTac, 0, 0, DartChoice), P(CsmTac, 0, 2.2f));
        w.Step(Idle);
        FireDart(w);                                                          // kills the enemy (and its owner, 2.2 m away)
        Assert.Equal(MatchState.Ended, w.State);
        while (!w.Players[1].Alive || !w.Players[2].Alive) w.Step(Idle);
        Assert.Equal(new Vector3(0, 2.2f, 0), w.Players[2].Position);
        var hits = new List<HitEvent>();
        for (int k = 0; k < 60; k++) { w.Step(Only1(k == 0 ? InputButtons.Melee : InputButtons.None)); hits.AddRange(w.LastHits); }
        Assert.Empty(hits);
        Assert.Equal(40f, Health(w, 2));
        FireDart(w, -1.4f);                                                   // at its own feet
        Assert.Equal((true, 40f), (w.Players[1].Alive, Health(w, 1)));
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal((new PlayerScore(1, 1), new PlayerScore(0, 1)), (w.Scores[1], w.Scores[2]));   // unchanged since the end
    }

    [Fact]
    public void The_restart_clears_projectiles_lingering_blasts_and_jumps_and_spawns_everyone_apart()
    {
        var w = Arena(Fragile, Rules with { ScoreLimit = 1, RestartDelay = 1f },
            P(SmTac, 0, 0, LingerChoice), P(SmTac, -10, 0, StickyChoice), P(SmJump, -10, -10), P(CsmTac, 0, 5));
        w.Step(Idle);
        FireDart(w);
        Assert.Equal(MatchState.Ended, w.State);
        bool sawAll = false;
        for (int k = 0; w.State == MatchState.Ended; k++)
        {
            w.Step(id => id == 2 && k == 0 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, -1.4f)
                : id == 3 && k == 0 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Jump) : Idle(id));
            if (w.State == MatchState.Ended)
                sawAll = w.Projectiles.Count > 0 && w.Areas.Count > 0 && w.Players[3].Jump != JumpPhase.None;
        }
        Assert.True(sawAll, "the last ended tick had a projectile, a lingering blast and a jump");
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Empty(w.Projectiles);
        Assert.Empty(w.Areas);
        Assert.All(w.Players.Values, p => Assert.Equal((true, JumpPhase.None, 0), (p.Alive, p.Jump, p.ProjectilesOut)));
        Assert.Equal(4, w.Players.Values.Select(p => p.Position).Distinct().Count());
    }

    [Fact]
    public void A_fall_out_of_the_level_is_a_death_without_a_killer_that_scores_for_the_other_side()
    {
        var v = new[] { new Vector3(-2, -2, 0), new Vector3(2, -2, 0), new Vector3(2, 2, 0), new Vector3(-2, 2, 0) };
        var floor = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var w = new World(Fragile, new CollisionWorld(floor, floor), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(1.5f, -1.5f, 0), 0f)]) { Rules = Rules };
        w.Spawn(1, SmTac); w.Spawn(2, CsmTac);
        w.Step(Idle);
        for (int k = 0; k < 400 && w.Players[1].Alive; k++) w.Step(id => id == 1 ? new PlayerInput(0, 0, 1, 0f, InputButtons.None) : Idle(id));
        Assert.False(w.Players[1].Alive);
        var kill = Assert.Single(w.LastKills);
        Assert.Equal((0, 1, KillCause.World, -1), (kill.Killer, kill.Victim, kill.Cause, kill.Index));
        Assert.Equal((0, 1), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal(new PlayerScore(0, 1), w.Scores[1]);
    }

    [Fact]
    public void A_player_leaving_mid_match_leaves_the_scoreboard_and_empties_its_side()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 9, 0, Config, new ServerOptions { Match = Rules });
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var b = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = CsmTac });
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, a.Scoreboard!.Players.Length);
        b.Leave();
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal([a.PlayerId], a.Scoreboard!.Players.Select(p => p.PlayerId));
        Assert.Equal((MatchState.Ended, Team.SpaceMarines), (a.Match.State, a.Match.Winner));
    }

    [Fact]
    public void Player_ids_wrap_at_65535_and_skip_ids_in_use()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 4, 0, Config);
        h.Server.NextPlayerIdForTests = 65534;
        GameClient Join(uint nonce) { var c = h.AddClient(nonce: nonce); h.Run(TimeSpan.FromMilliseconds(200)); return c; }
        Assert.Equal([65534, 65535, 1], new[] { Join(100), Join(101), Join(102) }.Select(c => c.PlayerId));
        h.Server.NextPlayerIdForTests = 65535;
        Assert.Equal(2, Join(103).PlayerId);                                  // 65535 and 1 are taken
        h.Server.NextPlayerIdForTests = 0;
        Assert.Equal(3, Join(104).PlayerId);                                  // out of range: from 1
    }

    [Fact]
    public void The_content_loader_refuses_more_classes_than_snapshots_can_name()
    {
        Assert.Equal(64, SimConfig.MaxClasses);
        Assert.Equal(SimConfig.MaxClasses, Protocol.MaxClasses);
        Assert.Equal(SimConfig.MaxClasses, SmContent.GameplayTuning.MaxClasses);
        ClassLoadout C(int i) => new($"c{i}", 0, -1, []);
        Assert.NotNull(SmContent.GameplayTuning.WithClasses(new SimConfig(), [Rifle], Enumerable.Range(0, 64).Select(C).ToArray()));
        Assert.Null(SmContent.GameplayTuning.WithClasses(new SimConfig(), [Rifle], Enumerable.Range(0, 65).Select(C).ToArray()));
        Assert.Null(SmContent.GameplayTuning.WithClasses(new SimConfig(), [Rifle], [C(0) with { Side = (Team)7 }]));
    }
    [Fact]
    public void The_config_hash_covers_class_sides_and_roles()
    {
        ulong h = Config.Hash();
        Assert.Equal(h, (Config with { Classes = Config.Classes.Select(c => c with { }).ToList() }).Hash());
        var sides = Config.Classes.ToList(); sides[0] = sides[0] with { Side = Team.Chaos };
        var roles = Config.Classes.ToList(); roles[0] = roles[0] with { Role = "heavy" };
        Assert.NotEqual(h, (Config with { Classes = sides }).Hash());
        Assert.NotEqual(h, (Config with { Classes = roles }).Hash());
    }
}
