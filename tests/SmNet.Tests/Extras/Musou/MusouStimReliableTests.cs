using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests;

/// <summary>
/// Owner 2026-11-12: "Pressing the equipment use hotkey with stims equipped doesn't always use the stims. Ensure it's more reliably working the first time you press the button."
/// The cause (found with a scripted sweep of presses over every action of the sim): the use key is a bufferable press kept only 0.3 s (9 ticks), so a press made more than 0.3 s before a swing, a charge or a
/// recovery ends is dropped unused; and a hit reaction that starts before the stim's release (0.3 s into the use) cancels the use ("before its release nothing is spent") with the press consumed. In Musou Mode
/// (<see cref="SimConfig.StimReliable"/>) the press is kept 0.5 s and no hit starts a reaction before the release; Classic PvP keeps both rules. Also the owner's "increase all melee attacks cleave distance by 20%"
/// (<see cref="TfwCombatRules.PlayerCleaveReachScale"/> 0.6 to 0.72), pinned here against the old scale.
/// </summary>
public class MusouStimReliableTests
{
    static SimConfig Make(bool musou) => StimTests.Config with
    {
        InputBufferWindow = 0.3f,
        Extras = musou ? ContentExtras.Musou : ContentExtras.None,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);

    /// <summary>A light swing at tick 0 (it ends at tick 30), the use key pressed for one tick at <paramref name="press"/>; whether a use began by tick 90, and the tick it began.</summary>
    static (bool Used, int At) PressDuringSwing(SimConfig c, int press)
    {
        var s = Fresh(c);
        for (int k = 0; k < 90; k++)
        {
            var b = k == 0 ? InputButtons.Melee : InputButtons.None;
            if (k == press) b |= InputButtons.Grenade;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b), c, out _);
            if (s.Throwing) return (true, k);
        }
        return (false, -1);
    }

    [Fact]
    public void The_old_stim_use_switch_brings_back_the_nine_tick_press_in_both_modes()
    {
        foreach (bool musou in new[] { false, true })
        {
            var old = Make(musou) with { StimReliable = false };
            Assert.Equal((false, -1), PressDuringSwing(old, 16));   // 14 ticks before the swing ends: dropped, as before
            Assert.Equal((true, 30), PressDuringSwing(old, 21));    // 9 ticks before: kept, used the tick the swing ends
        }
    }

    [Fact]
    public void The_press_is_kept_fifteen_ticks_and_uses_the_stim_when_the_swing_ends_in_Musou_Mode_and_Classic_PvP()
    {
        foreach (bool musou in new[] { false, true })
        {
            var c = Make(musou);
            Assert.True(c.StimReliable);
            Assert.Equal((true, 30), PressDuringSwing(c, 16));
            Assert.Equal((true, 30), PressDuringSwing(c, 15));
            Assert.Equal((false, -1), PressDuringSwing(c, 14));   // past 0.5 s the press lapses (the wire carries at most 15 ticks)
        }
    }

    [Fact]
    public void A_hit_before_the_stims_release_starts_no_reaction_unless_the_old_switch_is_on()
    {
        foreach (bool musou in new[] { false, true })
            foreach (bool reliable in new[] { false, true })
            {
                var c = Make(musou) with { StimReliable = reliable };
                var s = Fresh(c);
                for (int k = 0; k < 3 && !s.Throwing; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), c, out _);
                Assert.True(s.Throwing);
                Assert.True(s.ThrowTime < 0.3f);   // before the release (0.3 s)
                Assert.Equal(!reliable, Reaction.CanReact(s, c));
            }
    }

    [Fact]
    public void After_the_release_the_use_is_over_with_the_stims_own_immunity_and_a_frag_throw_is_never_armoured()
    {
        var c = Make(true);
        var s = Fresh(c) with { Equipment = 1, Grenades = 1 };   // the frag grenade (index 1 of the config's items)
        Assert.NotNull(Loadouts.Equipment(c, s));
        s = s with { Throwing = true, ThrowTime = 0.05f };
        Assert.False(Stims.WindUp(c, s));   // not a stim
        Assert.True(Reaction.CanReact(s, c));
    }

    [Fact]
    public void The_players_cleave_reach_in_the_war_is_twelve_tenths_of_the_pass_before()
    {
        var war = TfwCombatRules.Default;
        Assert.Equal(0.72f, war.PlayerCleaveReachScale);
        Assert.Equal(0.6f * 1.2f, war.PlayerCleaveReachScale, 5);
        // The angle, the target cap and the war's own scale are untouched.
        Assert.Equal(1.3f, war.CleaveReachScale);
        Assert.Equal(120f, war.CleaveHalfAngleDegrees);
        Assert.Equal(SmSim.Extras.Musou.MusouSim.Capacity, war.CleaveTargets);   // owner 2026-11-14: no cap
        Assert.Equal(15f, SmSim.Extras.Musou.Arsenal.SpearRules.ReachMetres);   // the spear's own 15 m (it goes through the scale: 10.8 m)
    }
}
