using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// charge-melee-chain (protocol 70; the owner 2026-11-12): a light melee press during a charge cancels it into the swing on that tick (any tick, its last included); a charge cannot start while one runs; a melee
/// attack between two charges lifts the 1 s spacing, so charge > melee > charge chains; in Musou Mode light attacks have no cooldown or delay (<see cref="SimConfig.MeleeSpam"/>). Restore switches
/// <see cref="SimConfig.ChargeMeleeChain"/> (false) and <see cref="SimConfig.MusouMeleeCooldown"/> (true). Synthetic clips (<see cref="RushTests"/>): the light lasts 1 s with its hit at 0.2 s; the rush ends at 1.0 s.
/// </summary>
public class ChargeMeleeChainTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = RushTests.Config;
    static readonly SimConfig Musou = Config with { Extras = ContentExtras.Musou };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
    static PlayerInput Press(InputButtons b, float y = 0f) => new(0, 0f, y, 0f, b);
    static readonly PlayerInput Idle = Press(InputButtons.None);
    static readonly PlayerInput Run = Press(InputButtons.Sprint, 1f);
    static readonly PlayerInput Charge = Press(InputButtons.Sprint | InputButtons.MeleeHeavy, 1f);
    static readonly PlayerInput Light = Press(InputButtons.Sprint | InputButtons.Melee, 1f);
    static PlayerState Step(PlayerState s, PlayerInput i, SimConfig c) => Combat.StepPlayer(s, i, c, out _);

    static PlayerState Charging(SimConfig c, int extraTicks)
    {
        var s = Step(Fresh(c), Charge, c);
        Assert.Equal(RushPhase.Rush, s.Rush);
        for (int k = 0; k < extraTicks; k++) s = Step(s, Run, c);
        return s;
    }

    [Fact]
    public void A_light_press_at_any_tick_of_a_charge_cancels_it_into_the_swing_on_that_tick()
    {
        for (int k = 0; k < 40; k++)
        {
            var before = Charging(Config, k);
            var after = Step(before, Light, Config);
            Assert.Equal(1, after.MeleeStep);   // the swing began this tick, whether the charge was mid-clip, on its last tick or already over
            Assert.Equal(RushPhase.None, after.Rush);
            Assert.Equal(MathF.Max(0f, Config.Classes[0].Melee![0].ComboOpen), after.MeleeLock, 3);   // only the swing's own combo-window wait: no extra cancel wait (protocol 54) from the charge's clip
        }
    }

    [Fact]
    public void The_cancelling_tick_resolves_no_charge_contact()
    {
        var before = Charging(Config, 3);
        Step(before, Light, Config);
        var s = Fresh(Config);
        s = Step(s, Charge, Config);
        var w = Combat.StepPlayer(s, Light, Config, out var ev);
        Assert.Equal(RushPhase.None, w.Rush);   // the charge is over before the tick's contact test: nothing for the world to resolve as a charge hit
        Assert.Equal(0, w.RushTicks);
    }

    [Fact]
    public void A_charge_cannot_start_while_one_is_running()
    {
        var s = Charging(Config, 2);
        int ticks = s.RushTicks;
        var again = Step(s, Charge, Config);
        Assert.Equal(RushPhase.Rush, again.Rush);
        Assert.Equal(ticks + 1, again.RushTicks);   // it ran on; no restart at tick 1
    }

    [Fact]
    public void Charge_melee_charge_melee_chains_five_times_with_no_wait()
    {
        var s = Fresh(Config);
        for (int round = 0; round < 5; round++)
        {
            s = Step(s, Charge, Config);
            Assert.Equal(RushPhase.Rush, s.Rush);   // every charge starts on its press tick, the spacing never in the way
            s = Step(s, Run, Config);
            s = Step(s, Idle, Config);
            s = Step(s, Light, Config);
            Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
            Assert.Equal(0, s.RushLock);
            int idle = 0;
            while (s.MeleeTime + 1e-5f < Config.Classes[0].Melee![0].HitTime) { s = Step(s, Idle, Config); idle++; }
            output.WriteLine($"round {round}: {idle} idle ticks until the hit landed");
            Assert.True(idle <= 8);
            s = Step(s, Idle, Config);
        }
    }

    [Fact]
    public void The_spacing_still_holds_between_two_charges_with_nothing_in_between()
    {
        var s = Charging(Config, 1);
        s = Step(s, Press(InputButtons.None, -1f), Config);   // S: the instant cancel
        Assert.Equal(RushPhase.None, s.Rush);
        for (int k = 0; k < 20; k++)
        {
            s = Step(s, Idle, Config);
            Assert.NotEqual(RushPhase.Rush, Step(s, Charge, Config).Rush);   // within 1 s: refused
        }
        for (int k = 0; k < 15; k++) s = Step(s, Idle, Config);
        Assert.Equal(RushPhase.Rush, Step(s, Charge, Config).Rush);   // past it: allowed
    }

    [Fact]
    public void A_melee_attack_between_two_charges_lifts_the_spacing_but_the_restore_switch_keeps_it()
    {
        var old = Config with { ChargeMeleeChain = false };
        Assert.NotEqual(Config.Hash(), old.Hash());
        var s = Step(Charging(old, 1), Press(InputButtons.None, -1f), old);   // charge, S cancel, then a swing
        s = Step(s, Press(InputButtons.Melee), old);
        Assert.True(s.RushLock > 0);   // protocol 69: only the clock
        var on = Step(Step(Charging(Config, 1), Press(InputButtons.None, -1f), Config), Press(InputButtons.Melee), Config);
        Assert.Equal(0, on.RushLock);
    }

    [Fact]
    public void The_stomp_is_never_cut_short_by_a_charge()
    {
        var s = Fresh(Config);
        Assert.False(ChargeCancel.CanChargeFromMelee(Config, s));   // no attack under way
    }

    // ---------------------------------------------------------------- Musou melee spam

    static (int Starts, int Ticks) SwingStarts(SimConfig c, int ticks)
    {
        var s = Fresh(c);
        int starts = 0; float lastTime = 0f; int lastStep = 0;
        for (int k = 0; k < ticks; k++)
        {
            s = Step(s, k % 2 == 0 ? Press(InputButtons.Melee) : Idle, c);
            if (s.MeleeStep > 0 && (lastStep == 0 || s.MeleeTime < lastTime)) starts++;
            (lastStep, lastTime) = (s.MeleeStep, s.MeleeTime);
        }
        return (starts, ticks);
    }

    [Fact]
    public void Musou_light_attacks_can_be_spammed_but_Classic_keeps_its_timing()
    {
        var classic = SwingStarts(Config, 90).Starts;
        var spam = SwingStarts(Musou, 90).Starts;
        var kept = SwingStarts(Musou with { MusouMeleeCooldown = true }, 90).Starts;
        output.WriteLine($"swing starts in 3 s: classic {classic}, musou spam {spam}, musou with --musou-melee-cooldown {kept}");
        Assert.True(spam > classic + 3);
        Assert.True(spam >= 90 / 2 / 4);   // one about every hit time (0.2 s) plus the press edge, never idle
        Assert.True(kept <= spam);   // the restore switch: the earlier Musou rule (it already restarted a swing after its hit; only the early press differs)
        Assert.NotEqual(Musou.Hash(), (Musou with { MusouMeleeCooldown = true }).Hash());
        Assert.Equal(Config.Hash(), (Config with { MusouMeleeCooldown = true }).Hash());   // Classic: the switch is not hashed
    }

    [Fact]
    public void A_Musou_press_after_the_hit_swings_at_once_and_one_before_it_swings_on_the_hit_tick()
    {
        var s = Step(Fresh(Musou), Press(InputButtons.Melee), Musou);
        var hit = Musou.Classes[0].Melee![0].HitTime;
        s = Step(s, Idle, Musou);
        var early = Step(s, Press(InputButtons.Melee), Musou);   // before the hit: queued, the swing runs on
        Assert.True(early.MeleeQueued);
        // spam: the queued swing starts on the tick the hit lands; with --musou-melee-cooldown it waits for the combo window
        var restoreSwitch = Musou with { MusouMeleeCooldown = true };
        var r = Step(Step(Step(Fresh(restoreSwitch), Press(InputButtons.Melee), restoreSwitch), Idle, restoreSwitch), Press(InputButtons.Melee), restoreSwitch);
        var q = early;
        for (int k = 0; k < 20 && r.MeleeTime + 1e-5f < hit; k++) { q = Step(q, Idle, Musou); r = Step(r, Idle, restoreSwitch); }
        Assert.True(q.MeleeTime < hit);   // restarted at the hit
        Assert.True(r.MeleeTime >= hit);   // no restart yet
        while (s.MeleeTime + 1e-5f < hit) s = Step(s, Idle, Musou);
        var late = Step(s, Press(InputButtons.Melee), Musou);
        Assert.True(late.MeleeTime < s.MeleeTime);   // restarted this tick
        var classic = Fresh(Config);
        classic = Step(classic, Press(InputButtons.Melee), Config);
        while (classic.MeleeTime + 1e-5f < hit) classic = Step(classic, Idle, Config);
        Assert.True(Step(classic, Press(InputButtons.Melee), Config).MeleeTime > classic.MeleeTime);   // Classic: no restart
    }
}
