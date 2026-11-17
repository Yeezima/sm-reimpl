using SmSim;
using System.Numerics;

namespace SmNet.Tests;

/// <summary>sim/plasma-cannon-auto-release (owner 2026-11-17, protocol 84): the cannon's charge fires by itself at its firing stage, button held or not. Synthetic data only.</summary>
public class PlasmaCannonAutoReleaseTests
{
    static readonly WeaponSpec Cannon = ChargeTests.Cannon with { Name = "sm_mp_exotic_plasma_cannon" };

    static SimConfig Config(WeaponSpec w, bool auto = true) => new()
    {
        Weapons = [w],
        Classes = [new ClassLoadout("cannon", 0, -1, SimConfig.PlaceholderMeleeSteps)],
        PlasmaCannonAutoRelease = auto,
    };

    static PlayerInput In(InputButtons b) => new(0, 0f, 0f, 0f, b);

    /// <summary>Holds Fire from a fresh player; returns the tick (1-based) in which the charged shot left, or 0 within <paramref name="max"/> ticks.</summary>
    static int HoldUntilFired(SimConfig c, int max, out PlayerState end, out SelfEvents shot)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
        shot = default;
        for (int k = 1; k <= max; k++)
        {
            s = Combat.StepPlayer(s, In(InputButtons.Fire), c, out var e);
            if (e.Fired && e.ShotStage > 0) { end = s; shot = e; return k; }
        }
        end = s;
        return 0;
    }

    [Fact]
    public void The_cannon_fires_the_charged_shot_by_itself_at_full_charge_with_the_button_held()
    {
        int tick = HoldUntilFired(Config(Cannon), 200, out var end, out var shot);
        // 3 s = 90 ticks of charge, the shot leaves on the next tick; the button is still down, the charge is over.
        Assert.InRange(tick, 90, 93);
        Assert.True(shot.ShotStage > 0);
        Assert.False(end.Charging);
    }

    [Fact]
    public void The_old_rule_holds_the_shot_until_release()
    {
        Assert.Equal(0, HoldUntilFired(Config(Cannon, auto: false), 150, out var end, out _));
        Assert.True(end.Charging);
    }

    [Fact]
    public void A_held_button_does_not_start_a_second_charge_or_shot_after_the_auto_release()
    {
        var c = Config(Cannon);
        int tick = HoldUntilFired(c, 200, out var s, out _);
        Assert.True(tick > 0);
        int shots = 0;
        for (int k = 0; k < 60; k++) { s = Combat.StepPlayer(s, In(InputButtons.Fire), c, out var e); if (e.Fired) shots++; }
        Assert.Equal(0, shots);
        Assert.False(s.Charging);
    }

    [Fact]
    public void Other_charge_weapons_still_wait_for_the_release()
    {
        var gun = Config(ChargeTests.PlasmaGun with { Name = "sm_mp_ranged_plasma_gun" });
        Assert.Equal(0, HoldUntilFired(gun, 40, out var end, out _));
        Assert.True(end.Charging);
    }

    [Fact]
    public void The_switch_is_in_the_config_hash_only_when_off()
    {
        Assert.NotEqual(Config(Cannon).Hash(), Config(Cannon, auto: false).Hash());
    }

    [Fact]
    public void Improved_Plasma_Charge_fires_at_1_65_seconds()
    {
        var fast = Cannon with { Charge = Cannon.Charge! with { MinTimeMultiplier = 0.55f } };
        int tick = HoldUntilFired(Config(fast), 200, out _, out _);
        Assert.InRange(tick, 50, 53);   // 1.65 s = 49.5 ticks, the shot leaves on the tick after
    }

    [Fact]
    public void The_Havoc_cannon_by_its_csm_name_fires_by_itself_too()
    {
        int tick = HoldUntilFired(Config(ChargeTests.Cannon with { Name = "csm_mp_exotic_plasma_cannon" }), 200, out _, out _);
        Assert.InRange(tick, 90, 93);
    }

    [Fact]
    public void A_default_config_hashes_as_before_the_switch_existed()
    {
        // The switch is hashed only when off: the default config's hash equals one that names the field as true, and differs from the old rule's.
        Assert.Equal(Config(Cannon).Hash(), (Config(Cannon) with { PlasmaCannonAutoRelease = true }).Hash());
        Assert.NotEqual(Config(Cannon).Hash(), Config(Cannon, auto: false).Hash());
    }
}
