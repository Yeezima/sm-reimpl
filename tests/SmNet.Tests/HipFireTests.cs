using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M27 hip fire (spec/behavior.md "Hip fire"): shooting without the Aim button is as accurate as aimed fire (no scatter,
/// owner's direction), works on the move at the full fire rate, and a shot ends the sprint (the behaviour graphs have no
/// sprint-fire state; superseded by protocol 51, sim/no-fire-while-sprinting: the sprint blocks the trigger, NoFireWhileSprintingTests). Synthetic configs only.
/// </summary>
public class HipFireTests(ITestOutputHelper output)
{
    static readonly SimConfig Speeds = new()
    {
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, AimRunSpeed = 3f, AimStrafeSpeed = 3f, AimBackSpeed = 2f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, ShotCooldown = 0.133f, ClipSize = 300, ReserveSize = 0,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);

    static float Speed(PlayerState s) => new Vector2(s.Velocity.X, s.Velocity.Y).Length();

    /// <summary>Runs <paramref name="ticks"/> ticks of the same held input; returns the last state and the shots fired.</summary>
    static (PlayerState State, int Shots) Hold(SimConfig c, InputButtons buttons, int ticks, PlayerState? from = null, uint first = 1)
    {
        var s = from ?? Fresh(c);
        int shots = 0;
        for (int t = 0; t < ticks; t++)
        {
            s = Combat.StepSelf(s, new PlayerInput(first + (uint)t, 0, 1f, 0f, buttons), c, out bool fired);
            if (fired) shots++;
        }
        return (s, shots);
    }

    [Fact]
    public void Firing_while_running_keeps_the_fire_rate()
    {
        int standing = 0;
        var s = Fresh(Speeds);
        for (uint t = 1; t <= 60; t++) { s = Combat.StepSelf(s, new PlayerInput(t, 0, 0, 0f, InputButtons.Fire), Speeds, out bool f); if (f) standing++; }
        var (running, shots) = Hold(Speeds, InputButtons.Fire, 60);
        Assert.Equal(standing, shots);
        Assert.True(standing >= 14);
        Assert.Equal(6.5f, Speed(running), 1e-3f);
    }

    [Fact]
    public void Superseded_by_protocol_51_sprint_with_the_trigger_held_sprints_and_fires_nothing()
    {
        // M27 had the held trigger end the sprint (run speed, firing); sim/no-fire-while-sprinting (protocol 51, the owner 2026-11-01): the sprint blocks the trigger.
        var (state, shots) = Hold(Speeds, InputButtons.Sprint | InputButtons.Fire, 60);
        Assert.Equal(10f, Speed(state), 1e-3f);
        Assert.Equal(0, shots);
        // The same trigger without Sprint fires on the move.
        Assert.True(Hold(Speeds, InputButtons.Fire, 60).Shots >= 14);
    }

    [Fact]
    public void Firing_resumes_when_the_sprint_is_let_go_and_an_empty_clip_trigger_starts_no_reload_while_sprinting()
    {
        var (state, _) = Hold(Speeds, InputButtons.Sprint | InputButtons.Fire, 30);
        Assert.Equal(10f, Speed(state), 1e-3f);
        var (after, shots) = Hold(Speeds, InputButtons.Fire, 40, state, 31);
        Assert.True(shots >= 8);
        Assert.Equal(6.5f, Speed(after), 1e-3f);
        // A trigger held with the clip empty while sprinting asks for nothing (the trigger is ignored); Reload still reloads.
        var dry = Fresh(Speeds) with { Clip = 0, Reserve = 30 };
        var (dryState, _) = Hold(Speeds, InputButtons.Sprint | InputButtons.Fire, 30, dry);
        Assert.Equal(10f, Speed(dryState), 1e-3f);
        Assert.Equal(0f, dryState.ReloadLeft);
    }

    [Fact]
    public void Sprint_is_only_dropped_when_shooting_not_for_unarmed_or_other_buttons()
    {
        // Another action button does not end the sprint.
        Assert.Equal(10f, Speed(Hold(Speeds, InputButtons.Sprint | InputButtons.Reload, 60).State), 1e-3f);
        // No weapon in hand: nothing to shoot, the trigger does not matter.
        var unarmed = Speeds with { Weapons = [], Classes = [new ClassLoadout("test - unarmed", -1, -1, SimConfig.PlaceholderMeleeSteps)] };
        Assert.Equal(10f, Speed(Hold(unarmed, InputButtons.Sprint | InputButtons.Fire, 60).State), 1e-3f);
    }

    [Theory]
    [InlineData(0f, 0f)]       // standing
    [InlineData(1f, 0.5f)]     // hip fire on the move: strafing and forward (the Aim bit changes the speed, not the shot)
    [InlineData(-0.6f, -1f)]   // backing away
    public void Hip_fire_and_aimed_fire_shoot_the_same_shot_and_hit_the_same_targets_also_on_the_move(float moveX, float moveY)
    {
        var c = Speeds with { HealthMax = 1e6f };
        var target = new Vector3(2.5f, 14f, 0f);
        (List<(float, float)> Aims, int Shots, List<string> Hits) Run(InputButtons extra)
        {
            var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(target, MathF.PI)]);
            world.Spawn(1, 0); world.Spawn(2, 1);
            var aims = new List<(float, float)>();
            var hits = new List<string>();
            int shots = 0;
            for (int t = 0; t < 40; t++)
            {
                // The crosshair is on the target's chest wherever the shooter has got to (the client's convergence).
                var eye = Combat.EyePosition(world.Players[1], c);
                var toTarget = new Vector3(target.X, target.Y, (c.BodyBottom + c.BodyTop) / 2f) - eye;
                var input = new PlayerInput((uint)(t + 1), moveX, moveY, 0.1f, InputButtons.Fire | extra, 0.02f).WithShotDirection(toTarget);
                aims.Add(Combat.ShotAim(input, c));
                world.Step(id => id == 1 ? input : new PlayerInput((uint)(t + 1), 0, 0, MathF.PI, InputButtons.None));
                shots += world.LastShots.Count(x => x.Shooter == 1);
                hits.AddRange(world.LastHits.Select(h => $"{h.Shooter}>{h.Target}:{h.Zone}"));
            }
            return (aims, shots, hits);
        }
        var hip = Run(InputButtons.None);
        var aimed = Run(InputButtons.Aim);
        Assert.True(hip.Shots >= 8);
        Assert.Equal(hip.Shots, aimed.Shots);
        // The Aim bit slows the shooter but every shot is aimed at the same crosshair point: same count, same zones.
        Assert.Equal(hip.Shots, hip.Hits.Count);
        Assert.Equal(aimed.Hits, hip.Hits);
        output.WriteLine($"{hip.Shots} shots, {hip.Hits.Count} hits (hip) / {aimed.Hits.Count} (aimed)");
        Assert.Equal(hip.Aims.Count, aimed.Aims.Count);
    }

    // ---------------------------------------------------------------- the sprint rule at its edges

    static SimConfig Armed(SimConfig? c = null) => (c ?? GrenadeTests.Config) with
    {
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1e6f, WeaponSwitchTime = 0.6f,
    };


    [Fact]
    public void Sprint_and_fire_during_a_reaction_do_nothing_and_the_sprint_comes_back_after_it()
    {
        var c = Armed();
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        for (uint t = 1; t <= 45; t++) s = Combat.StepSelf(s, new PlayerInput(t, 0, 1f, 0f, InputButtons.Sprint), c, out _);
        Assert.Equal(10f, Speed(s), 1e-3f);
        int clip = s.Clip;
        s = Reaction.Start(s, c, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.UnitY);   // sim/combat-2: a stun holds (the light kinds do not)
        bool fired = false;
        uint tick = 46;
        for (int k = 0; k < 6 && s.Reaction != ReactionKind.None; k++, tick++)
        {
            s = Combat.StepSelf(s, new PlayerInput(tick, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire), c, out bool f);
            fired |= f;
        }
        Assert.False(fired);
        Assert.Equal(clip, s.Clip);
        // Reaction over, Fire released: Sprint works again (the reaction did not leave the rule stuck).
        for (int k = 0; k < 120; k++, tick++) s = Combat.StepSelf(s, new PlayerInput(tick, 0, 1f, 0f, InputButtons.Sprint), c, out _);
        Assert.Equal(ReactionKind.None, s.Reaction);
        Assert.Equal(10f, Speed(s), 1e-3f);
    }

    [Fact]
    public void Superseded_by_protocol_51_fire_held_while_sprinting_starts_no_charge_and_the_sprint_goes_on()
    {
        // M31 let Fire held during a sprint charge the plasma gun at run speed and fire on the release; sim/no-fire-while-sprinting (protocol 51): no charge, no shot.
        var c = Armed(new SimConfig
        {
            Weapons = [ChargeTests.PlasmaGun], Classes = [new ClassLoadout("test - plasma", 0, -1, [])], HealthMax = 1e6f,
        });
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        uint tick = 1;
        for (; tick <= 40; tick++) s = Combat.StepSelf(s, new PlayerInput(tick, 0, 1f, 0f, InputButtons.Sprint), c, out _);
        Assert.Equal(10f, Speed(s), 1e-3f);
        int shots = 0;
        for (int k = 0; k < 45; k++, tick++)
        {
            s = Combat.StepSelf(s, new PlayerInput(tick, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire), c, out bool f);
            if (f) shots++;
            Assert.False(s.Charging);
            Assert.Equal(10f, Speed(s), 1e-3f);
        }
        // Released with Sprint still held: nothing.
        for (int k = 0; k < 30; k++, tick++)
        {
            s = Combat.StepSelf(s, new PlayerInput(tick, 0, 1f, 0f, InputButtons.Sprint), c, out bool f);
            if (f) shots++;
            Assert.Equal(0, s.PendingShot);
        }
        Assert.Equal(0, shots);
        Assert.Equal(10f, Speed(s), 1e-3f);
    }

    [Fact]
    public void Fire_held_during_a_switch_a_throw_and_a_melee_attack_does_not_cancel_the_sprint_and_shoots_nothing()
    {
        var c = Armed();
        PlayerState Sprinting(SimConfig config)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), config);
            for (uint t = 1; t <= 45; t++) s = Combat.StepSelf(s, new PlayerInput(t, 0, 1f, 0f, InputButtons.Sprint), config, out _);
            return s;
        }
        // Switch: the weapon waits for the switch time, so a held trigger shoots nothing and does not end the sprint.
        var sw = Sprinting(c);
        sw = Combat.StepSelf(sw, new PlayerInput(100, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire | InputButtons.Switch), c, out _);
        Assert.True(sw.SwitchLeft > 0f);
        int otherClip = sw.Clip;
        for (uint t = 101; sw.SwitchLeft > 0.1f; t++)
        {
            sw = Combat.StepSelf(sw, new PlayerInput(t, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire), c, out bool fired);
            Assert.False(fired);
            Assert.Equal(10f, Speed(sw), 1e-3f);
        }
        Assert.Equal(otherClip, sw.Clip);

        // Throw: a grenade press with the trigger held (the trigger does not shoot, the sprint goes on).
        var th = Sprinting(c);
        th = Combat.StepSelf(th, new PlayerInput(100, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire | InputButtons.Grenade), c, out bool thrownFired);
        Assert.True(th.Throwing);
        Assert.False(thrownFired);
        for (uint t = 101; th.Throwing && th.ThrowTime < 0.5f; t++)
        {
            th = Combat.StepSelf(th, new PlayerInput(t, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire), c, out bool fired);
            Assert.False(fired);
            if (th.Throwing) Assert.Equal(10f, Speed(th), 1e-3f);
        }

        // Melee: an attack that moves again from 0.3 s and lets a shot cancel it from 0.9 s: between them Sprint + Fire does
        // neither (the attack is not a ranged state; a shot would end it).
        var knife = new MeleeStep(1.2f, 0.25f, 0.4f, 0.6f, 0.9f, 0.3f, 40f, 20f, 3.5f);
        var mc = c with { Classes = c.Classes.Select(k => k with { Melee = [knife] }).ToArray() };
        var m = Sprinting(mc);
        m = Combat.StepSelf(m, new PlayerInput(100, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire | InputButtons.Melee), mc, out _);
        Assert.Equal(1, m.MeleeStep);
        for (uint t = 101; t < 100 + 21; t++)   // to 0.67 s: past MoveAt, before RangedAt
        {
            m = Combat.StepSelf(m, new PlayerInput(t, 0, 1f, 0f, InputButtons.Sprint | InputButtons.Fire), mc, out bool fired);
            Assert.False(fired);
            Assert.Equal(1, m.MeleeStep);
        }
        Assert.True(Speed(m) > 6.5f + 0.5f, $"moving again in the attack at {Speed(m)} m/s: sprint is still applied");
    }

    // ---------------------------------------------------------------- server and client agree

    [Fact]
    public void Server_and_prediction_agree_over_a_mixed_script_of_firing_sprinting_aiming_melee_throws_and_switches()
    {
        var c = Armed();
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed: 11, clients: 1, config: c);
        PlayerInput Script(int client, uint tick)
        {
            uint phase = tick / 45 % 9, k = tick % 45;
            float yaw = tick * 0.004f;
            return phase switch
            {
                0 => new PlayerInput(tick, 0, 1f, yaw, InputButtons.Fire),
                1 => new PlayerInput(tick, 0, 1f, yaw, InputButtons.Fire | InputButtons.Sprint),
                2 => new PlayerInput(tick, 1f, 0.3f, yaw, InputButtons.Fire | InputButtons.Aim),
                3 => new PlayerInput(tick, 0, 1f, yaw, InputButtons.Sprint),
                4 => new PlayerInput(tick, -0.5f, 1f, yaw, InputButtons.Fire | InputButtons.Sprint | (k == 3 ? InputButtons.Melee : 0)),
                5 => new PlayerInput(tick, 0.4f, 1f, yaw, InputButtons.Fire | InputButtons.Sprint | (k == 2 ? InputButtons.Grenade : 0)),
                6 => new PlayerInput(tick, 0, 1f, yaw, InputButtons.Fire | InputButtons.Sprint | (k == 1 ? InputButtons.Switch : 0)),
                7 => new PlayerInput(tick, 1f, 0f, yaw, InputButtons.Fire | (k < 2 ? InputButtons.Reload : 0)),
                _ => new PlayerInput(tick, 0, 0, yaw, InputButtons.None),
            };
        }
        h.Run(TimeSpan.FromSeconds(14), Script);
        // A quiet two seconds so every input has been applied and the last snapshot has caught up.
        h.Run(TimeSpan.FromSeconds(2), (_, t) => new PlayerInput(t, 0, 0, 0f, InputButtons.None));
        var client = h.Clients[0];
        Assert.Equal(ClientStatus.Connected, client.Status);
        var e = client.CorrectionErrors;
        double p95 = Harness.Percentile(e, 0.95), max = e.Max();
        output.WriteLine($"{e.Count} corrections, p95 {p95:F4} m, max {max:F4} m");
        Assert.True(e.Count >= 250, $"only {e.Count} correction samples");
        Assert.True(p95 < 0.05, $"p95 correction {p95:F4} m");
        Assert.True(max < 0.5, $"max correction {max:F4} m");
        var server = h.Server.World.Players[client.PlayerId];
        var predicted = client.Predicted;
        Assert.Equal((server.Clip, server.Reserve, server.Slot, server.Grenades, server.Alive), (predicted.Clip, predicted.Reserve, predicted.Slot, predicted.Grenades, predicted.Alive));
        Assert.True(Vector3.Distance(server.Position, predicted.Position) < 0.05f);
        Assert.True(server.Clip < c.Weapons[0].ClipSize || server.Reserve < c.Weapons[0].ReserveSize, "the script never fired");
    }

    // ---------------------------------------------------------------- the charge (M29) with the trigger held

    static readonly MeleeStep ChargeLight = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);
    static readonly MeleeStep ChargeHeavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f);

    static SimConfig ChargeConfig() => Speeds with
    {
        Weapons = [new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f)],
        Classes = [new ClassLoadout("marine", 0, -1, [ChargeLight]) { MeleeHeavy = [ChargeHeavy], MeleeRush = RushTests.Spec, Side = Team.SpaceMarines }],
        HealthMax = 1e6f,
    };

    [Theory]
    [InlineData(InputButtons.Fire, false)]
    [InlineData(InputButtons.Fire | InputButtons.Sprint, true)]
    [InlineData(InputButtons.Sprint, true)]
    [InlineData(InputButtons.None, false)]
    public void The_strike_button_with_W_charges_only_with_the_sprint_whatever_the_held_trigger(InputButtons held, bool charges)
    {
        // M31 made the charge need the sprint (so a held trigger made W + F the stun strike); superseded by protocol 39 (W alone), then protocol 49
        // (sim/charge-needs-sprint, owner 2026-10-31): Shift + W at the F press charges; without Shift it is the stun strike; the held trigger changes nothing.
        var c = ChargeConfig();
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        uint tick = 1;
        for (; tick <= 15; tick++) s = Combat.StepSelf(s, new PlayerInput(tick, 0f, 1f, 0f, held), c, out _);
        Assert.Equal((held & InputButtons.Sprint) != 0 ? c.SprintSpeed : c.RunSpeed, Speed(s), 1e-3f);   // protocol 51: the held trigger no longer ends the sprint
        s = Combat.StepSelf(s, new PlayerInput(tick++, 0f, 1f, 0f, held | InputButtons.MeleeHeavy), c, out bool firedOnPress);
        Assert.False(firedOnPress);
        if (charges) Assert.Equal((RushPhase.Rush, 0), (s.Rush, s.MeleeStep));
        else Assert.Equal((RushPhase.None, 1, true), (s.Rush, s.MeleeStep, s.MeleeHeavy));
    }
}
