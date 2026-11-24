namespace SmSim.Extras.Warfront.Faction;

/// <summary>
/// The enemy army of a Warfront run (Chaos Warfront C1, owner brief 2026-11-24 rev 1): chosen once, when a new run is built, and stored in <c>run.json</c>, so wipes, saves, restarts and reconnects keep it.
/// Orks is 0: a save written before the faction existed loads as Orks, and the hub wire's missing trailing byte decodes as Orks. One byte on the wire.
/// </summary>
public enum WarfrontFaction : byte
{
    Orks = 0,
    Chaos = 1,
}

/// <summary>
/// The host's setting for the next new run (<c>--warfront-faction alternate|orks|chaos</c>, the Host panel's FACTION row): Alternate (the default) takes turns run by run and starts with Orks; the other two force
/// that army. A run in progress never changes army: the setting is read only when a new run is built.
/// </summary>
public enum WarfrontFactionSetting : byte
{
    Alternate = 0,
    Orks = 1,
    Chaos = 2,
}

/// <summary>The setting's command-line keys and the choice it makes (pure; the rotation file is <see cref="WarfrontFactionRotation"/>).</summary>
public static class WarfrontFactionPolicy
{
    /// <summary>The command-line keys, in setting order (the Host panel's row lists them in this order too).</summary>
    public static readonly string[] Keys = ["alternate", "orks", "chaos"];

    /// <summary>The key of a setting (<c>alternate</c>, <c>orks</c>, <c>chaos</c>).</summary>
    public static string Key(WarfrontFactionSetting s) => Keys[(int)s];

    /// <summary>A setting from its key (case and surrounding spaces ignored); false for anything else. Null or empty is Alternate, the default.</summary>
    public static bool TryParse(string? text, out WarfrontFactionSetting setting)
    {
        setting = WarfrontFactionSetting.Alternate;
        if (string.IsNullOrWhiteSpace(text)) return true;
        int i = Array.IndexOf(Keys, text.Trim().ToLowerInvariant());
        if (i < 0) return false;
        setting = (WarfrontFactionSetting)i;
        return true;
    }

    /// <summary>The army of a new run: a forced setting gives its army; Alternate gives the other army than the last run's, and Orks when no run was recorded.</summary>
    public static WarfrontFaction Choose(WarfrontFactionSetting setting, WarfrontFaction? last) => setting switch
    {
        WarfrontFactionSetting.Orks => WarfrontFaction.Orks,
        WarfrontFactionSetting.Chaos => WarfrontFaction.Chaos,
        _ => last == WarfrontFaction.Orks ? WarfrontFaction.Chaos : WarfrontFaction.Orks,
    };

    /// <summary>A defined army (a number read from a file or the wire may be anything).</summary>
    public static bool IsKnown(int value) => value is (int)WarfrontFaction.Orks or (int)WarfrontFaction.Chaos;
}
