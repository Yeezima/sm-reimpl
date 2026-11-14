using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// charge-cancel (protocol 66) and charge-ally-slide (protocol 67; the owner 2026-11-11): a Grenade press (with or without a grenade left) ends no charge phase and starts no throw while the
/// charge plays; S held back (tapped or held) ends the bull rush from its stick branch; the restore switch <see cref="SimConfig.ChargeBackCancel"/> false brings back the protocol 65
/// rule (the grenade cancel); a charge passes an ally it cannot harm (no contact, no stop, nothing on the ally: it slides past), while enemies are hit, stunned and
/// stop it as before; <see cref="SimConfig.ChargeAllyBump"/> false restores the stop; prediction agrees with the server; an S tap before the branch is not remembered. Synthetic clips (<see cref="RushTests"/>): the rush moves
/// 10 m/s, ends at 1.0 s (30 ticks), its stick branch at 0.6 s (18 ticks); a frag (<see cref="GrenadeTests.Frag"/>) as the class's item.
/// </summary>
public class ChargeCancelTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const int StickBranchTicks = 18, EndTicks = 30;   // 0.6 s and 1.0 s of the rush clip

    // The collision stop's stick branch moved before its end (0.5 s; RushTests' stop clip has both at 0.9 s), so a grenade press and the stick can be told from the end.
    static readonly RushSpec Spec = RushTests.Spec with { Collision = RushTests.Spec.Collision with { MoveAt = 0.5f } };

    static readonly SimConfig Config = RushTests.Config with
    {
        Thrown = [GrenadeTests.Frag],
        Classes = [.. RushTests.Config.Classes.Select(k => k with { EquipmentChoices = [0], MeleeRush = Spec })],
    };

    static readonly SimConfig Old = Config with { ChargeBackCancel = false };
    /// <summary>The protocol 66 to 68 S cancel: from the rush's stick branch only (restore switch --old-charge-cancel-delay).</summary>
    static readonly SimConfig Delayed = Config with { ChargeInstantCancel = false };

    static PlayerState Fresh(int grenades, SimConfig? c = null) =>
        Combat.Respawned(Loadouts.Choose(c ?? Config, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, -1, -1, -1, 0), c ?? Config) with { Grenades = grenades };

    /// <summary>W + Shift + F from rest: the rush's first tick.</summary>
    static PlayerState Started(int grenades, SimConfig c)
    {
        var s = Combat.StepPlayer(Fresh(grenades, c), new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), c, out _);
        Assert.Equal((RushPhase.Rush, 1), (s.Rush, s.RushTicks));
        return s;
    }

    /// <summary>One charge from its start: <paramref name="input"/> picks each tick's input by the rush ticks played before it; returns every state after a tick and the throws.</summary>
    static (List<PlayerState> States, int Threw) Run(int grenades, SimConfig c, Func<PlayerState, PlayerInput> input, int ticks = 50)
    {
        var s = Started(grenades, c);
        var states = new List<PlayerState> { s };
        int threw = 0;
        for (int k = 0; k < ticks; k++)
        {
            s = Combat.StepPlayer(s, input(s), c, out var e);
            if (e.Threw) threw++;
            states.Add(s);
        }
        return (states, threw);
    }

    static int RushTicks(List<PlayerState> states) => states.Count(p => p.Rush == RushPhase.Rush);
    static readonly PlayerInput Idle = new(0, 0f, 0f, 0f, InputButtons.None);
    static PlayerInput Stick(float x, float y, InputButtons b = InputButtons.None) => new(0, x, y, 0f, b);

    // ---------------------------------------------------------------- the grenade

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_grenade_press_anywhere_in_the_rush_ends_nothing_and_starts_no_throw(int grenades)
    {
        Assert.Equal(EndTicks, RushTicks(Run(grenades, Config, _ => Idle).States));   // the control: no press, the clip's end
        for (int at = 1; at < EndTicks; at++)
        {
            int pressAt = at;
            var (states, threw) = Run(grenades, Config, s => s.Rush == RushPhase.Rush && s.RushTicks == pressAt ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade) : Idle);
            Assert.Equal(EndTicks, RushTicks(states));
            Assert.Equal(0, threw);
            Assert.All(states, p => Assert.False(p.Throwing));
            Assert.All(states, p => Assert.Equal(grenades, p.Grenades));
            Assert.All(states, p => Assert.Equal(ReactionKind.None, p.Reaction));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void With_the_input_buffer_a_grenade_pressed_late_in_the_rush_throws_only_once_it_has_ended(int grenades, bool throws)
    {
        var c = Config with { InputBufferWindow = 0.3f };
        var (states, threw) = Run(grenades, c, s => s.Rush == RushPhase.Rush && s.RushTicks == EndTicks - 3 ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade) : Idle);
        Assert.Equal(EndTicks, RushTicks(states));   // the charge plays to its end
        int end = states.FindIndex(p => p.Rush == RushPhase.None);
        int firstThrow = states.FindIndex(p => p.Throwing);
        output.WriteLine($"{grenades} grenades: the rush ends at state {end}, first throwing state {firstThrow}, throws {threw}");
        Assert.All(states.Where(p => p.Rush != RushPhase.None), p => Assert.False(p.Throwing));
        if (throws) Assert.Equal(end, firstThrow);   // the buffered press is replayed on the tick the rush ends
        else Assert.Equal((-1, 0), (firstThrow, states[^1].Grenades));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_grenade_press_does_not_end_the_collision_stop_and_its_stick_rule_is_unchanged(int grenades)
    {
        var stop = Rush.Stopped(Fresh(grenades) with { Rush = RushPhase.Rush, RushTicks = 5 });
        var s = stop;
        for (int k = 0; k < 16; k++) s = Combat.StepPlayer(s, Idle, Config, out _);
        Assert.Equal((RushPhase.Collision, 16), (s.Rush, s.RushTicks));   // 0.533 s: past its stick branch (0.5 s)
        var pressed = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), Config, out var e);
        Assert.Equal((RushPhase.Collision, false, false), (pressed.Rush, pressed.Throwing, e.Threw));
        Assert.Equal(RushPhase.None, Combat.StepPlayer(s, Stick(1f, 0f), Config, out _).Rush);   // the stick still ends it (any direction)
        Assert.Equal(RushPhase.None, Combat.StepPlayer(s, Stick(0f, 1f), Config, out _).Rush);
        // The protocol 65 rule: the press ends it (and throws when a grenade is left).
        var old = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), Old, out var oldEvents);
        Assert.Equal((RushPhase.None, grenades > 0), (old.Rush, old.Throwing));
    }

    [Theory]
    [InlineData(RushPhase.DashHit, 20)]    // the hit state: stick branch 19 ticks, end 21
    [InlineData(RushPhase.DashStop, 16)]   // the run-out: stick branch 14 ticks, end 27
    public void A_grenade_press_does_not_end_the_dash_attacks_states_either(RushPhase phase, int ticks)
    {
        var c = JumpPackAttackTests.Config;
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c) with { Rush = phase, RushTicks = ticks };
        Assert.Equal((phase, ticks + 1), (Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), c, out _) is var p ? (p.Rush, p.RushTicks) : default));
        Assert.Equal(RushPhase.None, Combat.StepPlayer(s, Stick(0f, -1f), c, out _).Rush);    // the stick still ends it (S here, any direction)
        Assert.Equal(RushPhase.None, Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), c with { ChargeBackCancel = false }, out _).Rush);   // protocol 65
    }

    // ---------------------------------------------------------------- S

    [Theory]
    [InlineData(0f, -1f)]      // S
    [InlineData(-1f, -1f)]     // S + A
    [InlineData(1f, -1f)]      // S + D
    [InlineData(0.3f, -0.6f)]  // a pad's stick back and a little right
    public void S_held_ends_the_rush_at_its_stick_branch(float x, float y)
    {
        var (states, _) = Run(1, Delayed, s => s.Rush != RushPhase.Rush || s.RushTicks >= 2 ? Stick(x, y) : Idle);   // held from the rush's second tick on
        Assert.Equal(StickBranchTicks, RushTicks(states));
        var last = states.Last(p => p.Rush == RushPhase.Rush);
        Assert.Equal(StickBranchTicks, last.RushTicks);
        var after = states[states.IndexOf(last) + 1];
        Assert.Equal((RushPhase.None, ReactionKind.None, 0, false), (after.Rush, after.Reaction, after.MeleeStep, after.Throwing));   // no stop clip, no stun, nothing started
        Assert.True(after.RushLock > 0, "the spacing runs on");
        // Normal movement: S held turns the body's way to backwards within a few ticks.
        int i = states.IndexOf(after);
        float forwardThen = after.Velocity.Y, forwardLater = states[i + 10].Velocity.Y;
        output.WriteLine($"stick ({x}, {y}): rush ticks {RushTicks(states)}, speed along the view at the cancel {forwardThen:F2} m/s, 10 ticks later {forwardLater:F2} m/s");
        Assert.True(forwardLater < 0f, $"moving back 10 ticks after the cancel ({forwardLater:F2})");
    }

    [Theory]
    [InlineData(0f, 0f)]    // nothing
    [InlineData(0f, 1f)]    // W
    [InlineData(-1f, 0f)]   // A
    [InlineData(1f, 0f)]    // D
    [InlineData(1f, 1f)]    // W + D
    [InlineData(1f, -0.3f)] // a pad's stick right and a little back (the right sector)
    public void Other_sticks_do_not_end_the_rush(float x, float y)
    {
        Assert.Equal(EndTicks, RushTicks(Run(1, Config, s => s.Rush == RushPhase.Rush && s.RushTicks >= 2 ? Stick(x, y) : Idle).States));
    }

    [Fact]
    public void An_S_tap_from_the_branch_on_ends_the_rush_at_once_and_one_before_it_does_nothing()
    {
        foreach (int tapAt in new[] { StickBranchTicks, 22, EndTicks - 1 })
        {
            var (states, _) = Run(0, Delayed, s => s.Rush == RushPhase.Rush && s.RushTicks == tapAt ? Stick(0f, -1f) : Idle);
            Assert.Equal(tapAt, RushTicks(states));   // the tap's tick ends it
        }
        // A tap let go before the branch is not kept (the stick is held, not pressed: no buffer): the rush runs to its end.
        Assert.Equal(EndTicks, RushTicks(Run(0, Delayed, s => s.Rush == RushPhase.Rush && s.RushTicks is 8 or 9 ? Stick(0f, -1f) : Idle).States));
        // Held from before the branch, it ends there.
        Assert.Equal(StickBranchTicks, RushTicks(Run(0, Delayed, s => s.Rush == RushPhase.Rush && s.RushTicks >= 8 ? Stick(0f, -1f) : Idle).States));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.3f)]
    public void An_early_S_tap_is_not_remembered_even_with_the_input_buffer(float window)
    {
        // charge-ally-slide (the owner 2026-11-11: "early s tap should not be remembered"): an S press made before the rush's stick branch (tick 18) is not buffered and does not end the rush
        // when the branch comes, whatever the input buffer's window (the stick is tested on the branch's own tick only); the control: S on the branch's tick ends it.
        var c = Delayed with { InputBufferWindow = window };
        for (int tapAt = 1; tapAt < StickBranchTicks; tapAt++)
        {
            int at = tapAt;
            var (states, _) = Run(0, c, s => s.Rush == RushPhase.Rush && s.RushTicks >= at && s.RushTicks < Math.Min(at + 2, StickBranchTicks) ? Stick(0f, -1f) : Idle);   // a two-tick tap, let go before the branch
            Assert.Equal(EndTicks, RushTicks(states));
        }
        Assert.Equal(StickBranchTicks, RushTicks(Run(0, c, s => s.Rush == RushPhase.Rush && s.RushTicks == StickBranchTicks ? Stick(0f, -1f) : Idle).States));
    }

    [Fact]
    public void The_cancel_keeps_hyper_armour_and_fires_nothing_on_its_tick()
    {
        // The cancel's tick is still the charge's: the held trigger fires nothing in it (BR-02), as for the other early ends.
        var s = Started(0, Config);
        while (s.RushTicks < StickBranchTicks) s = Combat.StepPlayer(s, Idle, Config, out _);
        Assert.True(Rush.HyperArmour(Started(0, Config), Config));   // the armour's window is the rush's (unchanged)
        var cancelled = Combat.StepPlayer(s, Stick(0f, -1f, InputButtons.Fire), Config, out var e);
        Assert.Equal((RushPhase.None, false), (cancelled.Rush, e.Fired));
    }

    // ---------------------------------------------------------------- the restore switch

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_restore_switch_brings_back_the_grenade_cancel_and_S_does_not_end_the_rush(int grenades)
    {
        var (states, threw) = Run(grenades, Old, s => s.Rush == RushPhase.Rush && s.RushTicks == 21 ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade) : Idle);
        Assert.Equal(21, RushTicks(states));   // the press ends it at once (past the branch)
        Assert.Equal(grenades, threw);          // and throws when one is left
        // A press before the branch is lost (no buffer here); S held never ends the rush.
        Assert.Equal(EndTicks, RushTicks(Run(grenades, Old, s => s.Rush == RushPhase.Rush && s.RushTicks == 10 ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade) : Idle).States));
        Assert.Equal(EndTicks, RushTicks(Run(grenades, Old, s => s.Rush == RushPhase.Rush && s.RushTicks >= 2 ? Stick(0f, -1f) : Idle).States));
    }

    [Fact]
    public void The_switches_are_in_the_config_hash_only_when_off()
    {
        var c = RushTests.Config;
        Assert.Equal(c.Hash(), (c with { ChargeBackCancel = true, ChargeAllyBump = true }).Hash());   // on by default: older hashes unchanged
        Assert.NotEqual(c.Hash(), (c with { ChargeBackCancel = false }).Hash());
        Assert.NotEqual(c.Hash(), (c with { ChargeAllyBump = false }).Hash());
        Assert.NotEqual((c with { ChargeBackCancel = false }).Hash(), (c with { ChargeAllyBump = false }).Hash());
        Assert.Equal(72, Protocol.Version);
        // The server's switches (the clients take the same words: client NetworkView.Bodies.cs).
        Assert.True(ServerCommandLine.TryParse(["--old-charge-cancel", "--old-ally-bump"], out var a, out var error), error);
        Assert.Equal((true, true), (a.OldChargeCancel, a.OldAllyBump));
        Assert.True(ServerCommandLine.TryParse([], out var none, out _));
        Assert.Equal((false, false), (none.OldChargeCancel, none.OldAllyBump));
    }

    // ---------------------------------------------------------------- the ally bump (the world)

    static World Teams(SimConfig c, bool friendlyFire, Team other)
    {
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 6, 0), 0f), new SpawnPoint(new Vector3(30, 0, 0), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = friendlyFire } };
        w.Spawn(1, 0); w.Spawn(3, 1); w.Spawn(2, other == Team.SpaceMarines ? 0 : 1);
        Assert.Equal(other, w.Players[2].Team);
        return w;
    }

    /// <summary>Player 1 runs forward (+Y) and charges at tick 10; the others stand facing it. Each tick's states and rush events.</summary>
    static (List<(PlayerState Charger, PlayerState Met)> States, List<RushEvent> Rushes) Charge(World w, int ticks = 40)
    {
        var states = new List<(PlayerState, PlayerState)>();
        var rushes = new List<RushEvent>();
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = w.Tick;
            w.Step(id => (id == 1
                ? new PlayerInput(0, 0f, tick <= 10 ? 1f : 0f, 0f, (tick <= 10 ? InputButtons.Sprint : InputButtons.None) | (tick == 10 ? InputButtons.MeleeHeavy : InputButtons.None))
                : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = view });
            states.Add((w.Players[1], w.Players[2]));
            rushes.AddRange(w.LastRushes);
        }
        return (states, rushes);
    }

    [Fact]
    public void A_charge_into_an_ally_slides_past_it_and_goes_on_with_nothing_on_the_ally()
    {
        var (states, rushes) = Charge(Teams(Config, friendlyFire: false, Team.SpaceMarines));
        Assert.Empty(rushes);   // no contact at all
        var rush = states.Select(p => p.Charger).Where(p => p.Rush == RushPhase.Rush).ToList();
        output.WriteLine($"rush ticks {rush.Count}, charger y {states[^1].Charger.Position.Y:F2}, ally y {states[^1].Met.Position.Y:F2}");
        Assert.Equal(EndTicks, rush.Count);                                                            // the clip plays to its end, as with nobody there
        Assert.DoesNotContain(states, p => p.Charger.Rush == RushPhase.Collision);                      // never the recoil
        Assert.All(states, p => Assert.Equal(ReactionKind.None, p.Charger.Reaction));
        Assert.All(states, p => Assert.Equal((ReactionKind.None, 100f, 1000f), (p.Met.Reaction, p.Met.Shield, p.Met.Health)));   // nothing on the ally
        Assert.All(states, p => Assert.Equal(new Vector3(0f, 6f, 0f), p.Met.Position));                // not shoved
        Assert.True(states[^1].Charger.Position.Y > states[^1].Met.Position.Y + 1f, "the charger is past the ally");
        // The same charge with nobody ahead: speed and direction within tolerance (the pass loses nothing).
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = false } };
        w.Spawn(1, 0);
        var free = new List<PlayerState>();
        for (int k = 0; k < 40; k++)
        {
            int tick = k; uint view = w.Tick;
            w.Step(id => new PlayerInput(0, 0f, tick <= 10 ? 1f : 0f, 0f, (tick <= 10 ? InputButtons.Sprint : InputButtons.None) | (tick == 10 ? InputButtons.MeleeHeavy : InputButtons.None)) with { ViewTick = view });
            free.Add(w.Players[1]);
        }
        for (int k = 0; k < states.Count; k++)
        {
            Assert.Equal(free[k].Rush, states[k].Charger.Rush);
            Assert.Equal(free[k].Position.Y, states[k].Charger.Position.Y, 3);
            Assert.Equal(free[k].Position.X, states[k].Charger.Position.X, 3);
            Assert.Equal(free[k].Velocity.Y, states[k].Charger.Velocity.Y, 3);
        }
    }

    [Fact]
    public void With_friendly_fire_or_the_restore_switch_the_ally_stops_the_charge_as_before()
    {
        // Friendly fire: the ally is hit and stunned and the charger plays the stop (protocol 65, unchanged).
        var (ff, ffRushes) = Charge(Teams(Config, friendlyFire: true, Team.SpaceMarines));
        var e = Assert.Single(ffRushes);
        Assert.True(e.Harmed);
        Assert.Equal(RushPhase.Collision, ff[(int)e.Tick - 1].Charger.Rush);
        Assert.Equal(ReactionKind.Stun, ff[(int)e.Tick - 1].Met.Reaction);
        // --old-ally-bump: the stop, the ally untouched.
        var (old, oldRushes) = Charge(Teams(Config with { ChargeAllyBump = false }, friendlyFire: false, Team.SpaceMarines));
        e = Assert.Single(oldRushes);
        Assert.False(e.Harmed);
        Assert.Equal(RushPhase.Collision, old[(int)e.Tick - 1].Charger.Rush);
        Assert.Equal((ReactionKind.None, 100f), (old[(int)e.Tick - 1].Met.Reaction, old[(int)e.Tick - 1].Met.Shield));
        // The rule is every mode's (Musou Mode too: the owner 2026-11-11 gave no exception).
        Assert.True(ChargeCancel.AllyPasses(Config with { Extras = ContentExtras.Musou }, friendlyFire: false));
        Assert.True(ChargeCancel.AllyPasses(Config with { Extras = ContentExtras.Orks }, friendlyFire: false));
        Assert.False(ChargeCancel.AllyPasses(Config, friendlyFire: true));
        Assert.False(ChargeCancel.AllyPasses(Config with { ChargeAllyBump = false }, friendlyFire: false));
    }

    [Fact]
    public void A_charge_into_an_enemy_hits_stuns_and_stops_as_before()
    {
        var (states, rushes) = Charge(Teams(Config, friendlyFire: false, Team.Chaos));
        var e = Assert.Single(rushes);
        Assert.Equal((2, true), (e.Target, e.Harmed));
        var (charger, enemy) = states[(int)e.Tick - 1];
        Assert.Equal((RushPhase.Collision, ReactionKind.Stun), (charger.Rush, enemy.Reaction));
        Assert.Equal((0f, 1000f), (enemy.Shield, enemy.Health));   // 25 then 75 off the 100 shield
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_own_prediction_of_an_ally_contact_is_the_worlds(bool friendlyFire)
    {
        // The own client's ChargeLock.Predict, stepped beside the world with the same input and the view the input names (no bodies: PlayerBodies off, so neither side
        // collides with a lagged picture): it gives the world's state every tick: no contact (the pass) without friendly fire, the stop with it.
        var c = Config with { PlayerBodies = false };
        var w = Teams(c, friendlyFire, Team.SpaceMarines);
        ChargeContact? contact = null;
        PlayerState predicted = default;
        for (int k = 0; k < (friendlyFire ? 40 : 24) && (contact is null || !friendlyFire); k++)
        {
            int tick = k;
            var view = new Dictionary<int, PlayerState>(w.Players);
            var input = new PlayerInput(0, 0f, tick <= 10 ? 1f : 0f, 0f, (tick <= 10 ? InputButtons.Sprint : InputButtons.None) | (tick == 10 ? InputButtons.MeleeHeavy : InputButtons.None)) with { ViewTick = w.Tick };
            var before = w.Players[1];
            var stepped = Combat.StepPlayer(before, input.Sanitized() with { Tick = w.Tick + 1 }, c, out _);
            predicted = ChargeLock.Predict(1, before, stepped, c, view, null, null, out contact, friendlyFire);
            uint viewTick = w.Tick;
            w.Step(id => id == 1 ? input : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None) with { ViewTick = viewTick });
        }
        output.WriteLine($"friendly fire {friendlyFire}: contact {contact}; predicted {predicted.Rush}, server {w.Players[1].Rush}");
        Assert.Equal(friendlyFire, contact is { Target: 2 });
        Assert.Equal(friendlyFire ? RushPhase.Collision : RushPhase.Rush, predicted.Rush);
        Assert.Equal(w.Players[1].Rush, predicted.Rush);
        Assert.Equal(w.Players[1].Position, predicted.Position);
        Assert.Equal(w.Players[1].Velocity, predicted.Velocity);
        Assert.Equal(w.Players[1].Yaw, predicted.Yaw);
    }

    [Theory]
    [InlineData(391)]
    [InlineData(392)]
    public void Prediction_matches_the_server_through_S_cancels_grenade_presses_and_ally_passes_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // Three marines of one side (side requests honoured, no friendly fire) run at each other in turn and charge; each charge is cancelled with S (held or tapped) or mashed
        // with Grenade (a frag in hand or none); allies in the way are passed (bodies on: the shared collision slides the charger along them). Every predicted tick of every client matches the server's, contacts included (nothing is excluded).
        var config = Config with
        {
            Classes = [.. Config.Classes.Select(k => k with { Side = Team.None, Melee = [RushTestsLight], MeleeHeavy = [] })],
            Weapons = [Config.Weapons[0] with { Damage = 0f, ReserveSize = 5000 }],
            Thrown = [GrenadeTests.Frag with { Projectile = GrenadeTests.Frag.Projectile with { Splash = GrenadeTests.Frag.Projectile.Splash! with { Damage = GrenadeTests.Frag.Projectile.Splash.Damage with { Damage = 0f, ShieldDamage = 0f } } } }],
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions
        {
            PeerTimeout = TimeSpan.FromMinutes(5), SnapshotRate = 20,
            Match = new MatchRules { TimeLimit = 0f, ScoreLimit = 0, FriendlyFire = false, HonorSideRequests = true },
        });
        for (int i = 0; i < 3; i++)
            h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = 0, RequestedSide = Team.SpaceMarines, SmoothInputLead = false, RequestedEquipment = 0 });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 3).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        int passes = 0, cancels = 0, contactsSeen = 0;
        PlayerInput Input(int i, uint t)
        {
            var me = h.Clients[i];
            var players = h.Server.World.Players;
            int other = players.Keys.Where(id => id != me.PlayerId).OrderBy(id => id).Skip((int)(t / 120 % 2)).FirstOrDefault();
            float yaw = 0f;
            if (players.TryGetValue(me.PlayerId, out var a) && players.TryGetValue(other, out var b))
                yaw = MathF.Atan2(-(b.Position.X - a.Position.X), b.Position.Y - a.Position.Y);
            uint phase = (t + (uint)i * 40) % 120;
            int kind = (int)((t + (uint)i * 40) / 120 % 4);
            if (phase < 30) return new PlayerInput(t, 0f, -1f, yaw, InputButtons.None);   // back off
            if (phase is >= 40 and < 52) return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint | (phase == 51 ? InputButtons.MeleeHeavy : InputButtons.None));   // run in and charge
            if (phase is >= 52 and < 85)
            {
                return kind switch
                {
                    0 => new PlayerInput(t, 0f, phase >= 60 ? -1f : 0f, yaw, InputButtons.None),                                      // S held
                    1 => new PlayerInput(t, 0f, phase is 70 or 71 ? -1f : 0f, yaw, InputButtons.None),                                // S tapped
                    2 => new PlayerInput(t, 0f, 0f, yaw, phase % 3 == 0 ? InputButtons.Grenade : InputButtons.None),                 // Grenade mashed
                    _ => new PlayerInput(t, 0f, 0f, yaw, InputButtons.None),                                                           // the clip's end
                };
            }
            return new PlayerInput(t, 0f, 0f, yaw, InputButtons.None);
        }
        h.Run(TimeSpan.FromSeconds(40), Input, () =>
        {
            var w = h.Server.World;
            if (!truth.ContainsKey(w.Tick))
            {
                if (truth.TryGetValue(w.Tick - 1, out var last))
                    foreach (var (id, p) in w.Players)
                        if (last.TryGetValue(id, out var q) && q.Rush == RushPhase.Rush && p.Rush == RushPhase.None && !w.LastRushes.Any(r => r.Player == id) && q.RushTicks < EndTicks) cancels++;
                contactsSeen += w.LastRushes.Count;
                foreach (var (id, p) in w.Players)
                    if (p.Rush == RushPhase.Rush && w.Players.Any(o => o.Key != id && o.Value.Alive && new Vector2(o.Value.Position.X - p.Position.X, o.Value.Position.Y - p.Position.Y).Length() < 1.4f)) passes++;
            }
            truth[w.Tick] = new Dictionary<int, PlayerState>(w.Players);
            for (int i = 0; i < 3; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        Assert.All(h.Clients, c => Assert.Equal(Team.SpaceMarines, h.Server.World.Players[c.PlayerId].Team));
        for (int i = 0; i < 3; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 600, $"client {i}: only {compared} compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"charging ticks within 1.4 m of an ally {passes}; contacts {contactsSeen}; early ends without a contact (S cancels) {cancels}");
        Assert.True(passes >= 2, $"only {passes} ticks of a charge passing an ally");
        Assert.Equal(0, contactsSeen);   // allies are never a contact (no enemy in this match)
        Assert.True(cancels >= 4, $"only {cancels} S cancels");
        // A pass is no connect for the presentation: no client ever took one for a contact (impact, hit-stop, camera kick, lock spot).
        Assert.All(h.Clients, c => Assert.Equal((0, 0u), (c.ChargeContactsPredicted, c.LastChargeContact.Tick)));
    }

    static readonly MeleeStep RushTestsLight = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 0f, 0f, 3.5f);
}
