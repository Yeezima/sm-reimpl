using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M29: the charge (bull rush): the start gate (W + F while running), the rush's motion, connecting with an enemy (damage and
/// stun), missing, a wall stop, an ally stopping it without harm, hyper armour negating the stun, the collision stop's
/// branches, prediction (own record replay and a 150 ms / 5% loss run), and protocol 18. Synthetic clips: the rush moves
/// 10 m/s for 1.2 s (12 m), connects until 0.6 s, meets walls from 0.3 s. M32: the charger's own hyper armour for the rush's
/// first 0.3 s (half its attack part up to 0.6 s): a stun then is shrugged off (damage taken, the charge runs on), later it ends it.
/// </summary>
public class RushTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];

    static readonly RushMove RushClip = new("SM_BUL_Hit_M_5m", Line(12f, 12), 1.2f, 1.0f, 0.6f, 0.5f, 1.0f);
    static readonly RushMove StopClip = new("SM_NAV_Bullrush2Idle_F_Collision_01", Line(0.5f, 10), 1.0f, 0.9f, 0.9f, 0.6f, 0.3f);
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    internal static readonly RushSpec Spec = new("ability_bullrush_01", RushClip, StopClip, Plain with { Damage = 75f, ShieldDamage = 25f }, new ReactionEntry(0f, ReactionKind.Stun),
        Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f) { ContactUntil = 0.6f, WallFrom = 0.3f, HyperArmourUntil = 0.3f };

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };
    const int Marine = 0, Chaos = 1;

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, [Light]) { MeleeHeavy = [Heavy], MeleeRush = Spec, Evade = 0, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, -1, [Light]) { MeleeHeavy = [Heavy], MeleeRush = Spec, Evade = 0, Side = Team.Chaos },
        ],
        Evades = [EvadeTests.Roll with { TargetableModeTime = 0.3f, CollisionModeTime = 0.2f }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static World Arena(SimConfig c, CollisionWorld? collision, bool teams, params (int Class, Vector3 At)[] players)
    {
        var world = new World(c, collision, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = teams ? new MatchRules { TimeLimit = 0f } : null };
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class);
        return world;
    }

    /// <summary>
    /// Player 1 sprints forward (+Y) from tick 0 and presses F at <paramref name="pressAt"/> (with W held, as the charge needs since protocol 39; the sprint itself is no longer needed); the
    /// others stand facing −Y. Every input views the current tick (no lag-compensation rewind).
    /// </summary>
    static void Charge(World w, int ticks, int pressAt, Action<int>? each = null, Func<int, int, PlayerInput?>? other = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = w.Tick;
            w.Step(id => (other?.Invoke(tick, id) ?? (id == 1
                ? new PlayerInput(0, 0f, tick <= pressAt ? 1f : 0f, 0f, (tick <= pressAt ? InputButtons.Sprint : InputButtons.None) | (tick == pressAt ? InputButtons.MeleeHeavy : InputButtons.None))
                : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None))) with { ViewTick = view });
            each?.Invoke(tick);
        }
    }

    // ---------------------------------------------------------------- start

    [Fact]
    public void F_with_W_held_starts_the_rush_from_rest_or_a_run_and_F_without_W_is_the_heavy_strike()
    {
        // feel/charge-directions (protocol 39; the owner's rule of 2026-10-29, superseding M31's sprint gate): W held at the F press is enough.
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, Config);
        var standing = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.MeleeHeavy), Config, out _);
        Assert.Equal((RushPhase.None, 1, true), (standing.Rush, standing.MeleeStep, standing.MeleeHeavy));   // no W: the heavy strike
        var back = Combat.StepPlayer(s, new PlayerInput(0, 0f, -1f, 0f, InputButtons.MeleeHeavy), Config, out _);
        Assert.Equal((RushPhase.None, 1), (back.Rush, back.MeleeStep));                                       // S: the heavy strike
        var side = Combat.StepPlayer(s, new PlayerInput(0, 1f, 0f, 0f, InputButtons.MeleeHeavy), Config, out _);
        Assert.Equal((RushPhase.None, 1), (side.Rush, side.MeleeStep));                                       // D alone: the heavy strike
        var weak = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0.5f, 0f, InputButtons.MeleeHeavy), Config, out _);
        Assert.Equal(RushPhase.None, weak.Rush);                                                              // a stick barely forward is not W
        var fromRest = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0.3f, InputButtons.MeleeHeavy), Config, out _);
        Assert.Equal((RushPhase.Rush, 1, 30, 0), (fromRest.Rush, fromRest.RushTicks, fromRest.RushLock, fromRest.MeleeStep));   // from rest
        var run = s;
        for (int k = 0; k < 20; k++) run = Combat.StepPlayer(run, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), Config, out _);
        Assert.Equal(Config.RunSpeed, run.Velocity.Y, 3);
        Assert.Equal(RushPhase.Rush, Combat.StepPlayer(run, new PlayerInput(0, 0f, 1f, 0f, InputButtons.MeleeHeavy), Config, out _).Rush);   // a run, no sprint
        // Aim mode and the held trigger no longer stop it (they took the sprint away under M31); the charge cancels aim mode.
        var aiming = Combat.StepPlayer(run, new PlayerInput(0, 0f, 1f, 0f, InputButtons.MeleeHeavy | InputButtons.Aim), Config, out _);
        Assert.Equal((RushPhase.Rush, false), (aiming.Rush, aiming.Aiming));
        Assert.Equal(RushPhase.Rush, Combat.StepPlayer(run, new PlayerInput(0, 0f, 1f, 0f, InputButtons.MeleeHeavy | InputButtons.Fire), Config, out _).Rush);
        var rush = Combat.StepPlayer(run, new PlayerInput(0, 0f, 1f, 0.3f, InputButtons.MeleeHeavy | InputButtons.Sprint), Config, out var e);
        Assert.Equal((RushPhase.Rush, 1, 30, 0, false), (rush.Rush, rush.RushTicks, rush.RushLock, rush.MeleeStep, e.Fired));
        Assert.Equal(0.3f, rush.Yaw, 5);
        // The body keeps its heading; the view turning does not turn it; the clip's motion moves it (10 m/s).
        var next = Combat.StepPlayer(rush, new PlayerInput(0, 0f, 0f, 2f, InputButtons.None), Config, out _);
        Assert.Equal(0.3f, next.Yaw, 5);
        var forward = new Vector2(-MathF.Sin(0.3f), MathF.Cos(0.3f));
        Assert.Equal(10f * Dt, Vector2.Dot(new Vector2(next.Position.X - rush.Position.X, next.Position.Y - rush.Position.Y), forward), 3);
        // Not in the air.
        var air = run with { Grounded = false, Position = run.Position with { Z = 2f } };
        Assert.False(Rush.CanStart(air, new PlayerInput(0, 0f, 1f, 0f, InputButtons.MeleeHeavy), Spec, Config));
    }

    // ---------------------------------------------------------------- the world

    [Fact]
    public void A_rush_that_connects_hits_and_stuns_the_enemy_and_stops_in_the_collision_clip()
    {
        var w = Arena(Config, null, false, (Marine, Vector3.Zero), (Chaos, new Vector3(0, 6, 0)), (Chaos, new Vector3(1.5f, 8f, 0)));
        var rushes = new List<RushEvent>();
        PlayerState target = default, other = default, rusher = default;
        Charge(w, 40, 10, _ =>
        {
            if (w.LastRushes.Count > 0) { target = w.Players[2]; other = w.Players[3]; rusher = w.Players[1]; }
            rushes.AddRange(w.LastRushes);
        });
        var e = Assert.Single(rushes);
        Assert.Equal((1, 2, true), (e.Player, e.Target, e.Harmed));
        Assert.Equal((100f - 25f - 75f, 1000f), (target.Shield, target.Health));   // the shield part first, then the damage (shield ×1)
        Assert.Equal(ReactionKind.Stun, target.Reaction);
        Assert.Equal((70f, ReactionKind.Interrupt), (other.Shield, other.Reaction));   // the second enemy in reach: the secondary hit (30 on the shield)
        Assert.Equal(RushPhase.Collision, rusher.Rush);
        float y = rusher.Position.Y;
        Assert.True(y < 6f, $"stopped short of the target ({y:F2})");
    }

    [Fact]
    public void A_rush_that_misses_runs_its_path_and_ends()
    {
        var w = Arena(Config, null, false, (Marine, Vector3.Zero), (Chaos, new Vector3(20, 0, 0)));
        float startY = 0f, lastY = 0f;
        var phases = new List<RushPhase>();
        Charge(w, 60, 10, k =>
        {
            if (k == 10) startY = w.Players[1].Position.Y;
            if (w.Players[1].Rush == RushPhase.Rush) lastY = w.Players[1].Position.Y;
            phases.Add(w.Players[1].Rush);
        });
        Assert.Empty(w.LastRushes);
        Assert.Equal(30, phases.Count(p => p == RushPhase.Rush));   // 1.0 s (its end branch)
        Assert.DoesNotContain(RushPhase.Collision, phases);
        Assert.Equal(RushPhase.None, w.Players[1].Rush);
        float travelled = lastY - startY;
        output.WriteLine($"travelled {travelled:F2} m in the rush, {w.Players[1].Position.Y - startY:F2} m in all");
        Assert.Equal(10f - 10f * Dt, travelled, 2);   // 10 m over its 30 ticks, the press tick's step already in startY
    }

    static CollisionWorld Walled(float wallY)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0));
        Quad(new(-50, wallY, -1), new(50, wallY, -1), new(50, wallY, 4), new(-50, wallY, 4));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Fact]
    public void A_wall_stops_the_rush_into_the_collision_clip()
    {
        var w = Arena(Config, Walled(8f), false, (Marine, Vector3.Zero));
        var phases = new List<RushPhase>();
        Charge(w, 40, 10, _ => phases.Add(w.Players[1].Rush));
        int first = phases.IndexOf(RushPhase.Collision);
        Assert.True(first > 0, "the wall stops the rush");
        Assert.True(w.Players[1].Position.Y + Config.CapsuleRadius <= 8f + 1e-3f);
        Assert.Empty(w.LastRushes);
        output.WriteLine($"collision from tick {first}, at y {w.Players[1].Position.Y:F2}");
    }

    [Fact]
    public void An_ally_stops_the_rush_without_harm_and_with_friendly_fire_it_is_hit()
    {
        foreach (bool friendlyFire in new[] { false, true })
        {
            var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 6, 0), 0f), new SpawnPoint(new Vector3(30, 0, 0), 0f)])
                { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = friendlyFire } };
            w.Spawn(1, Marine); w.Spawn(3, Chaos); w.Spawn(2, Marine);
            Assert.Equal(w.Players[1].Team, w.Players[2].Team);
            var rushes = new List<RushEvent>();
            Charge(w, 40, 10, _ => rushes.AddRange(w.LastRushes));
            var e = Assert.Single(rushes);
            Assert.Equal((2, friendlyFire), (e.Target, e.Harmed));
            Assert.Equal(RushPhase.Collision, w.Players[1].Rush is RushPhase.None ? RushPhase.Collision : w.Players[1].Rush);
            if (friendlyFire) Assert.Equal(0f, w.Players[2].Shield);
            else Assert.Equal((100f, ReactionKind.None), (w.Players[2].Shield, w.Players[2].Reaction));
        }
    }

    [Fact]
    public void Hyper_armour_negates_the_stun_but_not_the_damage()
    {
        // The target rolls toward the rush just before it arrives: it is hit within the roll's first 0.3 s.
        var w = Arena(Config with { MeleeVerticalReach = 5f }, null, false, (Marine, Vector3.Zero), (Chaos, new Vector3(0, 7, 0)));
        var rushes = new List<RushEvent>();
        var target = new List<PlayerState>();
        var rusher = new List<PlayerState>();
        Charge(w, 40, 10, k => { rushes.AddRange(w.LastRushes); target.Add(w.Players[2]); rusher.Add(w.Players[1]); },
            (k, id) => id == 2 && k == 11 ? new PlayerInput(0, 0f, 1f, MathF.PI, InputButtons.Jump) : null);   // rolls forward: toward the rush
        var e = Assert.Single(rushes);
        var hit = target[(int)(e.Tick - 1)];
        Assert.Equal((RushPhase.Collision, 0), (rusher[(int)(e.Tick - 1)].Rush, rusher[(int)(e.Tick - 1)].RushTicks));   // the contact started the stop
        output.WriteLine($"contact at tick {e.Tick}: evade ticks {hit.EvadeTicks}, reaction {hit.Reaction}");
        Assert.True(hit.EvadeTicks is > 0 and <= 9, "the roll's armour was on");
        Assert.Equal(ReactionKind.None, hit.Reaction);
        Assert.True(hit.Shield < 100f);
    }

    // ---------------------------------------------------------------- review additions (contact shape, walls, order, credit)

    [Fact]
    public void Only_a_player_ahead_is_reached_not_one_beside_or_behind()
    {
        var c = Config;
        var from = new Vector3(0, 2, 0); var to = new Vector3(0, 2.33f, 0);
        Assert.NotNull(Rush.Contact(from, to, 0f, new Vector3(0, 3.3f, 0), Spec, c));          // ahead, within reach
        Assert.Null(Rush.Contact(from, to, 0f, new Vector3(0.8f, 2.1f, 0), Spec, c));         // shoulder to shoulder (overlapping capsules)
        Assert.Null(Rush.Contact(from, to, 0f, new Vector3(0f, 1.3f, 0), Spec, c));           // behind, overlapping
        Assert.NotNull(Rush.Contact(from, to, 0f, new Vector3(0.6f, 3.2f, 0), Spec, c));      // ahead and a little aside

        // In the world (no teams: everyone may be harmed): a player keeping pace beside the run-up (0.8 m to the side: the
        // capsules overlap) and one 0.8 m behind are not reached; the one ahead is. Spawn points are taken in order when every
        // point is near a player, which places the close ones.
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 7, 0), 0f), new SpawnPoint(new Vector3(0.8f, 0f, 0), 0f), new SpawnPoint(new Vector3(0f, -0.8f, 0), 0f)]);
        for (int id = 1; id <= 4; id++) w.Spawn(id, id == 1 ? Marine : Chaos);
        Assert.Equal((new Vector3(0, 7, 0), new Vector3(0.8f, 0f, 0), new Vector3(0f, -0.8f, 0)), (w.Players[2].Position, w.Players[3].Position, w.Players[4].Position));
        var rushes = new List<RushEvent>();
        Charge(w, 40, 10, _ => rushes.AddRange(w.LastRushes),
            (k, id) => id is 3 or 4 ? new PlayerInput(0, 0f, k <= 10 ? 1f : 0f, 0f, InputButtons.None) : null);   // they run alongside until the press
        var e = Assert.Single(rushes);
        Assert.Equal(2, e.Target);
        Assert.Equal((100f, ReactionKind.None), (w.Players[3].Shield, w.Players[3].Reaction));   // beside
        Assert.Equal((100f, ReactionKind.None), (w.Players[4].Shield, w.Players[4].Reaction));   // behind
    }

    [Fact]
    public void A_target_behind_a_wall_is_not_reached_and_the_wall_stops_the_rush()
    {
        var w = Arena(Config, Walled(4f), false, (Marine, Vector3.Zero), (Chaos, new Vector3(0, 4.55f, 0)));
        var rushes = new List<RushEvent>();
        var phases = new List<RushPhase>();
        Charge(w, 40, 10, _ => { rushes.AddRange(w.LastRushes); phases.Add(w.Players[1].Rush); });
        Assert.Empty(rushes);
        Assert.Contains(RushPhase.Collision, phases);   // the wall stops it
        Assert.Equal((100f, ReactionKind.None), (w.Players[2].Shield, w.Players[2].Reaction));
    }

    [Theory]
    [InlineData(9f, false)]    // they meet 0.1 s into their rushes: both in their hyper armour (M32)
    [InlineData(17f, true)]    // they meet 0.4 s in: past the armour, both stunned
    public void Two_chargers_meeting_head_on_both_hit_and_are_stunned_only_past_their_armour(float apart, bool stunned)
    {
        var w = Arena(Config, null, false, (Marine, Vector3.Zero), (Chaos, new Vector3(0, apart, 0)));
        var rushes = new List<RushEvent>();
        var reactions = new List<ReactionEvent>();
        var phases = new List<(RushPhase, RushPhase)>();
        // Player 2 sprints toward -Y and presses F in the same tick as player 1.
        Charge(w, 40, 10, _ => { rushes.AddRange(w.LastRushes); reactions.AddRange(w.LastReactions); phases.Add((w.Players[1].Rush, w.Players[2].Rush)); },
            (k, id) => id == 2 ? new PlayerInput(0, 0f, k <= 10 ? 1f : 0f, MathF.PI, (k <= 10 ? InputButtons.Sprint : InputButtons.None) | (k == 10 ? InputButtons.MeleeHeavy : InputButtons.None)) : null);
        output.WriteLine($"{apart} m: {string.Join("; ", rushes)}; contact {(rushes[0].Tick - 10) * Dt:F3} s played in the rushes");
        Assert.Equal([(1, 2), (2, 1)], rushes.Select(r => (r.Player, r.Target)).OrderBy(r => r.Player));
        Assert.Single(rushes.Select(r => r.Tick).Distinct());
        int contact = (int)rushes[0].Tick - 1;   // the callback index of the contact tick
        Assert.Equal(stunned, (rushes[0].Tick - 10) * Dt > Spec.HyperArmourUntil + 1e-5f);   // the rushes' time played at the contact
        Assert.Equal(stunned ? [1, 2] : [], reactions.Where(r => r.Kind == ReactionKind.Stun).Select(r => r.Target).OrderBy(x => x));
        // Armoured, both enter the collision stop whatever the player order (the stops follow every contact of the tick); stunned, neither does.
        Assert.Equal(stunned ? (RushPhase.None, RushPhase.None) : (RushPhase.Collision, RushPhase.Collision), phases[contact]);
        Assert.Equal((0f, 0f), (w.Players[1].Shield, w.Players[2].Shield));   // 25 + 75 against a 100 shield each, armour or not
    }

    [Fact]
    public void The_chargers_hyper_armour_covers_the_rush_up_to_its_time_and_not_the_collision_stop()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, Config);
        Assert.False(Rush.HyperArmour(s, Config));
        var covered = Enumerable.Range(1, 30).Where(n => Rush.HyperArmour(s with { Rush = RushPhase.Rush, RushTicks = n }, Config)).ToList();
        Assert.Equal(Enumerable.Range(1, 9), covered);   // 0.3 s: the press tick and 8 more (as the evade's window, the press tick included)
        Assert.False(Rush.HyperArmour(s with { Rush = RushPhase.Collision, RushTicks = 1 }, Config));
        Assert.False(Reaction.CanReact(s with { Rush = RushPhase.Rush, RushTicks = 9 }, Config));
        Assert.True(Reaction.CanReact(s with { Rush = RushPhase.Rush, RushTicks = 10 }, Config));
        Assert.True(Reaction.CanReact(s with { Rush = RushPhase.Collision, RushTicks = 2 }, Config));
        // The hash covers it.
        Assert.NotEqual(Config.Hash(), (Config with { Classes = [Config.Classes[0] with { MeleeRush = Spec with { HyperArmourUntil = 0.31f } }, Config.Classes[1]] }).Hash());
    }

    [Fact]
    public void A_stun_in_the_rushs_first_half_is_shrugged_off_and_one_later_ends_the_charge()
    {
        // A third player beside the rush's path (2 m aside: never reached by the rush) strikes the passing charger with a stunning
        // heavy strike (no lunge), one strike per run, pressed in a different tick each run so it lands across the whole rush.
        const int Striker = 2;
        var c = Config with { Classes = [.. Config.Classes, Config.Classes[Chaos] with { MeleeHeavy = [Heavy with { Lunge = 0f }] }] };
        int early = 0, late = 0;
        for (int strikeAt = 0; strikeAt <= 28; strikeAt++)
        {
            var w = Arena(c, null, false, (Marine, Vector3.Zero), (Striker, new Vector3(2f, 7.5f, 0)));
            var states = new List<PlayerState>();
            var hits = new List<(int Index, HitEvent Hit)>();
            Charge(w, 50, 10, k => { states.Add(w.Players[1]); hits.AddRange(w.LastHits.Where(h => h.Shooter == 2 && h.Target == 1).Select(h => (k, h))); },
                (k, id) => id == 2 ? new PlayerInput(0, 0f, 0f, MathF.PI / 2f, k == strikeAt ? InputButtons.MeleeHeavy : InputButtons.None) : null);   // facing -X, toward the path
            if (hits.Count == 0) continue;
            var (index, hit) = Assert.Single(hits);
            var before = states[index - 1];
            if (before.Rush != RushPhase.Rush) continue;   // struck before the press or after the rush
            int n = before.RushTicks + 1;                    // the rush tick the strike landed in
            var after = states[index];
            Assert.True(hit.ShieldDamage + hit.HealthDamage > 0f, "the damage applies, armour or not");
            if (n * Dt <= Spec.HyperArmourUntil + 1e-5f)
            {
                early++;
                Assert.Equal((ReactionKind.None, RushPhase.Rush, n), (after.Reaction, after.Rush, after.RushTicks));
                Assert.Equal((RushPhase.Rush, n + 1), (states[index + 1].Rush, states[index + 1].RushTicks));   // the charge runs on
            }
            else
            {
                late++;
                Assert.Equal((ReactionKind.Stun, RushPhase.None), (after.Reaction, after.Rush));
            }
            output.WriteLine($"strike pressed at {strikeAt}: landed in rush tick {n} ({n * Dt:F3} s): {after.Reaction}, {after.Rush}");
        }
        Assert.True(early >= 2 && late >= 2, $"early {early}, late {late}");
    }

    [Fact]
    public void A_lethal_charge_is_credited_as_a_melee_kill()
    {
        var fragile = Config with { HealthMax = 50f, ShieldMax = 0f };
        var w = Arena(fragile, null, false, (Marine, Vector3.Zero), (Chaos, new Vector3(0, 6, 0)));
        var kills = new List<KillEvent>();
        Charge(w, 30, 10, _ => kills.AddRange(w.LastKills));
        var k = Assert.Single(kills);
        Assert.Equal((1, 2, KillCause.Melee), (k.Killer, k.Victim, k.Cause));
    }

    [Fact]
    public void Contact_uses_the_targets_as_the_charger_saw_them()
    {
        // The target stands in the path, then steps aside just before the rush gets there. A charger viewing the present
        // misses it; one whose client still showed it in the path (12 ticks behind) reaches it.
        foreach (int behind in new[] { 0, 12 })
        {
            var w = Arena(Config, null, false, (Marine, Vector3.Zero), (Chaos, new Vector3(0, 7.5f, 0)));
            var rushes = new List<RushEvent>();
            for (int k = 0; k < 40; k++)
            {
                int tick = k;
                uint view = (uint)Math.Max(0, (int)w.Tick - behind);
                w.Step(id => id == 1
                    ? new PlayerInput(0, 0f, tick <= 10 ? 1f : 0f, 0f, (tick <= 10 ? InputButtons.Sprint : InputButtons.None) | (tick == 10 ? InputButtons.MeleeHeavy : InputButtons.None), 0f, view)
                    : new PlayerInput(0, tick is >= 6 and < 14 ? 1f : 0f, 0f, MathF.PI, InputButtons.None, 0f, view));   // sidesteps
                rushes.AddRange(w.LastRushes);
            }
            output.WriteLine($"view {behind} ticks behind: {rushes.Count} contacts; target at {w.Players[2].Position}");
            Assert.Equal(behind > 0 ? 1 : 0, rushes.Count);
        }
    }

    [Fact]
    public void The_spacing_counts_down_during_a_reaction()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, Config) with { RushLock = 20 };
        s = Reaction.Start(s, Config, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.UnitY);
        for (int k = 0; k < 10; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _);
        Assert.Equal((ReactionKind.Stun, 10), (s.Reaction, s.RushLock));
    }

    [Fact]
    public void The_collision_stop_allows_melee_from_its_branch_and_a_reaction_ends_a_rush()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, Config);
        s = Rush.Stopped(s with { Rush = RushPhase.Rush, RushTicks = 5 });
        for (int k = 0; k < 8; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), Config, out _);   // held: no press edge
        Assert.Equal(RushPhase.Collision, s.Rush);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), Config, out _);   // 0.3 s played: the melee branch
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
        var rushing = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, Config) with { Rush = RushPhase.Rush, RushTicks = 3 };
        Assert.Equal(RushPhase.None, Reaction.Start(rushing, Config, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.UnitY).Rush);
    }

    // ---------------------------------------------------------------- prediction and protocol

    [Fact]
    public void The_own_record_carries_everything_a_rush_replays_from()
    {
        var w = Arena(Config, Walled(12f), false, (Marine, Vector3.Zero));
        Charge(w, 12, 10);
        var me = w.Players[1];
        Assert.Equal(RushPhase.Rush, me.Rush);
        var record = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(w.Tick, 0, 0, [me], Self: 1)))).Players[0];
        Assert.Equal(me, record);
        var slim = Protocol.Slim(me);
        Assert.Equal((RushPhase.Rush, me.RushTicks, 0), (slim.Rush, slim.RushTicks, slim.RushLock));
        var server = me; var client = record;
        for (int k = 0; k < 60; k++)
        {
            var input = new PlayerInput((uint)k, 0f, k % 3 == 0 ? 1f : 0f, 0.05f * k, k % 11 == 0 ? InputButtons.MeleeHeavy | InputButtons.Melee : InputButtons.None);
            server = Combat.StepPlayer(server, input, Config, out _, w.Collision);
            client = Combat.StepPlayer(client, Protocol.Quantize(input), Config, out _, w.Collision);
            Assert.Equal(server, client);
        }
    }

    [Fact]
    public void Protocol_18_carries_the_rush_phase_in_every_record_and_refuses_malformed_ones()
    {
        Assert.Equal(42, Protocol.Version);
        var p = new PlayerState(4, Vector3.One, Vector3.Zero, 1f, true) { Alive = true, Rush = RushPhase.Collision, RushTicks = 17, RushLock = 9 };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [p, p with { PlayerId = 5 }], Self: 4))));
        Assert.Equal(p, back.Players[0]);
        Assert.Equal((RushPhase.Collision, 17, 0), (back.Players[1].Rush, back.Players[1].RushTicks, back.Players[1].RushLock));
        var slim = Protocol.Write(new Snapshot(9, 1, 1, [p with { RushLock = 0 }]));
        Assert.Equal(32 | 2, slim[^30]);   // the melee byte: bit 5 and the phase
        byte[] With(byte b) { var c = (byte[])slim.Clone(); c[^30] = b; return c; }
        Assert.Null(Protocol.Read(With(32)));          // phase 0
        Assert.NotNull(Protocol.Read(With(32 | 3)));   // protocol 30: phase 3, the dash attack's lunge (phases 1..6 in bits 0..2)
        Assert.Null(Protocol.Read(With(32 | 7)));      // phase 7
        Assert.Null(Protocol.Read(With(32 | 64 | 1))); // with the heavy flag
        Assert.Null(Protocol.Read(With(32 | 8 | 1)));  // unknown bits
        Assert.Null(Protocol.Read(With(32 | 16 | 1))); // unknown bits
        Assert.NotNull(Protocol.Read(With(32 | 1)));
        var half = (byte[])slim.Clone(); BitConverter.GetBytes(2.5f).CopyTo(half, half.Length - 29);
        Assert.Null(Protocol.Read(half));              // ticks not whole
        Assert.Equal(31, Protocol.MaxMeleeStep);
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);
    }

    [Theory]
    [InlineData(293)]
    [InlineData(294)]
    public void Prediction_reconciles_after_contacts_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // Two clients charge each other every 4 s (aimed at the other's server position by the test script): contacts stop the
        // rushes and stun; outside the windows right after a contact the prediction matches the server exactly.
        var config = Config with
        {
            Classes = Config.Classes.Select(k => k with { MeleeRush = Spec with { Primary = Plain, Secondary = Plain }, Side = Team.None }).ToArray(),
            Weapons = [Rifle with { Damage = 0f, ReserveSize = 5000 }],
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        // feel/melee-lock: the charges lock onto the other (each client's view tick on a held snapshot: exact) and steer, since they are aimed 0.3 rad off it.
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = Marine });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        var external = new HashSet<(uint Tick, int Player)>();
        int contacts = 0;
        PlayerInput Input(int i, uint t)
        {
            var me = h.Clients[i];
            var players = h.Server.World.Players;
            int other = players.Keys.FirstOrDefault(id => id != me.PlayerId);
            float yaw = 0f;
            if (players.TryGetValue(me.PlayerId, out var a) && players.TryGetValue(other, out var b))
                yaw = MathF.Atan2(-(b.Position.X - a.Position.X), b.Position.Y - a.Position.Y);
            uint phase = t % 120;
            if (phase < 30) return new PlayerInput(t, 0f, -1f, yaw, InputButtons.None);          // back off
            if (phase is >= 40 and < 52) return new PlayerInput(t, 0f, 1f, yaw + 0.3f, InputButtons.Sprint | (phase == 51 ? InputButtons.MeleeHeavy : InputButtons.None));   // sprint in and charge
            return new PlayerInput(t, 0f, 0f, yaw, InputButtons.None);
        }
        int steered = 0;
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            if (truth.TryGetValue(h.Server.World.Tick - 1, out var last) && !truth.ContainsKey(h.Server.World.Tick))
                foreach (var (id, p) in h.Server.World.Players)
                    if (p.Rush == RushPhase.Rush && p.RushTicks > 1 && last.TryGetValue(id, out var q) && q.Rush == RushPhase.Rush && MathF.Abs(p.Yaw - q.Yaw) > 1e-4f) steered++;
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            foreach (var r in h.Server.World.LastRushes) { external.Add((r.Tick, r.Player)); external.Add((r.Tick, r.Target)); }
            foreach (var r in h.Server.World.LastReactions) external.Add((r.Tick, r.Target));
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        contacts = external.Select(x => x.Tick).Distinct().Count();
        const uint Window = 16;
        int stoppedCompared = 0;
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            var starts = external.Where(x => x.Player == id).Select(x => x.Tick).ToList();
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                if (starts.Any(s => s <= tick && tick < s + Window)) continue;
                compared++;
                if (real.Rush == RushPhase.Collision || real.Reaction != ReactionKind.None) stoppedCompared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; contacts on it {starts.Count}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 200, $"client {i}: only {compared} compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"contact ticks {contacts}; ticks compared in a stop or a reaction after a contact {stoppedCompared}; steered rush ticks {steered}");
        Assert.True(steered >= 10, $"only {steered} steered rush ticks");
        Assert.True(contacts >= 4, $"only {contacts} contacts");
        Assert.True(stoppedCompared >= 20, $"only {stoppedCompared}");
    }

    [Theory]
    [InlineData(291)]
    [InlineData(292)]
    public void Prediction_matches_the_server_through_rushes_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Config with
        {
            Classes = Config.Classes.Select(k => k with { MeleeRush = Spec with { Primary = Plain, Secondary = Plain }, Side = Team.None, Melee = [Light with { Damage = 0f, SecondaryDamage = 0f }],
                MeleeHeavy = [Heavy with { Damage = 0f, SecondaryDamage = 0f }] }).ToArray(),
            Weapons = [Rifle with { Damage = 0f, ReserveSize = 5000 }],
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 3; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = Marine });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        var external = new HashSet<(uint Tick, int Player)>();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            uint phase = (t + (uint)i * 41) % 120;
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Melee | InputButtons.MeleeHeavy);
            float y = move.MoveY;
            if (phase is >= 20 and < 40) { y = 1f; buttons |= InputButtons.Sprint; }        // run forward …
            if (phase is 38 or 39) buttons |= InputButtons.MeleeHeavy;                     // … and charge
            if (phase is 80 or 81) buttons |= InputButtons.MeleeHeavy;                     // standing: the heavy strike
            return move with { MoveY = y, Buttons = buttons };
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            foreach (var r in h.Server.World.LastRushes) { external.Add((r.Tick, r.Player)); external.Add((r.Tick, r.Target)); }
            foreach (var r in h.Server.World.LastReactions) external.Add((r.Tick, r.Target));
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        const uint Window = 16;   // a client cannot predict the world stopping its rush or hitting it
        int rushing = 0, total = 0;
        for (int i = 0; i < n; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            var starts = external.Where(x => x.Player == id).Select(x => x.Tick).ToList();
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                if (starts.Any(s => s <= tick && tick < s + Window)) continue;
                compared++;
                if (real.Rush != RushPhase.None) rushing++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            total += truth.Values.Count(wd => wd.TryGetValue(id, out var x) && x.Rush == RushPhase.Rush && x.RushTicks == 1);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"rushes started {total}, ticks compared during rushes {rushing}, contacts {h.Server.World.LastRushes.Count}");
        Assert.True(total >= 10, $"only {total} rushes");
        Assert.True(rushing >= 100, $"only {rushing} rushing ticks compared");
    }

    [Fact]
    public void A_charge_pressed_during_the_match_begins_countdown_does_nothing_and_works_after_it()
    {
        World Start(MatchRules rules)
        {
            var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 60, 0), 0f)]) { Rules = rules };
            w.Spawn(1, Marine); w.Spawn(2, Chaos);
            return w;
        }
        PlayerInput Press(int id) => id == 1 ? new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy) : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None);

        // Control: without a countdown the same inputs start the rush.
        var control = Start(new MatchRules { TimeLimit = 0f });
        bool rushed = false;
        for (int k = 0; k < 60 && !rushed; k++) { control.Step(id => k < 10 ? new PlayerInput(0, 0f, id == 1 ? 1f : 0f, id == 1 ? 0f : MathF.PI, id == 1 ? InputButtons.Sprint : InputButtons.None) : Press(id)); rushed |= control.Players[1].Rush != RushPhase.None; }
        Assert.True(rushed, "the control never started a rush");

        var w = Start(new MatchRules { TimeLimit = 0f, CountdownTime = 2f });
        w.Step(Press);
        Assert.Equal(MatchState.Countdown, w.State);
        while (w.State == MatchState.Countdown)
        {
            w.Step(Press);
            Assert.Equal(RushPhase.None, w.Players[1].Rush);
            Assert.Empty(w.LastHits);
        }
        // After it the charge works: sprint, then press.
        rushed = false;
        for (int k = 0; k < 60 && !rushed; k++) { w.Step(id => k < 10 ? new PlayerInput(0, 0f, id == 1 ? 1f : 0f, id == 1 ? 0f : MathF.PI, id == 1 ? InputButtons.Sprint : InputButtons.None) : Press(id)); rushed |= w.Players[1].Rush != RushPhase.None; }
        Assert.True(rushed);
    }
}
