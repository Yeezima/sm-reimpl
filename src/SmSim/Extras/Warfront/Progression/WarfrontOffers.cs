namespace SmSim.Extras.Warfront.Progression;

/// <summary>
/// The card offer (plan section 5). Slots 1 to 3: three distinct stat types out of five. Slot 4: +1 Retainer while eligible (roster below 9, class not the Dreadnought), otherwise a fourth distinct stat type.
/// No duplicates in an offer. Deterministic from (run seed, player key, pick index) and the eligibility: the same pick index always offers the same cards, so a restart or a reopen changes nothing.
/// </summary>
public static class WarfrontOffers
{
    public static bool RetainerEligible(int rosterSize, bool dreadnought) => !dreadnought && rosterSize < WarfrontProgressionRules.RosterMax;

    public static WarfrontCard[] Roll(ulong runSeed, ulong playerKey, int pickIndex, bool retainerEligible)
    {
        var pool = (WarfrontCard[])WarfrontProgressionRules.StatCards.Clone();
        ulong state = Mix(Mix(Mix(runSeed ^ 0x57464F4646525400UL) ^ playerKey) ^ (ulong)(uint)pickIndex);
        // Partial Fisher-Yates over the five stat types.
        for (int i = 0; i < pool.Length - 1; i++)
        {
            state = Mix(state);
            int j = i + (int)(state % (ulong)(pool.Length - i));
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var offer = new WarfrontCard[WarfrontProgressionRules.OfferSize];
        offer[0] = pool[0]; offer[1] = pool[1]; offer[2] = pool[2];
        offer[3] = retainerEligible ? WarfrontCard.Retainer : pool[3];
        return offer;
    }

    /// <summary>splitmix64 finaliser.</summary>
    static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
