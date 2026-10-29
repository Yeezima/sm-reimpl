using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// feel/melee-lock: the melee target lock's acquisition values from the install's <c>global</c> (read-only): its
/// <c>extensions/directional_targeting_ext</c> (<c>half_cone_angle_for_melee</c>, <c>max_melee_target_distance</c>,
/// <c>max_melee_target_vertical_distance</c>, <c>distance_band_size</c>, <c>angle_band_size</c>, <c>fan_start_distance</c>,
/// <c>fan_set_back_distance</c>, <c>sprint_or_rush_tuning</c>). The values the files do not hold keep <see cref="MeleeLockSpec"/>'s
/// (ours). How the original combines them is an **interpretation** (<see cref="MeleeLock"/>).
/// </summary>
public static class MeleeLockTuning
{
    public const string Extension = "extensions/directional_targeting_ext";

    /// <summary>The spec with every usable value of <paramref name="global"/>'s extension (finite, at least 0; angles at most 180); others noted and kept.</summary>
    public static MeleeLockSpec Read(BafDocument global, List<string> notes)
    {
        var spec = MeleeLockSpec.Default;
        var ext = global.Root.Table(Extension);
        if (ext is null) { notes.Add($"melee lock: no {Extension}; our values"); return spec; }
        float Value(BafTable? table, string field, float fallback, float max = float.MaxValue)
        {
            if (table?.Float(field) is not float v) { notes.Add($"melee lock: no {field}; {fallback} (ours)"); return fallback; }
            if (!float.IsFinite(v) || v < 0f || v > max) { notes.Add($"melee lock: {field} {v} not usable; {fallback} (ours)"); return fallback; }
            return v;
        }
        var rush = ext?.Table("sprint_or_rush_tuning");
        return spec with
        {
            HalfAngle = Value(ext, "half_cone_angle_for_melee", spec.HalfAngle, 180f),
            Range = Value(ext, "max_melee_target_distance", spec.Range),
            Vertical = Value(ext, "max_melee_target_vertical_distance", spec.Vertical),
            DistanceBand = Value(ext, "distance_band_size", spec.DistanceBand),
            AngleBand = Value(ext, "angle_band_size", spec.AngleBand, 180f),
            FanStart = Value(ext, "fan_start_distance", spec.FanStart),
            FanSetBack = Value(ext, "fan_set_back_distance", spec.FanSetBack),
            RushRange = Value(rush, "max_sprint_or_rush_target_distance", spec.RushRange),
            RushHalfAngle = Value(rush, "half_cone_angle_for_sprint_or_rush", spec.RushHalfAngle, 180f),
        };
    }

    /// <summary>The config with the install's lock values (<see cref="Read"/>); unchanged (noted) when <c>global</c> cannot be read.</summary>
    public static SimConfig Apply(SimConfig config, string gameDir, List<string> notes)
    {
        BafDocument? global = null;
        try
        {
            using var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage));
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        { notes.Add($"melee lock: {e.GetType().Name}: {e.Message}; our values"); return config; }
        if (global is null) { notes.Add("melee lock: global not found; our values"); return config; }
        return config with { MeleeLock = Read(global, notes) };
    }
}
