using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;

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
        // The buff of 2026-11-15 (owner): the cannon is the Musou autocannon at the braced heavy bolter's rate with a heat meter; the melta is the Tactical's, scaled up. A config without those sources keeps the data's guns.
        var autocannon = config.Weapons.FirstOrDefault(w => ArsenalRules.IsAutocannon(w.Name));
        var heavy = config.Weapons.FirstOrDefault(w => w.Name.Equals(Arsenal.Arsenal.HeavyBolterFile, StringComparison.OrdinalIgnoreCase));
        var tacticalMelta = config.Weapons.FirstOrDefault(w => w.Name.EndsWith("_mp_ranged_melta", StringComparison.OrdinalIgnoreCase) && w.Name.StartsWith("sm_", StringComparison.OrdinalIgnoreCase));
        var weapons = new List<WeaponSpec>(config.Weapons)
        {
            autocannon is not null && heavy?.Brace is not null ? CannonFromAutocannon(autocannon, heavy.Brace.ShotCooldown) : Cannon(cannon),
            tacticalMelta is not null ? MeltaFromTactical(tacticalMelta) : Melta(melta),
        };
        if (autocannon is null || heavy?.Brace is null) notes.Add("musou dreadnought: no Musou autocannon or braced heavy bolter in the config; the cannon keeps the data's numbers");
        if (tacticalMelta is null) notes.Add("musou dreadnought: no Tactical melta in the config; the melta keeps the data's numbers");
        return config with { Weapons = weapons };
    }

    /// <summary>
    /// The cannon of the buff (owner, 2026-11-15: "the dreadnaught ranged weapon has stats equal to the autocannon, except it has a high rate of fire like the heavy bolter braced ... unlimited ammo but a heat meter"): the Musou
    /// autocannon's every number (damage, range, multipliers, spread, pierce and blast through its own hooks) at the braced heavy bolter's shot cooldown (<paramref name="brace"/>, 0.1 s: the Classic data's, read only), never
    /// spending a round (<see cref="DreadnoughtRules.IsEndless"/>) with a heat meter whose per-shot heat is <see cref="CannonHeat"/>: it overheats after the autocannon's own seconds of continuous fire.
    /// </summary>
    public static WeaponSpec CannonFromAutocannon(WeaponSpec autocannon, float brace) => autocannon with
    {
        Name = DreadnoughtRules.CannonWargear, Slot = WeaponSlot.Ranged, ShotCooldown = brace, ReserveSize = 0, AlwaysReady = true, RaiseDelay = 0f,
        Heat = autocannon.Heat is { } h ? CannonHeat(h, autocannon.ShotCooldown, brace) : null,
    };

    /// <summary>
    /// The cannon's heat: the autocannon's meter (overheat at 15, high at 9, recovery at 1, cooling 4.5 a second after 0.41 s, the file's) with the heat per shot divided so that continuous fire reaches the overheat at the same moment. The
    /// autocannon's round n leaves at (n - 1) x 0.4 s and the 15th heats it over: 5.6 s; at <paramref name="brace"/> the same 5.6 s holds 1 + 5.6 / 0.1 = 57 rounds, so each adds 15 / 56.5 of the meter (0.2655: the 57th reaches 15, the 56th stays under it). Cooling and the
    /// lockout until the meter is back at its recovery value are the autocannon's (no vent: the weapon has none).
    /// </summary>
    public static HeatSpec CannonHeat(HeatSpec autocannon, float autocannonCooldown, float brace)
    {
        float rounds = MathF.Ceiling(autocannon.Overheat / autocannon.PerShot - 1e-4f);   // 15
        float seconds = (rounds - 1f) * autocannonCooldown;                                  // 5.6 s from the first round to the overheating one
        float fired = 1f + MathF.Round(seconds / brace);                                    // 57
        // Overheat / (fired - 0.5): round 57 reaches the overheat and round 56 does not, whatever the float sums round to (15 / 57 summed 57 times may land just under 15 and overheat one round late)
        return autocannon with { PerShot = autocannon.Overheat / (fired - 0.5f) };
    }

    /// <summary>
    /// The melta of the buff (owner, 2026-11-15: "a larger scaled up version of the tactical marine melta (1.5x damage, faster reload, bigger range, still does full damage at max range [no damage dropoff], infinite ammo)"): the
    /// Tactical's cone with the damage x1.5 (<see cref="DreadnoughtRules.MeltaDamageScale"/>, the owner's), the cone's reach, apex and range x1.5 (<see cref="DreadnoughtRules.MeltaRangeScale"/>, **estimate**) at its full damage out to
    /// the end (no distance fall-off), the reload and the recharge at 0.6 of the time (<see cref="DreadnoughtRules.MeltaReloadScale"/>, **estimate**); the pistol slot, always up, no round spent.
    /// </summary>
    public static WeaponSpec MeltaFromTactical(WeaponSpec melta)
    {
        float damage = DreadnoughtRules.MeltaDamageScale, reach = DreadnoughtRules.MeltaRangeScale, reload = DreadnoughtRules.MeltaReloadScale;
        var cone = melta.Cone is { } c
            ? c with
            {
                Length = c.Length * reach, StartLength = c.StartLength * reach, Offset = c.Offset * reach,
                Damage = c.Damage with { Damage = c.Damage.Damage * damage, ShieldDamage = c.Damage.ShieldDamage * damage },
                FallOff = [new DistancePoint(c.Length * reach, 1f)],
            }
            : null;
        return melta with
        {
            Name = DreadnoughtRules.MeltaWargear, Slot = WeaponSlot.Pistol, Damage = melta.Damage * damage, MaxRange = melta.MaxRange * reach,
            FallOffStart = melta.MaxRange * reach, FallOffEnd = melta.MaxRange * reach, FallOffEndMultiplier = 1f,
            ReloadTime = melta.ReloadTime * reload,
            Recharge = melta.Recharge is { } r ? new RechargeSpec(r.Delay * reload, r.PerSecond / reload) : null,
            Cone = cone, AlwaysReady = true, RaiseDelay = 0f, Brace = null, Vent = null, Variants = [],
        };
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
