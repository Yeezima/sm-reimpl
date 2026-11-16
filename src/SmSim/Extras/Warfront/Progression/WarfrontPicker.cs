using SmSim.Extras.Warfront.Campaign;

namespace SmSim.Extras.Warfront.Progression;

/// <summary>One standing offer. <see cref="PickIndex"/> is the player's pick counter when it was rolled (the sequence number a client echoes back).</summary>
public sealed record WarfrontOpenOffer(ulong Key, int PickIndex, WarfrontCard[] Cards, double OpenedAt);

/// <summary>A pick that went through (by the player or by the timeout).</summary>
public readonly record struct WarfrontPickResult(ulong Key, int PickIndex, int Slot, WarfrontCard Card, bool Timeout);

/// <summary>
/// The server-side picker (plan section 5; owner brief). Pure: the caller passes the clock (seconds) and the situation. It decides when a queued pick opens (a lull in the field, or aboard the ship), holds the offer,
/// validates a selection against it, and owns the protection flag: while an offer stands the player is invulnerable and their input is dropped (<see cref="Protected"/>); others are untouched.
/// Protection ends on a valid pick, on the timeout (the server takes card 1 after <see cref="WarfrontProgressionRules.PickTimeoutSeconds"/>) or when the player leaves.
/// Open offers are not persisted: a reopen re-rolls the same offer for the same pick index (<see cref="WarfrontOffers.Roll"/>).
/// </summary>
public sealed class WarfrontPicker
{
    readonly WarfrontCampaign _campaign;
    readonly Dictionary<ulong, WarfrontOpenOffer> _open = new();
    readonly Dictionary<ulong, double> _lastOpened = new();

    public WarfrontPicker(WarfrontCampaign campaign) => _campaign = campaign;

    public WarfrontOpenOffer? OpenOffer(ulong key) => _open.GetValueOrDefault(key);

    /// <summary>Invulnerable and unable to act while an offer stands.</summary>
    public bool Protected(ulong key) => _open.ContainsKey(key);

    /// <summary>Whether a lull holds: no damage dealt or taken for the quiet time and no Ork within the distance.</summary>
    public static bool IsLull(double secondsSinceDamage, float nearestOrkMetres) =>
        secondsSinceDamage >= WarfrontProgressionRules.LullQuietSeconds && nearestOrkMetres > WarfrontProgressionRules.LullOrkDistance;

    /// <summary>
    /// Opens the next queued pick for <paramref name="key"/> when allowed: a pick is pending, no offer stands, and either the player is aboard the ship (no gap) or a lull holds and
    /// at least <see cref="WarfrontProgressionRules.MinSecondsBetweenOpenings"/> have passed since the last opening. <paramref name="rosterSize"/> and <paramref name="dreadnought"/> decide the Retainer slot.
    /// </summary>
    public WarfrontOpenOffer? TryOpen(ulong key, double now, bool aboard, bool lull, int rosterSize, bool dreadnought)
    {
        var p = _campaign.Player(key);
        if (p is null || p.PendingPicks <= 0 || _open.ContainsKey(key)) return null;
        if (!aboard)
        {
            if (!lull) return null;
            if (_lastOpened.TryGetValue(key, out double last) && now - last < WarfrontProgressionRules.MinSecondsBetweenOpenings) return null;
        }
        var cards = WarfrontOffers.Roll(_campaign.RunSeed, key, p.PicksTaken, WarfrontOffers.RetainerEligible(rosterSize, dreadnought));
        var offer = new WarfrontOpenOffer(key, p.PicksTaken, cards, now);
        _open[key] = offer;
        _lastOpened[key] = now;
        return offer;
    }

    /// <summary>
    /// A selection from <paramref name="key"/>'s client: <paramref name="slot"/> 0 to 3, and optionally the pick index the client believes is standing. Rejects a player with no open offer, an index out of range
    /// and a stale pick index; nothing changes on a rejection. A Retainer card is taken like any other; the roster change is the caller's (W9) business, signalled by the returned card.
    /// </summary>
    public bool TryPick(ulong key, int slot, out WarfrontPickResult result, out string? error, int? expectedPickIndex = null)
    {
        result = default; error = null;
        if (!_open.TryGetValue(key, out var offer)) { error = "no open offer"; return false; }
        if (slot < 0 || slot >= offer.Cards.Length) { error = "no such card"; return false; }
        if (expectedPickIndex is { } want && want != offer.PickIndex) { error = "stale offer"; return false; }
        return Take(offer, slot, false, out result, out error);
    }

    bool Take(WarfrontOpenOffer offer, int slot, bool timeout, out WarfrontPickResult result, out string? error)
    {
        result = default; error = null;
        var card = offer.Cards[slot];
        if (!_campaign.TryTakePick(offer.Key, (int)card)) { _open.Remove(offer.Key); error = "no pick pending"; return false; }
        _open.Remove(offer.Key);
        result = new WarfrontPickResult(offer.Key, offer.PickIndex, slot, card, timeout);
        return true;
    }

    /// <summary>Takes card 1 for every offer open longer than the timeout and returns them (protection ends with each).</summary>
    public List<WarfrontPickResult> Tick(double now)
    {
        var done = new List<WarfrontPickResult>();
        foreach (var offer in _open.Values.Where(o => now - o.OpenedAt >= WarfrontProgressionRules.PickTimeoutSeconds).OrderBy(o => o.Key).ToList())
            if (Take(offer, 0, true, out var r, out _)) done.Add(r);
        return done;
    }

    /// <summary>The player left (or was removed): the offer and its protection go; the pick stays pending and re-opens identically on return.</summary>
    public void Leave(ulong key) => _open.Remove(key);

    public void Clear() { _open.Clear(); _lastOpened.Clear(); }
}
