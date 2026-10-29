using SmSim;

namespace SmNet;

/// <summary>Helpers for appearance codes on the server and client.</summary>
public static class Appearances
{
    /// <summary>
    /// The code with the custom emblem replaced by the fallback badge (what shows until the emblem is verified, or when custom emblems are
    /// off): every pad showing the emblem shows the fallback (its scheme-default value 255 reads as the scheme's decal, 253); the hash is cleared.
    /// </summary>
    public static AppearanceCode WithoutEmblem(AppearanceCode code)
    {
        if (!code.UsesEmblem && code.Emblem.IsNone) return code;
        byte fallback = code.FallbackBadge == AppearanceCode.FallbackSchemeDefault ? AppearanceCode.DecalSchemeDefault : code.FallbackBadge;
        byte Pad(byte d) => d == AppearanceCode.DecalEmblem ? fallback : d;
        return code.WithDecals(Pad(code.LeftDecal), Pad(code.RightDecal), code.FallbackBadge).WithEmblem(EmblemHash.None);
    }

    /// <summary>
    /// The race a player's appearance is for (protocol 28, <see cref="Races.Of"/>): its class's own race (the Ork Boy: <see cref="Race.Orks"/>,
    /// on either side), else its side, else its class's side, else the Space Marines.
    /// </summary>
    public static Race RaceOf(SimConfig config, PlayerState p) => Races.Of(config, p.Class, p.Team);

    /// <summary>The race of a class played on <paramref name="team"/> (as <see cref="RaceOf(SimConfig, PlayerState)"/>; a pending class change).</summary>
    public static Race RaceOf(SimConfig config, int playerClass, Team team) => Races.Of(config, playerClass, team);
}
