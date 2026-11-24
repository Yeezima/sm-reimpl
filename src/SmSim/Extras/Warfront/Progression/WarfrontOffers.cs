namespace SmSim.Extras.Warfront.Progression;

/// <summary>
/// The card offer (owner 2026-11-18: "make the level up cards in warfront mode always show all available options"). Every offer lists all five stat cards in the fixed order (slots 1 to 5). The +1 Retainer
/// card is gone from level-up selection (owner 2026-11-23, card 2: "remove the retainer cards from level up selection"; every player always has two retainers), so no offer holds it and the stat cards fill the
/// slots. Nothing is drawn, so the same pick index always offers the same cards. (Before 2026-11-23: +1 Retainer as slot 6 while eligible; before 2026-11-18: three random distinct stat cards and a fourth slot.)
/// </summary>
public static class WarfrontOffers
{
    /// <summary>Formerly whether +1 Retainer was offered (roster below 9, not the Dreadnought). Always false since 2026-11-23; the arguments are kept for the call sites.</summary>
    public static bool RetainerEligible(int rosterSize, bool dreadnought) => false;

    /// <summary>The offer: all stat cards. The seed arguments and <paramref name="retainerEligible"/> are kept for the call sites and are no longer used.</summary>
    public static WarfrontCard[] Roll(ulong runSeed, ulong playerKey, int pickIndex, bool retainerEligible)
    {
        int stats = WarfrontProgressionRules.StatCards.Length;
        var offer = new WarfrontCard[stats];
        for (int i = 0; i < stats; i++) offer[i] = WarfrontProgressionRules.StatCards[i];
        return offer;
    }
}
