using SmClient.Looks;

namespace SmNet.Tests;

/// <summary>Ork O7a: a race change at the respawn never leaves the old rig standing in (<see cref="SwapRules.DropWrongRig"/>).</summary>
public class OrkRaceSwapTests
{
    const string MarineKey = "marine|tactical|x", OrkKey = "ork:boy_ranged";

    [Fact]
    public void A_marine_awaiting_an_Ork_look_is_replaced_by_the_placeholder_at_once()
    {
        Assert.True(SwapRules.DropWrongRig(currentIsOrkRig: false, wantsOrk: true, MarineKey, alive: true, deathPending: false));
    }

    [Fact]
    public void An_Ork_awaiting_a_marine_look_is_replaced_even_when_it_is_the_only_model()
    {
        Assert.True(SwapRules.DropWrongRig(currentIsOrkRig: true, wantsOrk: false, OrkKey, alive: true, deathPending: false));
    }

    [Fact]
    public void The_placeholder_stays_while_an_Ork_is_awaited_and_matching_rigs_are_kept()
    {
        Assert.False(SwapRules.DropWrongRig(false, true, LookKeys.Placeholder, true, false));   // already the stand-in
        Assert.False(SwapRules.DropWrongRig(true, true, OrkKey, true, false));                  // an Ork stays an Ork (a look change is a swap, not a drop)
        Assert.False(SwapRules.DropWrongRig(false, false, MarineKey, true, false));
    }

    [Fact]
    public void Corpses_dying_players_and_characters_without_a_model_are_left_alone()
    {
        Assert.False(SwapRules.DropWrongRig(false, true, MarineKey, alive: false, deathPending: false));
        Assert.False(SwapRules.DropWrongRig(true, false, OrkKey, alive: true, deathPending: true));
        Assert.False(SwapRules.DropWrongRig(false, true, null, true, false));
    }
}
