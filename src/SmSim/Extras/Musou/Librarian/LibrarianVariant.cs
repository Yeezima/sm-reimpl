namespace SmSim.Extras.Musou
{
    /// <summary>
    /// The Librarian's restore switches for pass 3 (librarian-lightning, 2026-11-10; the lead: "keep a restore switch for each change"): the environment variable <see cref="EnvironmentVariable"/> =
    /// a comma list of <c>shockwave</c> (the pass 2 Shockwave: 9 m, 40 damage, ordinary Orks thrown 20 to 25 m, elites and bosses not moved), <c>pull</c> (the Warp Pull back on Grenade and no equipment),
    /// <c>send</c> (tag 16 at most 10 times a second for everything, no sends at once on a cast's start or stop) and <c>early</c> (the cast command refused: the lightning starts when the press's own input
    /// tick comes round), <c>rewind</c> (musou-balance 2026-11-11: the chain lightning picked on the crowd as it stands on the server now, not as the caster's client drew it at his input's view tick; server
    /// only) or <c>all</c>. Read where the match's tuning is loaded (<c>GameplayTuning.ExtrasLoad</c>), so the server and the clients must both have it: <c>pull</c> changes the Librarian's
    /// class, which the config hash covers (a client without it is refused, ConfigMismatch).
    /// </summary>
    public readonly record struct LibrarianVariant(bool OldShockwave = false, bool WarpPull = false, bool OldSend = false, bool NoEarly = false, bool NoRewind = false)
    {
        public const string EnvironmentVariable = "SM_LIBRARIAN_OLD";

        /// <summary>Whether any switch is on.</summary>
        public bool Any => OldShockwave || WarpPull || OldSend || NoEarly || NoRewind;

        /// <summary>The switches named in <paramref name="text"/> (comma or space separated, any case; unknown words are ignored; null or empty: none).</summary>
        public static LibrarianVariant Parse(string? text)
        {
            var v = new LibrarianVariant();
            if (string.IsNullOrWhiteSpace(text)) return v;
            foreach (var word in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
                switch (word.Trim().ToLowerInvariant())
                {
                    case "all": return new LibrarianVariant(true, true, true, true, true);
                    case "shockwave": v = v with { OldShockwave = true }; break;
                    case "pull": v = v with { WarpPull = true }; break;
                    case "send": v = v with { OldSend = true }; break;
                    case "early": v = v with { NoEarly = true }; break;
                    case "rewind": v = v with { NoRewind = true }; break;
                }
            return v;
        }

        public override string ToString() => Any
            ? string.Join(",", new[] { OldShockwave ? "shockwave" : null, WarpPull ? "pull" : null, OldSend ? "send" : null, NoEarly ? "early" : null, NoRewind ? "rewind" : null }.Where(s => s is not null))
            : "none";
    }
}

namespace SmSim
{
    using SmSim.Extras.Musou;

    public sealed partial record SimConfig
    {
        /// <summary>The Librarian's restore switches (<see cref="LibrarianVariant"/>; none by default). Not written into the hash itself: the one switch that changes a class (<c>pull</c>) is covered by the classes.</summary>
        public LibrarianVariant LibrarianVariant { get; init; }
    }
}
