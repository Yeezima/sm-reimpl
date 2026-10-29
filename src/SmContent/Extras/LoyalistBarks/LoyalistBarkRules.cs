namespace SmContent.Extras.LoyalistBarks;

/// <summary>The game moments a loyalist marine may bark at (ours: what the simulation shows every client, never per bullet or per damage tick).</summary>
public enum BarkMoment : byte
{
    /// <summary>The battle-cry key: a FOR THE EMPEROR take, at any time.</summary>
    Key = 1,
    /// <summary>The player started the charge (bull rush).</summary>
    Charge = 2,
    /// <summary>The player killed an enemy (the kill feed).</summary>
    Kill = 3,
    /// <summary>The player started a melee attack.</summary>
    Melee = 4,
    /// <summary>The player started firing a bolt weapon after a pause.</summary>
    Fire = 5,
    /// <summary>The own player started taking damage after a quiet spell (only the own client knows: others' health is not on the wire).</summary>
    UnderFire = 6,
}

/// <summary>Why a bark was or was not said (<c>--log-sound loyalist</c>, the bot summary).</summary>
public enum BarkVerdict : byte { Said, NotLoyalist, Chance, Cooldown, Spacing, PhraseCooldown, Speaking, Sm1Speech, NoLine }

/// <summary>
/// Added content "loyalist barks": which take a loyalist Space Marine says when (engine-free; tested). Every number here is ours (refine register,
/// "Added content: loyalist barks"); the 8 s speaker cooldown, the 20 s same-phrase cooldown and "no immediate repeat of a take" are the voice pack's
/// suggestions (the owner's pack notes).
/// <list type="bullet">
/// <item>Loyalist Space Marines only: never Chaos, never an Ork (whatever side it plays).</item>
/// <item>Moments to the pack's events: the charge says a battle cry, a kill an attack line or a battle cry, a melee attack an attack line, firing a bolt
/// weapon "Eat boltgun!", the own player's first damage after <see cref="QuietBeforeUnderFire"/> s "Taking fire!"; each by <see cref="Chance"/>.</item>
/// <item>One line at a time per speaker; at most one random bark per speaker every <see cref="SpeakerCooldown"/> s and the same phrase from a speaker
/// every <see cref="PhraseCooldown"/> s; random barks start at most every <see cref="GlobalSpacing"/> s anywhere; a take is never the speaker's last take
/// of that phrase.</item>
/// <item>Space Marine's own speech (M33's exertion and action lines) takes precedence over random barks: they are not started over it and stop when one
/// starts (yield).</item>
/// <item>The key: a FOR THE EMPEROR take at any time, at most every 2 s (the cooldown is in ticks, kept by the server and the shouting client: <c>LoyalistShoutWire</c>), picked from the player's shout sequence alone
/// (<see cref="KeyTake"/>: every client hears the same take, never the same one twice in a row); it skips the chance, the other cooldowns and the
/// spacing and cuts the speaker's own line and an SM1 line (the player asked for it). It changes no action and plays no animation.</item>
/// </list>
/// </summary>
public sealed class LoyalistBarkRules
{
    public const double SpeakerCooldown = 8.0;
    public const double PhraseCooldown = 20.0;
    public const double GlobalSpacing = 1.5;
    /// <summary>A firing bark needs the trigger to have rested this long (seconds).</summary>
    public const double FirePause = 2.0;
    /// <summary>"Taking fire!" needs this long without damage first (seconds).</summary>
    public const double QuietBeforeUnderFire = 5.0;

    public static double Chance(BarkMoment m) => m switch
    {
        BarkMoment.Charge => 0.6,
        BarkMoment.Kill => 0.5,
        BarkMoment.Melee => 0.15,
        BarkMoment.Fire => 0.1,
        BarkMoment.UnderFire => 0.35,
        _ => 1.0,
    };

    /// <summary>The pack events a moment draws from (the key: <see cref="LoyalistBarkPack.EmperorPhrase"/> only).</summary>
    public static BarkEvent[] Events(BarkMoment m) => m switch
    {
        BarkMoment.Charge => [BarkEvent.BattleCry],
        BarkMoment.Kill => [BarkEvent.Attack, BarkEvent.BattleCry],
        BarkMoment.Melee => [BarkEvent.Attack],
        BarkMoment.Fire => [BarkEvent.Firing],
        BarkMoment.UnderFire => [BarkEvent.UnderFire],
        _ => [BarkEvent.BattleCry],
    };

    /// <summary>The clips a moment may pick from, before the cooldowns (ordered by id, so every client builds the same list).</summary>
    public static IReadOnlyList<BarkClip> Candidates(LoyalistBarkPack pack, BarkMoment m) =>
        m == BarkMoment.Key
            ? pack.Clips.Where(c => c.Phrase == LoyalistBarkPack.EmperorPhrase).OrderBy(c => c.Id, StringComparer.Ordinal).ToList()
            : pack.Clips.Where(c => Events(m).Contains(c.Event)).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    sealed class Speaker
    {
        public double LastBark = double.NegativeInfinity;
        public readonly Dictionary<string, double> PhraseAt = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> LastTake = new(StringComparer.Ordinal);
    }

    readonly LoyalistBarkPack _pack;
    readonly Dictionary<int, Speaker> _speakers = new();
    readonly Dictionary<BarkMoment, IReadOnlyList<BarkClip>> _candidates = new();
    double _lastAny = double.NegativeInfinity;

