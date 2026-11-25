using System;
using System.Collections.Generic;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmClient.Extras.Warfront.Chaos.Finale;

/// <summary>Every look and timing number of the finale's client (Chaos Warfront C5c; ours, provisional: spec "the finale (C5)" C5c rows).</summary>
public static class ChaosDuelLook
{
    /// <summary>The duel state comes on every change and twice a second: with none for this long the HUD goes (a lost end, a rejoin, the stage left).</summary>
    public const double StaleSeconds = 3.0;
    /// <summary>The outcome line (won or lost) stays this long after the phase first showed it.</summary>
    public const double OutcomeSeconds = 8.0;
    /// <summary>The duelist's own entry line stays this long (plan: 3 s).</summary>
    public const double EntrySeconds = 3.0;
    /// <summary>A telegraph whose own seconds are shorter (the bolt's 0) is kept this long, so its cue shows.</summary>
    public const double CueSeconds = 0.15;
    /// <summary>At most this many telegraphs are kept (a burst never grows the list).</summary>
    public const int MaxTelegraphs = 12;
    /// <summary>The (tick, ability) pairs remembered against the server's three copies.</summary>
    public const int SeenKept = 64;

    public const string LordLabel = "CHAOS LORD";
    public const string LastChallenger = "LAST CHALLENGER";
    public const string EntryLine = "DUEL: YOU ALONE AGAINST THE CHAOS LORD";
    public const string ShieldedLord = "CHAOS LORD: SHIELDED";

    /// <summary>The HUD's words in a Chaos fortress before the relay (lead 40: a shielded cue on the Lord's bar): C3's Chaos words, the Lord's bar <see cref="ShieldedLord"/>.</summary>
    public static string ShieldedRelabel(string text)
    {
        string chaos = SmContent.Extras.Warfront.WarfrontChaosLooksPlan.HudLabel(text);
        return chaos == LordLabel ? ShieldedLord : chaos;
    }

    /// <summary>The leap marker's red-violet (ours; the breach marker's colour, the warp's).</summary>
    public static readonly (float R, float G, float B) LeapColour = (0.86f, 0.18f, 0.62f);
    /// <summary>The bolt's muzzle flash (ours): a bolter's hot yellow.</summary>
    public static readonly (float R, float G, float B) MuzzleColour = (1.0f, 0.78f, 0.32f);
    /// <summary>The leap ring starts at <see cref="WarfrontDuelRules.LeapRadius"/> x this and shrinks to the landing's radius over the marker's seconds (ours).</summary>
    public const float LeapRingStartScale = 1.6f;
    /// <summary>The muzzle flash's radius in metres (ours).</summary>
    public const float MuzzleRadius = 0.35f;
}

/// <summary>One of the Lord's telegraphs as the client keeps it: the wire's, and when it arrived.</summary>
public readonly record struct ChaosDuelTold(WarfrontChaosDuelWire.Telegraph Told, double At)
{
    /// <summary>How long it is shown: its own seconds, at least <see cref="ChaosDuelLook.CueSeconds"/>.</summary>
    public double Life => Math.Max(Told.Seconds, ChaosDuelLook.CueSeconds);
    /// <summary>0 at its arrival, 1 when it lands.</summary>
    public float Progress(double now) => (float)Math.Clamp((now - At) / Life, 0.0, 1.0);
    public bool Over(double now) => now - At >= Life;
}

/// <summary>What the duel HUD shows this frame; empty strings are not drawn.</summary>
public readonly record struct ChaosDuelLines(
    bool Shown, string Champion, string Next, string Queue, string Spectating, string Centre, bool CentreWarn, bool Bar, float BarFraction, string BarPercent);

/// <summary>
/// The finale on the client (Chaos Warfront C5c; tag 51 kinds 16 and 17, <see cref="WarfrontChaosDuelWire"/>): the latest duel state, the Lord's telegraphs (the first copy of each (tick, ability) pair: the
/// server sends each three times), the handoff countdown, the duelist's entry line and the outcome, and what the HUD's lines say. Pure and Godot-free (tested in <c>DuelHudModelTests</c>); the networked view
/// draws from it. Nothing reaches it outside a Chaos stage's finale: the server sends kind 16 only past the bodyguard.
/// </summary>
public sealed class ChaosDuelModel
{
    readonly List<ChaosDuelTold> _told = new();
    readonly Queue<(uint Tick, byte Ability)> _seenOrder = new();
    readonly HashSet<(uint Tick, byte Ability)> _seen = new();
    readonly List<ChaosDuelTold> _newTold = new();

