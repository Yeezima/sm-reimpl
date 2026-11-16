using SmSim.Extras.Warfront.Progression;

namespace SmNet.Extras.Warfront;

/// <summary>The <c>Progress</c> section (per client): the viewer's own level, XP, pending picks, owned cards and attack-speed bonus. The server owns every figure; the client only shows them.</summary>
public sealed record WarfrontProgressWire(int Level, uint Xp, uint XpAtLevel, uint XpAtNextLevel, int PendingPicks, IReadOnlyList<byte> CardCounts, int Retainers, int AttackSpeedMilli)
{
    /// <summary>The attack-speed bonus as a fraction (0.2 = +20 %).</summary>
    public float AttackSpeedBonus => AttackSpeedMilli / 1000f;
}

/// <summary>The <c>Offer</c> section (per client): the standing offer, or <see cref="Open"/> = false once when it closes. <see cref="PickIndex"/> is the value the client echoes in its Pick command.</summary>
public sealed record WarfrontOfferWire(bool Open, int PickIndex, IReadOnlyList<WarfrontCard> Cards, int TenthsLeft, bool Aboard)
{
    public float SecondsLeft => TenthsLeft / 10f;
}

/// <summary>
/// The wire of Warfront's card picker and XP HUD (slice W8): two envelope tags of their own, no change to <c>WarfrontWire</c>, <c>Protocol.Version</c> or <c>ContentExtras.WireVersion</c>. The pick itself is
/// W5a's <c>Pick</c> command (<c>[u8 slot][u8 pick index]</c>), echoing <see cref="WarfrontOfferWire.PickIndex"/>. Values are little-endian. Every <c>TryDecode</c> returns false on a malformed, truncated, oversized
/// or out-of-range payload and never throws.
/// <list type="bullet">
/// <item>Tag <see cref="ProgressTag"/>, <see cref="ProgressLength"/> bytes: <c>[u8 level][u32 xp][u32 xp at this level][u32 xp at next level][u8 pending picks][u8 x5 card counts in WarfrontCard order, AttackSpeed to MaxArmor][u8 retainer cards][u16 attack-speed bonus, 1/1000]</c>.
/// Sent on a change and every 2 s.</item>
/// <item>Tag <see cref="OfferTag"/>, <see cref="OfferLength"/> bytes: <c>[u8 open][u8 pick index][u8 x4 cards][u8 tenths of a second left][u8 aboard]</c>. Sent every tick group while an offer stands, and with
/// <c>open = 0</c> (cards and time zero) when it closes: the sender repeats the close record in three consecutive sends (the datagram is unreliable), and the client closes itself after
/// <c>WarfrontProgressModel.SelfCloseSeconds</c> without an open record or when Progress shows fewer pending picks than when the offer opened.</item>
/// </list>
/// </summary>
public static class WarfrontProgressionWire
{
    /// <summary>Envelope tags (W5a has 40 to 43, the squad 32 to 39, W12 46).</summary>
    public const byte ProgressTag = 44, OfferTag = 45;
    public const int ProgressLength = 22, OfferLength = 8, StatCardTypes = 5, OfferSize = 4;

    public static byte[] Encode(WarfrontProgressWire p)
    {
        var b = new byte[ProgressLength];
        b[0] = (byte)Math.Clamp(p.Level, 1, 255);
        Put32(b, 1, p.Xp); Put32(b, 5, p.XpAtLevel); Put32(b, 9, p.XpAtNextLevel);
        b[13] = (byte)Math.Clamp(p.PendingPicks, 0, 255);
        for (int i = 0; i < StatCardTypes; i++) b[14 + i] = i < p.CardCounts.Count ? p.CardCounts[i] : (byte)0;
        b[19] = (byte)Math.Clamp(p.Retainers, 0, 255);
        int bonus = Math.Clamp(p.AttackSpeedMilli, 0, ushort.MaxValue);
        b[20] = (byte)bonus; b[21] = (byte)(bonus >> 8);
        return b;
    }

    public static byte[] Encode(WarfrontOfferWire o)
    {
        var b = new byte[OfferLength];
        b[0] = (byte)(o.Open ? 1 : 0);
        b[1] = (byte)o.PickIndex;
        for (int i = 0; i < OfferSize; i++) b[2 + i] = i < o.Cards.Count ? (byte)o.Cards[i] : (byte)0;
        b[6] = (byte)Math.Clamp(o.TenthsLeft, 0, 255);
        b[7] = (byte)(o.Aboard ? 1 : 0);
        return b;
    }

    /// <summary>The close record: <c>open = 0</c>, everything else zero.</summary>
    public static byte[] EncodeClosed(int pickIndex) => Encode(new WarfrontOfferWire(false, pickIndex, new WarfrontCard[OfferSize], 0, false));

    public static bool TryDecode(ReadOnlySpan<byte> d, out WarfrontProgressWire progress)
    {
        progress = null!;
        if (d.Length != ProgressLength || d[0] == 0) return false;
        uint xp = Get32(d, 1), atLevel = Get32(d, 5), atNext = Get32(d, 9);
        if (atLevel > xp || atLevel > atNext) return false;
        var counts = new byte[StatCardTypes];
        for (int i = 0; i < StatCardTypes; i++) counts[i] = d[14 + i];
        progress = new WarfrontProgressWire(d[0], xp, atLevel, atNext, d[13], counts, d[19], d[20] | d[21] << 8);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> d, out WarfrontOfferWire offer)
    {
        offer = null!;
        if (d.Length != OfferLength || d[0] > 1 || d[7] > 1) return false;
        var cards = new WarfrontCard[OfferSize];
        for (int i = 0; i < OfferSize; i++)
        {
            if (d[2 + i] > (byte)WarfrontCard.Retainer) return false;
            cards[i] = (WarfrontCard)d[2 + i];
        }
        if (d[0] == 1)
        {
            // A standing offer: four distinct real cards, the Retainer only in the last slot.
            for (int i = 0; i < OfferSize; i++)
            {
                if (cards[i] == WarfrontCard.None) return false;
                if (cards[i] == WarfrontCard.Retainer && i != WarfrontProgressionRules.RetainerSlot) return false;
                for (int j = 0; j < i; j++) if (cards[j] == cards[i]) return false;
            }
        }
        offer = new WarfrontOfferWire(d[0] == 1, d[1], cards, d[6], d[7] == 1);
        return true;
    }

    static void Put32(byte[] b, int at, uint v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
    static uint Get32(ReadOnlySpan<byte> d, int at) => (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
}
