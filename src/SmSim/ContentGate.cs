namespace SmSim;

/// <summary>
/// What a content profile allows a player to ask for (added content E2, docs/added-content.md): one place for the server (it refuses an Ork class
/// and ignores the Ork look without the Orks), the client's Join (it never asks for them) and the customizer (it does not offer them). A faithful
/// profile has no Ork: its config has no Ork class, and these rules also hold for a config that has one without the flag.
/// </summary>
public static class ContentGate
{
    /// <summary>Whether the profile has the Ork Boy.</summary>
    public static bool OffersOrks(ContentExtras extras) => extras.HasFlag(ContentExtras.Orks);

    /// <summary>The factions the customizer's faction step offers in a profile (the Space Marines and Chaos always; the Orks only with the flag).</summary>
    public static IReadOnlyList<Race> Factions(ContentExtras extras) =>
        OffersOrks(extras) ? [Race.SpaceMarines, Race.Chaos, Race.Orks] : [Race.SpaceMarines, Race.Chaos];

    /// <summary>Whether a race may be played in a profile.</summary>
    public static bool OffersRace(ContentExtras extras, Race race) => race != Race.Orks || OffersOrks(extras);

    /// <summary>
    /// Whether class <paramref name="index"/> of <paramref name="config"/> may be played in a match with <paramref name="extras"/>: an index in range, and
    /// not an Ork class without the Orks.
    /// </summary>
    public static bool ClassOffered(SimConfig config, int index, ContentExtras extras) =>
        index >= 0 && index < config.Classes.Count && OffersRace(extras, config.Classes[index].Race);

    /// <summary>As <see cref="ClassOffered(SimConfig,int,ContentExtras)"/> with the match's own flags (<see cref="SimConfig.Extras"/>): the server's rule.</summary>
    public static bool ClassOffered(SimConfig config, int index) => ClassOffered(config, index, config.Extras);

    /// <summary>The Ork look byte a Hello or Setup may carry: the given one with the Orks, else <see cref="Races.OrkLookAutomatic"/> (ignored, never an error).</summary>
    public static byte OrkLook(byte look, ContentExtras extras) => OffersOrks(extras) ? look : Races.OrkLookAutomatic;

    /// <summary>
    /// A loadout request as a Join may send it: unchanged when its class is none (-1: the server's default) or offered; otherwise
    /// <paramref name="fallback"/> (the side's default class: a saved Ork loadout is not played in a profile without the Orks). A class index beyond
    /// <paramref name="config"/> is kept when the profile has the Orks (the front end's own lists are the six classes; the match's config decides).
    /// </summary>
    public static LoadoutChoice ForJoin(LoadoutChoice choice, SimConfig config, ContentExtras extras, LoadoutChoice fallback) =>
        choice.Class < 0 || ClassOffered(config, choice.Class, extras) || (choice.Class >= config.Classes.Count && OffersOrks(extras)) ? choice : fallback;
}