    public LoyalistBarkRules(LoyalistBarkPack pack) => _pack = pack;

    /// <summary>
    /// Decides a bark of <paramref name="player"/> at <paramref name="now"/> (seconds). <paramref name="seed"/> names the event (the kill's sequence,
    /// a tick bucket; for the key the player's shout sequence) so clients that see the same event roll and pick alike. <paramref name="speaking"/>: the speaker's own bark is playing;
    /// <paramref name="sm1Speech"/>: an SM1 line is. Returns the clip when said (and notes it), else null with the reason.
    /// </summary>
    public (BarkClip? Clip, BarkVerdict Verdict, double Roll) Decide(int player, bool loyalist, BarkMoment moment, long seed, double now, bool speaking, bool sm1Speech)
    {
        var (h1, h2) = Hash(player, moment, seed);
        double roll = (h1 >> 11) * (1.0 / (1UL << 53));
        if (!loyalist) return (null, BarkVerdict.NotLoyalist, roll);
        if (!_speakers.TryGetValue(player, out var sp)) _speakers[player] = sp = new Speaker();
        if (!_candidates.TryGetValue(moment, out var all)) _candidates[moment] = all = Candidates(_pack, moment);
        IReadOnlyList<BarkClip> candidates = all;
        if (candidates.Count == 0) return (null, BarkVerdict.NoLine, roll);
        bool key = moment == BarkMoment.Key;
        if (!key)
        {
            if (roll >= Chance(moment)) return (null, BarkVerdict.Chance, roll);
            if (sm1Speech) return (null, BarkVerdict.Sm1Speech, roll);
            if (speaking) return (null, BarkVerdict.Speaking, roll);
            if (now - sp.LastBark < SpeakerCooldown) return (null, BarkVerdict.Cooldown, roll);
            if (now - _lastAny < GlobalSpacing) return (null, BarkVerdict.Spacing, roll);
            candidates = candidates.Where(c => !sp.PhraseAt.TryGetValue(c.Phrase, out double at) || now - at >= PhraseCooldown).ToList();
            if (candidates.Count == 0) return (null, BarkVerdict.PhraseCooldown, roll);
        }
        if (key)
        {
            // The key: the take is a function of the player and its shout sequence alone (seed = the sequence), so every client hears the same take and
            // never the same one twice in a row: a fixed per-player order of the takes, stepped once per shout.
            var key4 = KeyTake(player, (byte)seed, candidates);
            sp.LastTake[key4.Phrase] = key4.Take;
            sp.PhraseAt[key4.Phrase] = now;
            return (key4, BarkVerdict.Said, roll);
        }
        // A phrase, then a take of it other than the speaker's last take of that phrase.
        var phrases = candidates.Select(c => c.Phrase).Distinct().ToList();
        string phrase = phrases[(int)(h2 % (ulong)phrases.Count)];
        var takes = candidates.Where(c => c.Phrase == phrase).ToList();
        if (takes.Count > 1 && sp.LastTake.TryGetValue(phrase, out int last)) takes = takes.Where(c => c.Take != last).ToList();
        var clip = takes[(int)((h2 >> 20) % (ulong)takes.Count)];
        sp.LastTake[phrase] = clip.Take;
        sp.PhraseAt[phrase] = now;
        sp.LastBark = now; _lastAny = now;
        return (clip, BarkVerdict.Said, roll);
    }

    /// <summary>
    /// The key's take for <paramref name="player"/>'s shout number <paramref name="sequence"/> (the server's per-player count, wrapping at 256): the takes
    /// (ordered by id) in a per-player order, the sequence stepping through it. Consecutive sequences never give the same take (also across the wrap, 256
    /// being a multiple of the four takes).
    /// </summary>
    public static BarkClip KeyTake(int player, byte sequence, IReadOnlyList<BarkClip> takes)
    {
        // Note: the no-repeat rule holds across the wrap (sequence 255 -> 0) only when 256 % takes.Count == 0 (KeyTakeWrapsWithoutRepeat; the pack has 4).
        // With 3, 5, 6 or 7 takes the take of sequence 0 can equal that of 255, once per 256 shouts of a player. A test pins the pack's count.
        var order = takes.OrderBy(c => Hash(player, BarkMoment.Key, c.Take).Item1).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        return order[sequence % order.Count];
    }

    /// <summary>Whether <see cref="KeyTake"/> never repeats a take across the u8 sequence's wrap for <paramref name="takes"/> takes (256 a multiple of it).</summary>
    public static bool KeyTakeWrapsWithoutRepeat(int takes) => takes > 1 && 256 % takes == 0;

    /// <summary>Forgets a speaker who left.</summary>
    public void Forget(int player) => _speakers.Remove(player);

    static (ulong, ulong) Hash(int player, BarkMoment moment, long seed)
    {
        ulong h = 1469598103934665603UL;
        foreach (long v in new long[] { player, (long)moment, seed })
            for (int b = 0; b < 8; b++) { h ^= (byte)(v >> (8 * b)); h *= 1099511628211UL; }
        ulong a = Mix(h), c = Mix(h ^ 0x9E3779B97F4A7C15UL);
        return (a, c);
        static ulong Mix(ulong x) { x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 27; x *= 0x94D049BB133111EBUL; return x ^ (x >> 31); }
    }
}
