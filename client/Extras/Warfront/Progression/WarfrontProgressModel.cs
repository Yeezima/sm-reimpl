using System;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>
/// The view-model of Warfront's XP HUD and card picker (slice W8; no Godot, no sockets). The server owns everything (XP, levels, offers, protection, the pick); this holds the last Progress and Offer
/// records, answers every text the HUD and the panel show, and asks for a pick: <see cref="PickRequested"/> carries the card's slot (0 to 5, as the Pick command's slot) and the pick index the panel is
/// showing, and fires at once on a press and again every <see cref="ResendSeconds"/> until the offer closes or its pick index moves on (a stale index is refused by the server anyway; the model never sends
/// one). The host calls <see cref="Tick"/> every frame. All text is capitals (the hub's style), ours.
/// </summary>
public sealed class WarfrontProgressModel
{
    /// <summary>Resend interval of an unanswered pick [ours].</summary>
    public const float ResendSeconds = 0.3f;
    /// <summary>How long the LEVEL N banner shows [ours].</summary>
    public const float BannerSeconds = 2f;
    /// <summary>A bot client picks this long after an offer opens (proofs only).</summary>
    public const float BotDelaySeconds = 1f;
    /// <summary>The picker closes itself when no open Offer record has arrived for this long (the server repeats the record while the offer stands, and sends the close three times in a row; a lost close must not leave the panel up) [ours].</summary>
    public const float SelfCloseSeconds = 1f;

    /// <summary>The display order of the five stat counters: the wire's card order.</summary>
    public static readonly WarfrontCard[] CounterCards = WarfrontProgressionRules.StatCards;
    public static readonly string[] CounterLabels = { "SPD", "HP", "DMG", "CD", "ARM" };   // RET retired with the Retainer card (T0 review 2026-11-25)

    public bool HasProgress { get; private set; }
    public int Level { get; private set; } = 1;
    public uint Xp { get; private set; }
    public uint XpAtLevel { get; private set; }
    public uint XpAtNextLevel { get; private set; }
    public int Pending { get; private set; }
    public byte[] CardCounts { get; } = new byte[WarfrontProgressionWire.StatCardTypes];
    public int Retainers { get; private set; }
    /// <summary>The server's attack-speed bonus as a fraction; the predicted local player's fire rate follows it (host side).</summary>
    public float AttackSpeedBonus { get; private set; }

    public bool OfferOpen { get; private set; }
    public int PickIndex { get; private set; }
    /// <summary>How many slots hold a card (5, or 6 with +1 Retainer); the cards are the first this many slots.</summary>
    public int OfferedCount { get { int n = 0; foreach (var c in Cards) if (c != WarfrontCard.None) n++; return n; } }

    public WarfrontCard[] Cards { get; } = new WarfrontCard[WarfrontProgressionWire.OfferSize];
    public float SecondsLeft { get; private set; }
    public bool Aboard { get; private set; }
    /// <summary>The highlighted card (D-pad); 0 when an offer opens.</summary>
    public int Highlight { get; private set; }
    /// <summary>The slot already asked for (resent until the offer closes), or -1.</summary>
    public int Sent { get; private set; } = -1;

    /// <summary>A bot client (<c>--warfront-bot</c>): picks slot (pick index mod the number of cards) after one second.</summary>
    public bool Bot { get; set; }

    public event Action? Changed;
    /// <summary>Slot 0 to 5 and the pick index shown. The host encodes the Pick command from it.</summary>
    public event Action<int, int>? PickRequested;
    /// <summary>The level rose to this number (the banner shows; no sound in this slice).</summary>
    public event Action<int>? LevelUp;

    int _batchTotal, _lastPending, _pendingAtOpen;
    float _bannerLeft, _resendIn, _openFor, _sinceOffer;
    int _bannerLevel, _hiddenShown = -1;

