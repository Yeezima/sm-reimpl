using System.Numerics;
using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// The Chaos eye glow (M40-T13). Evidence (Verified-local, <c>smtool baf-dump wargear.oppc --grep csm_mp_eye</c>): the glow is not a material but an
/// effect. Every Chaos multiplayer weapon that has one plays <c>csm_mp_eye_&lt;kind&gt;</c> (<c>master_fx</c>; kinds <c>bolter_heavy</c>, <c>meleehammer</c>,
/// <c>lascannon</c>, <c>plasmacannon</c>) twice, attached to the helmet markers <c>fx_lefteye</c> and <c>fx_righteye</c>, in its
/// <c>action_data/while_deployed_actions</c> (stop policy <c>stop_emitting</c>; a few non-MP thunder hammers and cannons use <c>lifetime_actions</c>).
/// Weapons without the binding (the Chaos bolt pistol, every Space Marine weapon) give no glow. <see cref="For"/> lists the plays of one wargear file.
/// </summary>
public sealed class EyeGlowData
{
    /// <summary>The two eye markers every Chaos helmet's <c>.O3d</c> has under <c>RIG_Head</c> (T6a); Space Marine helmets have none.</summary>
    public static readonly IReadOnlyList<string> Markers = ["fx_lefteye", "fx_righteye"];

    readonly Dictionary<string, IReadOnlyList<FxPlay>> _byWargear;

    EyeGlowData(Dictionary<string, IReadOnlyList<FxPlay>> byWargear) => _byWargear = byWargear;

    /// <summary>Wargear files that bind an eye effect.</summary>
    public IReadOnlyCollection<string> Wargear => _byWargear.Keys;

    /// <summary>The eye effects of a wargear file while it is deployed (in hand); empty when it has none.</summary>
    public IReadOnlyList<FxPlay> For(string? wargear) => wargear is not null && _byWargear.TryGetValue(wargear, out var plays) ? plays : [];

    /// <summary>Whether a <c>play_fx</c> binding is an eye glow of the deployed weapon: the default variant, at an eye marker, in the while-deployed (or lifetime) actions.</summary>
    public static bool IsEyeGlow(FxBinding b) => b.IsDefault && b.Play is { Name.Length: > 0 } p
        && Markers.Contains(p.Marker, StringComparer.OrdinalIgnoreCase)
        && (b.Path.Contains("while_deployed_actions/", StringComparison.Ordinal) || b.Path.Contains("lifetime_actions/", StringComparison.Ordinal));

    /// <summary>From already-read wargear files (testable without an install).</summary>
    public static EyeGlowData Build(IReadOnlyDictionary<string, BafDocument> wargear)
    {
        var map = new Dictionary<string, IReadOnlyList<FxPlay>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear)
        {
            var plays = FxBinding.Collect(doc.Root).Where(IsEyeGlow).Select(b => b.Play!).ToList();
            if (plays.Count > 0) map[name] = plays;
        }
        return new EyeGlowData(map);
    }

    /// <summary>Reads the named wargear files of the install (read-only) and keeps those that bind an eye effect.</summary>
    /// <exception cref="FileNotFoundException">The wargear package is missing.</exception>
    public static EyeGlowData Load(string gameDir, IEnumerable<string> wargearNames)
    {
        var wanted = wargearNames.Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count > 0)
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) => docs.TryAdd(e.FileName, BafDocument.Read(d)));
        return Build(docs);
    }

    /// <summary>
    /// Where a helmet's eye markers sit: for each of <see cref="Markers"/> the nearest skeleton bone above it in the helmet's <c>.O3d</c> and the marker's
    /// transform relative to that bone (row-vector). Empty when the helmet has no markers (every Space Marine helmet) or no <c>.O3d</c>.
    /// </summary>
    /// <param name="helmetArchive">The helmet piece package (e.g. <c>data/packages/csm_armour_mp_basic_helmet.oppc</c>); its <c>.O3d</c> has the package's name.</param>
    public static IReadOnlyList<(string Marker, string Bone, Matrix4x4 Local)> HelmetMarkers(string helmetArchive, Func<string, bool> isBone, List<string>? notes = null)
    {
        if (!File.Exists(helmetArchive)) return [];
        var tree = ObjectTree.Load(helmetArchive, Path.GetFileNameWithoutExtension(helmetArchive), notes);
        if (tree is null) return [];
        var found = new List<(string, string, Matrix4x4)>();
        foreach (var marker in Markers)
            if (tree.Contains(marker) && tree.UnderBone(marker, isBone) is { } under) found.Add((marker, under.Bone, under.Local));
        return found;
    }
}
