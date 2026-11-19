namespace SmSim.Extras.Warfront.Progression;

/// <summary>
/// The card offer (owner 2026-11-18: "make the level up cards in warfront mode always show all available options - squad capable only see retainer +1 options as an exception"). Every offer lists all five
/// stat cards in the fixed order (slots 1 to 5), then +1 Retainer as slot 6 while eligible (roster below 9, class not the Dreadnought: only a squad-capable class is ever offered it); otherwise five cards.
/// Nothing is drawn, so the same (pick index, eligibility) always offers the same cards. (Before: three random distinct stat cards and a fourth slot of +1 Retainer or a fourth stat card.)
/// </summary>
public static class WarfrontOffers
{
    public static bool RetainerEligible(int rosterSize, bool dreadnought) => !dreadnought && rosterSize < WarfrontProgressionRules.RosterMax;

    /// <summary>The offer: all stat cards, then the Retainer when <paramref name="retainerEligible"/>. The seed arguments are kept for the call sites and are no longer used.</summary>
    public static WarfrontCard[] Roll(ulong runSeed, ulong playerKey, int pickIndex, bool retainerEligible)
    {
        int stats = WarfrontProgressionRules.StatCards.Length;
        var offer = new WarfrontCard[retainerEligible ? stats + 1 : stats];
        for (int i = 0; i < stats; i++) offer[i] = WarfrontProgressionRules.StatCards[i];
        if (retainerEligible) offer[stats] = WarfrontCard.Retainer;
        return offer;
    }
}
