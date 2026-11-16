using System.Numerics;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// feel/melee-cooldown (protocol 53, classic PvP), feel/musou-melee-spam (Musou Mode) and feel/charge-no-cancel (all modes), owner 2026-11-02.
/// Synthetic timings half a tick off the 30 Hz grid: hit at tick 6.5, firing from 8.5 (before the combo window), window 12.5 to 18.5, attack over at 30.5.
/// </summary>
public class MeleeCooldownTests
{
    static float T(float ticks) => ticks / 30f;
    static MeleeStep Step() => new(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(8.5f), T(21.5f), 10f, 5f, 3.5f);
    static readonly SimConfig Classic = new() { MeleeSteps = [Step(), Step(), Step()], MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f };
    static readonly SimConfig Musou = Classic with { Extras = ContentExtras.Musou };

    sealed class Run(SimConfig c)
    {
        public readonly List<PlayerState> States = [];
        public readonly List<int> Hits = [];
        public PlayerState State = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        public Run Tick(InputButtons b = InputButtons.None)
        {
            State = Combat.StepSelf(State, new PlayerInput((uint)States.Count, 0f, 0f, 0f, b), c, out _, out int hit);
            States.Add(State); Hits.Add(hit);
            return this;
        }
        public Run Ticks(int n) { for (int i = 0; i < n; i++) Tick(); return this; }
    }

    /// <summary>Melee at tick 0, fire at tick 9 (ends the attack before its combo window), then melee presses at ticks 10 and 12 and again at 14.</summary>
    static Run FireCancelThenPresses(SimConfig c)
    {
        var r = new Run(c).Tick(InputButtons.Melee).Ticks(8).Tick(InputButtons.Fire);
        return r.Tick(InputButtons.Melee).Tick().Tick(InputButtons.Melee).Tick().Tick(InputButtons.Melee).Ticks(2);
    }

    [Fact]
    public void Classic_a_shot_cancelling_an_attack_does_not_free_a_new_one_before_the_combo_window()
    {
        var r = FireCancelThenPresses(Classic);
        Assert.Equal(1, r.States[0].MeleeStep);
        Assert.Equal(0, r.States[9].MeleeStep);                                          // the shot ended the attack
        Assert.All(new[] { 10, 11, 12 }, i => Assert.Equal(0, r.States[i].MeleeStep));   // presses inside the lock start nothing
        Assert.True(r.States[10].MeleeLock > 0f && r.States[12].MeleeLock > 0f);
        Assert.Equal(0f, r.States[13].MeleeLock);                                        // the combo window's time has passed
        Assert.Equal(1, r.States[14].MeleeStep);                                         // a press starts an attack again
    }

    [Fact]
    public void Musou_has_no_melee_lock_so_the_same_presses_start_at_once()
    {
        var r = FireCancelThenPresses(Musou);
        Assert.True(r.States[10].MeleeStep > 0);
        Assert.All(r.States, s => Assert.Equal(0f, s.MeleeLock));
    }

    [Fact]
    public void Classic_a_normal_combo_in_its_window_is_unchanged()
    {
        var r = new Run(Classic).Tick(InputButtons.Melee).Ticks(11).Tick(InputButtons.Melee).Ticks(3);   // a press at tick 12 is buffered; step 2 from tick 13
        Assert.Equal(1, r.States[12].MeleeStep);
        Assert.Equal(2, r.States[13].MeleeStep);
        var late = new Run(Classic).Tick(InputButtons.Melee).Ticks(40).Tick(InputButtons.Melee);
        Assert.Equal(0f, late.States[39].MeleeLock);
        Assert.Equal(1, late.State.MeleeStep);                                           // after the attack ended on its own, step 1 again
    }

    [Fact]
    public void Classic_an_attack_ended_by_any_other_means_leaves_the_lock_too()
    {
        var r = new Run(Classic).Tick(InputButtons.Melee).Ticks(2);
        r.State = r.State with { MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false };   // however it ended (a roll, a jump, a hit): the lock is separate state
        r.Tick().Tick(InputButtons.Melee);
        Assert.Equal(0, r.State.MeleeStep);
    }

