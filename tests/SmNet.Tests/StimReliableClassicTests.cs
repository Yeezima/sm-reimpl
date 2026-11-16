using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// stims-classic (owner 2026-11-12, "Classic too"): the reliable stim use (the use key's press kept 15 ticks, no hit reaction before the stim's release) applies in Classic PvP (no added content) as it does in
/// Musou Mode; <see cref="SimConfig.StimReliable"/> false (<c>--old-stim-use</c>) restores the 9-tick press and the hit cancel. Musou's own pins: MusouStimReliableTests.
/// </summary>
public class StimReliableClassicTests
{
    static readonly SimConfig Classic = StimTests.Config with { InputBufferWindow = 0.3f, Extras = ContentExtras.None };
    static readonly SimConfig Old = Classic with { StimReliable = false };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);

    /// <summary>A light swing at tick 0 (it ends at tick 30), the use key pressed for one tick at <paramref name="press"/>; the tick a use began by tick 90, or -1.</summary>
    static int UseTick(SimConfig c, int press)
    {
        var s = Fresh(c);
        for (int k = 0; k < 90; k++)
        {
            var b = k == 0 ? InputButtons.Melee : InputButtons.None;
            if (k == press) b |= InputButtons.Grenade;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b), c, out _);
            if (s.Throwing) return k;
        }
        return -1;
    }

    [Fact]
    public void A_Classic_press_made_fifteen_ticks_before_the_swing_ends_uses_the_stim()
    {
        Assert.False(Classic.Extras.HasFlag(ContentExtras.Musou));
        Assert.Equal(30, UseTick(Classic, 15));
        Assert.Equal(30, UseTick(Classic, 16));
        Assert.Equal(-1, UseTick(Classic, 14));   // past 0.5 s it lapses
    }

    [Fact]
    public void The_old_switch_restores_the_nine_tick_press()
    {
        Assert.Equal(-1, UseTick(Old, 16));
        Assert.Equal(30, UseTick(Old, 21));
    }

    [Fact]
    public void A_hit_during_the_wind_up_starts_no_reaction_but_the_damage_is_still_applied()
    {
        var s = Fresh(Classic);
        for (int k = 0; k < 3 && !s.Throwing; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), Classic, out _);
        Assert.True(s.Throwing);
        Assert.True(s.ThrowTime < 0.3f);
        Assert.True(Stims.WindUp(Classic, s));
        Assert.False(Reaction.CanReact(s, Classic));
        var (after, shield, health) = Combat.ApplyDamage(s, 10f, HitZone.Body, Classic);
        Assert.True(shield + health > 0f);
        Assert.True(after.Health + after.Shield < s.Health + s.Shield);
        Assert.True(after.Throwing);   // the use goes on

        // Old behaviour: the same moment can be cancelled by a hit.
        Assert.True(Reaction.CanReact(s, Old));
    }

    [Fact]
    public void After_the_release_the_ordinary_reaction_rules_apply_again()
    {
        var s = Fresh(Classic);
        for (int k = 0; k < 3 && !s.Throwing; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), Classic, out _);
        var stim = Loadouts.Equipment(Classic, s)!.Stim!;
        Assert.False(Stims.WindUp(Classic, s with { ThrowTime = stim.UseRelease + 0.01f }));
    }

    [Fact]
    public void The_switch_is_in_the_config_hash_only_when_on()
    {
        Assert.NotEqual(Classic.Hash(), Old.Hash());
        Assert.Equal(Classic.Hash(), (Classic with { StimReliable = true }).Hash());
    }
}