    /// <summary>The latest state (null: none this battle, or stale and forgotten).</summary>
    public WarfrontChaosDuelWire.State? State { get; private set; }
    public double StateAt { get; private set; } = double.NegativeInfinity;
    /// <summary>The champion of the last attempt (kept through the handoff and the outcome, for their lines).</summary>
    public ushort LastChampion { get; private set; }
    public double HandoffAt { get; private set; } = double.NegativeInfinity;
    public double OutcomeAt { get; private set; } = double.NegativeInfinity;
    public double EntryAt { get; private set; } = double.NegativeInfinity;
    public ushort EntryFor { get; private set; }
    /// <summary>Sections refused (malformed or another kind) and telegraph repeats dropped.</summary>
    public int Dropped { get; private set; }
    public int Repeats { get; private set; }
    /// <summary>Whether the last <see cref="Apply"/> changed the phase (the view logs it).</summary>
    public bool PhaseChanged { get; private set; }

    /// <summary>The telegraphs still showing.</summary>
    public IReadOnlyList<ChaosDuelTold> Telegraphs => _told;
    /// <summary>The telegraphs the last <see cref="Apply"/> took (first copies only).</summary>
    public IReadOnlyList<ChaosDuelTold> NewTelegraphs => _newTold;

    /// <summary>Whether the finale is past the bodyguard on this client: from the first state of the battle until it goes stale. The breach model's silent closes follow it.</summary>
    public bool Running(double now) => State is { } s && s.Phase != WarfrontDuelPhase.Bodyguard && now - StateAt <= ChaosDuelLook.StaleSeconds;

    /// <summary>A section of kind 16 or 17 at <paramref name="now"/>: false (and nothing changed) when it does not decode.</summary>
    public bool Apply(ReadOnlySpan<byte> payload, double now)
    {
        PhaseChanged = false;
        _newTold.Clear();
        if (!WarfrontChaosDuelWire.TryDecode(payload, out var section)) { Dropped++; return false; }
        if (section.Kind == WarfrontChaosDuelWire.KindTelegraph)
        {
            var t = section.Told;
            if (!_seen.Add((t.Tick, t.Ability))) { Repeats++; return true; }
            _seenOrder.Enqueue((t.Tick, t.Ability));
            while (_seenOrder.Count > ChaosDuelLook.SeenKept) _seen.Remove(_seenOrder.Dequeue());
            var told = new ChaosDuelTold(t, now);
            Expire(now);
            if (_told.Count >= ChaosDuelLook.MaxTelegraphs) _told.RemoveAt(0);
            _told.Add(told);
            _newTold.Add(told);
            return true;
        }
        var s = section.Duel;
        var before = State;
        if (before is null || now - StateAt > ChaosDuelLook.StaleSeconds) { before = null; ForgetBattle(); }
        PhaseChanged = before is not { } b || b.Phase != s.Phase;
        if (PhaseChanged)
        {
            if (s.Phase == WarfrontDuelPhase.Handoff) HandoffAt = now;
            if (s.Phase is WarfrontDuelPhase.Won or WarfrontDuelPhase.Lost) OutcomeAt = now;
        }
        if (s.Champion != 0)
        {
            if (s.Phase == WarfrontDuelPhase.Duel && (before is not { } was || was.Champion != s.Champion || was.Phase != WarfrontDuelPhase.Duel)) { EntryAt = now; EntryFor = s.Champion; }
            LastChampion = s.Champion;
        }
        State = s;
        StateAt = now;
        return true;
    }

    /// <summary>The frame: telegraphs that landed go; a state older than <see cref="ChaosDuelLook.StaleSeconds"/> is forgotten unless its outcome line still shows (true when the finale was forgotten).</summary>
    public bool Tick(double now)
    {
        Expire(now);
        if (State is null || now - StateAt <= ChaosDuelLook.StaleSeconds) return false;
        if (State.Value.Phase is WarfrontDuelPhase.Won or WarfrontDuelPhase.Lost && now - OutcomeAt < ChaosDuelLook.OutcomeSeconds) return false;
        Clear();
        return true;
    }

    void Expire(double now) => _told.RemoveAll(t => t.Over(now));

    void ForgetBattle()
    {
        LastChampion = 0;
        HandoffAt = OutcomeAt = EntryAt = double.NegativeInfinity;
        EntryFor = 0;
    }

    /// <summary>Forgets everything (the map went, the view changed).</summary>
    public void Clear()
    {
        State = null;
        StateAt = double.NegativeInfinity;
        _told.Clear();
        _newTold.Clear();
        _seen.Clear();
        _seenOrder.Clear();
        ForgetBattle();
    }

    /// <summary>The whole seconds left of the handoff's pause (3, 2, 1; 0 when it is over or not running).</summary>
    public int HandoffCountdown(double now)
    {
        if (State is not { Phase: WarfrontDuelPhase.Handoff }) return 0;
        double left = WarfrontDuelRules.HandoffSeconds - (now - HandoffAt);
        return left <= 0 ? 0 : (int)Math.Ceiling(left - 1e-9);
    }

    /// <summary>The Lord's bar: the permille as a fraction, 0 to 1.</summary>
    public static float BarFraction(ushort permille) => Math.Clamp(permille / 1000f, 0f, 1f);

