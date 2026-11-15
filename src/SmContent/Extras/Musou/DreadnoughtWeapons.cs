using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using SmSim.Extras.Musou;

namespace SmContent.Extras.Musou;

/// <summary>
/// The Musou Dreadnought's two guns (D3, D4; docs/musou-dreadnought-design.md section 3.3): the install's own <c>dread_assault_cannon</c> and <c>dread_melta</c> wargear read by the simulation's weapon reader
/// (<see cref="WeaponTuning.Read"/>: damage, rate, clip, reload, range, recharge are the files'; kind: data) and appended to the weapons of a Musou config (no existing index moves), so the class (<c>MusouLoadout.AppendDreadnought</c>)
/// can name them. The cannon's reserve is endless in the data (<c>capacity_reserve_infinite</c>): a large reserve here (ours); both guns are arms and never wait to be raised (ours: the model has no holster). Added content: Musou only.
/// </summary>
public static class DreadnoughtWeapons
{
    /// <summary>Rounds in the cannon's endless reserve (the data says infinite; the sim counts: 14,000 reloads of the 140-round clip).</summary>
    public const int EndlessReserve = 2_000_000;

    /// <summary>
    /// The config with the cannon and the melta appended when it has none yet and the install's files can be read; a missing file adds nothing (a note says so) and the class then has no gun.
    /// </summary>
    public static SimConfig Append(SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Weapons.Any(w => w.Name == DreadnoughtRules.CannonWargear)) return config;
        string path = Path.Combine(gameDir, GameplayTuning.WargearPackage);
        if (!File.Exists(path)) { notes.Add($"musou dreadnought: {GameplayTuning.WargearPackage} not found; no cannon or melta"); return config; }
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(path))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && (e.FileName.Equals(DreadnoughtRules.CannonWargear, StringComparison.OrdinalIgnoreCase) || e.FileName.Equals(DreadnoughtRules.MeltaWargear, StringComparison.OrdinalIgnoreCase)),
                (e, data) => docs[e.FileName] = BafDocument.Read(data));
        var cannon = docs.TryGetValue(DreadnoughtRules.CannonWargear, out var c) ? WeaponTuning.Read(DreadnoughtRules.CannonWargear, c, notes) : null;
        var melta = docs.TryGetValue(DreadnoughtRules.MeltaWargear, out var m) ? WeaponTuning.Read(DreadnoughtRules.MeltaWargear, m, notes) : null;
        if (cannon is null || melta is null) { notes.Add("musou dreadnought: the cannon or the melta could not be read; the class has no guns"); return config; }
        var weapons = new List<WeaponSpec>(config.Weapons) { Cannon(cannon), Melta(melta) };
        return config with { Weapons = weapons };
    }

    /// <summary>The cannon as the class carries it: the ranged slot, full auto, an endless reserve, always up.</summary>
    public static WeaponSpec Cannon(WeaponSpec read) => read with
    {
        Name = DreadnoughtRules.CannonWargear, Slot = WeaponSlot.Ranged, Trigger = TriggerKind.FullAuto, BurstLength = 0, BurstInterval = 0f,
        ReserveSize = EndlessReserve, AlwaysReady = true, RaiseDelay = 0f, Brace = null, Vent = null, Variants = [],
    };

    /// <summary>The melta as the class carries it: the pistol slot (Switch toggles to it), always up.</summary>
    public static WeaponSpec Melta(WeaponSpec read) => read with { Name = DreadnoughtRules.MeltaWargear, Slot = WeaponSlot.Pistol, AlwaysReady = true, RaiseDelay = 0f, Brace = null, Vent = null, Variants = [] };
}
