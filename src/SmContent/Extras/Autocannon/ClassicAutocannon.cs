using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Autocannon;

/// <summary>
/// Classic PvP's autocannon (added content <see cref="ContentExtras.Autocannon"/>, in the extended profile, never in the faithful one or Musou Mode; the owner, 2026-11-11: "add the autocannon to
/// classic pvp mode with everything the same except: no aoe blast radius / simply higher single shot damage to be the same as the stalker - with the original games headshot bonus value", "the sound
/// of the autocannon in classic pvp mode should simply it's original sound - no stalker bolter sound change"). Musou's arsenal autocannon (one weapon, <see cref="ArsenalRules.Autocannon"/>, its model,
/// effects, reticle and animations: <c>Extras/Musou/Arsenal/</c>) as a heavy weapon of the Space Marine Devastator and the Chaos Havoc, appended at the end of both primary lists (the same position,
/// so a custom class carries it over to the other side): <see cref="Arsenal.ClassicTwin"/> (the Stalker Bolter's shot, no blast, the multiplayer file's clip of 120 that spends and an ammunition box
/// refills as the heavy bolter's), its customizer row on both sides, its install twin's own sounds (no Stalker swap: <see cref="Arsenal.SoundSources(ContentExtras, string)"/> is Musou's alone), the
/// Chaos effects on the Havoc and the loyalist ones on the Devastator (the client's <c>NetworkView.ExtrasShooterEffects</c>). Reached from the match tuning's and the catalogue's
/// dispatchers (<c>GameplayTuning.Extras.cs</c>, <c>PerkCatalog.Extras.cs</c>) under its flag only.
/// </summary>
public static class ClassicAutocannon
{
    /// <summary>The note prefix of the load (the server's and the client's startup log).</summary>
    public const string Who = "classic autocannon";

    /// <summary>The Chaos heavy bolter's file: the Havoc's list is found by it.</summary>
    public const string HeavyBolterFileChaos = "csm_mp_exotic_heavy_bolter";

    /// <summary>The match config with the autocannon appended (<see cref="WithAutocannon"/>), read from the install; the config itself (and a note) when the install lacks the twin.</summary>
    public static SimConfig Append(SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Weapons.Any(w => ArsenalRules.IsAutocannon(w.Name))) return config;   // already appended
        if (Arsenal.ReadAutocannonSource(gameDir, notes, Who) is not { } source) return config;
        return WithAutocannon(config, source, notes);
    }

    /// <summary>
    /// <paramref name="config"/> with Classic's autocannon (<see cref="Arsenal.ClassicTwin"/> of <paramref name="source"/>, the Chaos file's weapon as read) appended to the weapons and to the end of
    /// the primary list of every heavy class of either side that lists a heavy bolter (the Devastator and the Havoc); the config itself, with a note, without a heavy bolter or a Stalker Bolter to take
    /// the numbers from, or when it has the autocannon already (the in-match picker's config).
    /// </summary>
    public static SimConfig WithAutocannon(SimConfig config, WeaponSpec source, List<string> notes)
    {
        if (config.Weapons.Any(w => ArsenalRules.IsAutocannon(w.Name))) return config;
        var weapons = config.Weapons.ToList();
        int heavy = weapons.FindIndex(w => w.Name.Equals(Arsenal.HeavyBolterFile, StringComparison.OrdinalIgnoreCase));
        int heavyChaos = weapons.FindIndex(w => w.Name.Equals(HeavyBolterFileChaos, StringComparison.OrdinalIgnoreCase));
        int stalker = weapons.FindIndex(w => w.Name.Equals(Arsenal.StalkerBolterFile, StringComparison.OrdinalIgnoreCase));
        if (stalker < 0) stalker = weapons.FindIndex(w => w.Name.Equals(Arsenal.StalkerBolterFileChaos, StringComparison.OrdinalIgnoreCase));
        if (heavy < 0 && heavyChaos < 0) { notes.Add($"{Who}: no heavy bolter in the config; no autocannon"); return config; }
        if (stalker < 0) { notes.Add($"{Who}: {Arsenal.StalkerBolterFile} not in the config; no autocannon"); return config; }
        int borrowed = heavy >= 0 ? heavy : heavyChaos;
        var gun = Arsenal.ClassicTwin(source, weapons[borrowed], weapons[stalker]);
        int[] heavies = heavyChaos >= 0 && heavy >= 0 ? [heavy, heavyChaos] : [borrowed];
        notes.Add($"{Who}: {gun.Name} appended (the Stalker Bolter's shot, {gun.Damage:0.##} with x{gun.HeadMultiplier:0.##} on the head; no blast; clip {gun.ClipSize}; ammunition boxes as the heavy bolter's)");
        var appended = Arsenal.Appended(config, gun, heavies, Team.SpaceMarines, Team.Chaos);
        return appended with { Pickups = WithBoxAmounts(appended.Pickups, borrowed, appended.Weapons.Count - 1) };
    }

    /// <summary>
    /// The drops with the autocannon's ammunition (the brief of 2026-11-11: "refilled by ammo boxes"): each pickup type gives the weapon at <paramref name="index"/> what it gives the heavy bolter at
    /// <paramref name="heavy"/> (into the clip, as for every weapon without a reserve: <c>Pickups.Collect</c>, capped at the clip's 120). The pickups are read from the install before the append, so
    /// without this their amounts end before the autocannon's index and a box gave it nothing. Types whose list is already longer are left alone; a missing index between is 0. Null stays null.
    /// </summary>
    public static PickupSpec? WithBoxAmounts(PickupSpec? pickups, int heavy, int index)
    {
        if (pickups is null || index < 0) return pickups;
        var types = pickups.Types.Select(t =>
        {
            if (t.WeaponAmounts.Count > index) return t;
            var amounts = new int[index + 1];
            for (int i = 0; i < t.WeaponAmounts.Count; i++) amounts[i] = t.WeaponAmounts[i];
            amounts[index] = t.WeaponAmount(heavy);
            return t with { WeaponAmounts = amounts };
        }).ToList();
        return pickups with { Types = types };
    }

    /// <summary>The catalogue with the autocannon's row in the Devastator's and the Havoc's heavy weapons (after the data's three).</summary>
    public static PerkCatalog Catalog(PerkCatalog catalog) => Arsenal.WithAutocannonRow(catalog, CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos);
}