    /// <summary>The LEVEL N line shows only while no offer stands (two messages at once washed one out); a banner cut short shows its remaining time after the close.</summary>
    public bool BannerShowing => PickerHidden && OfferOpen || _bannerLeft > 0f && !OfferOpen;
    public string BannerText => PickerHidden && OfferOpen ? $"CARD PICK: CARD 1 IS TAKEN IN {(int)MathF.Ceiling(SecondsLeft)} S" : "LEVEL " + _bannerLevel;

    /// <summary>The picker panel is switched off for this player (a live server until W5c's PickerBusy hook): the banner names the offer and its countdown so a frozen player knows why.</summary>
    public bool PickerHidden { get; set; }

    // ---- feed -------------------------------------------------------------------------------------------------------------------------------

    public void Apply(WarfrontProgressWire p)
    {
        int before = Level;
        bool had = HasProgress;
        HasProgress = true;
        Level = p.Level; Xp = p.Xp; XpAtLevel = p.XpAtLevel; XpAtNextLevel = p.XpAtNextLevel;
        for (int i = 0; i < CardCounts.Length; i++) CardCounts[i] = i < p.CardCounts.Count ? p.CardCounts[i] : (byte)0;
        Retainers = p.Retainers;
        AttackSpeedBonus = p.AttackSpeedBonus;
        TrackBatch(p.PendingPicks);
        if (had && p.Level > before)
        {
            _bannerLevel = p.Level; _bannerLeft = BannerSeconds;
            LevelUp?.Invoke(p.Level);
        }
        Changed?.Invoke();
    }

    public void Apply(WarfrontOfferWire o)
    {
        if (!o.Open)
        {
            // A close for an offer the panel does not show (a late or duplicate close) leaves the standing one alone.
            if (OfferOpen && o.PickIndex == PickIndex) Close();
            return;
        }
        bool fresh = !OfferOpen || o.PickIndex != PickIndex;
        OfferOpen = true;
        PickIndex = o.PickIndex;
        for (int i = 0; i < Cards.Length; i++) Cards[i] = i < o.Cards.Count ? o.Cards[i] : WarfrontCard.None;
        SecondsLeft = o.SecondsLeft;
        Aboard = o.Aboard;
        _sinceOffer = 0f;
        if (fresh)
        {
            Highlight = 0; Sent = -1; _resendIn = 0f; _openFor = 0f; _pendingAtOpen = Pending;
            if (_batchTotal < Pending) _batchTotal = Pending;
        }
        Changed?.Invoke();
    }

    void Close()
    {
        OfferOpen = false; Sent = -1; Highlight = 0; SecondsLeft = 0f;
        for (int i = 0; i < Cards.Length; i++) Cards[i] = WarfrontCard.None;
        if (Pending == 0) _batchTotal = 0;
        Changed?.Invoke();
    }

    /// <summary>The server sent nothing for a while (a stage change, a disconnect): everything shown goes.</summary>
    public void Reset()
    {
        HasProgress = false; Level = 1; Xp = XpAtLevel = XpAtNextLevel = 0; Pending = 0; Retainers = 0; AttackSpeedBonus = 0f;
        Array.Clear(CardCounts);
        _batchTotal = _lastPending = 0; _bannerLeft = 0f;
        Close();
    }

    /// <summary>The picks of one batch (the pending count when it began, plus any earned while it runs), so the header can read CARD k OF n.</summary>
    void TrackBatch(int pending)
    {
        if (pending > _lastPending && (_batchTotal > 0 || OfferOpen)) _batchTotal += pending - _lastPending;
        else if (_batchTotal < pending) _batchTotal = pending;
        _lastPending = Pending = pending;
        if (OfferOpen && pending < _pendingAtOpen) { Close(); return; }   // the pick went through (its close may have been lost)
        if (pending == 0 && !OfferOpen) _batchTotal = 0;
    }

    // ---- time -------------------------------------------------------------------------------------------------------------------------------