    [Fact]
    public void Musou_a_light_press_after_the_hit_restarts_the_swing_at_once_and_before_the_hit_it_queues()
    {
        var r = new Run(Musou).Tick(InputButtons.Melee).Ticks(7).Tick(InputButtons.Melee);   // tick 8: the hit (6.5) has landed
        Assert.NotEqual(r.States[7].MeleeStep, r.State.MeleeStep);                            // a different swing of the chain
        Assert.Equal((0f, false), (r.State.MeleeTime, r.State.MeleeQueued));
        r.Ticks(8);
        Assert.Equal(2, r.Hits.Count(h => h > 0));                                            // the restarted swing hits again
        var early = new Run(Musou).Tick(InputButtons.Melee).Ticks(2).Tick(InputButtons.Melee);   // tick 3: before the hit
        Assert.Equal((early.States[2].MeleeStep, true), (early.State.MeleeStep, early.State.MeleeQueued));
        Assert.Equal(T(3), early.State.MeleeTime, 4);
        var classic = new Run(Classic).Tick(InputButtons.Melee).Ticks(7).Tick(InputButtons.Melee);
        Assert.Equal((1, true), (classic.State.MeleeStep, classic.State.MeleeQueued));        // classic: buffered for the next step, not a restart
        Assert.Equal(T(8), classic.State.MeleeTime, 4);
    }

    [Fact]
    public void Musou_spammed_swings_use_the_whole_light_chain_and_never_repeat_one_at_once()
    {
        var r = new Run(Musou);
        var swings = new List<int>();
        for (int k = 0; k < 40; k++)
        {
            r.Tick(InputButtons.Melee).Ticks(7);
            if (swings.Count == 0 || swings[^1] != r.State.MeleeStep) swings.Add(r.State.MeleeStep);
            else Assert.Fail($"swing {k} repeated step {r.State.MeleeStep}");
        }
        Assert.Equal([1, 2, 3], swings.Distinct().Order());
    }

    [Fact]
    public void Musou_a_swing_never_picks_an_in_place_stomp_and_one_step_weapons_keep_it()
    {
        var light = new[] { Step(), Step() with { InPlace = true }, Step() };
        for (uint tick = 0; tick < 200; tick++) Assert.NotEqual(2, MeleeChain.MusouSwing(light, 1, tick, 3));
        Assert.Equal(1, MeleeChain.MusouSwing([Step()], 1, 5, 3));
        Assert.Equal(1, MeleeChain.MusouSwing([Step()], 0, 5, 3));
    }

    [Fact]
    public void Musou_the_swing_is_the_same_for_the_same_tick_and_player()
    {
        var light = new[] { Step(), Step(), Step() };
        for (uint tick = 0; tick < 50; tick++) Assert.Equal(MeleeChain.MusouSwing(light, 1, tick, 4), MeleeChain.MusouSwing(light, 1, tick, 4));
    }

    [Fact]
    public void Classic_the_first_swing_is_always_the_first_of_the_chain()
    {
        for (int wait = 0; wait < 30; wait++) Assert.Equal(1, new Run(Classic).Ticks(wait).Tick(InputButtons.Melee).State.MeleeStep);
    }

    // ---------------------------------------------------------------- feel/charge-no-cancel

    [Theory]
    [InlineData(0f, -1f, false)]   // S, with the restore switch --old-charge-cancel (charge-cancel, protocol 66: S ends it otherwise; ChargeCancelTests)
    [InlineData(1f, 0f, true)]     // D
    [InlineData(-1f, 0f, true)]    // A
    [InlineData(1f, 1f, true)]     // W + D
    public void No_movement_key_ends_the_bull_rush_before_its_clip_ends(float moveX, float moveY, bool backCancel)
    {
        var c = RushTests.Config with { ChargeBackCancel = backCancel };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.MeleeHeavy | InputButtons.Sprint), c, out _);
        Assert.Equal(RushPhase.Rush, s.Rush);
        for (int k = 1; k < 26; k++)   // the clip's stick branch is at 0.42 s (13 ticks) and its end at 1.0 s (30)
        {
            s = Combat.StepPlayer(s, new PlayerInput((uint)k, moveX, moveY, 0f, InputButtons.None), c, out _);
            Assert.Equal(RushPhase.Rush, s.Rush);
        }
    }
}
