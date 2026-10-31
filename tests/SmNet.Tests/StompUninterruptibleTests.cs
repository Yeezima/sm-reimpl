using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/stomp-uninterruptible (protocol 51; the owner 2026-11-01: "make it so that devastator/havoc stomp can only be cancelled by a hop move. shooting, aim down
/// sight, or any other action should never interrupt a stomp animation"): once the heavy classes' stomp (<see cref="MeleeStep.InPlace"/>) starts, Fire (held or
/// tapped, the pistol too), Aim, weapon switches, Reload (and the brace), a grenade, light and heavy melee presses, the sprint and the charge leave it running to its
/// end; the hop (Jump from the stomp's BRANCH_Evade) still ends it; afterwards everything works as before. Synthetic attacks and weapons.
/// </summary>
public class StompUninterruptibleTests(ITestOutputHelper output)
{
    /// <summary>The kick (melee): a light attack that chains to the stomp.</summary>
    static readonly MeleeStep Kick = new(1.667f, 0.367f, 0.567f, 1.0f, 0.9f, 1.33f, 30f, 10f, 2f) { StringEnd = true, HeavyNext = 1 };

    /// <summary>
    /// The stomp (stun strike): 1.667 s, blast at 0.767 s, a shot would have ended it from 0.9 s (RangedAt), movement back at 1.333 s, the hop from 0.5 s; a combo
    /// window (1.0 to 1.4 s, ours for the test) through which a melee press would have chained the next attack before the stomp's end.
    /// </summary>
    static readonly MeleeStep Stomp = new(1.6666666f, 0.767f, 1.0f, 1.4f, 0.9f, 1.333f, 0f, 0f, 3.5f)
    {
        InPlace = true, EvadeAt = 0.5f,
        Stomp = new SplashSpec(7f, new DamageSpec(25f, 50f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(7f, 0.15f)], true, true, "target_all"),
    };

    static readonly WeaponSpec HeavyBolter = new("heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 35f, 0.233f, 400, 0, 1.5f, 250f, 250f, 250f, 1f, 1f, 1f, 0.7f, 0.7f)
    {
        AlwaysReady = true, Brace = new BraceSpec(0.1f, 1.8f) { EnterTime = 1.433f, ExitTime = 1.3f },
    };
    static readonly WeaponSpec BoltPistol = new("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 40f, 0.25f, 10, 80, 0.75f, 250f, 30f, 40f, 0.78f, 2.5f, 1f, 0.5f, 0.5f)
    {
        RaiseDelay = 1f / 6f,
    };

    static readonly SimConfig Config = new()
    {
        Weapons = [HeavyBolter, BoltPistol],
        Thrown = [GrenadeTests.Frag],
        Classes =
        [
            new ClassLoadout("devastator", 0, 1, [Kick]) { MeleeHeavy = [Stomp], MeleeRush = RushTests.Spec, Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines },
        ],
        Evades = [EvadeTests.Hop],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, AimRunSpeed = 3f, AimStrafeSpeed = 3f, AimBackSpeed = 2f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 1e6f,
    };

    static PlayerState Fresh() => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Team = Team.SpaceMarines, Class = 0 }, Config);

    static (PlayerState S, SelfEvents E) Step(PlayerState s, InputButtons b, float my = 0f)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), Config, out var e);
        return (next, e);
    }

    /// <summary>A stomp started from rest (F alone): the state on its first tick.</summary>
    static PlayerState Stomping(PlayerState? from = null)
    {
        var s = from ?? Fresh();
        for (int k = 0; k < 5; k++) s = Step(s, InputButtons.None).S;
        s = Step(s, InputButtons.MeleeHeavy).S;
        Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy));
        return s;
    }

    /// <summary>Ticks the stomp lasts with <paramref name="buttons"/> pressed afresh every other tick (and the stick at <paramref name="my"/>); also whether anything fired, aimed, switched, threw or charged.</summary>
    static (int Ticks, bool Fired, bool Aimed, PlayerState Last, PlayerState Before) RunStomp(InputButtons buttons, float my = 0f, bool hold = false)
    {
        var s = Stomping();
        var before = s;
        int ticks = 1;
        bool fired = false, aimed = false;
        for (int k = 1; k < 120 && s.MeleeStep == 1 && s.MeleeHeavy; k++)
        {
            var (n, e) = Step(s, hold || k % 2 == 1 ? buttons : InputButtons.None, my);
            if (n.MeleeStep == 1 && n.MeleeHeavy)
            {
                ticks++;
                fired |= e.Fired;
                aimed |= n.Aiming;
                Assert.Equal((before.Slot, before.Clip, before.Grenades, before.Brace), (n.Slot, n.Clip, n.Grenades, n.Brace));
                Assert.False(n.Throwing);
                Assert.Equal(RushPhase.None, n.Rush);
                Assert.Equal(0, n.EvadeTicks);
            }
            s = n;
        }
        return (ticks, fired, aimed, s, before);
    }

    [Theory]
    [InlineData(InputButtons.Fire, true)]          // held fire: a shot ended the stomp from its RangedAt before
    [InlineData(InputButtons.Fire, false)]         // taps
    [InlineData(InputButtons.Aim | InputButtons.Fire, true)]
    [InlineData(InputButtons.Aim, true)]
    [InlineData(InputButtons.Switch, false)]
    [InlineData(InputButtons.SlotPistol, false)]
    [InlineData(InputButtons.Reload, false)]       // the heavy bolter's brace too
    [InlineData(InputButtons.Grenade, false)]
    [InlineData(InputButtons.Melee, false)]        // a light press in the stomp's combo window chained the next attack before
    [InlineData(InputButtons.MeleeHeavy, false)]
    [InlineData(InputButtons.Sprint, true)]
    [InlineData(InputButtons.Sprint | InputButtons.MeleeHeavy, false)]   // with W: the charge request
    public void No_action_but_the_hop_interrupts_the_stomp(InputButtons buttons, bool hold)
    {
        int full = RunStomp(InputButtons.None).Ticks;
        float my = (buttons & InputButtons.Sprint) != 0 ? 1f : 0f;
        var (ticks, fired, aimed, last, _) = RunStomp(buttons, my, hold);
        output.WriteLine($"{buttons} (hold {hold}): stomp {ticks} ticks (alone {full}), fired {fired}, aimed {aimed}");
        Assert.Equal(full, ticks);   // played to its end
        Assert.False(fired);
        Assert.False(aimed);
        Assert.Equal(51, full);      // the press tick and 1.667 s at 30 Hz
    }

    [Fact]
    public void The_pistol_in_hand_does_not_cancel_the_stomp_either()
    {
        var pistol = Fresh() with { Slot = WeaponSlot.Pistol, ReadyTicks = 300 };
        var s = Stomping(pistol);
        int ticks = 1;
        for (int k = 1; k < 120 && s.MeleeStep == 1; k++)
        {
            var (n, e) = Step(s, k % 2 == 1 ? InputButtons.Fire : InputButtons.None);
            Assert.False(e.Fired, $"pistol shot at stomp tick {k}");
            if (n.MeleeStep == 1) ticks++;
            s = n;
        }
        Assert.Equal(51, ticks);
    }

    [Fact]
    public void The_hop_cancels_the_stomp_from_its_evade_branch()
    {
        // Before the branch (0.5 s = tick 15) Jump does nothing; from it the hop starts in that tick and the stomp ends.
        var s = Stomping();
        for (int k = 1; k < 14; k++)
        {
            s = Step(s, k % 2 == 1 ? InputButtons.Jump : InputButtons.None).S;
            Assert.Equal(1, s.MeleeStep);
            Assert.Equal(0, s.EvadeTicks);
        }
        int hopAt = -1;
        for (int k = 14; k < 50 && hopAt < 0; k++)
        {
            s = Step(s, k % 2 == 1 ? InputButtons.Jump : InputButtons.None).S;
            if (s.EvadeTicks > 0) hopAt = k;
        }
        output.WriteLine($"hop at stomp tick {hopAt}");
        Assert.InRange(hopAt, 15, 17);   // the first press (every other tick) once 0.5 s has played
        Assert.Equal(0, s.MeleeStep);
    }

    [Fact]
    public void Everything_works_again_after_the_stomp()
    {
        var (_, _, _, after, _) = RunStomp(InputButtons.None);
        Assert.Equal(0, after.MeleeStep);
        var s = after;
        for (int k = 0; k < 10; k++) s = Step(s, InputButtons.None).S;
        Assert.True(Step(s, InputButtons.Fire).E.Fired);                 // the always-ready heavy bolter fires at once
        Assert.True(Step(s, InputButtons.Aim).S.Aiming);
        Assert.True(Step(s, InputButtons.Switch).S.SwitchLeft > 0f);
        Assert.True(Step(s, InputButtons.Grenade).S.Throwing);
        Assert.Equal(1, Step(s, InputButtons.Melee).S.MeleeStep);
        Assert.Equal(RushPhase.Rush, Step(s, InputButtons.Sprint | InputButtons.MeleeHeavy, 1f).S.Rush);
        // The kick still chains into the stomp, and a shot still cancels the kick (only the stomp is protected).
        var kick = Step(s, InputButtons.Melee).S;
        for (int k = 0; k < 40 && kick.MeleeStep == 1 && !kick.MeleeHeavy; k++) kick = Step(kick, k == 20 ? InputButtons.MeleeHeavy : InputButtons.None).S;
        Assert.True(kick.MeleeHeavy, "the kick chains into the stomp");
        var kick2 = Step(s, InputButtons.Melee).S;
        bool cancelled = false;
        for (int k = 0; k < 50 && !cancelled; k++) { var (n, e) = Step(kick2, InputButtons.Fire); cancelled = e.Fired && n.MeleeStep == 0; kick2 = n; }
        Assert.True(cancelled, "a shot ends the kick from its RangedAt as before");
    }
}