    public void Tick(float dt)
    {
        if (_bannerLeft > 0f && !OfferOpen) { _bannerLeft = MathF.Max(0f, _bannerLeft - dt); if (_bannerLeft == 0f) Changed?.Invoke(); }
        if (!OfferOpen) return;
        _sinceOffer += dt;
        if (_sinceOffer > SelfCloseSeconds) { Close(); return; }
        SecondsLeft = MathF.Max(0f, SecondsLeft - dt);
        if (PickerHidden && (int)MathF.Ceiling(SecondsLeft) is var whole && whole != _hiddenShown) { _hiddenShown = whole; Changed?.Invoke(); }   // the banner's countdown
        _openFor += dt;
        if (Bot && Sent < 0 && _openFor >= BotDelaySeconds) Pick(PickIndex % System.Math.Max(1, OfferedCount));
        if (Sent >= 0)
        {
            _resendIn -= dt;
            if (_resendIn <= 0f) { _resendIn = ResendSeconds; PickRequested?.Invoke(Sent, PickIndex); }
        }
    }

    // ---- input ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Asks for card <paramref name="slot"/> (0 to 5) of the standing offer. False when no offer stands or the slot is empty. A second press before the answer changes the slot asked for.</summary>
    public bool Pick(int slot)
    {
        if (!OfferOpen || slot < 0 || slot >= Cards.Length || Cards[slot] == WarfrontCard.None) return false;
        Highlight = slot;
        Sent = slot;
        _resendIn = ResendSeconds;
        PickRequested?.Invoke(slot, PickIndex);
        Changed?.Invoke();
        return true;
    }

    /// <summary>D-pad left or right: the highlight moves, clamped at the ends. Picks nothing.</summary>
    public void MoveHighlight(int delta)
    {
        if (!OfferOpen) return;
        int h = Math.Clamp(Highlight + delta, 0, Math.Max(0, OfferedCount - 1));
        if (h == Highlight) return;
        Highlight = h;
        Changed?.Invoke();
    }

    /// <summary>The mouse rests on a card: the highlight follows it.</summary>
    public void SetHighlight(int slot)
    {
        if (!OfferOpen || slot < 0 || slot >= Cards.Length || slot == Highlight) return;
        Highlight = slot;
        Changed?.Invoke();
    }

    /// <summary>D-pad down or A: takes the highlighted card.</summary>
    public bool ConfirmHighlight() => Pick(Highlight);

    // ---- text -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The card's name line.</summary>
    public static string CardName(WarfrontCard c) => c switch
    {
        WarfrontCard.AttackSpeed => "ATTACK SPEED",
        WarfrontCard.MaxHp => "MAX HP",
        WarfrontCard.Damage => "DAMAGE",
        WarfrontCard.CooldownReduction => "COOLDOWN",
        WarfrontCard.MaxArmor => "MAX ARMOUR",
        WarfrontCard.Retainer => "RETAINER",
        _ => "",
    };

    /// <summary>The one line of effect, the number from <see cref="WarfrontProgressionRules"/>.</summary>
    public static string CardEffect(WarfrontCard c) => c switch
    {
        WarfrontCard.AttackSpeed => $"+{Pct(WarfrontProgressionRules.AttackSpeedStep)} % ATTACK SPEED",
        WarfrontCard.MaxHp => $"+{Pct(WarfrontProgressionRules.MaxHpStep)} % MAX HP",
        WarfrontCard.Damage => $"+{Pct(WarfrontProgressionRules.DamageStep)} % DAMAGE",
        WarfrontCard.CooldownReduction => $"-{Pct(WarfrontProgressionRules.CooldownStep)} % ABILITY COOLDOWN",
        WarfrontCard.MaxArmor => $"+{Pct(WarfrontProgressionRules.MaxArmorStep)} % MAX ARMOUR",
        WarfrontCard.Retainer => "+1 RETAINER",
        _ => "",
    };

