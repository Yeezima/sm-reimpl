using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// What the install says about a multiplayer player's death (M23; values <b>extracted</b>, their effects partly
/// <b>interpretation</b>, spec/behavior.md "Death and ragdoll (M23)"). From the player blueprints
/// (<c>sm_space_marine_player</c>; the Chaos one has the same values), <c>extensions/health_ext</c>: <see cref="RagdollDeath"/>
/// (<c>ragdoll_death</c>: the body goes limp as a ragdoll), <see cref="DeathTime"/> (<c>death_time</c>, seconds; what it
/// times is in code: the corpse's stay or the respawn wait), <see cref="RemoveFromWorldOnDeath"/>; and
/// <c>extensions/biped_locomotion_ext</c>: <see cref="RagdollForceMultiplier"/> (<c>ragdoll_force_multiplier</c>, a factor on
/// forces applied to the ragdoll, by its name), <see cref="ForceToLiftOffFeet"/> (<c>force_required_to_lift_off_feet</c>).
/// From <c>global</c>, <c>extensions/weight_and_strength</c>: the named force levels weapons apply
/// (<see cref="Forces"/>: wussy, light, medium, strong, verystrong, irresistibleforce; wargear <c>hit_sim/actions/apply_force</c>
/// with <c>force</c> = a level name and <c>apply_force_body_type</c> "rigid_body") and the marine's weight
/// (<see cref="SpaceMarineWeight"/>, <c>weights/spacemarine</c>). Units are the original's (unknown; the force levels are
/// probably Havok impulses in kg·m/s, but that is not established).
/// </summary>
public sealed record DeathTuning(bool RagdollDeath, float DeathTime, bool RemoveFromWorldOnDeath, float RagdollForceMultiplier, float ForceToLiftOffFeet,
    IReadOnlyDictionary<string, float> Forces, float SpaceMarineWeight)
{
    /// <summary>Reads the values from the install (read-only); null with a note when the blueprint or global table is missing.</summary>
    public static DeathTuning? Load(string gameDir, List<string>? notes = null)
    {
        notes ??= [];
        BafDocument? marine = null, global = null;
        try
        {
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == GameplayTuning.MarineBlueprint, (_, d) => marine = BafDocument.Read(d));
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            notes.Add($"death tuning: {e.Message}");
            return null;
        }
        if (marine is null || global is null) { notes.Add("death tuning: the marine blueprint or the global table was not found"); return null; }
        return From(marine, global, notes);
    }

    /// <summary>The values from a player blueprint and the global table; null with a note when one is missing.</summary>
    public static DeathTuning? From(BafDocument blueprint, BafDocument global, List<string>? notes = null)
    {
        notes ??= [];
        var health = blueprint.Root.Table("extensions/health_ext");
        var locomotion = blueprint.Root.Table("extensions/biped_locomotion_ext");
        var forces = global.Root.Table("extensions/weight_and_strength/forces");
        var weights = global.Root.Table("extensions/weight_and_strength/weights");
        if (health is not { } h || locomotion is not { } l || forces is not { } f || weights is not { } w)
        {
            notes.Add("death tuning: health_ext, biped_locomotion_ext or weight_and_strength missing");
            return null;
        }
        float? deathTime = Number(h["death_time"]), multiplier = Number(l["ragdoll_force_multiplier"]), lift = Number(l["force_required_to_lift_off_feet"]),
            weight = Number(w["spacemarine"]);
        bool? ragdoll = Bool(h["ragdoll_death"]), remove = Bool(h["remove_from_world_on_death"]);
        var levels = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in f.Entries) if (Number(e) is { } v) levels[e.Name] = v;
        if (deathTime is null || multiplier is null || lift is null || weight is null || ragdoll is null || remove is null || levels.Count == 0)
        {
            notes.Add("death tuning: a value is missing or not a number");
            return null;
        }
        return new DeathTuning(ragdoll.Value, deathTime.Value, remove.Value, multiplier.Value, lift.Value, levels, weight.Value);
    }

    static float? Number(BafEntry? e) => e switch
    {
        { Type: BafType.Float } x when float.IsFinite(x.AsFloat()) => x.AsFloat(),
        { Type: BafType.Int } x => x.Raw,
        _ => null,
    };
    static bool? Bool(BafEntry? e) => e is { Type: BafType.Bool } x ? x.AsBool() : null;
}