    /// <summary>The bar's percent: the permille rounded to the nearest whole percent, at least 1 % while the Lord lives (the permille is already rounded up, so a living Lord never shows 0 %).</summary>
    public static string Percent(ushort permille)
    {
        int p = Math.Clamp((int)permille, 0, 1000);
        return $"{(p == 0 ? 0 : Math.Max(1, (int)Math.Round(p / 10.0, MidpointRounding.AwayFromZero)))}%";
    }

    /// <summary>
    /// The HUD's lines for the viewer <paramref name="own"/> (his player id) at <paramref name="now"/>; <paramref name="name"/> gives a player's HUD name (upper-cased here: one style per level).
    /// Banner: <c>CHAMPION: name</c> and <c>NEXT: name</c> (or <see cref="ChaosDuelLook.LastChallenger"/>); the queue count; <c>SPECTATING name</c> for everyone but the duelist; the centre line: the duelist's
    /// entry line (3 s), the handoff's countdown, the outcome.
    /// </summary>
    public ChaosDuelLines Lines(ushort own, Func<ushort, string> name, double now)
    {
        if (State is not { } s || s.Phase == WarfrontDuelPhase.Bodyguard) return default;
        bool outcome = s.Phase is WarfrontDuelPhase.Won or WarfrontDuelPhase.Lost;
        if (!outcome && now - StateAt > ChaosDuelLook.StaleSeconds) return default;
        if (outcome && now - OutcomeAt >= ChaosDuelLook.OutcomeSeconds) return default;
        string Name(ushort id) => id == 0 ? "" : id == own ? "YOU" : Upper(name(id), id);
        string champion = "", next = "", queue = "", spectating = "", centre = "";
        bool warn = false;
        switch (s.Phase)
        {
            case WarfrontDuelPhase.Snapshot:
            case WarfrontDuelPhase.Duel:
                if (s.Champion != 0) champion = "CHAMPION: " + Name(s.Champion);
                next = s.Next != 0 ? "NEXT: " + Name(s.Next) : ChaosDuelLook.LastChallenger;
                queue = QueueLine(s.Remaining);
                if (s.Champion != 0 && s.Champion != own) spectating = "SPECTATING " + Upper(name(s.Champion), s.Champion);
                if (s.Champion != 0 && s.Champion == own && EntryFor == own && now - EntryAt < ChaosDuelLook.EntrySeconds) { centre = ChaosDuelLook.EntryLine; warn = true; }
                break;
            case WarfrontDuelPhase.Handoff:
                if (LastChampion != 0) champion = (LastChampion == own ? "YOU HAVE" : Upper(name(LastChampion), LastChampion) + " HAS") + " FALLEN";
                next = s.Next != 0 ? "NEXT: " + Name(s.Next) : ChaosDuelLook.LastChallenger;
                queue = QueueLine(s.Remaining);
                int left = HandoffCountdown(now);
                centre = s.Next == own ? $"YOUR DUEL BEGINS IN {Math.Max(1, left)}" : $"THE NEXT CHALLENGER ENTERS IN {Math.Max(1, left)}";
                warn = s.Next == own;
                break;
            case WarfrontDuelPhase.Won:
                ushort victor = s.Champion != 0 ? s.Champion : LastChampion;
                centre = victor == 0 ? "THE CHAOS LORD IS DEAD" : victor == own ? "YOU HAVE SLAIN THE CHAOS LORD" : Upper(name(victor), victor) + " HAS SLAIN THE CHAOS LORD";
                break;
            case WarfrontDuelPhase.Lost:
                centre = "NO CHALLENGER LEFT: THE CAMPAIGN IS LOST";
                warn = true;
                break;
        }
        bool bar = !outcome;
        return new ChaosDuelLines(true, champion, next, queue, spectating, centre, warn, bar, BarFraction(s.Permille), Percent(s.Permille));
    }

    /// <summary>The queue count: the queued players still to come (the wire's remaining; an absent one counts until his turn is skipped).</summary>
    static string QueueLine(byte remaining) => remaining == 0 ? "" : $"CHALLENGERS WAITING: {remaining}";   // none: LAST CHALLENGER says it

    static string Upper(string? name, ushort id) => string.IsNullOrWhiteSpace(name) ? $"P{id}" : name.Trim().ToUpperInvariant();

    /// <summary>Whether the HUD shows anything at <paramref name="now"/> (the gate of <see cref="Lines"/> without building its strings).</summary>
    public bool HudShown(double now)
    {
        if (State is not { } s || s.Phase == WarfrontDuelPhase.Bodyguard) return false;
        bool outcome = s.Phase is WarfrontDuelPhase.Won or WarfrontDuelPhase.Lost;
        return outcome ? now - OutcomeAt < ChaosDuelLook.OutcomeSeconds : now - StateAt <= ChaosDuelLook.StaleSeconds;
    }

    /// <summary>Whether the viewer <paramref name="own"/> should watch the champion (the spectator camera): a duel is on and he is not its champion.</summary>
    public bool Watching(ushort own, double now) => Running(now) && State is { Phase: WarfrontDuelPhase.Duel, Champion: not 0 } s && s.Champion != own;
}