    /// <summary>The second line: what the player has and gets. Stat cards: "NOW +20 %, THEN +30 %"; cooldown at its floor: "AT THE 40 % FLOOR"; retainer: "SQUAD 3 OF 9" (after the pick).</summary>
    public string CardTotal(WarfrontCard c)
    {
        int n = Owned(c);
        float step = c switch
        {
            WarfrontCard.AttackSpeed => WarfrontProgressionRules.AttackSpeedStep,
            WarfrontCard.MaxHp => WarfrontProgressionRules.MaxHpStep,
            WarfrontCard.Damage => WarfrontProgressionRules.DamageStep,
            WarfrontCard.CooldownReduction => WarfrontProgressionRules.CooldownStep,
            WarfrontCard.MaxArmor => WarfrontProgressionRules.MaxArmorStep,
            _ => 0f,
        };
        if (c == WarfrontCard.Retainer) return $"SQUAD {Math.Min(n + 1, WarfrontProgressionRules.RosterMax)} OF {WarfrontProgressionRules.RosterMax}";
        if (step <= 0f) return "";
        if (c == WarfrontCard.CooldownReduction)
        {
            float max = 1f - WarfrontProgressionRules.CooldownFloor;
            if (n * step >= max - 1e-4f) return $"AT THE {Pct(WarfrontProgressionRules.CooldownFloor)} % FLOOR";
            return $"NOW -{Pct(n * step)} %, THEN -{Pct(MathF.Min(max, (n + 1) * step))} %";
        }
        return $"NOW +{Pct(n * step)} %, THEN +{Pct((n + 1) * step)} %";
    }

    static int Pct(float f) => (int)MathF.Round(f * 100f);

    /// <summary>How many of this card the player already holds.</summary>
    public int Owned(WarfrontCard c) =>
        c == WarfrontCard.Retainer ? Retainers
        : c >= WarfrontCard.AttackSpeed && c <= WarfrontCard.MaxArmor ? CardCounts[(int)c - 1] : 0;

    public string OwnedText(WarfrontCard c) => "OWNED " + Owned(c);

    /// <summary>The pick's place in the batch, 1 to <see cref="BatchSize"/>.</summary>
    public int BatchIndex => Math.Clamp(_batchTotal - Pending + 1, 1, Math.Max(1, _batchTotal));
    public int BatchSize => Math.Max(1, _batchTotal);

    public string Header => BatchSize > 1 ? $"CARD {BatchIndex} OF {BatchSize}" : "CHOOSE A CARD";

    public const string TimeoutNote = "CARD 1 IS TAKEN WHEN THE TIME RUNS OUT";
    public const string ProtectedNote = "YOU CANNOT BE HARMED WHILE CHOOSING";
    /// <summary>Five cards since the Retainer card was retired (owner 2026-11-23, card 2); before, six for a squad-capable class.</summary>
    public const string HintNote = "PRESS 1 TO 5, OR D-PAD LEFT AND RIGHT THEN A";

    /// <summary>0 to 1: the share of the pick time left (the countdown bar).</summary>
    public float TimeFraction => Math.Clamp(SecondsLeft / (float)WarfrontProgressionRules.PickTimeoutSeconds, 0f, 1f);

    /// <summary>The XP bar's fill, 0 to 1 between this level's floor and the next's.</summary>
    public float XpFraction
    {
        get
        {
            long span = (long)XpAtNextLevel - XpAtLevel;
            return span <= 0 ? 1f : Math.Clamp((float)(Xp - XpAtLevel) / span, 0f, 1f);
        }
    }

    public string LevelText => "LEVEL " + Level;
    public string PendingText => Pending > 0 ? "+" + Pending : "";
    public string XpText => $"{Xp:N0} / {XpAtNextLevel:N0} XP";

    /// <summary>The six counters, in order: attack speed, max HP, damage, cooldown, max armour, retainers.</summary>
    public string CounterText(int i) => CounterLabels[i] + "\n" + CardCounts[i];
}
