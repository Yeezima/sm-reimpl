using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// W4, the Remote Detonators (docs/new-weapons-design.md section 7; Musou Mode only): the install's unused <c>sm_mp_eq_demolition_charge</c> as every Space Marine class's last equipment choice, on the
/// install's melta bomb model (client). The <see cref="ThrownSpec"/> is read from the install (count, cooldown, speed, gravity: <c>wargear.oppc</c> and <c>pp_grenade_remote_detonation</c> in
/// <c>ebps.oppc</c>) and then made what the owner decided: it sticks at once to the level (no bounce), has no lifetime and no blast of its own (the fuse and the blasts are the World's,
/// <c>World.Charge.cs</c>), carries 1, 2 with Favor of the Armory. Without the install (a test) the data's values stand in as constants. Appended at the end of the lists: no existing index moves.
/// </summary>
public static partial class Arsenal
{
    /// <summary>The blueprint the install names (its effects stay the data's: <c>squig_explosion</c> at the centre, the fuse effect).</summary>
    public const string ChargeBlueprint = "pp_grenade_remote_detonation";

    /// <summary>The customizer text: locale 105800 and 105801 (Verified-local).</summary>
    public const string ChargeTitle = "Remote Detonators", ChargeDescription = "Explosives linked to a remote activator.";

    static partial void AppendRemoteDetonators(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Thrown.Any(t => t.Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase)) || config.Thrown.Count >= GameplayTuning.MaxIndexed) return;
        var spec = Build(ReadInstall(gameDir, notes));
        int index = config.Thrown.Count;
        var thrown = new List<ThrownSpec>(config.Thrown) { spec };
        int added = 0;
        var classes = config.Classes.Select(c =>
        {
            if (c.Side != Team.SpaceMarines || c.EquipmentChoices.Contains(index) || c.EquipmentChoices.Count >= GameplayTuning.MaxIndexed) return c;
            added++;
            return c with { EquipmentChoices = [.. c.EquipmentChoices, index] };
        }).ToList();
        config = config with { Thrown = thrown, Classes = classes };
        notes.Add($"musou arsenal: Remote Detonators ({spec.Count} carried) offered to {added} Space Marine class(es)");
    }

    static partial void RemoteDetonatorsItems(ref PerkCatalog catalog) =>
        catalog = catalog.WithClasses(c => c.Key.StartsWith("sm ", StringComparison.OrdinalIgnoreCase) && !c.Equipment.Items.Any(i => i.Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase))
            ? c with { Equipment = c.Equipment with { Items = [.. c.Equipment.Items, new LoadoutItem(ArsenalRules.RemoteDetonators, "equipment", ChargeTitle, ChargeDescription, 1, true, null, null)] } }
            : c);

    /// <summary>The install's item, or null (no install, or it could not be read: noted).</summary>
    static ThrownSpec? ReadInstall(string gameDir, List<string> notes)
    {
        if (string.IsNullOrEmpty(gameDir)) return null;
        try
        {
            BafDocument? item = null, blueprint = null;
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase), (_, d) => item = BafDocument.Read(d));
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Equals(ChargeBlueprint, StringComparison.OrdinalIgnoreCase), (_, d) => blueprint = BafDocument.Read(d));
            if (item is null || blueprint is null) { notes.Add($"musou arsenal: {ArsenalRules.RemoteDetonators} or {ChargeBlueprint} not found; the data's values stand in"); return null; }
            var read = new List<string>();
            var spec = WeaponTuning.ReadThrown(ArsenalRules.RemoteDetonators, item, read, path => path.EndsWith(ChargeBlueprint, StringComparison.OrdinalIgnoreCase) ? blueprint : null);
            foreach (var n in read) notes.Add($"musou arsenal: {n}");
            return spec;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or KeyNotFoundException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            notes.Add($"musou arsenal: the Remote Detonators could not be read ({e.GetType().Name}: {e.Message}); the data's values stand in");
            return null;
        }
    }

    /// <summary>The data's flight (speed 25, gravity x1 at 1 m and x2 from 2 m), count 1 and cooldown 0.5 s, when the install is not at hand.</summary>
    static readonly DistancePoint[] FallbackGravity = [new(0f, 1f), new(1f, 1f), new(2f, 2f)];

    /// <summary>The item as the owner decided it, from the data's read (or its stand-ins).</summary>
    internal static ThrownSpec Build(ThrownSpec? data)
    {
        var flight = data?.Projectile;
        var projectile = new ProjectileSpec(flight?.Blueprint is { Length: > 0 } b ? b : "ebps\\projectiles\\" + ChargeBlueprint, DemolitionChargeRules.Speed, 0f, "rigidbody", flight?.CollisionRadius ?? 0f,
            flight is { Gravity.Count: > 0 } ? flight.Gravity : FallbackGravity, DetonateOnCollision: false, DetonateWhenLifetimeOver: false, Sticky: true,
            MaxBounces: 0, GroundLifetime: 0f, ExtraAimAngle: flight?.ExtraAimAngle ?? 0f, MaxActive: 0, DetonateDamage: null, Splash: null);
        var spec = new ThrownSpec(ArsenalRules.RemoteDetonators, 1, -1, data?.Cooldown is > 0f ? data.Cooldown : 0.5f, data?.MaxRange ?? 0f, projectile);
        var double2 = spec with { Count = 2 };
        return spec with { Variants = [new UpgradeVariant<ThrownSpec>([DemolitionChargeRules.Favor], double2)] };
    }
}
