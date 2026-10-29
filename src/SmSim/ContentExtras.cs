namespace SmSim;

/// <summary>
/// Added content (docs/added-content.md): a whole system, faction, mode or mechanic SM1 multiplayer did not have, removable in one piece.
/// The owner's rule (2026-10-28): the added content stays separable from the faithful reproduction at any time; the first meta milestone is
/// a 95 % or more faithful reproduction of SM1. A match runs one set of these flags (<see cref="SimConfig.Extras"/>, in the config hash), so a
/// server and a client with another set refuse each other. <see cref="None"/> is the faithful game.
/// </summary>
[Flags]
public enum ContentExtras : uint
{
    /// <summary>The faithful game: no added content.</summary>
    None = 0,
    /// <summary>The Ork Boy as a playable class (docs/ork-faction-design.md; the one sanctioned addition).</summary>
    Orks = 1,
    /// <summary>Gore: severing, exploding bodies and blood beyond the data (paused on branch <c>gore/g1-sim</c>; the flag is reserved).</summary>
    Gore = 2,
    /// <summary>
    /// Loyalist barks (the owner, 2026-10-29): loyalist Space Marines bark lines of the owner's own voice pack at charges, kills, melee and firing,
    /// and the T key shouts FOR THE EMPEROR for everyone to hear, at any time, without an animation (docs/added-content.md section 7).
    /// </summary>
    LoyalistBarks = 4,
}

/// <summary>The build-wide facts about <see cref="ContentExtras"/> (C# 14 static extension members: <c>ContentExtras.WireVersion</c>, <c>ContentExtras.Compiled</c>).</summary>
public static class ContentExtrasInfo
{
    extension(ContentExtras)
    {
        /// <summary>
        /// The version of everything the added content puts on the wire or in the simulation that the faithful game does not have. Hashed with
        /// the flags (<see cref="SimConfig.Hash"/>) when any flag is set; raise it for an extras-only change, so <c>Protocol.Version</c> stays the
        /// faithful wire's version.
        /// </summary>
        public static uint WireVersion => 2;   // 2: the extras envelope (message kind 16) and the action key (input bit 13), loyalist barks

        /// <summary>
        /// True when this build compiled the <c>Extras/</c> folders (the <c>SmExtras</c> build property, true by default; <c>-p:SmExtras=false</c>
        /// builds the faithful game alone and runs only the faithful profile).
        /// </summary>
        public static bool Compiled =>
#if SM_EXTRAS
            true;
#else
            false;
#endif

        /// <summary>
        /// The added content that reads <see cref="InputButtons.ExtraAction"/> (input bit 13): a server whose match runs none of it drops the bit
        /// from every input it receives.
        /// </summary>
        public static ContentExtras ActionKeyUsers => ContentExtras.LoyalistBarks;

        /// <summary>Every flag this source tree knows.</summary>
        public static ContentExtras All => ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks;
    }
}

/// <summary>
/// The named content profiles of <c>--content</c> on the server and the client: <c>faithful</c> (no added content), <c>extended</c> (every
/// added content this build compiled), or a comma-separated list of flags (<c>orks,gore</c>).
/// </summary>
public static class ContentProfiles
{
    public const string Faithful = "faithful";
    public const string Extended = "extended";

    /// <summary>
    /// The profile a server or client runs without <c>--content</c> (the owner's default, 2026-10-28: he plays the Ork and gore). A one-line change
    /// for tester or release builds that ship the faithful game. A build without extras always runs <see cref="Faithful"/>.
    /// </summary>
    public const string DefaultName = Extended;

    /// <summary>The flags of <see cref="DefaultName"/> in this build (<see cref="ContentExtras.None"/> in a build without extras).</summary>
    public static ContentExtras Default => ContentExtras.Compiled && TryParse(DefaultName, out var extras, out _) ? extras : ContentExtras.None;

    /// <summary>The flag names of a list (lower case, as <c>--content</c> takes them).</summary>
    static readonly (string Name, ContentExtras Flag)[] Flags = [("orks", ContentExtras.Orks), ("gore", ContentExtras.Gore), ("loyalist-barks", ContentExtras.LoyalistBarks)];

    /// <summary>
    /// Parses a <c>--content</c> value: <c>faithful</c>, <c>extended</c> or flags such as <c>orks,gore</c> (case and spaces ignored). False with a
    /// one-line <paramref name="error"/> for an empty or unknown name, and for any added content in a build without extras.
    /// </summary>
    public static bool TryParse(string? text, out ContentExtras extras, out string error)
    {
        extras = ContentExtras.None;
        error = "";
        string value = (text ?? "").Trim().ToLowerInvariant();
        if (value == Faithful) return true;
        if (value == Extended) extras = ContentExtras.All;
        else
        {
            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            if (value.Length == 0 || parts.Any(p => p.Length == 0))
            {
                error = $"--content '{text}': expected {Expected}";
                return false;
            }
            foreach (string part in parts)
            {
                int i = Array.FindIndex(Flags, f => f.Name == part);
                if (i < 0)
                {
                    extras = ContentExtras.None;
                    error = $"--content '{text}': unknown added content '{part}'; expected {Expected}";
                    return false;
                }
                extras |= Flags[i].Flag;
            }
        }
        if (extras != ContentExtras.None && !ContentExtras.Compiled)
        {
            error = $"--content '{text}': this build has no added content (built with -p:SmExtras=false); only --content {Faithful} runs";
            extras = ContentExtras.None;
            return false;
        }
        return true;
    }

    /// <summary>What <c>--content</c> takes, for messages.</summary>
    public static string Expected => $"{Faithful}, {Extended} or a list of {string.Join(", ", Flags.Select(f => f.Name))} (such as orks,gore)";

    /// <summary>The profile's name for messages and logs: <c>faithful</c>, <c>extended</c> (every known flag) or the flag list (<c>orks</c>).</summary>
    public static string Name(ContentExtras extras)
    {
        if (extras == ContentExtras.None) return Faithful;
        if (extras == ContentExtras.All) return Extended;
        var names = Flags.Where(f => extras.HasFlag(f.Flag)).Select(f => f.Name).ToList();
        uint unknown = (uint)(extras & ~ContentExtras.All);
        if (unknown != 0) names.Add($"0x{unknown:X}");
        return string.Join(",", names);
    }

    /// <summary>The profile and extras wire version for a log line or refusal: "faithful", or "extended (orks,gore; extras wire 1)".</summary>
    public static string Describe(ContentExtras extras) =>
        extras == ContentExtras.None ? Faithful : $"{Name(extras)} ({string.Join(",", Flags.Where(f => extras.HasFlag(f.Flag)).Select(f => f.Name))}; extras wire {ContentExtras.WireVersion})";
}

public sealed partial record SimConfig
{
    /// <summary>
    /// The added content this match runs (<see cref="ContentExtras"/>; <see cref="ContentExtras.None"/>, the default, is the faithful game). Hashed
    /// only when not None, so a faithful config hashes byte for byte as before the switch existed.
    /// </summary>
    public ContentExtras Extras { get; init; }

    /// <summary>
    /// Hook for added content (docs/added-content.md): writes the extras' own config into the hash after the flags and
    /// <see cref="ContentExtras.WireVersion"/>; called only when <see cref="Extras"/> is not None. Implemented in an <c>Extras/</c> folder.
    /// </summary>
    partial void ExtrasHash(BinaryWriter w);
}
